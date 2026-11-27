namespace MessengerDesktop;
public partial class ShellViewModel
{
    void UpdateReplyPreviews()
    {
        var quotes = Messages.Where(message => message.HasReply).ToArray();
        if (quotes.Length == 0) return;
        var byId = Messages.ToDictionary(message => message.Id);
        foreach (var quote in quotes)
        {
            var original = byId.GetValueOrDefault(quote.ReplyToId!.Value);
            quote.ReplyAuthor = original == null ? "Ответ на сообщение"
                : original.SenderName == $"#{original.SenderId}" ? "Участник" : original.SenderName;
            quote.Reply = original == null ? "Перейти к сообщению" : MessageMapper.Trim(original.Text, 50);
        }
    }

    internal static void GroupTimeline(IEnumerable<TimelineItem> items)
    {
        TimelineItem? previousItem = null;
        MessageItem? previous = null;
        foreach (var item in items)
        {
            item.StartsDay = item.SentAt != null && previousItem?.SentAt?.LocalDateTime.Date != item.SentAt?.LocalDateTime.Date;
            previousItem = item;
            if (item is not MessageItem message)
            {
                if (previous != null) previous.EndsMinute = true;
                previous = null;
                continue;
            }
            message.StartsRun = previous == null || message.StartsDay || previous.SenderId != message.SenderId ||
                message.SentAt == null || previous.SentAt == null || message.SentAt - previous.SentAt > TimeSpan.FromMinutes(5);
            if (previous != null)
                previous.EndsMinute = message.StartsRun || message.SentAt?.UtcTicks / TimeSpan.TicksPerMinute !=
                    previous.SentAt?.UtcTicks / TimeSpan.TicksPerMinute;
            previous = message;
        }
        if (previous != null) previous.EndsMinute = true;
    }
}
