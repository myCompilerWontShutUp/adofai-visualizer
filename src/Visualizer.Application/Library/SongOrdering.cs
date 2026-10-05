using Visualizer.Application.Cache;
using Visualizer.Core.Settings;

namespace Visualizer.Application.Library;

/// <summary>
/// Display order of the song list. Nothing here touches notes, timing or the cache: it only decides the order songs are
/// listed in, which the main window arrows follow as well.
/// </summary>
public static class SongOrdering
{
    /// <summary>Sort keys in the order the settings list shows them.</summary>
    public static IReadOnlyList<SongSortField> Fields { get; } = Enum.GetValues<SongSortField>();

    /// <summary>The folders a group name stands for, or null for "All" (and for anything unreadable).</summary>
    public static IReadOnlyList<MapKind>? GroupKinds(string? name) =>
        Enum.TryParse<SongGroupField>(name, ignoreCase: true, out var group)
            ? group switch
            {
                SongGroupField.Base => [MapKind.Base],
                SongGroupField.Custom => [MapKind.Custom],
                SongGroupField.Edited => [MapKind.Edited],
                SongGroupField.BaseCustom => [MapKind.Base, MapKind.Custom],
                SongGroupField.CustomEdited => [MapKind.Custom, MapKind.Edited],
                SongGroupField.BaseEdited => [MapKind.Base, MapKind.Edited],
                _ => null
            }
            : null;

    public static SongSortField Parse(string? name) =>
        Enum.TryParse<SongSortField>(name, ignoreCase: true, out var key) ? key : SongSortField.Difficulty;

