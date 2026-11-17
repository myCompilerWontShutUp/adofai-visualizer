using System.Globalization;
using System.Windows;
using Visualizer.App.Commands;
using static Visualizer.Core.Localization.ConsoleText;
using Visualizer.App.Input;
using Visualizer.Infrastructure.Input;

namespace Visualizer.App.Views;

/// <summary>
/// link inspect: precise measurement only (user rule). It presses the lane key of every note the visualizer draws at the
/// moment the note reaches the judgment line, so the game judges the visualizer's timing; the user watches the notes and
/// where each press lands, and the game's Miss and Overload judgments are kept with their tiles for inspect result.
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
    /// <summary>Every run since the app opened, kept for inspect result and gone when it closes (user rule).</summary>
    private readonly List<InspectRun> _inspected = [];

    bool ICmdHost.Inspecting => _inspection is not null;

    IReadOnlyList<string> ICmdHost.GameSyncInspect(bool on, bool flow, IProgress<double>? progress, Action? ended)
    {
        if (!on)
        {
            if (_inspection is null) return [T("NotInspecting")];
            StopInspect("inspect stop");
            return [T("StoppedInspecting")];
        }
        if (_inspection is not null) return [T("AlreadyInspecting")];
        if (InspectRefusal() is { } why) return [T("CannotInspect", why)];
        if (!_model.FollowingGame)
        {
            var said = ((ICmdHost)this).GameSyncFollow(true);
            if (!_model.FollowingGame) return [T("CannotInspect", said.FirstOrDefault())];
        }
        // Esc after stopping inspect may have paused the app while the link still owns its clock.
        // A fresh inspection hands it back to the game; the follower waits if the game itself is paused.
        if (_model.Playback.State == Visualizer.Application.Playback.PlaybackState.Paused) _model.Start();
        var keys = ControlBindings.CreateLaneKeys(_model.Settings).ToDictionary(pair => pair.Value, pair => pair.Key);
        var send = KeySender ?? (OperatingSystem.IsWindows() ? KeyboardInjector.Send : null);
        if (send is null) return [T("CannotInspect", T("WindowsOnly"))];
        // Found now rather than caught in front on some frame: the run must know the game before its first note. The game
        // the linker follows comes first; by name, a second game running (the game starts twice now and then, user report)
        // could be the other one, and the keys would wait for it for ever.
        _gameProcess = _syncServer?.ClientProcessId is uint linked && linked != 0
            ? linked : WindowsForegroundObserver.FindProcess(_model.Settings.AdofaiProcessName);
        _inspectPrepared = _model.Playback.Prepared;
        _inspectFrameMs = double.NaN;
        var (reading, _) = Sync.Latest;
        _inspectRun = new InspectRun(++_inspectRuns, DateTime.Now, _model.LoadedSongName ?? "", Sync.Level?.Path ?? "", Sync.ModVersion,
            _model.Playback.Settings);
        if (reading is not null) _inspectRun.Observe(reading, null);
        // Only this process's keys may go, and only to the game: the thread asks before every key whether the game's
        // process owns the window in front (found on a frame, never guessed from a name).
        _inspection = new LinkInspection(_model.PlaybackClock, keys, send,
            () => _gameProcess != 0 && WindowsForegroundObserver.ForegroundProcessId() == _gameProcess,
            () => DescribeFront(WindowsForegroundObserver.ForegroundProcessId()),
            Enumerable.Range(0, _model.Playback.Notes.Count).Select(_model.Playback.SourceFloorOfNote).ToArray());
        _inspection.Start();
        _inspectNote = null;
        _inspectFlow = flow;
        _flowProgress = flow ? progress : null;
        _flowEnded = flow ? ended : null;
        _flowPercent = -1;
        _flowPresses = 0;
        _flowEvents = 0;
        _flowWarmed = 0;
        _inspectWarmedUp = false;
        _inspectKeys = keys;
        Log("LogInspectOn", _inspectRun.Number);
        ShowInspectBanner();
        return
        [
            T("Inspecting1"), T("Inspecting2"), T("Inspecting3")
        ];
    }

    /// <summary>Why link inspect may not run now, or null. Every rule the user set for it, in their order.</summary>
    private string? InspectRefusal()
    {
        if (_syncServer is not { Listening: true }) return T("InspectNoLink");
        var (reading, _) = Sync.Latest;
        if (!_syncServer.Connected || reading is null) return T("InspectNoLinker");
        if (_linkAuto) return T("InspectAutoOn");
        if (Sync.Level is not { } level) return T("InspectNoLevel");
        if (_model.LoadedEntry is not { } entry || !GamePlays(level, entry)) return T("InspectOtherSong");
        if (reading.NoFail is null) return T("InspectOldLinker");
        if (reading.NoFail != true) return T("InspectNoFailOff");
        return null;
    }

    /// <summary>What was in front instead of the game, for the stop reason (the user's report could not tell it).</summary>
    private string DescribeFront(uint front) =>
        T("InFront", WindowsForegroundObserver.ProcessName(front) ?? T("Nothing"), front)
        + (_gameProcess == 0 ? T("NoGameProcess", _model.Settings.AdofaiProcessName) : T("GamePid", _gameProcess));

    /// <summary>One frame of a run: the rules again, where the presses go next, and what the game judged.</summary>
    private void InspectFrame()
    {
        if (_inspection is null || _inspectRun is null) return;
        if (_inspection.StopReason is { } stopped) { StopInspect(stopped); return; }
        if (InspectRefusal() is { } why) { StopInspect(why); return; }
        if (!_model.FollowingGame) { StopInspect(T("FollowingEnded")); return; }
        // The link moved to another game: the keys must not go after it.
        if (_syncServer?.ClientProcessId is uint followed && followed != 0 && _gameProcess != 0 && followed != _gameProcess)
        {
            StopInspect(T("InspectOtherGame", followed));
            return;
        }
        if (_model.Playback.Prepared != _inspectPrepared) { StopInspect(T("NotesPreparedAgain")); return; }
        if (_gameProcess == 0 && WindowsForegroundObserver.IsProcessForeground(_model.Settings.AdofaiProcessName))
            _gameProcess = WindowsForegroundObserver.ForegroundProcessId();
        ShowInspectBanner();
        var now = _model.PlaybackClock.NowMs;
        if (!double.IsNaN(_inspectFrameMs) && now - _inspectFrameMs > InspectStallMs) _inspectRun.Stall(now - _inspectFrameMs);
        _inspectFrameMs = now;
        var playback = _model.Playback;
        var advancing = playback.State == Visualizer.Application.Playback.PlaybackState.Playing
            && _follower.State == Visualizer.Application.Sync.GameSyncFollowState.Following;
        if (_inspection.Plan(playback.Notes, playback.TimelineTimeMs, playback.Offsets.TotalMs, now, advancing, playback.HideNotesBeforeMs))
            _inspectWarmedUp = false;
        var (reading, _) = Sync.Latest;
        if (reading is not null) _inspectRun.Observe(reading, _inspection.LastPress);
        // Into the countdown: every lane key, Space and Enter once (user request), so the game's input is awake for tile 1.
        if (reading?.State == "Countdown" && advancing && !_inspectWarmedUp)
        {
            var firstDue = _inspection.NextNoteDueMs();
            if (double.IsFinite(firstDue))
            {
                _inspection.WarmUp([.. _inspectKeys.Values, SpaceKey, EnterKey], now, firstDue - LinkInspection.WarmQuietMs);
                _inspectWarmedUp = true;
            }
        }
        else if (reading?.State != "Countdown" || !advancing) _inspection.CancelWarmUp();
        if (_inspectFlow)
        {
            FlowLines(playback.Notes, reading);
            // The console's bar: notes pressed of the notes there are, told only when the percent moves.
            var percent = playback.Notes.Count == 0 ? 0 : _inspection.Presses.Count * 100 / playback.Notes.Count;
            if (percent != _flowPercent) { _flowPercent = percent; _flowProgress?.Report(percent / 100.0); }
        }
        if (_inspection.Finished(playback.Notes.Count)) StopInspect(T("SongEnded"));
    }

    /// <summary>Stops a run: every key goes up, and what it found is kept for inspect result.</summary>
    private void StopInspect(string reason)
    {
        if (_inspection is not { } inspection || _inspectRun is not { } run) return;
        _inspection = null;
        _inspectRun = null;
        inspection.Dispose();
        var (reading, _) = Sync.Latest;
        if (reading is not null) run.Observe(reading, inspection.LastPress);
        run.Finish(reason, inspection.Presses, inspection.Skipped);
        if (_inspectFlow)
        {
            FlowLines(_model.Playback.Notes, reading, inspection, run);
            CmdLog.Shared.Write([T("FlowStopped", run.Number, reason)]);
            _inspectFlow = false;
            var ended = _flowEnded;
            _flowEnded = null;
            _flowProgress = null;
            ended?.Invoke();
        }
        Log("LogInspectOff", run.Number, reason);
        ShowInspectBanner();
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

    /// <summary>
    /// The banner at the top of the lanes for as long as a run is on (user rule): it says keys are being pressed into the
    /// game, or that the run waits for the game to come to the front, and how to stop it.
    /// </summary>
    private void ShowInspectBanner()
    {
        if (_inspection is not { } inspection)
        {
            InspectBanner.Visibility = Visibility.Collapsed;
            return;
        }
        var text = inspection.WaitingForGame
            ? _model.Text["InspectWaiting"]
            : _model.Text.Format("InspectRunning", inspection.Presses.Count);
        if (!Equals(InspectBannerText.Text, text)) InspectBannerText.Text = text;
        InspectBanner.Visibility = Visibility.Visible;
    }

    private bool _inspectFlow;
    private IProgress<double>? _flowProgress;
    private Action? _flowEnded;
    private int _flowPercent;
    private int _flowPresses;
    private int _flowEvents;
    private int _flowWarmed;
    private bool _inspectWarmedUp;
    private IReadOnlyDictionary<int, int> _inspectKeys = new Dictionary<int, int>();
    private const int SpaceKey = 0x20;
    private const int EnterKey = 0x0D;

    /// <summary>
    /// inspect -flow (user request): every key the run sent since the last frame, and every judgment worth a look, goes
    /// to the console as it happens, one line each, so the run can be watched while it plays.
    /// </summary>
    private void FlowLines(IReadOnlyList<Visualizer.Core.Visualization.VisualizerNote> notes,
        Visualizer.Application.Sync.GameSyncMessage? reading, LinkInspection? from = null, InspectRun? finished = null)
    {
        if ((from ?? _inspection) is not { } inspection || (finished ?? _inspectRun) is not { } run) return;
        var lines = new List<string>();
        var warmed = inspection.Warmed;
        if (warmed.Count > _flowWarmed)
        {
            lines.Add(T("FlowWarm", string.Join(" ", warmed.Skip(_flowWarmed).Select(KeyName))));
            _flowWarmed = warmed.Count;
        }
        var presses = inspection.Presses;
        foreach (var press in presses.Skip(_flowPresses))
            lines.Add(T("FlowPress", KeyName(_inspectKeys.TryGetValue(press.Tile, out var key) ? key : 0), press.Note + 1, press.Tile,
                press.Note < notes.Count ? notes[press.Note].HitTimeMs.ToString("0.0", CultureInfo.InvariantCulture) : "-",
                reading?.Floor ?? 0, press.LateMs.ToString("0.0", CultureInfo.InvariantCulture),
                press.SourceFloor?.ToString(CultureInfo.InvariantCulture) ?? "?")
                + (press.PreviousKeyGapMs is double gap ? T("InspectKeyGap", gap.ToString("0.0", CultureInfo.InvariantCulture)) : ""));
        _flowPresses = presses.Count;
        foreach (var judged in run.Events.Skip(_flowEvents)) lines.Add(T("FlowJudgment", judged.Judgment, judged.Floor));
        _flowEvents = run.Events.Count;
        if (lines.Count > 0) CmdLog.Shared.Write(lines);

        static string KeyName(int key) => key == 0 ? "?" : System.Windows.Input.KeyInterop.KeyFromVirtualKey(key).ToString();
    }

    /// <summary>The link status line about inspect.</summary>
    private string InspectLine() => _inspection is { } inspection
        ? T("InspectRunningLine", inspection.Presses.Count) + (inspection.WaitingForGame ? T("InspectWaitingPart") : "")
        : _inspectNote is { } note ? T("InspectStoppedLine", note, _inspected.Count)
        : T("InspectOffLine");
}
