using System.Globalization;
using System.Windows.Data;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Presentation.Converters;

public sealed class LocalizedGameMetadataConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not Game game) return string.Empty;
        var language = values[1]?.ToString() ?? "en-US";
        return string.Equals(parameter?.ToString(), "reviews", StringComparison.OrdinalIgnoreCase)
            ? game.GetReviewSummary(language)
            : game.GetDescription(language);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
