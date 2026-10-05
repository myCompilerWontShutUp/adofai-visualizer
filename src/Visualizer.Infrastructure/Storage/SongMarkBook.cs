using System.Text.Json;

namespace Visualizer.Infrastructure.Storage;

/// <summary>What the song list marks and what was played last: cache ids, not song files.</summary>
/// <param name="New">Songs a sync added that have not been loaded since.</param>
/// <param name="Last">The song loaded last, offered again when the app starts; null when there is none.</param>
public sealed record SongMarks(IReadOnlyCollection<string> New, string? Last = null);

/// <summary>
/// The song list marks as <c>song-marks.json</c> in the app store. Kept apart from songs.json, which holds only what the
/// user chose, and out of the settings JSON, because every machine has different songs.
/// </summary>
public sealed class SongMarkBook(AppStorage storage)
{
    private readonly AppStorage _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    // Last is optional so a file written before the last song was remembered still loads (as null).
    private sealed record Data(int SchemaVersion, string[] New, string? Last = null);

    private string Path => System.IO.Path.Combine(_storage.Root, "song-marks.json");

    public SongMarks Read()
    {
        if (!File.Exists(Path)) return new SongMarks(Array.Empty<string>());
        var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(Path), Options);
        if (data is null || data.SchemaVersion != 1 || data.New is null || data.New.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Unsupported song mark file; run a supported version.");
        return new SongMarks(Array.AsReadOnly(data.New), string.IsNullOrWhiteSpace(data.Last) ? null : data.Last);
    }

    public void Write(SongMarks marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        Directory.CreateDirectory(_storage.Root);
        var temp = System.IO.Path.Combine(_storage.Root, $"song-marks-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(new Data(1, marks.New.Order(StringComparer.Ordinal).ToArray(), marks.Last), Options));
        File.Move(temp, Path, overwrite: true);
    }
}
