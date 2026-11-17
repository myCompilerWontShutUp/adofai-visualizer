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
using static Visualizer.Core.Localization.ConsoleText;

namespace Visualizer.App.Views;

/// <summary>What the console reads and changes in the app. Reasons handed back are in the console's language, as it prints them.</summary>
public partial class MainWindow
{
    ParsedTimeline? ICmdHost.Timeline => _model.Playback.Timeline;

    string? ICmdHost.SongToLoad => _model.SongToLoad?.SongName;

    string? ICmdHost.LoadedSongName => _model.LoadedSongName;

    bool ICmdHost.LoadSong(out string message)
    {
        // The main window shows its own message; the console gets the reason in its own words.
        var picked = _model.LoadSelectedSong();
        message = !picked ? T("NoSongPicked")
            : _model.LoadedEntry is { Status: not MapParseStatus.Supported } entry ? T("MapIs", entry.Status) : "";
        return _model.Playback.Timeline is not null;
    }

    string? ICmdHost.LoadSongById(string id, bool? keepSpeed)
    {
        if (_model.SongWithId(id) is not { } entry) return T("NoSongWithIdShort", id);
        _model.LoadChosen(entry, keepSpeed);
        // As the Load button does: the guide buttons forget the song they would put back, and the song panel folds away.
        ForgetGuide();
        MapPanel.IsExpanded = false;
        return _model.LoadedEntry is { Status: not MapParseStatus.Supported } loaded ? T("MapIs", loaded.Status) : null;
    }

    string? ICmdHost.ForceLoadSongById(string id, bool? keepSpeed)
    {
        if (_model.SongWithId(id) is not { } entry) return T("NoSongWithIdShort", id);
        if (entry.Status != MapParseStatus.Unsupported) return T("MapIsForceOnly", entry.Status);
        if (!UseAppStore) return T("StoreOff");
        try
        {
            _cache ??= new WorkspaceMapCache(Storage);
            var service = new MapSyncService(_cache, _cache, new AdofaiTimelineParser());
            // Every step awaits with ConfigureAwait(false), so waiting here on the UI thread cannot deadlock.
            var report = service.ForceAsync(new ReadOnlyDirectoryMapSource(entry.RootDirectory), entry).GetAwaiter().GetResult();
            if (report.Result.Status != MapParseStatus.Supported)
            {
                // The reason is the parser's own (Korean); the main window shows it, the console says what happened.
                _model.SetMessage(_model.Text.Format("ForceLoadFailed", string.Join(" ", report.Result.Warnings)));
                return T("ForceFailed");
            }
            _model.SetLibrary(report.Entries);
            return ((ICmdHost)this).LoadSongById(id, keepSpeed);
        }
        catch (Exception error) when (IsExpectedError(error)) { return error.Message; }
    }

    IReadOnlyList<string> ICmdHost.FrameStats(bool restart)
    {
        var lines = Rendering.FrameStats.Lines(_frames.Report(RenderCapability.Tier >> 16)).ToList();
        lines.Add(T("SchedulingLine", _scheduling is { Active: true } set ? T("OnTask", set.Task) : T("Off") + (_scheduling?.Problem is { } why ? " · " + why : ""),
            System.Runtime.GCSettings.LatencyMode));
        if (restart)
        {
            _frames.Reset();
            lines.Add(T("MeasuringAgain"));
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
            _followProblem = T("FollowingStopped", error.Message);
            Log("LogFollowProblem", error.Message);
            _autoRefusedAt = _model.PlaybackClock.NowMs;
        }
        try { GradeFrame(); }
        catch (Exception error) when (IsExpectedError(error)) { }
        // After following, so a run plans its presses from where this frame put the notes.
        try { InspectFrame(); }
        catch (Exception error) when (IsExpectedError(error)) { StopInspect(T("InspectCouldNotGoOn", error.Message)); }
        // Last, so it sees what this frame did; the log never costs the frame.
        try { LogFrame(); }
        catch (Exception error) when (IsExpectedError(error)) { }
    }

    /// <summary>Why following last stopped on its own, for link to show.</summary>
    private string? _followProblem;

    /// <summary>The game's judgment counts as last seen, turned into the grades it gave since.</summary>
    private readonly GameGrades _gameGrades = new();
    /// <summary>How long after a press the game's grade may still arrive for it: a reading or two later, with room to spare.</summary>
    private const double GradeWithinMs = 400;

