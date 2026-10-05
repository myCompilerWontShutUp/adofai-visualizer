using System.IO;
using Visualizer.Application.Cache;
using Visualizer.Application.Library;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using Visualizer.Infrastructure.Parsing;

namespace Visualizer.App.ViewModels;

/// <summary>A version of the loaded song in the slot picker: 0 is the original, 1..9 an edit slot.</summary>
public sealed record SlotChoice(int Number, string Label);

/// <summary>Song library: search, sync, loading, the bundled example, the rule preview and ◀ ▶ song stepping.</summary>
public sealed partial class MainViewModel
{
    private string _songQuery = "";
    /// <summary>
    /// What the list is searched by: the text typed in the song box. The box also shows the title of a song picked with the
    /// arrows or a click; that is not a search, so the list the arrows walk stays as it was.
    /// </summary>
    private string _filter = "";
    private string? _songName;
    /// <summary>The loaded song or example; null for the rule preview and before loading.</summary>
    private MapCacheEntry? _loadedEntry;

    private MapCacheEntry? _selectedMap;
    private IReadOnlyList<MapCacheEntry> _allSongs = [];
    private Dictionary<string, MapCacheEntry> _songsById = new(StringComparer.Ordinal);
    private Dictionary<string, SongPreference> _songPreferences = new(StringComparer.Ordinal);
    private HashSet<string> _secondary = new(StringComparer.Ordinal);
    /// <summary>Songs a sync added that have not been loaded since, shown as new; the main window keeps them in the app store.</summary>
    private HashSet<string> _newSongs = new(StringComparer.Ordinal);
    /// <summary>Edits saved in the editor while the app runs and not loaded since, shown as recently; never saved.</summary>
    private readonly HashSet<string> _recentEdits = new(StringComparer.Ordinal);

    /// <summary>
    /// The song picked in the list. Picking another one changes what Load would do, so the button's look follows it.
    /// </summary>
    public MapCacheEntry? SelectedMap
    {
        get => _selectedMap;
        set
        {
            if (ReferenceEquals(_selectedMap, value)) return;
            _selectedMap = value;
            Raise(nameof(SelectedMap));
            Raise(nameof(LoadPending));
            Raise(nameof(CanMoveSong));
        }
    }
    /// <summary>Folder of the bundled example; its entries are never listed beside the user's songs.</summary>
    public string? ExampleRoot { get; set; }
    /// <summary>Tells the user to sync first when Load is pressed before any song was synced. Null shows only the message.</summary>
    public Action<string>? NotifySyncFirst { get; set; }
    /// <summary>With song search off the song box only picks: typing is blocked and a click opens the list.</summary>
    public bool SongListReadOnly => !Settings.SongSearch;
    /// <summary>Every synced song, hidden ones included and under its own name: what the song list window lists.</summary>
    public IReadOnlyList<MapCacheEntry> AllSongs => _allSongs;
    /// <summary>Which of the app's map folders a song is in. Set by the main window, which owns the file layout.</summary>
    public Func<MapCacheEntry, MapKind>? SongKind { get; set; }
    /// <summary>What a song and its edits share (the folder the edits are filed under). Set by the main window.</summary>
    public Func<MapCacheEntry, string>? SongFamily { get; set; }

