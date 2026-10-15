using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Pulse.Views;

public static class Converters
{
    public static readonly IValueConverter NotNullToVisibility = new Lambda(v =>
        v is null || v is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible);

    public static readonly IValueConverter InverseBoolToVisibility = new Lambda(v =>
        v is true ? Visibility.Collapsed : Visibility.Visible);

    public static readonly IValueConverter IsNotNull = new Lambda(v => v is not null);

    private sealed class Lambda(Func<object?, object> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
