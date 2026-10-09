namespace LocalBackend.Models;

public class ChatMember 
{
    public long ChatId { get; set; }
    public Chat Chat { get; set; } = null!;

    public long UserId { get; set; }
    public User User { get; set; } = null!;

    // "member", "admin" или "owner".
    public string Role { get; set; } = "member";

    public DateTime JoinedAt { get; set; }
}