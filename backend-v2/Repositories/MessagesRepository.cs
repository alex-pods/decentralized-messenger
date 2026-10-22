using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Models;

namespace Messenger.Api.Repositories;

public interface IMessagesRepository
{
    Task<List<Message>> GetByChat(long chatId, long? beforeId, int limit);
    Task<Dictionary<long, Message>> GetLastBySenders(IReadOnlyCollection<long> senderIds);
}

public sealed class MessagesRepository(DbConnectionFactory db) : IMessagesRepository
{
    const string Columns = "id, chat_id AS ChatId, sender_id AS SenderId, text, sent_at AS SentAt";

    public async Task<List<Message>> GetByChat(long chatId, long? beforeId, int limit)
    {
        using var c = db.Open();
        var rows = await c.QueryAsync<Message>(
            $"SELECT {Columns} FROM messages WHERE chat_id = @chatId AND (@beforeId IS NULL OR id < @beforeId) ORDER BY id DESC LIMIT @limit",
            new { chatId, beforeId, limit });
        return rows.Reverse().ToList(); // от старых к новым
    }

    /// <summary>Последнее сообщение каждого отправителя — одним запросом.</summary>
    public async Task<Dictionary<long, Message>> GetLastBySenders(IReadOnlyCollection<long> senderIds)
    {
        if (senderIds.Count == 0) return [];
        using var c = db.Open();
        var rows = await c.QueryAsync<Message>(
            $"SELECT {Columns} FROM messages WHERE id IN (SELECT MAX(id) FROM messages WHERE sender_id IN @senderIds GROUP BY sender_id)",
            new { senderIds });
        return rows.ToDictionary(m => m.SenderId);
    }
}
