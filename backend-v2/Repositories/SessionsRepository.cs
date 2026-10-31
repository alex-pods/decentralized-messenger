using Dapper;
using System.Globalization;
using Messenger.Api.Database;
using Messenger.Api.Infrastructure;

namespace Messenger.Api.Repositories;

public interface ISessionsRepository
{
    Task Create(long userId, string tokenHash, string expiresAt);
    Task<long?> GetUserId(string tokenHash);
    Task Delete(string tokenHash);
}

public sealed class SessionsRepository(DbConnectionFactory db) : ISessionsRepository
{
    public async Task Create(long userId, string tokenHash, string expiresAt)
    {
        using var connection = db.Open();
        await connection.ExecuteAsync(
            "INSERT INTO sessions(user_id, token_hash, expires_at) VALUES(@userId, @tokenHash, @expiresAt)",
            new { userId, tokenHash = Crypto.Encrypt(tokenHash), expiresAt });
    }

    public async Task<long?> GetUserId(string tokenHash)
    {
        using var connection = db.Open();
        return await connection.QuerySingleOrDefaultAsync<long?>(
            "SELECT user_id FROM sessions WHERE token_hash = @tokenHash AND expires_at > @now",
            new { tokenHash = Crypto.Encrypt(tokenHash), now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
    }

    public async Task Delete(string tokenHash)
    {
        using var connection = db.Open();
        await connection.ExecuteAsync("DELETE FROM sessions WHERE token_hash = @tokenHash", new { tokenHash = Crypto.Encrypt(tokenHash) });
    }
}
