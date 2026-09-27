using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TermIDM.Desktop;

public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var status = value?.ToString() ?? string.Empty;
        var color = status.ToLowerInvariant() switch
        {
            "downloading" or "connecting" or "queued" => Color.FromArgb(255, 59, 130, 246),   // Blue
            "completed" => Color.FromArgb(255, 16, 185, 129),   // Green
            "paused" or "pausing" => Color.FromArgb(255, 245, 158, 11),     // Amber
            "cancelled" or "cancelling" => Color.FromArgb(255, 107, 114, 128), // Gray
            "error" or "failed" => Color.FromArgb(255, 239, 68, 68),    // Red
            _ => Color.FromArgb(255, 100, 116, 139),                  // Slate
        };
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
