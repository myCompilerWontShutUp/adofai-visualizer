using Visualizer.Core.Settings;

namespace Visualizer.Core.Visualization;

/// <summary>
/// A prepared note. <paramref name="StartsChord"/> marks the first of two notes drawn as one chord with the next note.
/// <paramref name="HoldEndMs"/> is set for a long note: its key is held until then (the release plays the next hit).
/// </summary>
public sealed record VisualizerNote(double HitTimeMs, int Tile, int Mode, bool StartsChord = false, double? HoldEndMs = null,
    bool Checkpoint = false)
{
    public bool IsHold => HoldEndMs is not null;

    /// <summary>The same note re-based in time (<c>time - origin + start</c>), long note end included.</summary>
    public VisualizerNote Rebased(double originMs, double startMs) =>
        this with { HitTimeMs = HitTimeMs - originMs + startMs, HoldEndMs = HoldEndMs - originMs + startMs };
}

/// <summary>Consumes verified hit times; does not infer ADOFAI timing.</summary>
public sealed class NoteGenerator
{
    /// <summary>How much longer the gap before a note must be than the gap after it to count as opening a run.</summary>
    private const double RunGapFactor = 1.5;

    /// <summary>
    /// How long a burst has to be to count as a run (user rule): two notes are not a pattern, and past eight it is a
    /// stream that would keep starting over.
    /// </summary>
    public const int MinimumRunNotes = 3;
    public const int MaximumRunNotes = 8;

    /// <summary>
    /// The notes in the burst that starts here: the ones that keep the same gap, give or take <see cref="RunGapFactor"/>.
    /// Counting stops once it is past the longest run that matters, so a stream costs nothing to measure.
    /// </summary>
    private static int RunNotes(double[] times, int start)
    {
        if (start + 1 >= times.Length) return 1;
        var gap = times[start + 1] - times[start];
        if (gap <= 0) return 1;
        var notes = 2;
        for (var i = start + 1; i + 1 < times.Length && notes <= MaximumRunNotes; i++)
        {
            var next = times[i + 1] - times[i];
            if (next > gap * RunGapFactor || next * RunGapFactor < gap) break;
            notes++;
        }
        return notes;
    }

    public IReadOnlyList<VisualizerNote> Generate(IEnumerable<double> hitTimesMs, VisualizerSettings settings) =>
        Generate(hitTimesMs, null, settings);

