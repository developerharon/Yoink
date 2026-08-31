using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Velopack;
using Yoink.Models;
using Yoink.Services;
using Yoink.Views;

namespace Yoink.Tests.Views;

/// <summary>
/// Regression coverage for a real report: after opening Settings and pressing the custom back
/// button, the built-in Settings entry's highlight stayed on, and clicking it again did nothing —
/// see MainWindow's <c>BtnBack_Click</c>/<c>ClearSettingsSelection</c> doc comments for the fix.
/// This asserts the outcome the fix guarantees (highlight cleared, Settings reopens), not the exact
/// FluentAvaloniaUI-internal race behind the original bug — a click-simulation run against this real
/// window, the same technique used here, never once reproduced that race itself; whatever triggers it
/// needs a live compositor's actual animation-frame timing this sandbox doesn't have (see the
/// headless-visual-verification project memory).
///
/// Uses real mouse clicks via Avalonia.Headless's MouseDown/MouseUp at each control's actual
/// on-screen position — not property pokes — since the original bug (DialogTitleBar's close button,
/// fixed earlier) turned out to be invisible to anything less than a genuine click.
///
/// Redirects the static <see cref="SettingsService.SettingsPath"/> and constructs
/// <see cref="MainWindow"/> against a temp <c>queue.db</c> (via the <c>databasePath</c> constructor
/// parameter added for exactly this) — never the real user's settings.json/queue.db, per this
/// project's own established test convention (see SettingsServiceTests/DownloadQueueServiceTests).
/// </summary>
public class MainWindowNavigationTests : IDisposable
{
    private readonly string _originalSettingsPath = SettingsService.SettingsPath;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"yoink-tests-{Guid.NewGuid():N}");

    // MainWindow's constructor builds a UpdateService, which throws InvalidOperationException
    // ("No VelopackLocator has been set") unless this has run first — see UpdateService's own doc
    // comment. Real Program.cs makes this the literal first line of Main; nothing in the test
    // process's own entry point (xUnit v3's generated Main) ever calls it, since no other test
    // constructs a real MainWindow. A static constructor runs at most once for this whole process,
    // before any test method below.
    static MainWindowNavigationTests()
    {
        VelopackApp.Build().Run();
    }

    public MainWindowNavigationTests()
    {
        Directory.CreateDirectory(_tempDir);
        SettingsService.SettingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        SettingsService.SettingsPath = _originalSettingsPath;
        Directory.Delete(_tempDir, recursive: true);
    }

    [AvaloniaFact]
    public void BackButton_ClearsSettingsHighlight_AndAllowsReopeningSettings()
    {
        // Whatever accent happens to be applied by the time this runs is irrelevant to navigation,
        // but MainWindow's constructor reads App.CurrentIcon — set once so it's never null here.
        App.ApplyAccent(AccentColor.Blue);

        var window = new MainWindow(Path.Combine(_tempDir, "queue.db"));
        try
        {
            window.Show();
            Pump();

            var settingsItem = window.GetVisualDescendants()
                .OfType<FANavigationViewItem>()
                .Single(i => i.Name == "SettingsItem");

            // Open Settings for real, the same way a user does.
            Click(window, settingsItem);
            Assert.True(window.SettingsBody.IsVisible);
            Assert.False(window.DownloadsBody.IsVisible);
            Assert.True(window.NavView.SettingsItem!.IsSelected);

            // Then press Back — this is the exact gesture from the report.
            Click(window, window.BtnBack);

            Assert.True(window.DownloadsBody.IsVisible);
            Assert.False(window.SettingsBody.IsVisible);
            Assert.False(window.BtnBack.IsVisible);
            // The actual bug: this stayed true after Back, so the gear icon stayed highlighted.
            Assert.False(window.NavView.SettingsItem!.IsSelected);
            Assert.NotEqual(window.NavView.SettingsItem, window.NavView.SelectedItem);

            // And the other half of the bug: clicking Settings again did nothing.
            Click(window, settingsItem);

            Assert.True(window.SettingsBody.IsVisible);
            Assert.False(window.DownloadsBody.IsVisible);
            Assert.True(window.NavView.SettingsItem!.IsSelected);
        }
        finally
        {
            // Triggers MainWindow.OnClosed, which disposes the DownloadQueueService/
            // ClipboardWatcherService this constructed — without this they'd keep running in the
            // background for the rest of this sequential test process.
            window.Close();
        }
    }

    private static void Click(Window window, Control target)
    {
        var topLeft = target.TranslatePoint(new Point(0, 0), window)!.Value;
        var center = topLeft + new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Pump();
    }

    private static void Pump()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }
}
