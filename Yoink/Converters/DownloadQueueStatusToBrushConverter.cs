using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Yoink.Models;

namespace Yoink.Converters;

/// <summary>
/// Maps a <see cref="DownloadQueueStatus"/> to its semantic brush (see BRANDING.md) for the queue
/// view's status text: green for a completed download, red for one that didn't make it, amber for
/// one whose file went missing after the fact (a real download outcome, but distinct from a download
/// that itself failed), muted for everything still in progress (pending/active/paused).
/// </summary>
public class DownloadQueueStatusToBrushConverter : IValueConverter
{
    public static readonly DownloadQueueStatusToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DownloadQueueStatus status)
            return AvaloniaProperty.UnsetValue;

        var resourceKey = status switch
        {
            DownloadQueueStatus.Completed => "SuccessBrush",
            DownloadQueueStatus.Failed or DownloadQueueStatus.Canceled => "ErrorBrush",
            DownloadQueueStatus.Missing => "WarningBrush",
            _ => "TextMutedBrush"
        };

        var app = Application.Current;
        if (app is not null && app.TryGetResource(resourceKey, app.ActualThemeVariant, out var resource))
            return resource as IBrush;

        return AvaloniaProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
