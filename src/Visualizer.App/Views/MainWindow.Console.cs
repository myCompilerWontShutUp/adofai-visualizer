using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Visualizer.App.Commands;
using Visualizer.App.ViewModels;
using Visualizer.Application.Cache;
using Visualizer.Application.Library;
using Visualizer.Application.Maps;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using System.Windows.Media;
using Visualizer.Core.Visualization;
using Visualizer.Infrastructure.Cache;
using Visualizer.Infrastructure.Maps;
using Visualizer.Infrastructure.Parsing;
using Visualizer.Infrastructure.Storage;
using Visualizer.Application.Playback;
using Visualizer.Application.Sync;
using Visualizer.Infrastructure.Sync;

namespace Visualizer.App.Views;

/// <summary>What the console reads and changes in the app. Reasons handed back are English, as the console prints them.</summary>
public partial class MainWindow
{
    ParsedTimeline? ICmdHost.Timeline => _model.Playback.Timeline;

    string? ICmdHost.SongToLoad => _model.SongToLoad?.SongName;

    string? ICmdHost.LoadedSongName => _model.LoadedSongName;

    bool ICmdHost.LoadSong(out string message)
    {
        // The main window shows its own message; the console gets the reason in English.
        var picked = _model.LoadSelectedSong();
        message = !picked ? "No song is picked on the main window."
            : _model.LoadedEntry is { Status: not MapParseStatus.Supported } entry ? $"The map is {entry.Status}." : "";
        return _model.Playback.Timeline is not null;
    }

    string? ICmdHost.LoadSongById(string id, bool? keepSpeed)
    {
        if (_model.SongWithId(id) is not { } entry) return $"No song has the id {id}.";
        _model.LoadChosen(entry, keepSpeed);
        // As the Load button does: the guide buttons forget the song they would put back, and the song panel folds away.
        ForgetGuide();
        MapPanel.IsExpanded = false;
        return _model.LoadedEntry is { Status: not MapParseStatus.Supported } loaded ? $"The map is {loaded.Status}." : null;
    }

    string? ICmdHost.ForceLoadSongById(string id, bool? keepSpeed)
    {
        if (_model.SongWithId(id) is not { } entry) return $"No song has the id {id}.";
        if (entry.Status != MapParseStatus.Unsupported) return $"The map is {entry.Status}; -force is only for unsupported songs.";
        if (!UseAppStore) return "The app store is off.";
        try
        {
            _cache ??= new WorkspaceMapCache(Storage);
            var service = new MapSyncService(_cache, _cache, new AdofaiTimelineParser());
            // Every step awaits with ConfigureAwait(false), so waiting here on the UI thread cannot deadlock.
            var report = service.ForceAsync(new ReadOnlyDirectoryMapSource(entry.RootDirectory), entry).GetAwaiter().GetResult();
            if (report.Result.Status != MapParseStatus.Supported)
            {
                // The reason is the parser's own (Korean); the main window shows it, the console says what happened in English.
                _model.SetMessage(_model.Text.Format("ForceLoadFailed", string.Join(" ", report.Result.Warnings)));
                return "No verified format version reads it, so something other than its format version is unsupported. It stays unsupported; the main window shows the reason.";
            }
            _model.SetLibrary(report.Entries);
            return ((ICmdHost)this).LoadSongById(id, keepSpeed);
        }
        catch (Exception error) when (IsExpectedError(error)) { return error.Message; }
    }

    IReadOnlyList<string> ICmdHost.FrameStats(bool restart)
    {
        var lines = Rendering.FrameStats.Lines(_frames.Report(RenderCapability.Tier >> 16)).ToList();
        lines.Add($"Scheduling  multimedia class {(_scheduling is { Active: true } set ? "on (" + set.Task + ")" : "off" + (_scheduling?.Problem is { } why ? " · " + why : ""))}"
            + $" · GC latency {System.Runtime.GCSettings.LatencyMode}");
        if (restart)
        {
            _frames.Reset();
            lines.Add("Measuring again from now.");
        }
        return lines;
    }

    /// <summary>
    /// The game link is console-only: there is no setting, and the pipe stays closed until link start asks for it.
    /// </summary>
    private GameSyncLink? _sync;
    private NamedPipeGameSyncServer? _syncServer;
    private readonly GameSyncFollower _follower = new();

    /// <summary>The link, made on first use so it shares the clock the playback session runs on.</summary>
    private GameSyncLink Sync => _sync ??= new GameSyncLink(_model.PlaybackClock);

