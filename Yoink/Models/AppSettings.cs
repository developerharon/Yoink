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
