using System.IO;
using Visualizer.App.ViewModels;
using Visualizer.App.Views;
using Visualizer.Application.Cache;
using Visualizer.Application.Maps;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using static Visualizer.Tests.Program;
using Path = System.IO.Path;

namespace Visualizer.Tests;

/// <summary>
/// 6-tile mode: settings and thresholds between 8K and 4K, cycles, chord lane pairs on 4- and 6-lane
/// screens, extra lane placement at 6 lanes and the offer of the smallest wider maximum.
/// </summary>
internal static class SixKeyTests
{
    private static MapCacheEntry Entry(string id, ParsedTimeline timeline) =>
        new(id, "unused", "map.adofai", id, MapParseStatus.Supported, timeline, Array.Empty<string>(), "test", "test");

    /// <summary>Every chord is on adjacent lanes at one time, and never across the middle of a 4-lane screen.</summary>
    private static void AssertPairs(IReadOnlyList<VisualizerNote> notes, VisualizerSettings settings)
    {
        var visible = settings.GetVisibleTiles().ToList();
        for (var i = 0; i + 1 < notes.Count; i++)
        {
            if (!notes[i].StartsChord) continue;
            // A chord of three or more lies over lanes side by side and crosses pairs by nature (user rule: the pair rules
            // are the two-note chord's own).
            var end = i;
            while (end + 1 < notes.Count && notes[end].StartsChord) end++;
            var lanes = Enumerable.Range(i, end - i + 1).Select(k => visible.IndexOf(notes[k].Tile)).Order().ToArray();
            Assert(Enumerable.Range(i, end - i + 1).All(k => notes[k].HitTimeMs == notes[i].HitTimeMs) && lanes[^1] - lanes[0] == lanes.Length - 1
                && (lanes.Length > 2 || !(visible.Count == 4 && lanes[0] == 1)),
                $"Chord on lanes {string.Join("-", lanes.Select(lane => lane + 1))} (mode {notes[i].Mode}): " + string.Join(" ", notes.Select(n => n.Tile)));
            i = end;
        }
    }

    /// <summary>
    /// Every chord sits on a pair the lane preference names. With the default "1-2,3-4" those are the screen's first two
    /// pairs: tiles 3-4 and 5-6 on four lanes, 2-3 and 4-5 on six, 1-2 and 3-4 on eight.
    /// </summary>
    private static void AssertPreferred(IReadOnlyList<VisualizerNote> notes, VisualizerSettings settings)
    {
        var visible = settings.GetVisibleTiles().ToList();
        var wanted = settings.GetPreferredChordLanes().Select(start => (visible[start], visible[start + 1])).ToList();
        Assert(wanted.Count > 0, "This check is about a preference being set");
        for (var i = 0; i + 1 < notes.Count; i++)
        {
            if (!notes[i].StartsChord) continue;
            var pair = (Math.Min(notes[i].Tile, notes[i + 1].Tile), Math.Max(notes[i].Tile, notes[i + 1].Tile));
            Assert(wanted.Contains(pair), $"Chord on tiles {pair.Item1}-{pair.Item2}: " + string.Join(" ", notes.Select(n => n.Tile)));
        }
    }

    /// <summary>
    /// The first chord of a run takes a preferred pair. Later ones in the same busy block may not: a chord never shares a
    /// lane with the notes around it, and a 1K/2K block also keeps its lanes contiguous, so the pair can be taken already.
    /// </summary>
    private static void AssertFirstChordPreferred(IReadOnlyList<VisualizerNote> notes, VisualizerSettings settings)
    {
        var visible = settings.GetVisibleTiles().ToList();
        var wanted = settings.GetPreferredChordLanes().Select(start => (visible[start], visible[start + 1])).ToList();
        var first = Enumerable.Range(0, notes.Count).First(index => notes[index].StartsChord);
        var pair = (Math.Min(notes[first].Tile, notes[first + 1].Tile), Math.Max(notes[first].Tile, notes[first + 1].Tile));
        Assert(wanted.Contains(pair), $"The first chord is on tiles {pair.Item1}-{pair.Item2}: " + string.Join(" ", notes.Select(n => n.Tile)));
    }

