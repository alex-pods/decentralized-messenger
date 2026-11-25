using System.Text.Json;
using System.Text.Json.Serialization;

namespace MessengerDesktop.Settings;

/// <summary>Чем отправляется сообщение из поля ввода.</summary>
public enum SendMode
{
    /// <summary>Ctrl+Enter / ⌘+Enter отправляет, Enter — новая строка (как было раньше).</summary>
    CtrlEnter,
    /// <summary>Enter отправляет, Shift+Enter — новая строка.</summary>
    Enter
}

/// <summary>Что делает двойной клик по сообщению.</summary>
public enum DoubleClickAction
{
    None,
    React,
    Reply
}

/// <summary>Локальные настройки приложения (хранятся на этом компьютере, на сервер не уходят).</summary>
public sealed record UserPreferences
{
    public SendMode SendMode { get; init; } = SendMode.CtrlEnter;
    public DoubleClickAction DoubleClick { get; init; } = DoubleClickAction.None;
    public string DoubleClickReaction { get; init; } = ReactionCatalog.Main[0];
}

public static class AppPreferences
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static UserPreferences Current { get; private set; } = new();

    /// <summary>Вызывается после любого изменения настроек.</summary>
    public static event Action? Changed;

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "messenger-desktop", "settings.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = Normalize(JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(FilePath), Json) ?? new());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            Current = new();
        }
    }

    public static void Update(Func<UserPreferences, UserPreferences> change)
    {
        Current = Normalize(change(Current));
        Changed?.Invoke();
        try { Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Настройка продолжит работать до закрытия приложения, даже если файл не записался.
        }
    }

    static void Save()
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Current, Json));
        File.Move(temporary, path, true);
    }

    static UserPreferences Normalize(UserPreferences value)
    {
        var known = ReactionCatalog.Main.Concat(ReactionCatalog.More);
        return known.Contains(value.DoubleClickReaction) ? value : value with { DoubleClickReaction = ReactionCatalog.Main[0] };
    }
}
