using Avalonia.Media;
using Yoink.Converters;
using Yoink.Models;

namespace Yoink.Tests.Converters;

public class DownloadQueueStatusToTextDecorationsConverterTests
{
    private static object? Convert(DownloadQueueStatus status) =>
        DownloadQueueStatusToTextDecorationsConverter.Instance.Convert(
            status, typeof(TextDecorationCollection), null, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Missing_UsesStrikethrough() =>
        Assert.Equal(TextDecorations.Strikethrough, Convert(DownloadQueueStatus.Missing));

    [Theory]
    [InlineData(DownloadQueueStatus.Pending)]
    [InlineData(DownloadQueueStatus.Active)]
    [InlineData(DownloadQueueStatus.Paused)]
    [InlineData(DownloadQueueStatus.Completed)]
    [InlineData(DownloadQueueStatus.Failed)]
    [InlineData(DownloadQueueStatus.Canceled)]
    public void EveryOtherStatus_HasNoDecoration(DownloadQueueStatus status) =>
        Assert.Null(Convert(status));

    [Fact]
    public void ConvertBack_IsNotSupported()
    {
        Assert.Throws<System.NotSupportedException>(() =>
            DownloadQueueStatusToTextDecorationsConverter.Instance.ConvertBack(
                null, typeof(DownloadQueueStatus), null, System.Globalization.CultureInfo.InvariantCulture));
    }
}
