using System.Text.Json;
using System.Text.Json.Nodes;
using Visualizer.Application.Maps;

namespace Visualizer.Infrastructure.Storage;

/// <summary>What a move did, for the status line. <see cref="Moved"/> false with a reason means data was left behind.</summary>
public sealed record StoreMoveResult(bool Moved, string? LeftBehind = null);

/// <summary>
/// Moves the store a copy kept inside its own repository (<c>.local/app-data</c>) to the shared default, once, so that
/// every copy on a PC sees the same songs, settings and notes (user request). The old folder is copied and kept as it
/// was. A song id is a hash of where its map lies, so each place that keeps one is rewritten for the new place: the
/// cache, the song list choices, the console ids, the marks and every edit.
/// </summary>
public static class StoreMove
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Copies <paramref name="from"/> into <paramref name="to"/> when the first holds data and the second holds no file
    /// at all; anything else changes nothing. A move that fails part way leaves <paramref name="to"/> as it was.
    /// </summary>
    public static StoreMoveResult Run(string from, string to)
    {
        from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(to));
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase) || !HoldsData(from)) return new(false);
        if (HoldsAnyFile(to)) return new(false, from);

        var pointer = Path.Combine(from, "data-root.txt");
        var chosen = File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : "";
        // Built beside the target and renamed at the end, so a failure never leaves half a store behind.
        var building = to + ".moving";
        if (Directory.Exists(building)) Directory.Delete(building, recursive: true);
        Directory.CreateDirectory(building);
        try
        {
            if (chosen.Length > 0)
                // The data sits in a folder the user chose, which is not moving: the new default only points there.
                File.Copy(pointer, Path.Combine(building, "data-root.txt"));
            else
            {
                Copy(new DirectoryInfo(from), building);
                var ids = RewriteCache(building, from, to);
                RewriteIds(building, ids);
            }
            if (Directory.Exists(to)) Directory.Delete(to, recursive: true); // Holds no file, checked above.
            Directory.Move(building, to);
        }
        catch
        {
            if (Directory.Exists(building)) Directory.Delete(building, recursive: true);
            throw;
        }
        return new(true);
    }

    private static bool HoldsData(string root) =>
        new[] { "settings.json", "data-root.txt", Path.Combine("map-cache", "library.json") }.Any(name => File.Exists(Path.Combine(root, name)));

    private static bool HoldsAnyFile(string root) =>
        Directory.Exists(root) && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any();

    /// <summary>Copies files and folders, never following a junction or link out of the store.</summary>
    private static void Copy(DirectoryInfo source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var destination = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo folder) Copy(folder, destination);
            else ((FileInfo)entry).CopyTo(destination);
        }
    }

    /// <summary>Moves each cache entry kept under the old store to the new one and returns the ids that changed.</summary>
    private static Dictionary<string, string> RewriteCache(string store, string from, string to)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.Combine(store, "map-cache", "library.json");
        if (!File.Exists(path)) return ids;
        var data = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("The song cache is empty.");
        foreach (var entry in data["Entries"]?.AsArray() ?? [])
        {
            if (entry?["RootDirectory"]?.GetValue<string>() is not { } root || entry["RelativePath"]?.GetValue<string>() is not { } relative
                || entry["Id"]?.GetValue<string>() is not { } id) continue;
            if (!Under(root, from)) continue; // Not ours; the app drops such entries itself.
            var moved = to + root[from.Length..];
            var newId = MapSyncService.IdOf(moved, relative);
            entry["RootDirectory"] = moved;
            entry["Id"] = newId;
            ids[id] = newId;
        }
        File.WriteAllText(path, data.ToJsonString(Indented));
        return ids;
    }

    /// <summary>Every other file that keeps a song id, rewritten with the new ones.</summary>
    private static void RewriteIds(string store, IReadOnlyDictionary<string, string> ids)
    {
        if (ids.Count == 0) return;
        string Map(string id) => ids.TryGetValue(id, out var moved) ? moved : id;
        Rewrite(Path.Combine(store, "songs.json"), data =>
        {
            foreach (var song in data["Songs"]?.AsArray() ?? [])
                if (song?["Id"]?.GetValue<string>() is { } id) song["Id"] = Map(id);
        });
        Rewrite(Path.Combine(store, "song-ids.json"), data =>
        {
            if (data["Ids"] is not JsonObject book) return;
            var pairs = book.Select(pair => (Key: pair.Key, Value: pair.Value?.DeepClone())).ToArray();
            book.Clear();
            foreach (var (key, value) in pairs) book[Map(key)] = value;
        });
        Rewrite(Path.Combine(store, "song-marks.json"), data =>
        {
            if (data["New"] is JsonArray marks)
                for (var i = 0; i < marks.Count; i++)
                    if (marks[i]?.GetValue<string>() is { } id) marks[i] = Map(id);
            if (data["Last"]?.GetValue<string>() is { } last) data["Last"] = Map(last);
        });
        var edited = Path.Combine(store, "maps", "edited");
        if (!Directory.Exists(edited)) return;
        foreach (var edit in Directory.EnumerateFiles(edited, "edit.json", SearchOption.AllDirectories))
            Rewrite(edit, data =>
            {
                if (data["SourceId"]?.GetValue<string>() is { } id) data["SourceId"] = Map(id);
            });
    }

    private static void Rewrite(string path, Action<JsonNode> change)
    {
        if (!File.Exists(path)) return;
        var data = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException($"{Path.GetFileName(path)} is empty.");
        change(data);
        File.WriteAllText(path, data.ToJsonString(Indented));
    }

    private static bool Under(string path, string parent) =>
        string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
