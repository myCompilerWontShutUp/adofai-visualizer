using System.Globalization;
using System.IO;
using Visualizer.App.Localization;
using Visualizer.Application.Cache;
using Visualizer.Application.Maps;
using Visualizer.Core.Settings;

namespace Visualizer.App.Views;

/// <summary>
/// The facts a song shows outside the song box: the song list rows and the "load the song you played last" question. Kept
/// in one place so both read the same way.
/// </summary>
public static class SongDetails
{
    /// <summary>One field of <see cref="VisualizerSettings.SongListFieldNames"/> as "label: value".</summary>
    public static string Field(MapCacheEntry entry, string field, UiText text)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(text);
        var timeline = entry.Timeline;
        return field switch
        {
            "Artist" => text.Format("SongListArtist", Or(entry.Info?.Artist)),
            "Length" => text.Format("SongListLength", timeline is { HitTimesMs.Count: > 0 } length
                ? TimeSpan.FromMilliseconds(length.HitTimesMs[^1]).ToString(@"m\:ss", CultureInfo.InvariantCulture) : "-"),
            "Notes" => text.Format("SongListNotes", timeline?.CountPressedHits() ?? 0),
            "Difficulty" => text.Format("SongListDifficulty", entry.Info?.Difficulty is { } level
                ? level.ToString(CultureInfo.InvariantCulture) : "-"),
            "Checkpoints" => text.Format("SongListCheckpoints", timeline?.CheckpointCount ?? 0),
            "Date" => Modified(entry),
            _ => entry.RelativePath,
        };

        static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    /// <summary>Every field, one per line, with the song's state first when it is not Supported.</summary>
    public static string Lines(MapCacheEntry entry, UiText text)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var lines = VisualizerSettings.SongListFieldNames.Select(field => Field(entry, field, text));
        return string.Join(Environment.NewLine, entry.Status == MapParseStatus.Supported ? lines : lines.Prepend(entry.Status.ToString()));
    }

    /// <summary>The map file's date; the song list sorts by it and shows it as one of the fields.</summary>
    public static string Modified(MapCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = Path.Combine(entry.RootDirectory, entry.RelativePath);
        return File.Exists(path) ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-";
    }
}
