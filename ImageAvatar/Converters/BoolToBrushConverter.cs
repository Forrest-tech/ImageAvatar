using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ImageAvatar.Converters;

[ValueConversion(typeof(bool), typeof(Brush))]
public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush  { get; set; } = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    public Brush FalseBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x42, 0x42, 0x42));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
