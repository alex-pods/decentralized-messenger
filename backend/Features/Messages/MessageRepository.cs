using System.Data;
using Dapper;
using MessengerBackend.Hubs;

namespace MessengerBackend.Core;

/// <summary>Сообщения: идемпотентная отправка, правка, удаление, реакции, прочтения,
/// пакетное построение DTO истории без N+1.</summary>
public static class MessageRepository
{
    public const int MaxTextLength = 4096;

    // ---------- чтение ----------

    public static MessageRow ById(IDbConnection c, long id) =>
        c.QueryFirstOrDefault<MessageRow>("SELECT * FROM messages WHERE id=@i", new { i = id })
        ?? throw new HttpException(404, "сообщение не найдено");

    /// <summary>Валидация reply_to: сообщение существует и принадлежит тому же чату
    /// (ответ на заглушку удалённого/legacy-not_stored разрешён — ссылка ведёт на заглушку).</summary>
    public static void ValidateReply(IDbConnection c, long chatId, long replyToId)
    {
        var target = c.QueryFirstOrDefault<MessageRow>("SELECT * FROM messages WHERE id=@i", new { i = replyToId });
        if (target is null)
            throw new HttpException(404, "reply_to_id: сообщение не найдено");
        if (target.Chat_Id != chatId)
            throw new HttpException(400, "reply_to_id: сообщение из другого чата");
    }

