using System.Globalization;
using Visualizer.App.Commands;
using Visualizer.Application.Playback;
using Visualizer.Application.Sync;
using Visualizer.Infrastructure.Input;
using static Visualizer.Core.Localization.ConsoleText;

namespace Visualizer.App.Views;

/// <summary>
/// The console's log (user request): what the link, the game, following and the notes did, so a moment that went wrong
/// during play can be read afterwards with log or written into a note with report -log. Kept in memory only, and no key
/// that is pressed is ever kept (AGENTS.md rule 5).
/// </summary>
public partial class MainWindow
{
    private readonly EventLog _events = new();

    /// <summary>What the last frame saw; a line goes into the log for each thing that differs.</summary>
    private sealed record Seen(bool Listening, bool Connected, uint Client, int Games, string? Mod, GameSyncLevel? Level, string? State,
        bool Paused, GameSyncFollowState Follow, bool Following, string? AutoNote, int Prepared, PlaybackState Playback,
        string? Song, string Message);

    private Seen? _seen;
    private double _lastLogFrameMs = double.NaN;
    private double _lastSnapshotMs = double.NegativeInfinity;
    private double _lastProcessCheckMs = double.NegativeInfinity;
    private string _processes = "";

    /// <summary>How often a snapshot is kept while the game plays or the notes follow it.</summary>
    private const double SnapshotMs = 1000;
    /// <summary>How often the game's processes are counted while the linker is connected; listing them is not free.</summary>
    private const double ProcessCheckMs = 10_000;
    /// <summary>A gap between frames longer than this is kept: the app was minimized, busy or blocked.</summary>
    private const double FrameGapMs = 500;

    private void Log(string key, params object?[] args) => _events.Add(T(key, args));

    IReadOnlyList<string> ICmdHost.LogLines() => _events.Lines();

    void ICmdHost.ClearLog() => _events.Clear();

    /// <summary>
    /// One frame: what changed since the last one goes into the log, and while the game plays (or the notes follow it) a
    /// snapshot of where the game and the notes stand once a second. Called from the frame loop after following.
    /// </summary>
    private void LogFrame()
    {
        var now = _model.PlaybackClock.NowMs;
        if (!double.IsNaN(_lastLogFrameMs) && now - _lastLogFrameMs > FrameGapMs)
            Log("LogFrameGap", Ms(now - _lastLogFrameMs));
        _lastLogFrameMs = now;
        var reading = _sync?.Latest.Reading;
        var playback = _model.Playback;
        var seen = new Seen(_syncServer?.Listening == true, _syncServer?.Connected == true, _syncServer?.ClientProcessId ?? 0,
            _syncServer?.Games ?? 0, _sync?.ModVersion, _sync?.Level, reading?.State, reading?.Paused == true, _follower.State, _model.FollowingGame, _autoNote,
            playback.Prepared, playback.State, _model.LoadedSongName, _model.Message);
        var was = _seen;
        _seen = seen;
        if (was is null || was.Listening != seen.Listening) Log(seen.Listening ? "LogPipeOpen" : "LogPipeClosed");
        if (seen.Connected && (was is null || !was.Connected || was.Client != seen.Client))
        {
            _processes = Processes();
            _lastProcessCheckMs = now;
            Log("LogLinkerIn", seen.Client, _processes);
        }
        else if (was is { Connected: true } && !seen.Connected) Log("LogLinkerOut");
        // A second game connecting, or going (the game starts twice now and then, user report).
        if (was is not null && seen.Games != was.Games && (seen.Games > 1 || was.Games > 1)) Log("LogGames", seen.Games, seen.Client);
        if (seen.Mod is { } mod && mod != was?.Mod) Log("LogMod", mod);
        if (seen.Level is { } level && !ReferenceEquals(level, was?.Level)) Log("LogLevel", level.Path, level.TimesMs.Count);
        if (reading is not null && (reading.State != was?.State || reading.Paused != was?.Paused))
            Log("LogGame", reading.State + (reading.Paused ? T("GamePaused") : "") + (reading.Dead ? T("GameOver") : ""),
                reading.Floor, Ms(reading.PositionMs));
        if (was is null || seen.Follow != was.Follow) Log("LogFollow", T("Follow." + seen.Follow), _follower.OffsetMs is { } offset ? Ms(offset) : "-");
        if (was is not null && seen.Following != was.Following) Log(seen.Following ? "LogFollowOn" : "LogFollowOff");
        if (seen.AutoNote is { } note && note != was?.AutoNote) Log("LogAuto", note);
        if (seen.Song != was?.Song) Log("LogSong", seen.Song ?? "-");
        if (was is null || seen.Prepared != was.Prepared)
            Log("LogPrepared", playback.Notes.Count, playback.StartTile?.ToString(CultureInfo.InvariantCulture) ?? "-",
                Ms(_model.Settings.PlaybackSpeedPercent));
        if (was is null || seen.Playback != was.Playback) Log("LogPlayback", seen.Playback);
        if (seen.Message.Length > 0 && seen.Message != was?.Message) Log("LogMessage", seen.Message);

        // Where things stand, once a second while there is something to stand: the game's song and the notes on screen.
        var playing = reading?.State is "PlayerControl" or "Countdown";
        if ((playing || seen.Following) && now - _lastSnapshotMs >= SnapshotMs)
        {
            _lastSnapshotMs = now;
            Log("LogSnapshot", reading?.State ?? "-", reading?.Floor ?? 0, reading is null ? "-" : Ms(reading.PositionMs),
                Ms(playback.TimelineTimeMs), playback.State, T("Follow." + _follower.State), NotesOnScreen(playback, hidden: false),
                NotesOnScreen(playback, hidden: true));
        }
        // A second game running (a known ADOFAI bug starts it twice) is easy to miss, and only the one connected is followed.
        if (seen.Connected && now - _lastProcessCheckMs >= ProcessCheckMs)
        {
            _lastProcessCheckMs = now;
            var processes = Processes();
            if (processes != _processes) Log("LogProcesses", processes);
            _processes = processes;
        }
    }

    /// <summary>The game's processes as the log names them: how many, and their ids.</summary>
    private string Processes()
    {
        if (!OperatingSystem.IsWindows()) return "-";
        var ids = WindowsForegroundObserver.FindProcesses(_model.Settings.AdofaiProcessName);
        return T("LogProcessList", ids.Count, string.Join(", ", ids));
    }

    /// <summary>
    /// How many prepared notes are between the top of the field and the judgment line right now: the ones drawn, or with
    /// <paramref name="hidden"/> the ones a game restart hides (tiles before the one it carries on from).
    /// </summary>
    private int NotesOnScreen(PlaybackSession playback, bool hidden)
    {
        var notes = playback.Notes;
        if (notes.Count == 0) return 0;
        var from = playback.TimelineTimeMs - playback.Offsets.TotalMs;
        var until = from + _model.Settings.NoteTravelTimeMs;
        var cut = Math.Clamp(playback.HideNotesBeforeMs, from, until);
        (from, until) = hidden ? (from, cut) : (cut, until);
        int First(double time)
        {
            int low = 0, high = notes.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (notes[middle].HitTimeMs < time) low = middle + 1;
                else high = middle;
            }
            return low;
        }
        return First(until) - First(from);
    }

    private static string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