    /// <summary>
    /// Orders <paramref name="entries"/> (already filtered for hidden songs). <paramref name="highFirst"/> puts the larger
    /// value, the later date or the later letters on top. Songs without the value are always last, whichever way it runs.
    /// <paramref name="fileTimes"/> supplies the map file dates; without it those two keys fall back to the sync order.
    /// </summary>
    public static IReadOnlyList<MapCacheEntry> Sort(IReadOnlyList<MapCacheEntry> entries, SongSortField key, bool highFirst,
        IReadOnlyDictionary<string, SongPreference>? preferences = null,
        Func<MapCacheEntry, (DateTime? Created, DateTime? Modified)>? fileTimes = null,
        Func<MapCacheEntry, (string Family, int? Slot)>? families = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (families is not null) return SortFamilies(entries, key, highFirst, preferences, fileTimes, families);
        var order = entries.Select((entry, index) => (entry, index)).ToArray();
        if (key == SongSortField.Manual)
        {
            // Songs the user has never dragged keep their incoming order behind the ones that were placed.
            int Place((MapCacheEntry Entry, int Index) item) =>
                preferences is not null && preferences.TryGetValue(item.Entry.Id, out var pref) && pref.Order >= 0 ? pref.Order : int.MaxValue;
            return order.OrderBy(Place).ThenBy(item => item.index).Select(item => item.entry).ToArray();
        }

        // File dates are read once per song, and a song whose file is gone counts as having no value.
        var times = key is SongSortField.Created or SongSortField.Modified && fileTimes is not null
            ? entries.ToDictionary(entry => entry.Id, entry => fileTimes(entry), StringComparer.Ordinal)
            : null;
        DateTime? Time((MapCacheEntry entry, int index) item)
        {
            if (times is null || !times.TryGetValue(item.entry.Id, out var pair)) return null;
            return key == SongSortField.Created ? pair.Created : pair.Modified;
        }
        var sorted = order.OrderBy(item => key is SongSortField.Created or SongSortField.Modified ? Time(item) is null : Missing(item.entry, key));
        sorted = key switch
        {
            SongSortField.Difficulty => Then(sorted, item => item.entry.Info?.Difficulty ?? 0),
            // Pressed notes only, as the song list and info count them.
            SongSortField.NoteCount => Then(sorted, item => item.entry.Timeline?.CountPressedHits() ?? 0),
            SongSortField.Duration => Then(sorted, item => item.entry.Timeline is { HitTimesMs.Count: > 0 } t ? t.HitTimesMs[^1] : 0d),
            SongSortField.Created or SongSortField.Modified => Then(sorted, item => Time(item) ?? DateTime.MinValue),
            SongSortField.Title => ThenText(sorted, item => item.entry.SongName),
            SongSortField.Artist => ThenText(sorted, item => item.entry.Info?.Artist ?? ""),
            SongSortField.Author => ThenText(sorted, item => item.entry.Info?.Author ?? ""),
            // The sync order is the order the cache lists songs in; newest on top means the later entries first.
            _ => Then(sorted, item => item.index),
        };
        // Same value: by title, then by path so two songs of one name never swap between runs.
        return sorted.ThenBy(item => item.entry.SongName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.entry).ToArray();

        IOrderedEnumerable<(MapCacheEntry entry, int index)> Then<T>(IOrderedEnumerable<(MapCacheEntry entry, int index)> source,
            Func<(MapCacheEntry entry, int index), T> value) => highFirst ? source.ThenByDescending(value) : source.ThenBy(value);

        IOrderedEnumerable<(MapCacheEntry entry, int index)> ThenText(IOrderedEnumerable<(MapCacheEntry entry, int index)> source,
            Func<(MapCacheEntry entry, int index), string> value) => highFirst
                ? source.ThenByDescending(value, StringComparer.CurrentCultureIgnoreCase)
                : source.ThenBy(value, StringComparer.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// Sorts the originals only and puts each one's edits right under it by slot, so an edit is never above its original
    /// whatever the order. An edit whose original is not there takes the place of the first edit
    /// of its family, with the others under it.
    /// </summary>
    private static IReadOnlyList<MapCacheEntry> SortFamilies(IReadOnlyList<MapCacheEntry> entries, SongSortField key, bool highFirst,
        IReadOnlyDictionary<string, SongPreference>? preferences, Func<MapCacheEntry, (DateTime? Created, DateTime? Modified)>? fileTimes,
        Func<MapCacheEntry, (string Family, int? Slot)> families)
    {
        var places = entries.Select(entry => (Entry: entry, Place: families(entry))).ToArray();
        var heads = new List<MapCacheEntry>();
        var members = new Dictionary<MapCacheEntry, List<MapCacheEntry>>(ReferenceEqualityComparer.Instance);
        var headOf = new Dictionary<string, MapCacheEntry>(StringComparer.OrdinalIgnoreCase);
        // Originals first, so every edit finds its original wherever the original is in the list.
        foreach (var (entry, place) in places.Where(item => item.Place.Slot is null))
        {
            heads.Add(entry);
            members[entry] = [];
            headOf.TryAdd(place.Family, entry);
        }
        foreach (var (entry, place) in places.Where(item => item.Place.Slot is not null).OrderBy(item => item.Place.Slot))
        {
            if (headOf.TryGetValue(place.Family, out var head))
            {
                members[head].Add(entry);
                continue;
            }
            heads.Add(entry);
            members[entry] = [];
            headOf[place.Family] = entry;
        }
        return Sort(heads, key, highFirst, preferences, fileTimes).SelectMany(head => members[head].Prepend(head)).ToArray();
    }

    /// <summary>Whether the song has no value for this key; those songs go last instead of piling up on top.</summary>
    private static bool Missing(MapCacheEntry entry, SongSortField key) => key switch
    {
        SongSortField.Difficulty => entry.Info?.Difficulty is null,
        SongSortField.NoteCount or SongSortField.Duration => entry.Timeline is not { HitTimesMs.Count: > 0 },
        SongSortField.Artist => string.IsNullOrWhiteSpace(entry.Info?.Artist),
        SongSortField.Author => string.IsNullOrWhiteSpace(entry.Info?.Author),
        _ => false,
    };
}
