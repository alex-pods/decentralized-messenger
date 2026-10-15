using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace MessengerBackend.Core;

/// <summary>Чаты и участники. Правила доступа/членства — здесь, используются и HTTP, и WS.</summary>
public static class Chats
{
    public const string DirectType = "direct";
    public const string GroupType = "group";

    public static ChatRow ById(IDbConnection c, long id) =>
        c.QueryFirstOrDefault<ChatRow>("SELECT * FROM chats WHERE id=@id", new { id })
        ?? throw new HttpException(404, "чат не найден");

    public static string RoleOf(IDbConnection c, long chatId, long userId) =>
        c.ExecuteScalar<string?>("SELECT role FROM chat_members WHERE chat_id=@c AND user_id=@u",
            new { c = chatId, u = userId }) ?? "";

    public static void RequireMember(IDbConnection c, long chatId, long userId)
    {
        if (RoleOf(c, chatId, userId) == "")
            throw new HttpException(403, "не участник чата");
    }

    public static void RequireManager(IDbConnection c, long chatId, long userId)
    {
        var role = RoleOf(c, chatId, userId);
        if (role is not ("owner" or "admin"))
            throw new HttpException(403, "требуется owner или admin");
    }

    public static List<MemberRow> MembersOf(IDbConnection c, long chatId) =>
        c.Query<MemberRow>(
            """
            SELECT m.chat_id AS Chat_Id, m.user_id AS User_Id, m.role AS Role, m.joined_at AS Joined_At,
                   m.last_read_message_id AS Last_Read_Message_Id, m.read_at AS Read_At,
                   u.tag AS Tag, u.display_name AS Display_Name, u.path_to_avatar_file AS Path_To_Avatar_File
            FROM chat_members m JOIN users u ON u.id = m.user_id
            WHERE m.chat_id=@c ORDER BY m.joined_at, m.user_id
            """, new { c = chatId }).ToList();

    /// <summary>Члены сразу нескольких чатов одним запросом (для списка чатов без N+1).</summary>
    public static Dictionary<long, List<MemberRow>> MembersOfAll(IDbConnection c, IReadOnlyCollection<long> chatIds) =>
        c.Query<MemberRow>(
            """
            SELECT m.chat_id AS Chat_Id, m.user_id AS User_Id, m.role AS Role, m.joined_at AS Joined_At,
                   m.last_read_message_id AS Last_Read_Message_Id, m.read_at AS Read_At,
                   u.tag AS Tag, u.display_name AS Display_Name, u.path_to_avatar_file AS Path_To_Avatar_File
            FROM chat_members m JOIN users u ON u.id = m.user_id
            WHERE m.chat_id IN @ids ORDER BY m.joined_at, m.user_id
            """, new { ids = chatIds })
            .GroupBy(m => m.Chat_Id)
            .ToDictionary(g => g.Key, g => g.ToList());

    public static long[] MemberIds(IDbConnection c, long chatId) =>
        c.Query<long>("SELECT user_id FROM chat_members WHERE chat_id=@c ORDER BY joined_at",
            new { c = chatId }).ToArray();

    /// <summary>Личный чат: максимум один на пару. Уникальность гарантирует БД
    /// (UNIQUE(user1,user2) в direct_chats); гонка одновременного создания с обеих сторон
    /// разрешается повторным поиском при нарушении констрейнта.</summary>
    public static (long ChatId, bool Created) GetOrCreateDirect(IDbConnection c, long me, long other)
    {
        var a = Math.Min(me, other);
        var b = Math.Max(me, other);
        var existing = c.ExecuteScalar<long?>(
            "SELECT chat_id FROM direct_chats WHERE user1_id=@a AND user2_id=@b", new { a, b });
        if (existing is { } e) return (e, false);
        try
        {
            var id = c.ExecuteScalar<long>(
                "INSERT INTO chats(type, title, created_at, created_by) VALUES('direct', NULL, @ca, @cb); " +
                "SELECT last_insert_rowid()", new { ca = Util.NowIso(), cb = me });
            c.Execute("INSERT INTO chat_members(chat_id, user_id, role, joined_at) VALUES(@c, @u, 'member', @j)",
                new { c = id, u = a, j = Util.NowIso() });
            c.Execute("INSERT INTO chat_members(chat_id, user_id, role, joined_at) VALUES(@c, @v, 'member', @j)",
                new { c = id, v = b, j = Util.NowIso() });
            c.Execute("INSERT INTO direct_chats(chat_id, user1_id, user2_id) VALUES(@c, @a, @b)", new { c = id, a, b });
            return (id, true);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE violation
        {
            var raced = c.ExecuteScalar<long?>(
                "SELECT chat_id FROM direct_chats WHERE user1_id=@a AND user2_id=@b", new { a, b });
            if (raced is { } r) return (r, false);
            throw;
        }
    }

    public static long CreateGroup(IDbConnection c, long owner, string title, IReadOnlyCollection<long> memberIds)
    {
        var id = c.ExecuteScalar<long>(
            "INSERT INTO chats(type, title, created_at, created_by) VALUES('group', @t, @ca, @cb); " +
            "SELECT last_insert_rowid()", new { t = title, ca = Util.NowIso(), cb = owner });
        c.Execute("INSERT INTO chat_members(chat_id, user_id, role, joined_at) VALUES(@c, @me, 'owner', @j)",
            new { c = id, me = owner, j = Util.NowIso() });
        foreach (var uid in memberIds)
            c.Execute("INSERT OR IGNORE INTO chat_members(chat_id, user_id, role, joined_at) VALUES(@c, @u, 'member', @j)",
                new { c = id, u = uid, j = Util.NowIso() });
        return id;
    }
}
