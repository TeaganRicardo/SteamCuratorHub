using System.Globalization;
using System.Windows.Data;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Presentation.Converters;

public sealed class LocalizedReviewContentConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length >= 2 && values[0] is PlayerReview review ? review.GetContent(values[1]?.ToString() ?? "en-US") : string.Empty;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