    /// <summary>
    /// The link's part in the judgment bar, once a frame (user request): while the linker says so, the game's own difficulty
    /// sets the windows, and the grades the game gave go to the presses just made, so the bar shows the game's grading
    /// itself rather than this app's estimate of it.
    /// </summary>
    private void GradeFrame()
    {
        var playback = _model.Playback;
        if (_syncServer is not { Connected: true })
        {
            playback.GameDifficulty = null;
            playback.GameEase = null;
            _gameGrades.Reset();
            return;
        }
        var (reading, _) = Sync.Latest;
        playback.GameDifficulty = reading?.Difficulty;
        // A linker that says nothing of the ease applies none, whatever the settings ask for.
        playback.GameEase = reading is null ? null : reading.Ease ?? 1;
        var grades = _gameGrades.Take(reading?.Judgments, Sync.JudgmentNames);
        if (grades.Count == 0 || playback.State != PlaybackState.Playing) return;
        var difficulty = HitGrading.DifficultyOf(_model.Settings.JudgmentBarDifficulty);
        var fallback = HitGrading.Fallback(playback.GameDifficulty ?? difficulty, playback.JudgmentEase);
        _model.Presses.TakeGameGrades(grades, playback.TimelineTimeMs - playback.Offsets.TotalMs, GradeWithinMs,
            mark => (playback.HitWindowsOf(mark.Note, difficulty) ?? fallback).Grade(mark.DeviationMs));
    }

    private void FollowGameSyncFrame()
    {
        AutoFollowGame();
        if (!_model.FollowingGame) return;
        var before = _model.Playback.State;
        var (reading, arrived) = Sync.Latest;
        _follower.Follow(reading, arrived, _model.PlaybackClock.NowMs, _model.Playback, Sync.Level);
        // The follower ends following by itself when the app changes the song under it, so the app has to come back out
        // of it here; the setter puts everything it took over back. link auto tries again after its usual wait, since the
        // same level, song and notes would otherwise never be tried again (user report: notes gone mid-play).
        if (_follower.OffsetMs is null)
        {
            _model.FollowingGame = false;
            _autoRefusedAt = _model.PlaybackClock.NowMs;
        }
        // The view model did not make this change, so nothing else would tell the window about it.
        else if (_model.Playback.State != before) _model.NotifyPlaybackChanged();
    }

    IReadOnlyList<string> ICmdHost.GameSync(bool start, bool stop, bool restart)
    {
        // Closing the link ends link auto as well (user rule): there is nothing left to match by itself, and it must not
        // start again on its own the next time the pipe opens.
        var manual = stop && _linkAuto;
        if (stop) StopGameSync();
        if (manual) ((ICmdHost)this).GameSyncAuto(false);
        // Without the game the notes would run on alone from wherever it left them: the song goes back to how loading
        // leaves it (user rule).
        var reset = stop && _model.BackToLoaded();
        if (start)
        {
            _syncServer ??= new NamedPipeGameSyncServer(Sync, GameSyncPipeName);
            _syncServer.Start();
        }
        if (restart) Sync.Reset();
        var lines = GameSyncLink.Lines(Sync.Report(), _syncServer?.Listening == true,
            _syncServer?.Connected == true, _syncServer?.Problem, AppInfo.Version).ToList();
        // The game runs twice now and then (user report): every one is taken in, and the one in front is followed.
        if (_syncServer is { Games: > 1 } server) lines.Add(T("LinkGames", server.Games, server.ClientProcessId));
        lines.AddRange(_follower.Lines());
        if (_followProblem is { } problem) lines.Add(T("ProblemLine", problem));
        lines.Add(_linkAuto ? T("AutoOn") + (_autoNote is { } note ? " \u00b7 " + note : "") : T("AutoOff"));
        // The note above is the latest attempt's; that the loaded song stands in for the game's stays said while it does.
        if (_linkAuto && _autoRedirect is { } put && _model.LoadedEntry is { } loaded && loaded.Id == put.Song
            && string.Equals(Sync.Level?.Path, put.Path, StringComparison.OrdinalIgnoreCase))
            lines.Add(T("AutoRedirectLine", loaded.SongName));
        lines.Add(InspectLine());
        if (manual) lines.Add(T("AutoOffWithStop"));
        if (reset) lines.Add(T("LinkStopReset"));
        if (restart) lines.Add(T("MeasuringAgain"));
        return lines;
    }

