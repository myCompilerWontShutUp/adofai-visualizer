using System.Globalization;
using Visualizer.Application.Playback;
using Visualizer.Application.Sync;
using Visualizer.Core.Visualization;

namespace Visualizer.App.Input;

/// <summary>One key link inspect sent: the note, its tile, when it was due and when it went, on the playback clock.</summary>
public sealed record InspectPress(int Note, int Tile, double DueMs, double SentMs)
{
    public double LateMs => SentMs - DueMs;
}

/// <summary>A judgment the game made during the run that is worth a look, with the tile it was on and the last key sent.</summary>
public sealed record InspectEvent(string Judgment, int Floor, double GameMs, InspectPress? LastPress);

/// <summary>
/// link inspect (diagnostics only; the narrow exception to AGENTS.md rule 6): presses the lane key of every visualizer note
/// at the moment that note reaches the judgment line on the visualizer's own clock, so the game judges the visualizer's
/// timing, not its own. The window plans the presses every frame from the notes it draws (<see cref="Plan"/>), and a thread
/// of its own sends them on time (<see cref="Step"/>); a key goes out only while the game is the window in front, and the
/// thread stops itself when the window stops planning (the visualizer is not drawing, so nobody can see what it presses).
/// </summary>
public sealed class LinkInspection : IDisposable
{
    /// <summary>How long a tap holds its key down.</summary>
    public const double TapMs = 25;
    /// <summary>A press this much after its moment counts as late (the thread could not keep up).</summary>
    public const double LateMs = 3;
    /// <summary>Planned no further ahead than this; the next frame plans again from the clock it then has.</summary>
    public const double AheadMs = 2000;
    /// <summary>Without a new plan for this long the visualizer is not drawing, and the run stops.</summary>
    public const double StaleMs = 500;

    private readonly object _gate = new();
    private readonly IMonotonicClock _clock;
    private readonly Action<int, bool> _send;
    private readonly Func<bool> _gameInFront;
    private readonly IReadOnlyDictionary<int, int> _keyOfTile;
    private readonly Dictionary<int, (int Tile, double PressAt, double ReleaseAt)> _plan = [];
    private readonly HashSet<int> _done = [];
    private readonly Dictionary<int, double> _held = [];
    private readonly List<InspectPress> _presses = [];
    private double _plannedAt = double.NaN;
    private int _lastNote = -1;
    private Thread? _thread;
    private volatile bool _stopping;