    /// <param name="holdEndsMs">Per hit, the end of its long note, or null for a tap. Null means every hit is a tap.</param>
    public IReadOnlyList<VisualizerNote> Generate(IEnumerable<double> hitTimesMs, IReadOnlyList<double?>? holdEndsMs, VisualizerSettings settings)
    {
        settings.EnsureValid();
        var times = hitTimesMs.ToArray();
        for (var i = 0; i < times.Length; i++)
        {
            if (!double.IsFinite(times[i]) || times[i] < 0 || (i > 0 && times[i] < times[i - 1]))
                throw new ArgumentException("Hit times must be finite, nonnegative and nondecreasing in ms.", nameof(hitTimesMs));
        }
        if (holdEndsMs is not null && (holdEndsMs.Count != times.Length
            || Enumerable.Range(0, times.Length).Any(i => holdEndsMs[i] is double end && (!double.IsFinite(end) || end <= times[i]))))
            throw new ArgumentException("Long note ends must match the hits and come after them.", nameof(holdEndsMs));
        if (times.Length == 0) return Array.Empty<VisualizerNote>();
        var groups = SimultaneousNotePlanner.Group(times, settings);
        times = groups.Select(g => g.TimeMs).ToArray();
        // A long note keeps its lane busy until it ends (user rule): a later note on that lane takes the next pattern lane.
        var busyUntil = SimultaneousNotePlanner.NewBusyLanes();
        var selector = new DynamicTimingSelector(settings);
        // No interval exists for a single note: dynamic mode uses the slowest (1K) case.
        var initialMode = selector.Select(times.Length > 1 ? times[1] - times[0] : double.MaxValue);
        var cursor = new TilePatternCursor(settings, initialMode);
        // The tile count may be held through a magic circle (user rule); the runs are found from the same display times.
        var magic = settings.UseMagicCircleTiles ? MagicCircleFinder.Find(times, settings) : [];
        var magicIndex = 0;
        var notes = new List<VisualizerNote>(times.Length);
        // Notes in a row that ask for fewer tiles; the smaller pattern waits until there are DownshiftNoteCount of them.
        var lowerRun = 0;
        for (var i = 0; i < times.Length; i++)
        {
            // Which gap decides this note's tile count. The gap after it is what the hand has to be ready for; the gap
            // before it is what the note just came from (user rule, the long-standing behaviour). The last note has no
            // gap after it, so it keeps the one it came from and stays with its run.
            var requested = settings.NextIntervalTiles
                ? i + 1 < times.Length ? selector.Select(times[i + 1] - times[i])
                    : i > 0 ? selector.Select(times[i] - times[i - 1]) : initialMode
                : i == 0 ? initialMode : selector.Select(times[i] - times[i - 1]);
            // Anticipate 1K -> 2K and 2K -> 4K only: start the faster cycle on the first member of the close run, rather
            // than opening one more slow cycle that repeats its tiles. Reading the gap after already does that, so this
            // only applies to the other way round.
            var anticipated = !settings.NextIntervalTiles && i > 0 && cursor.CurrentMode == requested
                && requested is 1 or 2 && i + 1 < times.Length
                && selector.Select(times[i + 1] - times[i]) == requested * 2;
            if (anticipated) requested *= 2;
            // A single slow note does not drop the tile count (user rule): the request has to repeat.
            if (settings.ImmediateDownshift && requested < cursor.CurrentMode)
            {
                lowerRun++;
                if (lowerRun < settings.DownshiftNoteCount) requested = cursor.CurrentMode;
                else lowerRun = 0;
            }
            else lowerRun = 0;
            while (magicIndex < magic.Count && magic[magicIndex].EndMs < times[i]) magicIndex++;
            if (magicIndex < magic.Count && magic[magicIndex].Contains(times[i]))
            {
                // Inside a run the count is fixed, and entering one switches at once instead of waiting for the cycle.
                anticipated = cursor.CurrentMode != settings.MagicCircleTiles;
                requested = settings.MagicCircleTiles;
                lowerRun = 0;
            }
            // A note whose gap before it is longer than the gap after it opens a run (experimental): the pattern starts
            // again there, so a burst of three reads 4-3-2 / 4-3-2 rather than carrying the last cycle's phase.
            if (settings.UseRestartOnRun && i > 0 && i + 1 < times.Length
                && times[i] - times[i - 1] > (times[i + 1] - times[i]) * RunGapFactor
                && RunNotes(times, i) is >= MinimumRunNotes and <= MaximumRunNotes)
                cursor.RestartCycle();
            var tile = cursor.Next(requested, anticipated);
            // Chord groups are re-placed by the planner, which checks busy lanes itself. A note exactly on a long note's
            // end may share its lane: that is where the key comes up, and two holds in a row are pressed again right there.
            for (var tries = 1; groups[i].Count == 1 && times[i] < busyUntil[tile]; tries++)
            {
                if (tries >= VisualizerSettings.LaneKeyCount) throw SimultaneousNotePlanner.HoldOverlap(settings, groups[i].SourceIndex, times[i]);
                tile = cursor.Next(cursor.CurrentMode);
            }
            var holdEnd = holdEndsMs?[groups[i].SourceIndex];
            notes.Add(new VisualizerNote(times[i], tile, cursor.CurrentMode, false, groups[i].Count == 1 ? holdEnd : null));
            if (groups[i].Count == 1 && holdEnd is double end) busyUntil[tile] = end;
        }
        return SimultaneousNotePlanner.Place(groups, notes, settings, holdEndsMs);
    }
}
