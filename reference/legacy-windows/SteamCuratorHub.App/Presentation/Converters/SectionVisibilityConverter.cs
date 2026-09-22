using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SteamCuratorHub.Presentation.Converters;

public sealed class SectionVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // The richer panels own these three sections; the legacy inline blocks remain available for reference.
        var section = parameter?.ToString();
        if (section is "Inventory" or "Playthrough" or "Editor") return Visibility.Collapsed;
        return string.Equals(value?.ToString(), section, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
