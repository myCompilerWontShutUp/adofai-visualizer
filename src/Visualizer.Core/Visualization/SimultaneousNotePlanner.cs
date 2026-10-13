using System.Globalization;
using Visualizer.Core.Localization;
using Visualizer.Core.Settings;

namespace Visualizer.Core.Visualization;

public sealed class NotePlacementException(string message) : InvalidOperationException(message);

/// <summary>Display-only grouping and advance lane planning; never modifies source hit times.</summary>
internal static class SimultaneousNotePlanner
{
    /// <summary>
    /// One cycle step. <paramref name="Count"/> hits share <paramref name="TimeMs"/>; members are shown as
    /// two-note chords in order (first+second, third+fourth), and an odd last member as a separate single.
    /// </summary>
    internal sealed record GroupedHit(double TimeMs, int Count, int SourceIndex);

    internal static IReadOnlyList<GroupedHit> Group(double[] times, VisualizerSettings settings)
    {
        var result = new List<GroupedHit>();
        for (var start = 0; start < times.Length;)
        {
            var end = start + 1;
            // Anchor to the first hit, not the preceding hit: no transitive drift.
            while (end < times.Length && times[end] - times[start] <= settings.SimultaneousWindowMs) end++;
            result.Add(new(times[start], end - start, start));
            start = end;
        }
        return result;
    }

    /// <summary>
    /// The groups that start a trill: two single taps within the trill window, taken in twos from the left as chord members
    /// are (a third this close starts the next pair). Long notes are never trills. The cursor, the chord pass and the trill
    /// pass all read trills from here, so they agree on which notes pair up.
    /// </summary>
    internal static bool[] TrillStarts(IReadOnlyList<GroupedHit> groups, VisualizerSettings settings, IReadOnlyList<double?>? holdEnds)
    {
        var starts = new bool[groups.Count];
        for (var i = 0; settings.TrillWindowMs > 0 && i + 1 < groups.Count; i++)
        {
            var gap = groups[i + 1].TimeMs - groups[i].TimeMs;
            if (groups[i].Count != 1 || groups[i + 1].Count != 1 || gap <= 0 || gap > settings.TrillWindowMs
                || holdEnds?[groups[i].SourceIndex] is not null || holdEnds?[groups[i + 1].SourceIndex] is not null) continue;
            starts[i] = true;
            i++;
        }
        return starts;
    }

    /// <summary>
    /// Two screen lanes (0-based) a trill may share: side by side, and on four lanes not the pair across the center while
    /// <paramref name="noCenter"/> refuses it.
    /// </summary>
    internal static bool IsTrillPair(int laneCount, int a, int b, bool noCenter) =>
        a >= 0 && b >= 0 && a < laneCount && b < laneCount && Math.Abs(a - b) == 1
        && !(laneCount == 4 && Math.Min(a, b) == laneCount / 2 - 1 && noCenter);

