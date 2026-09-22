using System.Globalization;
using System.Windows.Data;
using SteamCuratorHub.Presentation.ViewModels;

namespace SteamCuratorHub.Presentation.Converters;

public sealed class LocalizedSortOptionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.FirstOrDefault() is not SortOption option)
            return string.Empty;
        var language = values.Skip(1).FirstOrDefault()?.ToString() ?? "en-US";
        return language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? option.Chinese : option.English;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
