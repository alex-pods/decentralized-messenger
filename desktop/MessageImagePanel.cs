using Avalonia;
using Avalonia.Controls;

namespace MessengerDesktop;

/// <summary>A bounded album: one large photo, a pair, or a mosaic of up to four.</summary>
public sealed class MessageImagePanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Min(320, availableSize.Width);
        var height = width * (Children.Count switch { 1 => .75, 2 => .65, _ => .9 });
        for (var i = 0; i < Children.Count; i++)
            Children[i].Measure(Cell(i, width, height).Size);
        return Children.Count == 0 ? default : new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < Children.Count; i++)
            Children[i].Arrange(Cell(i, finalSize.Width, finalSize.Height));
        return finalSize;
    }

    Rect Cell(int index, double width, double height)
    {
        const double gap = 3;
        var halfWidth = Math.Max(0, Math.Floor((width - gap) / 2));
        var halfHeight = Math.Max(0, Math.Floor((height - gap) / 2));
        var rightWidth = Math.Max(0, width - halfWidth - gap);
        var bottomHeight = Math.Max(0, height - halfHeight - gap);
        return Children.Count switch
        {
            1 => new Rect(0, 0, width, height),
            2 => new Rect(index * (halfWidth + gap), 0, index == 0 ? halfWidth : rightWidth, height),
            3 when index == 0 => new Rect(0, 0, width, halfHeight),
            3 => new Rect((index - 1) * (halfWidth + gap), halfHeight + gap,
                index == 1 ? halfWidth : rightWidth, bottomHeight),
            _ => new Rect(index % 2 * (halfWidth + gap), index / 2 * (halfHeight + gap),
                index % 2 == 0 ? halfWidth : rightWidth, index < 2 ? halfHeight : bottomHeight)
        };
    }
}
