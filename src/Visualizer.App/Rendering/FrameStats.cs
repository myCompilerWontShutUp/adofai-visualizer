using System.Diagnostics;
using System.Globalization;

namespace Visualizer.App.Rendering;

/// <summary>
/// What one measurement window saw. Times are milliseconds. <paramref name="WindowSeconds"/> is what the kept samples cover,
/// while <paramref name="MeasuredSeconds"/> is the whole time since the measurement started, which is what the collection
/// counts belong to. The compositor times come from the frame event itself, so a gap there is a frame the system never gave,
/// while a steady compositor with a jumpy arrival means the frame reached this app late.
/// </summary>
public sealed record FrameReport(int Frames, double WindowSeconds, double MeasuredSeconds,
    double MinMs, double MedianMs, double P95Ms, double MaxMs,
    double FrameMinMs, double FrameMedianMs, double FrameP95Ms, double FrameMaxMs,
    double WorkMedianMs, double WorkP95Ms, double WorkMaxMs,
    double HandoffP95Ms, double HandoffMaxMs,
    int LateFrames, int SkippedFrames, double LateAboveMs,
    int Gen0, int Gen1, int Gen2, int RenderTier);

/// <summary>
/// Frame timing the app measures on itself: when the frames arrived, when the compositor meant them to, how long its own
/// work took, and the garbage collections meanwhile. Only counts and times are kept, never input or song data.
/// </summary>
public sealed class FrameStats
{
    /// <summary>About twenty seconds at 120 Hz; older frames fall out of the window.</summary>
    private const int Capacity = 2400;
    /// <summary>A frame is late when it came this much later than the usual one (half again as long).</summary>
    private const double LateFactor = 1.5;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double[] _intervals = new double[Capacity];
    private readonly double[] _frameGaps = new double[Capacity];
    private readonly double[] _work = new double[Capacity];
    private readonly double[] _handoff = new double[Capacity];
    private int _count;
    private int _next;
    private double _lastMs = double.NaN;
    private double _lastFrameMs = double.NaN;
    private double _startedMs;
    private int _gen0;
    private int _gen1;
    private int _gen2;

    public FrameStats() => Reset();

    /// <summary>Milliseconds since this object was made; the caller times its own work with it.</summary>
    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Starts a new measurement window: the samples so far are dropped and the collection counts noted again.</summary>
    public void Reset()
    {
        _count = 0;
        _next = 0;
        _lastMs = double.NaN;
        _lastFrameMs = double.NaN;
        _startedMs = NowMs;
        _gen0 = GC.CollectionCount(0);
        _gen1 = GC.CollectionCount(1);
        _gen2 = GC.CollectionCount(2);
    }

