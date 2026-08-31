using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yoink.Models;

namespace Yoink.Services;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for <see cref="AppSettings"/>, so
/// <see cref="SettingsService"/> never touches System.Text.Json's reflection-based
/// serializer/deserializer: every property is known at compile time instead of discovered by
/// reflecting over <see cref="AppSettings"/> at every single Load()/Save() call (Load happens once
/// per <see cref="DownloadQueueService"/> processing-loop iteration and once per
/// <see cref="ClipboardWatcherService"/> poll — frequent enough that avoiding the reflection
/// overhead is a real, not just theoretical, saving). This is also what makes the app safe to
/// publish trimmed (see Yoink.csproj's PublishTrimmed comment): reflection-based
/// JsonSerializer.Serialize/Deserialize calls are exactly what the trimmer can't statically prove
/// are safe (confirmed — a trimmed publish before this fix emitted IL2026 warnings pointing at
/// this class's two calls specifically), so without this, trimming risked the linker stripping
/// <see cref="AppSettings"/>'s properties out from under it.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;

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
                var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
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
        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        File.WriteAllText(SettingsPath, json);
    }

    /// <summary>
    /// The platform's own "Downloads" folder, used whenever <see cref="AppSettings.DownloadFolder"/>
    /// is unset. Windows and macOS don't expose a "Downloads" case among
    /// <see cref="Environment.SpecialFolder"/> the way they do Desktop/Documents (Windows only
    /// exposes it as a COM known-folder GUID, which would need interop nothing else in this app
    /// needs), but both default the real thing to a "Downloads" sibling of the profile folders that
    /// <see cref="Environment.SpecialFolder.UserProfile"/> *does* expose, so that guess is used
    /// directly there. Linux instead honors the user's actual freedesktop.org XDG_DOWNLOAD_DIR
    /// (env var first, then the ~/.config/user-dirs.dirs file most desktops write it to) — a
    /// relocated or localized ("Descargas", "Téléchargements", ...) Downloads folder isn't a given
    /// there the way it is on Windows/macOS — falling back to the same ~/Downloads guess if nothing
    /// configures it.
    /// </summary>
    public static string GetDefaultDownloadFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fallback = Path.Combine(home, "Downloads");

        if (!OperatingSystem.IsLinux())
            return fallback;

        var xdgEnv = Environment.GetEnvironmentVariable("XDG_DOWNLOAD_DIR");
        if (!string.IsNullOrWhiteSpace(xdgEnv))
            return xdgEnv;

        try
        {
            var userDirsPath = Path.Combine(home, ".config", "user-dirs.dirs");
            if (File.Exists(userDirsPath))
            {
                var configured = ParseXdgDownloadDir(File.ReadAllText(userDirsPath), home);
                if (!string.IsNullOrWhiteSpace(configured))
                    return configured;
            }
        }
        catch
        {
            // Best-effort — an unreadable/corrupt user-dirs.dirs just falls through to the guess below.
        }

        return fallback;
    }

    /// <summary>
    /// Pulls XDG_DOWNLOAD_DIR out of a freedesktop.org user-dirs.dirs file's contents (format:
    /// <c>XDG_DOWNLOAD_DIR="$HOME/Downloads"</c>, one assignment per line, '#' comments allowed),
    /// expanding the literal "$HOME" the file conventionally uses. Internal (not private) so
    /// Yoink.Tests can exercise the parsing directly rather than needing a real
    /// ~/.config/user-dirs.dirs file on whatever machine runs the tests. Returns null if the file
    /// has no such line.
    /// </summary>
    internal static string? ParseXdgDownloadDir(string userDirsFileContent, string home)
    {
        const string key = "XDG_DOWNLOAD_DIR=";

        foreach (var rawLine in userDirsFileContent.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(key, StringComparison.Ordinal))
                continue;

            var value = line[key.Length..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];

            return value.Replace("$HOME", home);
        }

        return null;
    }
}
