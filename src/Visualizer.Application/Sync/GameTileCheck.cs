using System.Globalization;
using Visualizer.Core.Maps;
using static Visualizer.Core.Localization.ConsoleText;

namespace Visualizer.Application.Sync;

/// <summary>
/// How one tile the game passed lines up with the hit this app computed for it. <paramref name="OffsetMs"/> is the game's
/// song position minus our hit time, so the same value for every tile means the two agree and only differ by where each
/// counts zero.
/// </summary>
public sealed record TileOffset(int Floor, int HitIndex, double GameTimeMs, double HitTimeMs, double OffsetMs);

/// <summary>What the comparison found. Offsets are milliseconds.</summary>
public sealed record TileCheckReport(int Matched, int Unmatched, double MinOffsetMs, double MedianOffsetMs,
    double MaxOffsetMs, double EarlyHalfMs, double LateHalfMs, TileOffset? Worst);

/// <summary>
/// Compares the tiles the game actually played with the hit times this app read from the map. This is the first check
/// against the game itself: the automated tests and the independent calculator share the same reading of the game's rules,
/// so agreeing with each other was never proof (docs/debug-logs/timing-mismatch.md).
/// </summary>
public static class GameTileCheck
{
    /// <summary>How far two schedules of the same map may differ and still count as the same, in milliseconds.</summary>
    public const double Tolerance = 1;

    /// <summary>
    /// Lines the game's own time for every tile up with the hit this app read from the same map. Both sides are schedules,
    /// not something the player did, so a map read the same way gives one offset for every tile.
    /// </summary>
    public static TileCheckReport Compare(IReadOnlyList<double> gameTimesByFloor, ParsedTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(gameTimesByFloor);
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.HitFloors is not { } floors) return new TileCheckReport(0, timeline.HitTimesMs.Count, 0, 0, 0, 0, 0, null);

        var matched = new List<TileOffset>(floors.Count);
        var unmatched = 0;
        for (var index = 0; index < floors.Count; index++)
        {
            var floor = floors[index];
            // A tile the game does not have is one side reading the map differently; it is counted, not guessed at.
            if ((uint)floor >= (uint)gameTimesByFloor.Count) { unmatched++; continue; }
            var game = gameTimesByFloor[floor];
            var hit = timeline.HitTimesMs[index];
            matched.Add(new TileOffset(floor, index, game, hit, game - hit));
        }
        if (matched.Count == 0) return new TileCheckReport(0, unmatched, 0, 0, 0, 0, 0, null);

        var offsets = matched.Select(tile => tile.OffsetMs).Order().ToArray();
        var middle = Percentile(offsets, 0.5);
        var half = matched.Count / 2;
        return new TileCheckReport(matched.Count, unmatched, offsets[0], middle, offsets[^1],
            Median(matched.Take(Math.Max(1, half))), Median(matched.Skip(half)),
            matched.MaxBy(tile => Math.Abs(tile.OffsetMs - middle)));
    }

    /// <summary>
    /// Whether the game's tile times are this timeline's own schedule, which names the song when its path does not: every
    /// hit's tile is one the game has, all of them differ by one offset within <see cref="Tolerance"/>, and the last hit is
    /// at the level's end (a short map that only agrees with the start of a longer one is another map). Stops at the first
    /// tile that disagrees, so trying it on every song in the list costs next to nothing.
    /// </summary>
    public static bool LinesUp(IReadOnlyList<double> gameTimesByFloor, ParsedTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(gameTimesByFloor);
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.HitFloors is not { Count: > 0 } floors || gameTimesByFloor.Count == 0) return false;
        var last = floors[^1];
        if (last >= gameTimesByFloor.Count || gameTimesByFloor[^1] - gameTimesByFloor[last] > Tolerance) return false;
        var low = double.PositiveInfinity;
        var high = double.NegativeInfinity;
        for (var index = 0; index < floors.Count; index++)
        {
            var offset = gameTimesByFloor[floors[index]] - timeline.HitTimesMs[index];
            low = Math.Min(low, offset);
            high = Math.Max(high, offset);
            if (high - low > Tolerance) return false;
        }
        return true;
    }

    private static double Median(IEnumerable<TileOffset> tiles)
    {
        var sorted = tiles.Select(tile => tile.OffsetMs).Order().ToArray();
        return sorted.Length == 0 ? 0 : Percentile(sorted, 0.5);
    }

    private static double Percentile(double[] sorted, double part) =>
        sorted[Math.Clamp((int)Math.Round(part * (sorted.Length - 1)), 0, sorted.Length - 1)];

    /// <summary>The report as the console prints it, in the console's language.</summary>
    public static IReadOnlyList<string> Lines(TileCheckReport report, string? warning = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
        var lines = new List<string>();
        if (warning != null) lines.Add(T("CarefulLine", warning));
        if (report.Matched == 0)
        {
            lines.Add(T("NoneMatched", report.Unmatched));
            return lines;
        }
        var spread = report.MaxOffsetMs - report.MinOffsetMs;
        lines.Add(T("TilesCompared", report.Matched) + (report.Unmatched > 0 ? T("TilesUnmatched", report.Unmatched) : string.Empty));
        lines.Add(T("TileOffset", Ms(report.MedianOffsetMs), Ms(report.MinOffsetMs), Ms(report.MaxOffsetMs)));
        lines.Add(T("TileSpread", Ms(spread)));
        lines.Add(T("TileDrift", Ms(report.EarlyHalfMs), Ms(report.LateHalfMs)));
        if (report.Worst is { } worst)
            lines.Add(T("TileWorst", worst.Floor, worst.HitIndex + 1, Ms(worst.GameTimeMs), Ms(worst.HitTimeMs), Ms(worst.OffsetMs - report.MedianOffsetMs)));
        // Both sides are schedules worked out from the same file, so agreeing means agreeing to the millisecond.
        lines.Add(spread <= Tolerance && report.Unmatched == 0
            ? T("TilesAgree") : T("TilesDisagree"));
        return lines;
    }
}
