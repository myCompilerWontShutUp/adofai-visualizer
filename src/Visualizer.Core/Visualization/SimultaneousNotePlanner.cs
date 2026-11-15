using System.Globalization;
using Visualizer.Core.Localization;
using Visualizer.Core.Settings;

namespace Visualizer.Core.Visualization;

public sealed class NotePlacementException(string message) : InvalidOperationException(message);

/// <summary>Display-only grouping and advance lane planning; never modifies source hit times.</summary>
internal static class SimultaneousNotePlanner
{
    /// <summary>
    /// One cycle step. <paramref name="Count"/> hits share <paramref name="TimeMs"/>, every one of them within the chord window
    /// of the first, so start and end are both in it (user rule). Two or more are one chord on lanes side by side as long as
    /// the screen has the lanes; a group wider than the screen takes the spare lanes as two-note chords in order.
    /// </summary>
    internal sealed record GroupedHit(double TimeMs, int Count, int SourceIndex);

    /// <summary>How close two hits are to be one instant: tiles the game puts at the same time, give or take rounding.</summary>
    internal const double SameInstantMs = 0.001;

    /// <summary>
    /// Whether a roll of <paramref name="length"/> notes is the pattern's own walk (user report: a fast run reads 4 3 2 1 4 3 2 1):
    /// it is when the pattern has three lanes or more and no more than the roll has notes. A pattern of one or two lanes
    /// walked over a long roll is one lane or 2 3 2 3 again and again, so such a roll goes side by side instead.
    /// </summary>
    internal static bool WalksPattern(int length, int patternLanes) => patternLanes >= 3 && length >= patternLanes;

    /// <summary>How many lanes a roll may run over: the screen's, or one hand's four when eight lanes are played by hand.</summary>
    internal static int RollLanes(VisualizerSettings settings) => settings.PlaysByHand ? 4 : Math.Max(2, settings.GetVisibleTiles().Count);

    internal static IReadOnlyList<GroupedHit> Group(double[] times, VisualizerSettings settings, IReadOnlyList<double?>? holdEnds = null)
    {
        var result = new List<GroupedHit>();
        // A group never holds more notes than it has lanes for, the screen's and any spare ones: its notes never share a lane,
        // so a wider one could never be placed. What is left starts the next group at its own time, as a roll too long for
        // the lanes does (user rule, from Camellia - Hello (BPM) 2026: 28 notes in 25ms).
        var cap = settings.GetVisibleTiles().Count + settings.GetExtraTiles().Count;
        // Notes linked one to the next by the trill window are a trill unless the whole run fits the chord window (user rule:
        // a chord needs a and c in the chord window, a trill only a-b and b-c in the trill window, whatever the gaps are).
        // So in such a run only notes of one instant sound together. A run with a long note in it is grouped as chords
        // (long notes are never trills).
        var loose = new bool[times.Length];
        for (var first = 0; settings.TrillWindowMs > 0 && first < times.Length;)
        {
            var last = first;
            while (last + 1 < times.Length && times[last + 1] - times[last] <= settings.TrillWindowMs) last++;
            var trill = times[last] - times[first] > settings.SimultaneousWindowMs
                && Enumerable.Range(first, last - first + 1).All(i => holdEnds?[i] is null);
            for (var i = first; i <= last; i++) loose[i] = trill;
            first = last + 1;
        }
        for (var start = 0; start < times.Length;)
        {
            var end = start + 1;
            var window = loose[start] ? SameInstantMs : settings.SimultaneousWindowMs;
            // Anchor to the first hit, not the preceding hit: no transitive drift.
            while (end < times.Length && end - start < cap && times[end] - times[start] <= window) end++;
            result.Add(new(times[start], end - start, start));
            start = end;
        }
        return result;
    }

    /// <summary>
    /// Trills (user rule): single taps that each come within the trill window of the one before are one roll, however many
    /// there are: a b c with a-b and b-c that close is a three-note trill, on four lanes 1-2-3 or 2-3-4. A chain longer than
    /// the screen's lanes is cut into rolls by <see cref="ChainPieces"/>. Chords come first: a group of two or more is never
    /// part of a trill. Long notes are never trills. Each group's place in its roll comes back: 0 outside any, 1 the first
    /// note, 2 the second and so on; <paramref name="lengths"/> holds each roll's length at its first note. The cursor, the
    /// chord pass and the trill pass all read trills this way, so they agree on which notes roll.
    /// </summary>
    internal static int[] TrillRuns(IReadOnlyList<GroupedHit> groups, VisualizerSettings settings, IReadOnlyList<double?>? holdEnds,
        out int[] lengths)
    {
        var positions = new int[groups.Count];
        lengths = new int[groups.Count];
        if (settings.TrillWindowMs <= 0) return positions;
        var cap = RollLanes(settings);
        bool Tap(int i) => groups[i].Count == 1 && holdEnds?[groups[i].SourceIndex] is null;
        bool Close(int i) => groups[i + 1].TimeMs - groups[i].TimeMs is var gap && gap > 0 && gap <= settings.TrillWindowMs;
        for (var i = 0; i + 1 < groups.Count;)
        {
            if (!Tap(i) || !Tap(i + 1) || !Close(i)) { i++; continue; }
            var end = i + 1;
            while (end + 1 < groups.Count && Tap(end + 1) && Close(end)) end++;
            var first = i;
            var at = i;
            foreach (var piece in ChainPieces(end - i + 1, k => groups[first + k + 1].TimeMs - groups[first + k].TimeMs, cap))
            {
                if (piece >= 2)
                {
                    lengths[at] = piece;
                    for (var k = 0; k < piece; k++) positions[at + k] = k + 1;
                }
                at += piece;
            }
            i = end + 1;
        }
        return positions;
    }

