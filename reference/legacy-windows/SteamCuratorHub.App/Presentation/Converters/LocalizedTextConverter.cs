using System.Globalization;
using System.Windows.Data;
using SteamCuratorHub.Presentation.Localization;

namespace SteamCuratorHub.Presentation.Converters;

/// <summary>Resolves a dictionary key against the current language binding.</summary>
public sealed class LocalizedTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => LocalizationService.Get(parameter?.ToString() ?? string.Empty, value?.ToString() ?? "zh-CN");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
