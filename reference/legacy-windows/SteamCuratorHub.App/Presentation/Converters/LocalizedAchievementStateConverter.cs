using System.Globalization;
using System.Windows.Data;
using SteamCuratorHub.Presentation.Localization;

namespace SteamCuratorHub.Presentation.Converters;

public sealed class LocalizedAchievementStateConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var unlocked = values.FirstOrDefault() is bool value && value;
        var language = values.Skip(1).FirstOrDefault()?.ToString() ?? "zh-CN";
        return LocalizationService.Get(unlocked ? "Unlocked" : "Locked", language);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