    /// <summary>
    /// One frame of following the game's clock, called from the frame loop. It does nothing at all until link follow, and
    /// only ever moves the playhead: notes, hit times and judging are untouched.
    /// </summary>
    private void FollowGameSync()
    {
        // This runs inside the frame loop, so whatever the game sent may cost following but never the frame.
        try { FollowGameSyncFrame(); }
        catch (Exception error) when (IsExpectedError(error))
        {
            _follower.Stop();
            _model.FollowingGame = false;
            _followProblem = "Following stopped: " + error.Message;
        }
        // After following, so a run plans its presses from where this frame put the notes.
        try { InspectFrame(); }
        catch (Exception error) when (IsExpectedError(error)) { StopInspect("it could not go on: " + error.Message); }
    }

    /// <summary>Why following last stopped on its own, for link to show.</summary>
    private string? _followProblem;

    private void FollowGameSyncFrame()
    {
        AutoFollowGame();
        if (!_model.FollowingGame) return;
        var before = _model.Playback.State;
        var (reading, arrived) = Sync.Latest;
        _follower.Follow(reading, arrived, _model.PlaybackClock.NowMs, _model.Playback, Sync.Level);
        // The follower ends following by itself when the app changes the song under it, so the app has to come back out
        // of it here; the setter puts everything it took over back.
        if (_follower.OffsetMs is null) _model.FollowingGame = false;
        // The view model did not make this change, so nothing else would tell the window about it.
        else if (_model.Playback.State != before) _model.NotifyPlaybackChanged();
    }

    IReadOnlyList<string> ICmdHost.GameSync(bool start, bool stop, bool restart)
    {
        if (stop) StopGameSync();
        if (start)
        {
            _syncServer ??= new NamedPipeGameSyncServer(Sync, GameSyncPipeName);
            _syncServer.Start();
        }
        if (restart) Sync.Reset();
        var lines = GameSyncLink.Lines(Sync.Report(), _syncServer?.Listening == true,
            _syncServer?.Connected == true, _syncServer?.Problem, AppInfo.Version).ToList();
        lines.AddRange(_follower.Lines());
        if (_followProblem is { } problem) lines.Add("Problem  " + problem);
        lines.Add(_linkAuto
            ? "Auto     on; the game's song is loaded here and its tiles matched by themselves" + (_autoNote is { } note ? " \u00b7 " + note : "")
            : "Auto     off; link auto matches the tiles by itself");
        lines.Add(InspectLine());
        if (restart) lines.Add("Measuring again from now.");
        return lines;
    }

    IReadOnlyList<string> ICmdHost.GameSyncFollow(bool on)
    {
        if (!on)
        {
            _follower.Stop();
            _model.FollowingGame = false;
            return ["Not following the game any more. The playhead is yours again."];
        }
        if (_syncServer is not { Listening: true }) return ["Not listening. Use link start first."];
        if (Sync.Level is not { } level) return ["No tile times yet. Load a level in the game with the mod switched on."];
        if (_model.Playback.State is PlaybackState.NoTimeline)
            return ["No song is loaded. Load the song the game is playing, then try again."];
        if (_model.Playback.State is PlaybackState.Off)
            return ["The visualizer is off. Turn it on, then try again."];
        // Out of any earlier run first, so a refusal here never leaves the app half in it.
        _model.FollowingGame = false;
        _followProblem = null;
        if (_follower.Prepare(level, _model.Playback) is { } why)
            return [_model.LoadedEntry is { } stale && MainViewModel.IsStale(stale)
                ? $"Cannot follow: {why} This song has an older timing cache ({stale.ParserVersion}); run sync, then load the song again."
                : "Cannot follow: " + why];
        _model.FollowingGame = true;
        var lines = new List<string>
        {
            $"Following the game. The offset is {_follower.OffsetMs!.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} ms.",
            "Start the song in the game; the notes come down with its countdown. link unfollow stops following."
        };
        return lines;
    }

