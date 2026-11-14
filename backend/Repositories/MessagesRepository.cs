using System.Data;
using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Infrastructure;
using Messenger.Api.Repositories.Models;
using Microsoft.Data.Sqlite;

namespace Messenger.Api.Repositories;

public interface IMessagesRepository
{
    Task<MessageRow?> GetByIdAsync(long messageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MessageRow>> GetByIdsAsync(
        IReadOnlyCollection<long> messageIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MessageRow>> GetHistoryAsync(
        long chatId, long viewerId, HistoryQuery query, CancellationToken cancellationToken = default);
    Task<AddMessageResult> AddAsync(NewMessage message, CancellationToken cancellationToken = default);
    Task<MessageRow?> EditAsync(
        long messageId,
        Secret newText,
        CancellationToken cancellationToken = default);

    Task<DeleteMessageResult?> DeleteAsync(
        long messageId,
        CancellationToken cancellationToken = default);
    Task HideAsync(long messageId, long userId, CancellationToken cancellationToken = default);
    Task MarkDeliveredAsync(long messageId, CancellationToken ct = default);
    Task<IReadOnlyList<PinRow>> ListPinsAsync(long chatId, long viewerId, CancellationToken ct = default);
    Task<bool> PinAsync(long chatId, long messageId, long userId, DateTimeOffset pinnedAt, CancellationToken ct = default);
    Task<bool> UnpinAsync(long chatId, long messageId, CancellationToken ct = default);
    Task<int> CountPinsAsync(long chatId, CancellationToken ct = default);
    Task<bool> IsPinnedAsync(long chatId, long messageId, CancellationToken ct = default);
}

public sealed class MessagesRepository(DbConnectionFactory db) : IMessagesRepository
{
    private const string RowSelect = """
        SELECT m.id, m.chat_id, m.sender_id, m.reply_to_id,
            m.text, m.content_state,
            m.sent_at, m.delivered_at, m.edited_at, m.deleted_at,
            m.client_ref_payload, v.read_at
        FROM messages m
        LEFT JOIN message_read_at v ON v.message_id = m.id
        """;

    public async Task<MessageRow?> GetByIdAsync(long messageId, CancellationToken cancellationToken = default)
    {
        using var connection = db.Open();
        return await LoadRow(connection, messageId, cancellationToken);
    }

    public async Task<IReadOnlyList<MessageRow>> GetByIdsAsync(
        IReadOnlyCollection<long> messageIds, CancellationToken cancellationToken = default)
    {
        if (messageIds.Count == 0) return Array.Empty<MessageRow>();

        using var connection = db.Open();
        var rows = await connection.QueryAsync<MessageRowRaw>(new CommandDefinition(
            $"{RowSelect} WHERE m.id IN @messageIds",
            new { messageIds }, cancellationToken: cancellationToken));
        return rows.Select(MapRow).ToList();
    }

    public async Task<IReadOnlyList<MessageRow>> GetHistoryAsync(
        long chatId, long viewerId, HistoryQuery query, CancellationToken cancellationToken = default)
    {
        var limit = query.Limit <= 0 ? 50 : Math.Min(query.Limit, 100);
        var sql = $"""
            {RowSelect}
            WHERE m.chat_id = @chatId
              AND NOT EXISTS (
                  SELECT 1 FROM message_hidden h
                  WHERE h.message_id = m.id AND h.user_id = @viewerId)
            """;

        var reverse = query.AfterId is null;
        if (query.AfterId is { } afterId)
        {
            sql += " AND m.id > @afterId";
            if (query.BeforeId is not null) sql += " AND m.id < @beforeId";
            sql += " ORDER BY m.id ASC LIMIT @limit";
        }
        else
        {
            if (query.BeforeId is not null) sql += " AND m.id < @beforeId";
            sql += " ORDER BY m.id DESC LIMIT @limit";
        }

        using var connection = db.Open();
        var rows = await connection.QueryAsync<MessageRowRaw>(new CommandDefinition(
            sql,
            new
            {
                chatId,
                viewerId,
                beforeId = query.BeforeId,
                afterId = query.AfterId,
                limit
            },
            cancellationToken: cancellationToken));

        var result = rows.Select(MapRow).ToList();
        if (reverse) result.Reverse();
        return result;
    }

    public async Task<AddMessageResult> AddAsync(
        NewMessage message,
        CancellationToken cancellationToken = default)
    {
        using var connection = db.Open();

        var fingerprint = MessageFingerprint.Compute(
            message.ChatId,
            message.Text,
            message.ReplyToId);

        if (message.ClientRef is not null)
        {
            var existing = await FindDuplicateAsync(
                connection,
                message,
                fingerprint,
                cancellationToken);

            if (existing is not null)
                return existing;
        }

        try
        {
            var id = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    """
                    INSERT INTO messages(
                        chat_id, sender_id, text,
                        reply_to_id, client_ref, client_ref_payload)
                    VALUES (
                        @ChatId, @SenderId, @Text,
                        @ReplyToId, @ClientRef, @fingerprint)
                    RETURNING id
                    """,
                    new
                    {
                        message.ChatId,
                        message.SenderId,
                        message.Text,
                        message.ReplyToId,
                        message.ClientRef,
                        fingerprint = message.ClientRef is null
                            ? null
                            : fingerprint
                    },
                    cancellationToken: cancellationToken));

            return new AddMessageResult(
                (await LoadRow(connection, id, cancellationToken))!,
                true);
        }
        catch (SqliteException exception) when (
            message.ClientRef is not null &&
            IsUniqueViolation(exception))
        {
            var existing = await FindDuplicateAsync(
                connection,
                message,
                fingerprint,
                cancellationToken);

            if (existing is null)
                throw;

            return existing;
        }
    }

