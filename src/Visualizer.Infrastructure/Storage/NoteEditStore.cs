using System.Text.Json;
using Visualizer.Application.Cache;
using Visualizer.Application.Library;
using Visualizer.Core.Settings;

namespace Visualizer.Infrastructure.Storage;

/// <summary>
/// Saved edits under <c>&lt;store&gt;/maps/edited/&lt;song&gt;/&lt;slot&gt;/</c>: the song's own <c>.adofai</c> text next to an
/// <c>edit.json</c> holding the lanes and chords. The copy is what makes an edit stand on its own after a resync, and the
/// original map folder is never written to.
/// </summary>
public sealed class NoteEditStore(MapLibrary maps)
{
    private readonly MapLibrary _maps = maps ?? throw new ArgumentNullException(nameof(maps));
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    /// <summary>A song may hold this many edits.</summary>
    public const int Slots = 9;

    // CreatedAt and SavedAt came later; older files leave them out and fall back to the file's time.
    private sealed record Data(int SchemaVersion, string SourceHash, string SourceId, int Slot,
        Dictionary<string, int> Lanes, int[][] Chords, VisualizerSettings? Settings,
        DateTimeOffset? CreatedAt = null, DateTimeOffset? SavedAt = null);

    /// <summary>Folder of one slot: <c>edited/&lt;song folder&gt;/&lt;slot&gt;</c>.</summary>
    public string FolderOf(string songFolder, int slot) =>
        Path.Combine(_maps.EditedRoot, songFolder, slot.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// The name the song's edits are filed under: the folder its map sits in. An edited copy files under the song it was
    /// made from, not under its slot folder, so editing an edit saves beside it.
    /// </summary>
    public string SongFolderOf(MapCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_maps.EditedSongOf(entry) is { } song) return song;
        var nested = Path.GetDirectoryName(entry.RelativePath);
        return string.IsNullOrEmpty(nested)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(entry.RootDirectory))
            : Path.GetFileName(nested);
    }

    /// <summary>Slots this song already uses, in order.</summary>
    public IReadOnlyList<int> UsedSlots(string songFolder)
    {
        var used = new List<int>();
        for (var slot = 1; slot <= Slots; slot++)
            if (File.Exists(Path.Combine(FolderOf(songFolder, slot), "edit.json"))) used.Add(slot);
        return used;
    }

    /// <summary>What each used slot of the song holds, in slot order; a slot whose file cannot be read is left out.</summary>
    public IReadOnlyList<EditSlotInfo> SlotsOf(string songFolder)
    {
        var slots = new List<EditSlotInfo>();
        foreach (var slot in UsedSlots(songFolder))
        {
            var path = Path.Combine(FolderOf(songFolder, slot), "edit.json");
            try
            {
                var data = Load(path);
                var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                slots.Add(new EditSlotInfo(slot, data.CreatedAt ?? written, data.SavedAt ?? written, data.Lanes.Count,
                    data.Chords.Length, data.Settings?.MaximumTileCount, data.SourceHash ?? ""));
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { }
        }
        return slots;
    }

    /// <summary>Reads the edit beside a map file, or null when that copy has none.</summary>
    public (NoteEdit Edit, VisualizerSettings? Settings)? Read(MapCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = EditFileOf(entry);
        if (!File.Exists(path)) return null;
        var data = Load(path);
        var lanes = data.Lanes.ToDictionary(pair => int.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture), pair => pair.Value);
        return (new NoteEdit(data.SourceHash ?? "", lanes, data.Chords.Select(group => (IReadOnlyList<int>)group).ToArray()), data.Settings);
    }

    /// <summary>
    /// Writes an edit into a slot: the song's text is copied beside it, so the edit keeps working after the custom folder
    /// is rebuilt. Returns the path of the copied map, relative to the edited root.
    /// </summary>
    public string Write(MapCacheEntry source, string text, NoteEdit edit, VisualizerSettings settings, int slot,
        DateTimeOffset? savedAt = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(settings);
        if (slot is < 1 or > Slots) throw new ArgumentOutOfRangeException(nameof(slot));
        var songFolder = SongFolderOf(source);
        var folder = FolderOf(songFolder, slot);
        // An edit saved back into the slot it was opened from keeps its birth; any other slot starts a new edit.
        var openedSlot = _maps.SlotOf(source);
        var opened = openedSlot is not null && File.Exists(EditFileOf(source)) ? Load(EditFileOf(source)) : null;
        var now = savedAt ?? DateTimeOffset.UtcNow;
        var created = opened is not null && openedSlot == slot ? opened.CreatedAt ?? now : now;
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(source.RelativePath);
        WriteFile(Path.Combine(folder, name), text);
        var data = new Data(NoteEdit.SchemaVersion, edit.SourceHash, opened?.SourceId ?? source.Id, slot,
            edit.Lanes.ToDictionary(pair => pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), pair => pair.Value),
            edit.Chords.Select(group => group.ToArray()).ToArray(), settings, created, now);
        WriteFile(Path.Combine(folder, "edit.json"), JsonSerializer.Serialize(data, Options));
        return Path.Combine(songFolder, slot.ToString(System.Globalization.CultureInfo.InvariantCulture), name);
    }

    private static string EditFileOf(MapCacheEntry entry) =>
        Path.Combine(entry.RootDirectory, Path.GetDirectoryName(entry.RelativePath) ?? "", "edit.json");

    private static Data Load(string path)
    {
        var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path), Options);
        if (data is null || data.SchemaVersion != NoteEdit.SchemaVersion || data.Lanes is null || data.Chords is null)
            throw new InvalidDataException("Unsupported edit file; run a supported version.");
        return data;
    }

    private static void WriteFile(string path, string text)
    {
        var folder = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(folder, $"edit-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