    /// <summary>
    /// The rolls a chain of <paramref name="count"/> notes, each within the trill window of the one before, is played as: their
    /// lengths in order. <paramref name="gapAfter"/> is the gap from the chain's note k to the next. A chain the lanes hold is
    /// one roll whatever its gaps (user rule: a b c is a three-note trill). A longer one is cut where its rhythm groups it, at
    /// each gap clearly longer than the gaps on both sides of it (user rule: three-note trills in a row read 4-3-2 4-3-2; cut every four
    /// notes they read 4-3-2 1-4-3, which is no trill at all), never leaving one note on either side. A piece still longer
    /// than <paramref name="cap"/> is cut every <paramref name="cap"/> notes, so an even stream walks the pattern (4 3 2 1 4 3 2 1)
    /// as before; what is left over (one note) is no trill.
    /// </summary>
    /// <summary>
    /// How much longer than the gap before it a gap has to be to group a chain (see <see cref="ChainPieces"/>). Hit times
    /// come from the level exactly, so an even stream has equal gaps; a quarter longer is already a rhythm (a triplet
    /// followed by a sixteenth is half again as long).
    /// </summary>
    internal const double RollBreakFactor = 1.25;

    internal static List<int> ChainPieces(int count, Func<int, double> gapAfter, int cap)
    {
        var pieces = new List<int>();
        if (count <= cap)
        {
            pieces.Add(count);
            return pieces;
        }
        var start = 0;
        // A pause between groups is longer than the gaps on both sides of it; one longer than a flam before it alone is not.
        for (var k = 1; k + 2 < count; k++)
            if (gapAfter(k) > Math.Max(gapAfter(k - 1), gapAfter(k + 1)) * RollBreakFactor && k + 1 - start >= 2 && count - (k + 1) >= 2)
            {
                pieces.Add(k + 1 - start);
                start = k + 1;
            }
        pieces.Add(count - start);
        return pieces.SelectMany(length => Enumerable.Range(0, (length + cap - 1) / cap).Select(part => Math.Min(cap, length - part * cap))).ToList();
    }

