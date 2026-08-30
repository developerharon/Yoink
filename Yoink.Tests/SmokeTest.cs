using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;

namespace Yoink.Tests;

public class SmokeTest
{
    [AvaloniaFact]
    public void ApplicationCurrent_IsNotClassicDesktopLifetime()
    {
        // If this ever becomes IClassicDesktopStyleApplicationLifetime, App.OnFrameworkInitializationCompleted
        // would construct a real MainWindow (and its %AppData%-pointed services) just from running tests.
        Assert.NotNull(Application.Current);
        Assert.False(Application.Current!.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime);
    }

    [AvaloniaFact]
    public void AccentBrush_ResourceExists()
    {
        Assert.True(Application.Current!.TryGetResource("AccentBrush", null, out var resource));
        Assert.NotNull(resource);
    }
}
