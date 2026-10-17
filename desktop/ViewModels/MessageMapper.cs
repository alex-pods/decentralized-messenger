namespace MessengerDesktop;

/// <summary>Чистые преобразования DTO → элементы списка. Без сети и UI-потока.</summary>
public static class MessageMapper
{
    public static string Body(MessageDto m) => m.ContentState switch
    {
        "deleted" => "Сообщение удалено",
        "not_stored" when string.IsNullOrEmpty(m.Text) => "Текст не сохранён на сервере",
        _ => m.Text ?? ""
    };

    public static string FormatTime(string? iso)
    {
        if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var t))
            return t.ToLocalTime().ToString("HH:mm");
        return "";
    }

    public static string Trim(string text, int n) => text.Length <= n ? text : text[..n] + "…";

    public static string Preview(MessageDto m, long myId, Func<long, string> nameOf)
    {
        var who = m.SenderId == myId ? "Вы" : nameOf(m.SenderId);
        return $"{who}: {Body(m)}";
    }

    public static ChatItem MapChat(ChatDto c, long myId, Func<long, string> nameOf)
    {
        var title = c.Type == "group"
            ? (string.IsNullOrWhiteSpace(c.Title) ? "Группа" : c.Title!)
            : (c.OtherUser?.DisplayName ?? c.OtherUser?.Tag ?? "Личный чат");
        return new ChatItem
        {
            Id = c.Id,
            Type = c.Type,
            Title = title,
            Kind = c.Type == "group" ? "Группа" : "Личный чат",
            Preview = c.LastMessage is null ? "Нет сообщений" : Preview(c.LastMessage, myId, nameOf)
        };
    }

    public static MessageItem ToItem(
        MessageDto m, long myId, Func<long, string> nameOf, IReadOnlyDictionary<long, string> texts)
    {
        var reply = "";
        if (m.ReplyToId is { } rid)
            reply = texts.TryGetValue(rid, out var t) ? "↩ " + Trim(t, 80) : "↩ ответ";
        var meta = FormatTime(m.SentAt);
        if (m.EditedAt != null) meta += " · изменено";
        if (m.SenderId == myId && m.ReadAt != null) meta += " · прочитано";
        else if (m.SenderId == myId && m.DeliveredAt != null) meta += " · доставлено";
        var item = new MessageItem
        {
            Id = m.Id,
            SenderId = m.SenderId,
            SenderName = m.SenderId == myId ? "Вы" : nameOf(m.SenderId),
            Mine = m.SenderId == myId,
            MyId = myId,
            Text = Body(m),
            Meta = meta,
            Reply = reply,
            IsDeleted = m.ContentState == "deleted",
            IsNotStored = m.ContentState == "not_stored"
        };
        item.SetReactions(ReactionMap(m.Reactions));
        return item;
    }

    public static Dictionary<string, HashSet<long>> ReactionMap(List<ReactionDto>? list)
    {
        var map = new Dictionary<string, HashSet<long>>();
        if (list is null) return map;
        foreach (var r in list)
            if (!string.IsNullOrEmpty(r.Emoji) && r.UserIds is { Count: > 0 })
                map[r.Emoji] = r.UserIds.ToHashSet();
        return map;
    }
}
