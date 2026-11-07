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

/// <summary>Stable avatar fill for an id or a name. Muted instrument tones, one orange.</summary>
public sealed class AvatarBrushConverter : IValueConverter
{
    static readonly IBrush[] Palette =
    [
        Solid("#314048"),
        Solid("#5C6A72"),
        Solid("#8A4E3A"),
        Solid("#C4542A"),
        Solid("#3E515C"),
        Solid("#6E6248"),
        Solid("#4A5560"),
        Solid("#7A8490"),
    ];

    static IBrush Solid(string hex) => new SolidColorBrush(Color.Parse(hex));

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