    public async Task<MessageRow?> EditAsync(
        long messageId,
        Secret newText,
        CancellationToken cancellationToken = default)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();

        await PreserveLegacyFingerprint(
            connection,
            messageId,
            transaction,
            cancellationToken);

        var changed = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE messages
                SET text = @newText, edited_at = CURRENT_TIMESTAMP
                WHERE id = @messageId AND content_state = 'stored'
                """,
                new { messageId, newText },
                transaction,
                cancellationToken: cancellationToken));

        var row = changed == 0
            ? null
            : await LoadRow(
                connection,
                messageId,
                cancellationToken,
                transaction);

        transaction.Commit();
        return row;
    }

    public async Task<DeleteMessageResult?> DeleteAsync(
        long messageId,
        CancellationToken cancellationToken = default)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        await PreserveLegacyFingerprint(
            connection,
            messageId,
            transaction,
            cancellationToken);

        var paths = (await connection.QueryAsync<Secret>(
            new CommandDefinition(
                """
                SELECT storage_path
                FROM attachments
                WHERE message_id = @messageId
                """,
                new { messageId },
                transaction,
                cancellationToken: cancellationToken)))
            .Select(path => path.Value)
            .ToList();

        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE messages
            SET content_state = 'deleted',
                deleted_at = CURRENT_TIMESTAMP,
                text = NULL
            WHERE id = @messageId AND content_state <> 'deleted'
            """,
            new { messageId },
            transaction,
            cancellationToken: cancellationToken));

        var row = await LoadRow(
            connection,
            messageId,
            cancellationToken,
            transaction);

        transaction.Commit();

        return row is null
            ? null
            : new DeleteMessageResult(row, changed > 0, paths);
    }

    public async Task HideAsync(long messageId, long userId, CancellationToken cancellationToken = default)
    {
        using var connection = db.Open();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR IGNORE INTO message_hidden(user_id, message_id) VALUES (@userId, @messageId)",
            new { userId, messageId }, cancellationToken: cancellationToken));
    }

    public async Task MarkDeliveredAsync(long messageId, CancellationToken ct = default)
    {
        using var connection = db.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE messages
            SET delivered_at = @now
            WHERE id = @messageId AND delivered_at IS NULL
            """,
            new
            {
                messageId,
                now = DateTimeOffset.UtcNow.ToString("O")
            },
            cancellationToken: ct));
    }

    private static async Task<AddMessageResult?> FindDuplicateAsync(
        IDbConnection connection,
        NewMessage message,
        string fingerprint,
        CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<MessageRowRaw>(
            new CommandDefinition(
                $"{RowSelect} WHERE m.sender_id = @SenderId AND m.client_ref = @ClientRef",
                new
                {
                    message.SenderId,
                    message.ClientRef
                },
                cancellationToken: ct));

        if (row is null)
            return null;

        var matches = row.ClientRefPayload is not null
            ? row.ClientRefPayload == fingerprint
            : row.ChatId == message.ChatId &&
            row.ReplyToId == message.ReplyToId &&
            (row.ContentState == "deleted" || row.Text == message.Text);

        return new AddMessageResult(
            MapRow(row),
            false,
            matches);
    }

    private static async Task PreserveLegacyFingerprint(
        IDbConnection connection,
        long messageId,
        IDbTransaction transaction,
        CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<MessageRowRaw>(
            new CommandDefinition(
                $"""
                {RowSelect}
                WHERE m.id = @messageId
                AND m.client_ref IS NOT NULL
                AND m.client_ref_payload IS NULL
                AND m.edited_at IS NULL
                AND m.content_state = 'stored'
                """,
                new { messageId },
                transaction,
                cancellationToken: ct));

        if (row is null)
            return;

        var fingerprint = MessageFingerprint.Compute(
            row.ChatId,
            row.Text!,
            row.ReplyToId);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE messages
                SET client_ref_payload = @fingerprint
                WHERE id = @messageId
                """,
                new { fingerprint, messageId },
                transaction,
                cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PinRow>> ListPinsAsync(
        long chatId, long viewerId, CancellationToken ct = default)
    {
        using var connection = db.Open();
        var rows = await connection.QueryAsync<PinRowRaw>(new CommandDefinition(
            """
            SELECT m.id, m.chat_id, m.sender_id, m.reply_to_id,
                m.text, m.content_state,
                m.sent_at, m.delivered_at, m.edited_at, m.deleted_at,
                m.client_ref_payload, v.read_at,
                p.pinned_by, p.pinned_at
            FROM pinned_messages p
            JOIN messages m ON m.id = p.message_id
            LEFT JOIN message_read_at v ON v.message_id = m.id
            WHERE p.chat_id = @chatId
              AND NOT EXISTS (
                  SELECT 1 FROM message_hidden h
                  WHERE h.message_id = m.id AND h.user_id = @viewerId)
            ORDER BY p.pinned_at DESC, p.message_id DESC
            """,
            new { chatId, viewerId },
            cancellationToken: ct));
        return rows.Select(row => new PinRow(
            row.Id,
            row.PinnedBy,
            SqliteTime.Parse(row.PinnedAt),
            MapRow(row))).ToList();
    }

    public async Task<bool> PinAsync(
        long chatId, long messageId, long userId, DateTimeOffset pinnedAt, CancellationToken ct = default)
    {
        using var connection = db.Open();
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO pinned_messages(chat_id, message_id, pinned_by, pinned_at)
            VALUES (@chatId, @messageId, @userId, @pinnedAt)
            ON CONFLICT(chat_id, message_id) DO NOTHING
            """,
            new { chatId, messageId, userId, pinnedAt = pinnedAt.ToString("o") },
            cancellationToken: ct));
        return inserted > 0;
    }

    public async Task<bool> UnpinAsync(long chatId, long messageId, CancellationToken ct = default)
    {
        using var connection = db.Open();
        var removed = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM pinned_messages WHERE chat_id = @chatId AND message_id = @messageId",
            new { chatId, messageId },
            cancellationToken: ct));
        return removed > 0;
    }

    public async Task<int> CountPinsAsync(long chatId, CancellationToken ct = default)
    {
        using var connection = db.Open();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM pinned_messages WHERE chat_id = @chatId",
            new { chatId },
            cancellationToken: ct));
    }

    public async Task<bool> IsPinnedAsync(long chatId, long messageId, CancellationToken ct = default)
    {
        using var connection = db.Open();
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*) FROM pinned_messages
            WHERE chat_id = @chatId AND message_id = @messageId
            """,
            new { chatId, messageId },
            cancellationToken: ct));
        return count > 0;
    }

    private static async Task<MessageRow?> LoadRow(
        IDbConnection connection,
        long messageId,
        CancellationToken cancellationToken,
        IDbTransaction? transaction = null)
    {
        var row = await connection.QuerySingleOrDefaultAsync<MessageRowRaw>(
            new CommandDefinition(
                $"{RowSelect} WHERE m.id = @messageId",
                new { messageId },
                transaction: transaction,
                cancellationToken: cancellationToken));

        return row is null ? null : MapRow(row);
    }

    private static MessageRow MapRow(MessageRowRaw row) => new(
        row.Id,
        row.ChatId,
        row.SenderId,
        row.ReplyToId,
        row.Text,
        ParseState(row.ContentState),
        SqliteTime.Parse(row.SentAt),
        SqliteTime.ParseOrNull(row.DeliveredAt),
        SqliteTime.ParseOrNull(row.EditedAt),
        SqliteTime.ParseOrNull(row.ReadAt),
        SqliteTime.ParseOrNull(row.DeletedAt));

    private static MessageContentState ParseState(string value) => value switch
    {
        "stored" => MessageContentState.Stored,
        "not_stored" => MessageContentState.NotStored,
        "deleted" => MessageContentState.Deleted,
        _ => throw new InvalidOperationException($"Unknown message content state '{value}'")
    };

    private static bool IsUniqueViolation(SqliteException exception) =>
        exception.SqliteErrorCode == 19 &&
        exception.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);

    private sealed class PinRowRaw : MessageRowRaw
    {
        public long PinnedBy { get; set; }
        public string PinnedAt { get; set; } = "";
    }

    private class MessageRowRaw
    {
        public long Id { get; set; }
        public long ChatId { get; set; }
        public long SenderId { get; set; }
        public long? ReplyToId { get; set; }
        public Secret? Text { get; set; }
        public string ContentState { get; set; } = "";
        public string SentAt { get; set; } = "";
        public string? DeliveredAt { get; set; }
        public string? EditedAt { get; set; }
        public string? DeletedAt { get; set; }
        public string? ReadAt { get; set; }
        public string? ClientRefPayload { get; set; }
    }
}
