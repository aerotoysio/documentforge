using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DocumentForge.Studio.Converters;

/// <summary>Collapses an element when its bound string is null/empty.</summary>
public sealed class NonEmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
