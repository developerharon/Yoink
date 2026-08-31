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
}
