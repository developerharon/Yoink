using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Yoink;

/// <summary>
/// Loads and saves the "Recent downloads" list as JSON, alongside settings.json in the user's
/// per-user config directory. Keeps only the most recent <see cref="MaxEntries"/> so the file
/// (and the list in the UI) can't grow without bound.
/// </summary>
public static class DownloadHistoryService
{
    private const int MaxEntries = 50;

    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Yoink",
        "history.json");

    public static List<DownloadHistoryEntry> Load()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var json = File.ReadAllText(HistoryPath);
                var entries = JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(json);
                if (entries is not null)
                    return entries;
            }
        }
        catch
        {
            // Missing/corrupt history file: start fresh rather than failing startup.
        }

        return [];
    }

    public static void Save(IEnumerable<DownloadHistoryEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        var trimmed = entries.Take(MaxEntries);
        var json = JsonSerializer.Serialize(trimmed, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(HistoryPath, json);
    }
}
