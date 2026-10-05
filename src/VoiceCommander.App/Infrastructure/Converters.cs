using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace VoiceCommander.App.Infrastructure;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (parameter is string s && s == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
}

/// <summary>Visible when the value is non-null / non-empty; "invert" flips it.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var has = value is string s ? !string.IsNullOrEmpty(s) : value != null;
        if (parameter is string p && p == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the integer value is zero ("empty state" hints); "invert" shows when non-zero.</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var zero = value is int i && i == 0;
        if (parameter is string p && p == "invert") zero = !zero;
        return zero ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps a 0..1 microphone level to a width fraction of the parameter (max width).</summary>
public sealed class LevelToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double level || values[1] is not double width) return 0.0;
        return Math.Max(0, Math.Min(1, level)) * width;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Enum / string equality for radio-style bindings: parameter is the value this button represents.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && targetType.IsEnum && parameter != null ? Enum.Parse(targetType, parameter.ToString()!, true) : Binding.DoNothing;
}

public sealed class BrushNameConverter : IValueConverter
{
    /// <summary>Resolves a theme brush key (string) to the current brush.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key && System.Windows.Application.Current.TryFindResource(key) is Brush b ? b : Brushes.Transparent;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Loads cached icon PNGs once and shares the frozen bitmap, so lists never re-decode on every render.</summary>
public static class IconCache
{
    private static readonly Dictionary<string, System.Windows.Media.ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static System.Windows.Media.ImageSource? Load(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var hit)) return hit;
            System.Windows.Media.ImageSource? img = null;
            try
            {
                if (System.IO.File.Exists(path))
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                    bmp.DecodePixelWidth = 64;
                    bmp.UriSource = new Uri(path);
                    bmp.EndInit();
                    bmp.Freeze();
                    img = bmp;
                }
            }
            catch { img = null; }
            Cache[path] = img;
            return img;
        }
    }
}

/// <summary>Icon file path (string) → cached ImageSource.</summary>
public sealed class IconPathConverter : IValueConverter
{
    public static readonly IconPathConverter Instance = new();
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => IconCache.Load(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
