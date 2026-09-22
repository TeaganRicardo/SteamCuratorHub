using System.Globalization;
using System.Windows.Data;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Presentation.Converters;

public sealed class LocalizedGameNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var game = values.FirstOrDefault() as Game;
        var language = values.Skip(1).FirstOrDefault()?.ToString() ?? "zh-CN";
        return game?.GetName(language) ?? string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
