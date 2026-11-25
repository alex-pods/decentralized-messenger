using Dapper;
using Messenger.Api.Database;
using Microsoft.Data.Sqlite;

namespace Messenger.Api.Antispam;

public sealed class AntispamRepository(DbConnectionFactory db)
{
    public string? BlockId(long userId)
    {
        using var connection = db.Open();
        return connection.QuerySingleOrDefault<string>("SELECT block_id FROM antispam_blocks WHERE user_id=@userId", new { userId });
    }

    public bool Reserve(long userId, string key, string? clientRef, long now, AntispamOptions options)
    {
        using var connection = (SqliteConnection)db.Open();
        // Immediate transaction serializes bursts across requests, chats and server processes.
        using var tx = connection.BeginTransaction(deferred: false);
        var args = new { userId, key, clientRef, now, blockId = Guid.NewGuid().ToString("N"), cutoff = now - options.WindowSeconds * 1000L };
        if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM antispam_blocks WHERE user_id=@userId", args, tx) != 0)
            return false;
        // Existing idempotent sends (including content conflicts) cannot create another message.
        if (clientRef != null && connection.ExecuteScalar<long>("SELECT COUNT(*) FROM messages WHERE sender_id=@userId AND client_ref=@clientRef", args, tx) != 0)
        { tx.Commit(); return true; }
        connection.Execute("DELETE FROM antispam_attempts WHERE user_id=@userId AND occurred_at<=@cutoff", args, tx);
        if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM antispam_attempts WHERE user_id=@userId AND attempt_key=@key", args, tx) != 0)
        { tx.Commit(); return true; }
        var count = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM antispam_attempts WHERE user_id=@userId", args, tx);
        if (count >= options.Burst)
        {
            connection.Execute("INSERT INTO antispam_blocks(user_id,block_id,blocked_at) VALUES(@userId,@blockId,@now)", args, tx);
            tx.Commit(); return false;
        }
        connection.Execute("INSERT INTO antispam_attempts(user_id,attempt_key,occurred_at) VALUES(@userId,@key,@now)", args, tx);
        tx.Commit(); return true;
    }

    public bool Clear(long userId, string blockId)
    {
        using var connection = db.Open();
        using var tx = connection.BeginTransaction();
        var cleared = connection.Execute("DELETE FROM antispam_blocks WHERE user_id=@userId AND block_id=@blockId", new { userId, blockId }, tx) == 1;
        if (cleared) connection.Execute("DELETE FROM antispam_attempts WHERE user_id=@userId", new { userId }, tx);
        tx.Commit(); return cleared;
    }
}
