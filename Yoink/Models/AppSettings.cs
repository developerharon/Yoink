namespace Yoink.Models;

/// <summary>
/// User-configurable preferences for the app. Persisted to disk via <see cref="SettingsService"/>.
/// </summary>
public class AppSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>
    /// Whether <see cref="Services.ClipboardWatcherService"/> is active. On by default — it only
    /// ever prompts before queuing anything, never downloads silently — but stays easy to turn off
    /// via the header toggle for anyone who'd rather not have their clipboard polled at all.
    /// </summary>
    public bool ClipboardWatchEnabled { get; set; } = true;

    /// <summary>
    /// Whether closing the main window hides it to the tray icon instead of quitting the app (see
    /// <c>App.SetUpTrayIcon</c>). Off by default: on a Linux desktop without tray/StatusNotifierItem
    /// support (plain GNOME without an extension, for instance) the tray icon simply won't be
    /// visible, and hiding-not-closing by default there would strand the window with no way back.
    /// Opt-in via the header toggle once someone's confirmed their tray actually shows it.
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; }
}

/// <summary>
/// The theme the user has chosen. <see cref="System"/> follows the OS light/dark setting and updates
/// automatically if the OS setting changes while the app is running.
/// </summary>
public enum ThemePreference
{
    System,
    Light,
    Dark
}