    /// <summary>
    /// link auto: the song the game moves to is loaded here, its tiles are matched and the notes go on the game's clock
    /// without link follow being typed. There is no UI for it (user rule): the console switches it, and link shows it.
    /// </summary>
    private bool _linkAuto;
    /// <summary>
    /// The game's song that is already taken care of: loaded here by link auto, or found loaded. One load per song the game
    /// moves to, so a song picked here by hand afterwards stays until the game moves on.
    /// </summary>
    private string? _autoLoadedPath;
    /// <summary>The level, song and preparation the last attempt was for, so one load is tried once instead of every frame.</summary>
    private (GameSyncLevel Level, string Song, int Prepared)? _autoTried;
    /// <summary>When the last attempt was refused, on the playback clock; a refusal is tried again after <see cref="AutoRetryMs"/>.</summary>
    private double _autoRefusedAt = double.NaN;
    /// <summary>
    /// How long a refused attempt waits before the same level and song are tried again. A level is sent while the game is
    /// still building it, and an older linker never sends it again, so one refusal must not be the last word (user report:
    /// link auto stopped working after moving to another song).
    /// </summary>
    private const double AutoRetryMs = 2000;
    /// <summary>What that attempt said, so link can show it: an automatic refusal must not be silent.</summary>
    private string? _autoNote;

    IReadOnlyList<string> ICmdHost.GameSyncAuto(bool on)
    {
        _linkAuto = on;
        _autoLoadedPath = null;
        _autoTried = null;
        _autoRefusedAt = double.NaN;
        _autoNote = null;
        if (!on) return ["The tiles are no longer matched by themselves. link follow still does it by hand."];
        var lines = new List<string>
        {
            "Matching by itself from now on: the song the game moves to is loaded here (the original; the slot box picks an edit),",
            "and the notes go on the game's clock. A song picked here by hand stays until the game moves to another one.",
            "Nothing is sent to the game, and link manual stops this."
        };
        if (_syncServer is not { Listening: true }) lines.Add("Nothing happens until the pipe is open; use link start.");
        return lines;
    }

    /// <summary>
    /// One frame of that. It does exactly what link follow does, so every refusal is the same: a song prepared from the
    /// middle, a speed that is not 100%, or tile times that do not agree all stop it, and the reason waits on link.
    /// </summary>
    private void AutoFollowGame()
    {
        if (!_linkAuto) return;
        if (_syncServer is not { Listening: true } || Sync.Level is not { } level) return;
        // The game moving to another song comes first (user rule: link auto loads it), following or not.
        if (_model.LoadedEntry is not { } entry || !GamePlays(level.Path, entry))
        {
            LoadGameSong(level.Path);
            return;
        }
        _autoLoadedPath = level.Path;
        if (_model.FollowingGame || _model.Playback.Timeline is null) return;
        // One try per level the game sends (each one sent is a new object, a resend included) and per song prepared here;
        // without this the tile compare would run every frame. Preparations are counted, not notes: a refusal over the
        // speed would otherwise never be tried again, since putting the speed back prepares the very same notes. A refusal
        // is tried again after a while all the same.
        var key = (level, entry.Id, _model.Playback.Prepared);
        var now = _model.PlaybackClock.NowMs;
        var again = _autoTried is { } tried && ReferenceEquals(tried.Level, key.level) && tried.Song == key.Id && tried.Prepared == key.Prepared;
        if (again && !(now - _autoRefusedAt >= AutoRetryMs)) return;
        _autoTried = key;
        _autoNote = ((ICmdHost)this).GameSyncFollow(true).FirstOrDefault();
        _autoRefusedAt = _model.FollowingGame ? double.NaN : now;
        // With link auto on the keys that move the notes are off, so a refusal seen only on link left the app looking dead
        // (user report). The main window says how it went once per try: a retry of the same refusal would only bury newer
        // messages and keep its age at "just now".
        if (_model.FollowingGame) _model.SetMessage(_model.Text["GameAutoFollowing"]);
        else if (!again) _model.SetMessage(_model.Text[MainViewModel.IsStale(entry) ? "GameAutoStaleCache" : "GameAutoRefused"]);
    }

