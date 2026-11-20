namespace MessengerDesktop;
public partial class ShellViewModel
{
    void UpdateMessageGroups() => GroupMessages(Messages);

    internal static void GroupMessages(IEnumerable<MessageItem> messages)
    {
        MessageItem? previous = null;
        foreach (var message in messages)
        {
            message.StartsDay = message.SentAt != null && (previous == null || previous.SentAt?.LocalDateTime.Date != message.SentAt?.LocalDateTime.Date);
            message.StartsRun = previous == null || message.StartsDay || previous.SenderId != message.SenderId ||
                message.SentAt == null || previous.SentAt == null || message.SentAt - previous.SentAt > TimeSpan.FromMinutes(5);
            previous = message;
        }
    }
}
