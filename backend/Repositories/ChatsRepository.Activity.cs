using System.Data;
using Dapper;
using Messenger.Api.Infrastructure;
using Messenger.Api.Common.Errors;

namespace Messenger.Api.Repositories;

public sealed record GroupActivity(long Id, string Kind, string? DisplayName, string OccurredAt);

public sealed partial class ChatsRepository
{
    public async Task<IReadOnlyList<GroupActivity>> GetGroupActivityAsync(
        long chatId, long viewerId, CancellationToken ct = default)
    {
        using var connection = db.Open();
        var rows = (await connection.QueryAsync<ActivityRow>(new CommandDefinition(
            """
            SELECT a.id AS Id, a.kind AS Kind, a.occurred_at AS OccurredAt, u.name AS DisplayName
            FROM users_chats viewer
            LEFT JOIN (
                SELECT id, user_id, kind, occurred_at FROM group_activity
                WHERE chat_id = @chatId ORDER BY id DESC LIMIT 30
            ) a ON 1 = 1
            LEFT JOIN users u ON u.id = a.user_id
            WHERE viewer.chat_id = @chatId AND viewer.user_id = @viewerId
            ORDER BY a.id DESC
            """, new { chatId, viewerId }, cancellationToken: ct))).ToArray();
        if (rows.Length == 0) throw new NotFoundException("Chat not found");
        return rows.Where(row => row.Id.HasValue)
            .Select(row => new GroupActivity(row.Id!.Value, row.Kind, row.DisplayName?.Value, row.OccurredAt)).ToArray();
    }

    static Task RecordActivity(IDbConnection connection, IDbTransaction transaction,
        long chatId, long userId, string kind, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO group_activity(chat_id, user_id, kind) VALUES (@chatId, @userId, @kind)",
            new { chatId, userId, kind }, transaction, cancellationToken: ct));

    sealed class ActivityRow
    {
        public long? Id { get; set; }
        public string Kind { get; set; } = "";
        public Secret? DisplayName { get; set; }
        public string OccurredAt { get; set; } = "";
    }
}
