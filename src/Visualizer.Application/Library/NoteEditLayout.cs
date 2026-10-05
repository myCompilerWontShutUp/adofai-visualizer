using Visualizer.Core.Localization;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;

namespace Visualizer.Application.Library;

/// <summary>
/// Lays a saved edit over the notes the rules produced. Only lanes and chord grouping are replaced; hit times, the notes
/// that exist and everything about judgment stay exactly as the parser and the rules made them.
/// </summary>
public static class NoteEditLayout
{
    /// <summary>Tiles an edit puts notes on, ascending.</summary>
    public static IReadOnlyList<int> TilesOf(NoteEdit layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout.Lanes.Values.Where(tile => tile is >= 1 and <= 8).Distinct().Order().ToArray();
    }

    /// <summary>
    /// Returns the notes with the edit applied, in time order. <paramref name="hits"/> is the source hit of each note and
    /// comes back reordered to match. A lane the screen does not show fails like any other placement problem, so loading
    /// the edit offers the lane change that fits it, as it does for chords. <paramref name="anyTile"/> takes every tile as
    /// it is, for printing an edit rather than playing it.
    /// </summary>
    public static IReadOnlyList<VisualizerNote> Apply(IReadOnlyList<VisualizerNote> notes, IReadOnlyList<int> hits,
        NoteEdit layout, VisualizerSettings settings, out IReadOnlyList<int> orderedHits, bool anyTile = false)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(settings);
        if (notes.Count != hits.Count) throw new ArgumentException("Every note needs its hit.", nameof(hits));

        // A chord with another note between its hits is not one (user rule): the editor splits it on opening,
        // and here it plays as single notes, so both screens show the same thing.
        var present = hits.ToHashSet();
        var chordOf = new Dictionary<int, int>();
        for (var group = 0; group < layout.Chords.Count; group++)
        {
            var members = layout.Chords[group];
            if (members.Count == 0) continue;
            var (low, high) = (members.Min(), members.Max());
            if (present.Any(hit => hit > low && hit < high && !members.Contains(hit))) continue;
            foreach (var hit in members) chordOf[hit] = group + 1;
        }

        // A chord shows at its first hit, exactly as an automatic one does.
        var chordTime = new Dictionary<int, double>();
        for (var i = 0; i < notes.Count; i++)
            if (chordOf.TryGetValue(hits[i], out var group))
                chordTime[group] = chordTime.TryGetValue(group, out var first) ? Math.Min(first, notes[i].HitTimeMs) : notes[i].HitTimeMs;

        var placed = new List<(VisualizerNote Note, int Hit)>(notes.Count);
        // Tiles are places on the screen, not a count: a 4-key pattern already uses tiles 3..6, so they are never compared
        // with the maximum. A tile the screen does not show is refused, as a chord with no lane is.
        var shown = settings.GetDisplayTiles().ToHashSet();
        for (var i = 0; i < notes.Count; i++)
        {
            var hit = hits[i];
            var tile = layout.Lanes.TryGetValue(hit, out var lane) ? lane : notes[i].Tile;
            if (!anyTile && !shown.Contains(tile))
                throw new NotePlacementException(Languages.Format(settings.Language, "Placement.EditedLane", hit + 1, tile));
            var group = chordOf.TryGetValue(hit, out var id) ? id : 0;
            var time = group == 0 ? notes[i].HitTimeMs : chordTime[group];
            placed.Add((notes[i] with { Tile = tile, HitTimeMs = time, StartsChord = false }, hit));
        }
        // Order by what is shown, then by chord (its first hit), then by hit: a chord's members always sit side by side, so
        // the first can carry the mark even when another note shows at the same moment.
        int Key(int hit) => chordOf.TryGetValue(hit, out var group) ? layout.Chords[group - 1].Min() : hit;
        placed.Sort((left, right) => left.Note.HitTimeMs != right.Note.HitTimeMs ? left.Note.HitTimeMs.CompareTo(right.Note.HitTimeMs)
            : Key(left.Hit) != Key(right.Hit) ? Key(left.Hit).CompareTo(Key(right.Hit)) : left.Hit.CompareTo(right.Hit));
        for (var i = 0; i < placed.Count - 1; i++)
        {
            var (note, hit) = placed[i];
            if (chordOf.TryGetValue(hit, out var group) && chordOf.TryGetValue(placed[i + 1].Hit, out var next) && group == next)
                placed[i] = (note with { StartsChord = true }, hit);
        }
        orderedHits = placed.Select(item => item.Hit).ToArray();
        return placed.Select(item => item.Note).ToArray();
    }
}
