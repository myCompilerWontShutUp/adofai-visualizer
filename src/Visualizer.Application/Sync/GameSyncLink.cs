using System.Diagnostics;
using System.Globalization;
using Visualizer.Application.Playback;

namespace Visualizer.Application.Sync;

/// <summary>
/// One reading the game sent. <paramref name="PositionMs"/> is the input-calibrated song position: the clock the game
/// judges a press against, which is what falling notes are for. <paramref name="InputOffsetMs"/> and
/// <paramref name="VisualOffsetMs"/> are the player's own calibration in the game, kept only to show.
/// <paramref name="PitchRate"/> is the real playback rate, unlike the practice speed setting.
/// </summary>
/// <remarks>
/// <see cref="NoFail"/> is the game's No Fail, null when the mod did not say (an older one); <see cref="Judgments"/> its
/// judgment counts by HitMargin (TooEarly 0 … FailMiss 8, FailOverload 9), null likewise. Only link inspect reads them.
/// </remarks>
public sealed record GameSyncMessage(int Protocol, double ModClockMs, double PositionMs, string State, bool Paused,
    int Floor, double Speed, string Path, double PitchRate = 1, double InputOffsetMs = 0, double VisualOffsetMs = 0,
    bool Dead = false, bool? NoFail = null, IReadOnlyList<int>? Judgments = null);

/// <summary>
/// A tile the game moved onto, with the song position it was noticed at. The mod only looks once a frame, so the crossing
/// happened somewhere in the frame before this position: the reading is late by 0 to one frame, never early.
/// </summary>
public sealed record FloorCrossing(int Floor, double PositionMs);

/// <summary>
/// The times the game worked out for every tile of a level, in milliseconds, sent once when it loads. This is the game's
/// own schedule, so it settles the timing without watching the player move.
/// </summary>
public sealed record GameSyncLevel(string Path, IReadOnlyList<double> TimesMs);

/// <summary>What the link saw in one measurement window. Times are milliseconds unless the name says seconds.</summary>
public sealed record GameSyncReport(int Accepted, int Rejected, string? FirstRejection, double WindowSeconds,
    double IntervalMinMs, double IntervalMedianMs, double IntervalP95Ms, double IntervalMaxMs,
    double LatencyP95Ms, double LatencyMaxMs, double PositionRate,
    int LevelTiles, string? ModVersion, GameSyncMessage? Last, IReadOnlyList<string> Changes);

/// <summary>
/// Takes the readings the sync mod sends, checks them and measures the link: how often they arrive, how much the delay
/// varies and whether the song position advances with real time. Nothing here is persisted (AGENTS.md rule 5) and nothing
/// is sent back (<c>docs/game-sync-plan.md</c>).
/// </summary>
public sealed class GameSyncLink
{
    /// <summary>The message shape this app understands. A different one is refused instead of guessed at.</summary>
    public const int Protocol = 2;
    /// <summary>About half a minute of readings at the rate the mod sends them.</summary>
    private const int Capacity = 4000;
    /// <summary>How many state changes the report shows, newest last.</summary>
    private const int ChangeCapacity = 8;

