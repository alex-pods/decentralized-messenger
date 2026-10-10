namespace Messenger.Core.Models;

public class MessageReaction
{
    public long Id { get; set; }

    public long MessageId { get; set; }
    public Message Message { get; set; } = null!;

    public long UserId { get; set; }
    public User User { get; set; } = null!;

    public string Emoji { get; set; } = null!;   // ≤ 32
    public DateTime CreatedAt { get; set; }
}