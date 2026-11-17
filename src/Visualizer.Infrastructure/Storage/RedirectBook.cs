using System.Text.Json;
using static Visualizer.Core.Localization.ConsoleText;

namespace Visualizer.Infrastructure.Storage;

/// <summary>
/// The console's redirects, as <c>redirects.json</c> in the app store (user request): the song link auto loads in place of
/// the one the game plays. Keyed by song family, as pins are, since the game plays an original's map for every edit of it
/// (user decision); the song put in its place is kept by its eight-character console id, which names one song, an edit's
/// slot included. Kept out of the settings JSON, because every machine has different songs.
/// </summary>
public sealed class RedirectBook(AppStorage storage)
{
    private readonly AppStorage _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private sealed record Data(int SchemaVersion, Dictionary<string, string> Redirects);

    private string Path => System.IO.Path.Combine(_storage.Root, "redirects.json");

    public IReadOnlyDictionary<string, string> Read()
    {
        if (!File.Exists(Path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(Path), Options);
        if (data is null || data.SchemaVersion != 1 || data.Redirects is null
            || data.Redirects.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            throw new InvalidDataException(T("UnsupportedRedirectFile"));
        return new Dictionary<string, string>(data.Redirects, StringComparer.Ordinal);
    }

    /// <summary>Writes every redirect through a temp file, replacing what was kept.</summary>
    public void Write(IReadOnlyDictionary<string, string> redirects)
    {
        ArgumentNullException.ThrowIfNull(redirects);
        Directory.CreateDirectory(_storage.Root);
        var kept = redirects.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var temp = System.IO.Path.Combine(_storage.Root, $"redirects-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(new Data(1, kept), Options));
        File.Move(temp, Path, overwrite: true);
    }
}
