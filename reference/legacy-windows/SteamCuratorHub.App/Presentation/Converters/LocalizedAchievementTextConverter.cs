using System.Globalization;
using System.Windows.Data;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Presentation.Converters;

public sealed class LocalizedAchievementTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not Achievement achievement) return string.Empty;
        var language = values[1]?.ToString() ?? "en-US";
        return string.Equals(parameter?.ToString(), "description", StringComparison.OrdinalIgnoreCase)
            ? achievement.GetDescription(language)
            : achievement.GetName(language);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
