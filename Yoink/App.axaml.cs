using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Yoink.Models;
using Yoink.Services;
using Yoink.Views;

namespace Yoink;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        RequestedThemeVariant = ToThemeVariant(SettingsService.Load().Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            SetUpTrayIcon(desktop, mainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Background operation (README roadmap step 6): a tray icon so the app can keep the download
    /// queue running without a window open. Whether closing the main window hides it (instead of
    /// quitting) is gated behind <see cref="AppSettings.MinimizeToTrayOnClose"/> — see that
    /// property's doc comment for why this isn't unconditional. Either way, the desktop lifetime
    /// moves to <see cref="ShutdownMode.OnExplicitShutdown"/> so this method's own Closing handler
    /// is always the one deciding whether a close becomes a hide or an actual shutdown, rather than
    /// racing an implicit one.
    /// </summary>
    private static void SetUpTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow mainWindow)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var menu = new NativeMenu();

        var showItem = new NativeMenuItem("Show Yoink");
        showItem.Click += (_, _) => RestoreMainWindow(mainWindow);
        menu.Items.Add(showItem);

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) => desktop.TryShutdown();
        menu.Items.Add(quitItem);

        var trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Yoink/Assets/tray-icon.png"))),
            ToolTipText = "Yoink",
            Menu = menu
        };
        trayIcon.Clicked += (_, _) => RestoreMainWindow(mainWindow);

        TrayIcon.SetIcons(Current!, new TrayIcons { trayIcon });

        mainWindow.Closing += (_, e) =>
        {
            // Only intercept the user clicking the window's own close button — let this same
            // handler's own desktop.TryShutdown() call below (or an OS-driven shutdown/logout)
            // close the window for real on its way back through, rather than looping forever.
            if (e.CloseReason != WindowCloseReason.WindowClosing)
                return;

            e.Cancel = true;

            if (SettingsService.Load().MinimizeToTrayOnClose)
                mainWindow.Hide();
            else
                desktop.TryShutdown();
        };
    }

    private static void RestoreMainWindow(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// Maps a persisted preference to the Avalonia theme variant that applies it.
    /// <see cref="ThemeVariant.Default"/> is what makes "System" track the OS setting, including
    /// live changes to it while the app is running.
    /// </summary>
    public static ThemeVariant ToThemeVariant(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };
}
