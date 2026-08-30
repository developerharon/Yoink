using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Yoink;

/// <summary>
/// Maps a <see cref="DownloadStatus"/> to its semantic brush (see BRANDING.md) for the history list's
/// status text.
/// </summary>
public class DownloadStatusToBrushConverter : IValueConverter
{
    public static readonly DownloadStatusToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DownloadStatus status)
            return AvaloniaProperty.UnsetValue;

        var resourceKey = status == DownloadStatus.Completed ? "SuccessBrush" : "ErrorBrush";
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
