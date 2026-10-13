using System.Globalization;
using System.Text.Json;
using Visualizer.Application.Cache;
using Visualizer.Application.Library;
using Visualizer.Application.Maps;
using static Visualizer.Core.Localization.ConsoleText;

namespace Visualizer.Infrastructure.Storage;

/// <summary>How far a copy has got, for the progress line.</summary>
public sealed record MapCopyProgress(int Copied, int Total, string Name);

/// <summary>
/// The maps the app owns, under <c>&lt;store&gt;/maps</c>: <c>base</c> (the game's own levels), <c>custom</c> (copies of
/// the Steam folder) and <c>edited</c> (edited copies, one folder per slot). Only <c>.adofai</c> text is copied, because
/// nothing else is read; the Steam folder is never written to, and nothing outside <c>maps</c> is ever deleted.
/// </summary>
public sealed class MapLibrary(AppStorage storage)
{
    private readonly AppStorage _storage = storage ?? throw new ArgumentNullException(nameof(storage));

    public string Root => Path.Combine(_storage.Root, "maps");
    public string BaseRoot => Path.Combine(Root, "base");
    public string CustomRoot => Path.Combine(Root, "custom");
    public string EditedRoot => Path.Combine(Root, "edited");
    /// <summary>The three roots in list order: base, custom, edited.</summary>
    public IReadOnlyList<string> Roots => [BaseRoot, CustomRoot, EditedRoot];
    /// <summary>Where the edited song removed last waits for restore. Never scanned, so it is out of the song list.</summary>
    public string RemovedRoot => Path.Combine(Root, "removed");
    private string HeldSlot => Path.Combine(RemovedRoot, "slot");
    private string HeldFile => Path.Combine(RemovedRoot, "removed.json");

    /// <summary>What the removed song was: its name in the list, its song folder and its slot.</summary>
    private sealed record Held(string Name, string Song, int Slot);

