using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Infrastructure;
using Messenger.Api.Repositories.Models;

namespace Messenger.Api.Repositories;

public interface IReactionsRepository
{
    Task<IReadOnlyDictionary<long, IReadOnlyList<ReactionRow>>> GetByMessageIdsAsync(
        IReadOnlyCollection<long> messageIds,
        CancellationToken ct = default);

    Task<bool> AddAsync(long messageId, long userId, Secret emoji, CancellationToken ct = default);

    Task<bool> RemoveAsync(long messageId, long userId, Secret emoji, CancellationToken ct = default);
}

public sealed class ReactionsRepository(DbConnectionFactory db) : IReactionsRepository
{
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<ReactionRow>>> GetByMessageIdsAsync(
        IReadOnlyCollection<long> messageIds,
        CancellationToken ct = default)
    {
        if (messageIds.Count == 0)
            return new Dictionary<long, IReadOnlyList<ReactionRow>>();

        using var c = db.Open();
        var raw = await c.QueryAsync<ReactionRowRaw>(new CommandDefinition(
            """
            SELECT message_id, emoji, user_id
            FROM message_reactions
            WHERE message_id IN @messageIds
            ORDER BY message_id, created_at, user_id
            """,
            new { messageIds }, cancellationToken: ct));

        return raw
            .GroupBy(x => x.MessageId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ReactionRow>)g
                    .GroupBy(x => x.Emoji)
                    .OrderBy(eg => eg.Key.Value, StringComparer.Ordinal)
                    .Select(eg => new ReactionRow(eg.Key.Value, eg.Select(x => x.UserId).ToList()))
                    .ToList());
    }

    public async Task<bool> AddAsync(long messageId, long userId, Secret emoji, CancellationToken ct = default)
    {
        using var c = db.Open();
        var rows = await c.ExecuteAsync(new CommandDefinition(
            "INSERT OR IGNORE INTO message_reactions(message_id, user_id, emoji) VALUES (@messageId, @userId, @emoji)",
            new { messageId, userId, emoji }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> RemoveAsync(long messageId, long userId, Secret emoji, CancellationToken ct = default)
    {
        using var c = db.Open();
        var rows = await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM message_reactions WHERE message_id = @messageId AND user_id = @userId AND emoji = @emoji",
            new { messageId, userId, emoji }, cancellationToken: ct));
        return rows > 0;
    }

    private sealed record ReactionRowRaw(long MessageId, Secret Emoji, long UserId);
}
