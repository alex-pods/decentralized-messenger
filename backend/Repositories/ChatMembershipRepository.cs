using Dapper;
using Messenger.Api.Database;

namespace Messenger.Api.Repositories;

public interface IChatMembershipRepository
{
    Task<bool> ChatExists(long chatId);
    Task<bool> IsMember(long chatId, long userId);
    Task<string?> GetRole(long chatId, long userId);
}

public sealed class ChatMembershipRepository(DbConnectionFactory db) : IChatMembershipRepository
{
    public async Task<bool> ChatExists(long chatId)
    {
        using var connection = db.Open();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM chats WHERE id = @chatId", new { chatId }) > 0;
    }

    public async Task<bool> IsMember(long chatId, long userId)
    {
        using var connection = db.Open();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM users_chats WHERE chat_id = @chatId AND user_id = @userId",
            new { chatId, userId }) > 0;
    }

    public async Task<string?> GetRole(long chatId, long userId)
    {
        using var connection = db.Open();
        return await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT role FROM users_chats WHERE chat_id = @chatId AND user_id = @userId",
            new { chatId, userId });
    }
}
