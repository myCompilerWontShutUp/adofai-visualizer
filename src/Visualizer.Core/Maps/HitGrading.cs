namespace Visualizer.Core.Maps;

/// <summary>
/// The grades a press can get, in the game's own order (HitMargin 0..8 since the game build of 2026-11-04: an exact middle
/// band, XPerfect, was added and Perfect split around it).
/// </summary>
public enum HitGrade
{
    TooEarly = 0,
    VeryEarly = 1,
    EarlyPerfect = 2,
    PerfectMinus = 3,
    XPerfect = 4,
    PerfectPlus = 5,
    LatePerfect = 6,
    VeryLate = 7,
    TooLate = 8
}

/// <summary>
/// The four edges of the game's grading for one hit, in real milliseconds either side of the hit. Counted is where a press
/// still counts, Perfect where early/late perfect ends, Pure where perfect ends and XPerfect the exact middle band.
/// </summary>
public sealed record HitWindows(double CountedMs, double PerfectMs, double PureMs, double XPerfectMs)
{
    /// <summary>
    /// The grade of a press <paramref name="deviationMs"/> late (negative is early), as scrMisc.SelectHitMarginByTimeBoundary
    /// picks it: the middle band first (early edge open, late edge closed), then the edges outwards, each open on its inner side.
    /// </summary>
    public HitGrade Grade(double deviationMs)
    {
        var d = deviationMs;
        if (d > -XPerfectMs && d <= XPerfectMs) return HitGrade.XPerfect;
        var grade = HitGrade.TooEarly;
        if (d > -CountedMs) grade = HitGrade.VeryEarly;
        if (d > -PerfectMs) grade = HitGrade.EarlyPerfect;
        if (d > -PureMs) grade = HitGrade.PerfectMinus;
        if (d > 0) grade = HitGrade.PerfectPlus;
        if (d > PureMs) grade = HitGrade.LatePerfect;
        if (d > PerfectMs) grade = HitGrade.VeryLate;
        if (d > CountedMs) grade = HitGrade.TooLate;
        return grade;
    }

    /// <summary>
    /// Where a press <paramref name="deviationMs"/> late sits on a bar drawn with <paramref name="layout"/>'s zones, from -1
    /// (the early end) to 1 (the late end): each of this hit's own edges lands on the same edge of the bar, and in between it
    /// moves evenly. So a press always sits in the zone of its grade, also when the bar shows another tile's windows (a
    /// different tempo or margin scale); with the same windows it is simply the deviation over the counted window.
    /// </summary>
    public double Place(double deviationMs, HitWindows layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var d = Math.Abs(deviationMs);
        var sign = Math.Sign(deviationMs);
        if (d >= CountedMs) return sign;
        // Kept in order, as a tiny margin scale can put the middle band past the perfect edge.
        double[] own = Ordered(XPerfectMs, PureMs, PerfectMs, CountedMs);
        double[] bar = Ordered(layout.XPerfectMs / layout.CountedMs, layout.PureMs / layout.CountedMs,
            layout.PerfectMs / layout.CountedMs, 1);
        for (var edge = 1; edge < own.Length; edge++)
        {
            if (d > own[edge]) continue;
            var span = own[edge] - own[edge - 1];
            var along = span > 0 ? (d - own[edge - 1]) / span : 1;
            return sign * Math.Min(1, bar[edge - 1] + along * (bar[edge] - bar[edge - 1]));
        }
        return sign;

        static double[] Ordered(double middle, double pure, double perfect, double counted)
        {
            double[] edges = [0, middle, pure, perfect, counted];
            for (var i = 1; i < edges.Length; i++) edges[i] = Math.Max(edges[i], edges[i - 1]);
            return edges;
        }
    }
}