    IReadOnlyList<string> ICmdHost.GameSyncFollow(bool on)
    {
        if (!on)
        {
            _follower.Stop();
            _model.FollowingGame = false;
            return [T("NotFollowing")];
        }
        if (_syncServer is not { Listening: true }) return [T("NotListening")];
        if (Sync.Level is not { } level) return [T("NoTileTimes")];
        if (_model.Playback.State is PlaybackState.NoTimeline)
            return [T("LoadGameSongFirst")];
        if (_model.Playback.State is PlaybackState.Off)
            return [T("VisualizerOff")];
        // Out of any earlier run first, so a refusal here never leaves the app half in it.
        _model.FollowingGame = false;
        _followProblem = null;
        if (_follower.Prepare(level, _model.Playback) is { } why)
            return [_model.LoadedEntry is { } stale && MainViewModel.IsStale(stale)
                ? T("CannotFollowStale", why, stale.ParserVersion) : T("CannotFollow", why)];
        _model.FollowingGame = true;
        var lines = new List<string>
        {
            T("FollowingOffset", _follower.OffsetMs!.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)),
            T("FollowingHint")
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
    /// <summary>
    /// The game's level and the song link auto put in its place by a redirect, so that song is followed as the game's own
    /// would be (its tiles are still matched first, and a refusal says why).
    /// </summary>
    private (string Path, string Song)? _autoRedirect;

    IReadOnlyList<string> ICmdHost.GameSyncAuto(bool on)
    {
        _linkAuto = on;
        _autoLoadedPath = null;
        _autoRedirect = null;
        _autoTried = null;
        _autoRefusedAt = double.NaN;
        _autoNote = null;
        if (!on) return [T("AutoStopped")];
        var lines = new List<string> { T("AutoStarted1"), T("AutoStarted2"), T("AutoStarted3") };
        if (_syncServer is not { Listening: true }) lines.Add(T("PipeNotOpen"));
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
        // The game moving to another song comes first (user rule: link auto loads it, or the song redirected in its place),
        // following or not.
        if (!string.Equals(_autoLoadedPath, level.Path, StringComparison.OrdinalIgnoreCase))
        {
            // A song found loaded already goes on to following in this frame; one loaded now waits for the next.
            var before = _model.LoadedEntry;
            LoadGameSong(level);
            if (!string.Equals(_autoLoadedPath, level.Path, StringComparison.OrdinalIgnoreCase) || !ReferenceEquals(before, _model.LoadedEntry)) return;
        }
        // The game's song, an edit of it or the song redirected in its place is followed; a song picked here by hand stays
        // as it is until the game moves on.
        if (_model.LoadedEntry is not { } entry) return;
        var redirected = _autoRedirect is { } put && put.Song == entry.Id
            && string.Equals(put.Path, level.Path, StringComparison.OrdinalIgnoreCase);
        if (!redirected && !GamePlays(level, entry)) return;
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
    /// Loads the song the game moved to, once per song: the song a redirect puts in its place when there is one (user
    /// request), else the game's own. A song already loaded that is the one wanted is kept as it is. Not under the editor or
    /// the settings panel: they hold work of the user's, so it waits for them to close. The load is the one the console's
    /// load does, speed rule included.
    /// </summary>
    private void LoadGameSong(GameSyncLevel level)
    {
        var gamePath = level.Path;
        if (string.IsNullOrWhiteSpace(gamePath) || string.Equals(_autoLoadedPath, gamePath, StringComparison.OrdinalIgnoreCase)) return;
        // The song list is searched only when a redirect could change what is wanted or something has to be loaded.
        var song = RedirectMap.Count > 0 ? GameSongOf(level) : null;
        var (target, missing) = song is null ? (null, null) : RedirectOf(song);
        if (_model.LoadedEntry is { } loaded && (target is not null ? loaded.Id == target.Id : GamePlays(level, loaded)))
        {
            _autoLoadedPath = gamePath;
            _autoRedirect = target is null ? null : (gamePath, target.Id);
            // Loaded by hand before link auto came on: it still gets the lanes it needs whole (user rule), once nothing of the
            // user's is open.
            if (!Busy && _settingsPanel is null) _model.WidenLoadedForGame();
            return;
        }
        if (Busy || _settingsPanel is not null) return;
        _autoLoadedPath = gamePath;
        _autoRedirect = null;
        if ((song ?? GameSongOf(level)) is not { } game)
        {
            _autoNote = T("AutoNotListed", gamePath);
            _model.SetMessage(_model.Text["GameSongMissing"]);
            return;
        }
        // At least the lanes the song needs whole, without asking (user rule).
        _model.LoadForGame(target ?? game);
        if (target is not null) _autoRedirect = (gamePath, target.Id);
        ForgetGuide();
        MapPanel.IsExpanded = false;
        _autoNote = target is not null ? T("AutoRedirected", target.SongName, game.SongName)
            : missing is not null ? T("AutoRedirectGone", game.SongName, missing) : T("AutoLoaded", game.SongName);
    }

    /// <summary>The level and song list the game's song was last looked up for, and what was found.</summary>
    private (GameSyncLevel Level, IReadOnlyList<MapCacheEntry> Songs, MapCacheEntry? Song)? _gameSong;

    /// <summary>The song in the list that the game's level is, as link auto finds it; looked up once per level and song list.</summary>
    private MapCacheEntry? GameSongOf(GameSyncLevel level)
    {
        var songs = _model.AllSongs;
        if (_gameSong is { } seen && ReferenceEquals(seen.Level, level) && ReferenceEquals(seen.Songs, songs)) return seen.Song;
        var song = SongFor(level, songs, OriginalPathOf);
        _gameSong = (level, songs, song);
        return song;
    }

    /// <summary>
    /// The song a redirect puts in place of <paramref name="song"/>, by its family (user decision: as pins are kept); with
    /// the id it names when that song is no longer in the list, so link auto can say so and load the game's own.
    /// </summary>
    private (MapCacheEntry? Target, string? Missing) RedirectOf(MapCacheEntry song)
    {
        if (_model.SongFamily is not { } familyOf || !RedirectMap.TryGetValue(familyOf(song), out var id)) return (null, null);
        return _model.SongWithId(id) is { } target ? (target, null) : (null, id);
    }

    string? ICmdHost.GameSongId(out string? problem)
    {
        problem = null;
        if (_syncServer is not { Listening: true }) { problem = T("LinkNotOn"); return null; }
        // The level the linker sent names the song, or failing that the path in its latest reading; link auto has no part
        // in it (user report: load link did nothing with link auto off).
        var (reading, _) = Sync.Latest;
        var level = Sync.Level is { Path.Length: > 0 } sent ? sent : null;
        var path = level?.Path ?? reading?.Path;
        if (!_syncServer.Connected || string.IsNullOrEmpty(path))
        {
            problem = T("LinkerNotConnected");
            return null;
        }
        if ((level is null ? SongFor(path, _model.AllSongs, OriginalPathOf) : SongFor(level, _model.AllSongs, OriginalPathOf)) is { } song)
        {
            // The console knows a song by its eight-character id, not by the cache's own key.
            if (_model.SongIdMap.TryGetValue(song.Id, out var id)) return id;
            problem = T("GameSongNoId", song.SongName);
            return null;
        }
        problem = T("GamePlayingUnlisted", path);
        return null;
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
    /// The same, for a level the game sent with its tile times. A map the game opened from a folder of its own, under other
    /// folder and file names than the copy in the list, has no path in common with it (user report: a map put in by hand
    /// was never loaded by link auto); the tile times the game worked out are that map's own schedule, so the song whose
    /// hits line up with them is the one (<see cref="GameTileCheck.LinesUp"/>). An original comes before an edit, and one
    /// with the game's file name before the rest. Public for tests.
    /// </summary>
    public static MapCacheEntry? SongFor(GameSyncLevel level, IEnumerable<MapCacheEntry> songs, Func<MapCacheEntry, string?> originalOf)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(originalOf);
        var listed = songs as IReadOnlyCollection<MapCacheEntry> ?? songs.ToArray();
        if (SongFor(level.Path, listed, originalOf) is { } byPath) return byPath;
        var name = Path.GetFileName(level.Path.Replace('/', '\\'));
        MapCacheEntry? original = null, edit = null;
        foreach (var song in listed)
        {
            if (song.Timeline is not { } timeline || !GameTileCheck.LinesUp(level.TimesMs, timeline)) continue;
            if (originalOf(song) is not null) { edit ??= song; continue; }
            if (string.Equals(Path.GetFileName(song.RelativePath), name, StringComparison.OrdinalIgnoreCase)) return song;
            original ??= song;
        }
        return original ?? edit;
    }

    /// <summary>
    /// Whether the game's level is this song. The game names its own copy of the map; ours is a copy under maps/, so the
    /// tail of the path is what can match. An edit lives in a slot folder of its own but plays the original's map, so it is
    /// also matched by the original's folder and file (user report: link auto never found an edit).
    /// </summary>
    private bool GamePlays(string gamePath, MapCacheEntry entry) => GamePlays(gamePath, entry.RelativePath, OriginalPathOf(entry));

    /// <summary>The level and song timeline last compared by their tiles, so link auto's check each frame compares them once.</summary>
    private (GameSyncLevel Level, ParsedTimeline Timeline, bool Same)? _tilesCompared;

    /// <summary>
    /// Whether the game's level is this song, by its path or, for a map the game opened under other names, by its tile
    /// times (see <see cref="SongFor(GameSyncLevel, IEnumerable{MapCacheEntry}, Func{MapCacheEntry, string?})"/>).
    /// </summary>
    private bool GamePlays(GameSyncLevel level, MapCacheEntry entry)
    {
        if (GamePlays(level.Path, entry)) return true;
        if (entry.Timeline is not { } timeline) return false;
        if (_tilesCompared is { } seen && ReferenceEquals(seen.Level, level) && ReferenceEquals(seen.Timeline, timeline)) return seen.Same;
        var same = GameTileCheck.LinesUp(level.TimesMs, timeline);
        _tilesCompared = (level, timeline, same);
        return same;
    }

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
        if (place.GameDirectory is null || place.Source is null) return T("LinkerMissingPart");
        try
        {
            _ = LinkerInstaller.Install(place.GameDirectory, place.Source, progress);
            return null;
        }
        catch (Exception error) when (IsExpectedError(error)) { return T("LinkerNotInstalledBecause", error.Message); }
    }

    IReadOnlyList<string> ICmdHost.GameSyncTiles()
    {
        if (_syncServer is not { Listening: true }) return [T("NotListening")];
        if (Sync.Level is not { } level) return [T("NoTileTimes")];
        if (_model.Playback.Timeline is not { } timeline) return [T("LoadGameSongFirstShort")];
        var warning = _model.LoadedEntry is { } entry && !GamePlays(level, entry) ? T("GameHasOther", level.Path, entry.RelativePath) : null;
        return GameTileCheck.Lines(GameTileCheck.Compare(level.TimesMs, timeline), warning);
    }

    /// <summary>Closes the pipe. Also called when the window closes, so no thread outlives the app.</summary>
    /// <summary>
    /// restart linker (user request): everything the link knew goes, as if the app had just started: the pipe, what the
    /// game said and what was measured, following, inspect, and what link auto remembered (whether it is on stays). The
    /// song goes back to how loading leaves it, and the pipe opens again for the game to connect to.
    /// </summary>
    IReadOnlyList<string> ICmdHost.RestartLinker()
    {
        var auto = _linkAuto;
        StopGameSync();
        _sync = null;
        _followProblem = null;
        ((ICmdHost)this).GameSyncAuto(auto);
        _model.BackToLoaded();
        _syncServer = new NamedPipeGameSyncServer(Sync, GameSyncPipeName);
        _syncServer.Start();
        Log("LogLinkRestarted");
        return [T("LinkerRestarted"), .. ((ICmdHost)this).GameSync(false, false, false)];
    }

    private void StopGameSync()
    {
        StopInspect(T("LinkClosed"));
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
        if (_editor is not { } editor) return T("EditorNotOpen");
        var notes = new List<NoteEditSession.NoteOverride>();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("notes", out var listed)) root = listed;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Array) return T("JsonNoList");
            foreach (var entry in root.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) return T("JsonNoteObject");
                if (Whole(entry, "n") is not { } number) return T("JsonNoNumber");
                if (Whole(entry, "tile") is not { } tile) return T("JsonNoTile", number);
                var chord = Whole(entry, "chord");
                var time = entry.TryGetProperty("ms", out var ms) && ms.ValueKind == System.Text.Json.JsonValueKind.Number
                    && ms.TryGetDouble(out var value) ? value : (double?)null;
                notes.Add(new NoteEditSession.NoteOverride(number, tile, chord, time));
            }
        }
        catch (System.Text.Json.JsonException error) { return T("JsonUnreadable", error.Message); }
        if (notes.Count == 0) return T("NoNotesNamed");
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

    // Practice maps are left out here too (user rule); link and link auto look in every song.
    IReadOnlyList<CmdSong> ICmdHost.Songs => !UseAppStore ? [] : _model.ListedSongs()
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
        if (_model.Playback.Timeline is not { CheckpointsVerified: true }) return T("LoadedNoCheckpoints");
        try { _model.Checkpoint = checkpoint; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            // The timeline's own reasons are Korean; the console says what happened in its own words.
            return T("CheckpointNotInSong");
        }
        // A placement failure empties the notes and the main window shows why, in its own language.
        return _model.Playback.Notes.Count == 0 ? T("NotPreparedThere") : null;
    }

    string? ICmdHost.StartAtTile(int tile)
    {
        if (_model.Playback.Timeline is not { } timeline) return T("NoSongLoaded");
        if (CmdSession.TileProblem(timeline, tile, _model.LoadedEntry?.SongName ?? T("TheLoadedSong")) is { } problem) return problem;
        if (!_model.StartAtTile(tile)) return T("NotPreparedFromTile");
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
        if (_pinsUnreadable is { } why) return T("PinsUnreadable", why);
        if (tiles.Count == 0) pins.Remove(family);
        else pins[family] = tiles.Distinct().Order().ToArray();
        if (!UseAppStore) return null;
        try
        {
            new PinBook(Storage).Write(pins);
            return null;
        }
        catch (Exception error) when (IsExpectedError(error)) { return T("PinsNotSaved", error.Message); }
    }

    /// <summary>Every redirect (song family → the console id of the song put in its place), read from redirects.json once; tests keep them in memory only.</summary>
    private Dictionary<string, string>? _redirects;
    /// <summary>Why redirects.json could not be read; nothing is written over it then, so no redirect is lost to a bad read.</summary>
    private string? _redirectsUnreadable;

    private Dictionary<string, string> RedirectMap
    {
        get
        {
            if (_redirects is not null) return _redirects;
            _redirects = new(StringComparer.Ordinal);
            if (!UseAppStore) return _redirects;
            try { foreach (var (family, id) in new RedirectBook(Storage).Read()) _redirects[family] = id; }
            catch (Exception error) when (IsExpectedError(error) || error is System.Text.Json.JsonException) { _redirectsUnreadable = error.Message; }
            return _redirects;
        }
    }

    IReadOnlyDictionary<string, string> ICmdHost.Redirects => RedirectMap;

    string? ICmdHost.SetRedirects(IReadOnlyDictionary<string, string> redirects)
    {
        ArgumentNullException.ThrowIfNull(redirects);
        var kept = RedirectMap;
        if (_redirectsUnreadable is { } why) return T("RedirectsUnreadable", why);
        // A change to the redirect of the song the game is on is acted on at once: link auto looks at that song again.
        var family = _linkAuto && Sync.Level is { } level && GameSongOf(level) is { } game ? _model.SongFamily?.Invoke(game) : null;
        var before = family is not null && kept.TryGetValue(family, out var was) ? was : null;
        var next = redirects.ToArray();
        kept.Clear();
        foreach (var (key, id) in next) kept[key] = id;
        if (family is not null && !string.Equals(before, kept.GetValueOrDefault(family), StringComparison.OrdinalIgnoreCase))
        {
            _autoLoadedPath = null;
            _autoTried = null;
        }
        if (!UseAppStore) return null;
        try
        {
            new RedirectBook(Storage).Write(kept);
            return null;
        }
        catch (Exception error) when (IsExpectedError(error)) { return T("RedirectsNotSaved", error.Message); }
    }

    string? ICmdHost.SetHidden(string id, bool hidden)
    {
        if (_model.SongWithId(id) is not { } entry) return T("NoSongWithIdShort", id);
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
        catch (Exception error) when (IsExpectedError(error)) { return T("SongListNotSaved", error.Message); }
    }

    /// <summary>The song list window opened from the console: confirming it applies at once, as there is no settings window to wait for.</summary>
    bool ICmdHost.OpenPlaylist()
    {
        if (AskSongList(_model.SongPreferences, _model.Settings) is not { } result) return false;
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
            done(UpdateCheck.Failed(T("NotFromRepository")));
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
            return T("NotFromRepository");
        start.RedirectStandardOutput = false;
        start.RedirectStandardError = false;
        try { Process.Start(start); }
        catch (Exception error) when (IsExpectedError(error) || error is System.ComponentModel.Win32Exception)
        {
            return T("UpdateCouldNotStart", error.Message);
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
            using var process = Process.Start(start) ?? throw new IOException(T("PowershellNotStarted"));
            process.OutputDataReceived += (_, e) => Read(e.Data);
            process.ErrorDataReceived += (_, e) => Read(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            if (lines.Count == 0) lines.Add(process.ExitCode == 0 ? T("ScriptDone") : T("ScriptUnfinished"));
        }
        catch (Exception error) when (IsExpectedError(error) || error is System.ComponentModel.Win32Exception)
        {
            lines.Add(T("ScriptCouldNotRun", error.Message));
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
        if (!UseAppStore) { problem = T("NotesNotThisRun"); return null; }
        try
        {
            return Reports.Read(title);
        }
        catch (Exception error) when (IsExpectedError(error)) { problem = error.Message; return null; }
    }

    string? ICmdHost.WriteReport(string title, IReadOnlyList<string> lines)
    {
        if (!UseAppStore) return T("NotesNotThisRun");
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
        if (!UseAppStore) return T("NotesNotThisRun");
        try
        {
            var version = state == ReportBook.Done ? AppInfo.Version : null;
            return Reports.SetState(title, state, version, why) ? null : T("NeverWritten", title);
        }
        catch (Exception error) when (IsExpectedError(error)) { return error.Message; }
    }

    string? ICmdHost.RenameReport(string title, string name)
    {
        if (!UseAppStore) return T("NotesNotThisRun");
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
        if (!UseAppStore) { problem = T("NotesNotThisRun"); return false; }
        try { return Reports.Delete(title); }
        catch (Exception error) when (IsExpectedError(error)) { problem = error.Message; return false; }
    }

    string? ICmdHost.RenameSong(string id, string name)
    {
        if (_model.SongWithId(id) is not { } entry) return T("NoSongWithIdShort", id);
        if (_model.SetDisplayName(entry, name) is { } problem) return problem;
        return SaveSongChoices();
    }

    string? ICmdHost.RemoveEdit(string id)
    {
        if (_model.SongWithId(id) is not { } entry) return T("NoSongWithIdShort", id);
        if (Maps.SlotOf(entry) is null) return T("OnlyEditsRemovable");
        try
        {
            Maps.Hold(entry, _model.DisplayName(entry));
            Forget(entry);
            return null;
        }
        catch (Exception error) when (IsExpectedError(error)) { return T("CouldNotRemove", error.Message); }
    }

    string? ICmdHost.HeldSong => UseAppStore ? Maps.HeldName : null;

    string? ICmdHost.Restore(out int slot)
    {
        slot = 0;
        if (!UseAppStore) return T("NothingToRestore");
        try
        {
            slot = Maps.Restore();
            // The slot is back on disk; scanning the edited folder puts it in the list again.
            _ = ScanEditedAsync();
            return null;
        }
        catch (Exception error) when (IsExpectedError(error)) { return T("CouldNotRestore", error.Message); }
    }

    string? ICmdHost.DeleteSong(string id) => _model.SongWithId(id) is { } entry
        ? RemoveSong(entry) is { } reason ? T("CouldNotDelete", reason) : null
        : T("NoSongWithIdShort", id);

    string? ICmdHost.EditSong(string id) => _model.SongWithId(id) is { } entry ? OpenEditor(entry, CmdSession.Language) : T("NoSongWithIdShort", id);

    string? ICmdHost.ApplySettings(VisualizerSettings settings)
    {
        try
        {
            _model.ApplySettings(settings);
            return null;
        }
        catch (NotePlacementException) { return T("CannotPlaceSettings"); }
        catch (Exception error) when (IsExpectedError(error)) { return error.Message; }
    }

    SettingsChange? ICmdHost.LastChange => _model.SettingsHistory.Count > 0 ? _model.SettingsHistory[^1] : null;

    string? ICmdHost.RollBack()
    {
        try
        {
            return _model.RollBack() is null ? T("NothingToRollBack") : null;
        }
        catch (ArgumentException) { return T("EarlierValuesDontFit"); }
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
