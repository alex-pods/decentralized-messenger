namespace LocalBackend.Models;

/// <summary>Кэш профилей пользователей (в том числе своего). Пароля здесь нет.</summary>
public class User
{
    /// <summary>Id пользователя на сервере (локально не генерируется).</summary>
    public long Id { get; set; }
    public string Tag { get; set; } = null!;          // ≤ 16
    public string DisplayName { get; set; } = null!;  // ≤ 32
    public string? Bio { get; set; }

    /// <summary>Путь к аватару на сервере (path_to_avatar_file).</summary>
    public string? AvatarRemotePath { get; set; }
    /// <summary>Скачанная локальная копия аватара.</summary>
    public string? AvatarLocalPath { get; set; }

    public DateTime JoinDate { get; set; }
    public DateTime? LastSeenAt { get; set; }

    /// <summary>Когда профиль последний раз обновлялся с сервера (для инвалидации кэша).</summary>
    public DateTime UpdatedAt { get; set; }
}