using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
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

    // ---- палитра ----
    const string Yellow = "#FFC83D";
    const string Amber = "#F59E0B";
    const string Brown = "#8A5A00";
    const string Ink = "#5B3A00";
    const string Water = "#5CC8FF";

    // ---- общие формы (сетка 24x24) ----
    const string Face = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0 -20 0Z";
    const string EyesHigh = "M7.8 9.5a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0Z M13.8 9.5a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0Z";
    const string EyesLow = "M7.8 11a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0Z M13.8 11a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0Z";
    const string OpenMouth = "M18 13a6 6 0 0 1-6 5 6 6 0 0 1-6-5h12Z";
    const string Smile = "M8.5 16c1 1.4 2.2 2 3.5 2s2.5-.6 3.5-2";
    const string Frown = "M8.5 17.5c1-1.4 2.2-2 3.5-2s2.5.6 3.5 2";

    void Rebuild()
    {
        // FE0F (variation selector) у сердца отбрасываем: сервер может вернуть его с ним и без него
        var key = Emoji?.Replace("\uFE0F", "");
        Child = key switch
        {
            null or "" => null,
            "\U0001F44D" => Thumb(down: false),   // 👍
            "\U0001F44E" => Thumb(down: true),    // 👎
            "\u2764" => Heart(),                  // ❤
            "\U0001F602" => Laugh(),              // 😂
            "\U0001F62E" => Wow(),                // 😮
            "\U0001F622" => Cry(),                // 😢
            "\U0001F621" => Angry(),              // 😡
            "\U0001F60D" => LoveEyes(),           // 😍
            "\U0001F60E" => Cool(),               // 😎
            "\U0001F525" => Fire(),               // 🔥
            "\U0001F389" => Party(),              // 🎉
            "\U0001F440" => Eyes(),               // 👀
            // эмодзи, которого нет в наборе (например, пришло от другого клиента): показываем как текст
            _ => new TextBlock { Text = Emoji, FontSize = 14, VerticalAlignment = VerticalAlignment.Center }
        };
    }

    // ---------- иконки ----------

    static Control Thumb(bool down)
    {
        const string shape = "M15 5.88 14 10h5.83a2 2 0 0 1 1.92 2.56l-2.33 8A2 2 0 0 1 17.5 22H4a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2h2.76a2 2 0 0 0 1.79-1.11L12 2a3.13 3.13 0 0 1 3 3.88Z";
        const string cuff = "M7 10v12H4a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2h3Z";
        return Icon(down,
            P(shape, fill: Yellow),
            P(cuff, fill: Amber),
            P(shape + " M7 10v12", stroke: Brown, thickness: 1.5));
    }

    static Control Heart() => Icon(false,
        P("M19 14c1.49-1.46 3-3.21 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.76 0-3 .5-4.5 2-1.5-1.5-2.74-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4.05 3 5.5l7 7Z",
          fill: "#FF4D6D"));

    static Control Laugh() => Icon(false,
        P(Face, fill: Yellow),
        P("M3.2 11.5a1.4 1.4 0 1 0 2.8 0a1.4 1.4 0 1 0 -2.8 0Z M18 11.5a1.4 1.4 0 1 0 2.8 0a1.4 1.4 0 1 0 -2.8 0Z", fill: Water),
        P(EyesHigh, fill: Ink),
        P(OpenMouth, fill: Ink));

    static Control Wow() => Icon(false,
        P(Face, fill: Yellow),
        P(EyesHigh, fill: Ink),
        P("M9.8 17a2.2 3 0 1 0 4.4 0a2.2 3 0 1 0 -4.4 0Z", fill: Ink));

    static Control Cry() => Icon(false,
        P(Face, fill: Yellow),
        P(EyesHigh, fill: Ink),
        P(Frown, stroke: Ink, thickness: 1.5),
        P("M7 12.5c.9 1.2 1.4 2 1.4 2.8a1.4 1.4 0 0 1-2.8 0c0-.8.5-1.6 1.4-2.8Z", fill: Water));

    static Control Angry() => Icon(false,
        P(Face, fill: "#FF6B4A"),
        P(EyesLow, fill: Ink),
        P("M6.5 7l4.3 2 M17.5 7l-4.3 2", stroke: Ink, thickness: 1.6),
        P(Frown, stroke: Ink, thickness: 1.5));

    static Control LoveEyes() => Icon(false,
        P(Face, fill: Yellow),
        P("M9 12.8l-2.5-2.6a1.55 1.55 0 0 1 2.5-1.8 1.55 1.55 0 0 1 2.5 1.8Z M15 12.8l-2.5-2.6a1.55 1.55 0 0 1 2.5-1.8 1.55 1.55 0 0 1 2.5 1.8Z", fill: "#FF3B5C"),
        P("M8.5 15.5c1 1.6 2.2 2.4 3.5 2.4s2.5-.8 3.5-2.4", stroke: Ink, thickness: 1.5));

    static Control Cool() => Icon(false,
        P(Face, fill: Yellow),
        P("M6.8 8.5h2.9a1.8 1.8 0 0 1 1.8 1.8v.4a1.8 1.8 0 0 1-1.8 1.8H6.8A1.8 1.8 0 0 1 5 10.7v-.4a1.8 1.8 0 0 1 1.8-1.8Z " +
          "M14.3 8.5h2.9a1.8 1.8 0 0 1 1.8 1.8v.4a1.8 1.8 0 0 1-1.8 1.8h-2.9a1.8 1.8 0 0 1-1.8-1.8v-.4a1.8 1.8 0 0 1 1.8-1.8Z", fill: "#2B2112"),
        P("M11.5 9.6h1", stroke: "#2B2112", thickness: 1.4),
        P(Smile, stroke: Ink, thickness: 1.5));

    static Control Fire() => Icon(false,
        P("M8.5 14.5A2.5 2.5 0 0 0 11 12c0-1.38-.5-2-1-3-1.072-2.143-.224-4.054 2-6 .5 2.5 2 4.9 4 6.5 2 1.6 3 3.5 3 5.5a7 7 0 1 1-14 0c0-1.153.433-2.294 1-3a2.5 2.5 0 0 0 2.5 2.5z",
          fill: "#FF7A1A"),
        P("M12 21a3 3 0 0 0 3-3c0-1.2-.8-2-1.5-3-.6-.9-1-1.6-1.5-2.5-.5 1-1.2 1.7-1.8 2.5C9.5 16 9 16.8 9 18a3 3 0 0 0 3 3Z",
          fill: "#FFD23F"));

    static Control Party() => Icon(false,
        P("M3 21l5-13 8 8Z", fill: Yellow, stroke: Brown, thickness: 1.2),
        P("M13.7 6a1.3 1.3 0 1 0 2.6 0a1.3 1.3 0 1 0 -2.6 0Z", fill: "#FF4D8D"),
        P("M18.8 11a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0Z", fill: "#4DA3FF"),
        P("M9.8 3.5a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0Z", fill: "#34D399"),
        P("M18 4l-1.5 2", stroke: "#A78BFA", thickness: 1.5),
        P("M21 15.5l-2.2-.9", stroke: "#FF8A3D", thickness: 1.5));

    static Control Eyes() => Icon(false,
        P("M4 12a4 6.5 0 1 0 8 0a4 6.5 0 1 0 -8 0Z M12 12a4 6.5 0 1 0 8 0a4 6.5 0 1 0 -8 0Z", fill: "#FFFFFF", stroke: "#8A94A6", thickness: 1),
        P("M6.8 13.5a2.2 2.2 0 1 0 4.4 0a2.2 2.2 0 1 0 -4.4 0Z M14.8 13.5a2.2 2.2 0 1 0 4.4 0a2.2 2.2 0 1 0 -4.4 0Z", fill: "#2B2B3A"));

    // ---------- помощники ----------

    static Control Icon(bool flipY, params AvaloniaPath[] shapes)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        if (flipY)
        {
            canvas.RenderTransform = new ScaleTransform(1, -1);
            canvas.RenderTransformOrigin = RelativePoint.Center;
        }
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
