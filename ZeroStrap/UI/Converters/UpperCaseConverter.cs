using System.Windows.Data;

namespace ZeroStrap.UI.Converters
{
    /// <summary>Upper-cases string content (column headers); any other value passes through untouched.</summary>
    class UpperCaseConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is string text ? text.ToUpper(culture) : value;

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
