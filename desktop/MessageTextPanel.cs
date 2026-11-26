using Avalonia;
using Avalonia.Controls;

namespace MessengerDesktop;

/// <summary>Places message metadata after the last text line when it fits, otherwise below it.</summary>
public sealed class MessageTextPanel : Panel
{
    const double Gap = 12;
    double _textHeight;
    bool _inline;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2 || Children[0] is not TextBlock text) return base.MeasureOverride(availableSize);
        var metadata = Children[1];
        text.Measure(availableSize);
        metadata.Measure(availableSize);
        _textHeight = text.DesiredSize.Height;
        if (!metadata.IsVisible) return text.DesiredSize;
        var lastWidth = text.TextLayout.TextLines.LastOrDefault()?.WidthIncludingTrailingWhitespace ?? 0;
        _inline = lastWidth + Gap + metadata.DesiredSize.Width <= availableSize.Width;
        var width = _inline ? Math.Max(text.DesiredSize.Width, lastWidth + Gap + metadata.DesiredSize.Width)
            : Math.Max(text.DesiredSize.Width, metadata.DesiredSize.Width);
        return new Size(width, _textHeight + (_inline ? 0 : metadata.DesiredSize.Height + 3));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 2) return base.ArrangeOverride(finalSize);
        Children[0].Arrange(new Rect(0, 0, finalSize.Width, _textHeight));
        var metadata = Children[1];
        metadata.Arrange(new Rect(Math.Max(0, finalSize.Width - metadata.DesiredSize.Width),
            Math.Max(0, finalSize.Height - metadata.DesiredSize.Height), metadata.DesiredSize.Width, metadata.DesiredSize.Height));
        return finalSize;
    }
}