    /// <summary>
    /// Takes one frame: <paramref name="arrivedMs"/> is when this app got it (<see cref="NowMs"/>),
    /// <paramref name="compositorMs"/> the time the frame event carries, and <paramref name="workMs"/> how long this app took
    /// in it. The first frame after a reset only sets the marks, and a repeated compositor time is the same frame again.
    /// </summary>
    public void Add(double arrivedMs, double compositorMs, double workMs)
    {
        if (!double.IsFinite(arrivedMs) || !double.IsFinite(workMs) || workMs < 0) return;
        // Without a compositor time (a caller that has none) the arrival stands in for it, so the gaps simply match.
        if (!double.IsFinite(compositorMs)) compositorMs = arrivedMs;
        var previous = _lastMs;
        var previousFrame = _lastFrameMs;
        // WPF can raise the event twice for one frame; the second one is not a new frame.
        if (double.IsFinite(previousFrame) && compositorMs == previousFrame) return;
        _lastMs = arrivedMs;
        _lastFrameMs = compositorMs;
        if (!double.IsFinite(previous) || arrivedMs <= previous) return;
        _intervals[_next] = arrivedMs - previous;
        _frameGaps[_next] = double.IsFinite(previousFrame) && compositorMs > previousFrame ? compositorMs - previousFrame : arrivedMs - previous;
        _work[_next] = workMs;
        // The two clocks have their own starting points, so only how much this varies means anything.
        _handoff[_next] = arrivedMs - compositorMs;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>The window as it stands, or null before two frames have been seen.</summary>
    public FrameReport? Report(int renderTier)
    {
        if (_count == 0) return null;
        var intervals = Sorted(_intervals);
        var gaps = Sorted(_frameGaps);
        var work = Sorted(_work);
        var handoff = Sorted(_handoff);
        var median = Percentile(intervals, 0.5);
        var late = intervals.Count(interval => interval > median * LateFactor);
        // A late frame whose compositor gap is just as long was never handed out; the rest reached this app late.
        var skipped = Enumerable.Range(0, _count)
            .Count(i => _intervals[i] > median * LateFactor && _frameGaps[i] > median * LateFactor);
        var handoffMiddle = Percentile(handoff, 0.5);
        return new FrameReport(_count, intervals.Sum() / 1000, (NowMs - _startedMs) / 1000,
            intervals[0], median, Percentile(intervals, 0.95), intervals[^1],
            gaps[0], Percentile(gaps, 0.5), Percentile(gaps, 0.95), gaps[^1],
            Percentile(work, 0.5), Percentile(work, 0.95), work[^1],
            Percentile(handoff, 0.95) - handoffMiddle, handoff[^1] - handoffMiddle,
            late, skipped, median * LateFactor,
            GC.CollectionCount(0) - _gen0, GC.CollectionCount(1) - _gen1, GC.CollectionCount(2) - _gen2, renderTier);
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
    public static IReadOnlyList<string> Lines(FrameReport? report)
    {
        if (report is not { } r) return ["No frames measured yet. Play or move the window, then try again."];
        string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
        var fps = r.MedianMs > 0 ? 1000 / r.MedianMs : 0;
        return
        [
            $"Frames {r.Frames} in {Ms(r.WindowSeconds)} s · {Ms(fps)} fps at the median (measuring for {Ms(r.MeasuredSeconds)} s)",
            $"Arrival   min {Ms(r.MinMs)} · median {Ms(r.MedianMs)} · p95 {Ms(r.P95Ms)} · max {Ms(r.MaxMs)} ms",
            $"Composer  min {Ms(r.FrameMinMs)} · median {Ms(r.FrameMedianMs)} · p95 {Ms(r.FrameP95Ms)} · max {Ms(r.FrameMaxMs)} ms",
            $"Handoff   p95 +{Ms(r.HandoffP95Ms)} · max +{Ms(r.HandoffMaxMs)} ms over its usual delay",
            $"App work  median {Ms(r.WorkMedianMs)} · p95 {Ms(r.WorkP95Ms)} · max {Ms(r.WorkMaxMs)} ms",
            $"Late {r.LateFrames} frames over {Ms(r.LateAboveMs)} ms · {r.SkippedFrames} of them never came from the compositor",
            $"Collections gen0 {r.Gen0} · gen1 {r.Gen1} · gen2 {r.Gen2} over the {Ms(r.MeasuredSeconds)} s measured",
            $"Render tier {r.RenderTier} ({(r.RenderTier >= 2 ? "hardware" : r.RenderTier == 1 ? "partial hardware" : "software")})",
            Verdict(r)
        ];
    }

    /// <summary>Which way the numbers point, so the next step is not guesswork.</summary>
    private static string Verdict(FrameReport r)
    {
        if (r.WorkP95Ms > r.MedianMs / 2) return "The drawing itself is heavy: look at the app first.";
        if (r.LateFrames == 0) return "Nothing stands out in this window.";
        if (r.SkippedFrames > r.LateFrames / 2)
            return "The compositor skipped those frames, so it is outside the app: the window being covered, the GPU busy with the game, or a variable refresh rate.";
        if (r.HandoffMaxMs > r.MedianMs)
            return "The frames came on time but reached this app late: thread scheduling, which MMCSS or a higher priority can help.";
        return "The late frames are not the drawing: collections, composition or power. Compare with the collection counts above.";
    }
}
