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
    Task<MessageRow?> EditAsync(long messageId, string newText, CancellationToken cancellationToken = default);
    Task<MessageRow?> DeleteAsync(long messageId, CancellationToken cancellationToken = default);
    Task HideAsync(long messageId, long userId, CancellationToken cancellationToken = default);
}

public sealed class MessagesRepository(DbConnectionFactory db) : IMessagesRepository
{
    private const string RowSelect = """
        SELECT m.id, m.chat_id, m.sender_id, m.reply_to_id, m.text, m.content_state,
               m.sent_at, m.delivered_at, m.edited_at, m.deleted_at, v.read_at
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

    public async Task<AddMessageResult> AddAsync(NewMessage message, CancellationToken cancellationToken = default)
    {
        using var connection = db.Open();

        if (!string.IsNullOrEmpty(message.ClientRef))
        {
            var existingId = await FindByClientRef(connection, message.SenderId, message.ClientRef, cancellationToken);
            if (existingId is { } id)
                return new AddMessageResult((await LoadRow(connection, id, cancellationToken))!, false);
        }

        try
        {
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO messages(chat_id, sender_id, text, reply_to_id, client_ref)
                VALUES (@ChatId, @SenderId, @Text, @ReplyToId, @ClientRef)
                RETURNING id
                """,
                new
                {
                    message.ChatId,
                    message.SenderId,
                    Text = Crypto.Encrypt(message.Text),
                    message.ReplyToId,
                    ClientRef = Crypto.Encrypt(message.ClientRef)
                }, cancellationToken: cancellationToken));
            return new AddMessageResult((await LoadRow(connection, id, cancellationToken))!, true);
        }
        catch (SqliteException exception) when (
            message.ClientRef is not null && IsUniqueViolation(exception))
        {
            var existingId = await FindByClientRef(
                connection, message.SenderId, message.ClientRef, cancellationToken);
            if (existingId is null) throw;
            return new AddMessageResult((await LoadRow(connection, existingId.Value, cancellationToken))!, false);
        }
    }

    public async Task<MessageRow?> EditAsync(
        long messageId, string newText, CancellationToken cancellationToken = default)
    {
        using var connection = db.Open();
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE messages
            SET text = @newText, edited_at = CURRENT_TIMESTAMP
            WHERE id = @messageId AND content_state = 'stored'
            """,
            new { messageId, newText = Crypto.Encrypt(newText) }, cancellationToken: cancellationToken));
        return changed == 0 ? null : await LoadRow(connection, messageId, cancellationToken);
    }

    public async Task<MessageRow?> DeleteAsync(long messageId, CancellationToken cancellationToken = default)
    {
        using var connection = db.Open();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE messages
            SET content_state = 'deleted', deleted_at = CURRENT_TIMESTAMP, text = NULL
            WHERE id = @messageId AND content_state <> 'deleted'
            """,
            new { messageId }, cancellationToken: cancellationToken));
        return await LoadRow(connection, messageId, cancellationToken);
    }

    public async Task HideAsync(long messageId, long userId, CancellationToken cancellationToken = default)
    {
        using var connection = db.Open();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR IGNORE INTO message_hidden(user_id, message_id) VALUES (@userId, @messageId)",
            new { userId, messageId }, cancellationToken: cancellationToken));
    }

    private static async Task<long?> FindByClientRef(
        IDbConnection connection, long senderId, string clientRef, CancellationToken cancellationToken)
        => await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM messages WHERE sender_id = @senderId AND client_ref = @clientRef",
            new { senderId, clientRef = Crypto.Encrypt(clientRef) }, cancellationToken: cancellationToken));

    private static async Task<MessageRow?> LoadRow(
        IDbConnection connection, long messageId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<MessageRowRaw>(new CommandDefinition(
            $"{RowSelect} WHERE m.id = @messageId",
            new { messageId }, cancellationToken: cancellationToken));
        return row is null ? null : MapRow(row);
    }

    private static MessageRow MapRow(MessageRowRaw row) => new(
        row.Id,
        row.ChatId,
        row.SenderId,
        row.ReplyToId,
        Crypto.Decrypt(row.Text),
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

    private sealed class MessageRowRaw
    {
        public long Id { get; set; }
        public long ChatId { get; set; }
        public long SenderId { get; set; }
        public long? ReplyToId { get; set; }
        public string? Text { get; set; }
        public string ContentState { get; set; } = "";
        public string SentAt { get; set; } = "";
        public string? DeliveredAt { get; set; }
        public string? EditedAt { get; set; }
        public string? DeletedAt { get; set; }
        public string? ReadAt { get; set; }
    }
}
