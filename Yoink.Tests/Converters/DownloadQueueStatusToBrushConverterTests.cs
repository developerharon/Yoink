using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Yoink.Converters;
using Yoink.Models;

namespace Yoink.Tests.Converters;

/// <summary>
/// [AvaloniaFact] gives these a real Application.Current (Yoink's own App, via TestAppBuilder) so the
/// converter's App.axaml resource lookups (SuccessBrush/ErrorBrush/TextMutedBrush — see BRANDING.md's
/// semantic colors) resolve against the actual app resources, not a stand-in.
/// </summary>
public class DownloadQueueStatusToBrushConverterTests
{
    private static IBrush? Convert(DownloadQueueStatus status) =>
        DownloadQueueStatusToBrushConverter.Instance.Convert(status, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture) as IBrush;

    [AvaloniaFact]
    public void Completed_UsesSuccessBrush()
    {
        Application.Current!.TryGetResource("SuccessBrush", Application.Current.ActualThemeVariant, out var expected);
        Assert.Equal(expected, Convert(DownloadQueueStatus.Completed));
    }

    [AvaloniaTheory]
    [InlineData(DownloadQueueStatus.Failed)]
    [InlineData(DownloadQueueStatus.Canceled)]
    public void FailedOrCanceled_UsesErrorBrush(DownloadQueueStatus status)
    {
        Application.Current!.TryGetResource("ErrorBrush", Application.Current.ActualThemeVariant, out var expected);
        Assert.Equal(expected, Convert(status));
    }

    [AvaloniaTheory]
    [InlineData(DownloadQueueStatus.Pending)]
    [InlineData(DownloadQueueStatus.Active)]
    [InlineData(DownloadQueueStatus.Paused)]
    public void StillInProgress_UsesMutedTextBrush(DownloadQueueStatus status)
    {
        Application.Current!.TryGetResource("TextMutedBrush", Application.Current.ActualThemeVariant, out var expected);
        Assert.Equal(expected, Convert(status));
    }

    [AvaloniaFact]
    public void NonStatusValue_ReturnsUnsetValue()
    {
        var result = DownloadQueueStatusToBrushConverter.Instance.Convert(
            "not a status", typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(AvaloniaProperty.UnsetValue, result);
    }

    [AvaloniaFact]
    public void ConvertBack_IsNotSupported()
    {
        Assert.Throws<System.NotSupportedException>(() =>
            DownloadQueueStatusToBrushConverter.Instance.ConvertBack(
                null, typeof(DownloadQueueStatus), null, System.Globalization.CultureInfo.InvariantCulture));
    }
}
