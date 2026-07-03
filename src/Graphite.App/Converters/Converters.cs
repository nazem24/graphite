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
