using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MessengerDesktop;

/// <summary>Bundled Twemoji artwork: identical reactions on every platform, without network requests.</summary>
public sealed class ReactionIcon : Decorator
{
    public static readonly StyledProperty<string> EmojiProperty =
        AvaloniaProperty.Register<ReactionIcon, string>(nameof(Emoji), "");
    // Twelve small bitmaps shared by all chips for the application lifetime.
    static readonly Dictionary<string, Bitmap> Images = new();
    public string Emoji { get => GetValue(EmojiProperty); set => SetValue(EmojiProperty, value); }
    static ReactionIcon() => EmojiProperty.Changed.AddClassHandler<ReactionIcon>((icon, _) => icon.Rebuild());

    void Rebuild()
    {
        var normalized = Emoji.Replace("\uFE0F", "");
        if (ReactionCatalog.Main.Concat(ReactionCatalog.More).Any(e => e.Replace("\uFE0F", "") == normalized))
        {
            if (!Images.TryGetValue(normalized, out var bitmap))
            {
                var code = string.Join("-", normalized.EnumerateRunes().Select(r => r.Value.ToString("x")));
                using var stream = AssetLoader.Open(new Uri($"avares://MessengerDesktop/Assets/Twemoji/{code}.png"));
                Images[normalized] = bitmap = new Bitmap(stream);
            }
            Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        }
        else
            Child = new TextBlock { Text = Emoji, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center };
    }
}
