using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Yoink.Models;
using Yoink.Services;
using Yoink.Views;

namespace Yoink;

public partial class App : Application
{
    /// <summary>
    /// The window/taskbar/tray icon for the currently-applied accent (see <see cref="ApplyAccent"/>),
    /// so a freshly-constructed <see cref="Window"/> can just read this in its own constructor
    /// instead of every window needing its own accent-lookup logic.
    /// </summary>
    public static WindowIcon? CurrentIcon { get; private set; }

    private static TrayIcon? _trayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = SettingsService.Load();
        RequestedThemeVariant = ToThemeVariant(settings.Theme);
        ApplyAccent(settings.AccentColor);

        // AccentSoftBrush (below) depends on light-vs-dark, not just the chosen preset, so it needs
        // recomputing whenever the *actual* variant changes — including a live OS light/dark flip
        // while Theme is set to System, which never runs through Views.SettingsView at all.
        ActualThemeVariantChanged += (_, _) => ApplyAccent(SettingsService.Load().AccentColor);

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
            Icon = CurrentIcon,
            ToolTipText = "Yoink",
            Menu = menu
        };
        trayIcon.Clicked += (_, _) => RestoreMainWindow(mainWindow);

        TrayIcon.SetIcons(Current!, new TrayIcons { trayIcon });
        _trayIcon = trayIcon;

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

    /// <summary>base/hover/active/soft straight from BRANDING.md — "on-accent" is white for every
    /// preset there (all five are dark enough to hold white text at AA contrast), so it's not
    /// worth a fifth field here.</summary>
    private readonly record struct AccentPalette(Color Base, Color Hover, Color Active, Color Soft);

    private static readonly Dictionary<AccentColor, AccentPalette> AccentPalettes = new()
    {
        [AccentColor.Blue] = new AccentPalette(
            Color.Parse("#2F6FED"), Color.Parse("#275DC7"), Color.Parse("#1F4AA0"), Color.Parse("#E4ECFD")),
        [AccentColor.Orange] = new AccentPalette(
            Color.Parse("#E95420"), Color.Parse("#C6431A"), Color.Parse("#A23716"), Color.Parse("#FDEAE2")),
        [AccentColor.Purple] = new AccentPalette(
            Color.Parse("#8B5CF6"), Color.Parse("#7C3AED"), Color.Parse("#6D28D9"), Color.Parse("#F1EAFE")),
        [AccentColor.Green] = new AccentPalette(
            Color.Parse("#22A06B"), Color.Parse("#1C8659"), Color.Parse("#166B47"), Color.Parse("#E0F5EC")),
        [AccentColor.Red] = new AccentPalette(
            Color.Parse("#E5484D"), Color.Parse("#CB3439"), Color.Parse("#A82A2E"), Color.Parse("#FBE4E5")),
    };

    /// <summary>
    /// Applies one of the five accent presets from BRANDING.md app-wide — called once at startup
    /// (from the persisted <see cref="AppSettings.AccentColor"/>) and again immediately whenever
    /// <c>Views.SettingsView</c>'s accent swatches are clicked, the same live-update pattern
    /// <see cref="ToThemeVariant"/>'s caller uses for theme. Every affected resource
    /// (<c>SystemAccentColor</c> and friends, so FluentTheme's own controls — checkboxes, sliders,
    /// focus rings — pick it up too, plus this app's own AccentBrush/AccentSoftBrush) is looked up
    /// via DynamicResource everywhere it's consumed, so overwriting the dictionary entries here is
    /// enough to repaint every open window without anyone needing to be told about it.
    /// FluentTheme itself only hands us one hover/pressed shade per theme direction, so
    /// Light1-3/Dark1-3 are derived: Dark1/2 are the brand's own hover/active (both already darker,
    /// which is what a light-background control's pressed state wants), Dark3 one step past that;
    /// Light1-3 are the base tinted toward white in even steps for dark-background controls' hover.
    /// </summary>
    public static void ApplyAccent(AccentColor accent)
    {
        var p = AccentPalettes[accent];
        var resources = Current!.Resources;

        resources["SystemAccentColor"] = p.Base;
        resources["SystemAccentColorLight1"] = Lerp(p.Base, Colors.White, 0.2);
        resources["SystemAccentColorLight2"] = Lerp(p.Base, Colors.White, 0.4);
        resources["SystemAccentColorLight3"] = Lerp(p.Base, Colors.White, 0.6);
        resources["SystemAccentColorDark1"] = p.Hover;
        resources["SystemAccentColorDark2"] = p.Active;
        resources["SystemAccentColorDark3"] = Lerp(p.Active, Colors.Black, 0.2);

        resources["AccentBrush"] = new SolidColorBrush(p.Base);
        resources["AccentLightBrush"] = new SolidColorBrush((Color)resources["SystemAccentColorLight1"]!);
        resources["AccentDarkBrush"] = new SolidColorBrush(p.Hover);
        resources["OnAccentBrush"] = new SolidColorBrush(Colors.White);

        // BRANDING.md ships a flat pastel tint for the progress-track/subtle-background "soft"
        // color on light surfaces, and a low-opacity wash of the accent itself for dark ones
        // (a fixed pastel would either wash out or barely show against a dark card).
        var isDark = Current.ActualThemeVariant == ThemeVariant.Dark;
        resources["AccentSoftBrush"] = isDark
            ? new SolidColorBrush(p.Base, 0.18)
            : new SolidColorBrush(p.Soft);

        // The window/taskbar/tray icon itself is one of BRANDING.md's five full-color badge PNGs
        // (Assets/app-icons/, rendered from the badge SVGs — see that file for how) — matching the
        // chosen accent, same as every button/progress-bar recolor above. CurrentIcon is what a
        // freshly-opened window reads in its own constructor; the loop below is what repaints one
        // already open (MainWindow chief among them, since it usually outlives a Settings visit).
        CurrentIcon = LoadIcon(accent);

        if (Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
                window.Icon = CurrentIcon;
        }

        if (_trayIcon is not null)
            _trayIcon.Icon = CurrentIcon;
    }

    private static WindowIcon LoadIcon(AccentColor accent)
    {
        var name = accent.ToString().ToLowerInvariant();
        using var stream = AssetLoader.Open(new Uri($"avares://Yoink/Assets/app-icons/app-icon-{name}.png"));
        return new WindowIcon(stream);
    }

    private static Color Lerp(Color from, Color to, double t) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * t),
        (byte)(from.G + (to.G - from.G) * t),
        (byte)(from.B + (to.B - from.B) * t));
}
