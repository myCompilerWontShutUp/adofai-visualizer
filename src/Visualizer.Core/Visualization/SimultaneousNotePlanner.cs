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

    /// <summary>Per logical tile (1..8), the end of the long note occupying it; negative infinity when free.</summary>
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
    /// refused on four lanes while the setting says so. The first note keeps its lane, so the pattern is unchanged and only
    /// the second note moves; nothing moves onto a lane a long note still holds, and a trill that has nowhere to go is left
    /// as it is (a trill is how it reads, not whether it can be placed).
    /// </summary>
    private static void SplitTrills(List<VisualizerNote> result, VisualizerSettings settings, double[] busyUntil)
    {
        if (settings.TrillWindowMs <= 0) return;
        var visible = settings.GetVisibleTiles().ToList();
        var preferred = settings.GetPreferredTrillLanes();
        var half = visible.Count / 2;
        // Pair starts a trill may take, best first: the preference, then any other adjacent pair, then the one across the
        // center. Four lanes never cross the center while the setting holds.
        var pairs = Enumerable.Range(0, Math.Max(0, visible.Count - 1))
            .Where(low => low != half - 1 || visible.Count != 4 || !settings.NoTrillCenterPair)
            .OrderBy(low => preferred.Contains(low) ? 0 : low == half - 1 ? 2 : 1)
            .ToArray();
        bool Single(int index) => index >= 0 && index < result.Count && !result[index].StartsChord
            && (index == 0 || !result[index - 1].StartsChord);
        for (var i = 0; i + 1 < result.Count; i++)
        {
            if (!Single(i) || !Single(i + 1)) continue;
            var gap = result[i + 1].HitTimeMs - result[i].HitTimeMs;
            if (gap <= 0 || gap > settings.TrillWindowMs) continue;
            // Taken in twos, as a chord group is: a third note this close starts the next pair.
            var lane = visible.IndexOf(result[i].Tile);
            var other = visible.IndexOf(result[i + 1].Tile);
            if (lane >= 0 && Math.Abs(lane - other) == 1 && pairs.Contains(Math.Min(lane, other))) { i++; continue; }
            if (lane >= 0 && result[i].HoldEndMs is null && result[i + 1].HoldEndMs is null)
            {
                var partner = pairs.Where(low => low == lane || low + 1 == lane)
                    .Select(low => low == lane ? low + 1 : low)
                    .Where(position => result[i + 1].HitTimeMs >= busyUntil[visible[position]])
                    .Select(position => (int?)visible[position])
                    .FirstOrDefault();
                if (partner is { } tile) result[i + 1] = result[i + 1] with { Tile = tile };
            }
            i++;
        }
    }

    internal static IReadOnlyList<VisualizerNote> Place(IReadOnlyList<GroupedHit> groups,
        IReadOnlyList<VisualizerNote> baseline, VisualizerSettings settings, IReadOnlyList<double?>? holdEnds = null)
    {
        var result = new List<VisualizerNote>();
        var visible = settings.GetVisibleTiles().ToArray();
        var extras = settings.GetExtraTiles().ToArray();
        var busyUntil = NewBusyLanes();
        // Every note is checked against the long notes already placed, so no note ever starts on a lane still held. A note
        // exactly on a long note's end is allowed: that is the moment the key comes up, and two holds in a row ask the
        // player to press again right there (scrPlayer.UpdateHoldKeys takes the press as soon as the next tile is a hold).
        void Add(VisualizerNote note, int sourceIndex)
        {
            if (note.HitTimeMs < busyUntil[note.Tile]) throw HoldOverlap(settings, sourceIndex, note.HitTimeMs);
            result.Add(note);
        }
        void Track(int from)
        {
            for (var i = from; i < result.Count; i++)
                if (result[i].HoldEndMs is double holdEnd) busyUntil[result[i].Tile] = Math.Max(busyUntil[result[i].Tile], holdEnd);
        }
        for (var start = 0; start < groups.Count;)
        {
            var mode = baseline[start].Mode;
            var end = start + 1;
            while (end < groups.Count && baseline[end].Mode == mode) end++;
            if (!Enumerable.Range(start, end - start).Any(i => groups[i].Count > 1))
            {
                for (var i = start; i < end; i++)
                {
                    Add(baseline[i], groups[i].SourceIndex);
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
            var previousTile = 0;
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
                // (and always with the switch off) only the original style is used, so placement is unchanged.
                (bool Avoid, bool Repeat, bool NoFollow)[] styles = chordLanes is null
                    ? [(false, false, false)]
                    : [(true, false, true), (true, true, true), (true, true, false), (false, false, true), (false, false, false)];
                var lanes = chordLanes;
                var used = laneHistory.ToArray();
                var previous = previousTile;
                (int Tile, bool StartsChord)[]? Find(int budget) => styles
                    .SelectMany(style => (budget == 0 ? [true] : new[] { true, false })
                        .SelectMany(keepChords => Enumerable.Range(1, 3).Select(level => (style, keepChords, level))))
                    .Select(option => Assign(block, candidates, visible, mode, extras, budget, option.level, option.keepChords, busyUntil,
                        option.style.Avoid ? lanes : null, option.style.Avoid ? used : null, option.style.Repeat, option.style.NoFollow, previous,
                        settings.GetPreferredChordLanes(), settings.UseChordCenterPair))
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
                previousTile = assignment[^1].Tile;
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
        SplitTrills(result, settings, busyUntil);
        return result.AsReadOnly();
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
    /// off the lane of the note right before it, <paramref name="previousTile"/> being the last lane of the previous block.
    /// <paramref name="preferredLane"/> is the 0-based lane a chord tries first (-1 for none), and <paramref name="centerPair"/>
    /// lets a chord cross the screen center on four lanes.
    /// </summary>
    private static (int Tile, bool StartsChord)[]? Assign(GroupedHit[] groups, int[] candidates, int[] visible, int mode, int[] extras, int budget,
        int level, bool keepChords, double[] busyUntil, int[]? sectionLanes, int[]? sectionUsed, bool repeatSingles, bool noFollow,
        int previousTile, IReadOnlyList<int> preferredLanes, bool centerPair)
    {
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
            // Chord lane preference (timing, default none): a chord takes the chosen pair first (lanes 1-2 or 3-4), then the nearest lanes.
            // While the preference is off, chords follow the pattern order. Singles always do.
            var ordered = single || preferredLanes.Count == 0 ? choices
                : choices.OrderBy(tile => Array.IndexOf(visible, tile) is var lane
                        && preferredLanes.Any(start => lane >= start && lane <= start + 1) ? 0 : 1)
                    .ThenBy(tile => Array.IndexOf(visible, tile) is var lane && lane >= 0 ? lane : int.MaxValue).ToArray();
            foreach (var tile in ordered)
            {
                if (groups[groupIndex].TimeMs <= busyUntil[tile]) continue;
                // Two notes of one block never share a lane, except singles when the kept chord lanes leave too few of them.
                // Notes of one group sound together, so they never share a lane at all.
                var reuse = repeatSingles && single && !chordTiles.Contains(tile)
                    && !tiles.Where((t, index) => t == tile && owners[index] == groupIndex).Any();
                if (tiles.Contains(tile) && !reuse) continue;
                if (single && sectionLanes is not null && Array.IndexOf(sectionLanes, tile) >= 0) continue;
                if (single && noFollow && tile == (tiles.Count > 0 ? tiles[^1] : previousTile)) continue;
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

    internal static NotePlacementException HoldOverlap(VisualizerSettings settings, int index, double time) =>
        new(Languages.Format(settings.Language, "Placement.Failed", index + 1, time.ToString("F3", CultureInfo.InvariantCulture),
            Languages.Format(settings.Language, "Placement.HoldOverlap", settings.MaximumTileCount)));

    private static NotePlacementException Failure(VisualizerSettings settings, int index, double time, int mode, int count) =>
        new(Languages.Format(settings.Language, "Placement.Failed", index + 1, time.ToString("F3", CultureInfo.InvariantCulture),
            Languages.Format(settings.Language, "Placement.Chords", settings.MaximumTileCount, mode, count)));
}