    /// <summary>
    /// Two screen lanes (0-based) a trill may share: side by side, and from four lanes up not the pair across the center
    /// while <paramref name="noCenter"/> keeps it off.
    /// </summary>
    internal static bool IsTrillPair(int laneCount, int a, int b, bool noCenter) =>
        a >= 0 && b >= 0 && a < laneCount && b < laneCount && Math.Abs(a - b) == 1
        && !(laneCount >= 4 && laneCount % 2 == 0 && Math.Min(a, b) == laneCount / 2 - 1 && noCenter);

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
    /// settings: a pair the preference names comes first, the pair across the screen center is used like any other, kept
    /// for the last resort or refused, as the setting says. A trill rolls in the pattern's own direction (user rule: on a 4-3-2-1
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
        // center. The center is the last resort while the setting says so rather than refused (user rule): the cursor puts a
        // trill there only where every other pair breaks the run, and such a trill is kept as it is. Set to never, the center
        // is no pair at all, as for a two-note chord on four lanes.
        bool Center(int low) => low == half - 1 && visible.Count >= 4 && visible.Count % 2 == 0;
        var pairs = Enumerable.Range(0, Math.Max(0, visible.Count - 1))
            .Where(low => !(Center(low) && settings.TrillCenterNever))
            .OrderBy(low => Center(low) && settings.TrillCenterLast ? 3 : preferred.Contains(low) ? 0 : low == half - 1 ? 2 : 1)
            .ToArray();
        bool Single(int index) => index >= 0 && index < result.Count && !result[index].StartsChord
            && (index == 0 || !result[index - 1].StartsChord);
        bool Trill(int index) => Single(index) && Single(index + 1)
            && result[index + 1].HitTimeMs - result[index].HitTimeMs is var gap && gap > 0 && gap <= settings.TrillWindowMs;
        // The trill just turned: two trills in a row take two pairs (user rule: not 1-2 1-2).
        var lastEnd = -2;
        var lastLow = -1;
        var lastRoll = Array.Empty<int>();
        // Where the rolls of the current row start: rolls as long as each other with nothing between them.
        var rollRun = new List<int>();
        // How many notes roll from each note that starts a roll (user rule): taps that each come within the window of the one
        // before, cut as TrillRuns cuts them (ChainPieces). Long notes end a roll. A note left over from a cut is inside a
        // chain but rolls with nothing, so it is never paired with the next roll.
        bool Tap(int index) => Single(index) && result[index].HoldEndMs is null;
        var rollFrom = new int[result.Count];
        var inChain = new bool[result.Count];
        for (var start = 0; start + 1 < result.Count;)
        {
            if (!Tap(start) || !Tap(start + 1) || !Trill(start)) { start++; continue; }
            var end = start + 1;
            while (end + 1 < result.Count && Tap(end + 1) && Trill(end)) end++;
            var first = start;
            var at = start;
            foreach (var piece in ChainPieces(end - start + 1, k => result[first + k + 1].HitTimeMs - result[first + k].HitTimeMs, RollLanes(settings)))
            {
                rollFrom[at] = piece;
                at += piece;
            }
            for (var k = start; k <= end; k++) inChain[k] = true;
            start = end + 1;
        }
        for (var i = 0; i + 1 < result.Count; i++)
        {
            if (!Trill(i)) continue;
            // Two singles this close with a long note among them are still turned as a pair.
            var length = rollFrom[i] > 0 ? rollFrom[i] : inChain[i] ? 0 : 2;
            if (length < 2) continue;
            if (length >= 3)
            {
                LayRoll(i, length);
                i += length - 1;
                continue;
            }
            // Taken in twos, as a chord group is: a third note this close starts the next pair.
            var lane = visible.IndexOf(result[i].Tile);
            var other = visible.IndexOf(result[i + 1].Tile);
            var step = Direction(settings, Math.Max(result[i].Mode, result[i + 1].Mode));
            var follows = lastEnd == i - 1;
            bool Repeats((int, int) option) => follows && Math.Min(option.Item1, option.Item2) == lastLow;
            // A trill right after is turned the same way in its turn, and two pairs that run one way never share a key.
            var ahead = Trill(i + 2) ? [] : LanesAround(result, i + 2, backward: false);
            // The single after a trill sits beside it, never back on one of its lanes (user report: 1-2 2 for 1-2 3): the
            // hand has just played both.
            bool ClearsSingle((int, int) option) => ahead.Length != 1
                || (!ahead.Contains(visible[option.Item1]) && !ahead.Contains(visible[option.Item2]));
            // A roll that starts at the far edge has nowhere to go that way and rolls back (user rule), so that one is kept too.
            var rollsBack = other - lane == -step && (lane + step < 0 || lane + step >= visible.Count);
            var fine = lane >= 0 && (other - lane == step || rollsBack) && pairs.Contains(Math.Min(lane, other)) && !Repeats((lane, other))
                && !LanesAround(result, i - 1, backward: true).Contains(result[i].Tile);
            if (fine && ClearsSingle((lane, other)))
            {
                (lastEnd, lastLow) = (i + 1, Math.Min(lane, other));
                i++;
                continue;
            }
            if (lane >= 0 && result[i].HoldEndMs is null && result[i + 1].HoldEndMs is null)
            {
                var before = LanesAround(result, i - 1, backward: true);
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
                // A trill that is fine but for the single after it moves only to a pair that is fine and clears the single too.
                var chosen = (fine ? running.Where(option => !Repeats(option) && ClearsSingle(option))
                        : running.Where(option => !Repeats(option) && ClearsSingle(option)).Concat(running.Where(ClearsSingle)).Concat(running)
                            .Concat(options.Where(option => option.Item1 == lane && !ahead.Contains(visible[option.Item2]))))
                    .Select(option => ((int, int)?)option).FirstOrDefault();
                // Set to never, a trill is never left on the center pair, as a two-note chord never is (user rule): failing
                // all else it takes the pair that touches the notes around it least (3-4 2-3 1-2 reads 3-4 2-1 1-2).
                if (chosen is null && settings.TrillCenterNever && other >= 0 && Math.Abs(other - lane) == 1 && Center(Math.Min(lane, other)))
                    chosen = options.OrderBy(option => (before.Contains(visible[option.Item1]) ? 1 : 0)
                            + (ahead.Contains(visible[option.Item2]) ? 1 : 0) + (ClearsSingle(option) ? 0 : 1))
                        .Select(option => ((int, int)?)option).FirstOrDefault();
                if (chosen is { } pick)
                {
                    result[i] = result[i] with { Tile = visible[pick.Item1] };
                    result[i + 1] = result[i + 1] with { Tile = visible[pick.Item2] };
                }
            }
            // Where no pair clears the single, the single moves beside the trill instead: on the side it ran to, else the
            // other, else the nearest lane left (2-1 4 when 3 comes next), never onto the event after it nor a lane a long
            // note holds.
            var (low, high) = (visible.IndexOf(result[i].Tile), visible.IndexOf(result[i + 1].Tile));
            if (ahead.Length == 1 && low >= 0 && high >= 0 && Math.Abs(high - low) == 1 && !ClearsSingle((low, high))
                && Single(i + 2) && result[i + 2].HoldEndMs is null)
            {
                var after = LanesAround(result, i + 3, backward: false);
                var byHand = settings.PlaysByHand && result[i].Mode <= 4;
                var sides = new[] { high + (high - low), low - (high - low) }
                    .Concat(Enumerable.Range(0, visible.Count).OrderBy(side => Math.Abs(side - high)))
                    .Where(side => side != low && side != high);
                foreach (var side in sides)
                    if (side >= 0 && side < visible.Count && !after.Contains(visible[side]) && Free(visible[side], result[i + 2].HitTimeMs)
                        && (!byHand || side / 4 == low / 4))
                    {
                        result[i + 2] = result[i + 2] with { Tile = visible[side] };
                        break;
                    }
            }
            (lastEnd, lastLow) = (i + 1, Math.Min(visible.IndexOf(result[i].Tile), visible.IndexOf(result[i + 1].Tile)));
            lastRoll = [visible.IndexOf(result[i].Tile), visible.IndexOf(result[i + 1].Tile)];
            i++;
        }

