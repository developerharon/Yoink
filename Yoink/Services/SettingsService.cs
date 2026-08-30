using System;
using System.IO;
using System.Text.Json;
using Yoink.Models;

namespace Yoink.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in the user's per-user config directory
/// (e.g. ~/.config/Yoink on Linux), so preferences survive reinstalling/updating the app itself.
/// </summary>
public static class SettingsService
{
    /// <summary>
    /// Internal (not private) and mutable, rather than a private readonly, purely so
    /// Yoink.Tests can redirect it to an isolated temp file for the duration of a test instead of
    /// ever touching the real user's actual settings.json — reset it back afterward. Nothing in the
    /// app itself ever reassigns this outside of tests.
    /// </summary>
    internal static string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Yoink",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                    return settings;
            }
        }
        catch
        {
            // Missing/corrupt settings file: fall back to defaults rather than failing startup.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
