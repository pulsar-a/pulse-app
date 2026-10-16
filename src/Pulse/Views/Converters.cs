using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Pulse.Views;

public static class Converters
{
    public static readonly IValueConverter NotNullToVisibility = new Lambda(v =>
        v is null || v is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible);

    /// <summary>Visible only while the value is null, for placeholders like "Choose a header".</summary>
    public static readonly IValueConverter NullToVisibility = new Lambda(v =>
        v is null ? Visibility.Visible : Visibility.Collapsed);

    public static readonly IValueConverter InverseBoolToVisibility = new Lambda(v =>
        v is true ? Visibility.Collapsed : Visibility.Visible);

    public static readonly IValueConverter IsNotNull = new Lambda(v => v is not null);

    /// <summary>
    /// For a row of radio buttons bound two-way to an index: checked when the index equals ConverterParameter,
    /// and checking one sets the index to it.
    /// </summary>
    public static readonly IValueConverter IndexIs = new IndexConverter();

    private sealed class IndexConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is int index && Index(parameter) == index;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true && Index(parameter) is { } index ? index : Binding.DoNothing;

        private static int? Index(object? parameter) =>
            parameter is int i ? i : int.TryParse(parameter as string, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private sealed class Lambda(Func<object?, object> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