    public static IEnumerable<(string Name, Action Run)> Create(string artifacts)
    {
        yield return ("6 tiles: settings, thresholds and dynamic selection between 8 and 4", () =>
        {
            // Values are pinned so editing DefaultSettings.jsonc does not change this rule check.
            var s = new VisualizerSettings { MaximumTileCount = 4, MinimumTileCount = 1, EightKeyBoundaryMs = 20, SixKeyBoundaryMs = 30,
                FourKeyBoundaryMs = 50, TwoKeyBoundaryMs = 500, SixKeyPattern = "2 3 4 5 6 7", SixKeyOrder = "6 5 4 3 2 1",
                FourKeyPattern = "3 4 5 6", TwoKeyPattern = "4 5", EnableTilePreference = false };
            Equal(s.SixKeyBoundaryMs, 30d); Equal(s.SixKeyPattern, "2 3 4 5 6 7"); Equal(s.SixKeyOrder, "6 5 4 3 2 1");
            Assert(VisualizerSettings.Modes.SequenceEqual(new[] { 1, 2, 4, 6, 8 }), "Modes include 6");
            Sequence(s.GetPattern(6), 7, 6, 5, 4, 3, 2);
            Sequence((s with { EnableTilePreference = true, SixKeyOrder = "1 2 3 4 5 6" }).GetPattern(6), 2, 3, 4, 5, 6, 7);
            var six = s with { MaximumTileCount = 6 };
            six.EnsureValid();
            Sequence(six.GetVisibleTiles(), 2, 3, 4, 5, 6, 7);
            var selector = new DynamicTimingSelector(six);
            Equal(selector.Select(10), 6); Equal(selector.Select(29.9), 6); Equal(selector.Select(30), 4);
            Equal(selector.Select(49), 4); Equal(selector.Select(50), 2); Equal(selector.Select(500), 1);
            var eight = new DynamicTimingSelector(s with { MaximumTileCount = 8 });
            Equal(eight.Select(19), 8); Equal(eight.Select(20), 6); Equal(eight.Select(30), 4);
            Equal(new DynamicTimingSelector(s).Select(10), 4); // The default maximum is 4.
            Equal(new DynamicTimingSelector(s with { MaximumTileCount = 8, MinimumTileCount = 6 }).Select(40), 6);
            Assert(six.IsThresholdEnabled(6) && !six.IsThresholdEnabled(8), "6 lanes use the 6K ↔ 4K threshold only above");
            Assert((s with { MaximumTileCount = 8, MinimumTileCount = 6 }).IsThresholdEnabled(8)
                && !(s with { MaximumTileCount = 8, MinimumTileCount = 6 }).IsThresholdEnabled(6), "A 6-lane minimum disables the thresholds below");
            Throws<ArgumentException>(() => (s with { MaximumTileCount = 8, SixKeyBoundaryMs = 60 }).EnsureValid());
            Throws<ArgumentException>(() => (s with { MaximumTileCount = 8, EightKeyBoundaryMs = 40 }).EnsureValid());
            // Two equal thresholds are allowed and leave the faster mode unused (user request): with 8K 20, 6K 50 and
            // 4K 50 the six-lane band is empty, so four lanes go straight to eight.
            var skipped = s with { MaximumTileCount = 8, SixKeyBoundaryMs = 50 };
            skipped.EnsureValid();
            var band = new DynamicTimingSelector(skipped);
            Equal(band.Select(10), 8);
            Equal(band.Select(35), 4);
            Equal(band.Select(60), 2);
            Equal(band.Select(600), 1);
            // The same with the eight-lane threshold: eight is the one left out then.
            var noEight = s with { MaximumTileCount = 8, EightKeyBoundaryMs = 30 };
            noEight.EnsureValid();
            Equal(new DynamicTimingSelector(noEight).Select(10), 6);
            Throws<ArgumentException>(() => (s with { MaximumTileCount = 6, SixKeyBoundaryMs = 0 }).EnsureValid());
            Throws<ArgumentException>(() => (s with { MaximumTileCount = 6, SixKeyPattern = "1 2 3 4 5" }).EnsureValid());
            Throws<ArgumentException>(() => (six with { FourKeyPattern = "1 2 3 4" }).EnsureValid()); // Tile 1 is not on a 6-lane screen.
            foreach (var invalid in new[] { 3, 5, 7 }) Throws<ArgumentException>(() => (s with { MaximumTileCount = invalid }).EnsureValid());
            (s with { MaximumTileCount = 6, MinimumTileCount = 6 }).EnsureValid();
            Assert(!s.HasSameNoteRulesAs(s with { SixKeyBoundaryMs = 25 }) && !s.HasSameNoteRulesAs(s with { SixKeyPattern = "1 2 3 4 5 6" }),
                "6K thresholds and patterns re-prepare notes");
        });

        yield return ("6K notes cycle the 6-tile pattern from the Initial Tile and switch at cycle boundaries", () =>
        {
            var generator = new NoteGenerator();
            // Trills are off: notes 25ms apart would roll over the six lanes as one (that is the trill tests' part).
            var fixedSix = new VisualizerSettings { MaximumTileCount = 6, DynamicTiming = false, SimultaneousWindowMs = 0, TrillWindowMs = 0 };
            Sequence(generator.Generate(Enumerable.Range(0, 8).Select(i => i * 25d), fixedSix).Select(n => n.Tile), 5, 4, 3, 2, 7, 6, 5, 4);
            // Downshift wait pinned to 1: this check is about the cycle boundary, not about ignoring a single slow note.
            // The boundaries are pinned to the values this case was written for (25ms is 6K, 40ms is 4K).
            var dynamic = new VisualizerSettings { MaximumTileCount = 8, SimultaneousWindowMs = 0, DownshiftNoteCount = 1, TrillWindowMs = 0,
                EightKeyBoundaryMs = 20, SixKeyBoundaryMs = 30, FourKeyBoundaryMs = 80, TwoKeyBoundaryMs = 500 };
            var fast = generator.Generate(Enumerable.Range(0, 12).Select(i => i * 25d), dynamic);
            Assert(fast.All(n => n.Mode == 6), "25ms intervals choose 6K under an 8-lane maximum: " + string.Join(" ", fast.Select(n => n.Mode)));
            // 6K (25ms) then 4K (40ms): the switch waits for the 6-note cycle to finish.
            var mixed = generator.Generate(new[] { 0d, 25, 50, 75, 100, 125, 165, 205, 245, 285, 325 }, dynamic);
            Assert(mixed.Take(6).All(n => n.Mode == 6) && mixed.Skip(6).Take(4).All(n => n.Mode == 4), "Cycle boundary: " + string.Join(" ", mixed.Select(n => n.Mode)));
        });

        yield return ("6K chords use the pattern's lane pairs; 4- and 6-lane screens never chord across pairs", () =>
        {
            var generator = new NoteGenerator();
            var six = new VisualizerSettings { MaximumTileCount = 6, DynamicTiming = false };
            var chords = generator.Generate(new[] { 0d, 1, 100, 101, 200, 201, 300, 301, 302 }, six);
            // Three pairs and one group of three: the three are one chord on three lanes side by side (user rule).
            Equal(chords.Count, 9); Equal(chords.Count(n => n.StartsChord), 5);
            AssertPairs(chords, six);
            // Six notes at once are one chord over all six lanes.
            var three = generator.Generate(new[] { 0d, 1, 2, 3, 4, 5 }, six);
            Equal(three.Count(n => n.StartsChord), 5);
            Sequence(three.Select(n => n.Tile).Order(), 2, 3, 4, 5, 6, 7);
            AssertPairs(three, six);
            // Two chords and two singles in one 6K cycle: the chords take the preferred pairs, tiles 2-3 and 4-5.
            AssertPreferred(generator.Generate(new[] { 0d, 1, 100, 101, 200, 300 }, six), six);
            // Seven at once are more than the six lanes: six of them are the chord, the seventh comes at its own time.
            var seven = generator.Generate(new[] { 0d, 1, 2, 3, 4, 5, 6 }, six);
            Equal(seven.Count(n => n.HitTimeMs == 0), 6); Equal(seven.Count(n => n.HitTimeMs == 6), 1);

            // Slow (1K/2K) sections on a 6-lane screen also keep the pairs.
            var slow = new VisualizerSettings { MaximumTileCount = 6 };
            var slowNotes = generator.Generate(new[] { 0d, 600, 601, 1200, 1800, 1801, 2400, 2700, 2701, 3000, 3300, 3301 }, slow);
            AssertPairs(slowNotes, slow);
            AssertFirstChordPreferred(slowNotes, slow);
            // Eight lanes keep chords on the left-hand pairs 1-2 and 3-4 (the default preference).
            var eightSlow = new VisualizerSettings { MaximumTileCount = 8 };
            AssertPreferred(generator.Generate(new[] { 0d, 600, 601, 1200, 1800, 1801, 2400, 2700, 2701, 3000, 3300, 3301 }, eightSlow), eightSlow);
            AssertFirstChordPreferred(generator.Generate(new[] { 0d, 45, 46, 90, 135, 136, 180, 225, 270, 271 }, eightSlow), eightSlow);
            Assert(slowNotes.Count(n => n.StartsChord) == 4, "Every chord was placed: " + string.Join(" ", slowNotes.Select(n => n.Tile)));

            // The 4-lane rule is unchanged: pairs 1-2 and 3-4 only, which is also what the default preference names there.
            var four = new VisualizerSettings();
            var fourNotes = generator.Generate(new[] { 0d, 600, 601, 1200, 1500, 1501, 1800, 1830, 1831, 1860 }, four);
            AssertPairs(fourNotes, four);
            AssertPreferred(fourNotes, four);

            // Extra lane placement applies at 6 lanes too: tiles 1 and 8 take overflowing notes.
            var sixExtra = six with { ExtraLanes = true };
            Sequence(sixExtra.GetExtraTiles(), 1, 8);
            var overflow = generator.Generate(new[] { 0d, 1, 2, 3, 4, 5, 6 }, sixExtra);
            Assert(overflow.Count(n => n.Tile is 1 or 8) == 1 && overflow.Count(n => n.StartsChord) == 3, "One spare single: " + string.Join(" ", overflow.Select(n => n.Tile)));
        });

        yield return ("Unplaceable 4-lane chords offer the smallest wider maximum that places the song", () =>
        {
            var asked = new List<string>();
            var model = new MainViewModel { ConfirmWiderLanes = message => { asked.Add(message); return true; } };
            model.SelectedMap = Entry("six", HeldChord(3));
            model.LoadSelectedSong();
            Equal(asked.Count, 1); Assert(asked[0].Contains("최대 4키") && asked[0].Contains("6개"), "Asks for 6 lanes: " + asked[0]);
            Equal(model.Settings.MaximumTileCount, 6); Equal(model.Playback.Notes.Count, 5);

            var eight = new MainViewModel { ConfirmWiderLanes = message => { asked.Add(message); return true; } };
            eight.SelectedMap = Entry("eight", HeldChord(4));
            eight.LoadSelectedSong();
            Assert(asked[^1].Contains("8개"), "Four notes beside the long note need 8 lanes: " + asked[^1]);
            Equal(eight.Settings.MaximumTileCount, 8); Equal(eight.Playback.Notes.Count, 6);

            var window = new MainWindow();
            var main = (MainViewModel)window.DataContext;
            main.ApplySettings(main.Settings with { MaximumTileCount = 6, DynamicTiming = false });
            main.Playback.LoadTimeline(new[] { 0d, 0, 300, 600, 600, 900, 1200, 1200, 1500 }, main.Settings);
            main.Toggle();
            Capture(window, Path.Combine(artifacts, "six-lanes.png"), 480, 800);
            var dialog = new SettingsPanel(main.Settings, new MemorySettingsLayoutStore());
            Capture(dialog, Path.Combine(artifacts, "settings-six-lanes.png"), 640, 2700);
            window.Close();
        });
    }
}
