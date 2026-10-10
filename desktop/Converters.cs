using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace MessengerDesktop;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class EqConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MineAlignConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Stable colourful avatar fill for an id (long) or a name (string).</summary>
public sealed class AvatarBrushConverter : IValueConverter
{
    static readonly IBrush[] Palette =
    [
        Gradient("#5EE7FF", "#3B82F6"),
        Gradient("#E29BFF", "#9333EA"),
        Gradient("#FF8FC7", "#E11D74"),
        Gradient("#FFD27A", "#F97316"),
        Gradient("#7CF2B0", "#10B981"),
        Gradient("#7FF0E6", "#0EA5A4"),
        Gradient("#A5A3FF", "#6366F1"),
        Gradient("#FFA39A", "#EF4444"),
    ];

    static IBrush Gradient(string from, string to) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(from), 0), new GradientStop(Color.Parse(to), 1) },
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        long key = value switch
        {
            long l => l,
            int i => i,
            string s => s.Aggregate(17L, (h, c) => unchecked(h * 31 + c)),
            _ => 0,
        };
        return Palette[(int)(((key % Palette.Length) + Palette.Length) % Palette.Length)];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        foreach (var ch in value as string ?? "")
            if (char.IsLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
        return "?";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
