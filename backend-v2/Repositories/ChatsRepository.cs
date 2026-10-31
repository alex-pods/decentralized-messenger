using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Infrastructure;
using Messenger.Api.Repositories.Models;
using Microsoft.Data.Sqlite;

namespace Messenger.Api.Repositories;

public interface IChatsRepository
{
    Task<bool> ExistsAsync(long chatId, CancellationToken ct = default);
    Task<ChatAccess?> GetAccessAsync(long chatId, long userId, CancellationToken ct = default);
    Task<ChatSummary?> GetSummaryAsync(long chatId, long viewerId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatSummary>> GetVisibleChatsAsync(long userId, CancellationToken ct = default);
    Task<DirectChatResult> GetOrCreateDirectAsync(long callerId, long anotherUserId, CancellationToken ct = default);
    Task<long> CreateGroupAsync(long creatorId, string title, IReadOnlyCollection<long> otherMemberIds, CancellationToken ct = default);
    Task HideAsync(long chatId, long userId, CancellationToken ct = default);
    Task<IReadOnlyList<MemberRow>> GetMembersAsync(long chatId, CancellationToken ct = default);
    Task<bool> AddMemberAsync(long chatId, long userId, CancellationToken ct = default);
    Task RemoveMemberAsync(long chatId, long userId, CancellationToken ct = default);
    Task SetRoleAsync(long chatId, long userId, MemberRole role, CancellationToken ct = default);
    Task TransferOwnerAsync(long chatId, long currentOwnerId, long newOwnerId, CancellationToken ct = default);
    Task<LeaveResult> LeaveGroupAsync(long chatId, long userId, CancellationToken ct = default);
    Task<long> MarkReadAsync(long chatId, long userId, long messageId, CancellationToken ct = default);
}

public sealed class ChatsRepository(DbConnectionFactory db) : IChatsRepository
{
    public async Task<bool> ExistsAsync(long chatId, CancellationToken ct = default)
    {
        using var c = db.Open();
        return await c.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM chats WHERE id = @chatId",
            new { chatId }, cancellationToken: ct)) > 0;
    }

    public async Task<ChatAccess?> GetAccessAsync(long chatId, long userId, CancellationToken ct = default)
    {
        using var c = db.Open();
        var raw = await c.QuerySingleOrDefaultAsync<AccessRow>(new CommandDefinition(
            """
            SELECT c.type,
                   CASE WHEN c.type = 'direct' THEN NULL ELSE uc.role END AS role
            FROM chats c
            JOIN users_chats uc ON uc.chat_id = c.id AND uc.user_id = @userId
            WHERE c.id = @chatId
            """,
            new { chatId, userId }, cancellationToken: ct));
        return raw is null ? null : MapAccess(raw);
    }

    public async Task<ChatSummary?> GetSummaryAsync(long chatId, long viewerId, CancellationToken ct = default)
    {
        using var c = db.Open();
        var rows = await c.QueryAsync<SummaryRow>(new CommandDefinition(
            """
            WITH base AS (
                SELECT c.id, c.type, c.title, c.created_by, c.created_at,
                       CASE WHEN c.type = 'direct'
                            THEN CASE WHEN c.user_a_id = @viewerId THEN c.user_b_id ELSE c.user_a_id END
                       END AS other_user_id,
                       CAST((SELECT MAX(m.id) FROM messages m
                        WHERE m.chat_id = c.id
                          AND NOT EXISTS (SELECT 1 FROM message_hidden h
                                          WHERE h.message_id = m.id AND h.user_id = @viewerId)
                       ) AS INTEGER) AS last_message_id
                FROM chats c
                JOIN users_chats uc ON uc.chat_id = c.id AND uc.user_id = @viewerId
                WHERE c.id = @chatId
            )
            SELECT b.id, b.type, b.title, b.created_by, b.created_at,
                   b.other_user_id, b.last_message_id,
                   ou.tag  AS other_user_tag,
                   ou.name AS other_user_name
            FROM base b
            LEFT JOIN users ou ON ou.id = b.other_user_id
            """,
            new { chatId, viewerId }, cancellationToken: ct));
        var row = rows.SingleOrDefault();
        return row is null ? null : MapSummary(row);
    }

    public async Task<IReadOnlyList<ChatSummary>> GetVisibleChatsAsync(long userId, CancellationToken ct = default)
    {
        using var c = db.Open();
        var rows = await c.QueryAsync<SummaryRow>(new CommandDefinition(
            """
            WITH base AS (
                SELECT c.id, c.type, c.title, c.created_by, c.created_at,
                       CASE WHEN c.type = 'direct'
                            THEN CASE WHEN c.user_a_id = @userId THEN c.user_b_id ELSE c.user_a_id END
                       END AS other_user_id,
                       CAST((SELECT MAX(m.id) FROM messages m
                        WHERE m.chat_id = c.id
                          AND NOT EXISTS (SELECT 1 FROM message_hidden h
                                          WHERE h.message_id = m.id AND h.user_id = @userId)
                       ) AS INTEGER) AS last_message_id
                FROM chats c
                JOIN users_chats uc ON uc.chat_id = c.id AND uc.user_id = @userId
                WHERE uc.hidden_at IS NULL
            )
            SELECT b.id, b.type, b.title, b.created_by, b.created_at,
                   b.other_user_id, b.last_message_id,
                   ou.tag  AS other_user_tag,
                   ou.name AS other_user_name
            FROM base b
            LEFT JOIN users ou ON ou.id = b.other_user_id
            ORDER BY COALESCE(b.last_message_id, 0) DESC, b.id DESC
            """,
            new { userId }, cancellationToken: ct));
        return rows.Select(MapSummary).ToList();
    }

    public async Task<DirectChatResult> GetOrCreateDirectAsync(long callerId, long anotherUserId, CancellationToken ct = default)
    {
        if (callerId == anotherUserId)
            throw new ArgumentException("Direct chat with self is not allowed", nameof(anotherUserId));

        var a = Math.Min(callerId, anotherUserId);
        var b = Math.Max(callerId, anotherUserId);

        using var c = db.Open();
        var existing = await c.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT id FROM chats WHERE type = 'direct' AND user_a_id = @a AND user_b_id = @b",
            new { a, b }, cancellationToken: ct));
        if (existing.HasValue)
            return new DirectChatResult(existing.Value, false);

        try
        {
            var newId = await c.ExecuteScalarAsync<long>(new CommandDefinition(
                "INSERT INTO chats(type, user_a_id, user_b_id) VALUES ('direct', @a, @b) RETURNING id",
                new { a, b }, cancellationToken: ct));
            return new DirectChatResult(newId, true);
        }
        catch (SqliteException ex) when (IsUniqueViolation(ex))
        {
            var id = await c.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM chats WHERE type = 'direct' AND user_a_id = @a AND user_b_id = @b",
                new { a, b }, cancellationToken: ct));
            return new DirectChatResult(id, false);
        }
    }

    public async Task<long> CreateGroupAsync(
        long creatorId,
        string title,
        IReadOnlyCollection<long> otherMemberIds,
        CancellationToken ct = default)
    {
        var members = otherMemberIds.Where(id => id != creatorId).Distinct().ToList();
        if (members.Count == 0)
            throw new ArgumentException("Group must have at least one other member", nameof(otherMemberIds));

        using var c = db.Open();
        using var tx = c.BeginTransaction();

        var chatId = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO chats(type, title, created_by) VALUES ('group', @title, @creatorId) RETURNING id",
            new { title = Crypto.Encrypt(title), creatorId }, tx, cancellationToken: ct));

        await c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO users_chats(user_id, chat_id, role) VALUES (@creatorId, @chatId, 'owner')",
            new { creatorId, chatId }, tx, cancellationToken: ct));

        foreach (var memberId in members)
            await c.ExecuteAsync(new CommandDefinition(
                "INSERT INTO users_chats(user_id, chat_id) VALUES (@memberId, @chatId)",
                new { memberId, chatId }, tx, cancellationToken: ct));

        tx.Commit();
        return chatId;
    }

    public async Task HideAsync(long chatId, long userId, CancellationToken ct = default)
    {
        using var c = db.Open();
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE users_chats SET hidden_at = CURRENT_TIMESTAMP WHERE chat_id = @chatId AND user_id = @userId",
            new { chatId, userId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<MemberRow>> GetMembersAsync(long chatId, CancellationToken ct = default)
    {
        using var c = db.Open();
        var rows = await c.QueryAsync<MemberRowRaw>(new CommandDefinition(
            """
            SELECT uc.user_id, u.tag, u.name AS display_name, uc.role, uc.joined_at
            FROM users_chats uc
            JOIN users u ON u.id = uc.user_id
            WHERE uc.chat_id = @chatId
            ORDER BY uc.joined_at, uc.user_id
            """,
            new { chatId }, cancellationToken: ct));
        return rows.Select(MapMember).ToList();
    }

    public async Task<bool> AddMemberAsync(long chatId, long userId, CancellationToken ct = default)
    {
        using var c = db.Open();
        var rows = await c.ExecuteAsync(new CommandDefinition(
            "INSERT OR IGNORE INTO users_chats(user_id, chat_id) VALUES (@userId, @chatId)",
            new { userId, chatId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task RemoveMemberAsync(long chatId, long userId, CancellationToken ct = default)
    {
        using var c = db.Open();
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM users_chats WHERE chat_id = @chatId AND user_id = @userId",
            new { chatId, userId }, cancellationToken: ct));
    }

    public async Task SetRoleAsync(long chatId, long userId, MemberRole role, CancellationToken ct = default)
    {
        using var c = db.Open();
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE users_chats SET role = @role WHERE chat_id = @chatId AND user_id = @userId",
            new { chatId, userId, role = RoleToString(role) }, cancellationToken: ct));
    }

    public async Task TransferOwnerAsync(long chatId, long currentOwnerId, long newOwnerId, CancellationToken ct = default)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();

        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE users_chats SET role = 'admin' WHERE chat_id = @chatId AND user_id = @currentOwnerId AND role = 'owner'",
            new { chatId, currentOwnerId }, tx, cancellationToken: ct));

        var rows = await c.ExecuteAsync(new CommandDefinition(
            "UPDATE users_chats SET role = 'owner' WHERE chat_id = @chatId AND user_id = @newOwnerId",
            new { chatId, newOwnerId }, tx, cancellationToken: ct));

        if (rows == 0)
            throw new InvalidOperationException("New owner is not a chat member");

        tx.Commit();
    }

    public async Task<LeaveResult> LeaveGroupAsync(long chatId, long userId, CancellationToken ct = default)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();

        var info = await c.QuerySingleOrDefaultAsync<LeaveInfoRow>(new CommandDefinition(
            """
            SELECT uc.role,
                   c.type,
                   (SELECT COUNT(*) FROM users_chats WHERE chat_id = @chatId) AS member_count
            FROM users_chats uc
            JOIN chats c ON c.id = uc.chat_id
            WHERE uc.chat_id = @chatId AND uc.user_id = @userId
            """,
            new { chatId, userId }, tx, cancellationToken: ct));

        if (info is null)
        {
            tx.Commit();
            return LeaveResult.Left;
        }

        if (info.Type != "group")
            throw new InvalidOperationException("LeaveGroupAsync is for group chats only");

        if (info.Role != "owner")
        {
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM users_chats WHERE chat_id = @chatId AND user_id = @userId",
                new { chatId, userId }, tx, cancellationToken: ct));
            tx.Commit();
            return LeaveResult.Left;
        }

        if (info.MemberCount == 1)
        {
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM chats WHERE id = @chatId",
                new { chatId }, tx, cancellationToken: ct));
            tx.Commit();
            return LeaveResult.GroupClosed;
        }

        tx.Commit();
        return LeaveResult.OwnerTransferRequired;
    }

    public async Task<long> MarkReadAsync(long chatId, long userId, long messageId, CancellationToken ct = default)
    {
        using var c = db.Open();
        var updated = await c.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            """
            UPDATE users_chats
            SET last_read_message_id = @messageId,
                last_read_at = CURRENT_TIMESTAMP
            WHERE chat_id = @chatId AND user_id = @userId AND last_read_message_id < @messageId
            RETURNING last_read_message_id
            """,
            new { chatId, userId, messageId }, cancellationToken: ct));
        if (updated.HasValue)
            return updated.Value;

        var current = await c.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT last_read_message_id FROM users_chats WHERE chat_id = @chatId AND user_id = @userId",
            new { chatId, userId }, cancellationToken: ct));
        if (current is null)
            throw new InvalidOperationException("User is not a member of this chat");
        return current.Value;
    }

    private static ChatAccess MapAccess(AccessRow r) => new(
        r.Type == "direct" ? ChatType.Direct : Enum.Parse<ChatType>(r.Type, true),
        r.Role is null ? null : Enum.Parse<MemberRole>(r.Role, true));

    private static ChatSummary MapSummary(SummaryRow r) => new(
        r.Id,
        r.Type == "direct" ? ChatType.Direct : Enum.Parse<ChatType>(r.Type, true),
        Crypto.Decrypt(r.Title),
        r.CreatedBy,
        SqliteTime.Parse(r.CreatedAt),
        r.OtherUserId is long uid && r.OtherUserTag is not null && r.OtherUserName is not null
            ? new UserCardRow(uid, r.OtherUserTag, Crypto.Decrypt(r.OtherUserName)!)
            : null,
        r.LastMessageId);

    private static MemberRow MapMember(MemberRowRaw r) => new(
        r.UserId,
        r.Tag,
        Crypto.Decrypt(r.DisplayName)!,
        Enum.Parse<MemberRole>(r.Role, true),
        SqliteTime.Parse(r.JoinedAt));

    private static string RoleToString(MemberRole role) => role switch
    {
        MemberRole.Member => "member",
        MemberRole.Admin => "admin",
        MemberRole.Owner => "owner",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static bool IsUniqueViolation(SqliteException ex)
        => ex.SqliteErrorCode == 19 && ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);

    private sealed class AccessRow
    {
        public string Type { get; set; } = "";
        public string? Role { get; set; }
    }

    private sealed class SummaryRow
    {
        public long Id { get; set; }
        public string Type { get; set; } = "";
        public string? Title { get; set; }
        public long? CreatedBy { get; set; }
        public string CreatedAt { get; set; } = "";
        public long? OtherUserId { get; set; }
        public long? LastMessageId { get; set; }
        public string? OtherUserTag { get; set; }
        public string? OtherUserName { get; set; }
    }

    private sealed record MemberRowRaw(
        long UserId, string Tag, string DisplayName, string Role, string JoinedAt);

    private sealed record LeaveInfoRow(string Role, string Type, int MemberCount);
}
