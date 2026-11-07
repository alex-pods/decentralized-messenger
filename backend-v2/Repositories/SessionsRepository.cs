using Dapper;
using System.Globalization;
using Messenger.Api.Database;
using Messenger.Api.Infrastructure;

namespace Messenger.Api.Repositories;

public interface ISessionsRepository
{
    Task Create(long userId, Secret tokenHash, string expiresAt);
    Task<long?> GetUserId(Secret tokenHash);
    Task Delete(Secret tokenHash);
}

public sealed class SessionsRepository(DbConnectionFactory db) : ISessionsRepository
{
    public async Task Create(long userId, Secret tokenHash, string expiresAt)
    {
        using var connection = db.Open();
        await connection.ExecuteAsync(
            "INSERT INTO sessions(user_id, token_hash, expires_at) VALUES(@userId, @tokenHash, @expiresAt)",
            new { userId, tokenHash, expiresAt });
    }

    public async Task<long?> GetUserId(Secret tokenHash)
    {
        using var connection = db.Open();
        return await connection.QuerySingleOrDefaultAsync<long?>(
            "SELECT user_id FROM sessions WHERE token_hash = @tokenHash AND expires_at > @now",
            new { tokenHash, now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
    }

    public async Task Delete(Secret tokenHash)
    {
        using var connection = db.Open();
        await connection.ExecuteAsync("DELETE FROM sessions WHERE token_hash = @tokenHash", new { tokenHash });
    }
}