    /// <param name="keyOfTile">The virtual key of each tile's lane key (the settings' lane keys).</param>
    /// <param name="send">Sends a key down (true) or up; the window passes the real sender, tests their own.</param>
    /// <param name="gameInFront">Whether the game is the window in front right now. Asked before every press, from any thread.</param>
    public LinkInspection(IMonotonicClock clock, IReadOnlyDictionary<int, int> keyOfTile, Action<int, bool> send, Func<bool> gameInFront)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _keyOfTile = keyOfTile ?? throw new ArgumentNullException(nameof(keyOfTile));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _gameInFront = gameInFront ?? throw new ArgumentNullException(nameof(gameInFront));
    }

    /// <summary>Why the run stopped by itself (the game went out of front, nothing was planned for a while); null while it runs.</summary>
    public string? StopReason { get; private set; }

    /// <summary>The keys sent so far.</summary>
    public IReadOnlyList<InspectPress> Presses { get { lock (_gate) return _presses.ToArray(); } }

    /// <summary>The last key sent, or null.</summary>
    public InspectPress? LastPress { get { lock (_gate) return _presses.Count > 0 ? _presses[^1] : null; } }

    /// <summary>Whether every note planned so far has been pressed and let go, past the last note of the song.</summary>
    public bool Finished(int noteCount)
    {
        lock (_gate) return noteCount > 0 && _lastNote == noteCount - 1 && _held.Count == 0;
    }

    /// <summary>
    /// Plans the coming presses from what the window draws: a note is due when it reaches the judgment line, which is when the
    /// timeline is at its hit time plus the offsets. Notes already past on the first plan are not pressed (the run starts at
    /// the next one), and a note pressed once is never pressed again.
    /// </summary>
    public void Plan(IReadOnlyList<VisualizerNote> notes, double timelineMs, double offsetsMs, double nowMs)
    {
        ArgumentNullException.ThrowIfNull(notes);
        lock (_gate)
        {
            var first = double.IsNaN(_plannedAt);
            _plannedAt = nowMs;
            _plan.Clear();
            for (var index = 0; index < notes.Count; index++)
            {
                if (_done.Contains(index)) continue;
                var note = notes[index];
                var due = nowMs + (note.HitTimeMs + offsetsMs - timelineMs);
                if (first && due < nowMs + TapMs)
                {
                    _done.Add(index);
                    _lastNote = Math.Max(_lastNote, index);
                    continue;
                }
                if (due > nowMs + AheadMs) break;
                var release = note.HoldEndMs is double end ? nowMs + (end + offsetsMs - timelineMs) : due + TapMs;
                _plan[index] = (note.Tile, due, Math.Max(release, due + 1));
            }
        }
    }

    /// <summary>
    /// Sends whatever is due by <paramref name="nowMs"/>: releases first, then presses in order. False once the run has
    /// stopped by itself (<see cref="StopReason"/>); every key is up by then.
    /// </summary>
    public bool Step(double nowMs)
    {
        lock (_gate)
        {
            if (StopReason is not null) return false;
            if (!double.IsNaN(_plannedAt) && nowMs - _plannedAt > StaleMs) return Stop("the visualizer stopped drawing its notes (minimized or busy)");
            foreach (var key in _held.Where(held => held.Value <= nowMs).Select(held => held.Key).ToArray())
            {
                _send(key, false);
                _held.Remove(key);
            }
            foreach (var (note, plan) in _plan.Where(entry => !_done.Contains(entry.Key) && entry.Value.PressAt <= nowMs)
                         .OrderBy(entry => entry.Value.PressAt).ToArray())
            {
                if (!_keyOfTile.TryGetValue(plan.Tile, out var key)) return Stop($"lane {plan.Tile} has no lane key");
                // Asked right before the key goes: a key may only ever reach the game.
                if (!_gameInFront()) return Stop("the game window is not in front");
                if (_held.ContainsKey(key)) _send(key, false);
                _send(key, true);
                _held[key] = plan.ReleaseAt;
                _done.Add(note);
                _lastNote = Math.Max(_lastNote, note);
                _presses.Add(new InspectPress(note, plan.Tile, plan.PressAt, nowMs));
            }
            return true;
        }
    }

    /// <summary>The next moment something is due, or infinity when nothing is planned.</summary>
    public double NextDueMs()
    {
        lock (_gate)
        {
            var next = double.PositiveInfinity;
            foreach (var (note, plan) in _plan) if (!_done.Contains(note)) next = Math.Min(next, plan.PressAt);
            foreach (var until in _held.Values) next = Math.Min(next, until);
            return next;
        }
    }

    /// <summary>Starts the thread that sends the keys on time.</summary>
    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "link inspect", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            while (!_stopping)
            {
                if (!Step(_clock.NowMs)) break;
                var wait = NextDueMs() - _clock.NowMs;
                // A sleep is only good to about 15 ms on Windows, so the last stretch is spun out to keep the press on time.
                if (wait > 20 || double.IsInfinity(wait)) Thread.Sleep(1);
                else if (wait > 0) Thread.Yield();
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        {
            lock (_gate) StopReason ??= "a key could not be sent: " + error.Message;
        }
        finally { ReleaseAll(); }
    }

    private bool Stop(string why)
    {
        StopReason ??= why;
        ReleaseAll();
        return false;
    }

    private void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var key in _held.Keys.ToArray())
            {
                try { _send(key, false); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or ArgumentException) { }
                _held.Remove(key);
            }
        }
    }

    /// <summary>Stops the thread and lets every key go.</summary>
    public void Dispose()
    {
        _stopping = true;
        _thread?.Join(200);
        ReleaseAll();
    }
}

