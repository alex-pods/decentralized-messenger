using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace MessengerDesktop.Appearance;

public sealed record ThemeSettings(string Name, bool Dark, string Background, string Surface, string Text, string Accent)
{
    public static readonly ThemeSettings[] Presets =
    [
        new("Светлая", false, "#F0F0E8", "#FFFFFF", "#29352F", "#33755C"),
        new("Тёмная", true, "#191C20", "#25292D", "#E6EAEE", "#88C7A6"),
        new("Бумага", false, "#F0ECE4", "#FFFCF5", "#38352F", "#87613B"),
        new("Полночь", true, "#111925", "#1B2838", "#E4ECF6", "#80B6F0")
    ];

    public static ThemeSettings Current { get; private set; } = Presets[0];
    public static event Action? Changed;
    internal static string FilePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "messenger-desktop", "appearance.json");

    public static void Restore()
    {
        try
        {
            Apply(File.Exists(FilePath)
                ? ReadFrom(FilePath)
                : Presets[0]);
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException or ArgumentException)
        {
            Apply(Presets[0]);
        }
    }

    public static void Save() => SaveTo(FilePath);

    internal static ThemeSettings ReadFrom(string path) =>
        JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(path)) ?? Presets[0];

    internal static void SaveTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Current));
        File.Move(temporary, path, true);
    }

    public static void Apply(ThemeSettings theme)
    {
        var background = Color.Parse(theme.Background);
        var surface = Color.Parse(theme.Surface);
        var text = Color.Parse(theme.Text);
        var accent = Color.Parse(theme.Accent);
        var muted = Blend(text, surface, .70);
        var line = Blend(text, surface, .18);
        var hover = Blend(text, surface, .07);
        var bubble = Blend(accent, surface, .14);
        var app = Application.Current!;
        app.RequestedThemeVariant = theme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        foreach (var fluent in app.Styles.OfType<FluentTheme>())
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                if (!fluent.Palettes.TryGetValue(variant, out var palette))
                    fluent.Palettes[variant] = palette = new ColorPaletteResources();
                palette.Accent = accent;
            }
        }
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
        Set(Blend(accent, surface, theme.Dark ? .26 : .18), "BubbleMine");
        Set(bubble, "AccentFill");
        Set(Blend(accent, surface, theme.Dark ? .16 : .08), "QuoteFill");
        Set(theme.Dark ? Color.Parse("#FF9696") : Color.Parse("#A52835"), "DangerBrush");
        Set(Contrast(accent, Colors.Black) > Contrast(accent, Colors.White) ? Colors.Black : Colors.White, "ActionTextBrush");
        Current = theme;
        Changed?.Invoke();
    }

    public static double Contrast(Color first, Color second)
    {
        static double Luminance(Color c)
        {
            static double Linear(byte channel) { var v = channel / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    static Color Blend(Color foreground, Color background, double amount) => Color.FromRgb(
        (byte)(foreground.R * amount + background.R * (1 - amount)),
        (byte)(foreground.G * amount + background.G * (1 - amount)),
        (byte)(foreground.B * amount + background.B * (1 - amount)));
}
