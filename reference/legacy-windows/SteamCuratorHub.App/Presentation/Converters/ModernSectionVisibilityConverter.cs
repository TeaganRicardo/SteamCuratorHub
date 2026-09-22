using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SteamCuratorHub.Presentation.Converters;

/// <summary>Visibility converter for the richer Inventory, Playthrough and Editor panels.</summary>
public sealed class ModernSectionVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var selected = value?.ToString();
        var section = parameter?.ToString();
        return string.Equals(selected, section, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
