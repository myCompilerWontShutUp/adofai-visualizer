using System.Text.Json;

namespace Visualizer.Infrastructure.Storage;

/// <summary>
/// The short ids as <c>song-ids.json</c> in the app store: cache id → eight characters. Kept out of the settings JSON, like
/// the song list, because every machine has different songs.
/// </summary>
public sealed class SongIdBook(AppStorage storage)
{
    private readonly AppStorage _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private sealed record Data(int SchemaVersion, Dictionary<string, string> Ids);

    private string Path => System.IO.Path.Combine(_storage.Root, "song-ids.json");

    public IReadOnlyDictionary<string, string> Read()
    {
        if (!File.Exists(Path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(Path), Options);
        if (data is null || data.SchemaVersion != 1 || data.Ids is null)
            throw new InvalidDataException("Unsupported song id file; run a supported version.");
        return new Dictionary<string, string>(data.Ids, StringComparer.Ordinal);
    }

    public void Write(IReadOnlyDictionary<string, string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Directory.CreateDirectory(_storage.Root);
        var temp = System.IO.Path.Combine(_storage.Root, $"song-ids-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(new Data(1, new Dictionary<string, string>(ids, StringComparer.Ordinal)), Options));
        File.Move(temp, Path, overwrite: true);
    }
}