    /// <summary>Неизменяемый отпечаток запроса создания: правки сообщения его не меняют,
    /// поэтому повтор исходного запроса остаётся идемпотентным и после PATCH.</summary>
    public static string RefPayload(long chatId, string text, long? replyToId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{chatId}|{replyToId ?? 0}|{text}"))).ToLowerInvariant();

    /// <summary>Идемпотентная отправка. client_ref уникален в рамках отправителя:
    /// повтор с тем же отпечатком исходного запроса (chat, text, reply_to_id) возвращает
    /// прежний message_id — независимо от последующих правок и удаления; иная пара
    /// (client_ref, содержимое) — 409 client_ref_conflict.</summary>
    public static (long MessageId, bool Created, MessageRow Row) Send(
        IDbConnection c, long chatId, long senderId, string text, long? replyToId, string? clientRef)
    {
        if (clientRef is not null)
        {
            var payload = RefPayload(chatId, text, replyToId);
            var prev = c.QueryFirstOrDefault<MessageRow>(
                "SELECT * FROM messages WHERE sender_id=@s AND client_ref=@r", new { s = senderId, r = clientRef });
            if (prev is not null)
            {
                if ((prev.Client_Ref_Payload is not null && Infrastructure.StorageEncryption.ReadFingerprint(prev.Client_Ref_Payload) == payload) || (prev.Client_Ref_Payload is null &&
                    prev.Chat_Id == chatId && prev.Reply_To_Id == replyToId &&
                    (prev.Deleted_At is not null || prev.Text == text)))
                    return (prev.Id, false, prev); // legacy-строки без отпечатка сверяем по содержимому
                throw new HttpException(409,
                    "client_ref уже использован с другим содержимым", "client_ref_conflict");
            }
            var id = c.ExecuteScalar<long>(
                "INSERT INTO messages(chat_id, sender_id, reply_to_id, text, sent_at, client_ref, client_ref_payload, storage_version) " +
                "VALUES(@c, @s, @r, @t, @sa, @ref, @fp, 1); SELECT last_insert_rowid()",
                new { c = chatId, s = senderId, r = replyToId, t = Infrastructure.StorageEncryption.Protect(text), sa = Util.NowIso(), @ref = clientRef, fp = Infrastructure.StorageEncryption.ProtectFingerprint(payload) });
            return (id, true, ById(c, id));
        }
        var idNoRef = c.ExecuteScalar<long>(
            "INSERT INTO messages(chat_id, sender_id, reply_to_id, text, sent_at, storage_version) " +
            "VALUES(@c, @s, @r, @t, @sa, 1); SELECT last_insert_rowid()",
            new { c = chatId, s = senderId, r = replyToId, t = Infrastructure.StorageEncryption.Protect(text), sa = Util.NowIso() });
        return (idNoRef, true, ById(c, idNoRef));
    }

    /// <summary>Курсор прочтения получателя (не уменьшается).</summary>
    public static void MarkRead(IDbConnection c, long chatId, long userId, long messageId)
    {
        c.Execute("""
            UPDATE chat_members SET last_read_message_id=@m, read_at=@ra
            WHERE chat_id=@c AND user_id=@u
              AND (last_read_message_id IS NULL OR last_read_message_id < @m)
            """, new { m = messageId, ra = Util.NowIso(), c = chatId, u = userId });
    }

    /// <summary>read_at (агрегат): прочитано ВСЕМИ текущими участниками, кроме отправителя.</summary>
    public static string? AggregateReadAt(long messageId, long senderId, List<MemberRow> members)
    {
        var others = members.Where(m => m.User_Id != senderId).ToList();
        if (others.Count == 0) return null;
        if (others.Any(m => (m.Last_Read_Message_Id ?? 0) < messageId)) return null;
        return others.Where(m => m.Read_At is not null).Max(m => m.Read_At);
    }

    /// <summary>Удаление для всех: пометка + отзыв вложений (строки и файлы) + снятие реакций.
    /// Событие шлём уже после фиксации в БД; сбой рассылки не теряет изменение.</summary>
    public static List<string> DeleteForEveryone(IDbConnection c, long messageId)
    {
        return Infrastructure.SchemaMigrator.Transaction(c, () =>
        {
            var files = c.Query<string>(
                "SELECT path_to_file FROM attachments WHERE message_id=@m", new { m = messageId }).ToList();
            c.Execute("UPDATE messages SET deleted_at=@d, text='', storage_version=0 WHERE id=@i AND deleted_at IS NULL",
                new { d = Util.NowIso(), i = messageId });
            c.Execute("DELETE FROM attachments WHERE message_id=@m", new { m = messageId });
            c.Execute("DELETE FROM message_reactions WHERE message_id=@m", new { m = messageId });
            return files;
        });
    }

    // ---------- пакетное построение DTO ----------

    public static List<AttachmentRow> AttachmentsOf(IDbConnection c, IReadOnlyCollection<long> messageIds) =>
        messageIds.Count == 0 ? [] :
        c.Query<AttachmentRow>("SELECT * FROM attachments WHERE message_id IN @ids ORDER BY id",
            new { ids = messageIds }).ToList();

    public static List<ReactionRow> ReactionsOf(IDbConnection c, IReadOnlyCollection<long> messageIds) =>
        messageIds.Count == 0 ? [] :
        c.Query<ReactionRow>("SELECT * FROM message_reactions WHERE message_id IN @ids ORDER BY id",
            new { ids = messageIds }).ToList();

    /// <summary>content_state: stored — обычное сообщение; deleted — удалено для всех (заглушка);
    /// not_stored — legacy-запись без сохранённого текста (до отказа от save_history).</summary>
    public static string StateOf(MessageRow m) =>
        m.Deleted_At is not null ? "deleted" : m.Text.Length == 0 ? "not_stored" : "stored";

    public static List<MessageOut> ToDtoList(IDbConnection c, IEnumerable<MessageRow> rows, List<MemberRow> members)
    {
        var list = rows.ToList();
        var ids = list.Select(m => m.Id).ToList();
        var atts = AttachmentsOf(c, ids).GroupBy(a => a.Message_Id)
            .ToDictionary(g => g.Key, g => g.ToList());
        var reacts = ReactionsOf(c, ids).GroupBy(r => r.Message_Id)
            .ToDictionary(g => g.Key, g => g
                .GroupBy(r => r.Emoji)
                .Select(g2 => new ReactionOut(g2.Key, g2.Select(r => r.User_Id).ToList()))
                .ToList());
        var result = new List<MessageOut>(list.Count);
        foreach (var m in list)
        {
            var deleted = m.Deleted_At is not null;
            var notStored = !deleted && m.Text.Length == 0;
            result.Add(new MessageOut(m.Id, m.Chat_Id, m.Sender_Id, m.Reply_To_Id,
                deleted || notStored ? null : m.Text,
                StateOf(m),
                m.Sent_At, m.Delivered_At, m.Edited_At,
                AggregateReadAt(m.Id, m.Sender_Id, members.Where(member => member.Chat_Id == m.Chat_Id).ToList()), m.Deleted_At,
                deleted ? [] : atts.GetValueOrDefault(m.Id)?.Select(a =>
                    new AttachmentOut(a.Id, a.Message_Id, a.File_Name, a.File_Type, a.File_Size_Bytes)).ToList() ?? [],
                deleted ? [] : reacts.GetValueOrDefault(m.Id) ?? []));
        }
        return result;
    }

    public static MessageOut ToDto(IDbConnection c, MessageRow m, List<MemberRow>? members = null) =>
        ToDtoList(c, [m], members ?? ChatRepository.MembersOf(c, m.Chat_Id))[0];

    /// <summary>Последние видимые сообщения набора чатов одним запросом (для GET /chats).</summary>
    public static Dictionary<long, MessageRow> LastVisibleMessagesOf(
        IDbConnection c, IReadOnlyCollection<long> chatIds, long viewerId)
    {
        if (chatIds.Count == 0) return [];
        var rows = c.Query<MessageRow>(
            """
            SELECT m.* FROM messages m
            JOIN (SELECT chat_id, MAX(id) AS mid FROM messages
                  WHERE chat_id IN @ids
                    AND id NOT IN (SELECT message_id FROM message_hidden WHERE user_id=@u)
                  GROUP BY chat_id) last ON m.id = last.mid
            """, new { ids = chatIds, u = viewerId });
        return rows.ToDictionary(r => r.Chat_Id, r => r);
    }
}
