using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Models;

namespace Messenger.Api.Repositories;

public interface IMessagesRepository
{
    Task<List<Message>> GetByChat(long chatId, long userId, long? beforeId, long? afterId, int limit);
    Task<Message?> GetById(long id);
    Task<MessageInsertResult> Insert(long chatId, long senderId, string text, long? replyToId, string? clientRef);
    Task UpdateText(long id, string text);
    Task<List<string>> DeleteForEveryone(long id);
    Task HideForUser(long id, long userId);
    Task MarkRead(long chatId, long userId, long messageId);
    Task<string?> GetReadAt(long messageId);
}

public sealed record MessageInsertResult(long Id, bool Created, Message? Existing);

public sealed class MessagesRepository(DbConnectionFactory db) : IMessagesRepository
{
    private const string Columns = """
        id, chat_id AS ChatId, sender_id AS SenderId, text,
        sent_at AS SentAt, edited_at AS EditedAt, delivered_at AS DeliveredAt,
        deleted_at AS DeletedAt, reply_to_id AS ReplyToId, content_state AS ContentState
        """;

    public async Task<List<Message>> GetByChat(long chatId, long userId, long? beforeId, long? afterId, int limit)
    {
        using var connection = db.Open();
        var descending = afterId is null;
        var order = descending ? "DESC" : "ASC";
        var rows = await connection.QueryAsync<Message>(
            $"""
            SELECT {Columns} FROM messages m
            WHERE m.chat_id = @chatId
              AND (@beforeId IS NULL OR m.id < @beforeId)
              AND (@afterId IS NULL OR m.id > @afterId)
              AND NOT EXISTS (SELECT 1 FROM message_hidden h WHERE h.message_id = m.id AND h.user_id = @userId)
            ORDER BY m.id {order} LIMIT @limit
            """, new { chatId, userId, beforeId, afterId, limit });
        var result = rows.ToList();
        if (descending) result.Reverse();
        return result;
    }

    public async Task<Message?> GetById(long id)
    {
        using var connection = db.Open();
        return await connection.QuerySingleOrDefaultAsync<Message>(
            $"SELECT {Columns} FROM messages WHERE id = @id", new { id });
    }

    public async Task<MessageInsertResult> Insert(long chatId, long senderId, string text, long? replyToId, string? clientRef)
    {
        using var connection = db.Open();
        if (clientRef is not null)
        {
            var existing = await connection.QuerySingleOrDefaultAsync<Message>(
                $"SELECT {Columns} FROM messages WHERE sender_id = @senderId AND client_ref = @clientRef",
                new { senderId, clientRef });
            if (existing is not null)
                return new MessageInsertResult(existing.Id, false, existing);
        }

        var inserted = await connection.ExecuteAsync(
            """
            INSERT OR IGNORE INTO messages(chat_id, sender_id, text, reply_to_id, client_ref)
            VALUES(@chatId, @senderId, @text, @replyToId, @clientRef)
            """, new { chatId, senderId, text, replyToId, clientRef });

        if (inserted == 0 && clientRef is not null)
        {
            var existing = await connection.QuerySingleOrDefaultAsync<Message>(
                $"SELECT {Columns} FROM messages WHERE sender_id = @senderId AND client_ref = @clientRef",
                new { senderId, clientRef });
            if (existing is not null)
                return new MessageInsertResult(existing.Id, false, existing);
        }

        var id = await connection.ExecuteScalarAsync<long>("SELECT last_insert_rowid()");
        return new MessageInsertResult(id, true, null);
    }

    public async Task UpdateText(long id, string text)
    {
        using var connection = db.Open();
        await connection.ExecuteAsync(
            "UPDATE messages SET text = @text, edited_at = CURRENT_TIMESTAMP WHERE id = @id",
            new { id, text });
    }

    public async Task<List<string>> DeleteForEveryone(long id)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        var paths = (await connection.QueryAsync<string>(
            "SELECT storage_path FROM attachments WHERE message_id = @id", new { id }, transaction)).ToList();
        await connection.ExecuteAsync(
            "UPDATE messages SET content_state = 'deleted', text = NULL, deleted_at = CURRENT_TIMESTAMP WHERE id = @id AND content_state <> 'deleted'",
            new { id }, transaction);
        transaction.Commit();
        return paths;
    }

    public async Task HideForUser(long id, long userId)
    {
        using var connection = db.Open();
        await connection.ExecuteAsync(
            "INSERT OR IGNORE INTO message_hidden(user_id, message_id) VALUES(@userId, @id)",
            new { userId, id });
    }

    public async Task MarkRead(long chatId, long userId, long messageId)
    {
        using var connection = db.Open();
        await connection.ExecuteAsync(
            """
            UPDATE users_chats
            SET last_read_at = CASE WHEN last_read_message_id < @messageId THEN CURRENT_TIMESTAMP ELSE last_read_at END,
                last_read_message_id = MAX(last_read_message_id, @messageId)
            WHERE chat_id = @chatId AND user_id = @userId
            """, new { chatId, userId, messageId });
    }

    public async Task<string?> GetReadAt(long messageId)
    {
        using var connection = db.Open();
        return await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT read_at FROM message_read_at WHERE message_id = @messageId", new { messageId });
    }
}
