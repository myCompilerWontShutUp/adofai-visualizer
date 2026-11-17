using System.IO;
using System.Windows;
using System.Windows.Controls;
using Visualizer.App.ViewModels;
using Visualizer.App.Views;
using Visualizer.Application.Playback;
using Visualizer.Application.Maps;
using Visualizer.Core.Maps;
using Visualizer.Core.Localization;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

internal static class SimultaneousNoteTests
{
    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }
    private static VisualizerSettings Settings => new()
    {
        FourKeyPattern = "1 2 3 4", TwoKeyPattern = "2 3", InitialTile = 2, EnableTilePreference = true,
        // The boundaries are pinned to the values these cases were written for (a 100ms gap is 2K).
        EightKeyBoundaryMs = 20, SixKeyBoundaryMs = 30, FourKeyBoundaryMs = 80, TwoKeyBoundaryMs = 500,
        FourKeyOrder = "1 2 3 4", TwoKeyOrder = "1 2" // Ascending cycles, as these cases were written.
    };
    /// <summary>The user's note settings when the trill reports came in (four lanes, 2K on lanes 2-3).</summary>
    private static VisualizerSettings UserSettings => new()
    {
        MaximumTileCount = 4, MinimumTileCount = 2, EightKeyBoundaryMs = 55, SixKeyBoundaryMs = 55, FourKeyBoundaryMs = 100,
        TwoKeyBoundaryMs = 500, SimultaneousWindowMs = 25, TrillWindowMs = 40, TrillCenterPair = "LastResort", TrillLanePreference = "1-2,3-4",
        TrillAsOneNote = true, ChordsOnlyAboveFourKeys = true, ImmediateDownshift = true, DownshiftNoteCount = 3, NextIntervalTiles = true,
        BothIntervalTiles = false, FourKeyPattern = "3 4 5 6", TwoKeyPattern = "4 5", EnableTilePreference = true, TwoKeyOrder = "2 1",
        FourKeyOrder = "4 3 2 1", InitialTile = 5, ExperimentalFeatures = true, EnhancedChordRules = true,
        ChordLanePreference = "None", MagicCircleFixedTiles = true, MagicCircleTileCount = 4, MagicCircleNoteCount = 40,
        MagicCircleMaxIntervalMs = 150, MagicCircleChords = false
    };

    /// <summary>A seeded run of two-note trills, singles and two-note chords at gaps from 60 to 450 ms.</summary>
    private static double[] TrillRhythm(int seed)
    {
        var random = new Random(seed);
        var times = new List<double>();
        var at = 0d;
        for (var events = 0; events < 1200; events++)
        {
            var kind = random.Next(4);
            if (kind == 0) { times.Add(at); times.Add(at + 26 + random.Next(14)); }
            else if (kind == 3 && random.Next(3) == 0) { times.Add(at); times.Add(at + 5); }
            else times.Add(at);
            at = times[^1] + new[] { 60, 80, 100, 120, 150, 200, 300, 450 }[random.Next(8)];
        }
        return times.ToArray();
    }

    private static IEnumerable<TextBlock> TextBlocks(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock text) yield return text;
            foreach (var nested in TextBlocks(child)) yield return nested;
        }
    }

    public static IEnumerable<(string Name, Action Run)> Create(string artifacts)
    {
        yield return ("Chord window defaults, validation and note-rule identity", () =>
        {
            Equal(Settings.SimultaneousWindowMs, 20d);
            foreach (var invalid in new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                Throws<ArgumentException>(() => (Settings with { SimultaneousWindowMs = invalid }).EnsureValid());
            (Settings with { SimultaneousWindowMs = 0 }).EnsureValid();
            Assert(!Settings.HasSameNoteRulesAs(Settings with { SimultaneousWindowMs = 9.5 }), "Window changes must regenerate notes");
        });
        yield return ("Inclusive anchored grouping preserves source and normalizes display only", () =>
        {
            var generator = new NoteGenerator();
            var times = new[] { 0d, 10, 20, 30 };
            var original = times.ToArray();
            // The window is pinned here so editing the initial settings does not change this rule check. Trills are off: with
            // them on, notes linked one to the next by the trill window beyond the chord window are a trill (checked below).
            var window = Settings with { SimultaneousWindowMs = 10, TrillWindowMs = 0 };
            var notes = generator.Generate(times, window);
            Assert(notes.Select(n => n.HitTimeMs).SequenceEqual(new[] { 0d, 0, 20, 20 }), "Groups must use first hit, without chaining");
            var trill = generator.Generate(times, window with { TrillWindowMs = 40 });
            Assert(trill.Select(n => n.HitTimeMs).SequenceEqual(times) && !trill.Any(n => n.StartsChord), "With trills on the run is a trill");
            Assert(times.SequenceEqual(original), "Original hit times must remain intact");
            Equal(generator.Generate(new[] { 0d, 10.001 }, window)[1].HitTimeMs, 10.001);
            Equal(generator.Generate(new[] { 0d, 0 }, Settings with { SimultaneousWindowMs = 0 })[1].HitTimeMs, 0d);
            Equal(generator.Generate(new[] { 0d, 0.01 }, Settings with { SimultaneousWindowMs = 0 })[1].HitTimeMs, 0.01);
        });
        yield return ("2K single and chord repeat a contiguous three-lane pattern", () =>
        {
            var times = new[] { 0d, 100, 110, 200, 300, 310, 400, 500, 510 };
            var generator = new NoteGenerator();
            var notes = generator.Generate(times, Settings);
            Sequence(notes.Select(n => n.Tile), 2, 3, 4, 2, 3, 4, 2, 3, 4);
            Assert(notes.All(n => n.Mode == 2), "Chord counts as one group for mode selection");
            Assert(notes.Select(n => n.HitTimeMs).SequenceEqual(new[] { 0d, 100, 100, 200, 300, 300, 400, 500, 500 }), "Chord display must align");
            Assert(notes.SequenceEqual(generator.Generate(times, Settings)), "Planning must be deterministic");
        });
        yield return ("4K plans outer adjacent chords before placing singles", () =>
        {
            // The chords stand clear of the notes around them (more than the trill window away), so they stay chords.
            var times = new[] { 0d, 45, 50, 95, 125, 170, 175, 220, 250 };
            var generator = new NoteGenerator();
            var notes = generator.Generate(times, Settings with { InitialTile = 3 });
            // 95/125 and 220/250 are trills: they roll up the ascending pattern (3-4) away from the chord before them, and the
            // chord between them keeps off the key just played.
            Sequence(notes.Select(n => n.Tile), 3, 1, 2, 3, 4, 1, 2, 3, 4);
            Assert(notes.All(n => n.Mode == 4), "Expected grouped 4K mode");
            foreach (var initial in new[] { 1, 2, 3, 4 })
            foreach (var pattern in new[] { "1 2 3 4", "4 3 2 1", "1 3 2 4" })
            {
                // Tiles are 1-4, so each written order is also its tile preference.
                var result = generator.Generate(times, Settings with { InitialTile = initial, FourKeyPattern = pattern, FourKeyOrder = pattern });
                foreach (var pair in result.GroupBy(n => n.HitTimeMs).Where(g => g.Count() == 2))
                {
                    var tiles = pair.Select(n => n.Tile).Order().ToArray();
                    Assert(tiles.SequenceEqual(new[] { 1, 2 }) || tiles.SequenceEqual(new[] { 3, 4 }), "A 4K chord must occupy an outer pair");
                }
            }
        });
        yield return ("A chord of three is drawn as one gapless bar over its three lanes", () =>
        {
            const int width = 300, height = 400;
            var session = new PlaybackSession(new Clock());
            // The chord is the play's first moment, so the start color is set to the chord color to read it as a chord.
            var plain = new VisualizerSettings();
            session.LoadTimeline(new[] { 0d, 1, 2, 2000 }, plain with { DynamicTiming = false, JudgmentLineHeightDip = 36, StartNoteColor = plain.ChordColor });
            session.Toggle();
            var field = new Visualizer.App.Rendering.NoteField { Session = session };
            field.Measure(new Size(width, height));
            field.Arrange(new Rect(0, 0, width, height));
            field.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(field);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            // Four lanes of 75px; the judgment line is at y 334. Chord green is high G and low R (B G R A bytes).
            bool Green(int x) => (334 * width + x) * 4 is var index && pixels[index + 1] > 180 && pixels[index + 2] < 120;
            var visible = plain.GetVisibleTiles().ToList();
            var lanes = session.Notes.Take(3).Select(note => visible.IndexOf(note.Tile)).Order().ToArray();
            Assert(lanes[^1] - lanes[0] == 2, "Three lanes side by side: " + string.Join(",", lanes));
            // Lane centers and the two lane borders inside the chord, but not the lane left out.
            foreach (var x in new[] { lanes[0] * 75 + 37, lanes[0] * 75 + 75, lanes[1] * 75 + 37, lanes[1] * 75 + 75, lanes[2] * 75 + 37 })
                Assert(Green(x), $"Chord color at x {x}");
            var left = Enumerable.Range(0, 4).Single(lane => !lanes.Contains(lane));
            Assert(!Green(left * 75 + 37), $"Lane {left + 1} is empty");
        });

        yield return ("A trill counted as one note: its tile count from the gaps around it, its lanes in the pattern's flow", () =>
        {
            // User rule (default on): a trill counts as one note when the tile count is worked out. The rhythm from Wont Let
            // You Go, a trill (29ms) and a single in turn, asked for 4K note by note; as one note the gaps are 172 and 171ms.
            // Its lanes are laid by the trill rules either way (user rule: laid like a chord, a machine-gun run lost 4 3 2 1).
            var settings = new VisualizerSettings { MaximumTileCount = 4, SimultaneousWindowMs = 20, TrillWindowMs = 40, FourKeyBoundaryMs = 120,
                TwoKeyBoundaryMs = 500, MagicCircleFixedTiles = false, TrillCenterPair = "LastResort" };
            Assert(new VisualizerSettings().TrillAsOneNote, "On by default (user rule)");
            Assert(!settings.HasSameNoteRulesAs(settings with { TrillAsOneNote = false }), "It is a note rule");
            var visible = settings.GetVisibleTiles().ToList();
            var generator = new NoteGenerator();
            var times = new List<double>();
            for (var at = 1000d; times.Count < 30; at += 343) times.AddRange([at, at + 29, at + 172]);
            var one = generator.Generate(times.ToArray(), settings);
            var each = generator.Generate(times.ToArray(), settings with { TrillAsOneNote = false });
            Assert(each.Any(note => note.Mode == 4) && one.All(note => note.Mode <= 2),
                $"The tile count: {string.Join("", each.Select(n => n.Mode))} note by note, {string.Join("", one.Select(n => n.Mode))} as one");
            // Each note keeps its own time and none is drawn as a chord.
            Assert(one.Select(note => note.HitTimeMs).SequenceEqual(times) && one.All(note => !note.StartsChord), "Own times, no chord marks");
            string Text(IReadOnlyList<VisualizerNote> notes) => string.Join(" ", notes.Select(note => visible.IndexOf(note.Tile) + 1));
            // Lanes side by side, rolling the pattern's way (right to left) unless at the far edge, and no key twice in a row.
            void Check(IReadOnlyList<VisualizerNote> notes, IReadOnlyList<double> at)
            {
                for (var i = 1; i < notes.Count; i++)
                {
                    var (lane, last) = (visible.IndexOf(notes[i].Tile), visible.IndexOf(notes[i - 1].Tile));
                    if (at[i] - at[i - 1] <= settings.TrillWindowMs)
                        Assert(lane - last == -1 || (lane - last == 1 && last == 0), $"A trill rolls on at {at[i]}: {Text(notes)}");
                    else Assert(lane != last, $"No key twice in a row at {at[i]}: {Text(notes)}");
                }
            }
            Check(one, times);
            // A 25ms stream is no trill to count as one note: it walks the pattern at its own pace (4K, 4 3 2 1 going round).
            var stream = Enumerable.Range(0, 16).Select(i => 2000 + i * 25d).ToArray();
            var rolled = generator.Generate(stream, settings).Select(note => visible.IndexOf(note.Tile) + 1).ToArray();
            Assert(rolled.Zip(rolled.Skip(1)).All(pair => pair.Second == pair.First - 1 || (pair.First == 1 && pair.Second == 4))
                && generator.Generate(stream, settings).All(note => note.Mode == 4), "A stream walks the pattern: " + string.Join(" ", rolled));
            // Trills in a row take turns on the pairs, as chords do (user rule: not 1-2 1-2), also across the chord pass's blocks.
            // A single first, as in Wont Let You Go (tile 604), so the blocks of two start with the single and then on a trill
            // right after another.
            var pairs = new List<double> { 0 };
            for (var at = 343d; pairs.Count < 25; at += 172) pairs.AddRange([at, at + 29]);
            var turns = generator.Generate(pairs.ToArray(), settings);
            for (var i = 3; i + 1 < turns.Count; i += 2)
                Assert(!new[] { turns[i].Tile, turns[i + 1].Tile }.Order().SequenceEqual(new[] { turns[i - 2].Tile, turns[i - 1].Tile }.Order()),
                    $"Two trills in a row on one pair at {pairs[i]}: {Text(turns)}");
            // Trills among singles and chords at 4K pace keep the same rules.
            var mixed = new List<double>();
            for (var at = 0d; mixed.Count < 60; at += 400) mixed.AddRange([at, at + 25, at + 130, at + 131, at + 260]);
            var placed = generator.Generate(mixed.ToArray(), settings);
            Equal(placed.Count, mixed.Count);
            // A screen of one lane has nowhere to roll: the notes stay where they are, and nothing fails.
            Equal(generator.Generate(new[] { 0d, 600, 625, 1200 }, settings with { MaximumTileCount = 1, MinimumTileCount = 1 }).Count, 4);
        });

        yield return ("A fast run over every lane rolls the pattern's way, 4 3 2 1, never 1 2 3 4", () =>
        {
            // User report (a machine-gun stretch used to read 4 3 2 1 4 3 2 1): notes 36 ms apart are rolls over all four lanes,
            // and one rolled back from lane 1 kept every roll after it going back (He He He, Hello (BPM) 2021).
            var settings = new VisualizerSettings { MaximumTileCount = 4, MinimumTileCount = 2, FourKeyPattern = "3 4 5 6", TwoKeyPattern = "4 5",
                InitialTile = 5, SimultaneousWindowMs = 25, TrillWindowMs = 40, FourKeyBoundaryMs = 100, TwoKeyBoundaryMs = 500,
                EightKeyBoundaryMs = 50, SixKeyBoundaryMs = 50, NextIntervalTiles = true, BothIntervalTiles = true, MagicCircleFixedTiles = false };
            var visible = settings.GetVisibleTiles().ToList();
            var checkedChords = new HashSet<int>();
            // The run comes right after a chord, as in both songs; what leads into the chord decides its lanes, so a few
            // lead-ins make sure a chord on 3-4 is among them (it blocks lane 4, where the pattern's roll starts).
            foreach (var lead in new[] { 0, 1, 2, 3 })
            foreach (var one in new[] { true, false })
            {
                var times = Enumerable.Range(0, lead).Select(i => i * 188d).ToList();
                var chordAt = lead * 188d + 300;
                times.AddRange([chordAt, chordAt + 1]);
                for (var at = chordAt + 36; times.Count < lead + 2 + 32; at += 36) times.Add(at);
                var notes = new NoteGenerator().Generate(times.ToArray(), settings with { TrillAsOneNote = one });
                checkedChords.Add(visible.IndexOf(notes[lead].Tile) + 1);
                var lanes = notes.Skip(lead + 2).Select(note => visible.IndexOf(note.Tile) + 1).ToArray();
                var walking = lanes.Zip(lanes.Skip(1)).Count(pair => pair.Second == pair.First - 1 || (pair.First == 1 && pair.Second == 4));
                Assert(walking >= lanes.Length - 2, $"Walks 4 3 2 1 (lead {lead}, trill as one note {one}): " + string.Join(" ", lanes));
            }
            Assert(checkedChords.Contains(3), "A chord on 3-4 came before a run: " + string.Join(",", checkedChords));
        });

        yield return ("Trills counted as one note never fail a song, lose a note or put a trill's notes on one key", () =>
        {
            // What the hand-written cases cannot cover: random runs of singles, trills and chords over every lane count and
            // the switches that change the chord pass. Trills are not a placement condition (user rule), so wherever the
            // song places with trills counted note by note it places with them counted as one.
            var generator = new NoteGenerator();
            var gaps = new[] { 12d, 25, 30, 38, 45, 60, 90, 130, 170, 250, 400 };
            var checkedSongs = 0;
            foreach (var lanes in new[] { 1, 2, 4, 6, 8 })
            foreach (var enhanced in new[] { false, true })
            foreach (var extra in new[] { false, true })
            foreach (var center in VisualizerSettings.TrillCenterChoices)
            for (var seed = 1; seed <= 6; seed++)
            {
                var random = new Random(seed * 31 + lanes);
                var times = new List<double>();
                for (var at = 0d; times.Count < 120; at += gaps[random.Next(gaps.Length)])
                {
                    times.Add(at);
                    if (random.Next(6) == 0) times.Add(at + 1);
                }
                var settings = new VisualizerSettings
                {
                    MaximumTileCount = lanes, MinimumTileCount = 1, SimultaneousWindowMs = 20, TrillWindowMs = 40, ExperimentalFeatures = enhanced,
                    EnhancedChordRules = enhanced, ExtraLanes = extra, TrillCenterPair = center, MagicCircleFixedTiles = false
                };
                if (lanes == 1) settings = settings with { InitialTile = settings.GetPattern(1)[0] };
                IReadOnlyList<VisualizerNote> each;
                try { each = generator.Generate(times.ToArray(), settings with { TrillAsOneNote = false }); }
                catch (NotePlacementException) { continue; }
                var notes = generator.Generate(times.ToArray(), settings);
                var what = $"{lanes} lanes, enhanced {enhanced}, extra {extra}, center {center}, seed {seed}";
                Equal(notes.Count, times.Count);
                // A trill's notes keep their own times, exactly as they do counted note by note.
                Assert(notes.Select(note => note.HitTimeMs).SequenceEqual(each.Select(note => note.HitTimeMs)), "The same times: " + what);
                var chords = Enumerable.Range(0, notes.Count).Where(i => notes[i].StartsChord || (i > 0 && notes[i - 1].StartsChord)).ToHashSet();
                for (var i = 1; lanes > 1 && i < notes.Count; i++)
                    if (!chords.Contains(i) && !chords.Contains(i - 1) && notes[i].HitTimeMs - notes[i - 1].HitTimeMs <= settings.TrillWindowMs)
                        Assert(notes[i].Tile != notes[i - 1].Tile, $"A trill's notes on one key at {notes[i].HitTimeMs}: {what}");
                checkedSongs++;
            }
            Assert(checkedSongs > 100, "Enough of them placed to say something: " + checkedSongs);
        });

        yield return ("Notes keep one hand's flow: no key twice in a row, singles beside chords, trills roll with the pattern", () =>
        {
            // User report (Plum - Megamix): 1 2 3 2 1 zigzags, trills against the 4-3-2-1 pattern (1-2 instead of 2-1), the same
            // key twice in a row (3 3 2 3 2, 3-4 3-4) and singles away from the chord (1 3-4 1 3-4 instead of 2 3-4 2 3-4).
            var settings = new VisualizerSettings
            {
                SimultaneousWindowMs = 20, TrillWindowMs = 40, MaximumTileCount = 4, InitialTile = 4, EnableTilePreference = false,
                EightKeyBoundaryMs = 50, SixKeyBoundaryMs = 70, FourKeyBoundaryMs = 120, TwoKeyBoundaryMs = 500,
                ChordLanePreference = "1-2,3-4", TrillCenterPair = "LastResort", MagicCircleFixedTiles = false,
                // The trill rules below are the ones for trills counted note by note; the mixed run at the end checks both.
                TrillAsOneNote = false
            };
            var visible = settings.GetVisibleTiles().ToList();
            var generator = new NoteGenerator();
            // Events as screen lanes from the left (1-based): one lane for a single, two for a chord.
            List<(double Time, int[] Lanes)> Events(IReadOnlyList<VisualizerNote> notes)
            {
                var events = new List<(double, int[])>();
                for (var i = 0; i < notes.Count; i++)
                {
                    // A chord is every note marked in a row and the one after the last.
                    var first = i;
                    while (notes[i].StartsChord && i + 1 < notes.Count) i++;
                    var lanes = notes.Skip(first).Take(i - first + 1).ToArray();
                    events.Add((notes[i].HitTimeMs, lanes.Select(note => visible.IndexOf(note.Tile) + 1).Order().ToArray()));
                }
                return events;
            }
            string Text(List<(double Time, int[] Lanes)> events) => string.Join(" ", events.Select(e => string.Join("-", e.Lanes)));
            void NoRepeat(List<(double Time, int[] Lanes)> events)
            {
                for (var i = 1; i < events.Count; i++)
                    Assert(events[i].Time - events[i - 1].Time >= 500 || !events[i].Lanes.Intersect(events[i - 1].Lanes).Any(),
                        $"No key twice in a row at {events[i].Time}: {Text(events)}");
            }
            IEnumerable<double> Chord(double time) => [time, time + 1];

            // Singles and chords taking turns, on 4K (100ms) and 2K (167ms) pace: the single sits beside the chord.
            foreach (var gap in new[] { 100d, 167 })
            {
                var times = Enumerable.Range(0, 12).SelectMany(i => i % 2 == 1 ? Chord(i * gap) : [i * gap]).ToArray();
                var events = Events(generator.Generate(times, settings));
                NoRepeat(events);
                for (var i = 1; i < events.Count; i++)
                    if (events[i].Lanes.Length == 1 && events[i - 1].Lanes.Length == 2)
                        Assert(events[i - 1].Lanes.Any(lane => Math.Abs(lane - events[i].Lanes[0]) == 1),
                            $"A single beside the chord before it ({gap}ms): {Text(events)}");
            }

            // Chords alone alternate their pairs.
            NoRepeat(Events(generator.Generate(Enumerable.Range(0, 8).SelectMany(i => Chord(i * 100d)).ToArray(), settings)));

            // A 25ms stream rolls the descending pattern in pairs: 4-3, 2-1, never 1-2 or across the center.
            var stream = Events(generator.Generate(Enumerable.Range(0, 16).Select(i => 1000 + i * 25d).ToArray(), settings));
            NoRepeat(stream);
            for (var i = 0; i + 1 < stream.Count; i += 2)
                Assert(stream[i].Lanes[0] - stream[i + 1].Lanes[0] == 1 && stream[i + 1].Lanes[0] != 2,
                    "Trills run with the pattern on an outer pair: " + Text(stream));

            // A trill inside a 2K run: the 2K lanes are the center pair, which four lanes refuse, so it moves off them and
            // still rolls right to left, clear of the notes around it.
            var twoK = new[] { 0d, 300, 600, 900, 930, 1200, 1500, 1800 };
            var mixed = Events(generator.Generate(twoK, settings));
            NoRepeat(mixed);
            Assert(mixed[3].Lanes[0] - mixed[4].Lanes[0] == 1 && mixed[4].Lanes[0] != 2, "A 2K trill rolls on an outer pair: " + Text(mixed));

            // Trills among singles at 2K and 4K pace (user rules): every trill rolls right to left, the center pair only
            // where it carries the run on from the note before it (4 3-2), two trills in a row take two pairs (not 1-2 1-2),
            // and the single after a trill sits beside it on the side the roll ran to (4-3 2); after a roll into the edge
            // (2-1) the pattern simply goes on, so no lane drops out of the cycle (4 3-2 1 4, not 2-1 3).
            var units = "TTSTSSTTTSTSTTSS";
            var paces = new[] { 300d, 220, 100, 160 };
            var flowTimes = new List<double>();
            var at = 5000d;
            for (var i = 0; i < units.Length * 3; i++, at += paces[i % paces.Length])
                flowTimes.AddRange(units[i % units.Length] == 'T' ? [at, at + 25] : [at]);
            var flow = Events(generator.Generate(flowTimes.ToArray(), settings));
            NoRepeat(flow);
            int[]? lastTrill = null;
            for (var i = 0; i < flow.Count; i++)
            {
                var trill = i + 1 < flow.Count && flow[i + 1].Time - flow[i].Time <= 40;
                if (trill)
                {
                    var (first, second) = (flow[i].Lanes[0], flow[i + 1].Lanes[0]);
                    Assert(first - second == 1, $"A trill rolls right to left at {flow[i].Time}: " + Text(flow));
                    Assert(second != 2 || (i > 0 && flow[i - 1].Lanes[0] - first == 1),
                        $"A trill on the center pair carries the run on at {flow[i].Time}: " + Text(flow));
                    Assert(lastTrill is null || lastTrill.Min() != second, $"Two trills in a row take two pairs at {flow[i].Time}: " + Text(flow));
                    lastTrill = [first, second];
                    i++;
                    continue;
                }
                if (lastTrill is { } pair)
                    Assert(pair[1] > 1 ? flow[i].Lanes[0] == pair[1] - 1 : !pair.Contains(flow[i].Lanes[0]),
                        $"The single after a trill sits beside it on the side the roll ran to at {flow[i].Time}: " + Text(flow));
                lastTrill = null;
            }

            // 1K to 2K: the 2K pattern does not open on the 1K lane.
            NoRepeat(Events(generator.Generate(new[] { 0d, 600, 1200, 1500, 1800, 2100 }, settings with { InitialTile = 4 })));

            // A mixed run, the same every time: no key twice in a row anywhere, with or without the trill rule.
            var random = new Random(7);
            var gaps = new[] { 25d, 60, 90, 130, 200, 300, 600 };
            var mixedTimes = new List<double>();
            for (double time = 0; mixedTimes.Count < 600; time += gaps[random.Next(gaps.Length)])
                mixedTimes.AddRange(random.Next(4) == 0 ? Chord(time) : [time]);
            foreach (var trills in new[] { 40d, 0 })
                NoRepeat(Events(generator.Generate(mixedTimes, settings with { TrillWindowMs = trills })));
            NoRepeat(Events(generator.Generate(mixedTimes, settings with { TrillAsOneNote = true })));
        });
        yield return ("Difficult patterns from the user's charts are gone: quick returns, a lane after a chord, rolls at the edge", () =>
        {
            // User report (memo "난해한 패턴들", Wont Let You Go and PLUM MEGAMIX 2), on the user's own layout: tiles 3 4 5 6 played
            // 6 to 3, so lanes 4 to 1, 2K on the center pair. A lane that comes back within twice the 4K boundary while another
            // lane sat free is hard to play; so is a single after a chord that is not beside it, and a trill that skips a lane.
            var generator = new NoteGenerator();
            var settings = new VisualizerSettings { FourKeyPattern = "3 4 5 6", TwoKeyPattern = "4 5", MaximumTileCount = 4, InitialTile = 5,
                SimultaneousWindowMs = 20, TrillWindowMs = 40, TrillCenterPair = "LastResort", MagicCircleFixedTiles = false, NextIntervalTiles = true,
                FourKeyBoundaryMs = 100, TwoKeyBoundaryMs = 500, EightKeyBoundaryMs = 29, SixKeyBoundaryMs = 30, DownshiftNoteCount = 3 };
            var quick = 2 * settings.FourKeyBoundaryMs;
            (double Time, int[] Lanes)[] Events(IReadOnlyList<VisualizerNote> notes) => notes
                .GroupBy(note => note.HitTimeMs).Select(group => (group.Key, group.Select(note => note.Tile - 2).Order().ToArray())).ToArray();
            string Show((double Time, int[] Lanes)[] events) => string.Join(" ", events.Select(e => string.Join("-", e.Lanes)));
            // No single comes back to a lane within the quick window while another lane has rested longer, with at most four
            // notes in that window (five on four lanes have to repeat one).
            void NoQuickReturn((double Time, int[] Lanes)[] events, string what)
            {
                for (var i = 1; i < events.Length; i++)
                {
                    if (events[i].Lanes.Length != 1) continue;
                    var lane = events[i].Lanes[0];
                    var last = Array.FindLastIndex(events, i - 1, i, e => e.Lanes.Contains(lane));
                    if (last < 0 || events[i].Time - events[last].Time >= quick) continue;
                    if (Enumerable.Range(last, i - last).Any(j => events[j].Lanes.Length != 1)) continue;
                    if (events.Count(e => e.Time > events[i].Time - quick && e.Time <= events[i].Time) > 4) continue;
                    var rested = Enumerable.Range(1, 4).Any(other => other != lane
                        && Array.FindLastIndex(events, i - 1, i, e => e.Lanes.Contains(other)) is var at && (at < 0 || events[at].Time < events[last].Time));
                    Assert(!rested, $"{what}: lane {lane} comes back after {events[i].Time - events[last].Time:0} ms with another lane free: {Show(events)}");
                }
            }

            // 2K that suddenly speeds up (Wont Let You Go, 37 s): 2 3 2 3 at 43 ms left lanes 1 and 4 empty. The gaps around a note
            // add up to less than twice the 4K boundary, so 4K starts on that note instead of at the end of the 2K cycle.
            var burst = new[] { 0d, 171, 342, 513, 599, 642, 685, 856, 1027 };
            NoQuickReturn(Events(generator.Generate(burst, settings)), "2K burst");
            // Both gaps: a note between a fast gap and a slow one is as busy as the fast side, wherever it is. At the end of the
            // 2K cycle the note after the 50 ms gap asks for 4K that way, where the gap after it alone keeps 2K.
            var gaps = new[] { 0d, 300, 350, 650, 950 };
            Equal(generator.Generate(gaps, settings with { BothIntervalTiles = true })[2].Mode, 4);
            Equal(generator.Generate(gaps, settings)[2].Mode, 2);

            // Restarting the pattern on a run (experimental) never brings the lane just played back that soon (3 2 | 4 3 2).
            var runs = settings with { ExperimentalFeatures = true, RestartPatternOnRun = true };
            var stream = new[] { 0d, 94, 188, 282, 376, 470, 564, 658, 752, 846, 940, 1034, 1128, 1222, 1410, 1504, 1598, 1692, 1786 };
            NoQuickReturn(Events(generator.Generate(stream, runs)), "run restart");

            // A single after a chord sits beside it, even where the run after the chords starts the pattern again (1-2 4 read
            // 1-2 3 in Wont Let You Go, 60 s).
            var afterChord = new[] { 0d, 5, 171, 176, 342, 347, 513, 518, 684, 770, 856, 942, 1028 };
            var placed = Events(generator.Generate(afterChord, runs));
            var chordAt = Array.FindLastIndex(placed, e => e.Lanes.Length == 2);
            Assert(placed[chordAt + 1].Lanes[0] is var next && placed[chordAt].Lanes.Any(lane => Math.Abs(lane - next) == 1),
                "The single after the chord sits beside it: " + Show(placed));

            // Trills about 190 ms apart take turns (4-3 2-1 4-3), never coming back to the pair just played (MEGAMIX 2, 122 s).
            var trills = new[] { 0d, 31, 187, 218, 374, 405, 561, 592, 748, 779 };
            var rolled = Events(generator.Generate(trills, settings));
            NoQuickReturn(rolled, "trills in a row");

            // At the far edge a roll goes back the other way (user rule) instead of jumping back up: 4 3 2 1 then 2-3, not 4-3.
            var edge = Events(generator.Generate(new[] { 0d, 56, 112, 168, 224, 252, 280 }, settings));
            Assert(!(edge[3].Lanes[0] == 1 && edge[4].Lanes[0] == 4), "No jump back up after the edge: " + Show(edge));
        });

        yield return ("Enhanced chord rules keep a chord in place among singles, by how many singles there are", () =>
        {
            // User rule (ネコーン - 俳徹句): chords and singles taking turns, the chord jumping between 1-2 and 3-4. Experimental and
            // off by default: with it, every chord keeps the first one's lanes and the singles sit beside it.
            var generator = new NoteGenerator();
            var settings = new VisualizerSettings { FourKeyPattern = "3 4 5 6", TwoKeyPattern = "4 5", MaximumTileCount = 4, InitialTile = 5,
                SimultaneousWindowMs = 20, TrillWindowMs = 40, MagicCircleFixedTiles = false, NextIntervalTiles = true,
                FourKeyBoundaryMs = 100, TwoKeyBoundaryMs = 500, EightKeyBoundaryMs = 29, SixKeyBoundaryMs = 30,
                ExperimentalFeatures = true, EnhancedChordRules = true };
            (double Time, int[] Lanes)[] Events(IReadOnlyList<VisualizerNote> notes) => notes
                .GroupBy(note => note.HitTimeMs).Select(group => (group.Key, group.Select(note => note.Tile - 2).Order().ToArray())).ToArray();
            string Show((double Time, int[] Lanes)[] events) => string.Join(" ", events.Select(e => string.Join("-", e.Lanes)));
            // A chord every (singles + 1) steps of 102 ms, singles between.
            double[] Times(int singles, int chords, double step)
            {
                var list = new List<double>();
                var t = 0d;
                for (var c = 0; c < chords; c++)
                {
                    list.Add(t); list.Add(t + 5); t += step;
                    for (var s = 0; s < singles; s++) { list.Add(t); t += step; }
                }
                list.Add(t); list.Add(t + 5);
                return list.ToArray();
            }
            foreach (var singles in new[] { 1, 2, 3 })
            {
                var events = Events(generator.Generate(Times(singles, 6, 102), settings));
                var chords = events.Where(e => e.Lanes.Length == 2).Select(e => string.Join("-", e.Lanes)).Distinct().ToArray();
                Equal(chords.Length, 1);
                var pair = events.First(e => e.Lanes.Length == 2).Lanes;
                Assert(events.Where(e => e.Lanes.Length == 1).All(e => !pair.Contains(e.Lanes[0])), $"{singles} singles keep off the chord: {Show(events)}");
                // The single right after each chord is beside it.
                for (var i = 0; i + 1 < events.Length; i++)
                    if (events[i].Lanes.Length == 2 && events[i + 1].Lanes.Length == 1)
                        Assert(pair.Any(lane => Math.Abs(lane - events[i + 1].Lanes[0]) == 1), $"Beside the chord: {Show(events)}");
            }
            // Three singles faster than the 4K boundary alternate: 1-2 3 4 3 1-2 4 3 4.
            var fast = Events(generator.Generate(Times(3, 4, 85), settings));
            var groups = new List<string>();
            for (var i = 0; i + 3 < fast.Length; i++)
                if (fast[i].Lanes.Length == 2 && fast[i + 1].Lanes.Length == 1 && fast[i + 2].Lanes.Length == 1 && fast[i + 3].Lanes.Length == 1)
                    groups.Add($"{fast[i + 1].Lanes[0]}{fast[i + 2].Lanes[0]}{fast[i + 3].Lanes[0]}");
            Assert(groups.Count >= 2 && groups[0] != groups[1] && groups.All(g => g[0] == g[2]), "Fast threes alternate: " + Show(fast));
            // Off (the default), nothing of this applies: the chord is placed by the usual rules.
            Assert(!new VisualizerSettings().UseEnhancedChordRules, "Off by default");
        });

        yield return ("Trills keep the run going: the center pair when it must, the edge, and a single that leads a trill", () =>
        {
            // User rules, on the user's own layout (tiles 3 4 5 6, played 6 to 3, lanes 4 to 1 below): a trill may take the
            // center pair, but only where every other pair breaks the run; after a roll into the edge the pattern goes on, so
            // no lane drops out of the cycle; and a single that a trill follows closely leads the roll (4 3-2, not 2 4-3).
            var generator = new NoteGenerator();
            var settings = new VisualizerSettings { FourKeyPattern = "3 4 5 6", MaximumTileCount = 4, DynamicTiming = false,
                SimultaneousWindowMs = 0, TrillWindowMs = 40, TrillCenterPair = "LastResort", MagicCircleFixedTiles = false, InitialTile = 6,
                TrillAsOneNote = false };
            int[] Lanes(double[] times, VisualizerSettings? other = null) =>
                generator.Generate(times, other ?? settings).Select(note => note.Tile - 2).ToArray();
            // A single with a trill close after it heads a roll off the center first (user rule, in this order: 3 2-1, then
            // 4 3-2 on the center only where no tile heads such a roll, and 4 2-1 never).
            Sequence(Lanes([0, 80, 110, 220]), 3, 2, 1, 4);
            // A roll into the edge, 2-1, and the single after it goes on to 4 (not back to 3: 4 3 2-1 3 left 4 out).
            Sequence(Lanes([0, 100, 160, 190, 300]), 4, 3, 2, 1, 4);
            // A single just before a trill heads the roll: 4 3-2 rather than 2 4-3.
            Sequence(Lanes([0, 300, 400, 460, 490]), 4, 3, 4, 3, 2);
            // With no run before it a trill keeps off the center pair, as before: starting on 3, it rolls 2-1.
            Sequence(Lanes([0, 30, 1000, 1030], settings with { InitialTile = 5 }), 2, 1, 4, 3);
        });

        yield return ("A chord of three or four is one chord on lanes side by side; two keep an outer pair", () =>
        {
            var generator = new NoteGenerator();
            // User rule: every note of a group within the chord window of the first (start and end both in it) is one chord,
            // as wide as it has notes while the screen has the lanes: on four lanes 1-2-3 or 2-3-4 for three, 1-2-3-4 for four.
            // The center rule is the two-note chord's own, so only a pair keeps an outer pair.
            void Check(IReadOnlyList<VisualizerNote> notes, VisualizerSettings settings, double[] times)
            {
                Equal(notes.Count, times.Length);
                var visible = settings.GetVisibleTiles().ToList();
                foreach (var group in notes.Select((note, index) => (note, index)).GroupBy(entry => entry.note.HitTimeMs))
                {
                    var members = group.ToArray();
                    Equal(members.Select(entry => entry.note.Tile).Distinct().Count(), members.Length);
                    if (members.Length == 1) continue;
                    // One chord: every member but the last carries the mark, so the screen draws them as one.
                    Assert(members.SkipLast(1).All(entry => entry.note.StartsChord) && !members[^1].note.StartsChord,
                        "One chord, marked through: " + string.Join(" ", notes.Select(n => n.Tile + (n.StartsChord ? "-" : " "))));
                    var lanes = members.Select(entry => visible.IndexOf(entry.note.Tile)).Order().ToArray();
                    Assert(lanes[^1] - lanes[0] == lanes.Length - 1, "Lanes side by side: " + string.Join(",", lanes));
                    if (members.Length == 2 && members[0].note.Mode == 4)
                    {
                        var pair = members.Select(entry => entry.note.Tile).Order().ToArray();
                        var active = settings.GetPattern(4).Order().ToArray();
                        Assert(pair.SequenceEqual(active[..2]) || pair.SequenceEqual(active[2..]), "A 4K two-note chord must occupy an outer pair");
                    }
                }
            }
            foreach (var initial in new[] { 1, 2, 3, 4 })
            foreach (var pattern in new[] { "1 2 3 4", "4 3 2 1", "1 3 2 4" })
            {
                var settings = Settings with { InitialTile = initial, FourKeyPattern = pattern, FourKeyOrder = pattern };
                foreach (var times in new[]
                {
                    new[] { 0d, 1, 2 }, new[] { 0d, 1, 2, 3 }, new[] { 0d, 600, 601, 602, 1200 },
                    new[] { 0d, 300, 301, 302, 600, 900, 901, 902, 903, 1200 },
                    new[] { 0d, 30, 60, 61, 62, 90, 120, 121, 122, 123, 150, 180 }
                })
                    Check(generator.Generate(times, settings), settings, times);
            }
            var three = generator.Generate(new[] { 0d, 1, 2 }, Settings);
            Assert(three.All(n => n.HitTimeMs == 0), "Display times align to the first hit");
            Assert(three[0].StartsChord && three[1].StartsChord && !three[2].StartsChord, "Three notes are one chord");
            var four = generator.Generate(new[] { 0d, 1, 2, 3 }, Settings with { FourKeyPattern = "1 2 3 4" });
            Assert(four.Select(n => n.Tile).Order().SequenceEqual(new[] { 1, 2, 3, 4 }) && four.SkipLast(1).All(n => n.StartsChord), "Four on four lanes");
            // A chord needs start and end in the chord window, a trill only each note in the trill window of the one before
            // (user rule): a b c with a-b and b-c close but a-c not is a three-note trill, whatever the gaps, rolled on three
            // lanes side by side, never a-b chord and c.
            foreach (var abc in new[] { new[] { 0d, 15, 30 }, [0, 5, 30], [0, 18, 39] })
            {
                var chain = generator.Generate(abc, Settings with { SimultaneousWindowMs = 20 });
                Assert(!chain.Any(n => n.StartsChord) && chain.Select(n => n.HitTimeMs).SequenceEqual(abc), "a b c is a trill: " + string.Join(" ", abc));
                var steps = chain.Zip(chain.Skip(1), (a, b) => b.Tile - a.Tile).ToArray();
                Assert(steps.All(step => step == steps[0]) && Math.Abs(steps[0]) == 1, "Rolled side by side: " + string.Join(" ", chain.Select(n => n.Tile)));
            }
            // Notes of one instant still sound together inside such a run, and with trills off a-b is the chord it was.
            var instant = generator.Generate(new[] { 0d, 0, 30 }, Settings with { SimultaneousWindowMs = 20 });
            Assert(instant[0].StartsChord && !instant[1].StartsChord && instant[2].HitTimeMs == 30, "One instant stays a chord");
            var off = generator.Generate(new[] { 0d, 15, 30 }, Settings with { SimultaneousWindowMs = 20, TrillWindowMs = 0 });
            Assert(off[0].StartsChord && !off[1].StartsChord && off[2].HitTimeMs == 30, "Without trills a-b is a chord, c apart");
        });
        yield return ("With four lanes, two-note chords never use the middle lanes 2-3 in any mode", () =>
        {
            var generator = new NoteGenerator();
            var defaults = new VisualizerSettings(); // Visible tiles 3 4 5 6; lanes 2-3 are tiles 4 and 5.
            foreach (var settings in new[] { defaults, defaults with { DynamicTiming = false }, Settings, Settings with { InitialTile = 3 } })
            foreach (var times in new[]
            {
                new[] { 0d, 1000, 1005, 2000 },                          // 1K chord
                new[] { 0d, 100, 200, 205, 300, 400, 405, 500 },         // 2K singles and chords
                new[] { 0d, 100, 101, 102, 200, 300, 301, 302, 303 },    // 2K split groups
                new[] { 0d, 600, 601, 602, 1200, 1800, 1801, 1802, 1803 } // 1K split groups
            })
            {
                var notes = generator.Generate(times, settings);
                var visible = settings.GetVisibleTiles().ToList();
                // A chord of three or more crosses the center by nature; the rule is the two-note chord's (user rule).
                foreach (var group in notes.GroupBy(n => n.HitTimeMs).Select(g => g.ToArray()).Where(g => g.Length == 2))
                    for (var m = 0; m + 1 < group.Length; m += 2)
                    {
                        var lanes = new[] { visible.IndexOf(group[m].Tile), visible.IndexOf(group[m + 1].Tile) }.Order().ToArray();
                        Assert(lanes.SequenceEqual(new[] { 0, 1 }) || lanes.SequenceEqual(new[] { 2, 3 }),
                            $"Chord on lanes {lanes[0] + 1}-{lanes[1] + 1} (mode {group[m].Mode}): " + string.Join(" ", notes.Select(n => n.Tile)));
                    }
            }
        });
        yield return ("Experimental chord lanes: singles after a chord avoid its lanes, and only while switched on", () =>
        {
            // User rule (from Raimukun - C†hulhu): the singles right after a chord keep its lanes free, so the
            // shape is 1-2 3 4 3 instead of 1-2 3 2 3. Chords keep their own placement and notes before a chord are untouched.
            var generator = new NoteGenerator();
            var defaults = new VisualizerSettings(); // Visible tiles 3 4 5 6.
            var times = new List<double>();
            for (var cycle = 0; cycle < 6; cycle++)
            {
                var start = cycle * 320d; // 80ms apart: a 2K section, as in the reported song.
                times.Add(start);
                times.Add(start + 1); // Within the chord window: the pair of a two-note chord.
                for (var single = 1; single <= 3; single++) times.Add(start + single * 80);
            }
            var off = generator.Generate(times, defaults);
            // An advanced setting: it applies without the experimental switch, and the switch alone changes nothing.
            var on = generator.Generate(times, defaults with { ChordSectionLanes = true });
            Sequence(generator.Generate(times, defaults with { ExperimentalFeatures = true }).Select(note => note.Tile), off.Select(note => note.Tile).ToArray());
            Assert(!on.Select(note => note.Tile).SequenceEqual(off.Select(note => note.Tile)), "The switch changes the lanes");

            int[]? recent = null;
            var since = 0;
            for (var i = 0; i < on.Count; i++)
            {
                if (on[i].StartsChord)
                {
                    recent = [on[i].Tile, on[i + 1].Tile];
                    since = 0;
                    i++;
                    continue;
                }
                since++;
                if (recent is not null && since <= 3)
                    Assert(Array.IndexOf(recent, on[i].Tile) < 0,
                        $"A single took the lane of the chord before it: " + string.Join(" ", on.Select(note => note.Tile)));
                if (i > 0 && on[i].HitTimeMs != on[i - 1].HitTimeMs)
                    Assert(on[i].Tile != on[i - 1].Tile, "A lane never follows itself: " + string.Join(" ", on.Select(note => note.Tile)));
            }
            // Notes of one group never share a lane, even where two chords sound together.
            var wide = generator.Generate(new[] { 0d, 1, 2, 3, 80, 160, 240, 320, 321, 322, 323 },
                defaults with { ChordSectionLanes = true });
            foreach (var group in wide.GroupBy(note => note.HitTimeMs).Select(g => g.ToArray()))
                Equal(group.Select(note => note.Tile).Distinct().Count(), group.Length);
        });
        yield return ("Above four keys only for chords: the gaps pick 4K at most, and a chord of five or more widens for itself", () =>
        {
            // User rule: on eight lanes fast singles stay in four keys, and only a chord that four lanes cannot hold takes the
            // lanes it needs, for that chord alone; chords of two to four notes never ask for them.
            var generator = new NoteGenerator();
            var eight = new VisualizerSettings { MaximumTileCount = 8, MinimumTileCount = 1, EightKeyBoundaryMs = 50, SixKeyBoundaryMs = 70,
                FourKeyBoundaryMs = 120, TwoKeyBoundaryMs = 500, SimultaneousWindowMs = 20, TrillWindowMs = 0, ChordsOnlyAboveFourKeys = true };
            var times = Enumerable.Range(0, 16).Select(i => i * 30d)
                .Concat([600, 601, 602, 603, 604]).Concat([700, 701, 702])
                .Concat(Enumerable.Range(0, 8).Select(i => 800 + i * 30d)).ToArray();
            var notes = generator.Generate(times, eight);
            // Eight lanes are played by hand (user rule): four keys read inside one hand.
            var hand = notes[0].Tile <= 4 ? new[] { 1, 2, 3, 4 } : [5, 6, 7, 8];
            Assert(notes.Take(16).All(n => n.Mode == 4 && hand.Contains(n.Tile)), "30ms singles stay in 4K, in one hand: "
                + string.Join(" ", notes.Take(16).Select(n => $"{n.Tile}/{n.Mode}")));
            var wide = notes.Where(n => n.HitTimeMs == 600).ToArray();
            Equal(wide.Length, 5);
            Assert(wide.All(n => n.Mode == 6 && eight.GetPattern(6).Contains(n.Tile)) && wide.Select(n => n.Tile).Distinct().Count() == 5,
                "The five-note chord takes six lanes: " + string.Join(" ", wide.Select(n => $"{n.Tile}/{n.Mode}")));
            Assert(notes.Where(n => n.HitTimeMs > 604).All(n => n.Mode <= 4), "The three-note chord and what follows go back to four keys");
            // Off, the gaps pick eight keys again.
            Assert(generator.Generate(times, eight with { ChordsOnlyAboveFourKeys = false }).Take(16).Any(n => n.Mode == 8), "Off: 8K by the gaps");
            Assert(!new VisualizerSettings().ChordsOnlyAboveFourKeys, "Off by default (user decision)");
            Assert(!eight.HasSameNoteRulesAs(eight with { ChordsOnlyAboveFourKeys = false }), "It is a note rule");
        });
        yield return ("Four-note chords on one hand on eight lanes, the hands in turn", () =>
        {
            // User rule, from S3RL - play it loud: four notes at once every 79ms read a s d f / j k l ;, not d f j k (the 4K
            // lanes 3-6 across both hands).
            var generator = new NoteGenerator();
            var eight = new VisualizerSettings { MaximumTileCount = 8, MinimumTileCount = 1, EightKeyBoundaryMs = 50, SixKeyBoundaryMs = 50,
                FourKeyBoundaryMs = 80, TwoKeyBoundaryMs = 500, SimultaneousWindowMs = 20 };
            var times = Enumerable.Range(0, 6).SelectMany(k => Enumerable.Repeat(k * 79d, 4)).ToArray();
            var visible = eight.GetVisibleTiles().ToList();
            var chords = generator.Generate(times, eight).GroupBy(n => n.HitTimeMs)
                .Select(g => g.Select(n => visible.IndexOf(n.Tile) + 1).Order().ToArray()).ToArray();
            Equal(chords.Length, 6);
            for (var k = 0; k < chords.Length; k++)
                Assert(chords[k].SequenceEqual(k % 2 == 0 ? new[] { 1, 2, 3, 4 } : new[] { 5, 6, 7, 8 }),
                    "Left, right, left: " + string.Join(" | ", chords.Select(c => string.Join(" ", c))));
            // Always so now (user rule): no setting, no experimental switch.
            Assert(new VisualizerSettings { MaximumTileCount = 8 }.PlaysByHand, "Eight lanes always play by hand");
        });
        yield return ("Eight lanes: chords and trills take only the table's places, never the pair across the center", () =>
        {
            // User rule: on eight lanes a chord of n notes sits only where the table puts it (2: 3-4, 5-6; 3: 2-3-4, 5-6-7;
            // 4: 1-4, 5-8; 5: 2-6, 3-7; 6: 2-7, 1-6, 3-8; 7: 1-7, 2-8; 8: all), and a trill takes the same places first.
            int[][] table = [[], [], [2, 4], [1, 4], [0, 4], [1, 2], [1, 0, 2], [0, 1], [0]];
            var generator = new NoteGenerator();
            var eight = new VisualizerSettings { MaximumTileCount = 8, MinimumTileCount = 1, EightKeyBoundaryMs = 25, SixKeyBoundaryMs = 50,
                FourKeyBoundaryMs = 100, TwoKeyBoundaryMs = 500, SimultaneousWindowMs = 25, TrillWindowMs = 40, TrillCenterPair = "LastResort" };
            var visible = eight.GetVisibleTiles().ToList();
            string Lanes(IEnumerable<VisualizerNote> notes) => string.Join(" ", notes.Select(n => visible.IndexOf(n.Tile) + 1));
            void OnTable(IReadOnlyList<VisualizerNote> notes, string what)
            {
                foreach (var chord in notes.GroupBy(n => n.HitTimeMs).Where(group => group.Count() >= 2))
                {
                    var lanes = chord.Select(n => visible.IndexOf(n.Tile)).Order().ToArray();
                    Assert(lanes[^1] - lanes[0] == lanes.Length - 1 && table[lanes.Length].Contains(lanes[0]),
                        $"{what}: a chord on lanes {string.Join("-", lanes.Select(lane => lane + 1))}");
                }
            }
            // Chords of every size among singles and trills, at many paces, with each rule set that changes chords.
            var random = new Random(7);
            var times = new List<double>();
            for (double at = 0, k = 0; k < 400; k++)
            {
                var size = random.Next(1, 9);
                for (var n = 0; n < size; n++) times.Add(at + n);
                at += size + random.Next(20, 420);
            }
            foreach (var (name, rules) in new[] { ("plain", eight), ("wide chords only", eight with { ChordsOnlyAboveFourKeys = true }),
                         ("enhanced 4/8 keys", eight with { ExperimentalFeatures = true, EnhancedFourEightKeys = true }) })
            {
                var notes = generator.Generate(times, rules);
                Equal(notes.Count, times.Count);
                OnTable(notes, name);
            }

            // Trills: two notes on 3-4 or 5-6, three on 2-3-4 or 5-6-7, four on one hand's lanes.
            IReadOnlyList<int[]> Trills(double[] shape, VisualizerSettings rules)
            {
                var run = Enumerable.Range(0, 12).SelectMany(k => shape.Select(offset => k * 600 + offset)).ToArray();
                var notes = generator.Generate(run, rules);
                return Enumerable.Range(0, 12).Select(k => notes.Skip(k * shape.Length).Take(shape.Length)
                    .Select(n => visible.IndexOf(n.Tile) + 1).Order().ToArray()).ToArray();
            }
            foreach (var (shape, places) in new[] { (new[] { 0d, 30 }, new[] { "3 4", "5 6" }), (new[] { 0d, 25, 50 }, new[] { "2 3 4", "5 6 7" }),
                         (new[] { 0d, 20, 40, 60 }, new[] { "1 2 3 4", "5 6 7 8" }) })
            {
                var laid = Trills(shape, eight);
                Assert(laid.All(lanes => places.Contains(string.Join(" ", lanes))), $"{shape.Length}-note trills: " + string.Join(" | ", laid.Select(l => string.Join(" ", l))));
            }
            // The trill setting decides the pair across the center (4-5): never there when it says never.
            var never = Trills([0d, 30], eight with { TrillCenterPair = "Never" });
            Assert(never.All(lanes => string.Join(" ", lanes) is "3 4" or "5 6"), "Never: " + string.Join(" | ", never.Select(l => string.Join(" ", l))));
            var allowed = Trills([0d, 30], eight with { TrillCenterPair = "Allowed" });
            Assert(allowed.All(lanes => string.Join(" ", lanes) is "3 4" or "4 5" or "5 6"), "Allowed: " + string.Join(" | ", allowed.Select(l => string.Join(" ", l))));
            Assert(!new[] { "1 2", "2 3", "6 7", "7 8" }.Any(pair => Trills([0d, 30], eight).Any(lanes => string.Join(" ", lanes) == pair)), "No pair off the table");

            // Enhanced 4/8 keys (experimental, user rule): a trill rolls past four keys, over a chord's places, while singles
            // keep to four keys, in one hand. Off, a roll stays in one hand.
            var enhanced = eight with { ExperimentalFeatures = true, EnhancedFourEightKeys = true };
            var six = Trills([0d, 25, 50, 75, 100, 125], enhanced);
            Assert(six.All(lanes => string.Join(" ", lanes) is "2 3 4 5 6 7" or "1 2 3 4 5 6" or "3 4 5 6 7 8"), "Six-note trills: " + string.Join(" | ", six.Select(l => string.Join(" ", l))));
            var chain = Enumerable.Range(0, 12).SelectMany(k => new[] { 0d, 25, 50, 75, 100, 125 }.Select(offset => k * 600 + offset)).ToArray();
            var rolled = generator.Generate(chain, eight).Select(n => visible.IndexOf(n.Tile) + 1).ToArray();
            for (var k = 0; k < 12; k++)
            {
                var head = rolled.Skip(k * 6).Take(4).ToArray();
                Assert(head.All(lane => lane <= 4) || head.All(lane => lane >= 5), "Off, the first four roll in one hand: " + string.Join(" ", rolled.Skip(k * 6).Take(6)));
            }
            var fast = Enumerable.Range(0, 32).Select(i => i * 45d).ToArray(); // 45ms: 6K by the gaps, not a trill
            var singles = generator.Generate(fast, enhanced);
            Assert(singles.All(n => n.Mode <= 4), "Singles keep to four keys: " + Lanes(singles));
            Assert(generator.Generate(fast, eight).Any(n => n.Mode > 4), "Off, the gaps pick more than four keys");
            // Four lanes or fewer: the same either way.
            var four = new VisualizerSettings { TrillWindowMs = 40, SimultaneousWindowMs = 25 };
            Assert(generator.Generate(times, four).SequenceEqual(generator.Generate(times, four with { ExperimentalFeatures = true, EnhancedFourEightKeys = true })),
                "Nothing changes on four lanes");
            Assert(!eight.HasSameNoteRulesAs(enhanced), "It is a note rule");
        });
        yield return ("Eight lanes by hand: four keys or fewer stay in one hand, a new run takes the other", () =>
        {
            // User rule, from S3RL - play it loud: a four-note run reads a s d f / j k l ;, never d f j k across the hands, a long
            // stream goes round inside the hand (4 3 2 1 4 3 2 1), and each new run takes the other hand.
            var generator = new NoteGenerator();
            var hands = new VisualizerSettings { MaximumTileCount = 8, MinimumTileCount = 1, EightKeyBoundaryMs = 25, SixKeyBoundaryMs = 25,
                FourKeyBoundaryMs = 80, TwoKeyBoundaryMs = 500, SimultaneousWindowMs = 25, TrillWindowMs = 40 };
            Assert(hands.PlaysByHand && !(hands with { MaximumTileCount = 6 }).PlaysByHand, "Eight lanes, and only eight, play by hand");
            var visible = hands.GetVisibleTiles().ToList();
            int Hand(VisualizerNote note) => visible.IndexOf(note.Tile) / 4;
            var runs = new List<double>();
            for (var run = 0; run < 4; run++)
                runs.AddRange(Enumerable.Range(0, 8).Select(i => run * 1200 + i * 79d));
            var notes = generator.Generate(runs, hands);
            for (var run = 0; run < 4; run++)
            {
                var part = notes.Skip(run * 8).Take(8).ToArray();
                Assert(part.All(n => Hand(n) == Hand(part[0])), $"Run {run + 1} in one hand: " + string.Join(" ", part.Select(n => visible.IndexOf(n.Tile) + 1)));
                if (run > 0) Assert(Hand(part[0]) != Hand(notes[(run - 1) * 8]), "Each new run takes the other hand");
            }
            // A 39ms stream: rolls of the hand's four lanes, the pattern's own walk.
            var stream = generator.Generate(Enumerable.Range(0, 24).Select(i => i * 39d).ToArray(), hands).Select(n => visible.IndexOf(n.Tile) + 1).ToArray();
            Assert(stream.All(lane => lane <= 4) || stream.All(lane => lane >= 5), "The stream stays in one hand: " + string.Join(" ", stream));
            Assert(stream.Zip(stream.Skip(1)).All(p => p.Second == p.First - 1 || p.Second == p.First + 3), "It walks 4 3 2 1: " + string.Join(" ", stream));
        });
        yield return ("On six and eight lanes too, a trill takes the pair between the hands only as a last resort", () =>
        {
            // User rule: the center pair is last for a trill on every lane count, so a 2K trill on eight lanes (pattern 4 5,
            // the center pair itself) rolls on one hand's pair.
            var generator = new NoteGenerator();
            var eight = new VisualizerSettings { MaximumTileCount = 8, MinimumTileCount = 1, TwoKeyBoundaryMs = 500, SimultaneousWindowMs = 20,
                TrillWindowMs = 40, TrillCenterPair = "LastResort" };
            var times = new[] { 0d, 300, 326, 600, 900, 926, 1200, 1500, 1526, 1800 };
            var visible = eight.GetVisibleTiles().ToList();
            var notes = generator.Generate(times, eight);
            foreach (var at in new[] { 1, 4, 7 })
            {
                var pair = new[] { visible.IndexOf(notes[at].Tile) + 1, visible.IndexOf(notes[at + 1].Tile) + 1 }.Order().ToArray();
                Assert(!pair.SequenceEqual(new[] { 4, 5 }), "A trill off the center pair: " + string.Join(" ", notes.Select(n => visible.IndexOf(n.Tile) + 1)));
            }
        });
        yield return ("Three-note trills in a row keep their lanes, a chain is cut where its rhythm groups it, and none runs against the pattern", () =>
        {
            // User report, with the user's settings and a 60 ms trill window: three-note trills in a row read 4-3-2 1-4-3 (one
            // chain cut every four notes) or took turns on other lanes, and a trill sometimes read 2-3-4 against 4 3 2 1.
            var user = UserSettings with { TrillWindowMs = 60 };
            var visible = user.GetVisibleTiles().ToList();
            var generator = new NoteGenerator();
            int[] Lanes(double[] times, VisualizerSettings settings) => generator.Generate(times, settings).Select(note => visible.IndexOf(note.Tile) + 1).ToArray();
            // Five groups of three, then a single 200 ms after the last.
            static double[] Groups(double inner, double between)
            {
                var times = new List<double>();
                for (var group = 0; group < 5; group++)
                {
                    var at = group * (2 * inner + between);
                    times.AddRange([at, at + inner, at + 2 * inner]);
                }
                times.Add(times[^1] + 200);
                return times.ToArray();
            }
            // Apart (100 ms), and inside one chain (45 or 35 ms between, inside the window): every group on the same lanes, each
            // the pattern's way, and the single after the last one off its lanes.
            foreach (var (inner, between) in new[] { (30d, 100d), (30d, 45d), (26d, 35d) })
            {
                var lanes = Lanes(Groups(inner, between), user);
                var groups = lanes.Take(15).Chunk(3).ToArray();
                var what = $"{inner}/{between}: " + string.Join(" ", lanes);
                Assert(groups.All(group => group.SequenceEqual(groups[0])), "The same lanes every time, " + what);
                Assert(groups[0][1] - groups[0][0] == -1 && groups[0][2] - groups[0][1] == -1, "The pattern's way, " + what);
                Assert(!groups[0].Contains(lanes[^1]), "The single after keeps off the trill, " + what);
            }
            // An even stream has no groups: it walks the pattern over every lane as before (4 3 2 1 4 3 2 1).
            var stream = Lanes(Groups(30, 30), user).Take(15).ToArray();
            Assert(stream.Zip(stream.Skip(1)).All(pair => pair.Second == pair.First - 1 || (pair.First == 1 && pair.Second == 4)),
                "An even stream walks: " + string.Join(" ", stream));
            // A three-note trill with no room the pattern's way where it starts takes the same lanes the pattern's way: after
            // 4 3 it read 2-3-4, and 4-3-2 is meant. From the edge lane it rolls back (1-2-3), as before.
            var mixed = Lanes([0, 37, 75, 150, 225, 300, 337, 375, 450, 525, 600, 675, 750, 787, 825, 900], user);
            Assert(mixed.Skip(5).Take(3).SequenceEqual([4, 3, 2]), "4 3 then 4-3-2: " + string.Join(" ", mixed));

            // Any rhythm of three-note trills, singles, chords and joined groups, on four lanes and eight played by hand: no roll
            // of three runs against the pattern unless it starts on the edge lane of its hand, and three-note trills right after
            // one another take the same lanes.
            int rolls = 0, pairs = 0, kept = 0;
            foreach (var settings in new[] { user, user with { MaximumTileCount = 8, TrillWindowMs = 60 } })
            for (var seed = 1; seed <= 3; seed++)
            {
                var random = new Random(seed);
                var times = new List<double>();
                var at = 0d;
                for (var events = 0; events < 600; events++)
                {
                    var kind = random.Next(5);
                    var inner = 26 + random.Next(10);
                    if (kind <= 1) times.AddRange([at, at + inner, at + 2 * inner]);
                    else if (kind == 2) times.AddRange([at, at + 5]);
                    else times.Add(at);
                    at = times[^1] + new[] { 45, 55, 75, 100, 150, 200, 300 }[random.Next(7)];
                }
                var shown = settings.GetVisibleTiles().ToList();
                var notes = generator.Generate(times.ToArray(), settings);
                bool Tap(int i) => i >= 0 && i < notes.Count && !notes[i].StartsChord && (i == 0 || !notes[i - 1].StartsChord);
                bool Close(int i) => notes[i + 1].HitTimeMs - notes[i].HitTimeMs <= settings.TrillWindowMs;
                bool Three(int i) => i >= 0 && i + 2 < notes.Count && Tap(i) && Tap(i + 1) && Tap(i + 2) && Close(i) && Close(i + 1)
                    && !(Tap(i - 1) && Close(i - 1)) && !(i + 3 < notes.Count && Tap(i + 3) && Close(i + 2));
                var span = settings.PlaysByHand ? 4 : shown.Count;
                for (var i = 0; i + 2 < notes.Count; i++)
                {
                    // A whole chain of three or four (a roll the lanes hold), judged from its first note.
                    if (!Tap(i) || (Tap(i - 1) && Close(i - 1))) continue;
                    var end = i;
                    while (end + 1 < notes.Count && Tap(end + 1) && Close(end)) end++;
                    if (end - i + 1 is < 3 or > 4) continue;
                    var lanes = Enumerable.Range(i, end - i + 1).Select(k => shown.IndexOf(notes[k].Tile)).ToArray();
                    if (Three(i)) rolls++;
                    Assert(!(lanes.Zip(lanes.Skip(1)).All(pair => pair.Second - pair.First == 1) && lanes[0] % span != 0),
                        $"{shown.Count} lanes, seed {seed}, at {notes[i].HitTimeMs}: " + string.Join("-", lanes.Select(lane => lane + 1)));
                    if (Three(i) && Three(i + 3))
                    {
                        pairs++;
                        if (notes[i + 3].Tile == notes[i].Tile && notes[i + 4].Tile == notes[i + 1].Tile && notes[i + 5].Tile == notes[i + 2].Tile) kept++;
                    }
                }
            }
            Assert(rolls > 500 && pairs > 50, $"Enough to say something: {rolls} trills, {pairs} in a row");
            Assert(kept >= pairs * 0.95, $"Three-note trills in a row keep their lanes: {kept} of {pairs}");
        });
        yield return ("A single after a trill never comes back onto the trill's lanes (1-2 3 1-2 3, not 1-2 2 1-2 2)", () =>
        {
            // User report, with the user's settings: four lanes, 2K on lanes 2-3, the center pair a trill's last resort. A trill
            // in 2K reaches out to the left pair (2-1) and the cycle came straight back to 2.
            var user = UserSettings;
            var visible = user.GetVisibleTiles().ToList();
            string Lanes(IEnumerable<VisualizerNote> notes) => string.Join(" ", notes.Select(note => visible.IndexOf(note.Tile) + 1));
            var generator = new NoteGenerator();
            var times = new[] { 0d, 30, 180, 380, 410, 560, 760, 790, 940, 1140, 1170, 1320 };
            Equal(Lanes(generator.Generate(times, user)), "2 1 3 2 1 3 2 1 3 2 1 3");

            // Any rhythm of trills, singles and chords, on four, six and eight lanes, with every center choice: the single right
            // after a two-note trill is on neither of its lanes while a lane is left that the event after it does not take
            // (between a trill on 1-2 and a chord on 3-4 every lane is taken). Before the fix, 858 of 3483 of these came back.
            var checkedSingles = 0;
            foreach (var center in VisualizerSettings.TrillCenterChoices)
            foreach (var settings in new[] { user, new VisualizerSettings { MagicCircleFixedTiles = false },
                         new VisualizerSettings { MaximumTileCount = 6, MagicCircleFixedTiles = false },
                         new VisualizerSettings { MaximumTileCount = 8, MagicCircleFixedTiles = false } })
            for (var seed = 1; seed <= 3; seed++)
            {
                var config = settings with { TrillCenterPair = center };
                var shown = config.GetVisibleTiles().ToList();
                var notes = generator.Generate(TrillRhythm(seed), config);
                bool Tap(int i) => i >= 0 && i < notes.Count && !notes[i].StartsChord && (i == 0 || !notes[i - 1].StartsChord);
                bool Close(int i) => notes[i + 1].HitTimeMs - notes[i].HitTimeMs <= config.TrillWindowMs;
                for (var i = 0; i + 2 < notes.Count; i++)
                {
                    if (!Tap(i) || !Tap(i + 1) || !Close(i) || (Tap(i - 1) && Close(i - 1)) || !Tap(i + 2) || Close(i + 1)
                        || (i + 3 < notes.Count && Tap(i + 3) && Close(i + 2))) continue;
                    var next = notes.Skip(i + 3).TakeWhile((note, k) => k == 0 || notes[i + 2 + k].StartsChord).Select(note => note.Tile).ToArray();
                    if (!shown.Any(tile => tile != notes[i].Tile && tile != notes[i + 1].Tile && !next.Contains(tile))) continue;
                    checkedSingles++;
                    Assert(notes[i + 2].Tile != notes[i].Tile && notes[i + 2].Tile != notes[i + 1].Tile,
                        $"{shown.Count} lanes, {center}, seed {seed}, at {notes[i].HitTimeMs}: "
                        + string.Join(" ", notes.Skip(Math.Max(0, i - 4)).Take(9).Select(note => shown.IndexOf(note.Tile) + 1)));
                }
            }
            Assert(checkedSingles > 1000, "Enough singles after a trill to say something: " + checkedSingles);
        });
        yield return ("Trill notes are drawn in the trill color, halfway between the single and chord colors by default", () =>
        {
            // User rule: a trill color of its own; chords and the start, end and checkpoint colors outrank it.
            var defaults = new VisualizerSettings();
            Equal(defaults.TrillColor, "84, 220, 185");
            Assert(defaults.HasSameNoteRulesAs(defaults with { TrillColor = "1, 2, 3" }), "A color is not a note rule");
            Equal((defaults with { TrillColor = "1, 2, 3" }).WithVisuals(defaults).TrillColor, defaults.TrillColor);
            Throws<ArgumentException>(() => (defaults with { TrillColor = "blue-ish" }).EnsureValid());

            const int width = 300, height = 400;
            var settings = defaults with
            {
                DynamicTiming = false, JudgmentLineHeightDip = 36, MagicCircleJudgmentLine = false, SimultaneousWindowMs = 20, TrillWindowMs = 40,
                NoteColor = "200, 30, 30", TrillColor = "30, 30, 200", ChordColor = "30, 200, 30",
                StartNoteColor = "255, 255, 255", EndNoteColor = "255, 255, 255"
            };
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            // The start and end notes keep their own color, so the trill (1000, 1030) and the single (2000) sit between them.
            session.LoadTimeline(new[] { 0d, 1000, 1030, 2000, 3000 }, settings);
            session.Toggle(); session.Start();
            var field = new Visualizer.App.Rendering.NoteField { Session = session };
            var visible = settings.GetVisibleTiles().ToList();
            byte[] Render()
            {
                field.Tick();
                field.Measure(new Size(width, height));
                field.Arrange(new Rect(0, 0, width, height));
                field.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(field);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                return pixels;
            }
            // Four lanes of 75px with the judgment line at y 334; pixels are B G R A.
            (byte B, byte G, byte R) At(byte[] pixels, VisualizerNote note)
            {
                var index = (334 * width + visible.IndexOf(note.Tile) * 75 + 37) * 4;
                return (pixels[index], pixels[index + 1], pixels[index + 2]);
            }
            clock.NowMs = 1000;
            var trill = At(Render(), session.Notes[1]);
            Assert(trill.B > 150 && trill.R < 90, $"The trill's first note in the trill color: {trill}");
            clock.NowMs = 2000;
            var single = At(Render(), session.Notes[3]);
            Assert(single.R > 150 && single.B < 90, $"The single after it in the note color: {single}");

            // With trills off the same notes are singles.
            var off = new PlaybackSession(clock);
            off.LoadTimeline(new[] { 0d, 1000, 1030, 2000, 3000 }, settings with { TrillWindowMs = 0 });
            // Start reads the clock as the song's beginning.
            clock.NowMs = 0;
            off.Toggle(); off.Start();
            field.Session = off;
            clock.NowMs = 1000;
            var plain = At(Render(), off.Notes[1]);
            Assert(plain.R > 150 && plain.B < 90, $"Trills off: the note color: {plain}");

            // The rule, as both screens read it: singles that are no long note within the trill window of a neighbouring one.
            var notes = new NoteGenerator().Generate(new[] { 0d, 30, 500, 510, 1000, 2000, 2030 }, new double?[] { null, null, null, null, null, 2500, null },
                settings with { SimultaneousWindowMs = 20 });
            var marks = Enumerable.Range(0, notes.Count).Select(i => TrillNotes.IsTrill(notes, i, 40)).ToArray();
            Assert(marks.SequenceEqual(new[] { true, true, false, false, false, false, false }), "Trill, chord, single, long note: " + string.Join(" ", marks));
            Assert(!TrillNotes.IsTrill(notes, 0, 0), "No trills with the window at 0");
            var times = new[] { 0d, 30, 500 };
            Assert(TrillNotes.IsTrill(3, 1, 40, _ => true, k => times[k]) && !TrillNotes.IsTrill(3, 2, 40, _ => true, k => times[k])
                && !TrillNotes.IsTrill(3, 1, 40, k => k != 0, k => times[k]), "The same rule over other notes");
        });
        yield return ("The trill center pair is allowed, the last resort, or never taken as a two-note chord never takes it", () =>
        {
            // User rule: a list of three. Last resort is the default and the user's: the center is taken where every other pair
            // breaks the run (4 3-2 1); never keeps every two-note trill off it, as a chord is kept off it on four lanes.
            var generator = new NoteGenerator();
            var user = UserSettings;
            var visible = user.GetVisibleTiles().ToList();
            string Lanes(VisualizerSettings settings, double[] times) =>
                string.Join(" ", generator.Generate(times, settings).Select(note => visible.IndexOf(note.Tile) + 1));
            var run = new[] { 0d, 80, 160, 190, 270, 350, 430, 460, 540 };
            Equal(Lanes(user, run), "3 4 3 2 1 4 3 2 1");
            Equal(Lanes(user with { TrillCenterPair = "Never" }, run), "3 2 4 3 2 1 4 3 2");
            Equal(new VisualizerSettings().TrillCenterPair, "LastResort");

            // Any rhythm, on four, six and eight lanes: set to never, no two-note trill sits on the pair between the hands.
            var trills = 0;
            foreach (var settings in new[] { user, new VisualizerSettings { MagicCircleFixedTiles = false },
                         new VisualizerSettings { MaximumTileCount = 6, MagicCircleFixedTiles = false },
                         new VisualizerSettings { MaximumTileCount = 8, MagicCircleFixedTiles = false } })
            for (var seed = 1; seed <= 3; seed++)
            {
                var never = settings with { TrillCenterPair = "Never" };
                var shown = never.GetVisibleTiles().ToList();
                var notes = generator.Generate(TrillRhythm(seed), never);
                bool Tap(int i) => i >= 0 && i < notes.Count && !notes[i].StartsChord && (i == 0 || !notes[i - 1].StartsChord);
                bool Close(int i) => i >= 0 && i + 1 < notes.Count && notes[i + 1].HitTimeMs - notes[i].HitTimeMs <= never.TrillWindowMs;
                for (var i = 0; i + 1 < notes.Count; i++)
                {
                    if (!Tap(i) || !Tap(i + 1) || !Close(i) || (Tap(i - 1) && Close(i - 1)) || (Tap(i + 2) && Close(i + 1))) continue;
                    trills++;
                    var pair = new[] { shown.IndexOf(notes[i].Tile), shown.IndexOf(notes[i + 1].Tile) }.Order().ToArray();
                    Assert(!(pair[0] == shown.Count / 2 - 1 && pair[1] == shown.Count / 2),
                        $"{shown.Count} lanes, seed {seed}: a trill on the center pair at {notes[i].HitTimeMs}");
                }
            }
            Assert(trills > 1000, "Enough trills to say something: " + trills);
        });
        yield return ("A long roll in a pattern of one or two lanes goes side by side, never 2 2 2 2 or 2 3 2 3", () =>
        {
            // A roll is the pattern's own walk only where the pattern has the lanes for it: walked in 1K or 2K a four-note roll
            // read one lane four times or 2 3 2 3 (user report, RUSH E).
            var generator = new NoteGenerator();
            foreach (var one in new[] { true, false })
            {
                var settings = Settings with { TrillWindowMs = 40, TrillAsOneNote = one };
                var notes = generator.Generate(new[] { 0d, 600, 630, 660, 690, 1300 }, settings);
                var roll = notes.Skip(1).Take(4).Select(n => n.Tile).ToArray();
                Equal(roll.Distinct().Count(), 4);
                var steps = roll.Zip(roll.Skip(1), (a, b) => b - a).ToArray();
                Assert(steps.All(step => step == steps[0]), "Side by side: " + string.Join(" ", roll));
            }
        });
        yield return ("A run of singles in a chord section walks on from the lane really played, 4 3 2 1 and never 1 4 2 3", () =>
        {
            // User report (We Could Get More Machinegun Psystyle, 65.5s): a 47ms run in a 4K section that also holds a chord read
            // 1 4 2 3 over and over. Its first note had to move off the lane just played, and every note after it then took the
            // cursor's tile, a step behind, which the note before had just taken.
            var settings = Settings with { TrillWindowMs = 40, SimultaneousWindowMs = 20, FourKeyBoundaryMs = 80, TwoKeyBoundaryMs = 500 };
            var times = new List<double> { 0, 0.5, 94, 188, 281 };
            times.AddRange(Enumerable.Range(0, 16).Select(i => 375 + i * 47d));
            times.AddRange([375 + 16 * 47, 375 + 16 * 47 + 0.5]);
            var notes = new NoteGenerator().Generate(times, settings);
            var visible = settings.GetVisibleTiles().ToList();
            var run = notes.Where(n => n.HitTimeMs >= 375 && n.HitTimeMs < 375 + 16 * 47).Select(n => visible.IndexOf(n.Tile) + 1).ToArray();
            Assert(run.Zip(run.Skip(1)).All(p => p.Second == p.First + 1 || p.Second == p.First - 3)
                || run.Zip(run.Skip(1)).All(p => p.Second == p.First - 1 || p.Second == p.First + 3), "The run walks the pattern: " + string.Join(" ", run));

            // Mixes the same every time: 2K stretches with chords, 4K runs at 47-66ms and chords right after them, on the
            // user's layout (tiles 3 4 5 6 played 6 to 3). Every run of six singles or more walks the pattern: a lane taken
            // by a chord earlier in the block is free again once rested, a run walks on from the lane really played, and a
            // single after a chord at the edge goes round as after a trill rolled into it (2-1 4, not 2-1 3 4 2 1).
            var user = new VisualizerSettings { MinimumTileCount = 2, FourKeyBoundaryMs = 80, TwoKeyBoundaryMs = 500, SimultaneousWindowMs = 25,
                TrillWindowMs = 40, NextIntervalTiles = true, MagicCircleFixedTiles = false };
            var userVisible = user.GetVisibleTiles().ToList();
            var random = new Random(11);
            for (var variant = 0; variant < 120; variant++)
            {
                var mix = new List<double>();
                var runs = new List<(int From, int To)>();
                var t = 0d;
                for (var part = random.Next(2, 5); part > 0; part--)
                {
                    for (var k = random.Next(0, 5); k > 0; k--)
                    {
                        mix.AddRange(random.Next(3) == 0 ? [t, t + 0.5] : [t]);
                        t += new[] { 94d, 110, 130, 160 }[random.Next(4)];
                    }
                    var gap = new[] { 47d, 52, 59, 66 }[random.Next(4)];
                    var count = random.Next(6, 15);
                    runs.Add((mix.Count, mix.Count + count));
                    for (var k = 0; k < count; k++, t += gap) mix.Add(t);
                    if (random.Next(10) < 7) { mix.AddRange([t, t + 0.5]); t += gap; }
                    t += new[] { 0d, 94, 200 }[random.Next(3)];
                }
                var placed = new NoteGenerator().Generate(mix, user);
                CheckRuns(placed, runs, $"Variant {variant}");
            }
            // A trill inside a run (Plum - Life Archive, 58s: 54ms with a 27ms roll, 4K boundary 100): after it every lane
            // comes back a step too soon for the quick window, and resting the oldest lane first kept the run on 1 4 2 3 for good.
            var paced = user with { FourKeyBoundaryMs = 100 };
            for (var variant = 0; variant < 60; variant++)
            {
                var mix = new List<double>();
                var t = 0d;
                var gap = new[] { 52d, 54, 57, 62 }[random.Next(4)];
                for (var k = random.Next(4, 9); k > 0; k--, t += gap) mix.Add(t);
                mix.AddRange([t, t + 27, t + 54]);
                t += 54 + gap;
                var from = mix.Count;
                for (var k = 0; k < 12; k++, t += gap) mix.Add(t);
                var end = mix.Count;
                // A chord after the run makes its section a chord section, where singles are placed one by one.
                mix.AddRange([t, t + 0.5]);
                var placed = new NoteGenerator().Generate(mix, paced);
                CheckRuns(placed, [(from + 2, end)], $"Trill variant {variant}");
            }
            void CheckRuns(IReadOnlyList<VisualizerNote> placed, IEnumerable<(int From, int To)> runs, string label)
            {
                foreach (var (from, to) in runs)
                {
                    // The last single may sit beside the pair of a chord right after it (user rule: 2 3-4), so it is left out.
                    var lanes = placed.Skip(from).Take(to - from - 1).Where(n => n.Mode == 4).Select(n => userVisible.IndexOf(n.Tile) + 1).ToArray();
                    if (lanes.Length < 6) continue;
                    Assert(lanes.Zip(lanes.Skip(1)).All(p => p.Second == p.First - 1 || p.Second == p.First + 3)
                        || lanes.Zip(lanes.Skip(1)).All(p => p.Second == p.First + 1 || p.Second == p.First - 3),
                        $"{label}: a run walks the pattern: " + string.Join(" ", lanes));
                }
            }
        });
        yield return ("A chord with more notes than its section's pattern takes the screen's lanes", () =>
        {
            // Six notes at once in a 4K section of eight lanes: the 4K pattern has four lanes, the chord needs six, so it takes
            // the screen's (two songs could not load with a maximum of eight).
            var eight = new VisualizerSettings { MaximumTileCount = 8, MinimumTileCount = 1, FourKeyBoundaryMs = 120, SixKeyBoundaryMs = 50,
                EightKeyBoundaryMs = 30, SimultaneousWindowMs = 20, TrillWindowMs = 40 };
            var times = new[] { 0d, 90, 180, 270, 271, 272, 273, 274, 275, 360, 450 };
            var notes = new NoteGenerator().Generate(times, eight);
            Equal(notes.Count, times.Length);
            var chord = notes.Where(n => n.HitTimeMs == 270).ToArray();
            Equal(chord.Length, 6); Equal(chord.Select(n => n.Tile).Distinct().Count(), 6);
        });
        yield return ("A chord wider than its lanes is cut at them, and the rest is the next chord at its own time", () =>
        {
            // User rule, from Camellia - Hello (BPM) 2026 (28 notes in 25ms, more than any lanes): a group holds no more notes
            // than it has lanes, the screen's and any spare ones; what is left starts the next group, as a long roll does.
            var generator = new NoteGenerator();
            var seven = generator.Generate(new[] { 0d, 1, 2, 3, 4, 5, 6, 500 }, Settings);
            Equal(seven.Count, 8);
            Assert(seven.Take(4).All(note => note.HitTimeMs == 0) && seven.Take(4).Select(note => note.Tile).Distinct().Count() == 4,
                "The first four are one chord over the four lanes: " + string.Join(" ", seven.Select(note => $"{note.Tile}@{note.HitTimeMs}")));
            Assert(seven.Skip(4).Take(3).All(note => note.HitTimeMs == 4) && seven.Skip(4).Take(3).Select(note => note.Tile).Distinct().Count() == 3,
                "The other three are the next chord at their own first time: " + string.Join(" ", seven.Select(note => $"{note.Tile}@{note.HitTimeMs}")));
            // With spare lanes on, the group first fills them, as before.
            var spare = Settings with { ExtraLanes = true };
            var wide = generator.Generate(new[] { 0d, 1, 2, 3, 4, 5, 6, 500 }, spare);
            Equal(wide.Count(note => note.HitTimeMs == 0), 6); Equal(wide.Count(note => note.HitTimeMs == 6), 1);
            // A dense stream of the kind the song has: every note is placed, and no lane carries two notes at one time.
            var stream = Enumerable.Range(0, 120).Select(i => 1000 + i * 0.9).ToArray();
            var streamed = generator.Generate(stream, Settings);
            Equal(streamed.Count, stream.Length);
            Assert(streamed.GroupBy(note => note.HitTimeMs).All(group => group.Count() <= 4 && group.Select(note => note.Tile).Distinct().Count() == group.Count()),
                "Every group fits the four lanes");
        });
        yield return ("Unplaceable chords fail clearly without silent expansion or dropped hits", () =>
        {
            var generator = new NoteGenerator();
            Throws<NotePlacementException>(() => generator.Generate(new[] { 0d, 100, 105 }, Settings with { MaximumTileCount = 2 }));
            Throws<NotePlacementException>(() => generator.Generate(new[] { 0d, 1, 2 }, Settings with { MaximumTileCount = 2 }));
            // A chord right as a long note ends keeps off its lane (user rule): the long note is on tile 2, so three notes
            // have only 1 and 3-4 beside it.
            double[] held = [0, 501, 502, 503, 2000];
            double?[] ends = [500, null, null, null, null];
            Throws<NotePlacementException>(() => generator.Generate(held, ends, Settings));
            var pair = generator.Generate(new[] { 0d, 1 }, Settings);
            Equal(pair.Count, 2); Assert(pair[0].Tile != pair[1].Tile, "Never overlay notes on one tile");
            var english = Settings with { Language = Languages.English };
            try { generator.Generate(held, ends, english); throw new InvalidOperationException("Expected failure"); }
            catch (NotePlacementException e) { Assert(e.Message.Contains("Cannot place chords"), "Localized actionable failure"); }
        });
        yield return ("Failed preparation blocks Start and recovers from retained source", () =>
        {
            var clock = new Clock();
            var playback = new PlaybackSession(clock);
            var original = new[] { 100d, 200, 205 };
            playback.LoadTimeline(original, Settings);
            playback.Toggle(); Assert(playback.Start(), "Start prepared song");
            Throws<NotePlacementException>(() => playback.ApplySettings(Settings with { MaximumTileCount = 2 }));
            Equal(playback.State, PlaybackState.NoTimeline); Equal(playback.Notes.Count, 0);
            Assert(!playback.Start(), "Stale notes must never restart");
            Equal(playback.Settings, Settings);
            Assert(playback.Timeline!.HitTimesMs.SequenceEqual(original), "Retain raw source for recovery");
            playback.ApplySettings(Settings);
            Equal(playback.State, PlaybackState.Waiting); Equal(playback.Notes.Count, 3);
            var prepared = playback.Notes;
            playback.Start(); Assert(ReferenceEquals(prepared, playback.Notes), "Start cannot replan chords");
            playback.Reset(Settings); Assert(prepared.SequenceEqual(playback.Notes), "Reset deterministic");
            Throws<NotePlacementException>(() => playback.LoadParsedTimeline(HeldChord(3), Settings));
            Assert(!playback.Start(), "New invalid song must not retain old playback");
            playback.ApplySettings(Settings with { SimultaneousWindowMs = 0 });
            Equal(playback.State, PlaybackState.Waiting);
        });
        yield return ("Chord settings and preview render in Korean and English", () =>
        {
            foreach (var language in new[] { Languages.Korean, Languages.English })
            {
                var window = new SettingsPanel(Settings with { Language = language });
                var fields = ((DockPanel)window.Content).Children.OfType<ScrollViewer>().Single().Content as StackPanel;
                // Rows live inside section cards, so search the whole settings list.
                Assert(TextBlocks(fields!).Any(t => t.Text.Contains(language == Languages.Korean ? "동타 판정" : "Simultaneous window")),
                    "Localized chord field missing");
                Capture(window, Path.Combine(artifacts, $"chord-setting-{language}.png"), 720, 760);
            }
            var main = new MainWindow();
            var model = (MainViewModel)main.DataContext;
            model.ApplySettings(Settings with { InitialTile = 3 });
            model.Playback.LoadTimeline(new[] { 0d, 300, 305, 600, 900, 1200, 1205, 1500, 1800 },
                Settings with { InitialTile = 3, DynamicTiming = false });
            model.Toggle();
            Capture(main, Path.Combine(artifacts, "chord-four-lanes.png"), 480, 800);
            main.Close();
        });
        yield return ("Checkpoint and UI actions contain chord preparation failures", () =>
        {
            var model = new MainViewModel();
            var safe = Settings with { SimultaneousWindowMs = 0 };
            model.ApplySettings(safe);
            // Three hits within the window right as a long note ends need three lanes side by side off its lane, which four
            // lanes do not have.
            var timeline = new ParsedTimeline(new[] { 0d, 100, 101, 102, 103, 200, 300 },
                new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 5) }, true,
                hitKinds: new[] { HitKind.HoldStart, HitKind.HoldEnd, HitKind.Tap, HitKind.Tap, HitKind.Tap, HitKind.Tap, HitKind.Tap });
            model.Playback.LoadParsedTimeline(timeline, safe);
            model.Toggle(); model.Start();
            Throws<NotePlacementException>(() => model.ApplySettings(Settings));
            Assert(model.Message.Contains("동타를 배치할 수 없습니다"), "UI must expose the reason");
            model.Start(); Equal(model.Playback.State, PlaybackState.NoTimeline);
            model.ApplySettings(safe); Equal(model.Playback.State, PlaybackState.Waiting);
            model.Checkpoint = 2;
            Equal(model.Playback.Notes.Count, 2); Equal(model.Playback.Notes[0].HitTimeMs, 0d);
            // Checkpoint starts slice a full play, so the unplaceable group before checkpoint 2 blocks it as well.
            Throws<NotePlacementException>(() => model.ApplySettings(Settings));
            Equal(model.Playback.Notes.Count, 0); Equal(model.Playback.State, PlaybackState.NoTimeline);
            Assert(model.Message.Contains("동타를 배치할 수 없습니다"), "Checkpoint failure needs a reason");
            model.Reset(); Equal(model.Playback.State, PlaybackState.Waiting);
            // The rejected settings above were never applied, so load the invalid song with a model on the strict settings.
            var loader = new MainViewModel();
            loader.ApplySettings(Settings);
            var invalid = HeldChord(3);
            loader.SelectedMap = new Visualizer.Application.Cache.MapCacheEntry("bad-chord", "unused", "map.adofai", "chord test",
                MapParseStatus.Supported, invalid, Array.Empty<string>(), "test", "test");
            loader.LoadSelectedSong(); loader.Toggle(); loader.Toggle(); loader.Start();
            Equal(loader.Playback.Notes.Count, 0);
            Assert(loader.Playback.State != PlaybackState.Playing, "UI retry must not revive stale notes");
            Assert(loader.Message.Contains("동타를 배치할 수 없습니다"), "Load/Toggle failure must remain visible");
        });
    }
}