    /// <summary>Which of the three folders this song is in, or <see cref="MapKind.External"/> when it is in none.</summary>
    public MapKind KindOf(MapCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Within(entry.RootDirectory, BaseRoot)) return MapKind.Base;
        if (Within(entry.RootDirectory, CustomRoot)) return MapKind.Custom;
        if (Within(entry.RootDirectory, EditedRoot)) return MapKind.Edited;
        return MapKind.External;
    }

    /// <summary>The slot number 1..9 of an edited copy (<c>edited/&lt;song&gt;/&lt;slot&gt;/&lt;file&gt;</c>), or null for the rest.</summary>
    public int? SlotOf(MapCacheEntry entry)
    {
        if (KindOf(entry) != MapKind.Edited) return null;
        // The slot is the folder the file sits in, so a song folder that happens to be named "3" is not mistaken for it.
        var parts = entry.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length >= 3 && parts[^2] is [>= '1' and <= '9'] slot ? slot[0] - '0' : null;
    }

    /// <summary>The song folder an edited copy belongs to (<c>edited/&lt;song&gt;/…</c>), or null for the rest.</summary>
    public string? EditedSongOf(MapCacheEntry entry) => SlotOf(entry) is null ? null
        : entry.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[^3];

    /// <summary>The whole slot folder of an edited copy (level copy and edit file), or null for the rest.</summary>
    private string? SlotFolderOf(MapCacheEntry entry) => SlotOf(entry) is { } slot && EditedSongOf(entry) is { } song
        ? Path.Combine(EditedRoot, song, slot.ToString(CultureInfo.InvariantCulture)) : null;

    /// <summary>
    /// Copies levels the custom folder does not have yet, or whose text changed. Songs no longer in the Steam folder are
    /// left alone; <see cref="RebuildCustomAsync"/> is what removes those.
    /// </summary>
    public async Task<int> SyncCustomAsync(IReadOnlyMapDirectory source, IProgress<MapCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var levels = await LevelsOf(source, cancellationToken).ConfigureAwait(false);
        var copied = 0;
        for (var i = 0; i < levels.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new MapCopyProgress(i, levels.Count, levels[i]));
            var text = (await source.ReadAsync(levels[i], cancellationToken).ConfigureAwait(false)).Text;
            var target = Path.Combine(CustomRoot, levels[i]);
            if (File.Exists(target) && string.Equals(File.ReadAllText(target), text, StringComparison.Ordinal)) continue;
            Write(target, text);
            copied++;
        }
        progress?.Report(new MapCopyProgress(levels.Count, levels.Count, ""));
        return copied;
    }

    /// <summary>
    /// Rebuilds the custom folder so it matches the Steam folder exactly, which is how a song that was unsubscribed leaves
    /// the list. The new set is built beside the old one and swapped in at the end, so a failure or a cancel keeps what was
    /// already there.
    /// </summary>
    public async Task<int> RebuildCustomAsync(IReadOnlyMapDirectory source, IProgress<MapCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var levels = await LevelsOf(source, cancellationToken).ConfigureAwait(false);
        var building = CustomRoot + ".new";
        var previous = CustomRoot + ".old";
        foreach (var leftover in new[] { building, previous })
            if (Directory.Exists(leftover)) Delete(leftover);
        try
        {
            for (var i = 0; i < levels.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new MapCopyProgress(i, levels.Count, levels[i]));
                Write(Path.Combine(building, levels[i]), (await source.ReadAsync(levels[i], cancellationToken).ConfigureAwait(false)).Text);
            }
        }
        catch
        {
            // Nothing has been swapped yet, so dropping the half-built folder leaves the old songs untouched.
            if (Directory.Exists(building)) Delete(building);
            throw;
        }
        Directory.CreateDirectory(Root);
        if (!Directory.Exists(building)) Directory.CreateDirectory(building);
        // Renames on one drive: there is no moment where the custom folder is half finished.
        if (Directory.Exists(CustomRoot)) Directory.Move(CustomRoot, previous);
        Directory.Move(building, CustomRoot);
        if (Directory.Exists(previous)) Delete(previous);
        progress?.Report(new MapCopyProgress(levels.Count, levels.Count, ""));
        return levels.Count;
    }

    /// <summary>
    /// Deletes a copy the app owns. An edited copy goes with its whole slot folder, edit file included, so the slot is free
    /// again; base and custom songs lose their level file.
    /// Anything else is refused.
    /// </summary>
    public void Remove(MapCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (KindOf(entry) == MapKind.External) throw new InvalidOperationException(T("OnlyAppSongsRemoved"));
        var path = Path.Combine(entry.RootDirectory, entry.RelativePath);
        CheckInside(path);
        if (SlotFolderOf(entry) is { } slot)
        {
            if (Directory.Exists(slot)) Delete(slot);
            path = slot;
        }
        else if (File.Exists(path)) File.Delete(path);
        PruneAbove(path);
    }

    /// <summary>
    /// Takes an edited song out of the list but keeps it for <see cref="Restore"/>. Only the newest removal is kept: holding
    /// another deletes the one held before (user rule).
    /// </summary>
    public void Hold(MapCacheEntry entry, string name)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (SlotFolderOf(entry) is not { } folder || !Directory.Exists(folder))
            throw new InvalidOperationException(T("OnlyEditsKept"));
        CheckInside(folder);
        CheckInside(RemovedRoot);
        if (Directory.Exists(RemovedRoot)) Delete(RemovedRoot);
        Directory.CreateDirectory(RemovedRoot);
        Directory.Move(folder, HeldSlot);
        File.WriteAllText(HeldFile, JsonSerializer.Serialize(new Held(name ?? "", EditedSongOf(entry)!, SlotOf(entry)!.Value)));
        PruneAbove(folder);
    }

    /// <summary>The list name of the song kept for restore, or null when nothing is kept.</summary>
    public string? HeldName => ReadHeld()?.Name;

    /// <summary>
    /// Puts the kept song back into its own slot, or the first free one when that slot was used since. Returns the slot.
    /// Nothing is kept afterwards, so a second restore finds nothing.
    /// </summary>
    public int Restore()
    {
        var held = ReadHeld() ?? throw new InvalidOperationException(T("NothingKept"));
        string Folder(int slot) => Path.Combine(EditedRoot, held.Song, slot.ToString(CultureInfo.InvariantCulture));
        var free = new[] { held.Slot }.Concat(Enumerable.Range(1, 9)).Where(slot => slot is >= 1 and <= 9)
            .Cast<int?>().FirstOrDefault(slot => !Directory.Exists(Folder(slot!.Value)));
        if (free is not { } target) throw new IOException(T("EverySlotUsed"));
        var destination = Folder(target);
        CheckInside(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(HeldSlot, destination);
        Delete(RemovedRoot);
        return target;
    }

    private Held? ReadHeld()
    {
        if (!File.Exists(HeldFile) || !Directory.Exists(HeldSlot)) return null;
        try { return JsonSerializer.Deserialize<Held>(File.ReadAllText(HeldFile)); }
        catch (JsonException) { return null; }
    }

    /// <summary>Folders a song leaves behind go too, up to (but never including) the three roots.</summary>
    private void PruneAbove(string path)
    {
        for (var folder = Path.GetDirectoryName(path); folder is not null && Roots.All(root => !Same(folder, root)) && Within(folder, Root); )
        {
            if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any()) break;
            Directory.Delete(folder);
            folder = Path.GetDirectoryName(folder);
        }
    }

    private static async Task<IReadOnlyList<string>> LevelsOf(IReadOnlyMapDirectory source, CancellationToken cancellationToken)
    {
        var discovery = await source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return MapFileRules.SelectLevels(discovery.RelativePaths);
    }

    private void Write(string target, string text)
    {
        CheckInside(target);
        var folder = Path.GetDirectoryName(target) ?? Root;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $"map-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, text);
        File.Move(temp, target, overwrite: true);
    }

    private void Delete(string folder)
    {
        CheckInside(folder);
        Directory.Delete(folder, recursive: true);
    }

    private void CheckInside(string path)
    {
        // ".new" and ".old" sit beside the custom folder, so the test is against the maps root.
        if (!Within(path, Root)) throw new IOException(T("LibraryOwnFolder"));
        RejectReparseAncestors(path);
    }

    private static bool Same(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

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
                    throw new IOException(T("LibraryReparse"));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