/// <summary>
/// The game's hit windows, worked out the way it grades (game build of 2026-11-04, read from Assembly-CSharp):
/// scrMisc.GetAdjustedAngleBoundaryInDeg takes each edge as the larger of a fixed angle times the tile's margin scale
/// (Counted GCS.HITMARGIN_COUNTED = 60°, Perfect 45°, Pure 30°) and the angle a minimum time covers at the tile's speed
/// (scrMisc.GetMinimumTimes: Counted 91 / 65 / 40 ms for Lenient / Normal / Strict, Perfect 30 ms, Pure 25 ms; the
/// smaller minimums are floored at 25 ms), and the middle band as the larger of 12.5° and 1/60 s. The planet turns 180° a
/// beat (scrMisc.TimeToAngleInRad), so the angles become time through the tile's tempo. Speed trial and mobile minimums
/// are not covered. The judgment ease (user request) multiplies the minimum times, as the linker does in the game when
/// it is on; the middle band's 1/60 s is not one of them.
/// </summary>
public static class HitGrading
{
    /// <summary>scrMisc.GetMinimumTimes for Lenient, Normal and Strict (Difficulty 0, 1, 2), in milliseconds.</summary>
    private static readonly double[] CountedFloorMs = [91, 65, 40];
    private const double PerfectFloorMs = 30;
    private const double PureFloorMs = 25;
    private const double XPerfectFloorMs = 1000.0 / 60;

    /// <summary>The game's Difficulty number for a JudgmentBarDifficulty name; Normal for anything else.</summary>
    public static int DifficultyOf(string? name) => name?.Trim() switch { "Lenient" => 0, "Strict" => 2, _ => 1 };

    /// <summary>The largest judgment ease the settings take: three times the game's minimum times.</summary>
    public const double MaximumEase = 3;

    /// <summary>
    /// The windows of a hit reached at <paramref name="beatsPerMinute"/> real beats a minute (the tile's tempo times the
    /// song's pitch) with the tile's <paramref name="marginScale"/>, at <paramref name="difficulty"/> (0 Lenient, 1 Normal,
    /// 2 Strict), with the minimum times multiplied by <paramref name="ease"/> (1 is the game's own judgment).
    /// </summary>
    public static HitWindows Windows(double beatsPerMinute, double marginScale, int difficulty, double ease = 1)
    {
        if (!double.IsFinite(beatsPerMinute) || beatsPerMinute <= 0) throw new ArgumentOutOfRangeException(nameof(beatsPerMinute));
        if (!double.IsFinite(marginScale) || marginScale < 0) throw new ArgumentOutOfRangeException(nameof(marginScale));
        if ((uint)difficulty > 2) throw new ArgumentOutOfRangeException(nameof(difficulty));
        CheckEase(ease);
        // 180 degrees a beat.
        var msPerDegree = 60000 / beatsPerMinute / 180;
        return new HitWindows(
            Math.Max(60 * marginScale * msPerDegree, CountedFloorMs[difficulty] * ease),
            Math.Max(45 * marginScale * msPerDegree, PerfectFloorMs * ease),
            Math.Max(30 * marginScale * msPerDegree, PureFloorMs * ease),
            Math.Max(12.5 * msPerDegree, XPerfectFloorMs));
    }

    private static void CheckEase(double ease)
    {
        if (!double.IsFinite(ease) || ease < 1 || ease > MaximumEase) throw new ArgumentOutOfRangeException(nameof(ease));
    }

    /// <summary>
    /// Windows when the tile's tempo is not known (a cache from before tempos were kept): the difficulty's counted floor with
    /// the angle ratios, which is what a tile of about 300 BPM gets.
    /// </summary>
    public static HitWindows Fallback(int difficulty, double ease = 1)
    {
        if ((uint)difficulty > 2) throw new ArgumentOutOfRangeException(nameof(difficulty));
        CheckEase(ease);
        var counted = CountedFloorMs[difficulty] * ease;
        return new HitWindows(counted, Math.Max(counted * 0.75, PerfectFloorMs * ease), Math.Max(counted * 0.5, PureFloorMs * ease),
            Math.Max(CountedFloorMs[difficulty] * 12.5 / 60, XPerfectFloorMs));
    }
}
