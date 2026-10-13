using System.Windows;
using Visualizer.App.Commands;
using static Visualizer.Core.Localization.ConsoleText;
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
            if (_inspection is null) return [T("NotInspecting")];
            StopInspect("link -inspect stop");
            return [T("StoppedInspecting")];
        }
        if (_inspection is not null) return [T("AlreadyInspecting")];
        if (InspectRefusal() is { } why) return [T("CannotInspect", why)];
        if (!_model.FollowingGame)
        {
            var said = ((ICmdHost)this).GameSyncFollow(true);
            if (!_model.FollowingGame) return [T("CannotInspect", said.FirstOrDefault())];
        }
        var keys = ControlBindings.CreateLaneKeys(_model.Settings).ToDictionary(pair => pair.Value, pair => pair.Key);
        var send = KeySender ?? (OperatingSystem.IsWindows() ? KeyboardInjector.Send : null);
        if (send is null) return [T("CannotInspect", T("WindowsOnly"))];
        // Found by name now rather than caught in front on some frame: the run must know the game before its first note.
        _gameProcess = WindowsForegroundObserver.FindProcess(_model.Settings.AdofaiProcessName);
        _inspectPrepared = _model.Playback.Prepared;
        _inspectFrameMs = double.NaN;
        var (reading, _) = Sync.Latest;
        _inspectRun = new InspectRun(++_inspectRuns, DateTime.Now, _model.LoadedSongName ?? "", Sync.Level?.Path ?? "", Sync.ModVersion);
        if (reading is not null) _inspectRun.Observe(reading, null);
        // Only this process's keys may go, and only to the game: the thread asks before every key whether the game's
        // process owns the window in front (found on a frame, never guessed from a name).
        _inspection = new LinkInspection(_model.PlaybackClock, keys, send,
            () => _gameProcess != 0 && WindowsForegroundObserver.ForegroundProcessId() == _gameProcess,
            () => DescribeFront(WindowsForegroundObserver.ForegroundProcessId()));
        _inspection.Start();
        _inspectNote = null;
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
        if (_model.LoadedEntry is not { } entry || !GamePlays(level.Path, entry)) return T("InspectOtherSong");
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
        if (_model.Playback.Prepared != _inspectPrepared) { StopInspect(T("NotesPreparedAgain")); return; }
        if (_gameProcess == 0 && WindowsForegroundObserver.IsProcessForeground(_model.Settings.AdofaiProcessName))
            _gameProcess = WindowsForegroundObserver.ForegroundProcessId();
        ShowInspectBanner();
        var now = _model.PlaybackClock.NowMs;
        if (!double.IsNaN(_inspectFrameMs) && now - _inspectFrameMs > InspectStallMs) _inspectRun.Stall(now - _inspectFrameMs);
        _inspectFrameMs = now;
        var playback = _model.Playback;
        _inspection.Plan(playback.Notes, playback.TimelineTimeMs, playback.Offsets.TotalMs, now);
        var (reading, _) = Sync.Latest;
        if (reading is not null) _inspectRun.Observe(reading, _inspection.LastPress);
        if (_inspection.Finished(playback.Notes.Count)) StopInspect(T("SongEnded"));
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
        run.Finish(reason, inspection.Presses, inspection.Skipped);
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
    /// The banner in the middle of the notes while a run is on (user rule): it says keys are being pressed into the game, or
    /// that the run waits for the game to come to the front, and how to stop it.
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

    /// <summary>The link status line about inspect.</summary>
    private string InspectLine() => _inspection is { } inspection
        ? T("InspectRunningLine", inspection.Presses.Count) + (inspection.WaitingForGame ? T("InspectWaitingPart") : "")
        : _inspectNote is { } note ? T("InspectStoppedLine", note, _inspected.Count)
        : T("InspectOffLine");
}
