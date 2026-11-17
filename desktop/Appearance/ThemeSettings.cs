using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace MessengerDesktop.Appearance;

public sealed record ThemeSettings(string Name, bool Dark, string Background, string Surface, string Text, string Accent)
{
    public static readonly ThemeSettings[] Presets =
    [
        new("Светлая", false, "#F5F6F8", "#FFFFFF", "#20272D", "#237A63"),
        new("Тёмная", true, "#171B20", "#20262D", "#E9EDF1", "#72C7AC"),
        new("Бумага", false, "#F0ECE4", "#FFFCF5", "#38352F", "#87613B"),
        new("Полночь", true, "#111925", "#1B2838", "#E4ECF6", "#80B6F0")
    ];

    public static ThemeSettings Current { get; private set; } = Presets[0];
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "messenger-desktop", "appearance.json");

    public static void Restore()
    {
        try
        {
            Apply(File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(FilePath)) ?? Presets[0]
                : Presets[0]);
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException or ArgumentException)
        {
            Apply(Presets[0]);
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Current));
        File.Move(temporary, FilePath, true);
    }

    public static void Apply(ThemeSettings theme)
    {
        var background = Color.Parse(theme.Background);
        var surface = Color.Parse(theme.Surface);
        var text = Color.Parse(theme.Text);
        var accent = Color.Parse(theme.Accent);
        var muted = Blend(text, surface, .60);
        var line = Blend(text, surface, .18);
        var hover = Blend(text, surface, .07);
        var bubble = Blend(accent, surface, .14);
        var app = Application.Current!;
        app.RequestedThemeVariant = theme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        void Set(Color color, params string[] keys)
        {
            foreach (var key in keys) app.Resources[key] = new SolidColorBrush(color);
        }
        Set(background, "ChassisBrush", "MainFill", "PanelFill", "RowHover");
        Set(surface, "RaisedBrush", "SidebarFill", "FieldBrush", "SheetFill", "ToastFill", "GlassFill", "ScreenInset", "BubbleTheirs");
        Set(text, "InkBrush", "OnFieldBrush", "LabelPrimary", "TitleText");
        Set(muted, "LabelBrush", "OnFieldMutedBrush", "LabelSecondary", "LabelTertiary");
        Set(line, "LineBrush", "SeparatorBrush", "FieldBorder", "GlassEdge", "PanelEdge", "RaisedEdge", "WellEdge");
        Set(hover, "WellBrush", "ElevatedFill", "HoverFill", "GlassFillHover", "GlassFillPressed", "FieldFill", "FieldFillHover", "AccentFillHover", "AccentFillPressed", "SelectionFill", "DialFill");
        Set(accent, "AccentBrush", "OnlineBrush", "SelectionEdge");
        Set(bubble, "BubbleMine", "AccentFill");
        Set(theme.Dark ? Color.Parse("#FF9696") : Color.Parse("#A52835"), "DangerBrush");
        Current = theme;
    }

    static Color Blend(Color foreground, Color background, double amount) => Color.FromRgb(
        (byte)(foreground.R * amount + background.R * (1 - amount)),
        (byte)(foreground.G * amount + background.G * (1 - amount)),
        (byte)(foreground.B * amount + background.B * (1 - amount)));
}
