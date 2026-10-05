using System.Text.Json;
using Visualizer.Application.Library;

namespace Visualizer.Infrastructure.Storage;

/// <summary>
/// The song list choices (hidden, display name, manual order) as <c>songs.json</c> in the app store. They stay out of the
/// settings JSON because every machine has different songs, and nothing here is written to a map folder.
/// </summary>
public sealed class WorkspaceSongPreferences(AppStorage storage) : ISongPreferenceStore
{
    private readonly AppStorage _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private sealed record Data(int SchemaVersion, SongPreference[] Songs);

    private string Path => System.IO.Path.Combine(_storage.Root, "songs.json");

    public IReadOnlyList<SongPreference> Read()
    {
        if (!File.Exists(Path)) return Array.Empty<SongPreference>();
        var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(Path), Options);
        if (data is null || data.SchemaVersion != 1 || data.Songs is null)
            throw new InvalidDataException("Unsupported song list file; run a supported version.");
        // An entry without an id cannot be matched to a song, so the file is treated as damaged rather than half-used.
        if (data.Songs.Any(song => song is null || string.IsNullOrWhiteSpace(song.Id))) throw new InvalidDataException("Invalid song list entry.");
        return Array.AsReadOnly(data.Songs);
    }

    public void Write(IReadOnlyList<SongPreference> preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        // Only songs that carry a choice are kept, so the file stays small and a reset simply empties it.
        // A song the user never decided anything about is not written; "show this one" is a decision, so it is.
        var kept = preferences.Where(song => song.Hidden is not null || song.DisplayName is not null || song.Order >= 0).ToArray();
        Directory.CreateDirectory(_storage.Root);
        var temp = System.IO.Path.Combine(_storage.Root, $"songs-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(new Data(1, kept), Options));
        File.Move(temp, Path, overwrite: true);
    }
}
