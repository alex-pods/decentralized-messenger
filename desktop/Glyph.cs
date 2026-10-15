using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MessengerDesktop;

/// <summary>Stroked line icon; Data is laid out in a 24×24 box.</summary>
public sealed class Glyph : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Glyph, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Glyph>();

    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<Glyph, double>(nameof(StrokeWidth), 1.6);

    static Glyph() => AffectsRender<Glyph>(DataProperty, ForegroundProperty, StrokeWidthProperty);

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (Data is null || Foreground is null || size <= 0) return;
        var scale = size / 24;
        var transform = Matrix.CreateScale(scale, scale) *
                        Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        var pen = new Pen(Foreground, StrokeWidth / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(transform))
            context.DrawGeometry(null, pen, Data);
    }
}
