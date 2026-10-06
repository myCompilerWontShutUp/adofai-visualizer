using System.Text.Json;

namespace Visualizer.Infrastructure.Storage;

/// <summary>
/// The tiles pinned for the console's go, as <c>pins.json</c> in the app store. Keyed by song family: an original and every
/// edit of it share one list (user rule), whichever of them a pin was set on. Tiles are kept in order, as checkpoints are.
/// Kept out of the settings JSON, because every machine has different songs.
/// </summary>
public sealed class PinBook(AppStorage storage)
{
    private readonly AppStorage _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private sealed record Data(int SchemaVersion, Dictionary<string, int[]> Pins);

    private string Path => System.IO.Path.Combine(_storage.Root, "pins.json");

    public IReadOnlyDictionary<string, IReadOnlyList<int>> Read()
    {
        if (!File.Exists(Path)) return new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(Path), Options);
        if (data is null || data.SchemaVersion != 1 || data.Pins is null
            || data.Pins.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || pair.Value.Any(tile => tile < 1)))
            throw new InvalidDataException("Unsupported pin file; run a supported version.");
        return data.Pins.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<int>)pair.Value.Distinct().Order().ToArray(), StringComparer.Ordinal);
    }

    /// <summary>Writes every family's pins through a temp file; a family left with none is dropped.</summary>
    public void Write(IReadOnlyDictionary<string, IReadOnlyList<int>> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        Directory.CreateDirectory(_storage.Root);
        var kept = pins.Where(pair => pair.Value.Count > 0).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Distinct().Order().ToArray(), StringComparer.Ordinal);
        var temp = System.IO.Path.Combine(_storage.Root, $"pins-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(new Data(1, kept), Options));
        File.Move(temp, Path, overwrite: true);
    }
}
