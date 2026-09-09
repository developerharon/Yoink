using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Yoink.Models;

namespace Yoink.Converters;

/// <summary>
/// Strikes through the queue view's title text for a <see cref="DownloadQueueStatus.Missing"/> row —
/// the visual half of "cross it out as missing" (the color half is
/// <see cref="DownloadQueueStatusToBrushConverter"/>'s <c>WarningBrush</c> on the status text next to
/// it). Every other status renders with no decoration.
/// </summary>
public class DownloadQueueStatusToTextDecorationsConverter : IValueConverter
{
    public static readonly DownloadQueueStatusToTextDecorationsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DownloadQueueStatus.Missing ? TextDecorations.Strikethrough : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
