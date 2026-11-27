namespace MessengerDesktop;

/// <summary>Supported reactions. Each entry has a bundled Twemoji asset and an accessible label.</summary>
public static class ReactionCatalog
{
    public static string Label(string emoji) => emoji.Replace("\uFE0F", "") switch
    {
        "👍" => "Нравится", "👎" => "Не нравится", "❤" => "Сердце", "😂" => "Смех",
        "😮" => "Удивление", "😢" => "Грусть", "😡" => "Злость", "😍" => "Восторг",
        "🔥" => "Огонь", "🎉" => "Праздник", "😎" => "Круто", "👀" => "Смотрю", _ => emoji
    };
    /// <summary>Основные: всегда видны в панели.</summary>
    public static readonly IReadOnlyList<string> Main = new[]
    {
        "\U0001F44D",   // 👍
        "\u2764\uFE0F", // ❤️
        "\U0001F602",   // 😂
    };

    /// <summary>Остальные: раскрываются по кнопке «+».</summary>
    public static readonly IReadOnlyList<string> More = new[]
    {
        "\U0001F44E", // 👎
        "\U0001F62E", // 😮
        "\U0001F622", // 😢
        "\U0001F621", // 😡
        "\U0001F60D", // 😍
        "\U0001F525", // 🔥
        "\U0001F389", // 🎉
        "\U0001F60E", // 😎
        "\U0001F440", // 👀
    };
}
