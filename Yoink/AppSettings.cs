namespace Yoink;

/// <summary>
/// User-configurable preferences for the app. Persisted to disk via <see cref="SettingsService"/>.
/// </summary>
public class AppSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;
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
