using System.Globalization;
using System.Windows.Data;
using SteamCuratorHub.Domain.Models;
using SteamCuratorHub.Presentation.Localization;

namespace SteamCuratorHub.Presentation.Converters;

public sealed class LocalizedStatusConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => LocalizationService.Status(values.FirstOrDefault() is ReviewTaskStatus status ? status : ReviewTaskStatus.Pending, values.Skip(1).FirstOrDefault()?.ToString() ?? "zh-CN");
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
