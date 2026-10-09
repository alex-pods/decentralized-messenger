namespace LocalBackend.Models;

/// <summary>
/// Диалог 1-на-1 с собеседником. Одна БД = один аккаунт, поэтому достаточно PeerUserId
/// (пара user1/user2 с сервера здесь не нужна).
/// </summary>
public class Chat
{
    /// <summary>Id чата на сервере (общий для обоих участников).</summary>
    public long Id { get; set; }
    public long PeerUserId { get; set; }
    public User Peer { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    // ── Денормализованные поля для быстрого списка чатов ──
    public DateTime? LastMessageAt { get; set; }
    public int UnreadCount { get; set; }

    // ── Локальные настройки / состояние UI ──
    public bool IsPinned { get; set; }
    public bool IsMuted { get; set; }
    public bool IsArchived { get; set; }
    public string? DraftText { get; set; }

    public List<Message> Messages { get; set; } = [];
}