        // A roll of three notes or more (user rule: a b c, each within the window of the one before, read 3-2-1 or 4-3-2 on
        // four lanes) runs over that many lanes side by side in the pattern's direction, back the other way only when it starts
        // on the far edge lane (1-2-3; 2-3-4 is against the pattern, user report). A roll right after one as long takes the same
        // lanes (user rule: 4-3-2 4-3-2). One already laid out so stays; else the lanes keep off the event before and the event
        // after, the first note stays where it can, and nothing moves onto a lane a long note holds. The center rule is the
        // two-note trill's own, so it does not apply here. A roll with nowhere to go stays as it is.
        void LayRoll(int at, int length)
        {
            var step = Direction(settings, Enumerable.Range(at, length).Max(k => result[k].Mode));
            var lanes = Enumerable.Range(at, length).Select(k => visible.IndexOf(result[k].Tile)).ToArray();
            // A roll the pattern walks is kept as the cursor laid it (see WalksPattern).
            if (WalksPattern(length, Enumerable.Range(at, length).Min(k => Math.Min(result[k].Mode, visible.Count))))
            {
                (lastEnd, lastRoll, lastLow) = (at + length - 1, lanes, Math.Min(lanes[^2], lanes[^1]));
                rollRun.Clear();
                return;
            }
            var before = LanesAround(result, at - 1, backward: true);
            var ahead = LanesAround(result, at + length, backward: false);
            // Played by hand, a roll of four keys or fewer stays in one hand (user rule).
            var byHand = settings.PlaysByHand && Enumerable.Range(at, length).Max(k => result[k].Mode) <= 4;
            bool Fits(int first, int way) => first >= 0 && first < visible.Count
                && first + (length - 1) * way >= 0 && first + (length - 1) * way < visible.Count
                && (!byHand || first / 4 == (first + (length - 1) * way) / 4);
            // The far edge is where the pattern's way runs out of lanes (of the hand, played by hand).
            bool AtEdge(int first) => first + step < 0 || first + step >= visible.Count || (byHand && (first + step) / 4 != first / 4);
            bool Allowed(int first, int way) => Fits(first, way) && (way == step || AtEdge(first));
            int LaneAt(int first, int way, int k) => first + k * way;
            bool Usable(int first, int way) => Allowed(first, way)
                && Enumerable.Range(0, length).All(k => Free(visible[LaneAt(first, way, k)], result[at + k].HitTimeMs));
            bool Clear(int first, int way) => !before.Contains(visible[first]) && !ahead.Contains(visible[LaneAt(first, way, length - 1)]);
            // The roll right before, when it is as long and nothing comes between them.
            (int First, int Way)? last = lastEnd == at - 1 && lastRoll.Length == length && lastRoll.All(lane => lane >= 0)
                && Math.Abs(lastRoll[1] - lastRoll[0]) == 1 && Enumerable.Range(1, length - 1).All(k => lastRoll[k] - lastRoll[k - 1] == lastRoll[1] - lastRoll[0])
                ? (lastRoll[0], lastRoll[1] - lastRoll[0]) : null;
            bool Again(int first, int way) => last is { } roll && roll.First == first && roll.Way == way;
            var repeatable = last is { } previous && Usable(previous.First, previous.Way) && Clear(previous.First, previous.Way);
            // Only a single right after on the lane it would end on stands in the way: it moves beside the roll instead, on the
            // side the roll ran to, else the other, else the nearest lane left, never onto the event after it nor a held lane
            // (user rule, as after a two-note trill).
            var after = at + length;
            if (!repeatable && last is { } again && Usable(again.First, again.Way) && !before.Contains(visible[again.First])
                && ahead.Length == 1 && Single(after) && result[after].HoldEndMs is null)
            {
                var roll = Enumerable.Range(0, length).Select(k => LaneAt(again.First, again.Way, k)).ToArray();
                var next = LanesAround(result, after + 1, backward: false);
                var side = new[] { roll[^1] + again.Way, roll[0] - again.Way }
                    .Concat(Enumerable.Range(0, visible.Count).OrderBy(lane => Math.Abs(lane - roll[^1])))
                    .Where(lane => lane >= 0 && lane < visible.Count && !roll.Contains(lane) && !next.Contains(visible[lane])
                        && Free(visible[lane], result[after].HitTimeMs) && (!byHand || lane / 4 == roll[0] / 4))
                    .Select(lane => (int?)lane).FirstOrDefault();
                if (side is { } moved)
                {
                    result[after] = result[after] with { Tile = visible[moved] };
                    ahead = [visible[moved]];
                    repeatable = true;
                }
            }
            var way = lanes.All(lane => lane >= 0) && Enumerable.Range(1, length - 1).All(k => lanes[k] - lanes[k - 1] == lanes[1] - lanes[0])
                && Math.Abs(lanes[1] - lanes[0]) == 1 ? lanes[1] - lanes[0] : 0;
            if (way == 0 || !Allowed(lanes[0], way) || !Clear(lanes[0], way) || (repeatable && !Again(lanes[0], way)))
            {
                var current = lanes[0] >= 0 ? lanes[0] : 0;
                var chosen = Enumerable.Range(0, visible.Count).SelectMany(first => new[] { (First: first, Way: step), (First: first, Way: -step) })
                    .Where(option => Usable(option.First, option.Way))
                    .OrderBy(option => Clear(option.First, option.Way) ? 0 : 1)
                    .ThenBy(option => Again(option.First, option.Way) ? 0 : 1)
                    .ThenBy(option => option.Way == step ? 0 : 1)
                    // A roll turned the pattern's way keeps its lanes (2-3-4 becomes 4-3-2).
                    .ThenBy(option => Enumerable.Range(0, length).Select(k => LaneAt(option.First, option.Way, k)).Order().SequenceEqual(lanes.Order()) ? 0 : 1)
                    .ThenBy(option => Math.Abs(option.First - current))
                    .Select(option => ((int First, int Way)?)option).FirstOrDefault();
                if (chosen is { } pick)
                {
                    for (var k = 0; k < length; k++) result[at + k] = result[at + k] with { Tile = visible[LaneAt(pick.First, pick.Way, k)] };
                    lanes = Enumerable.Range(0, length).Select(k => LaneAt(pick.First, pick.Way, k)).ToArray();
                }
            }
            // A roll that could not take the lanes of the ones right before it gives them its own instead, when they are free
            // then and the event before them allows it: rolls in a row share one set of lanes either way (user rule). Those
            // before may have been laid by a chord block, which never saw the rolls after them.
            if (last is not null && !lanes.SequenceEqual(lastRoll) && rollRun.Count > 0 && lanes.All(lane => lane >= 0)
                && !LanesAround(result, rollRun[0] - 1, backward: true).Contains(visible[lanes[0]])
                && rollRun.All(start => Enumerable.Range(0, length).All(k => Free(visible[lanes[k]], result[start + k].HitTimeMs))))
                foreach (var start in rollRun)
                    for (var k = 0; k < length; k++) result[start + k] = result[start + k] with { Tile = visible[lanes[k]] };
            if (last is null) rollRun.Clear();
            rollRun.Add(at);
            lastEnd = at + length - 1;
            lastRoll = lanes;
            lastLow = Math.Min(lanes[^2], lanes[^1]);
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
        var pattern = settings.GetPatternFor(baseline[start].Mode, baseline[start].Tile).ToArray();
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
        // A step moves the pattern's lanes only, so it can pull a trill apart: 3-2 two steps on is 1-4, and 2K's 2-1 one
        // step on is 3-1, since lane 1 is not in 2K's pattern. The trill pass then pairs it again on other lanes and the
        // single after it lands on one of them (user report: 1-2 2 1-2 2). A step that keeps every trill as it runs comes first.
        var trills = Enumerable.Range(start, Math.Max(0, end - start - 1))
            .Where(i => baseline[i].HoldEndMs is null && baseline[i + 1].HoldEndMs is null
                && baseline[i + 1].HitTimeMs - baseline[i].HitTimeMs is var gap && gap > 0 && gap <= settings.TrillWindowMs)
            .ToArray();
        bool KeepsTrills(Func<int, int> map) => trills.All(i =>
            visible.IndexOf(map(baseline[i + 1].Tile)) - visible.IndexOf(map(baseline[i].Tile))
            == visible.IndexOf(baseline[i + 1].Tile) - visible.IndexOf(baseline[i].Tile));
        // Each step that keeps off the lanes just played and the long notes, best first: keeping the trills, beside a close
        // chord, then the fewest lanes back too soon, then no shift at all, then the order above.
        var options = steps.Select(step => (Step: step, Map: (Func<int, int>)(tile => Array.IndexOf(pattern, tile) is var at and >= 0 ? pattern[(at + step) % pattern.Length] : tile)))
            .Where(option => !previous.Contains(option.Map(first))
                && !Enumerable.Range(start, end - start).Any(i => baseline[i].HitTimeMs <= busyUntil[option.Map(baseline[i].Tile)]))
            .OrderBy(option => KeepsTrills(option.Map) ? 0 : 1)
            .ThenBy(option => chordClose && !Beside(option.Map(first)) ? 1 : 0)
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

    /// <summary>
    /// The lanes of the event at <paramref name="index"/>: one for a single, every member for a chord. A chord is the notes
    /// marked <see cref="VisualizerNote.StartsChord"/> in a row and the one after the last of them.
    /// </summary>
    private static int[] LanesAround(List<VisualizerNote> notes, int index, bool backward)
    {
        if (index < 0 || index >= notes.Count) return [];
        var (first, last) = ChordSpan(notes, index);
        return backward ? notes.Skip(first).Take(index - first + 1).Select(note => note.Tile).ToArray()
            : notes.Skip(index).Take(last - index + 1).Select(note => note.Tile).ToArray();
    }

    /// <summary>The first and last note of the chord the note at <paramref name="index"/> belongs to (itself for a single).</summary>
    internal static (int First, int Last) ChordSpan(IReadOnlyList<VisualizerNote> notes, int index)
    {
        var first = index;
        while (first > 0 && notes[first - 1].StartsChord) first--;
        var last = index;
        while (last + 1 < notes.Count && notes[last].StartsChord) last++;
        return (first, last);
    }

    /// <summary>The tiles of the chord that starts at <paramref name="at"/> in a block's assignment.</summary>
    private static int[] ChordTiles((int Tile, bool StartsChord)[] assignment, int at)
    {
        var end = at;
        while (end + 1 < assignment.Length && assignment[end].StartsChord) end++;
        return assignment.Skip(at).Take(end - at + 1).Select(placed => placed.Tile).ToArray();
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
        // The part each group plays in a trill: 1 opens one, 2 carries it on (its second note and any after it).
        var trillAt = TrillRuns(groups, settings, holdEnds, out var trillLengths);
        var roles = trillAt.Select(at => at == 1 ? 1 : at >= 2 ? 2 : 0).ToArray();
        // Every note of a roll knows how long it is, so a block that holds part of one leaves room for the rest.
        var rollLengths = new int[groups.Count];
        for (var i = 0; i < groups.Count; i++)
            if (trillAt[i] > 0) rollLengths[i] = trillAt[i] == 1 ? trillLengths[i] : rollLengths[i - 1];
        // Every note is checked against the long notes already placed, so no note ever lands on a lane still held, nor the
        // note after a long note on its lane (HeldUntil).
        var groupTimes = groups.Select(group => group.TimeMs).ToArray();
        // Experimental (user rule): on eight lanes a four-note chord takes one hand, the other one than the last such chord.
        var oneHand = settings.PlaysByHand;
        var lastHand = 1;
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
                // Played by hand, each block keeps to the hand its first note's run is in.
                var blockPreferred = preferred;
                if (oneHand && settings.GetPatternFor(mode, baseline[blockStart].Tile).ToArray() is var own && Array.IndexOf(own, baseline[blockStart].Tile) is var turn and >= 0)
                    blockPreferred = own.Skip(turn).Concat(own.Take(turn)).ToArray();
                // A four-note chord on one hand may need lanes outside the section's pattern (4K is 3-6 on eight lanes).
                var hand = oneHand && block.Any(group => group.Count == 4) ? 1 - lastHand : -1;
                var candidates = mode >= 4 && hand < 0 ? blockPreferred : blockPreferred.Concat(visible).Distinct().ToArray();
                // A chord with more notes than the section's pattern has lanes takes the screen's lanes too: it needs that many.
                if (block.Any(group => group.Count > candidates.Length)) candidates = candidates.Concat(visible).Distinct().ToArray();
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
                int[] Around(int[] values) => Enumerable.Range(blockStart - 2, blockEnd - blockStart + 2).Select(i => i >= 0 ? values[i] : 0).ToArray();
                var flow = new Flow(Enumerable.Range(blockStart, blockEnd - blockStart).Select(i => baseline[i].Tile).ToArray(),
                    Around(roles), Around(trillAt), Around(rollLengths),
                    Direction(settings, mode), settings.TrillCenterLast, settings.TrillCenterNever,
                    [result.Count >= 2 ? result[^2].Tile : 0, result.Count >= 1 ? result[^1].Tile : 0],
                    result.Count >= 1 ? result[^1].HitTimeMs : double.NegativeInfinity, settings.TrillWindowMs * NoteGenerator.RollLeadWindows,
                    lastPlayed, NoteGenerator.QuickReturnMs(settings, mode), oneHand && mode <= 4, blockPreferred);
                var previous = LanesAround(result, result.Count - 1, backward: true);
                (int Tile, bool StartsChord)[]? Find(int budget) => styles
                    .SelectMany(style => (budget == 0 ? [true] : new[] { true, false })
                        .SelectMany(keepChords => Enumerable.Range(1, 3).Select(level => (style, keepChords, level))))
                    .Select(option => Assign(block, candidates, visible, mode, extras, budget, option.level, option.keepChords, busyUntil,
                        option.style.Avoid ? lanes : null, option.style.Avoid ? used : null, option.style.Repeat, option.style.NoFollow, previous,
                        settings.GetPreferredChordLanes(), settings.UseChordCenterPair, option.style.Fresh ? flow : null, hand))
                    .FirstOrDefault(found => found is not null);
                var assignment = Find(0) ?? Enumerable.Range(1, extras.Length).Select(Find).FirstOrDefault(found => found is not null);
                if (assignment is null) throw Failure(settings, block[0].SourceIndex, block[0].TimeMs, mode, count);
                if (hand >= 0)
                    for (int g = 0, at = 0; g < block.Length; at += block[g].Count, g++)
                        if (block[g].Count == 4 && Array.IndexOf(visible, assignment[at].Tile) is var first && first % 4 == 0
                            && Enumerable.Range(0, 4).All(k => Array.IndexOf(visible, assignment[at + k].Tile) == first + k))
                            lastHand = first / 4;
                // Experimental: a single right before a chord keeps off the chord's lanes, so "3 3-4" reads "2 3-4".
                // Chords are placed after the singles, so the single is the one that moves; the chord keeps its pair.
                if (settings.UseChordAvoidBefore && result.Count > 0
                    && Array.FindIndex(assignment, placed => placed.StartsChord) is var chordAt and >= 0)
                {
                    var last = result.Count - 1;
                    var pair = ChordTiles(assignment, chordAt);
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
                    while (chord > 0 && assignment[chord - 1].StartsChord) chord--;
                    if (chord >= 0)
                    {
                        chordLanes = ChordTiles(assignment, chord);
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
        // The events in order: where each starts in the result and how many notes it has. Only two-note chords are locked;
        // a wider one keeps its lanes and ends a stretch.
        var events = new List<(int At, int Size)>();
        for (var i = 0; i < result.Count;)
        {
            var size = ChordSpan(result, i).Last - i + 1;
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
        int[] previousLanes, IReadOnlyList<int> preferredLanes, bool centerPair, Flow? flow = null, int hand = -1)
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
            // A group of three or more that the screen has lanes for is one chord (user rule: a b c with a-c in the chord
            // window), each member on the lane after the one before. Narrower or wider groups pair up in order, an odd
            // last member being a free single.
            var wide = groups[groupIndex].Count >= 3 && groups[groupIndex].Count <= visible.Length;
            var pairsWithPrevious = wide ? member >= 1 : member % 2 == 1;
            // Drawn as a single: no partner before it and none after it inside its group.
            var single = !pairsWithPrevious && member + 1 == groups[groupIndex].Count;
            // Chord lane preference (default 1-2,3-4): a chord takes the chosen pairs first, then the nearest lanes.
            // With no preference, chords follow the pattern order. Singles always do.
            var ordered = single || preferredLanes.Count == 0 ? choices
                : choices.OrderBy(tile => Array.IndexOf(visible, tile) is var lane
                        && preferredLanes.Any(start => lane >= start && lane <= start + 1) ? 0 : 1)
                    .ThenBy(tile => Array.IndexOf(visible, tile) is var lane && lane >= 0 ? lane : int.MaxValue).ToArray();
            // One hand per four-note chord on eight lanes (experimental): it starts on lane 1 or 5, the wanted hand first.
            // Only an order, so a chord that cannot have either hand is still placed.
            if (hand >= 0 && member == 0 && groups[groupIndex].Count == 4 && visible.Length == 8)
                ordered = ordered.OrderBy(tile => Array.IndexOf(visible, tile) is var lane && lane == hand * 4 ? 0 : lane == (1 - hand) * 4 ? 1 : 2).ToArray();
            var before = Before(groupIndex);
            if (flow is not null && single)
            {
                var role = flow.RoleOf(groupIndex);
                // A roll in the pattern's direction over two neighbours. The pair across the center counts: the trill setting
                // makes it the last resort, and below it only loses to a roll that keeps the flow; only "never" refuses it.
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
                    // Set to never, the center pair is no roll at all.
                    if (flow.NeverTrillCenter && Centered(tile, partner)) return 5;
                    return (breaks ? 3 : 0) + (Centered(tile, partner) ? 1 : 0);
                }
                // A roll of three notes or more (user rule) runs over that many lanes side by side: the pattern's way where it
                // fits, back from the far edge where it does not, and no center rule (that is the two-note trill's own). It may
                // reach past this block, so its first note looks at the lanes the rest will need: one a chord of this block
                // already holds cannot be had. Each note after the first goes on the way the roll started.
                var length = flow.LengthOf(groupIndex);
                // Played by hand, a roll of four keys or fewer stays in one hand (user rule).
                bool SameHand(int a, int b) => !flow.ByHand || a / 4 == b / 4;
                // Against the pattern only from the far edge lane (1-2-3, never 2-3-4: user report).
                bool AtEdge(int at) => at + flow.Step < 0 || at + flow.Step >= visible.Length || !SameHand(at, at + flow.Step);
                int RollWay(int at) => at < 0 ? 0
                    : at + (length - 1) * flow.Step is var end && end >= 0 && end < visible.Length && SameHand(at, end) ? flow.Step
                    : AtEdge(at) && at - (length - 1) * flow.Step is var back && back >= 0 && back < visible.Length && SameHand(at, back) ? -flow.Step : 0;
                int Long(int tile)
                {
                    var at = Array.IndexOf(visible, tile);
                    // A roll the pattern walks (see WalksPattern) starts on any lane and goes round from the far edge.
                    if (WalksPattern(length, Math.Min(mode, visible.Length)))
                    {
                        if (flow.PlaceOf(groupIndex) == 1) return 0;
                        var walked = Array.IndexOf(visible, LaneOf(groupIndex - 1));
                        return walked >= 0 && at == ((walked + flow.Step) % visible.Length + visible.Length) % visible.Length ? 0 : 2;
                    }
                    if (flow.PlaceOf(groupIndex) == 1)
                    {
                        var way = RollWay(at);
                        if (way == 0 || Enumerable.Range(1, length - 1)
                                .Any(k => groupIndex + k < groups.Length && chordTiles.Contains(visible[at + k * way]))) return 5;
                        var from = Array.IndexOf(visible, before.Length == 1 ? before[0] : 0) + flow.Step;
                        var continues = at == (from < 0 || from >= visible.Length ? (flow.Step < 0 ? visible.Length - 1 : 0) : from);
                        return way != flow.Step ? 2 : closeSingle && !continues ? 3 : 0;
                    }
                    var last = Array.IndexOf(visible, LaneOf(groupIndex - 1));
                    var going = flow.PlaceOf(groupIndex) == 2 ? RollWay(last)
                        : Array.IndexOf(visible, LaneOf(groupIndex - 2)) is var earlier && earlier >= 0 ? last - earlier : 0;
                    return last >= 0 && going != 0 && at == last + going ? 0 : 2;
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
                        : flow.NeverTrillCenter ? 4 : over && span <= flow.CloseMs ? 3 : 1;
                }
                // A lane played within the quick window would come back too soon while another is free (user rule: 2 1 2 1
                // between chords, with 3 and 4 free): it comes after the lanes that have rested.
                double PlayedAt(int tile) => Math.Max(flow.LastPlayed[tile], tiles.Select((placed, index) => (placed, index))
                    .Where(entry => entry.placed == tile).Select(entry => groups[owners[entry.index]].TimeMs).DefaultIfEmpty(double.NegativeInfinity).Max());
                bool Rested(int tile) => groups[groupIndex].TimeMs - PlayedAt(tile) >= flow.QuickMs;
                // A run of singles walks on from the lane really played before it (user report: 4 3 2 1 read 1 4 2 3). The
                // cursor's tile is a step behind wherever the note before it was moved, so following it put every later note
                // off the walk.
                bool WalksOn(int tile) => before.Length == 1 && flow.Walk is { Length: >= 3 } walk
                    && Array.IndexOf(walk, before[0]) is var from && from >= 0 && walk[(from + 1) % walk.Length] == tile;
                // Inside a run the walk wins over the quick window once its lane is a full turn old at the run's own pace (a
                // lane back after three notes of four): at one pace every order brings each lane back every turn, so resting
                // the oldest lane first only kept a run off its walk for good after a trill or chord (1 4 2 3 at 54ms). A lane
                // back sooner than that (2 | 4 3 2) still waits.
                bool WalksAround(int tile) => WalksOn(tile) && flow.Walk is { } turn
                    && groups[groupIndex].TimeMs - PlayedAt(tile) >= (turn.Length - 1) * (groups[groupIndex].TimeMs - lastTime) - 1;
                // After a chord a run of singles goes the way the pattern walks: beside the chord on that side, and round to
                // the far lane where the chord sits at the edge (user rule: 3-4 2, and 2-1 4 as after a trill rolled into the
                // edge). Before another chord the single stays beside it, so singles and chords take turns (3 1-2 3 1-2).
                int ChordWalk(int tile)
                {
                    if (!afterChord || beforeChord || flow.Walk is not { Length: >= 3 } walk) return 1;
                    var lead = before.FirstOrDefault(lane => Array.IndexOf(walk, lane) is var at && at >= 0 && !before.Contains(walk[(at + 1) % walk.Length]));
                    return lead != 0 && walk[(Array.IndexOf(walk, lead) + 1) % walk.Length] == tile ? 0 : 1;
                }
                ordered = ordered.OrderBy(tile => role != 0 && length >= 3 ? Long(tile) : role == 1 ? Opens(tile)
                        : role == 2 && before.Length == 1 ? RollsBack(before[0], tile) ? 1 : !Rolls(before[0], tile) ? 2
                            : Centered(before[0], tile) ? flow.NeverTrillCenter ? 2 : 1 : 0 : 0)
                    // Two trills in a row take two pairs (user rule: not 1-2 1-2).
                    .ThenBy(tile => role == 1 && length < 3 && SamePair(tile) ? 1 : 0)
                    .ThenBy(tile => role == 0 && trill is { } last ? BesideTrill(last, tile) : 0)
                    .ThenBy(ChordWalk)
                    .ThenBy(tile => afterChord && before.Any(lane => Beside(lane, tile)) ? 0 : 1)
                    .ThenBy(tile => leadsTrill ? Heads(tile) : 0)
                    .ThenBy(tile => beforeChord && BesidePair(tile) ? 0 : 1)
                    .ThenBy(tile => WalksAround(tile) ? 0 : 1)
                    .ThenBy(tile => Rested(tile) ? 0 : 1)
                    .ThenBy(tile => WalksOn(tile) ? 0 : 1)
                    .ThenBy(tile => tile == flow.Hints[groupIndex] ? 0 : 1)
                    .ThenBy(tile => tiles.Contains(tile) ? 1 : 0).ToArray();
            }
            foreach (var tile in ordered)
            {
                if (groups[groupIndex].TimeMs <= busyUntil[tile]) continue;
                // Two notes of one block never share a lane, except singles when the kept chord lanes leave too few of them.
                // Notes of one group sound together, so they never share a lane at all.
                // Fresh singles may come back to a single's lane too (2 3-4 2): the lane order above keeps that for when it helps.
                // A chord's lane is free again for a later single of the block once it has rested (the quick window is what
                // keeps a lane from coming back): kept off it, a run right after a chord fell out of the pattern's walk for good.
                bool RestedChordLane() => fresh && flow is not null && tiles.Select((placed, index) => (placed, index)).Where(entry => entry.placed == tile)
                    .All(entry => groups[groupIndex].TimeMs - groups[owners[entry.index]].TimeMs >= flow.QuickMs);
                var reuse = (repeatSingles || fresh) && single && (!chordTiles.Contains(tile) || RestedChordLane())
                    && !tiles.Where((t, index) => t == tile && owners[index] == groupIndex).Any();
                if (tiles.Contains(tile) && !reuse) continue;
                if (single && sectionLanes is not null && Array.IndexOf(sectionLanes, tile) >= 0) continue;
                if (noFollow && (fresh ? before.Contains(tile)
                    : single && tile == (tiles.Count > 0 ? tiles[^1] : previousLanes.LastOrDefault()))) continue;
                var spare =Array.IndexOf(extras, tile) >= 0;
                if (spare && spareUsed == budget) continue;
                if (spare && keepChords && (pairsWithPrevious || member + 1 < groups[groupIndex].Count)) continue;
                var chord = pairsWithPrevious && !spare && Array.IndexOf(extras, tiles[^1]) < 0;
                // A wide chord runs over lanes side by side in one direction. The center rule is the two-note chord's own
                // (user rule): three notes on four lanes cannot keep off the center anyway.
                if (chord && !(wide ? Array.IndexOf(visible, tile) == Array.IndexOf(visible, tiles[^1]) + 1 : Adjacent(tiles[^1], tile))) continue;
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
    /// trill (1 opens, 2 closes; the two groups before the block first), its place in its roll and how long that roll is,
    /// the pattern's direction over the lanes, whether a trill keeps off the center (last resort) or never takes it, the
    /// last two tiles placed before the block, when the last of them sounded, and how close a single must come before a trill
    /// to be part of its roll; when each lane was last played, and how soon a lane coming back is too soon.
    /// </summary>
    private sealed record Flow(int[] Hints, int[] Roles, int[] Places, int[] Lengths, int Step, bool NoTrillCenter, bool NeverTrillCenter,
        int[] Trailing, double LastTimeMs, double CloseMs, double[] LastPlayed, double QuickMs, bool ByHand = false, int[]? Walk = null)
    {
        /// <summary>A group's part in a trill; the two groups before the block are -2 and -1.</summary>
        public int RoleOf(int group) => Roles[group + 2];
        /// <summary>A group's place in its roll, 1 for the first note (0 outside any), and how many notes the roll has.</summary>
        public int PlaceOf(int group) => Places[group + 2];
        public int LengthOf(int group) => Lengths[group + 2];
    }

    internal static NotePlacementException HoldOverlap(VisualizerSettings settings, int index, double time) =>
        new(Languages.Format(settings.Language, "Placement.Failed", index + 1, time.ToString("F3", CultureInfo.InvariantCulture),
            Languages.Format(settings.Language, "Placement.HoldOverlap", settings.MaximumTileCount)));

    private static NotePlacementException Failure(VisualizerSettings settings, int index, double time, int mode, int count) =>
        new(Languages.Format(settings.Language, "Placement.Failed", index + 1, time.ToString("F3", CultureInfo.InvariantCulture),
            Languages.Format(settings.Language, "Placement.Chords", settings.MaximumTileCount, mode, count)));
}
