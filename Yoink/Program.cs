using Avalonia;
using Velopack;

namespace Yoink;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before anything else, full stop — this is how Velopack (README roadmap's
        // update/distribution story; see Services/UpdateService.cs) recognizes when it's been
        // launched with its own special install/update/uninstall hook arguments rather than a
        // normal run, handles them, and exits on its own. On an ordinary launch this returns
        // immediately and execution just continues into Avalonia below.
        VelopackApp.Build().Run();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
