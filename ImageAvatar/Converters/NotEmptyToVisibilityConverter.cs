using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ImageAvatar.Converters;

[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
