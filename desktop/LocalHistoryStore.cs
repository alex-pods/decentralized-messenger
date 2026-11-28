using System.Security.Cryptography;
using System.Text;
using Messenger.Identifiers;
using Microsoft.Data.Sqlite;

namespace MessengerDesktop;

internal sealed class LocalHistoryStore
{
    internal static string DirectoryPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "messenger-desktop", "history");

    private readonly string _connectionString;
    private readonly LocalHistoryKey _key;

    internal LocalHistoryStore(string fingerprint, string userId)
    {
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint + ":" + userId)));
        var path = DirectoryPath;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var database = Path.Combine(path, scope + ".db");
        _key = new LocalHistoryKey(Path.Combine(path, scope + ".key"), scope);
        var options = new SqliteConnectionStringBuilder
        {
            DataSource = database, ForeignKeys = true, DefaultTimeout = 1, Pooling = false
        };
        _connectionString = options.ToString();
        using var db = Open();
        Execute(db, null, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS chats (id TEXT PRIMARY KEY, preview BLOB, members BLOB, pins BLOB);
            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY, chat_id TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
                sequence INTEGER NOT NULL, data BLOB NOT NULL,
                UNIQUE (chat_id, sequence)
            );
            CREATE TABLE IF NOT EXISTS activity (
                chat_id TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
                id INTEGER NOT NULL, data BLOB NOT NULL, PRIMARY KEY (chat_id, id)
            );
            CREATE TABLE IF NOT EXISTS settings (id TEXT PRIMARY KEY, data BLOB NOT NULL);
            """);
    }

    internal void SaveChats(List<ChatDto> chats)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        Execute(db, tx, "UPDATE chats SET preview = NULL");
        foreach (var chat in chats)
        {
            var metadata = new ChatDto
            {
                Id = chat.Id, Type = chat.Type, Title = chat.Title, OtherUser = chat.OtherUser,
                CreatedAt = chat.CreatedAt, CreatedBy = chat.CreatedBy
            };
            Execute(db, tx, """
                INSERT INTO chats(id, preview) VALUES ($chat, $data)
                ON CONFLICT(id) DO UPDATE SET preview=excluded.preview
                """, ("$chat", chat.Id), ("$data", _key.Encrypt("chat:" + chat.Id, metadata)));
        }
        Execute(db, tx, "DELETE FROM chats WHERE preview IS NULL");
        tx.Commit();
    }

    internal List<ChatDto> ReadChats()
    {
        using var db = Open();
        using var command = Command(db, null, """
            SELECT c.id, c.preview, m.id, m.data FROM chats c
            LEFT JOIN messages m ON m.id = (
                SELECT id FROM messages WHERE chat_id=c.id ORDER BY sequence DESC LIMIT 1
            )
            WHERE c.preview IS NOT NULL
            """);
        using var rows = command.ExecuteReader();
        var items = new List<ChatDto>();
        while (rows.Read())
        {
            var chat = _key.Decrypt<ChatDto>("chat:" + rows.GetString(0), rows.GetFieldValue<byte[]>(1));
            if (!rows.IsDBNull(2))
                chat.LastMessage = _key.Decrypt<MessageDto>("message:" + rows.GetString(2), rows.GetFieldValue<byte[]>(3));
            items.Add(chat);
        }
        return items.OrderByDescending(c => c.LastMessage?.SentAt).ThenByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id, StringComparer.Ordinal).ToList();
    }

    internal void RemoveChat(string chatId)
    {
        using var db = Open();
        Execute(db, null, "DELETE FROM chats WHERE id=$chat", ("$chat", chatId));
    }

    internal void SavePage(string chatId, IReadOnlyList<MessageDto> page, int limit, long? before, long? after)
    {
        // A full page proves its bounded interval; a short page also proves the remaining tail is empty.
        var full = page.Count >= limit;
        var from = after ?? (full ? page[0].Sequence - 1 : 0);
        var through = before is { } upper ? upper - 1 : after.HasValue && full ? page[^1].Sequence : long.MaxValue;
        using var db = Open();
        using var tx = db.BeginTransaction();
        EnsureChat(db, tx, chatId);
        Execute(db, tx, "DELETE FROM messages WHERE chat_id=$chat AND sequence>$from AND sequence<=$through",
            ("$chat", chatId), ("$from", from), ("$through", through));
        foreach (var message in page) SaveMessage(db, tx, chatId, message);
        tx.Commit();
    }

    internal List<MessageDto> ReadMessages(string chatId, int limit, long? before = null)
    {
        using var db = Open();
        using var command = Command(db, null, """
            SELECT id, data FROM messages WHERE chat_id=$chat AND sequence<$before ORDER BY sequence DESC LIMIT $limit
            """, ("$chat", chatId), ("$before", before ?? long.MaxValue), ("$limit", limit));
        using var rows = command.ExecuteReader();
        var result = new List<MessageDto>();
        while (rows.Read()) result.Add(_key.Decrypt<MessageDto>("message:" + rows.GetString(0), rows.GetFieldValue<byte[]>(1)));
        result.Reverse();
        return result;
    }

    internal void PutMessage(string chatId, MessageDto message)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        EnsureChat(db, tx, chatId);
        SaveMessage(db, tx, chatId, message);
        tx.Commit();
    }

    internal void RemoveMessage(string chatId, string id)
    {
        using var db = Open();
        Execute(db, null, "DELETE FROM messages WHERE chat_id=$chat AND id=$id", ("$chat", chatId), ("$id", id));
    }

    internal void UpdateMessage(string chatId, string id, Action<MessageDto> update)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = Command(db, tx, "SELECT data FROM messages WHERE chat_id=$chat AND id=$id",
            ("$chat", chatId), ("$id", id));
        if (cmd.ExecuteScalar() is byte[] data)
        {
            var message = _key.Decrypt<MessageDto>("message:" + id, data);
            update(message);
            SaveMessage(db, tx, chatId, message);
        }
        tx.Commit();
    }

    private void SaveMessage(SqliteConnection db, SqliteTransaction tx, string chatId, MessageDto message)
    {
        if (message.ChatId != chatId || !EntityIds.IsMessageInChat(message.Id, chatId) || message.Sequence <= 0 ||
            message.ContentState is not ("stored" or "deleted")) return;
        Execute(db, tx, """
            INSERT INTO messages(id, chat_id, sequence, data) VALUES ($id, $chat, $sequence, $data)
            ON CONFLICT(id) DO UPDATE SET data=excluded.data
            """, ("$id", message.Id), ("$chat", chatId), ("$sequence", message.Sequence),
            ("$data", _key.Encrypt("message:" + message.Id, message)));
    }

    internal void SaveMembers(string chatId, List<MemberDto> items) => SaveChatSnapshot(chatId, "members", items);
    internal List<MemberDto> ReadMembers(string chatId) => ReadChatSnapshot<List<MemberDto>>(chatId, "members") ?? [];
    internal void SavePins(string chatId, List<PinDto> items) => SaveChatSnapshot(chatId, "pins", items);
    internal List<PinDto> ReadPins(string chatId) => ReadChatSnapshot<List<PinDto>>(chatId, "pins") ?? [];
    internal void ClearMembers(string chatId) => ClearChatSnapshot(chatId, "members");

    private void ClearChatSnapshot(string chatId, string column)
    {
        using var db = Open();
        Execute(db, null, $"UPDATE chats SET {column}=NULL WHERE id=$chat", ("$chat", chatId));
    }

    internal void UpdateSharedPin(string chatId, string id, MessageDto? message, bool onlyIfPresent = false)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var command = Command(db, tx, "SELECT pins FROM chats WHERE id=$chat", ("$chat", chatId));
        if (command.ExecuteScalar() is byte[] data)
        {
            var slot = "pins:" + chatId;
            var pins = _key.Decrypt<List<PinDto>>(slot, data);
            var removed = pins.RemoveAll(pin => pin.MessageId == id) > 0;
            var replace = message is { ContentState: "stored" } && (!onlyIfPresent || removed);
            if (replace)
                pins.Add(new PinDto { MessageId = id, Message = message });
            if (removed || replace)
                Execute(db, tx, "UPDATE chats SET pins=$pins WHERE id=$chat", ("$chat", chatId),
                    ("$pins", _key.Encrypt(slot, pins)));
        }
        tx.Commit();
    }

    private void SaveChatSnapshot<T>(string chatId, string column, T value)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        EnsureChat(db, tx, chatId);
        Execute(db, tx, $"UPDATE chats SET {column}=$value WHERE id=$chat", ("$chat", chatId),
            ("$value", _key.Encrypt(column + ":" + chatId, value)));
        tx.Commit();
    }

    private T? ReadChatSnapshot<T>(string chatId, string column) where T : class
    {
        using var db = Open();
        using var command = Command(db, null, $"SELECT {column} FROM chats WHERE id=$chat", ("$chat", chatId));
        return command.ExecuteScalar() is byte[] data ? _key.Decrypt<T>(column + ":" + chatId, data) : null;
    }

    internal void SaveActivity(string chatId, IReadOnlyList<GroupActivityItem> items)
    {
        if (items.Count == 0) return;
        using var db = Open();
        using var tx = db.BeginTransaction();
        EnsureChat(db, tx, chatId);
        foreach (var item in items)
            Execute(db, tx, "INSERT OR REPLACE INTO activity(chat_id, id, data) VALUES ($chat, $id, $data)",
                ("$chat", chatId), ("$id", item.Id), ("$data", _key.Encrypt($"activity:{chatId}:{item.Id}", item)));
        tx.Commit();
    }

    internal GroupActivityItem[] ReadActivity(string chatId, int limit, long? before)
    {
        using var db = Open();
        using var command = Command(db, null, """
            SELECT id, data FROM activity WHERE chat_id=$chat AND id<$before ORDER BY id DESC LIMIT $limit
            """, ("$chat", chatId), ("$before", before ?? long.MaxValue), ("$limit", limit));
        using var rows = command.ExecuteReader();
        var result = new List<GroupActivityItem>();
        while (rows.Read()) result.Add(_key.Decrypt<GroupActivityItem>($"activity:{chatId}:{rows.GetInt64(0)}", rows.GetFieldValue<byte[]>(1)));
        return result.ToArray();
    }

    private sealed record LocalPins(List<LocalPinStore.Entry> Entries, HashSet<string> ImportedAddresses);
    private const string LocalPinsSlot = "local-pins";

    internal List<LocalPinStore.Entry> ReadLocalPins()
    {
        using var db = Open();
        return ReadLocalPins(db, null).Entries;
    }

    internal void UpdateLocalPins(Func<List<LocalPinStore.Entry>, bool> change) =>
        ModifyLocalPins(pins => change(pins.Entries));

    internal void ImportLegacyPins(string userId, IReadOnlyList<string> verifiedAddresses)
    {
        ModifyLocalPins(pins =>
        {
            var changed = false;
            foreach (var address in verifiedAddresses)
            {
                changed |= ImportPins(pins, address, () => LocalPinStore.Load(address, userId));
                changed |= ImportPins(pins, "numeric:" + address, () => LocalPinStore.LoadConverted(address, userId));
            }
            return changed;
        });
    }

    private static bool ImportPins(LocalPins pins, string source, Func<List<LocalPinStore.Entry>?> load)
    {
        if (pins.ImportedAddresses.Contains(source)) return false;
        List<LocalPinStore.Entry>? entries;
        try { entries = load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
        if (entries == null) return false; // The converted file can arrive after the first sign-in.
        foreach (var entry in entries)
        {
            if (entry == null || !EntityIds.IsMessageInChat(entry.MessageId, entry.ChatId) ||
                pins.Entries.Any(p => p.ChatId == entry.ChatId && p.MessageId == entry.MessageId)) continue;
            pins.Entries.Add(entry);
        }
        pins.ImportedAddresses.Add(source);
        return true;
    }

    private void ModifyLocalPins(Func<LocalPins, bool> change)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var state = ReadLocalPins(db, tx);
        if (change(state))
            Execute(db, tx, "INSERT OR REPLACE INTO settings(id, data) VALUES ($id, $data)",
                ("$id", LocalPinsSlot), ("$data", _key.Encrypt(LocalPinsSlot, state)));
        tx.Commit();
    }

    private LocalPins ReadLocalPins(SqliteConnection db, SqliteTransaction? tx)
    {
        using var command = Command(db, tx, "SELECT data FROM settings WHERE id=$id", ("$id", LocalPinsSlot));
        return command.ExecuteScalar() is byte[] data ? _key.Decrypt<LocalPins>(LocalPinsSlot, data) : new([], []);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        try
        {
            db.Open();
            Execute(db, null, "PRAGMA synchronous=NORMAL");
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static void EnsureChat(SqliteConnection db, SqliteTransaction tx, string chatId) =>
        Execute(db, tx, "INSERT OR IGNORE INTO chats(id) VALUES ($chat)", ("$chat", chatId));

    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object Value)[] args)
    {
        using var command = Command(db, tx, sql, args);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object Value)[] args)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        return command;
    }
}
