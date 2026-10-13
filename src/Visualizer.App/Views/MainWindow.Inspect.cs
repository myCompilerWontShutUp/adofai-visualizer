using Visualizer.App.Commands;
using Visualizer.App.Input;
using Visualizer.Infrastructure.Input;

namespace Visualizer.App.Views;

/// <summary>
/// link inspect: precise measurement only (user rule). It presses the lane key of every note the visualizer draws at the
/// moment the note reaches the judgment line, so the game judges the visualizer's timing; the user watches the notes and
/// where each press lands, and the game's Miss and Overload judgments are kept with their tiles for report -inspect.
/// It runs only while all four rules hold, checked when it starts and on every frame: the link is on with link auto off,
/// the game plays the loaded song, No Fail is on in the game (a clear with it never counts), and the game is in front.
/// </summary>
public partial class MainWindow
{
    /// <summary>Sends a key down or up. Tests put their own here; null is the real keyboard.</summary>
    public static Action<int, bool>? KeySender { get; set; }

    /// <summary>The frames between two of the visualizer's that count as a stall: it could not keep up while pressing.</summary>
    private const double InspectStallMs = 40;

    private LinkInspection? _inspection;
    private InspectRun? _inspectRun;
    private int _inspectPrepared;
    private int _inspectRuns;
    private double _inspectFrameMs = double.NaN;
    /// <summary>The game's process, found the first time it is in front; keys go only while this process is.</summary>
    private uint _gameProcess;
    /// <summary>Why the last run stopped, for link to show.</summary>
    private string? _inspectNote;
    /// <summary>Every run since the app opened, kept for report -inspect and gone when it closes (user rule).</summary>
    private readonly List<InspectRun> _inspected = [];

    IReadOnlyList<string> ICmdHost.GameSyncInspect(bool on)
    {
        if (!on)
        {
            if (_inspection is null) return ["Not inspecting."];
            StopInspect("link uninspect");
            return ["Stopped inspecting. report -inspect create <title> writes what it found."];
        }
        if (_inspection is not null) return ["Already inspecting. link uninspect or Esc stops it."];
        if (InspectRefusal() is { } why) return ["Cannot inspect: " + why];
        if (!_model.FollowingGame)
        {
            var said = ((ICmdHost)this).GameSyncFollow(true);
            if (!_model.FollowingGame) return ["Cannot inspect: " + said.FirstOrDefault()];
        }
        var keys = ControlBindings.CreateLaneKeys(_model.Settings).ToDictionary(pair => pair.Value, pair => pair.Key);
        var send = KeySender ?? (OperatingSystem.IsWindows() ? KeyboardInjector.Send : null);
        if (send is null) return ["Cannot inspect: keys can only be sent on Windows."];
        _gameProcess = 0;
        _inspectPrepared = _model.Playback.Prepared;
        _inspectFrameMs = double.NaN;
        var (reading, _) = Sync.Latest;
        _inspectRun = new InspectRun(++_inspectRuns, DateTime.Now, _model.LoadedSongName ?? "", Sync.Level?.Path ?? "", Sync.ModVersion);
        if (reading is not null) _inspectRun.Observe(reading, null);
        // Only this process's keys may go, and only to the game: the thread asks before every key whether the game's
        // process owns the window in front (found on a frame, never guessed from a name).
        _inspection = new LinkInspection(_model.PlaybackClock, keys, send,
            () => _gameProcess != 0 && WindowsForegroundObserver.ForegroundProcessId() == _gameProcess);
        _inspection.Start();
        _inspectNote = null;
        return
        [
            "Inspecting: every note the visualizer draws presses its lane key as it reaches the judgment line, into the game only.",
            "It waits until the game is in front. Esc, link uninspect, link auto, the end of the song or any rule breaking stops it.",
            "report -inspect create <title> writes what it found (write adds it to a note); the runs are kept until the app closes."
        ];
    }

    /// <summary>Why link inspect may not run now, or null. Every rule the user set for it, in their order.</summary>
    private string? InspectRefusal()
    {
        if (_syncServer is not { Listening: true }) return "the link is not on. link start first.";
        var (reading, _) = Sync.Latest;
        if (!_syncServer.Connected || reading is null) return "the linker is not connected. Start the game with the linker in it.";
        if (_linkAuto) return "link auto is on. It measures by hand only: link manual first.";
        if (Sync.Level is not { } level) return "the game has sent no level yet. Open the song in the game.";
        if (_model.LoadedEntry is not { } entry || !GamePlays(level.Path, entry)) return "the loaded song is not the one open in the game.";
        if (reading.NoFail is null) return "the linker does not say whether No Fail is on (an older one). install linker, then start the game again.";
        if (reading.NoFail != true) return "No Fail is off in the game. Turn it on: with it on, a clear never counts.";
        return null;
    }

    /// <summary>One frame of a run: the rules again, where the presses go next, and what the game judged.</summary>
    private void InspectFrame()
    {
        if (_inspection is null || _inspectRun is null) return;
        if (_inspection.StopReason is { } stopped) { StopInspect(stopped); return; }
        if (InspectRefusal() is { } why) { StopInspect(why); return; }
        if (!_model.FollowingGame) { StopInspect("following the game stopped"); return; }
        if (_model.Playback.Prepared != _inspectPrepared) { StopInspect("the notes were prepared again"); return; }
        if (_gameProcess == 0 && WindowsForegroundObserver.IsProcessForeground(_model.Settings.AdofaiProcessName))
            _gameProcess = WindowsForegroundObserver.ForegroundProcessId();
        var now = _model.PlaybackClock.NowMs;
        if (!double.IsNaN(_inspectFrameMs) && now - _inspectFrameMs > InspectStallMs) _inspectRun.Stall(now - _inspectFrameMs);
        _inspectFrameMs = now;
        var playback = _model.Playback;
        _inspection.Plan(playback.Notes, playback.TimelineTimeMs, playback.Offsets.TotalMs, now);
        var (reading, _) = Sync.Latest;
        if (reading is not null) _inspectRun.Observe(reading, _inspection.LastPress);
        if (_inspection.Finished(playback.Notes.Count)) StopInspect("the song ended");
    }

    /// <summary>Stops a run: every key goes up, and what it found is kept for report -inspect.</summary>
    private void StopInspect(string reason)
    {
        if (_inspection is not { } inspection || _inspectRun is not { } run) return;
        _inspection = null;
        _inspectRun = null;
        inspection.Dispose();
        var (reading, _) = Sync.Latest;
        if (reading is not null) run.Observe(reading, inspection.LastPress);
        run.Finish(reason, inspection.Presses);
        _inspected.Add(run);
        _inspectNote = reason;
        _model.SetMessage(_model.Text.Format("InspectStopped", run.Number));
    }

    IReadOnlyList<string>? ICmdHost.InspectLines(int count)
    {
        if (_inspected.Count == 0) return null;
        var runs = count <= 0 ? _inspected : _inspected.Skip(Math.Max(0, _inspected.Count - count));
        return runs.SelectMany(run => run.Lines().Append("")).SkipLast(1).ToArray();
    }

    /// <summary>The link status line about inspect.</summary>
    private string InspectLine() => _inspection is { } inspection
        ? $"Inspect  running · {inspection.Presses.Count} keys sent{(_gameProcess == 0 ? " · waiting for the game to be in front" : "")}"
        : _inspectNote is { } note ? $"Inspect  off · the last run stopped: {note} · {_inspected.Count} kept for report -inspect"
        : "Inspect  off; link inspect presses the visualizer's notes into the game for a measurement";
}
