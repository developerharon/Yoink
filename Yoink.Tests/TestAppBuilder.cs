using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Yoink;

[assembly: AvaloniaTestApplication(typeof(Yoink.Tests.TestAppBuilder))]

namespace Yoink.Tests;

/// <summary>
/// Wires <c>[AvaloniaFact]</c> tests to Yoink's own real <see cref="App"/> (not a stand-in), rendered
/// entirely off-screen — so resource lookups in <c>App.ApplyAccent</c>/the queue-status brush
/// converter resolve against the actual App.axaml resources rather than a hand-rolled approximation.
/// Deliberately does NOT run through <see cref="App.OnFrameworkInitializationCompleted"/>'s
/// classic-desktop-lifetime branch (headless mode's <c>ApplicationLifetime</c> isn't an
/// <c>IClassicDesktopStyleApplicationLifetime</c>), so no <c>MainWindow</c> — and none of its real
/// %AppData%-pointed services — ever gets constructed just by running these tests.
/// </summary>
public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
