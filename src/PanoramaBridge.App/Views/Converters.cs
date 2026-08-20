using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PanoramaBridge.App.Views;

/// <summary>Shows an element when a boolean is false. The inverse of the built-in converter.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>
/// Shows an element only when a nullable number has a value.
/// </summary>
/// <remarks>
/// Used for the progress bar: a null fraction means the size is unknown, and an empty bar reads
/// as "nothing is happening" rather than "this cannot be measured".
/// </remarks>
public sealed class NullableDoubleToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// True when a bound enum equals the value named in the parameter.
/// </summary>
/// <remarks>
/// Lets a group of radio buttons bind to one enum property without a converter per member.
/// </remarks>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null
        && parameter is string name
        && string.Equals(value.ToString(), name, StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Only the checked radio button writes back; the unchecked ones must not clear the value.
        if (value is not true || parameter is not string name)
        {
            return Binding.DoNothing;
        }

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return Enum.Parse(enumType, name);
    }
}
