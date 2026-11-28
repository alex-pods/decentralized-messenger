using Dapper;
using Messenger.Api.Common.Errors;
using Messenger.Api.Database;
using Microsoft.Data.Sqlite;

namespace Messenger.Api.Repositories;

internal sealed class RelayReceiptsRepository(DbConnectionFactory db)
{
    internal sealed record Receipt(string SenderId, long Sequence, string SentAt, long QueuedConnections);

    internal async Task<(Receipt Receipt, bool Created)> ReserveAsync(
        string id, string chatId, string senderId, string sentAt, CancellationToken ct)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        var key = new { id, chatId, senderId, sentAt };
        var existing = await connection.QuerySingleOrDefaultAsync<Receipt>(new CommandDefinition(
            """
            SELECT sender_id AS SenderId, sequence AS Sequence, sent_at AS SentAt,
                   queued_connections AS QueuedConnections
            FROM relay_receipts WHERE id = @id
            """, key, transaction, cancellationToken: ct));
        if (existing != null)
        {
            transaction.Commit();
            return (existing, false);
        }

        if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM messages WHERE id = @id)", key, transaction, cancellationToken: ct)))
            throw AppException.Conflict("message_id_conflict", "Message ID already used");

        var sequence = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            """
            UPDATE chats SET last_message_sequence = last_message_sequence + 1
            WHERE id = @chatId AND EXISTS (
                SELECT 1 FROM users_chats WHERE chat_id = @chatId AND user_id = @senderId
            )
            RETURNING last_message_sequence
            """, key, transaction, cancellationToken: ct));
        if (sequence is null) throw new NotFoundException("Chat not found");

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO relay_receipts(id, chat_id, sender_id, sequence, sent_at)
            VALUES (@id, @chatId, @senderId, @sequence, @sentAt)
            """, new { id, chatId, senderId, sequence, sentAt }, transaction, cancellationToken: ct));
        transaction.Commit();
        return (new Receipt(senderId, sequence.Value, sentAt, 0), true);
    }

    internal async Task CompleteAsync(string id, int queued)
    {
        using var connection = db.Open();
        if (queued == 0)
            await connection.ExecuteAsync("DELETE FROM relay_receipts WHERE id = @id AND queued_connections = 0", new { id });
        else
            await connection.ExecuteAsync("UPDATE relay_receipts SET queued_connections = @queued WHERE id = @id",
                new { id, queued });
    }

    internal static bool IsIdConflict(SqliteException exception) =>
        exception.SqliteExtendedErrorCode == 1811 &&
        exception.Message.Contains("message ID reserved for relay", StringComparison.Ordinal);
}
