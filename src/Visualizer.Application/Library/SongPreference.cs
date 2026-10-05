namespace Visualizer.Application.Library;

/// <summary>
/// What the song list holds per song. The id is the cache id (a hash of the map path), so the choices survive a re-sync.
/// <paramref name="Hidden"/> null means the user has not decided, so the automatic choice applies (a level beside
/// main.adofai starts hidden). <paramref name="DisplayName"/> null keeps the name read from the map, and
/// <paramref name="Order"/> -1 means no manual place.
/// </summary>
public sealed record SongPreference(string Id, bool? Hidden = null, string? DisplayName = null, int Order = -1);

/// <summary>App-owned store for the song list choices. Map files are never touched.</summary>
public interface ISongPreferenceStore
{
    IReadOnlyList<SongPreference> Read();
    void Write(IReadOnlyList<SongPreference> preferences);
}