/// <summary>What one link inspect run left behind, kept in memory until the app closes and written out by report -inspect.</summary>
public sealed class InspectRun
{
    /// <summary>The judgments looked at one by one (HitMargin): TooEarly 0, TooLate 6, FailMiss 8, FailOverload 9.</summary>
    private static readonly IReadOnlyDictionary<int, string> Watched = new Dictionary<int, string>
    {
        [0] = "TooEarly", [6] = "TooLate", [8] = "FailMiss", [9] = "FailOverload"
    };
    private static readonly string[] MarginNames =
        ["TooEarly", "VeryEarly", "EarlyPerfect", "Perfect", "LatePerfect", "VeryLate", "TooLate", "Multipress", "FailMiss", "FailOverload", "Auto", "OverPress"];

    private int[]? _first;
    private int[]? _last;
    private readonly List<InspectEvent> _events = [];
    private readonly List<double> _stalls = [];

    public InspectRun(int number, DateTime startedAt, string song, string levelPath, string? modVersion)
    {
        Number = number;
        StartedAt = startedAt;
        Song = song;
        LevelPath = levelPath;
        ModVersion = modVersion;
    }

    public int Number { get; }
    public DateTime StartedAt { get; }
    public string Song { get; }
    public string LevelPath { get; }
    public string? ModVersion { get; }
    public IReadOnlyList<InspectPress> Presses { get; private set; } = [];
    public IReadOnlyList<InspectEvent> Events => _events;
    public string? StopReason { get; private set; }

    /// <summary>Looks at the game's judgment counts of one reading: each Miss, Overload, too early or too late is kept with its tile.</summary>
    public void Observe(GameSyncMessage reading, InspectPress? lastPress)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.Judgments is not { } counts) return;
        var now = counts.ToArray();
        _first ??= now;
        if (_last is { } before && before.Length == now.Length)
            foreach (var (margin, name) in Watched)
                if (margin < now.Length)
                    for (var extra = now[margin] - before[margin]; extra > 0; extra--)
                        _events.Add(new InspectEvent(name, reading.Floor, reading.PositionMs, lastPress));
        _last = now;
    }

    /// <summary>A frame of the visualizer's that came this late after the one before (the app itself could not keep up).</summary>
    public void Stall(double gapMs) => _stalls.Add(gapMs);

    public void Finish(string reason, IReadOnlyList<InspectPress> presses)
    {
        StopReason = reason;
        Presses = presses;
    }

    /// <summary>The run as the lines report -inspect writes (in English, as the console writes).</summary>
    public IReadOnlyList<string> Lines()
    {
        static string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            $"link inspect #{Number} · {StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} · {Song} · linker {ModVersion ?? "?"}",
            $"level: {LevelPath}"
        };
        var late = Presses.Where(press => press.LateMs > LinkInspection.LateMs).ToArray();
        lines.Add(Presses.Count == 0 ? "presses: none were sent"
            : $"presses: {Presses.Count} sent, {late.Length} later than {Ms(LinkInspection.LateMs)} ms"
              + (late.Length > 0 ? $" (most {Ms(late.Max(press => press.LateMs))} ms)" : "")
              + $", mean {Ms(Presses.Average(press => press.LateMs))} ms after their moment");
        lines.Add(_stalls.Count == 0 ? "frames: none came late" : $"frames: {_stalls.Count} came late (most {Ms(_stalls.Max())} ms apart)");
        if (_first is { } first && _last is { } last && first.Length == last.Length)
        {
            var changed = Enumerable.Range(0, last.Length).Where(margin => last[margin] != first[margin])
                .Select(margin => $"{(margin < MarginNames.Length ? MarginNames[margin] : margin.ToString(CultureInfo.InvariantCulture))} +{last[margin] - first[margin]}");
            lines.Add("judgments: " + (changed.Any() ? string.Join(", ", changed) : "none"));
        }
        else lines.Add("judgments: the linker did not send them");
        if (_events.Count == 0) lines.Add("problems: none");
        else
        {
            lines.Add($"problems: {_events.Count}");
            lines.AddRange(_events.Select(problem => $"  {problem.Judgment} at tile {problem.Floor} (game {Ms(problem.GameMs / 1000)} s)"
                + (problem.LastPress is { } press ? $", after note {press.Note + 1} on lane {press.Tile} sent {Ms(press.LateMs)} ms after its moment" : "")));
        }
        lines.Add($"stopped: {StopReason ?? "still running"}");
        return lines;
    }
}
