namespace LocalBackend.Models;

/// <summary>Простое key-value хранилище: CurrentUserId, курсор синхронизации и т.п.
/// Токены и секреты лучше держать в защищённом хранилище ОС, а не здесь.</summary>
public class AppStateEntry
{
    public string Key { get; set; } = null!;
    public string Value { get; set; } = null!;
}