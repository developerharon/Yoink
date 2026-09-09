using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Yoink.Models;

namespace Yoink.Controls;

/// <summary>
/// The IDM-style "several small bars filling up at once" visualization for a
/// <c>Services.DownloadEngine</c> segmented download — added directly in response to real user
/// feedback that the queue view's plain flat progress bar gave no visible sign a "big download" was
/// actually splitting across several connections at once, even though the engine had been doing that
/// since the general-file-downloads phase. Bound to <see cref="DownloadQueueItem.SegmentProgress"/>
/// (via <see cref="Segments"/>) and shown in place of the plain <c>ProgressBar</c> — see
/// <see cref="DownloadQueueItem.ShowSegments"/>/<c>ShowPlainProgress</c> for exactly when each one
/// shows in <c>Views.MainWindow</c>'s queue row template.
///
/// Deliberately one continuous bar, not several separate pill-shaped chunks with gaps between them —
/// that reads more like "one download, made of visible pieces" (closer to what was actually asked
/// for: "0-20, 20-40, 40-60 ... which after all the segments download becomes like one") than a row
/// of disconnected bars would. Each segment's slot width is proportional to its own byte range within
/// the whole file (not simply divided evenly — the last segment is often a different size than the
/// others), filled independently up to that segment's own live fraction, with a thin
/// <see cref="SeparatorBrush"/> line at each internal boundary so the individual pieces still read as
/// distinct while filling up. <see cref="TrackBrush"/>/<see cref="FillBrush"/> default (via the
/// <c>Style</c> in <c>App.axaml</c>) to the exact same <c>AccentSoftBrush</c>/<c>AccentBrush</c> pair
/// the plain <c>ProgressBar</c> already uses, so this reads as a variant of the same control rather
/// than a visually distinct one, and repaints live on an accent-color change
/// (<c>App.ApplyAccent</c>) for free, the same way every other themed control in this app does.
/// </summary>
public sealed class SegmentedProgressBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<DownloadSegmentInfo>?> SegmentsProperty =
        AvaloniaProperty.Register<SegmentedProgressBar, IReadOnlyList<DownloadSegmentInfo>?>(nameof(Segments));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SegmentedProgressBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<SegmentedProgressBar, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<IBrush?> SeparatorBrushProperty =
        AvaloniaProperty.Register<SegmentedProgressBar, IBrush?>(nameof(SeparatorBrush));

    static SegmentedProgressBar()
    {
        AffectsRender<SegmentedProgressBar>(SegmentsProperty, TrackBrushProperty, FillBrushProperty, SeparatorBrushProperty);
        HeightProperty.OverrideDefaultValue<SegmentedProgressBar>(10);
    }

    public IReadOnlyList<DownloadSegmentInfo>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    /// <summary>The thin line drawn between adjacent segments' slots — null means "don't draw dividers at all", not an error.</summary>
    public IBrush? SeparatorBrush
    {
        get => GetValue(SeparatorBrushProperty);
        set => SetValue(SeparatorBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var segments = Segments;
        var bounds = Bounds;
        if (segments is null || segments.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var totalLength = segments.Sum(s => s.EndByte - s.StartByte + 1);
        if (totalLength <= 0)
            return;

        using (context.PushClip(new RoundedRect(new Rect(bounds.Size), bounds.Height / 2.0)))
        {
            if (TrackBrush is { } trackBrush)
                context.FillRectangle(trackBrush, new Rect(bounds.Size));

            var boundaryXs = new List<double>(segments.Count - 1);
            var x = 0.0;
            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                var length = segment.EndByte - segment.StartByte + 1;
                var slotWidth = bounds.Width * length / totalLength;

                var fillWidth = slotWidth * Math.Clamp(segment.Fraction, 0, 1);
                if (fillWidth > 0 && FillBrush is { } fillBrush)
                    context.FillRectangle(fillBrush, new Rect(x, 0, fillWidth, bounds.Height));

                x += slotWidth;
                if (i < segments.Count - 1)
                    boundaryXs.Add(x);
            }

            if (SeparatorBrush is { } separatorBrush)
            {
                var pen = new Pen(separatorBrush, 1);
                foreach (var boundaryX in boundaryXs)
                    context.DrawLine(pen, new Point(boundaryX, 0), new Point(boundaryX, bounds.Height));
            }
        }
    }
}