    /// <summary>Per logical tile (1..8), the end of the long note occupying it; negative infinity when free.</summary>
    /// <summary>
    /// Until when a long note keeps its lane: through its end and the first note that comes at or after it (user rule: no
    /// note on a lane while its long note runs, and the note after a long note never takes its lane, two long notes in a row
    /// included). <paramref name="times"/> are the note times in order. Lanes are compared with this inclusively.
    /// </summary>
    internal static double HeldUntil(IReadOnlyList<double> times, double holdEnd)
    {
        int low = 0, high = times.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (times[middle] < holdEnd) low = middle + 1;
            else high = middle;
        }
        return low < times.Count ? times[low] : holdEnd;
    }

    internal static double[] NewBusyLanes()
    {
        var busy = new double[VisualizerSettings.LaneKeyCount + 1];
        Array.Fill(busy, double.NegativeInfinity);
        return busy;
    }

    /// <summary>
    /// Trills (user request): two single notes within the trill window are played with two fingers, so they sit on the two
    /// lanes of one pair instead of one lane. It runs after the chords, which are grouped by their own window, so a pair
    /// this close that is not already a chord is what is left over. The pair rules are the chord ones with the trill's own
    /// settings: a pair the preference names comes first, the pair across the screen center is the last resort and is
    /// refused on four lanes while the setting says so. A trill rolls in the pattern's own direction (user rule: on a 4-3-2-1
    /// pattern it reads 2-1, never 1-2); the cursor and the chord pass already line most of them up, and what is left is
    /// turned here, keeping the first note where it is when it can and the notes around the trill off its lanes. Nothing
    /// moves onto a lane a long note holds, and a trill that has nowhere to go is left as it is (a trill is how it reads,
    /// not whether it can be placed).
    /// </summary>
    private static void SplitTrills(List<VisualizerNote> result, VisualizerSettings settings)
    {
        if (settings.TrillWindowMs <= 0) return;
        var visible = settings.GetVisibleTiles().ToList();
        // A lane is free when no long note holds it at that moment, the note after its end included (HeldUntil).
        var noteTimes = result.Select(note => note.HitTimeMs).ToArray();
        var holds = result.Where(note => note.HoldEndMs is not null)
            .Select(note => (note.Tile, Start: note.HitTimeMs, Until: HeldUntil(noteTimes, note.HoldEndMs!.Value))).ToArray();
        bool Free(int tile, double time) => !holds.Any(hold => hold.Tile == tile && hold.Start < time && time <= hold.Until);
        var preferred = settings.GetPreferredTrillLanes();
        var half = visible.Count / 2;
        // Pair starts a trill may take, best first: the preference, then any other adjacent pair, then the one across the
        // center. On four lanes the center is the last resort while the setting holds rather than refused (user rule): the
        // cursor puts a trill there only where every other pair breaks the run, and such a trill is kept as it is.
        var pairs = Enumerable.Range(0, Math.Max(0, visible.Count - 1))
            .OrderBy(low => low == half - 1 && visible.Count == 4 && settings.NoTrillCenterPair ? 3
                : preferred.Contains(low) ? 0 : low == half - 1 ? 2 : 1)
            .ToArray();
        bool Single(int index) => index >= 0 && index < result.Count && !result[index].StartsChord
            && (index == 0 || !result[index - 1].StartsChord);
        bool Trill(int index) => Single(index) && Single(index + 1)
            && result[index + 1].HitTimeMs - result[index].HitTimeMs is var gap && gap > 0 && gap <= settings.TrillWindowMs;
        // The trill just turned: two trills in a row take two pairs (user rule: not 1-2 1-2).
        var lastEnd = -2;
        var lastLow = -1;
        for (var i = 0; i + 1 < result.Count; i++)
        {
            if (!Trill(i)) continue;
            // Taken in twos, as a chord group is: a third note this close starts the next pair.
            var lane = visible.IndexOf(result[i].Tile);
            var other = visible.IndexOf(result[i + 1].Tile);
            var step = Direction(settings, Math.Max(result[i].Mode, result[i + 1].Mode));
            var follows = lastEnd == i - 1;
            bool Repeats((int, int) option) => follows && Math.Min(option.Item1, option.Item2) == lastLow;
            // A roll that starts at the far edge has nowhere to go that way and rolls back (user rule), so that one is kept too.
            var rollsBack = other - lane == -step && (lane + step < 0 || lane + step >= visible.Count);
            if (lane >= 0 && (other - lane == step || rollsBack) && pairs.Contains(Math.Min(lane, other)) && !Repeats((lane, other))
                && !LanesAround(result, i - 1, backward: true).Contains(result[i].Tile))
            {
                (lastEnd, lastLow) = (i + 1, Math.Min(lane, other));
                i++;
                continue;
            }
            if (lane >= 0 && result[i].HoldEndMs is null && result[i + 1].HoldEndMs is null)
            {
                var before = LanesAround(result, i - 1, backward: true);
                // A trill right after is turned the same way in its turn, and two pairs that run one way never share a key.
                var ahead = Trill(i + 2) ? [] : LanesAround(result, i + 2, backward: false);
                // The second one step on in the pattern's direction; else the first one step back so the roll still runs
                // that way; else the pair one step on (2K sits on the center pair, which four lanes refuse); else the second
                // on the other side, as before. Keeping the first note comes first, then the preferred pair.
                // After a trill on the same pair, the pair one further on or one back comes in as well.
                var options = new[] { (lane, lane + step), (lane - step, lane), (lane + step, lane + 2 * step),
                        (lane + 2 * step, lane + 3 * step), (lane - 2 * step, lane - step), (lane, lane - step) }
                    .Where(option => option.Item1 >= 0 && option.Item2 >= 0 && option.Item1 < visible.Count && option.Item2 < visible.Count
                        && pairs.Contains(Math.Min(option.Item1, option.Item2))
                        && Free(visible[option.Item1], result[i].HitTimeMs) && Free(visible[option.Item2], result[i + 1].HitTimeMs))
                    .OrderBy(option => option.Item1 == lane ? 0 : 1)
                    .ThenBy(option => Array.IndexOf(pairs, Math.Min(option.Item1, option.Item2)))
                    .ToArray();
                // Lanes around the trill are kept free; past that the first note stays and nothing moves onto the next note's lane
                // (a trill next is turned in its own turn, and keeps off this one's lanes then).
                var running = options.Where(option => option.Item2 - option.Item1 == step
                    && !before.Contains(visible[option.Item1]) && !ahead.Contains(visible[option.Item2])).ToArray();
                var chosen = running.Where(option => !Repeats(option)).Concat(running)
                    .Concat(options.Where(option => option.Item1 == lane && !ahead.Contains(visible[option.Item2])))
                    .Select(option => ((int, int)?)option).FirstOrDefault();
                if (chosen is { } pick)
                {
                    result[i] = result[i] with { Tile = visible[pick.Item1] };
                    result[i + 1] = result[i + 1] with { Tile = visible[pick.Item2] };
                }
            }
            (lastEnd, lastLow) = (i + 1, Math.Min(visible.IndexOf(result[i].Tile), visible.IndexOf(result[i + 1].Tile)));
            i++;
        }
    }

    /// <summary>
    /// A segment without chords is the pattern as the cursor walked it, and the cursor already keeps a switch off the tile it
    /// played last. Only after a segment the chords re-placed can its first note land on the lane just played (3-4 3, or 2 2
    /// on a 4K to 2K switch), or, right after a chord, away from it (1-2 4, where the single after a chord sits beside it by
    /// user rule). Then the whole segment walks the same pattern a step further on: two steps on four lanes and more, so its
    /// trills keep their pairs, one on two. Null when nothing has to move, when the pattern has a single tile (1K has nowhere
    /// else to go), or when a long note before the segment still holds a lane the shift would use.
    /// </summary>
    private static Func<int, int>? Shift(IReadOnlyList<VisualizerNote> baseline, int start, int end, int[] previous,
        VisualizerSettings settings, double[] busyUntil, double gapMs, double[] lastPlayed)
    {
        if (previous.Length == 0) return null;
        var visible = settings.GetVisibleTiles().ToList();
        bool Beside(int tile) => visible.IndexOf(tile) is var at && at >= 0
            && previous.Any(lane => visible.IndexOf(lane) is var other && other >= 0 && Math.Abs(other - at) == 1);
        var first = baseline[start].Tile;
        var pattern = settings.GetPattern(baseline[start].Mode).ToArray();
        var quick = NoteGenerator.QuickReturnMs(settings, baseline[start].Mode);
        // How many of the segment's first cycle would bring a lane back too soon, counting the lanes the re-placed notes
        // before it played (the cursor never saw where those went).
        int Returns(Func<int, int> map)
        {
            var played = (double[])lastPlayed.Clone();
            var count = 0;
            for (var i = start; i < Math.Min(end, start + pattern.Length); i++)
            {
                var tile = map(baseline[i].Tile);
                if (baseline[i].HitTimeMs - played[tile] < quick) count++;
                played[tile] = baseline[i].HitTimeMs;
            }
            return count;
        }
        // Only while the chord is close: past the 2K boundary the hand has come off it anyway.
        var chordClose = previous.Length >= 2 && gapMs <= settings.BoundaryMs(2);
        var steps = pattern.Length >= 4 ? new[] { 0, 2, 1, 3 } : pattern.Length >= 2 ? [0, 1] : [];
        // Each step that keeps off the lanes just played and the long notes, best first: beside a close chord, then the
        // fewest lanes back too soon, then no shift at all, then the order above.
        var options = steps.Select(step => (Step: step, Map: (Func<int, int>)(tile => Array.IndexOf(pattern, tile) is var at and >= 0 ? pattern[(at + step) % pattern.Length] : tile)))
            .Where(option => !previous.Contains(option.Map(first))
                && !Enumerable.Range(start, end - start).Any(i => baseline[i].HitTimeMs <= busyUntil[option.Map(baseline[i].Tile)]))
            .OrderBy(option => chordClose && !Beside(option.Map(first)) ? 1 : 0)
            .ThenBy(option => Returns(option.Map))
            .ThenBy(option => option.Step == 0 ? 0 : 1)
            .ToArray();
        if (options.Length == 0) return null;
        return options[0].Step == 0 ? null : options[0].Map;
    }

    /// <summary>The way a pattern runs over the lanes: -1 from right to left (the default 4-3-2-1), +1 the other way.</summary>
    private static int Direction(VisualizerSettings settings, int mode)
    {
        var pattern = settings.GetPattern(Math.Max(mode, 2) <= settings.MaximumTileCount ? Math.Max(mode, 2) : settings.MaximumTileCount);
        return pattern.Count >= 2 && pattern[1] > pattern[0] ? 1 : -1;
    }

    /// <summary>The lanes of the event at <paramref name="index"/>: one for a single, both for a chord.</summary>
    private static int[] LanesAround(List<VisualizerNote> notes, int index, bool backward)
    {
        if (index < 0 || index >= notes.Count) return [];
        if (backward && index > 0 && notes[index - 1].StartsChord) return [notes[index - 1].Tile, notes[index].Tile];
        if (!backward && notes[index].StartsChord && index + 1 < notes.Count) return [notes[index].Tile, notes[index + 1].Tile];
        return [notes[index].Tile];
    }

    internal static IReadOnlyList<VisualizerNote> Place(IReadOnlyList<GroupedHit> groups,
        IReadOnlyList<VisualizerNote> baseline, VisualizerSettings settings, IReadOnlyList<double?>? holdEnds = null)
    {
        var result = new List<VisualizerNote>();
        var visible = settings.GetVisibleTiles().ToArray();
        var extras = settings.GetExtraTiles().ToArray();
        var busyUntil = NewBusyLanes();
        // When each lane was last played, so a single in a chord section keeps off a lane that would come back too soon.
        var lastPlayed = NewBusyLanes();
        // The part each group plays in a trill: 1 opens one, 2 closes it.
        var trillStarts = TrillStarts(groups, settings, holdEnds);
        var roles = Enumerable.Range(0, groups.Count)
            .Select(i => trillStarts[i] ? 1 : i > 0 && trillStarts[i - 1] ? 2 : 0).ToArray();
        // Every note is checked against the long notes already placed, so no note ever lands on a lane still held, nor the
        // note after a long note on its lane (HeldUntil).
        var groupTimes = groups.Select(group => group.TimeMs).ToArray();
        void Add(VisualizerNote note, int sourceIndex)
        {
            if (note.HitTimeMs <= busyUntil[note.Tile]) throw HoldOverlap(settings, sourceIndex, note.HitTimeMs);
            result.Add(note);
            lastPlayed[note.Tile] = Math.Max(lastPlayed[note.Tile], note.HitTimeMs);
        }
        void Track(int from)
        {
            for (var i = from; i < result.Count; i++)
                if (result[i].HoldEndMs is double holdEnd)
                    busyUntil[result[i].Tile] = Math.Max(busyUntil[result[i].Tile], HeldUntil(groupTimes, holdEnd));
        }
        for (var start = 0; start < groups.Count;)
        {
            var mode = baseline[start].Mode;
            var end = start + 1;
            while (end < groups.Count && baseline[end].Mode == mode) end++;
            if (!Enumerable.Range(start, end - start).Any(i => groups[i].Count > 1))
            {
                var shift = Shift(baseline, start, end, LanesAround(result, result.Count - 1, backward: true), settings, busyUntil,
                    result.Count > 0 ? groups[start].TimeMs - result[^1].HitTimeMs : double.PositiveInfinity, lastPlayed);
                for (var i = start; i < end; i++)
                {
                    Add(shift is null ? baseline[i] : baseline[i] with { Tile = shift(baseline[i].Tile) }, groups[i].SourceIndex);
                    Track(result.Count - 1);
                }
                start = end;
                continue;
            }

            var pattern = settings.GetPattern(mode).ToArray();
            var rotation = Array.IndexOf(pattern, baseline[start].Tile);
            var preferred = pattern.Skip(rotation).Concat(pattern.Take(rotation)).ToArray();
            // Chord section lanes (advanced, default off; user rule, from Raimukun - C†hulhu): the singles that follow a chord keep
            // its lanes free while it is still close, so no lane carries a chord and a single in turn (1-2 3 2 3 becomes 1-2 3 4 3).
            // Chords are placed exactly as with the switch off, so alternating pairs (1-2 3-4 1-2 3-4) stay as they are, and notes
            // before the first chord are untouched.
            int[]? chordLanes = null;
            var chordGroup = int.MinValue;
            var laneHistory = new List<int>();
            // 1/2K count groups, and a group of three or more hits stands alone. 4/8K have a fixed
            // physical lane budget; a group consumes one lane per hit and is never split across a block.
            for (var blockStart = start; blockStart < end;)
            {
                // The rule fades out once the chord is more than two cycles behind.
                if (blockStart - chordGroup > 2 * mode) { chordLanes = null; laneHistory.Clear(); }
                var blockEnd = blockStart;
                var count = 0;
                while (blockEnd < end)
                {
                    var size = groups[blockEnd].Count;
                    var fits = blockEnd == blockStart || (mode <= 2
                        ? blockEnd - blockStart < mode && size <= 2 && groups[blockStart].Count <= 2
                        : count + size <= mode);
                    if (!fits) break;
                    count += size;
                    blockEnd++;
                }
                var block = groups.Skip(blockStart).Take(blockEnd - blockStart).ToArray();
                var candidates = mode >= 4 ? preferred : preferred.Concat(visible).Distinct().ToArray();
                // Chord pairs are tried from the most centered (tiles 3-4 and 5-6 first) outward. Spare lanes are used only when
                // no pairing places the block on the pattern lanes, with as few spare notes as possible; with spare notes, placements
                // that send only leftover singles to spare lanes (every chord kept) come before breaking a chord.
                // Lane styles, most wanted first: keep the recent chord's lanes for singles and let them repeat a lane inside the
                // block when few lanes are left, before giving up the rule or letting a lane follow itself. Without a recent chord
                // (and always with the switch off) the first try keeps every note off the lanes played right before it, chords
                // included (user rule: the same key twice in a row, 3-4 3-4 or 3 3, is hard to play), and only then the original.
                (bool Avoid, bool Repeat, bool NoFollow, bool Fresh)[] styles = chordLanes is null
                    ? [(false, false, true, true), (false, false, false, false)]
                    : [(true, false, true, false), (true, true, true, false), (true, true, false, false), (false, false, true, false), (false, false, false, false)];
                var lanes = chordLanes;
                var used = laneHistory.ToArray();
                var flow = new Flow(Enumerable.Range(blockStart, blockEnd - blockStart).Select(i => baseline[i].Tile).ToArray(),
                    Enumerable.Range(blockStart - 2, blockEnd - blockStart + 2).Select(i => i >= 0 ? roles[i] : 0).ToArray(),
                    Direction(settings, mode), settings.NoTrillCenterPair,
                    [result.Count >= 2 ? result[^2].Tile : 0, result.Count >= 1 ? result[^1].Tile : 0],
                    result.Count >= 1 ? result[^1].HitTimeMs : double.NegativeInfinity, settings.TrillWindowMs * NoteGenerator.RollLeadWindows,
                    lastPlayed, NoteGenerator.QuickReturnMs(settings, mode));
                var previous = LanesAround(result, result.Count - 1, backward: true);
                (int Tile, bool StartsChord)[]? Find(int budget) => styles
                    .SelectMany(style => (budget == 0 ? [true] : new[] { true, false })
                        .SelectMany(keepChords => Enumerable.Range(1, 3).Select(level => (style, keepChords, level))))
                    .Select(option => Assign(block, candidates, visible, mode, extras, budget, option.level, option.keepChords, busyUntil,
                        option.style.Avoid ? lanes : null, option.style.Avoid ? used : null, option.style.Repeat, option.style.NoFollow, previous,
                        settings.GetPreferredChordLanes(), settings.UseChordCenterPair, option.style.Fresh ? flow : null))
                    .FirstOrDefault(found => found is not null);
                var assignment = Find(0) ?? Enumerable.Range(1, extras.Length).Select(Find).FirstOrDefault(found => found is not null);
                if (assignment is null) throw Failure(settings, block[0].SourceIndex, block[0].TimeMs, mode, count);
                // Experimental: a single right before a chord keeps off the chord's lanes, so "3 3-4" reads "2 3-4".
                // Chords are placed after the singles, so the single is the one that moves; the chord keeps its pair.
                if (settings.UseChordAvoidBefore && result.Count > 0
                    && Array.FindIndex(assignment, placed => placed.StartsChord) is var chordAt and >= 0)
                {
                    var last = result.Count - 1;
                    var pair = new[] { assignment[chordAt].Tile, assignment[chordAt + 1].Tile };
                    // Only a single moves: a chord member would break its pair, and its partner sits beside it.
                    var isChordMember = result[last].StartsChord || (last > 0 && result[last - 1].StartsChord);
                    if (!isChordMember && pair.Contains(result[last].Tile))
                    {
                        var before = last > 0 ? result[last - 1].Tile : 0;
                        var free = visible.Where(lane => !pair.Contains(lane) && lane != before
                            && result[last].HitTimeMs > busyUntil[lane])
                            // Nearest to where it already was, so the hand moves as little as the rule allows.
                            .OrderBy(lane => Math.Abs(lane - result[last].Tile)).ToArray();
                        if (free.Length > 0) result[last] = result[last] with { Tile = free[0] };
                    }
                }
                var offset = 0;
                var added = result.Count;
                foreach (var group in block)
                    for (var n = 0; n < group.Count; n++, offset++)
                        Add(new(group.TimeMs, assignment[offset].Tile, mode, assignment[offset].StartsChord, holdEnds?[group.SourceIndex + n]),
                            group.SourceIndex + n);
                Track(added);
                if (settings.UseChordSectionLanes)
                {
                    var chord = Array.FindLastIndex(assignment, placed => placed.StartsChord);
                    if (chord >= 0)
                    {
                        chordLanes = [assignment[chord].Tile, assignment[chord + 1].Tile];
                        chordGroup = blockEnd - 1;
                        laneHistory.Clear();
                        laneHistory.AddRange(chordLanes);
                    }
                    if (chordLanes is not null)
                        foreach (var placed in assignment)
                            if (!laneHistory.Contains(placed.Tile)) laneHistory.Add(placed.Tile);
                }
                blockStart = blockEnd;
            }
            start = end;
        }
        if (settings.UseEnhancedChordRules) LockChords(result, settings);
        SplitTrills(result, settings);
        return result.AsReadOnly();
    }

    /// <summary>
    /// Enhanced chord rules (experimental, user rule from ネコーン - 俳徹句, where chords and singles took turns and the chord
    /// kept jumping between 1-2 and 3-4). Where chords come back with one to three singles between them, close together, the
    /// singles never move the chord: every chord keeps the first one's lanes and the singles take the lanes beside it, by how
    /// many there are: one 1-2 3, two 1-2 3 4, three 1-2 3 4 3, and three faster than the 4K boundary alternate with 4 3 4.
    /// Four singles or more keep their lanes, except that four going back and forth on two lanes right before a chord
    /// (1 2 1 2 3-4) walk the pattern instead when they can. Nothing moves onto a lane a long note holds, a chord with a long
    /// note is never part of it, and a stretch that cannot be laid out so is left as it was.
    /// </summary>
    private static void LockChords(List<VisualizerNote> result, VisualizerSettings settings)
    {
        var visible = settings.GetVisibleTiles().ToList();
        var noteTimes = result.Select(note => note.HitTimeMs).ToArray();
        var holds = result.Where(note => note.HoldEndMs is not null)
            .Select(note => (note.Tile, Start: note.HitTimeMs, Until: HeldUntil(noteTimes, note.HoldEndMs!.Value))).ToArray();
        bool Free(int tile, double time) => !holds.Any(hold => hold.Tile == tile && hold.Start < time && time <= hold.Until);
        // The events in order: where each starts in the result and how many notes it has (a chord is two).
        var events = new List<(int At, int Size)>();
        for (var i = 0; i < result.Count;)
        {
            var size = result[i].StartsChord && i + 1 < result.Count ? 2 : 1;
            events.Add((i, size));
            i += size;
        }
        double TimeOf(int e) => result[events[e].At].HitTimeMs;
        int[] LanesOf(int e) => Enumerable.Range(events[e].At, events[e].Size).Select(n => visible.IndexOf(result[n].Tile)).ToArray();
        bool Plain(int e) => Enumerable.Range(events[e].At, events[e].Size).All(n => result[n].HoldEndMs is null)
            && LanesOf(e).All(lane => lane >= 0);
        // Close enough to be one stretch, and not a trill.
        bool Linked(int a, int b) => TimeOf(b) - TimeOf(a) is var gap && gap > settings.TrillWindowMs && gap <= settings.MagicCircleMaxIntervalMs;
        bool Chord(int e) => events[e].Size == 2 && Plain(e);
        bool Single(int e) => events[e].Size == 1 && Plain(e);
        var fast = settings.MaximumTileCount >= 4 ? settings.BoundaryMs(4) : 0;

        for (var e = 0; e < events.Count; e++)
        {
            if (!Chord(e)) continue;
            // The chords this one chains to, each through one to three singles.
            var chords = new List<int> { e };
            for (var at = e; ;)
            {
                var next = at + 1;
                while (next < events.Count && next - at <= 4 && Single(next) && Linked(next - 1, next)) next++;
                if (next - at - 1 is >= 1 and <= 3 && next < events.Count && Chord(next) && Linked(next - 1, next)) { chords.Add(next); at = next; }
                else break;
            }
            RollBeforeChord(e);
            if (chords.Count >= 2) Lay(chords);
            e = chords[^1];
        }

        void Lay(List<int> chords)
        {
            var pair = LanesOf(chords[0]).Order().ToArray();
            var (low, high) = (pair[0], pair[1]);
            // The singles' side: the one with two lanes beside the chord; with both, the side the singles mostly took.
            int[] right = [high + 1, high + 2], left = [low - 1, low - 2];
            bool Fits(int[] side) => side.All(lane => lane >= 0 && lane < visible.Count);
            var singles = chords.Zip(chords.Skip(1)).SelectMany(span => Enumerable.Range(span.First + 1, span.Second - span.First - 1)).ToArray();
            var rightVotes = singles.Count(s => LanesOf(s)[0] > high);
            var side = Fits(right) && Fits(left) ? (rightVotes * 2 >= singles.Length ? right : left)
                : Fits(right) ? right : Fits(left) ? left : null;
            if (side is null) return;
            var plan = new Dictionary<int, int>();
            foreach (var chord in chords)
                for (var n = 0; n < 2; n++) plan[events[chord].At + n] = n == 0 ? low : high;
            var threes = 0;
            foreach (var (first, second) in chords.Zip(chords.Skip(1)))
            {
                var count = second - first - 1;
                int[] lanes = count switch
                {
                    1 => [side[0]],
                    2 => [side[0], side[1]],
                    _ => TimeOf(first + 2) - TimeOf(first + 1) < fast && threes++ % 2 == 1 ? [side[1], side[0], side[1]] : [side[0], side[1], side[0]]
                };
                for (var n = 0; n < count; n++) plan[events[first + 1 + n].At] = lanes[n];
            }
            // What comes right after the stretch keeps off the chord's lanes too: a single moves beside it (not onto the lane
            // of the event after it), a chord to the pair on the singles' side. When neither can, the stretch stays as it was.
            var after = chords[^1] + 1;
            if (after < events.Count && Plain(after) && Linked(chords[^1], after) && LanesOf(after) is var next
                && (next.Contains(low) || next.Contains(high)))
            {
                int[] following = after + 1 < events.Count && Linked(after, after + 1) ? LanesOf(after + 1) : [];
                if (events[after].Size == 1)
                {
                    if (side.Where(lane => !following.Contains(lane)).Select(lane => (int?)lane).FirstOrDefault() is not { } moved) return;
                    plan[events[after].At] = moved;
                }
                else
                {
                    if (following.Contains(side[0]) || following.Contains(side[1])) return;
                    plan[events[after].At] = side[0];
                    plan[events[after].At + 1] = side[1];
                }
            }
            if (plan.Any(entry => !Free(visible[entry.Value], result[entry.Key].HitTimeMs))) return;
            foreach (var (index, planned) in plan) result[index] = result[index] with { Tile = visible[planned] };
        }

        // Four singles or more before a chord that go back and forth on two lanes (1 2 1 2 3-4) walk the pattern instead,
        // from where they start, as long as they still keep off the chord's lanes and off the event before them.
        void RollBeforeChord(int chord)
        {
            var first = chord;
            while (first - 1 >= 0 && Single(first - 1) && Linked(first - 1, first)) first--;
            var count = chord - first;
            if (count < 4) return;
            var lanes = Enumerable.Range(first, count).Select(s => LanesOf(s)[0]).ToArray();
            var tail = lanes[^4..];
            if (!(tail[0] == tail[2] && tail[1] == tail[3] && tail[0] != tail[1])) return;
            var step = Direction(settings, result[events[chord].At].Mode);
            var target = LanesOf(chord);
            var previous = first > 0 ? LanesOf(first - 1) : [];
            foreach (var start in Enumerable.Range(0, visible.Count).OrderBy(lane => Math.Abs(lane - lanes[0])))
            {
                var walk = Enumerable.Range(0, count).Select(n => ((start + n * step) % visible.Count + visible.Count) % visible.Count).ToArray();
                if (previous.Contains(walk[0]) || target.Contains(walk[^1])) continue;
                if (Enumerable.Range(0, count).Any(n => !Free(visible[walk[n]], TimeOf(first + n)))) continue;
                for (var n = 0; n < count; n++) result[events[first + n].At] = result[events[first + n].At] with { Tile = visible[walk[n]] };
                return;
            }
        }
    }

    /// <summary>
    /// Lanes for every member of a block. Members pair into chords in order. With a spare budget, up to that many members
    /// may come down on spare lanes as singles; a pair with a spare member is shown as two singles instead of a chord.
    /// (A chord that includes a spare lane would need the same lanes plus adjacency, so singles are always at least as placeable.)
    /// <paramref name="level"/> widens the lane pairs a chord may use: 1 = centered pairs only, 2 = any pair within one half of the
    /// screen, 3 = also a pair across the center (never on four lanes). With <paramref name="keepChords"/>, only a group's leftover
    /// single (its odd last member) may go to a spare lane, so every chord stays a chord.
    /// <paramref name="sectionLanes"/> are the recent chord's lanes, which singles keep free; <paramref name="repeatSingles"/> lets a
    /// single reuse a lane another single took in this block (too few lanes are left), and <paramref name="noFollow"/> keeps a single
    /// off the lane of the note right before it, <paramref name="previousLanes"/> being the lanes of the event right before the
    /// block. With <paramref name="flow"/> every note, chords included, keeps off all the lanes of the event before it, a single
    /// after a chord sits beside it and a single before a chord takes a lane beside a pair it is not part of, so singles and
    /// chords alternate as one hand would play them (2 3-4 2 3-4, 3 1-2 3 1-2). A trill comes before those rules (it opens on
    /// a lane whose neighbour in the pattern's direction may close it, and closes there), and past them a single keeps the
    /// tile the cursor gave it, so a run of singles stays as the pattern walked it.
    /// <paramref name="preferredLane"/> is the 0-based lane a chord tries first (-1 for none), and <paramref name="centerPair"/>
    /// lets a chord cross the screen center on four lanes.
    /// </summary>
    private static (int Tile, bool StartsChord)[]? Assign(GroupedHit[] groups, int[] candidates, int[] visible, int mode, int[] extras, int budget,
        int level, bool keepChords, double[] busyUntil, int[]? sectionLanes, int[]? sectionUsed, bool repeatSingles, bool noFollow,
        int[] previousLanes, IReadOnlyList<int> preferredLanes, bool centerPair, Flow? flow = null)
    {
        var fresh = flow is not null;
        var total = groups.Sum(g => g.Count);
        if (total > candidates.Length + budget) return null;
        var tiles = new List<int>();
        var chords = new List<bool>();
        var chordTiles = new List<int>();
        // The group each placed lane belongs to: notes of one group sound together, so they never share a lane.
        var owners = new List<int>();
        var spareUsed = 0;
        var choices = budget > 0 ? candidates.Concat(extras).ToArray() : candidates;
        bool Adjacent(int a, int b)
        {
            var ia = Array.IndexOf(visible, a);
            var ib = Array.IndexOf(visible, b);
            if (Math.Abs(ia - ib) != 1) return false;
            var low = Math.Min(ia, ib);
            var half = visible.Length / 2;
            // A pair across the screen center (lanes 2-3 of four, 3-4 of six, 4-5 of eight) is never used on four lanes
            // (user rule, found in 1K/2K sections of RUSH E), whatever the preference says.
            if (low == half - 1 && visible.Length == 4 && !centerPair) return false;
            // A pair the preference names is taken from the first level on; that is what choosing it means.
            if (preferredLanes.Contains(low)) return true;
            // Otherwise the pair across the center is the last resort.
            if (low == half - 1) return level >= 3;
            if (level >= 2) return true;
            // Preferred (user rule): pairs counted outward from the center in each half, so chords sit on the middle
            // tiles 3-4 and 5-6 first (then 1-2 and 7-8). On the default four lanes these are lanes 1-2 and 3-4, as before.
            return low < half ? (half - 2 - low) % 2 == 0 : (low - half) % 2 == 0;
        }
        // The lanes of the group right before this one: the event before the block for its first group.
        int[] Before(int groupIndex) => groupIndex == 0 ? previousLanes
            : tiles.Where((_, index) => owners[index] == groupIndex - 1).ToArray();
        bool Beside(int a, int b) => Array.IndexOf(visible, a) is var ia and >= 0 && Array.IndexOf(visible, b) is var ib and >= 0
            && Math.Abs(ia - ib) == 1;
        // The tile of a single group placed before this one; groups before the block are -1 and -2.
        int LaneOf(int group) => group >= 0
            ? tiles.Where((_, index) => owners[index] == group).DefaultIfEmpty(0).First()
            : flow!.Trailing[group + 2];
        // How well a tile sits after a trill: 0 beside it on its second note's side, 1 anywhere else. A roll that ran into
        // the edge has no lane there and the pattern goes on: going back to the other side left a lane out of the cycle
        // (4 3-2 1 3 2-1, user report), as the cursor already knows.
        int BesideTrill((int First, int Second) trill, int tile)
        {
            var first = Array.IndexOf(visible, trill.First);
            var second = Array.IndexOf(visible, trill.Second);
            var at = Array.IndexOf(visible, tile);
            if (first < 0 || second < 0 || at < 0) return 1;
            return at == second + (second - first) ? 0 : 1;
        }
        // A single right before a chord does best beside a pair it is not part of: the chord can then follow it without
        // repeating its lane and without jumping across the hand.
        bool BesidePair(int tile)
        {
            var at = Array.IndexOf(visible, tile);
            return at >= 0 && ((at + 2 < visible.Length && Adjacent(visible[at + 1], visible[at + 2]))
                || (at - 2 >= 0 && Adjacent(visible[at - 2], visible[at - 1])));
        }
        bool Search(int groupIndex, int member)
        {
            if (groupIndex == groups.Length)
            {
                // A 1/2K block with a chord (single + chord, or a wider split group) occupies contiguous pattern lanes. While a recent
                // chord's lanes are kept free the singles sit beside them instead of inside them, so those lanes count as well.
                if (mode > 2 || !chords.Contains(true)) return true;
                var positions = tiles.Concat(sectionUsed ?? []).Where(t => Array.IndexOf(extras, t) < 0)
                    .Select(t => Array.IndexOf(visible, t)).Distinct().ToArray();
                return positions.Max() - positions.Min() + 1 == positions.Length;
            }
            if (member == groups[groupIndex].Count) return Search(groupIndex + 1, 0);
            // Odd members pair with the member before them; an odd last member is a free single.
            var pairsWithPrevious = member % 2 == 1;
            // Drawn as a single: no partner before it and none after it inside its group.
            var single = !pairsWithPrevious && member + 1 == groups[groupIndex].Count;
            // Chord lane preference (default 1-2,3-4): a chord takes the chosen pairs first, then the nearest lanes.
            // With no preference, chords follow the pattern order. Singles always do.
            var ordered = single || preferredLanes.Count == 0 ? choices
                : choices.OrderBy(tile => Array.IndexOf(visible, tile) is var lane
                        && preferredLanes.Any(start => lane >= start && lane <= start + 1) ? 0 : 1)
                    .ThenBy(tile => Array.IndexOf(visible, tile) is var lane && lane >= 0 ? lane : int.MaxValue).ToArray();
            var before = Before(groupIndex);
            if (flow is not null && single)
            {
                var role = flow.RoleOf(groupIndex);
                // A roll in the pattern's direction over two neighbours. The pair across the center counts: the trill setting
                // makes it the last resort, not refused, and below it only loses to a roll that keeps the flow.
                bool Rolls(int from, int to) => Array.IndexOf(visible, from) is var a && a >= 0 && Array.IndexOf(visible, to) is var b
                    && b - a == flow.Step && IsTrillPair(visible.Length, a, b, false);
                // A trill that opened at the far edge closes the other way.
                bool RollsBack(int from, int to) => Array.IndexOf(visible, from) is var a && a >= 0 && Array.IndexOf(visible, to) is var b
                    && b - a == -flow.Step && (a + flow.Step < 0 || a + flow.Step >= visible.Length) && IsTrillPair(visible.Length, a, b, false);
                bool Centered(int from, int to) => !IsTrillPair(visible.Length, Array.IndexOf(visible, from), Array.IndexOf(visible, to), flow.NoTrillCenter);
                // How a trill opening on a tile reads: 0 rolls on from the single just before it, 1 on the center pair, 2 skips a
                // lane or comes back (4 2-1, user report: the center is used then rather than broken flow), 4 no roll at all.
                var lastTime = groupIndex > 0 ? groups[groupIndex - 1].TimeMs : flow.LastTimeMs;
                var closeSingle = before.Length == 1 && groups[groupIndex].TimeMs - lastTime <= flow.CloseMs;
                int Opens(int tile)
                {
                    var partner = Array.IndexOf(visible, tile) is var at && at >= 0 && at + flow.Step >= 0 && at + flow.Step < visible.Length
                        ? visible[at + flow.Step] : 0;
                    // At the far edge a roll goes back the other way (user rule: 3 2 1 | 2-3-4), after any roll that keeps the
                    // flow and before one that jumps.
                    if (partner == 0)
                        return at >= 0 && at - flow.Step >= 0 && at - flow.Step < visible.Length
                            && IsTrillPair(visible.Length, at, at - flow.Step, false) && !(closeSingle && before[0] == visible[at - flow.Step]) ? 2 : 5;
                    if (!Rolls(tile, partner)) return 5;
                    // Going round from the far edge (1 then 4-3) continues the run as well.
                    var from = Array.IndexOf(visible, before.Length == 1 ? before[0] : 0) + flow.Step;
                    var continues = Array.IndexOf(visible, tile) == (from < 0 || from >= visible.Length ? (flow.Step < 0 ? visible.Length - 1 : 0) : from);
                    var breaks = closeSingle && !continues;
                    return (breaks ? 3 : 0) + (Centered(tile, partner) ? 1 : 0);
                }
                // The trill that ended right before this group, as its first and second tile, when one did.
                (int First, int Second)? trill = flow.RoleOf(groupIndex - 1) == 2 && LaneOf(groupIndex - 2) is var a && a > 0
                    && LaneOf(groupIndex - 1) is var b && b > 0 ? (a, b) : null;
                bool SamePair(int tile) => trill is { } last && visible.Any(next => Rolls(tile, next)
                    && new[] { tile, next }.Order().SequenceEqual(new[] { last.First, last.Second }.Order()));
                // After a chord: beside it. After a trill: beside it too, on its second note's side first (user rule: 1-2 3,
                // 3-4 2). Before a chord: beside a pair it is not part of. The pattern order breaks ties. A lane another
                // single of the block took comes last: a run of singles still walks the whole pattern.
                var afterChord = before.Length >= 2;
                var beforeChord = groupIndex + 1 < groups.Length && groups[groupIndex + 1].Count >= 2;
                // A single that a trill follows closely heads its roll (user rule, in this order): 3 2-1 with the trill off
                // the center, then 4 3-2 with the center pair only where no tile heads such a roll, and 4 2-1 last.
                var leadsTrill = role == 0 && groupIndex + 1 < groups.Length && flow.RoleOf(groupIndex + 1) == 1
                    && groups[groupIndex + 1].TimeMs - groups[groupIndex].TimeMs <= flow.CloseMs;
                int Heads(int tile)
                {
                    var second = Array.IndexOf(visible, tile) is var at && at >= 0 ? at + flow.Step : -1;
                    var third = second + flow.Step;
                    // From the far edge the pattern goes round, and a roll off the center from the other edge carries on (1 4-3).
                    if (at >= 0 && (second < 0 || second >= visible.Length))
                    {
                        var round = flow.Step < 0 ? visible.Length - 1 : 0;
                        return IsTrillPair(visible.Length, round, round + flow.Step, flow.NoTrillCenter) ? 0 : 4;
                    }
                    if (second < 0 || third < 0 || second >= visible.Length || third >= visible.Length) return 4;
                    // A roll over the lane just played brings it straight back. Off the center, anywhere in the quick window is
                    // too soon (1 3 2-1): 3 cannot head it, and the center roll comes first then. The center roll is the
                    // fallback, so only a close lane just played puts it behind the rest (what is left jumps back as well).
                    // Measured to the trill, as the cursor measures it.
                    var over = new[] { at, second, third }.Any(lane => before.Contains(visible[lane]));
                    var span = groups[groupIndex + 1].TimeMs - lastTime;
                    return IsTrillPair(visible.Length, second, third, flow.NoTrillCenter)
                        ? over && span < flow.QuickMs ? 2 : 0
                        : over && span <= flow.CloseMs ? 3 : 1;
                }
                // A lane played within the quick window would come back too soon while another is free (user rule: 2 1 2 1
                // between chords, with 3 and 4 free): it comes after the lanes that have rested.
                double PlayedAt(int tile) => Math.Max(flow.LastPlayed[tile], tiles.Select((placed, index) => (placed, index))
                    .Where(entry => entry.placed == tile).Select(entry => groups[owners[entry.index]].TimeMs).DefaultIfEmpty(double.NegativeInfinity).Max());
                bool Rested(int tile) => groups[groupIndex].TimeMs - PlayedAt(tile) >= flow.QuickMs;
                ordered = ordered.OrderBy(tile => role == 1 ? Opens(tile)
                        : role == 2 && before.Length == 1 ? RollsBack(before[0], tile) ? 1 : !Rolls(before[0], tile) ? 2 : Centered(before[0], tile) ? 1 : 0 : 0)
                    // Two trills in a row take two pairs (user rule: not 1-2 1-2).
                    .ThenBy(tile => role == 1 && SamePair(tile) ? 1 : 0)
                    .ThenBy(tile => role == 0 && trill is { } last ? BesideTrill(last, tile) : 0)
                    .ThenBy(tile => afterChord && before.Any(lane => Beside(lane, tile)) ? 0 : 1)
                    .ThenBy(tile => leadsTrill ? Heads(tile) : 0)
                    .ThenBy(tile => beforeChord && BesidePair(tile) ? 0 : 1)
                    .ThenBy(tile => Rested(tile) ? 0 : 1)
                    .ThenBy(tile => tile == flow.Hints[groupIndex] ? 0 : 1)
                    .ThenBy(tile => tiles.Contains(tile) ? 1 : 0).ToArray();
            }
            foreach (var tile in ordered)
            {
                if (groups[groupIndex].TimeMs <= busyUntil[tile]) continue;
                // Two notes of one block never share a lane, except singles when the kept chord lanes leave too few of them.
                // Notes of one group sound together, so they never share a lane at all.
                // Fresh singles may come back to a single's lane too (2 3-4 2): the lane order above keeps that for when it helps.
                var reuse = (repeatSingles || fresh) && single && !chordTiles.Contains(tile)
                    && !tiles.Where((t, index) => t == tile && owners[index] == groupIndex).Any();
                if (tiles.Contains(tile) && !reuse) continue;
                if (single && sectionLanes is not null && Array.IndexOf(sectionLanes, tile) >= 0) continue;
                if (noFollow && (fresh ? before.Contains(tile)
                    : single && tile == (tiles.Count > 0 ? tiles[^1] : previousLanes.LastOrDefault()))) continue;
                var spare =Array.IndexOf(extras, tile) >= 0;
                if (spare && spareUsed == budget) continue;
                if (spare && keepChords && (pairsWithPrevious || member + 1 < groups[groupIndex].Count)) continue;
                var chord = pairsWithPrevious && !spare && Array.IndexOf(extras, tiles[^1]) < 0;
                if (chord && !Adjacent(tiles[^1], tile)) continue;
                if (chord) { chords[^1] = true; chordTiles.Add(tiles[^1]); chordTiles.Add(tile); }
                tiles.Add(tile);
                owners.Add(groupIndex);
                chords.Add(false);
                if (spare) spareUsed++;
                if (Search(groupIndex, member + 1)) return true;
                if (spare) spareUsed--;
                chords.RemoveAt(chords.Count - 1);
                owners.RemoveAt(owners.Count - 1);
                tiles.RemoveAt(tiles.Count - 1);
                if (chord) { chords[^1] = false; chordTiles.RemoveRange(chordTiles.Count - 2, 2); }
            }
            return false;
        }
        return Search(0, 0) ? tiles.Zip(chords).ToArray() : null;
    }

    /// <summary>
    /// What the chord pass knows of the notes around a block: the tile the cursor gave each group, each group's part in a
    /// trill (1 opens, 2 closes; the two groups before the block first), the pattern's direction over the lanes, whether a
    /// trill may cross the center, the last two tiles placed before the block, when the last of them sounded, and how close
    /// a single must come before a trill to be part of its roll; when each lane was last played, and how soon a lane coming
    /// back is too soon.
    /// </summary>
    private sealed record Flow(int[] Hints, int[] Roles, int Step, bool NoTrillCenter, int[] Trailing, double LastTimeMs, double CloseMs,
        double[] LastPlayed, double QuickMs)
    {
        /// <summary>A group's part in a trill; the two groups before the block are -2 and -1.</summary>
        public int RoleOf(int group) => Roles[group + 2];
    }

    internal static NotePlacementException HoldOverlap(VisualizerSettings settings, int index, double time) =>
        new(Languages.Format(settings.Language, "Placement.Failed", index + 1, time.ToString("F3", CultureInfo.InvariantCulture),
            Languages.Format(settings.Language, "Placement.HoldOverlap", settings.MaximumTileCount)));

    private static NotePlacementException Failure(VisualizerSettings settings, int index, double time, int mode, int count) =>
        new(Languages.Format(settings.Language, "Placement.Failed", index + 1, time.ToString("F3", CultureInfo.InvariantCulture),
            Languages.Format(settings.Language, "Placement.Chords", settings.MaximumTileCount, mode, count)));
}