    /// <summary>A song's family and slot, for keeping edits under their original; null until the main window sets both.</summary>
    public Func<MapCacheEntry, (string Family, int? Slot)>? SongFamilies =>
        SongFamily is { } family && SongSlot is { } slot ? entry => (family(entry), slot(entry)) : null;
    /// <summary>The edit slot 1..9 of an edited copy, or null. Set by the main window.</summary>
    public Func<MapCacheEntry, int?>? SongSlot { get; set; }
    /// <summary>The saved layout of an edited copy, or null for a song the rules lay out. Set by the main window.</summary>
    public Func<MapCacheEntry, NoteEdit?>? SongLayout { get; set; }
    /// <summary>Cache id → the eight-character id the console shows. Set by the main window.</summary>
    public IReadOnlyDictionary<string, string> SongIdMap { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public MapKind KindOf(MapCacheEntry entry) => SongKind?.Invoke(entry) ?? MapKind.External;

    /// <summary>The song an eight-character id stands for (letters in either case), or null when no song has it.</summary>
    public MapCacheEntry? SongWithId(string? id)
    {
        if (id is not { Length: > 0 }) return null;
        var wanted = id.Trim();
        var cacheId = SongIdMap.FirstOrDefault(pair => string.Equals(pair.Value, wanted, StringComparison.OrdinalIgnoreCase)).Key;
        return cacheId is null ? null : _allSongs.FirstOrDefault(entry => entry.Id == cacheId);
    }

    /// <summary>The name as the list shows it: an edited copy carries its slot, as in "1*Plum - R".</summary>
    public string DisplayName(MapCacheEntry entry)
    {
        // The list holds copies already named for display, so the name comes from the synced song; naming a listed copy again
        // would put its slot in twice ("1*1*…").
        var song = _songsById.TryGetValue(entry.Id, out var synced) ? synced : entry;
        if (SongSlot?.Invoke(entry) is not { } slot) return ChosenName(song);
        // An edit is named after its original (user rule): renaming the original renames every edit under it.
        return $"{slot}*{ChosenName(OriginalOf(entry) ?? song)}";
    }

    private string ChosenName(MapCacheEntry entry) =>
        _songPreferences.TryGetValue(entry.Id, out var pref) && pref.DisplayName is { Length: > 0 } own ? own : entry.SongName;

    /// <summary>
    /// The original an edited copy belongs to, or null when the entry is not an edit or its original is not in the list.
    /// Only edits look this up, so the walk over the songs stays rare.
    /// </summary>
    public MapCacheEntry? OriginalOf(MapCacheEntry entry)
    {
        if (entry is null || SongFamily is not { } family || SongSlot is not { } slot || slot(entry) is null) return null;
        var home = family(entry);
        return _allSongs.FirstOrDefault(each => slot(each) is null && string.Equals(family(each), home, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gives a song the name the list shows, or clears it with null. An edit hands the name to its original, since that is
    /// where the family's name lives (user rule). Null when set, otherwise why not.
    /// </summary>
    public string? SetDisplayName(MapCacheEntry entry, string? name)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var target = OriginalOf(entry) ?? entry;
        var wanted = name?.Trim();
        if (wanted is { Length: > 0 } && !wanted.Any(char.IsLetterOrDigit)) return Text["SongListNameNeedsLetter"];
        _songPreferences[target.Id] = _songPreferences.TryGetValue(target.Id, out var own)
            ? own with { DisplayName = wanted } : new SongPreference(target.Id, null, wanted);
        RebuildLibrary();
        return null;
    }

    /// <summary>
    /// Whether the song starts hidden without the user saying so: a level file beside main.adofai, which the song select
    /// does not play. The song list window can still bring it back, and that choice is kept.
    /// </summary>
    public bool IsHiddenByDefault(MapCacheEntry entry) => entry is not null && _secondary.Contains(entry.Id);

    /// <summary>Whether the song is out of the main list right now, by choice or by the automatic default.</summary>
    public bool IsHidden(MapCacheEntry entry) =>
        (_songPreferences.TryGetValue(entry.Id, out var pref) ? pref.Hidden : null) ?? IsHiddenByDefault(entry);

    /// <summary>Hides or shows one song at once (the console's hide and show); every other choice stays.</summary>
    public void SetHidden(MapCacheEntry entry, bool hidden)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _songPreferences[entry.Id] = _songPreferences.TryGetValue(entry.Id, out var own)
            ? own with { Hidden = hidden } : new SongPreference(entry.Id, hidden);
        RebuildLibrary();
    }

    /// <summary>The cache ids shown as new. Setting them (the ones read at start) rebuilds the list.</summary>
    public IReadOnlyCollection<string> NewSongs
    {
        get => _newSongs;
        set
        {
            _newSongs = new HashSet<string>(value ?? [], StringComparer.Ordinal);
            RebuildLibrary();
        }
    }

    /// <summary>
    /// The song loaded last. The app offers it again when it starts (user rule), so it is kept in the app store with the
    /// new marks. Songs loaded from outside the list (the example, the preview) do not count.
    /// </summary>
    public string? LastSongId { get; private set; }

    /// <summary>Called when the marks or the last song change, so the main window can save them.</summary>
    public Action? SongMarksChanged { get; set; }

    /// <summary>Takes the marks read at start; the list is rebuilt with them.</summary>
    public void RestoreMarks(IEnumerable<string> newSongs, string? lastSong)
    {
        LastSongId = lastSong;
        NewSongs = (newSongs ?? []).ToArray();
    }

    /// <summary>Marks the songs a sync added as new until they are loaded (user rule); songs no longer synced lose the mark.</summary>
    public void MarkNew(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var changed = _newSongs.RemoveWhere(id => !_songsById.ContainsKey(id)) > 0;
        foreach (var id in ids) changed |= _songsById.ContainsKey(id) && _newSongs.Add(id);
        if (!changed) return;
        RebuildLibrary();
        SongMarksChanged?.Invoke();
    }

    /// <summary>Marks an edit just saved in the editor as recently, until it is loaded or the app closes (user rule).</summary>
    public void MarkRecentEdit(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_recentEdits.Add(id)) RebuildLibrary();
    }

    /// <summary>The mark after a title in the song list (user rule): forced, else recently, else new; null for none.</summary>
    public string? BadgeOf(MapCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.IsForced ? Text["SongBadgeForced"]
            : _recentEdits.Contains(entry.Id) ? Text["SongBadgeRecently"]
            : _newSongs.Contains(entry.Id) ? Text["SongBadgeNew"] : null;
    }

    /// <summary>Map file dates for the date sort keys; set by the main window, since the view model does not touch files.</summary>
    public Func<MapCacheEntry, (DateTime? Created, DateTime? Modified)>? SongFileTimes { get; set; }

    /// <summary>The song list choices. Setting them rebuilds the visible list (hidden songs out, display names in, sorted).</summary>
    public IReadOnlyList<SongPreference> SongPreferences
    {
        get => _songPreferences.Values.ToArray();
        set
        {
            _songPreferences = (value ?? []).ToDictionary(song => song.Id, StringComparer.Ordinal);
            RebuildLibrary();
        }
    }

    /// <summary>
    /// Builds the visible list from the synced songs: hidden songs are left out, a display name replaces the title shown
    /// (the cache keeps the original), and the applied sort decides the order the picker and the ◀ ▶ arrows follow.
    /// </summary>
    private void RebuildLibrary()
    {
        var selectedId = SelectedMap?.Id;
        // Named as shown first (slot prefix included), so a title sort follows the names on screen; the cache keeps its own.
        var kinds = SongOrdering.GroupKinds(Settings.SongGroup);
        // Where edits are listed beside their originals, they stay right under them whatever the order, so hidden songs are
        // sorted too: an edit of a hidden original keeps the place the original would have.
        var families = kinds is null || (kinds.Contains(MapKind.Edited) && kinds.Count > 1) ? SongFamilies : null;
        var named = _allSongs
            .Where(entry => (families is not null || !IsHidden(entry)) && Shown(kinds, KindOf(entry)))
            .Select(entry => entry with { SongName = DisplayName(entry), Badge = BadgeOf(entry) })
            .ToArray();
        var ordered = SongOrdering.Sort(named, SongOrdering.Parse(Settings.SongSortKey), Settings.SongSortHighFirst,
            _songPreferences, SongFileTimes, families).Where(entry => !IsHidden(entry));
        Library.Clear();
        foreach (var entry in ordered) Library.Add(entry);
        SelectedMap = Library.FirstOrDefault(e => e.Id == selectedId) ?? Library.FirstOrDefault();
        NotifyAll();
    }

    /// <summary>
    /// Whether a song of this kind belongs in the list under the chosen group. A song in none of the app's three folders
    /// is named by no group, so only a group asking for one folder leaves it out: the wider groups keep showing it rather
    /// than making a song disappear because its folder could not be read.
    /// </summary>
    private static bool Shown(IReadOnlyList<MapKind>? kinds, MapKind kind) =>
        kinds is null || kinds.Contains(kind) || (kinds.Count > 1 && kind == MapKind.External);
    public string SongName => _songName ?? Text["NoSong"];
    /// <summary>The song, example or rule preview being viewed; null before one is loaded.</summary>
    public string? LoadedSongName => _songName;

    public string SongQuery
    {
        get => _songQuery;
        set
        {
            var query = value ?? "";
            if (query == _songQuery) return;
            // The box is where this came from, so it is not told again: that put the text back into the box while it was
            // being typed.
            _songQuery = query;
            // The title of a song just picked is not a search: the arrows keep walking the list the typed text made.
            if (!IsPickedTitle(query))
            {
                _filter = query;
                LibraryView.Refresh();
                Raise(nameof(CanMoveSong));
            }
            // A filtered list can put a different song first, which is the one Load would bring up.
            Raise(nameof(LoadPending));
        }
    }

    private bool MatchesSongQuery(MapCacheEntry entry)
    {
        var query = _filter.Trim();
        // The title of the picked song shows the whole list again.
        return query.Length == 0 || (SelectedMap is { } picked && string.Equals(query, picked.SongName.Trim(), StringComparison.OrdinalIgnoreCase))
            || entry.SongName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public async Task RefreshLibraryAsync(IReadOnlyMapCache cache)
    {
        SetLibrary(await cache.ReadAsync());
    }

    /// <summary>
    /// Scans one of the app's map folders and takes the result into the library. The report is collected by the caller,
    /// which scans all three folders in a row.
    /// </summary>
    public async Task<MapSyncReport> SyncAsync(MapSyncService service, IReadOnlyMapDirectory source,
        IProgress<MapScanProgress>? progress = null)
    {
        var report = await service.SyncAsync(source, progress);
        SetLibrary(report.Entries);
        return report;
    }

    /// <summary>Says how a whole sync went: what was scanned, how it parsed, and the first warnings.</summary>
    public void ReportSync(IReadOnlyList<MapSyncReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var entries = _allSongs;
        Message = Text.Format("SyncReport", reports.Sum(report => report.ScannedCount),
            entries.Count(e => e.Status == MapParseStatus.Supported), entries.Count(e => e.Status == MapParseStatus.Unsupported),
            entries.Count(e => e.Status == MapParseStatus.Error));
        Details = string.Join(Environment.NewLine, reports.SelectMany(report => report.Warnings).Take(10));
        NotifyAll();
    }

    /// <summary>Takes the synced songs, dropping the bundled example and editor backups. Public for tests.</summary>
    public void SetLibrary(IReadOnlyList<MapCacheEntry> entries)
    {
        var songs = new List<MapCacheEntry>();
        foreach (var entry in entries)
        {
            // The bundled example is loaded directly by its own button and never duplicates a Workshop song.
            if (IsExampleEntry(entry)) continue;
            // Caches synced before the editor-backup rule may still hold backup.adofai beside its level.
            if (!MapFileRules.IsShadowedEditorBackup(entry.RelativePath, entries
                    .Where(e => string.Equals(e.RootDirectory, entry.RootDirectory, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.RelativePath)))
                songs.Add(entry);
        }
        // A level beside main.adofai is not the song of its folder, so it starts hidden unless the user says otherwise.
        _secondary = songs.Where(entry => MapFileRules.IsBesideMainLevel(entry.RelativePath, songs
                .Where(other => string.Equals(other.RootDirectory, entry.RootDirectory, StringComparison.OrdinalIgnoreCase))
                .Select(other => other.RelativePath)))
            .Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        _allSongs = songs;
        _songsById = songs.GroupBy(entry => entry.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        RebuildLibrary();
    }

    public bool IsExampleEntry(MapCacheEntry entry) => ExampleRoot is { } root
        && string.Equals(Path.TrimEndingDirectorySeparator(entry.RootDirectory), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What Load and Enter in the song box bring up: the picked song while the box shows its title, otherwise the first song
    /// of the searched list, never a song picked before the search was typed.
    /// </summary>
    public MapCacheEntry? SongToLoad => ShowsPick ? SelectedMap : LibraryView.Cast<MapCacheEntry>().FirstOrDefault();

    /// <summary>Whether the box shows the picked song, by its title or with nothing typed, rather than a search.</summary>
    private bool ShowsPick => SelectedMap is not null && (_songQuery.Trim().Length == 0 || IsPickedTitle(_songQuery));

    private bool IsPickedTitle(string text) =>
        SelectedMap is { } picked && string.Equals(text.Trim(), picked.SongName.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The arrows in the song box: picks the next or previous song of the searched list without loading it, from its first
    /// song while a search is typed, wrapping at the ends. The search stays as typed, so the arrows keep to it.
    /// </summary>
    public MapCacheEntry? PickInSearch(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var shown = LibraryView.Cast<MapCacheEntry>().ToList();
        if (shown.Count == 0) return null;
        var at = ShowsPick ? shown.FindIndex(entry => entry.Id == SelectedMap!.Id) : -1;
        var next = at < 0 ? (direction > 0 ? 0 : shown.Count - 1) : (at + direction + shown.Count) % shown.Count;
        var entry = shown[next];
        SelectedMap = entry;
        // The box shows the picked title; that is not a search, so the list stays the searched one.
        _songQuery = entry.SongName;
        Raise(nameof(SongQuery));
        return entry;
    }

    public bool LoadSelectedSong()
    {
        if (SongToLoad is not { } entry)
        {
            if (Library.Count == 0) NotifySyncFirst?.Invoke(Text["SyncFirstMessage"]);
            SetMessage(Text["SelectSongFirst"]);
            return false;
        }
        Pick(entry);
        LoadAsked(entry);
        return true;
    }

    /// <summary>Loads a song the console named, picking it in the song box when the list has it.</summary>
    public void LoadChosen(MapCacheEntry entry, bool? keepSpeed)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Library.FirstOrDefault(listed => listed.Id == entry.Id) is { } listed) Pick(listed);
        LoadAsked(entry, keepSpeed);
    }

    /// <summary>Shows the song in the box as picked; its title in the box shows the whole list again.</summary>
    private void Pick(MapCacheEntry entry)
    {
        SelectedMap = entry;
        _songQuery = _filter = entry.SongName;
        Raise(nameof(SongQuery));
        LibraryView.Refresh();
    }

    /// <summary>
    /// Loads a song the user asked for. The practice speed goes back to 100% unless keep speed on load is on or
    /// <paramref name="keepSpeed"/> says otherwise (the console's -sm and -sr). That is a settings change of its own: kept for
    /// rollback and saved. A song put back by a guide button (<see cref="LoadCached"/>) keeps it.
    /// </summary>
    private void LoadAsked(MapCacheEntry entry, bool? keepSpeed = null)
    {
        // Loading a song is looking at it, so its new and recently marks go, and it becomes the song the next run offers.
        var seen = _newSongs.Remove(entry.Id);
        if (_recentEdits.Remove(entry.Id) | seen) RebuildLibrary();
        var last = _songsById.ContainsKey(entry.Id) && LastSongId != entry.Id;
        if (last) LastSongId = entry.Id;
        if (seen || last) SongMarksChanged?.Invoke();
        var speedReset = !(keepSpeed ?? Settings.KeepSpeedOnLoad) && Settings.PlaybackSpeedPercent != 100;
        // Automatic checkpoints come back on with a new song (user rule), unless that is switched off.
        var autoReset = Settings.AutoCheckpointOnLoad && !Settings.AutoCheckpointAdvance;
        if (speedReset || autoReset)
        {
            var next = Settings;
            if (speedReset) next = next with { PlaybackSpeedPercent = 100 };
            if (autoReset) next = next with { AutoCheckpointAdvance = true };
            // The old song goes first, so the session takes the change without placing that song again.
            Playback.Clear();
            Playback.ApplySettings(next, 1);
            Record(Settings, next);
            Settings = next;
        }
        LoadEntry(entry);
        if (speedReset || autoReset) SettingsCommitted?.Invoke();
    }

    /// <summary>Whether Load would bring up a song other than the one already loaded; the button leads the eye while it does.</summary>
    public bool LoadPending => SongToLoad is { } next && next.Id != _loadedEntry?.Id;

    /// <summary>Whether ◀ ▶ would load a song other than the loaded one; the buttons grey out otherwise.</summary>
    public bool CanMoveSong
    {
        get
        {
            var songs = LibraryView.Cast<MapCacheEntry>().ToList();
            return songs.Count > 1 || (songs.Count == 1 && songs[0].Id != _loadedEntry?.Id);
        }
    }

    /// <summary>
    /// The ◀ ▶ beside the song box: picks the previous or next song of the (searched) list and loads it right away,
    /// like the checkpoint arrows. Wraps around at either end of the list.
    /// </summary>
    public bool MoveSong(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var songs = LibraryView.Cast<MapCacheEntry>().ToList();
        if (songs.Count == 0)
        {
            if (Library.Count == 0) NotifySyncFirst?.Invoke(Text["SyncFirstMessage"]);
            return false;
        }
        if (!CanMoveSong) return false;
        var current = (_loadedEntry ?? SelectedMap) is { } song ? songs.FindIndex(e => e.Id == song.Id) : -1;
        var next = current < 0 ? (direction > 0 ? 0 : songs.Count - 1) : (current + direction + songs.Count) % songs.Count;
        var entry = songs[next];
        // The picked title in the box shows the whole list again, so stepping continues through every song.
        Pick(entry);
        LoadAsked(entry);
        return true;
    }

    /// <summary>Loads the bundled example without adding it to the song library or the cache.</summary>
    /// <summary>Loads a prepared cache entry directly: the bundled example, or the song put back when a guide button is released.</summary>
    public void LoadCached(MapCacheEntry entry) => LoadEntry(entry);

    /// <summary>
    /// The versions of the loaded song: its original and the edit slots saved under it, in slot order. An edit is picked
    /// here now rather than as a song of its own (user rule), so a hidden version is left out — hiding it is what says it
    /// should not be offered — unless it is the one loaded.
    /// </summary>
    public IReadOnlyList<SlotChoice> SlotOptions
    {
        get
        {
            if (_loadedEntry is null) return [];
            // The picker is always on the field (user rule), so a song whose folders cannot be read still offers the
            // one version there is.
            if (SongFamily is not { } family || SongSlot is not { } slot) return [new SlotChoice(0, Text["SlotOrigin"])];
            var home = family(_loadedEntry);
            return
            [
                .. _allSongs
                    .Where(each => string.Equals(family(each), home, StringComparison.OrdinalIgnoreCase))
                    .Where(each => each.Id == _loadedEntry.Id || !IsHidden(each))
                    .OrderBy(each => slot(each) ?? 0)
                    .Select(each => slot(each) ?? 0)
                    .Distinct()
                    .Select(number => new SlotChoice(number, number == 0 ? Text["SlotOrigin"] : Text.Format("SlotNumber", number)))
            ];
        }
    }

    /// <summary>
    /// Whether the slot picker is on the field. A song with no edits still shows it reading "원본" (user rule): the
    /// three controls keep one shape whatever the song is.
    /// </summary>
    public bool ShowSlotPicker => SlotOptions.Count > 0;

    /// <summary>
    /// The version on screen: 0 for the original, 1..9 for an edit slot. Picking one loads it at once (user rule). It is
    /// the same song, so the speed stays and the practice tile is put back where it was.
    /// </summary>
    public int SelectedSlot
    {
        get => _loadedEntry is not null && SongSlot?.Invoke(_loadedEntry) is { } slot ? slot : 0;
        set
        {
            if (_loadedEntry is null || SongFamily is not { } family || SongSlot is not { } slot || value == SelectedSlot) return;
            var home = family(_loadedEntry);
            var wanted = _allSongs.FirstOrDefault(each => (slot(each) ?? 0) == value
                && string.Equals(family(each), home, StringComparison.OrdinalIgnoreCase));
            if (wanted is null) return;
            var section = _sectionText;
            LoadCached(wanted);
            if (section.Length > 0) SectionText = section;
            NotifyAll();
        }
    }

    public void LoadExample(MapCacheEntry entry) => LoadAsked(entry);

    /// <summary>The entry now loaded; null for the rule preview and before anything is loaded.</summary>
    public MapCacheEntry? LoadedEntry => _loadedEntry;

    /// <summary>Back to "no song", for a guide button released with nothing loaded before it.</summary>
    public void ClearLoaded()
    {
        _songName = null;
        _loadedEntry = null;
        _checkpoint = 1;
        _manualCheckpointPending = false;
        _sectionText = "";
        Playback.Clear();
        Details = "";
        Message = "";
        NotifyAll();
    }

    private void LoadEntry(MapCacheEntry entry)
    {
        _songName = DisplayName(entry);
        _loadedEntry = entry;
        // An edited copy brings its own lanes and chords; every other song is laid out by the rules.
        Playback.Layout = SongLayout?.Invoke(entry);
        Playback.Clear();
        _checkpoint = 1;
        _manualCheckpointPending = false;
        _sectionText = "";
        Details = string.Join(Environment.NewLine, entry.Warnings);
        if (entry.Status == MapParseStatus.Supported && entry.Timeline is { } timeline)
        {
            if (TryPrepare(() => Playback.LoadParsedTimeline(timeline, Settings))) Message = TakeNotice() + Text["Loaded"];
            // Loading is what the user asked for, so an off visualizer switches itself on.
            if (Playback.State == PlaybackState.Off && Playback.Notes.Count > 0) Playback.Toggle();
        }
        else Message = $"{entry.Status}: {entry.Warnings.FirstOrDefault()}";
        if (entry.ParserVersion.StartsWith("adofai-", StringComparison.Ordinal)
            && entry.ParserVersion != AdofaiTimelineParser.CurrentVersion)
            Message = Text["StaleCache"] + Message;
        NotifyAll();
    }

    public void LoadPreview() => LoadPreview(Settings);

    /// <summary>
    /// The preview timeline with the rules of <paramref name="rules"/>. The settings panel passes what it holds but has
    /// not applied, so its changes can be watched before they are committed; nothing else here is touched by them.
    /// </summary>
    public void LoadPreview(VisualizerSettings rules)
    {
        _songName = null;
        _loadedEntry = null;
        _checkpoint = 1;
        _manualCheckpointPending = false;
        _sectionText = "";
        try
        {
            Playback.LoadParsedTimeline(new ParsedTimeline(RulePreviewTimeline.Create(rules.MaximumTileCount),
                new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 25), new TimelineCheckpoint(3, 49) }, true), rules);
            if (Playback.State == PlaybackState.Off) Playback.Toggle();
            Message = Text["PreviewMessage"];
        }
        catch (NotePlacementException error) { Message = error.Message; }
        _songName = Text["PreviewSong"];
        Details = "";
        NotifyAll();
    }
}
