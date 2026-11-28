using Dapper;
using Messenger.Api.Database;

namespace Messenger.Api.Repositories;

public interface IChatMembershipRepository
{
    Task<bool> ChatExists(string chatId);
    Task<bool> IsMember(string chatId, string userId);
    Task<string?> GetRole(string chatId, string userId);
}

public sealed class ChatMembershipRepository(DbConnectionFactory db) : IChatMembershipRepository
{
    public async Task<bool> ChatExists(string chatId)
    {
        using var connection = db.Open();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM chats WHERE id = @chatId", new { chatId }) > 0;
    }

    public async Task<bool> IsMember(string chatId, string userId)
    {
        using var connection = db.Open();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM users_chats WHERE chat_id = @chatId AND user_id = @userId",
            new { chatId, userId }) > 0;
    }

    public async Task<string?> GetRole(string chatId, string userId)
    {
        using var connection = db.Open();
        return await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT role FROM users_chats WHERE chat_id = @chatId AND user_id = @userId",
            new { chatId, userId });
    }
}
