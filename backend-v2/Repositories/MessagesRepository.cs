using System.Data;
using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Infrastructure;
using Messenger.Api.Repositories.Models;
using Microsoft.Data.Sqlite;

namespace Messenger.Api.Repositories;

public interface IMessagesRepository
{
    Task<MessageRow?> GetByIdAsync(long messageId, CancellationToken ct = default);
    Task<IReadOnlyList<MessageRow>> GetByIdsAsync(IReadOnlyCollection<long> messageIds, CancellationToken ct = default);
    Task<IReadOnlyList<MessageRow>> GetHistoryAsync(long chatId, long viewerId, HistoryQuery query, CancellationToken ct = default);
    Task<AddMessageResult> AddAsync(NewMessage message, CancellationToken ct = default);
    Task<MessageRow?> EditAsync(long messageId, string newText, CancellationToken ct = default);
    Task<MessageRow?> DeleteAsync(long messageId, CancellationToken ct = default);
}

public sealed class MessagesRepository(DbConnectionFactory db) : IMessagesRepository
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private const string RowSelect =
        """
        SELECT m.id, m.chat_id, m.sender_id, m.reply_to_id, m.text, m.content_state,
               m.sent_at, m.delivered_at, m.edited_at, m.deleted_at, v.read_at
        FROM messages m
        LEFT JOIN message_read_at v ON v.message_id = m.id
        """;

    public async Task<MessageRow?> GetByIdAsync(long messageId, CancellationToken ct = default)
    {
        using var c = db.Open();
        return await LoadRow(c, messageId, ct);
    }

    public async Task<IReadOnlyList<MessageRow>> GetByIdsAsync(
        IReadOnlyCollection<long> messageIds,
        CancellationToken ct = default)
    {
        if (messageIds.Count == 0)
            return Array.Empty<MessageRow>();

        using var c = db.Open();
        var rows = await c.QueryAsync<MessageRowRaw>(new CommandDefinition(
            $"{RowSelect} WHERE m.id IN @messageIds",
            new { messageIds }, cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task<IReadOnlyList<MessageRow>> GetHistoryAsync(
        long chatId,
        long viewerId,
        HistoryQuery query,
        CancellationToken ct = default)
    {
        var limit = query.Limit <= 0 ? DefaultLimit : Math.Min(query.Limit, MaxLimit);

        var sql = $"{RowSelect} WHERE m.chat_id = @chatId " +
                  "AND NOT EXISTS (SELECT 1 FROM message_hidden h WHERE h.message_id = m.id AND h.user_id = @viewerId)";

        string orderBy;
        bool reverse;

        if (query.AfterId.HasValue)
        {
            sql += " AND m.id > @afterId";
            if (query.BeforeId.HasValue)
                sql += " AND m.id < @beforeId";
            orderBy = " ORDER BY m.id ASC LIMIT @limit";
            reverse = false;
        }
        else if (query.BeforeId.HasValue)
        {
            sql += " AND m.id < @beforeId";
            orderBy = " ORDER BY m.id DESC LIMIT @limit";
            reverse = true;
        }
        else
        {
            orderBy = " ORDER BY m.id DESC LIMIT @limit";
            reverse = true;
        }

        using var c = db.Open();
        var rows = (await c.QueryAsync<MessageRowRaw>(new CommandDefinition(
            sql + orderBy,
            new
            {
                chatId,
                viewerId,
                beforeId = query.BeforeId,
                afterId = query.AfterId,
                limit,
            },
            cancellationToken: ct))).ToList();

        var mapped = rows.Select(MapRow).ToList();
        if (reverse)
            mapped.Reverse();
        return mapped;
    }

    public async Task<AddMessageResult> AddAsync(NewMessage message, CancellationToken ct = default)
    {
        using var c = db.Open();

        if (!string.IsNullOrEmpty(message.ClientRef))
        {
            var existing = await c.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                "SELECT id FROM messages WHERE sender_id = @senderId AND client_ref = @clientRef",
                new { message.SenderId, message.ClientRef }, cancellationToken: ct));
            if (existing.HasValue)
                return new AddMessageResult((await LoadRow(c, existing.Value, ct))!, false);
        }

        try
        {
            var newId = await c.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO messages(chat_id, sender_id, text, reply_to_id, client_ref)
                VALUES (@chatId, @senderId, @text, @replyToId, @clientRef)
                RETURNING id
                """,
                new { message.ChatId, message.SenderId, message.Text, message.ReplyToId, message.ClientRef },
                cancellationToken: ct));
            return new AddMessageResult((await LoadRow(c, newId, ct))!, true);
        }
        catch (SqliteException ex) when (
            IsUniqueViolation(ex) && !string.IsNullOrEmpty(message.ClientRef))
        {
            var existing = await c.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM messages WHERE sender_id = @senderId AND client_ref = @clientRef",
                new { message.SenderId, message.ClientRef }, cancellationToken: ct));
            return new AddMessageResult((await LoadRow(c, existing, ct))!, false);
        }
    }

    public async Task<MessageRow?> EditAsync(long messageId, string newText, CancellationToken ct = default)
    {
        using var c = db.Open();
        var rows = await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE messages
            SET text = @newText, edited_at = CURRENT_TIMESTAMP
            WHERE id = @messageId AND content_state = 'stored'
            """,
            new { messageId, newText }, cancellationToken: ct));
        return rows == 0 ? null : await LoadRow(c, messageId, ct);
    }

    public async Task<MessageRow?> DeleteAsync(long messageId, CancellationToken ct = default)
    {
        using var c = db.Open();
        var rows = await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE messages
            SET content_state = 'deleted', deleted_at = CURRENT_TIMESTAMP, text = NULL
            WHERE id = @messageId AND content_state = 'stored'
            """,
            new { messageId }, cancellationToken: ct));
        return rows == 0 ? null : await LoadRow(c, messageId, ct);
    }

    private static async Task<MessageRow?> LoadRow(IDbConnection c, long messageId, CancellationToken ct)
    {
        var raw = await c.QuerySingleOrDefaultAsync<MessageRowRaw>(new CommandDefinition(
            $"{RowSelect} WHERE m.id = @messageId",
            new { messageId }, cancellationToken: ct));
        return raw is null ? null : MapRow(raw);
    }

    private static MessageRow MapRow(MessageRowRaw r) => new(
        r.Id,
        r.ChatId,
        r.SenderId,
        r.ReplyToId,
        r.Text,
        ParseState(r.ContentState),
        SqliteTime.Parse(r.SentAt),
        SqliteTime.ParseOrNull(r.DeliveredAt),
        SqliteTime.ParseOrNull(r.EditedAt),
        SqliteTime.ParseOrNull(r.ReadAt),
        SqliteTime.ParseOrNull(r.DeletedAt));

    private static MessageContentState ParseState(string s) => s switch
    {
        "stored" => MessageContentState.Stored,
        "not_stored" => MessageContentState.NotStored,
        "deleted" => MessageContentState.Deleted,
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private static bool IsUniqueViolation(SqliteException ex)
        => ex.SqliteErrorCode == 19 && ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);

    private sealed record MessageRowRaw(
        long Id,
        long ChatId,
        long SenderId,
        long? ReplyToId,
        string? Text,
        string ContentState,
        string SentAt,
        string? DeliveredAt,
        string? EditedAt,
        string? ReadAt,
        string? DeletedAt);
}
