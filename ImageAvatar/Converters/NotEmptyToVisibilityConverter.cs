using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ImageAvatar.Converters;

/// <summary>
/// Converts a string or integer value to Visibility.
/// Visible when the value is non-empty / non-zero; Collapsed otherwise.
/// Pass ConverterParameter="Inverse" to flip the logic (Visible when empty/zero).
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isEmpty = value switch
        {
            int i    => i == 0,
            string s => string.IsNullOrEmpty(s),
            _        => value is null
        };

        bool inverse = parameter is string p &&
                       p.Equals("Inverse", StringComparison.OrdinalIgnoreCase);

        return (inverse ? isEmpty : !isEmpty) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