    /// <summary>
    /// Loads the song the game moved to, once per song. Not under the editor or the settings panel: they hold work of the
    /// user's, so it waits for them to close. The load is the one the console's load does, speed rule included.
    /// </summary>
    private void LoadGameSong(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || string.Equals(_autoLoadedPath, gamePath, StringComparison.OrdinalIgnoreCase)) return;
        if (Busy || _settingsPanel is not null) return;
        _autoLoadedPath = gamePath;
        if (SongFor(gamePath, _model.AllSongs, OriginalPathOf) is not { } song)
        {
            _autoNote = $"the game moved to {gamePath}, which is not in the song list; sync brings it in.";
            _model.SetMessage(_model.Text["GameSongMissing"]);
            return;
        }
        _model.LoadChosen(song, null);
        ForgetGuide();
        MapPanel.IsExpanded = false;
        _autoNote = $"loaded {song.SongName}, which the game moved to.";
    }

    /// <summary>
    /// The song in the list that the game's level is: its original when the list has it (an edit is the user's to pick from
    /// the slot box), else the first edit of it. Public for tests.
    /// </summary>
    public static MapCacheEntry? SongFor(string gamePath, IEnumerable<MapCacheEntry> songs, Func<MapCacheEntry, string?> originalOf)
    {
        ArgumentNullException.ThrowIfNull(gamePath);
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(originalOf);
        MapCacheEntry? edit = null;
        foreach (var song in songs)
        {
            if (GamePlays(gamePath, song.RelativePath)) return song;
            if (edit is null && GamePlays(gamePath, originalOf(song))) edit = song;
        }
        return edit;
    }

    /// <summary>
    /// Whether the game's level is this song. The game names its own copy of the map; ours is a copy under maps/, so the
    /// tail of the path is what can match. An edit lives in a slot folder of its own but plays the original's map, so it is
    /// also matched by the original's folder and file (user report: link auto never found an edit).
    /// </summary>
    private bool GamePlays(string gamePath, MapCacheEntry entry) => GamePlays(gamePath, entry.RelativePath, OriginalPathOf(entry));

    /// <summary>The original's folder and file for an edit, as a path under maps/; nothing for an original.</summary>
    private string? OriginalPathOf(MapCacheEntry entry)
    {
        if (!UseAppStore) return null;
        try
        {
            if (Maps.SlotOf(entry) is not null)
                return Path.Combine(Edits.SongFolderOf(entry), Path.GetFileName(entry.RelativePath));
        }
        catch (Exception error) when (IsExpectedError(error)) { }
        return null;
    }

    /// <summary>The path test itself: the game's path ends with one of these, folder boundary included. Public for tests.</summary>
    public static bool GamePlays(string gamePath, params string?[] relativePaths)
    {
        ArgumentNullException.ThrowIfNull(gamePath);
        var game = gamePath.Replace('/', '\\');
        return relativePaths.OfType<string>().Where(path => path.Length > 0).Any(path =>
        {
            var tail = path.Replace('/', '\\').TrimStart('\\');
            return game.Equals(tail, StringComparison.OrdinalIgnoreCase)
                || game.EndsWith('\\' + tail, StringComparison.OrdinalIgnoreCase);
        });
    }

    LinkerPlace ICmdHost.FindLinker()
    {
        var game = LinkerInstaller.FindGame(_model.Settings.MapRootDirectory);
        string? repository = null;
        try { repository = WorkspaceMapCache.FindRepositoryRoot(AppContext.BaseDirectory); }
        catch (Exception error) when (IsExpectedError(error)) { }
        var source = LinkerInstaller.FindSource(AppContext.BaseDirectory, repository);
        return new LinkerPlace(game, source, source is null ? null : LinkerInstaller.VersionAt(source),
            game is null ? null : LinkerInstaller.InstalledVersion(game), game is null ? "" : LinkerInstaller.TargetOf(game));
    }

    string? ICmdHost.InstallLinker(LinkerPlace place, IProgress<double> progress)
    {
        ArgumentNullException.ThrowIfNull(place);
        if (place.GameDirectory is null || place.Source is null) return "Nothing was written: the game folder or the linker is missing.";
        try
        {
            _ = LinkerInstaller.Install(place.GameDirectory, place.Source, progress);
            return null;
        }
        catch (Exception error) when (IsExpectedError(error)) { return "The linker was not installed. " + error.Message; }
    }

    IReadOnlyList<string> ICmdHost.GameSyncTiles()
    {
        if (_syncServer is not { Listening: true }) return ["Not listening. Use link start first."];
        if (Sync.Level is not { } level) return ["No tile times yet. Load a level in the game with the mod switched on."];
        if (_model.Playback.Timeline is not { } timeline) return ["Load the song the game is playing first."];
        var warning = _model.LoadedEntry is { } entry && !GamePlays(level.Path, entry)
            ? $"the game has {level.Path}, which is not the loaded {entry.RelativePath}."
            : null;
        return GameTileCheck.Lines(GameTileCheck.Compare(level.TimesMs, timeline), warning);
    }

    /// <summary>Closes the pipe. Also called when the window closes, so no thread outlives the app.</summary>
    private void StopGameSync()
    {
        StopInspect("the link was closed");
        _follower.Stop();
        _model.FollowingGame = false;
        _syncServer?.Dispose();
        _syncServer = null;
    }

    VisualizerSettings ICmdHost.Settings => _model.Settings;

    bool ICmdHost.SettingsOpen => _settingsPanel is not null;

    bool ICmdHost.EditorOpen => _editor is not null;

    CmdNoteSource? ICmdHost.NowNotes =>
        // While editing, now is the work in progress, saved or not (user rule).
        _editor is { } editor && _editorTimeline is { } edited ? new(edited, editor.Session.ToLayout())
        : _model.Playback.Timeline is { } timeline ? new(timeline, _model.Playback.Layout) : null;

    NoteEdit? ICmdHost.EditOf(CmdSong song) => LayoutOf(song.Entry);

    /// <summary>
    /// The console's override. The JSON is read here, not trusted: a line without a note number or a lane, a time that
    /// does not match, or a chord the rules refuse leaves the editor exactly as it was.
    /// </summary>
    string? ICmdHost.OverrideNotes(string json)
    {
        if (_editor is not { } editor) return "The note editor is not open.";
        var notes = new List<NoteEditSession.NoteOverride>();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("notes", out var listed)) root = listed;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Array) return "The JSON has no list of notes.";
            foreach (var entry in root.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) return "Every note is an object with n and tile.";
                if (Whole(entry, "n") is not { } number) return "A note has no number (n).";
                if (Whole(entry, "tile") is not { } tile) return $"note {number} has no tile.";
                var chord = Whole(entry, "chord");
                var time = entry.TryGetProperty("ms", out var ms) && ms.ValueKind == System.Text.Json.JsonValueKind.Number
                    && ms.TryGetDouble(out var value) ? value : (double?)null;
                notes.Add(new NoteEditSession.NoteOverride(number, tile, chord, time));
            }
        }
        catch (System.Text.Json.JsonException error) { return "The JSON could not be read. " + error.Message; }
        if (notes.Count == 0) return "No notes were named.";
        var problem = editor.Session.Override(notes, _model.Settings.SimultaneousWindowMs);
        if (problem is null) editor.Redrawn();
        return problem;

        static int? Whole(System.Text.Json.JsonElement entry, string name) =>
            entry.TryGetProperty(name, out var found) && found.ValueKind == System.Text.Json.JsonValueKind.Number
                && found.TryGetInt32(out var value) ? value : null;
    }

    bool ICmdHost.HasUnappliedSettings => _settingsPanel?.HasUnappliedChanges == true;

    string? ICmdHost.ApplyPendingSettings() => _settingsPanel?.ApplyInPlace(CmdSession.Language);

    string? ICmdHost.CloseSettings(bool apply) => _settingsPanel?.CloseFromOutside(apply ? CmdSession.Language : null);

    void ICmdHost.CopyText(string text) => SettingsPanel.CopyToClipboard(text);

    IReadOnlyList<CmdSong> ICmdHost.Songs => !UseAppStore ? [] : _model.AllSongs
        .Where(entry => _model.SongIdMap.ContainsKey(entry.Id))
        .Select(entry => new CmdSong(_model.SongIdMap[entry.Id], _model.DisplayName(entry), Maps.KindOf(entry).ToString().ToLowerInvariant(),
            _model.IsHidden(entry), Maps.SlotOf(entry), FamilyOf(entry), entry))
        .ToArray();

    IReadOnlyList<string> ICmdHost.CurrentIds => !UseAppStore ? [] : _model.Library
        .Where(entry => _model.SongIdMap.ContainsKey(entry.Id)).Select(entry => _model.SongIdMap[entry.Id]).ToArray();

    /// <summary>The same for a song and its edits: the folder the edits are filed under, with the level's file name.</summary>
    private string FamilyOf(MapCacheEntry entry) => Edits.SongFolderOf(entry) + "/" + Path.GetFileName(entry.RelativePath);

    string? ICmdHost.LoadedId => _model.LoadedEntry is { } entry && _model.SongIdMap.TryGetValue(entry.Id, out var id) ? id : null;

    int ICmdHost.Checkpoint => _model.Checkpoint;

    string? ICmdHost.MoveCheckpoint(int checkpoint)
    {
        if (_model.Playback.Timeline is not { CheckpointsVerified: true }) return "The loaded song has no checkpoints.";
        try { _model.Checkpoint = checkpoint; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            // The timeline's own reasons are Korean; the console says what happened in English.
            return "That checkpoint is not in this song.";
        }
        // A placement failure empties the notes and the main window shows why, in its own language.
        return _model.Playback.Notes.Count == 0 ? "The notes could not be prepared there; the main window says why." : null;
    }

    string? ICmdHost.StartAtTile(int tile)
    {
        if (_model.Playback.Timeline is not { } timeline) return "No song is loaded.";
        if (CmdSession.TileProblem(timeline, tile, _model.LoadedEntry?.SongName ?? "The loaded song") is { } problem) return problem;
        if (!_model.StartAtTile(tile)) return "The notes could not be prepared from that tile; the main window says why.";
        return null;
    }

    /// <summary>Every family's pins, read from pins.json once; tests keep them in memory only.</summary>
    private Dictionary<string, IReadOnlyList<int>>? _pins;
    /// <summary>Why pins.json could not be read; nothing is written over it then, so no pin is lost to a bad read.</summary>
    private string? _pinsUnreadable;

    private Dictionary<string, IReadOnlyList<int>> Pins
    {
        get
        {
            if (_pins is not null) return _pins;
            _pins = new(StringComparer.Ordinal);
            if (!UseAppStore) return _pins;
            try { foreach (var (family, tiles) in new PinBook(Storage).Read()) _pins[family] = tiles; }
            catch (Exception error) when (IsExpectedError(error) || error is System.Text.Json.JsonException) { _pinsUnreadable = error.Message; }
            return _pins;
        }
    }

    IReadOnlyList<int> ICmdHost.PinsOf(string family) => Pins.TryGetValue(family, out var tiles) ? tiles : [];

    string? ICmdHost.SetPins(string family, IReadOnlyList<int> tiles)
    {
        var pins = Pins;
        if (_pinsUnreadable is { } why) return "pins.json cannot be read, so nothing was changed. " + why;
        if (tiles.Count == 0) pins.Remove(family);
        else pins[family] = tiles.Distinct().Order().ToArray();
        if (!UseAppStore) return null;
        try
        {
            new PinBook(Storage).Write(pins);
            return null;
        }
        catch (Exception error) when (IsExpectedError(error)) { return "The pins could not be saved. " + error.Message; }
    }

    string? ICmdHost.SetHidden(string id, bool hidden)
    {
        if (_model.SongWithId(id) is not { } entry) return $"No song has the id {id}.";
        _model.SetHidden(entry, hidden);
        return SaveSongChoices();
    }

    /// <summary>Writes the song list choices; null when written (or nothing is stored in tests), otherwise why not.</summary>
    private string? SaveSongChoices()
    {
        if (!UseAppStore) return null;
        try
        {
            SongStore.Write(_model.SongPreferences);
            return null;
        }
        catch (Exception error) when (IsExpectedError(error)) { return "The song list could not be saved. " + error.Message; }
    }

    /// <summary>The song list window opened from the console: confirming it applies at once, as there is no settings window to wait for.</summary>
    bool ICmdHost.OpenPlaylist()
    {
        var window = new SongListWindow(_model.AllSongs, _model.SongPreferences, _model.Settings, SongFileTimes, _model.IsHiddenByDefault,
            _model.SongFamilies)
        {
            Owner = this,
            RemoveSong = RemoveListed
        };
        if (window.ShowDialog() != true || window.Result is not { } result) return false;
        _model.SongPreferences = result.Preferences;
        var settings = _model.Settings with { SongListFields = result.Fields };
        // Dragging songs into place makes the order hand-made, as it does from the settings window.
        if (result.Reordered) settings = settings with { SongSortKey = nameof(SongSortField.Manual) };
        if (settings != _model.Settings) _model.ApplySettings(settings);
        else SaveSongChoices();
        return true;
    }

    void ICmdHost.Resync(IProgress<double> progress, Action<int> done) => _ = Run(ResyncAsync(progress), done);

    void ICmdHost.SyncMaps(IProgress<double> progress, Action<int> done) => _ = Run(SyncMapsAsync(progress), done);

    /// <summary>
    /// The repository this build came from, or null when the app was copied out of it. Only update.ps1 there is ever
    /// started, and nothing else on this computer (user request).
    /// </summary>
    private string? Repository
    {
        get
        {
            try { return WorkspaceMapCache.FindRepositoryRoot(AppContext.BaseDirectory); }
            catch (Exception error) when (IsExpectedError(error)) { return null; }
        }
    }

    /// <summary>
    /// update.ps1 with these arguments, or null when this build is not sitting in the repository. It is the one program
    /// the app ever starts (AGENTS.md rule 2), so no other script name can be passed in: uninstall.ps1 in particular is
    /// run by hand in PowerShell only (user rule).
    /// </summary>
    private ProcessStartInfo? UpdateScript(params string[] arguments)
    {
        var repository = Repository;
        var script = repository is null ? null : Path.Combine(repository, "update.ps1");
        if (script is null || !File.Exists(script)) return null;
        var start = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = repository!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script! }.Concat(arguments))
            start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>update and version: asks the repository what is there. -Check changes nothing, so this is safe at any time.</summary>
    void ICmdHost.CheckUpdate(string? version, IProgress<double> progress, Action<UpdateCheck> done)
    {
        if (UpdateScript(["-Check", "-Progress", .. version is null ? Array.Empty<string>() : ["-Version", version]]) is not { } start)
        {
            done(UpdateCheck.Failed("This build is not running from the repository, so there is nothing to update."));
            return;
        }
        _ = RunScriptAsync(start, progress, lines => done(UpdateCheck.Read(lines)));
    }

    /// <summary>
    /// Takes the update: the script waits for this app to close before it builds, since a running app holds its own files,
    /// and starts the new build afterwards. Nothing is read back, because this app will not be here to read it.
    /// </summary>
    string? ICmdHost.ApplyUpdate(bool force, string? version)
    {
        var arguments = new List<string> { "-WaitForProcessId", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "-Restart" };
        if (force) arguments.Add("-Force");
        if (version is not null) arguments.AddRange(["-Version", version]);
        // An installed linker follows the app (user rule): the script builds it from the new version and writes it again.
        if (((ICmdHost)this).FindLinker() is { Installed: not null, GameDirectory: { } game }) arguments.AddRange(["-LinkerGame", game]);
        if (UpdateScript([.. arguments]) is not { } start)
            return "This build is not running from the repository, so there is nothing to update.";
        start.RedirectStandardOutput = false;
        start.RedirectStandardError = false;
        try { Process.Start(start); }
        catch (Exception error) when (IsExpectedError(error) || error is System.ComponentModel.Win32Exception)
        {
            return "The update could not start. " + error.Message;
        }
        // Closing is what lets the build replace this app's own files; the script brings it back.
        Dispatcher.BeginInvoke(new Action(() => System.Windows.Application.Current.Shutdown()),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        return null;
    }

    /// <summary>
    /// Runs a script off the UI thread and brings its words back onto it: the "step" lines move the bar and the rest is
    /// what the console prints when it is over.
    /// </summary>
    private async Task RunScriptAsync(ProcessStartInfo start, IProgress<double> progress, Action<IReadOnlyList<string>> done)
    {
        var lines = new List<string>();
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("powershell did not start.");
            process.OutputDataReceived += (_, e) => Read(e.Data);
            process.ErrorDataReceived += (_, e) => Read(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            if (lines.Count == 0) lines.Add(process.ExitCode == 0 ? "Done." : "The script did not finish.");
        }
        catch (Exception error) when (IsExpectedError(error) || error is System.ComponentModel.Win32Exception)
        {
            lines.Add("The script could not run. " + error.Message);
        }
        done(lines);

        void Read(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            // The script marks its own progress; everything else is for the screen.
            if (line.StartsWith("step ", StringComparison.Ordinal)
                && double.TryParse(line[5..], NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction))
            {
                Dispatcher.InvokeAsync(() => progress.Report(fraction));
                return;
            }
            lock (lines) lines.Add(line.TrimEnd());
        }
    }

    /// <summary>
    /// Lets the console know a long job finished, with the songs in the library, so it can take its progress bar away and
    /// put the prompt back. The job itself already reported its own failures on the main screen.
    /// </summary>
    private async Task Run(Task job, Action<int> done)
    {
        try { await job; }
        finally { done(_model.AllSongs.Count); }
    }

    /// <summary>The notes the console's report editor keeps in the app store; they never leave it (AGENTS.md rule 5).</summary>
    private ReportBook Reports => _reports ??= new ReportBook(Storage);
    private ReportBook? _reports;

    IReadOnlyList<string> ICmdHost.ReportTitles()
    {
        if (!UseAppStore) return [];
        try
        {
            return Reports.Titles();
        }
        catch (Exception error) when (IsExpectedError(error)) { return []; }
    }

    IReadOnlyList<string>? ICmdHost.ReadReport(string title, out string? problem)
    {
        problem = null;
        if (!UseAppStore) { problem = "Notes are not kept in this run."; return null; }
        try
        {
            return Reports.Read(title);
        }
        catch (Exception error) when (IsExpectedError(error)) { problem = error.Message; return null; }
    }

    string? ICmdHost.WriteReport(string title, IReadOnlyList<string> lines)
    {
        if (!UseAppStore) return "Notes are not kept in this run.";
        try
        {
            Reports.Write(title, lines, AppInfo.Version);
            return null;
        }
        catch (Exception error) when (IsExpectedError(error)) { return error.Message; }
    }

    IReadOnlyList<CmdNote> ICmdHost.ReportNotes()
    {
        if (!UseAppStore) return [];
        try
        {
            return [.. Reports.Notes().Select(note => new CmdNote(note.Title,
                note.Standing == ReportBook.Done ? note.Done : note.Version, note.Done, note.Lines, note.Text,
                note.Standing, note.Why))];
        }
        catch (Exception error) when (IsExpectedError(error)) { return []; }
    }

    string? ICmdHost.MarkReport(string title, string state, string? why)
    {
        if (!UseAppStore) return "Notes are not kept in this run.";
        try
        {
            var version = state == ReportBook.Done ? AppInfo.Version : null;
            return Reports.SetState(title, state, version, why) ? null : $"{title} was never written.";
        }
        catch (Exception error) when (IsExpectedError(error)) { return error.Message; }
    }

    string? ICmdHost.RenameReport(string title, string name)
    {
        if (!UseAppStore) return "Notes are not kept in this run.";
        try { return Reports.Rename(title, name); }
        catch (Exception error) when (IsExpectedError(error)) { return error.Message; }
    }

    IReadOnlyList<string> ICmdHost.ClearDoneReports()
    {
        if (!UseAppStore) return [];
        try { return Reports.ClearDone(); }
        catch (Exception error) when (IsExpectedError(error)) { return []; }
    }

    bool ICmdHost.DeleteReport(string title, out string? problem)
    {
        problem = null;
        if (!UseAppStore) { problem = "Notes are not kept in this run."; return false; }
        try { return Reports.Delete(title); }
        catch (Exception error) when (IsExpectedError(error)) { problem = error.Message; return false; }
    }

    string? ICmdHost.RenameSong(string id, string name)
    {
        if (_model.SongWithId(id) is not { } entry) return $"No song has the id {id}.";
        if (_model.SetDisplayName(entry, name) is { } problem) return problem;
        return SaveSongChoices();
    }

    string? ICmdHost.RemoveEdit(string id)
    {
        if (_model.SongWithId(id) is not { } entry) return $"No song has the id {id}.";
        if (Maps.SlotOf(entry) is null) return "Only edited songs can be removed.";
        try
        {
            Maps.Hold(entry, _model.DisplayName(entry));
            Forget(entry);
            return null;
        }
        catch (Exception error) when (IsExpectedError(error)) { return "The song could not be removed. " + error.Message; }
    }

    string? ICmdHost.HeldSong => UseAppStore ? Maps.HeldName : null;

    string? ICmdHost.Restore(out int slot)
    {
        slot = 0;
        if (!UseAppStore) return "There is nothing to restore.";
        try
        {
            slot = Maps.Restore();
            // The slot is back on disk; scanning the edited folder puts it in the list again.
            _ = ScanEditedAsync();
            return null;
        }
        catch (Exception error) when (IsExpectedError(error)) { return "The song could not be restored. " + error.Message; }
    }

    string? ICmdHost.DeleteSong(string id) => _model.SongWithId(id) is { } entry
        ? RemoveSong(entry) is { } reason ? "The song could not be deleted. " + reason : null
        : $"No song has the id {id}.";

    string? ICmdHost.EditSong(string id) => _model.SongWithId(id) is { } entry ? OpenEditor(entry, CmdSession.Language) : $"No song has the id {id}.";

    string? ICmdHost.ApplySettings(VisualizerSettings settings)
    {
        try
        {
            _model.ApplySettings(settings);
            return null;
        }
        catch (NotePlacementException) { return "The song cannot be placed with these settings; note * shows why."; }
        catch (Exception error) when (IsExpectedError(error)) { return error.Message; }
    }

    SettingsChange? ICmdHost.LastChange => _model.SettingsHistory.Count > 0 ? _model.SettingsHistory[^1] : null;

    string? ICmdHost.RollBack()
    {
        try
        {
            return _model.RollBack() is null ? "There is nothing to roll back." : null;
        }
        catch (ArgumentException) { return "The earlier values do not fit the settings as they are now, so nothing changed."; }
    }

    string? ICmdHost.PickFolder(string start)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = false };
        // The data folder starts from the store in use while its setting is empty.
        var from = start.Length > 0 ? start : UseAppStore ? Storage.Root : "";
        if (Directory.Exists(from)) dialog.InitialDirectory = from;
        return IsVisible && dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
}
