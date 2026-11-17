using System.Security.Cryptography;
using System.Text;
using Visualizer.Application.Cache;

namespace Visualizer.Application.Maps;

public sealed record MapSyncReport(IReadOnlyList<MapCacheEntry> Entries, IReadOnlyList<string> Warnings, int ScannedCount);

/// <summary>How far a scan has got: levels parsed out of the total, and the last song's name. Shown as a percentage.</summary>
public sealed record MapScanProgress(int Parsed, int Total, string Name);

/// <summary>What load -force made of one song, and the cache after it (unchanged when no format read the song).</summary>
public sealed record MapForceReport(MapParseResult Result, IReadOnlyList<MapCacheEntry> Entries);

public sealed class MapSyncService(IReadOnlyMapCache cache, IMapCacheWriter writer, IMapTimelineParser parser)
{
    /// <summary>
    /// Brings the cache up to date with one folder. A level already cached from the same text by this parser keeps its
    /// entry: parsing is the slow part, and a sync is for the songs that came in or changed (user rule). With
    /// <paramref name="reparse"/> every level is read again, which is what sync rebuild does.
    /// </summary>
    public async Task<MapSyncReport> SyncAsync(IReadOnlyMapDirectory source, IProgress<MapScanProgress>? progress = null,
        CancellationToken cancellationToken = default, bool reparse = false)
    {
        writer.ValidateMapRoot(source.RootDirectory);
        var existing = await cache.ReadAsync(cancellationToken).ConfigureAwait(false);
        var before = existing.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var discovery = await source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        // Only remove absent entries when the whole selected root was scanned successfully.
        var entries = existing.Where(e => discovery.Warnings.Count > 0
            || !string.Equals(e.RootDirectory, source.RootDirectory, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => e.Id, StringComparer.Ordinal);
        // An editor auto-save (backup.adofai) beside the real level is not a separate song; skipping it here
        // also removes an entry cached by an earlier sync, because entries of a fully scanned root are rebuilt.
        // Parsing is the slow part of a sync, so it is counted out for the progress the app and the console show.
        var levels = MapFileRules.SelectLevels(discovery.RelativePaths).ToArray();
        var parsed = 0;
        progress?.Report(new MapScanProgress(0, levels.Length, ""));
        foreach (var path in levels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = IdOf(source.RootDirectory, path);
            MapCacheEntry entry;
            try
            {
                var raw = await source.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                var hash = HashOf(raw.Text);
                before.TryGetValue(id, out var old);
                // The same text read by the same parser gives the same entry (a forced one stays forced), so it is kept.
                if (!reparse && old is not null && old.SourceHash == hash && old.ParserVersion == parser.Version
                    && string.Equals(old.RootDirectory, source.RootDirectory, StringComparison.OrdinalIgnoreCase))
                    entry = old;
                else
                {
                    var result = parser.Parse(raw);
                    // A song forced before stays forced while its file is the same, so a sync does not quietly take it away.
                    if (result.Status == MapParseStatus.Unsupported && old?.ForcedFormatVersion is not null
                        && old.SourceHash == hash && parser.ParseForced(raw) is { Status: MapParseStatus.Supported } forced)
                        result = forced;
                    entry = EntryOf(id, source.RootDirectory, path, result, raw.Text);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
            {
                entry = new(id, source.RootDirectory, path, Path.GetFileNameWithoutExtension(path) + " (파일명)",
                    MapParseStatus.Error, null, new[] { error.Message }, "", parser.Version);
            }
            entries[id] = entry;
            progress?.Report(new MapScanProgress(++parsed, levels.Length, entry.SongName));
        }
        var snapshot = Array.AsReadOnly(entries.Values.OrderBy(e => e.SongName, StringComparer.OrdinalIgnoreCase).ToArray());
        // Nothing came in, changed or left: the cache file already says this, and writing it again is the other slow part.
        var kept = existing.ToHashSet(ReferenceEqualityComparer.Instance);
        if (snapshot.Count != existing.Count || !snapshot.All(kept.Contains))
            await writer.ReplaceAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return new MapSyncReport(snapshot, discovery.Warnings, discovery.RelativePaths.Count);
    }

    /// <summary>
    /// load -force (user rule): reads one Unsupported song the forced way (<see cref="IMapTimelineParser.ParseForced"/>) and,
    /// when a format reads it, keeps that in the cache in its place. Only the app's cache is written; the map is only read.
    /// </summary>
    public async Task<MapForceReport> ForceAsync(IReadOnlyMapDirectory source, MapCacheEntry song, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (song.Status != MapParseStatus.Unsupported) throw new ArgumentException("Only an unsupported song can be forced.", nameof(song));
        writer.ValidateMapRoot(source.RootDirectory);
        var raw = await source.ReadAsync(song.RelativePath, cancellationToken).ConfigureAwait(false);
        var result = parser.ParseForced(raw);
        var existing = await cache.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (result.Status != MapParseStatus.Supported) return new MapForceReport(result, existing);
        var forced = EntryOf(song.Id, song.RootDirectory, song.RelativePath, result, raw.Text);
        var entries = Array.AsReadOnly(existing.Select(entry => entry.Id == song.Id ? forced : entry).ToArray());
        await writer.ReplaceAsync(entries, cancellationToken).ConfigureAwait(false);
        return new MapForceReport(result, entries);
    }

    /// <summary>A song's cache id: a hash of where its map lies, so moving the store means new ids (see StoreMove).</summary>
    public static string IdOf(string root, string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.Combine(root, path).ToUpperInvariant())));

    /// <summary>The hash a cache entry keeps of its map's text, to tell a changed file from the one that was synced.</summary>
    public static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private MapCacheEntry EntryOf(string id, string root, string path, MapParseResult result, string text)
    {
        if (result.Status == MapParseStatus.Supported && result.Timeline is null)
            throw new InvalidDataException("Parser returned Supported without a timeline.");
        return new(id, root, path, result.SongName ?? Path.GetFileNameWithoutExtension(path) + " (파일명)",
            result.Status, result.Status == MapParseStatus.Supported ? result.Timeline : null,
            result.Warnings.ToArray(), HashOf(text), parser.Version, result.Info,
            result.Status == MapParseStatus.Supported ? result.ForcedFormatVersion : null);
    }
}
