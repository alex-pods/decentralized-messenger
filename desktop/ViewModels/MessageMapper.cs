namespace MessengerDesktop;

/// <summary>Чистые преобразования DTO → элементы списка. Без сети и UI-потока.</summary>
public static class MessageMapper
{
    public const double BubbleContentMaxWidth = 420;

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

    public static string FormatDateTime(string? iso)
    {
        if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var t))
            return t.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        return "";
    }

    public static string Trim(string text, int n)
    {
        text = OneLine(text);
        if (text.Length <= n) return text;
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var end = 0;
        while (elements.MoveNext() && elements.ElementIndex <= n) end = elements.ElementIndex;
        return text[..end] + "…";
    }

    internal static string? PreviewAuthor(long senderId, long myId, Func<long, string> nameOf)
    {
        var who = senderId == myId ? "Вы" : nameOf(senderId);
        return who == $"#{senderId}" ? null : who;
    }

    public static ChatItem MapChat(ChatDto c, long myId, Func<long, string> nameOf)
    {
        var title = c.Type == "group"
            ? (string.IsNullOrWhiteSpace(c.Title) ? "Группа" : c.Title!)
            : (c.OtherUser?.DisplayName ?? c.OtherUser?.Tag ?? "Личный чат");
        var chat = new ChatItem
        {
            Id = c.Id,
            Type = c.Type,
            Title = title,
            Tag = c.OtherUser?.Tag,
            AvatarUserId = c.Type == "direct" ? c.OtherUser?.Id : null,
            Kind = c.Type == "group" ? "Группа" : "Личный чат",
            LastMessageId = c.LastMessage?.Id ?? 0,
            LastMessageSequence = c.LastMessage?.Sequence ?? 0,
            LastMessageSentAt = DateTimeOffset.TryParse(c.LastMessage?.SentAt, out var lastSent) ? lastSent : null,
            CreatedAt = DateTimeOffset.TryParse(c.CreatedAt, out var created) ? created : DateTimeOffset.MinValue,
            Time = c.LastMessage is null ? "" : FormatTime(c.LastMessage.SentAt)
        };
        if (c.LastMessage is { } last)
            chat.SetPreview(PreviewAuthor(last.SenderId, myId, nameOf), Body(last));
        return chat;
    }

    public static MessageItem ToItem(MessageDto m, long myId, Func<long, string> nameOf)
    {
        var meta = FormatTime(m.SentAt);
        if (m.EditedAt != null) meta += " · изменено";
        if (m.SenderId == myId && m.ReadAt != null) meta += " · прочитано";
        else if (m.SenderId == myId && m.DeliveredAt != null) meta += " · доставлено";
        var item = new MessageItem
        {
            Id = m.Id,
            Sequence = m.Sequence,
            SentAt = DateTimeOffset.TryParse(m.SentAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var sentAt) ? sentAt : null,
            SenderId = m.SenderId,
            SenderName = m.SenderId == myId ? "Вы" : nameOf(m.SenderId),
            Mine = m.SenderId == myId,
            MyId = myId,
            Text = Body(m),
            Meta = meta,
            ReplyToId = m.ReplyToId,
            Forwarded = m.ForwardedFrom is { } f ? "↪ Переслано от " + f.AuthorName : "",
            ForwardedTip = m.ForwardedFrom is { } g ? "Отправлено " + FormatDateTime(g.OriginalSentAt) : "",
            IsDeleted = m.ContentState == "deleted",
            IsNotStored = m.ContentState == "not_stored"
        };
        item.Images = m.ContentState is "deleted" or "not_stored" ? [] :
            m.Attachments?.Where(a => a.IsImage).DistinctBy(a => a.Id).ToArray() ?? [];
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

    static string OneLine(string text) =>
        string.Join(' ', text.Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
