using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace MessengerDesktop;

/// <summary>Цветная векторная иконка реакции. Рисуется кодом и не зависит от эмодзи-шрифтов системы.</summary>
public sealed class ReactionIcon : Decorator
{
    public static readonly StyledProperty<string?> EmojiProperty =
        AvaloniaProperty.Register<ReactionIcon, string?>(nameof(Emoji));

    static ReactionIcon() =>
        EmojiProperty.Changed.AddClassHandler<ReactionIcon>((icon, _) => icon.Rebuild());

    public string? Emoji
    {
        get => GetValue(EmojiProperty);
        set => SetValue(EmojiProperty, value);
    }

    const string ThumbShape = "M15 5.88 14 10h5.83a2 2 0 0 1 1.92 2.56l-2.33 8A2 2 0 0 1 17.5 22H4a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2h2.76a2 2 0 0 0 1.79-1.11L12 2a3.13 3.13 0 0 1 3 3.88Z";
    const string ThumbCuff = "M7 10v12H4a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2h3Z";
    const string HeartShape = "M19 14c1.49-1.46 3-3.21 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.76 0-3 .5-4.5 2-1.5-1.5-2.74-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4.05 3 5.5l7 7Z";
    const string Face = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0Z";
    const string Mouth = "M18 13a6 6 0 0 1-6 5 6 6 0 0 1-6-5h12Z";
    const string Eyes = "M7.8 9.5a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0Z M13.8 9.5a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0Z";
    const string Tears = "M3.2 11.5a1.4 1.4 0 1 0 2.8 0a1.4 1.4 0 1 0 -2.8 0Z M18 11.5a1.4 1.4 0 1 0 2.8 0a1.4 1.4 0 1 0 -2.8 0Z";

    void Rebuild()
    {
        // FE0F (variation selector) у сердца отбрасываем: сервер может вернуть его с ним и без него
        var key = Emoji?.Replace("\uFE0F", "");
        Child = key switch
        {
            null or "" => null,
            "\U0001F44D" => Icon(                                   // 👍
                P(ThumbShape, fill: "#FFC83D"),
                P(ThumbCuff, fill: "#F59E0B"),
                P(ThumbShape + " M7 10v12", stroke: "#8A5A00", thickness: 1.5)),
            "\u2764" => Icon(P(HeartShape, fill: "#FF4D6D")),       // ❤
            "\U0001F602" => Icon(                                   // 😂
                P(Face, fill: "#FFC83D"),
                P(Tears, fill: "#5CC8FF"),
                P(Eyes, fill: "#5B3A00"),
                P(Mouth, fill: "#5B3A00")),
            _ => new TextBlock { Text = Emoji, FontSize = 14, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
        };
    }

    static Control Icon(params AvaloniaPath[] shapes)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        foreach (var s in shapes) canvas.Children.Add(s);
        return new Viewbox { Stretch = Stretch.Uniform, Child = canvas };
    }

    static AvaloniaPath P(string data, string? fill = null, string? stroke = null, double thickness = 0) => new()
    {
        Data = Geometry.Parse(data),
        Fill = fill is null ? null : Brush.Parse(fill),
        Stroke = stroke is null ? null : Brush.Parse(stroke),
        StrokeThickness = thickness,
        StrokeJoin = PenLineJoin.Round,
        StrokeLineCap = PenLineCap.Round
    };
}
