using System.Globalization;
using System.Windows.Data;
using SteamCuratorHub.Presentation.Localization;

namespace SteamCuratorHub.Presentation.Converters;

public sealed class LocalizedCuratorCopyStatusConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = values.FirstOrDefault()?.ToString() ?? string.Empty;
        var language = values.Skip(1).FirstOrDefault()?.ToString() ?? "en-US";
        var key = status.ToLowerInvariant() switch { "available" => "Available", "claimed" => "Claimed", "expired" => "StatusArchived", _ => status };
        if (string.Equals(parameter?.ToString(), "action", StringComparison.OrdinalIgnoreCase) && string.Equals(status, "Available", StringComparison.OrdinalIgnoreCase))
            key = "Claim";
        return LocalizationService.Get(key, language);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
