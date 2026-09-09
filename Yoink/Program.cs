using System;
using System.Linq;
using Avalonia;
using Velopack;
using Yoink.Services;

namespace Yoink;

internal static class Program
{
    /// <summary>
    /// A URL handed to this launch (via <c>--url=</c>, or a bare positional argument Chrome's
    /// native-messaging host / a `.desktop` file's <c>Exec=... %u</c> passed straight through) that
    /// should open pre-filled in <c>AddDownloadDialog</c> once <see cref="App"/> has a
    /// <see cref="Views.MainWindow"/> to show it in. Null on an ordinary launch with nothing to hand
    /// off. A plain static field, not passed through <c>AppBuilder</c>, since <c>App.axaml.cs</c>
    /// already reads static <see cref="App.CurrentIcon"/> the same way — this app has no DI container
    /// to thread it through instead.
    /// </summary>
    internal static string? PendingLaunchUrl { get; private set; }

    // Held for the process's entire lifetime once acquired — see SingleInstanceLock's own doc
    // comment. Never explicitly disposed; the OS releases the underlying file lock the moment this
    // process exits, by any means (clean shutdown, crash, kill).
    private static SingleInstanceLock? _instanceLock;

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
        // immediately and execution just continues below. Stays unconditional on every platform,
        // including a Linux .deb install with no Velopack context at all — it's a guaranteed no-op
        // there, since a .deb-installed Yoink is never invoked with Velopack's own hook arguments.
        VelopackApp.Build().Run();

        // A second entry point into this same executable — Chrome spawns it this way as the native
        // messaging host (see NativeMessagingHost's own doc comment for the full bridge). Branches
        // out before touching anything Avalonia/UI-related, mirroring the shape of the Velopack
        // check just above.
        if (args.Length > 0 && args[0] == "--native-messaging-host")
        {
            NativeMessagingHost.Run();
            return;
        }

        var url = ExtractUrlArg(args);

        // Only one instance is ever allowed to become "the" running Yoink (own window, own tray
        // icon, own queue.db writer) — see SingleInstanceLock's own doc comment for why a file lock
        // rather than a named Mutex. A losing second launch has nothing useful left to do beyond
        // handing off whatever URL it was given, if any, to the instance that's already running.
        if (SingleInstanceLock.TryAcquire() is { } instanceLock)
        {
            _instanceLock = instanceLock;
            PendingLaunchUrl = url;

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        else if (url is not null)
        {
            _ = SingleInstanceIpcService.TrySendAsync(url, timeoutMs: 3000).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// A URL can arrive either as an explicit <c>--url=&lt;value&gt;</c> flag (what
    /// <see cref="NativeMessagingHost"/>'s own cold-start fallback always uses) or a bare positional
    /// argument recognized by <see cref="DownloadUrlKind.IsRecognizedDownloadUrl"/> (what a `.deb`'s
    /// <c>.desktop</c> file's <c>Exec=... %u</c> hands a launcher-opened link as) — null if neither
    /// shape is present, meaning an ordinary launch with nothing to hand off.
    /// </summary>
    internal static string? ExtractUrlArg(string[] args)
    {
        const string prefix = "--url=";
        var flagArg = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));
        if (flagArg is not null)
            return flagArg[prefix.Length..];

        return args.FirstOrDefault(DownloadUrlKind.IsRecognizedDownloadUrl);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