    private sealed class OwnClock : IMonotonicClock
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        public double NowMs => _watch.Elapsed.TotalMilliseconds;
    }

    private readonly object _gate = new();
    private readonly IMonotonicClock _clock;
    private readonly double[] _intervals = new double[Capacity];
    private readonly double[] _latencies = new double[Capacity];
    /// <summary>How far apart two readings may be and still count as the song simply playing on.</summary>
    private const double MaxStepMs = 500;
    private readonly List<FloorCrossing> _crossings = [];
    private readonly List<string> _changes = [];
    private int _count;
    private int _next;
    private double _startedMs;
    private double _lastArrivedMs = double.NaN;
    private double _lastModMs = double.NaN;
    private double _lastPositionMs = double.NaN;
    private double _playedMs;
    private double _realMs;
    private int _accepted;
    private int _rejected;
    private string? _firstRejection;
    private GameSyncMessage? _last;
    private GameSyncLevel? _level;
    private string? _modVersion;

    /// <summary>
    /// <paramref name="clock"/> should be the one the playback session runs on, so a reading's arrival can be compared
    /// with the playhead; without one the link keeps its own, which is enough for measuring the link alone.
    /// </summary>
    public GameSyncLink(IMonotonicClock? clock = null)
    {
        _clock = clock ?? new OwnClock();
        Reset();
    }

    /// <summary>Milliseconds since this link was made; the caller stamps arrivals with it.</summary>
    public double NowMs => _clock.NowMs;

    /// <summary>Whether anything has been accepted since the last reset.</summary>
    public bool HasReadings { get { lock (_gate) return _accepted > 0; } }

    /// <summary>The tiles the game has moved onto in the run it is playing now, oldest first.</summary>
    public IReadOnlyList<FloorCrossing> Crossings { get { lock (_gate) return _crossings.ToArray(); } }

    /// <summary>The newest reading and when it arrived, for following the game's clock.</summary>
    public (GameSyncMessage? Reading, double ArrivedMs) Latest
    {
        get { lock (_gate) return (_last, _lastArrivedMs); }
    }

    /// <summary>The build the connected mod came from, or null when it never said (an older mod, or none connected).</summary>
    public string? ModVersion { get { lock (_gate) return _modVersion; } }

    /// <summary>Takes the mod's own version, which it sends first on every connection.</summary>
    public void SetMod(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        lock (_gate) _modVersion = version;
    }

    /// <summary>The tile times of the level the game is playing, or null until it has sent them.</summary>
    public GameSyncLevel? Level { get { lock (_gate) return _level; } }

    /// <summary>The level the game last reported, or null before anything arrived.</summary>
    public string? LevelPath { get { lock (_gate) return _last?.Path; } }

    /// <summary>The practice speed the game last reported; 1 when nothing has arrived.</summary>
    public double Speed { get { lock (_gate) return _last?.Speed ?? 1; } }

    /// <summary>Starts a new window: the samples, counts and state changes so far are dropped.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _count = 0;
            _next = 0;
            _accepted = 0;
            _rejected = 0;
            _firstRejection = null;
            _last = null;
            _lastArrivedMs = double.NaN;
            _lastModMs = double.NaN;
            _lastPositionMs = double.NaN;
            _changes.Clear();
            _crossings.Clear();
            // The level's tile times are reference data, not a measurement, so a new window keeps them.
            _playedMs = 0;
            _realMs = 0;
            _startedMs = NowMs;
        }
    }

    /// <summary>
    /// A mod connected. Its clock and its run start over with it (a restarted game counts from zero again), and it sends who
    /// it is and the level it has before anything else, so what the last connection said is dropped rather than mixed in.
    /// The measurement window stays: link -reset is what starts that over.
    /// </summary>
    public void BeginConnection()
    {
        lock (_gate)
        {
            if (_last is not null) Note("the mod connected again");
            _last = null;
            _lastArrivedMs = double.NaN;
            _lastModMs = double.NaN;
            _lastPositionMs = double.NaN;
            _crossings.Clear();
            _level = null;
            _modVersion = null;
        }
    }

    /// <summary>
    /// Checks one reading and takes it in. Returns null when it was accepted, otherwise why it was refused. Called from the
    /// thread that reads the pipe, so everything is under the lock.
    /// </summary>
    public string? Add(GameSyncMessage message, double arrivedMs)
    {
        ArgumentNullException.ThrowIfNull(message);
        var refusal = Check(message, arrivedMs);
        lock (_gate)
        {
            if (refusal != null)
            {
                _rejected++;
                _firstRejection ??= refusal;
                return refusal;
            }
            // The mod clock must not go backwards within a run; if it does, the reading is not from the run we are measuring.
            if (double.IsFinite(_lastModMs) && message.ModClockMs < _lastModMs)
            {
                _rejected++;
                _firstRejection ??= "the mod clock went backwards";
                return "the mod clock went backwards";
            }
            if (_last is { } previous && previous.State != message.State)
                Note($"{previous.State} -> {message.State} at {message.PositionMs.ToString("0", CultureInfo.InvariantCulture)} ms"
                     + (message.Paused ? " (paused)" : string.Empty));
            else if (_last is { } wasPaused && wasPaused.Paused != message.Paused)
                Note($"{(message.Paused ? "paused" : "resumed")} at {message.PositionMs.ToString("0", CultureInfo.InvariantCulture)} ms");

            // A new level, or the song put back to the start, ends the run: the tiles seen so far belong to the old one.
            if (_last is { } before && (before.Path != message.Path || message.PositionMs < _lastPositionMs - MaxStepMs))
            {
                _crossings.Clear();
                if (before.Path != message.Path && _level?.Path != message.Path) _level = null;
            }
            else if (_last is { } moved && message.Floor > moved.Floor && _crossings.Count < Capacity)
                _crossings.Add(new FloorCrossing(message.Floor, message.PositionMs));

            if (double.IsFinite(_lastArrivedMs) && arrivedMs > _lastArrivedMs)
            {
                var gap = arrivedMs - _lastArrivedMs;
                _intervals[_next] = gap;
                _latencies[_next] = arrivedMs - message.ModClockMs;
                _next = (_next + 1) % Capacity;
                if (_count < Capacity) _count++;
                // How fast the song position moves against real time, summed over the pairs where it simply played on.
                // A per-reading ratio is useless here: the game moves the position once a frame while the readings arrive
                // on their own, so single ratios swing wildly and only the totals mean anything.
                var step = message.PositionMs - _lastPositionMs;
                if (gap < MaxStepMs && step > 0 && step < MaxStepMs && !message.Paused && _last is { Paused: false })
                {
                    _playedMs += step;
                    _realMs += gap;
                }
            }
            _accepted++;
            _last = message;
            _lastArrivedMs = arrivedMs;
            _lastModMs = message.ModClockMs;
            _lastPositionMs = message.PositionMs;
            return null;
        }
    }

    /// <summary>Takes the tile times of a level. A level is only ever sent once, so the newest one wins.</summary>
    public void SetLevel(GameSyncLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (level.TimesMs.Any(time => !double.IsFinite(time))) { Refuse("a tile time was not a number"); return; }
        lock (_gate) _level = level;
    }

    /// <summary>Counts a line that could not be read as a reading at all, so the console can say the link is talking nonsense.</summary>
    public void Refuse(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            _rejected++;
            _firstRejection ??= reason;
        }
    }

    /// <summary>
    /// What the mod said it is. An older mod says nothing at all, which looks the same as one that sends no tile times,
    /// so that case is named instead of left to guess at.
    /// </summary>
    private static string ModLine(string? modVersion, string? appVersion) => modVersion is null
        ? "Mod      an older build: it never said its version. Build it again and restart the game."
        : appVersion is not null && !appVersion.StartsWith(modVersion, StringComparison.Ordinal)
            ? $"Mod      {modVersion}, but this app is {appVersion}. Build the mod again and restart the game."
            : $"Mod      {modVersion}";

    private static string? Check(GameSyncMessage message, double arrivedMs)
    {
        if (message.Protocol != Protocol) return $"message shape {message.Protocol}, this app reads {Protocol}";
        if (!double.IsFinite(arrivedMs)) return "the arrival time is not a number";
        if (!double.IsFinite(message.ModClockMs) || message.ModClockMs < 0) return "the mod clock is not a time";
        if (!double.IsFinite(message.PositionMs)) return "the song position is not a number";
        // The practice speed and the calibrations are only shown, so an odd value there never costs the position.
        if (message.Floor < -1) return "the tile number is negative";
        if (string.IsNullOrWhiteSpace(message.State)) return "the state is empty";
        if (string.IsNullOrWhiteSpace(message.Path)) return "the level path is empty";
        return null;
    }

    private void Note(string change)
    {
        _changes.Add(change);
        if (_changes.Count > ChangeCapacity) _changes.RemoveAt(0);
    }

    /// <summary>The window as it stands. Null before anything was accepted or refused.</summary>
    public GameSyncReport? Report()
    {
        lock (_gate)
        {
            if (_accepted == 0 && _rejected == 0) return null;
            var intervals = Sorted(_intervals);
            var latencies = Sorted(_latencies);
            var middle = latencies.Length > 0 ? Percentile(latencies, 0.5) : 0;
            return new GameSyncReport(_accepted, _rejected, _firstRejection, (NowMs - _startedMs) / 1000,
                intervals.Length > 0 ? intervals[0] : 0,
                intervals.Length > 0 ? Percentile(intervals, 0.5) : 0,
                intervals.Length > 0 ? Percentile(intervals, 0.95) : 0,
                intervals.Length > 0 ? intervals[^1] : 0,
                latencies.Length > 0 ? Percentile(latencies, 0.95) - middle : 0,
                latencies.Length > 0 ? latencies[^1] - middle : 0,
                _realMs > 0 ? _playedMs / _realMs : 0,
                _level?.TimesMs.Count ?? 0, _modVersion, _last, _changes.ToArray());
        }
    }

    private double[] Sorted(double[] samples)
    {
        var taken = new double[_count];
        Array.Copy(samples, taken, _count);
        Array.Sort(taken);
        return taken;
    }

    private static double Percentile(double[] sorted, double part) =>
        sorted[Math.Clamp((int)Math.Round(part * (sorted.Length - 1)), 0, sorted.Length - 1)];

    /// <summary>The report as the console prints it (English, like the rest of the console).</summary>
    public static IReadOnlyList<string> Lines(GameSyncReport? report, bool listening, bool connected, string? problem,
        string? appVersion = null)
    {
        var lines = new List<string>();
        lines.Add(listening
            ? connected ? "Listening on the sync pipe · the mod is connected."
                : "Listening on the sync pipe · no mod connected yet. It looks for the pipe every two seconds, so run sync again in a moment."
            : "Not listening. Use link start to open the pipe.");
        if (problem != null) lines.Add("Problem  " + problem);
        if (report is not { } r)
        {
            if (listening) lines.Add("Nothing received yet. Start a song in the game with the mod switched on.");
            return lines;
        }
        string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
        lines.Add($"Readings {r.Accepted} accepted · {r.Rejected} refused over {Ms(r.WindowSeconds)} s"
                  + (r.FirstRejection is { } why ? $" (first: {why})" : string.Empty));
        if (r.Accepted > 1)
        {
            lines.Add($"Arrival  min {Ms(r.IntervalMinMs)} · median {Ms(r.IntervalMedianMs)} · p95 {Ms(r.IntervalP95Ms)} · max {Ms(r.IntervalMaxMs)} ms apart");
            lines.Add($"Delay    p95 +{Ms(r.LatencyP95Ms)} · max +{Ms(r.LatencyMaxMs)} ms over its usual delay");
            lines.Add($"Position advancing {r.PositionRate.ToString("0.000", CultureInfo.InvariantCulture)} ms per ms of real time"
                      + " (song time against the clock, over the readings where it simply played on)");
        }
        lines.Add(ModLine(r.ModVersion, appVersion));
        lines.Add(r.LevelTiles > 0
            ? $"Level    the game sent its own times for {r.LevelTiles} tiles · link tiles compares them with the loaded song"
            : "Level    no tile times yet; they arrive when the game loads a level.");
        if (r.Last is { } last)
        {
            lines.Add($"Game     {last.State}{(last.Paused ? " · paused" : string.Empty)}{(last.Dead ? " · over" : string.Empty)} · tile {last.Floor}"
                      + $" · position {Ms(last.PositionMs)} ms · rate {(last.PitchRate * 100).ToString("0", CultureInfo.InvariantCulture)}%"
                      + $" (practice setting {(last.Speed * 100).ToString("0", CultureInfo.InvariantCulture)}%)");
            lines.Add($"Offsets  the game's input {Ms(last.InputOffsetMs)} ms · visual {Ms(last.VisualOffsetMs)} ms"
                      + " · notes follow the input one, so only this app's calibration shifts them");
            lines.Add("Playing  " + last.Path);
        }
        foreach (var change in r.Changes) lines.Add("Change   " + change);
        return lines;
    }
}
