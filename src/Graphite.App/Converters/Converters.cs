using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Graphite.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>count == 0 -> Visible (used for the start page); Invert flips it.</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool zero = value is int i && i == 0;
        if (Invert) zero = !zero;
        return zero ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type t, object p, CultureInfo c)
    {
        bool isNull = value is null;
        if (Invert) isNull = !isNull;
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString((string)value));
            brush.Freeze();
            return brush;
        }
        catch { return Brushes.Gray; }
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Two-way binding between an enum property and a RadioButton (parameter = enum member name).</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        value?.ToString() == parameter?.ToString();
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo c) =>
        value is true ? Enum.Parse(targetType, (string)parameter) : Binding.DoNothing;
}

/// <summary>True when the bound string equals the converter parameter (case-insensitive).
/// One-way: used to tick the swatch matching the current markup colour.</summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? parameter, CultureInfo c) =>
        string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>True when the bound number equals the converter parameter (invariant culture,
/// tolerance 1e-6). One-way: ticks the thickness row matching the current stroke width.</summary>
public sealed class NumberEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? parameter, CultureInfo c)
    {
        if (value is not double d ||
            !double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
            return false;
        return Math.Abs(d - p) < 1e-6;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Splits a "Name|Key|Hint" tooltip string. Parameter "0"/"1"/"2" returns that part
/// as text; "v1"/"v2" returns Visible when that part is non-empty (Collapsed otherwise), so
/// the tooltip template hides the key cap and hint line when they are not given.</summary>
public sealed class TipPartConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? parameter, CultureInfo c)
    {
        string raw = value as string ?? value?.ToString() ?? "";
        var parts = raw.Split('|');
        string key = parameter as string ?? "0";
        bool asVisibility = key.StartsWith('v');
        int index = int.TryParse(asVisibility ? key[1..] : key, out int i) ? i : 0;
        string part = index < parts.Length ? parts[index].Trim() : "";
        if (asVisibility) return part.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        return part;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b && !b;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b && !b;
}

public sealed class PlusOneConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => (int)value + 1;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class ZoomToPercentConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        $"{Math.Round((double)value * 100)}%";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        System.IO.Path.GetFileName(value as string ?? "") is { Length: > 0 } n ? n : (value ?? "");
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class FileDirConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        try { return System.IO.Path.GetDirectoryName(value as string ?? "") ?? ""; }
        catch { return ""; }
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
