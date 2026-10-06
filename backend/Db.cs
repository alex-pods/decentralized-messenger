using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace MessengerBackend;

/// <summary>
/// SQLite-хранилище строго по схеме спринта: Users, Chats, ChatMembers, DirectChats,
/// Messages, Attachments, MessageReactions, UserSessions, UserSettings.
/// </summary>
public static class Db
{
    public static string Path { get; } =
        Environment.GetEnvironmentVariable("MESSENGER_DB") ?? "messenger.db";

    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public static SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={Path};Cache=Shared");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public static void Init()
    {
        Directory.CreateDirectory(PathUtilities.DirOf(Path));
        using var probe = Open();
        var cols = probe.Query<string>("SELECT name FROM pragma_table_info('users')").ToList();
        var hasUsers = cols.Count > 0;
        if (hasUsers && !cols.Any(c => c == "tag"))
            throw new InvalidOperationException(
                $"БД {Path} создана старой схемой (нет Users.tag). Архивируйте её и запустите сервис заново.");

        using var c = Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }
        c.Execute("""
            CREATE TABLE IF NOT EXISTS users(
              id                   INTEGER PRIMARY KEY AUTOINCREMENT,
              tag                  VARCHAR(16)  NOT NULL UNIQUE,
              password_hash        VARCHAR(128) NOT NULL,
              display_name         VARCHAR(32)  NOT NULL,
              path_to_avatar_file  TEXT,
              bio                  TEXT,
              join_date            TEXT NOT NULL,
              last_seen_at         TEXT
            );
            CREATE TABLE IF NOT EXISTS user_sessions(
              id          INTEGER PRIMARY KEY AUTOINCREMENT,
              user_id     INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
              token_hash  VARCHAR(128) NOT NULL UNIQUE,
              created_at  TEXT NOT NULL,
              expires_at  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS user_settings(
              user_id      INTEGER PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
              save_history INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS chats(
              id         INTEGER PRIMARY KEY AUTOINCREMENT,
              type       VARCHAR(16) NOT NULL CHECK(type IN ('direct','group')),
              title      VARCHAR(64),
              created_at TEXT NOT NULL,
              created_by INTEGER REFERENCES users(id) ON DELETE SET NULL
            );
            CREATE TABLE IF NOT EXISTS chat_members(
              chat_id   INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
              user_id   INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
              role      VARCHAR(16) NOT NULL DEFAULT 'member' CHECK(role IN ('member','admin','owner')),
              joined_at TEXT NOT NULL,
              PRIMARY KEY(chat_id, user_id)
            );
            CREATE TABLE IF NOT EXISTS direct_chats(
              chat_id  INTEGER PRIMARY KEY REFERENCES chats(id) ON DELETE CASCADE,
              user1_id INTEGER NOT NULL REFERENCES users(id),
              user2_id INTEGER NOT NULL REFERENCES users(id),
              UNIQUE(user1_id, user2_id),
              CHECK(user1_id < user2_id)
            );
            CREATE TABLE IF NOT EXISTS messages(
              id           INTEGER PRIMARY KEY AUTOINCREMENT,
              chat_id      INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
              sender_id    INTEGER NOT NULL REFERENCES users(id),
              reply_to_id INTEGER REFERENCES messages(id),
              text         TEXT NOT NULL,
              sent_at      TEXT NOT NULL,
              delivered_at TEXT,
              edited_at    TEXT,
              read_at      TEXT,
              deleted_at   TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_messages_chat ON messages(chat_id, id);
            CREATE TABLE IF NOT EXISTS attachments(
              id              INTEGER PRIMARY KEY AUTOINCREMENT,
              message_id      INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
              file_name       TEXT NOT NULL,
              path_to_file    TEXT NOT NULL,
              file_type       VARCHAR(32) NOT NULL,
              file_size_bytes INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS message_reactions(
              id         INTEGER PRIMARY KEY AUTOINCREMENT,
              message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
              user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
              emoji      VARCHAR(32) NOT NULL,
              UNIQUE(message_id, user_id, emoji)
            );
            CREATE TABLE IF NOT EXISTS message_hidden(
              message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
              user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
              PRIMARY KEY(message_id, user_id)
            );
            CREATE TABLE IF NOT EXISTS chat_hidden(
              chat_id INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
              user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
              PRIMARY KEY(chat_id, user_id)
            );
            """);
        // миграции существующей БД (идемпотентны)
        foreach (var sql in new[]
                 {
                     "ALTER TABLE chat_members ADD COLUMN last_read_message_id INTEGER",
                     "ALTER TABLE chat_members ADD COLUMN read_at TEXT",
                 })
            try { c.Execute(sql); }
            catch (SqliteException) { /* колонка уже есть */ }
    }

    /// <summary>SQLite — один писатель; записи сериализуются, читатели живут параллельно (WAL).</summary>
    public static async Task<T> WriteAsync<T>(Func<IDbConnection, Task<T>> action)
    {
        await WriteLock.WaitAsync();
        try
        {
            await using var c = Open();
            return await action(c);
        }
        finally { WriteLock.Release(); }
    }

    public static T Write<T>(Func<IDbConnection, T> action)
    {
        WriteLock.Wait();
        try
        {
            using var c = Open();
            return action(c);
        }
        finally { WriteLock.Release(); }
    }

    public static T Read<T>(Func<IDbConnection, T> action)
    {
        using var c = Open();
        return action(c);
    }
}

internal static class PathUtilities
{
    public static string DirOf(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        return string.IsNullOrEmpty(dir) ? "." : dir;
    }
}

// ---------- Строки таблиц ----------

public class UserRow
{
    public long Id { get; set; }
    public string Tag { get; set; } = "";
    public string Password_Hash { get; set; } = "";
    public string Display_Name { get; set; } = "";
    public string? Path_To_Avatar_File { get; set; }
    public string? Bio { get; set; }
    public string Join_Date { get; set; } = "";
    public string? Last_Seen_At { get; set; }
}

public class ChatRow
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public string? Title { get; set; }
    public string Created_At { get; set; } = "";
    public long? Created_By { get; set; }
}

public class MemberRow
{
    public long Chat_Id { get; set; }
    public long User_Id { get; set; }
    public string Role { get; set; } = "member";
    public string Joined_At { get; set; } = "";
    public long? Last_Read_Message_Id { get; set; }
    public string? Read_At { get; set; }
    public string? Tag { get; set; }
    public string? Display_Name { get; set; }
    public string? Path_To_Avatar_File { get; set; }
}

public class DirectChatRow
{
    public long Chat_Id { get; set; }
    public long User1_Id { get; set; }
    public long User2_Id { get; set; }
}

public class MessageRow
{
    public long Id { get; set; }
    public long Chat_Id { get; set; }
    public long Sender_Id { get; set; }
    public long? Reply_To_Id { get; set; }
    public string Text { get; set; } = "";
    public string Sent_At { get; set; } = "";
    public string? Delivered_At { get; set; }
    public string? Edited_At { get; set; }
    public string? Read_At { get; set; }
    public string? Deleted_At { get; set; }
}

public class AttachmentRow
{
    public long Id { get; set; }
    public long Message_Id { get; set; }
    public string File_Name { get; set; } = "";
    public string Path_To_File { get; set; } = "";
    public string File_Type { get; set; } = "";
    public long File_Size_Bytes { get; set; }
}

public class ReactionRow
{
    public long Id { get; set; }
    public long Message_Id { get; set; }
    public long User_Id { get; set; }
    public string Emoji { get; set; } = "";
}
