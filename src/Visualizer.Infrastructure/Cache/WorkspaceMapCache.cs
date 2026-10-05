using System.Text.Json;
using Visualizer.Application.Cache;
using Visualizer.Application.Maps;
using Visualizer.Core.Maps;

namespace Visualizer.Infrastructure.Cache;

/// <summary>App-owned workspace cache; source files are never opened for writing.</summary>
public sealed class WorkspaceMapCache : IReadOnlyMapCache, IMapCacheWriter
{
    private readonly string _storeRoot; // Boundary the cache must stay inside: the repository root, or the app store root.
    public string StorageDirectory { get; }
    private string CachePath => Path.Combine(StorageDirectory, "library.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    // HitFloors, HitKinds and Sections are optional so caches written before them still load (as null).
    private sealed record TimelineData(double[] HitTimesMs, TimelineCheckpoint[] Checkpoints, bool CheckpointsVerified, int[]? HitFloors = null,
        HitKind[]? HitKinds = null, TimelineSection[]? Sections = null);
    // ForcedFormatVersion is optional so caches written before load -force still load (as null).
    private sealed record EntryData(string Id, string RootDirectory, string RelativePath, string SongName,
        MapParseStatus Status, TimelineData? Timeline, string[] Warnings, string SourceHash, string ParserVersion, MapSongInfo? Info = null,
        int? ForcedFormatVersion = null);
    private sealed record CacheData(int SchemaVersion, EntryData[] Entries);

    public WorkspaceMapCache(string repositoryRoot)
    {
        _storeRoot = Path.GetFullPath(repositoryRoot);
        if (!File.Exists(Path.Combine(_storeRoot, "AGENTS.md")) || !File.Exists(Path.Combine(_storeRoot, "Visualizer.sln")))
            throw new ArgumentException("Repository root with AGENTS.md and Visualizer.sln is required.");
        StorageDirectory = Path.Combine(_storeRoot, ".local", "app-data", "map-cache");
        CheckStorage(); // No directory is created by construction or cache reads.
    }

    /// <summary>Cache inside the app store (<see cref="Storage.AppStorage"/>), which may sit outside the repository.</summary>
    public WorkspaceMapCache(Storage.AppStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storeRoot = Path.GetFullPath(storage.Root);
        StorageDirectory = Path.GetFullPath(storage.CacheDirectory);
        CheckStorage();
    }

    public static string FindRepositoryRoot(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) && File.Exists(Path.Combine(dir.FullName, "Visualizer.sln")))
                return dir.FullName;
        throw new IOException("프로젝트 루트를 찾지 못했습니다. 외부 저장소는 자동 생성하지 않습니다.");
    }

    public void ValidateMapRoot(string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        RejectReparseAncestors(root);
        CheckStorage();
        if (Within(root, StorageDirectory) || Within(StorageDirectory, root))
            throw new IOException("맵 폴더와 프로그램 저장소는 같거나 서로의 하위 폴더일 수 없습니다.");
    }

    public async Task<IReadOnlyList<MapCacheEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        CheckStorage();
        if (!File.Exists(CachePath)) return Array.Empty<MapCacheEntry>();
        var data = JsonSerializer.Deserialize<CacheData>(await File.ReadAllTextAsync(CachePath, cancellationToken).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("Empty cache.");
        if (data.SchemaVersion != 1 || data.Entries is null) throw new InvalidDataException("Unsupported cache schema; run a supported version.");
        var entries = new List<MapCacheEntry>();
        foreach (var entry in data.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.RootDirectory)
                || string.IsNullOrWhiteSpace(entry.RelativePath) || entry.Warnings is null || !Enum.IsDefined(entry.Status))
                throw new InvalidDataException("Invalid cache entry.");
            ValidateMapRoot(entry.RootDirectory);
            var timeline = entry.Timeline is { } t
                ? new ParsedTimeline(t.HitTimesMs, t.Checkpoints, t.CheckpointsVerified, t.HitFloors, t.HitKinds, t.Sections) : null;
            if ((entry.Status == MapParseStatus.Supported) != (timeline is not null))
                throw new InvalidDataException("Cache support status and timeline disagree.");
            entries.Add(new(entry.Id, entry.RootDirectory, entry.RelativePath, entry.SongName,
                entry.Status, timeline, Array.AsReadOnly(entry.Warnings), entry.SourceHash, entry.ParserVersion, entry.Info,
                entry.Status == MapParseStatus.Supported ? entry.ForcedFormatVersion : null));
        }
        if (entries.Select(e => e.Id).Distinct().Count() != entries.Count) throw new InvalidDataException("Duplicate cache IDs.");
        return entries.AsReadOnly();
    }

    public async Task ReplaceAsync(IReadOnlyList<MapCacheEntry> entries, CancellationToken cancellationToken = default)
    {
        foreach (var entry in entries) ValidateMapRoot(entry.RootDirectory);
        var data = new CacheData(1, entries.Select(e => new EntryData(e.Id, e.RootDirectory, e.RelativePath, e.SongName,
            e.Status, e.Timeline is { } t ? new TimelineData(t.HitTimesMs.ToArray(), t.Checkpoints.ToArray(), t.CheckpointsVerified, t.HitFloors?.ToArray(),
                t.HitKinds?.ToArray(), t.Sections.Count == 0 ? null : t.Sections.ToArray()) : null,
            e.Warnings.ToArray(), e.SourceHash, e.ParserVersion, e.Info, e.ForcedFormatVersion)).ToArray());
        var json = JsonSerializer.Serialize(data, JsonOptions);
        cancellationToken.ThrowIfCancellationRequested();
        CheckStorage();
        Directory.CreateDirectory(StorageDirectory);
        CheckStorage();
        var tempPath = Path.Combine(StorageDirectory, $"library-{Guid.NewGuid():N}.tmp");
        // New app-owned file, then atomic replace. A failed write leaves only an internal temp file.
        await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var text = new StreamWriter(stream))
            await text.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        CheckStorage();
        RejectReparseAncestors(tempPath);
        File.Move(tempPath, CachePath, overwrite: true);
    }

    private void CheckStorage()
    {
        if (!Within(StorageDirectory, _storeRoot)) throw new IOException("Storage must remain within the app store root.");
        RejectReparseAncestors(CachePath);
    }

    private static bool Within(string path, string parent)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparseAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse point paths are not supported for cache/map roots.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
