using Visualizer.Application.Maps;
using Visualizer.Core.Maps;

namespace Visualizer.Application.Cache;

/// <summary>
/// A synced level. <paramref name="Info"/> is null for caches written before song info was read.
/// <paramref name="ForcedFormatVersion"/> is the verified format load -force read an Unsupported level as; null otherwise.
/// </summary>
public sealed record MapCacheEntry(string Id, string RootDirectory, string RelativePath, string SongName,
    MapParseStatus Status, ParsedTimeline? Timeline, IReadOnlyList<string> Warnings,
    string SourceHash, string ParserVersion, MapSongInfo? Info = null, int? ForcedFormatVersion = null)
{
    public string Label => $"{SongName} · {Status} · {RelativePath}";
    public bool IsForced => ForcedFormatVersion is not null;
    /// <summary>Display only, never cached: the mark the song list shows after the title (new, forced, recently).</summary>
    public string? Badge { get; init; }
}

public interface IReadOnlyMapCache
{
    Task<IReadOnlyList<MapCacheEntry>> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Writes only the app-owned cache, never map sources.</summary>
public interface IMapCacheWriter
{
    Task ReplaceAsync(IReadOnlyList<MapCacheEntry> entries, CancellationToken cancellationToken = default);
    void ValidateMapRoot(string rootDirectory);
}
