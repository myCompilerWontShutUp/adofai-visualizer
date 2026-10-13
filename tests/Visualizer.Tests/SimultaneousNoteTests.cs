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
            // The window is pinned here so editing the initial settings does not change this rule check.
            var window = Settings with { SimultaneousWindowMs = 10 };
            var notes = generator.Generate(times, window);
            Assert(notes.Select(n => n.HitTimeMs).SequenceEqual(new[] { 0d, 0, 20, 20 }), "Groups must use first hit, without chaining");
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
            var times = new[] { 0d, 30, 35, 60, 90, 120, 125, 150, 180 };
            var generator = new NoteGenerator();
            var notes = generator.Generate(times, Settings with { InitialTile = 3 });
            // 60/90 and 150/180 are trills: they roll up the ascending pattern (3-4) away from the chord before them, and the
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

        yield return ("A trill counted as one note is one step: its tile count from the gaps around it, its lanes side by side", () =>
        {
            // User rule (default on): a trill counts as one note, as a chord does. The rhythm from Wont Let You Go, a trill
            // (29ms) and a single in turn 172 and 171ms apart, read 4 2-1 4 2-1 note by note: the 29ms asked for 4K and every
            // note took a step of the cycle, so a lane dropped out. As one note the gaps are 172 and 171ms.
            var settings = new VisualizerSettings { MaximumTileCount = 4, SimultaneousWindowMs = 20, TrillWindowMs = 40, FourKeyBoundaryMs = 120,
                TwoKeyBoundaryMs = 500, MagicCircleFixedTiles = false, NoTrillCenterPair = true };
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
            // A 25ms stream: rolls of four, each one step, 4-3-2-1 every time.
            var stream = Enumerable.Range(0, 16).Select(i => 2000 + i * 25d).ToArray();
            var rolled = generator.Generate(stream, settings);
            Sequence(rolled.Select(note => visible.IndexOf(note.Tile) + 1), 4, 3, 2, 1, 4, 3, 2, 1, 4, 3, 2, 1, 4, 3, 2, 1);
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
            foreach (var centerLast in new[] { false, true })
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
                    EnhancedChordRules = enhanced, ExtraLanes = extra, NoTrillCenterPair = centerLast, MagicCircleFixedTiles = false
                };
                if (lanes == 1) settings = settings with { InitialTile = settings.GetPattern(1)[0] };
                IReadOnlyList<VisualizerNote> each;
                try { each = generator.Generate(times.ToArray(), settings with { TrillAsOneNote = false }); }
                catch (NotePlacementException) { continue; }
                var notes = generator.Generate(times.ToArray(), settings);
                var what = $"{lanes} lanes, enhanced {enhanced}, extra {extra}, center last {centerLast}, seed {seed}";
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
                ChordLanePreference = "1-2,3-4", NoChordCenterPair = true, NoTrillCenterPair = true, MagicCircleFixedTiles = false,
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
                SimultaneousWindowMs = 20, TrillWindowMs = 40, NoTrillCenterPair = true, MagicCircleFixedTiles = false, NextIntervalTiles = true,
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
                SimultaneousWindowMs = 0, TrillWindowMs = 40, NoTrillCenterPair = true, MagicCircleFixedTiles = false, InitialTile = 6,
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
            // Start and end both in the window (user rule): a b c with a-b and b-c close but a-c not is no three-note chord;
            // a-b is one, and c, close after it, rolls on beside it (1-2 3 or 3-4 2).
            var chain = generator.Generate(new[] { 0d, 15, 30 }, Settings with { SimultaneousWindowMs = 20 });
            Assert(chain[0].StartsChord && !chain[1].StartsChord && chain[2].HitTimeMs == 30, "a-b chord, c apart");
            var chordLanes = chain.Take(2).Select(n => n.Tile).ToArray();
            Assert(chordLanes.Any(tile => Math.Abs(tile - chain[2].Tile) == 1) && !chordLanes.Contains(chain[2].Tile),
                "c sits beside the chord: " + string.Join(" ", chain.Select(n => n.Tile)));
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
        yield return ("Unplaceable chords fail clearly without silent expansion or dropped hits", () =>
        {
            var generator = new NoteGenerator();
            Throws<NotePlacementException>(() => generator.Generate(new[] { 0d, 100, 105 }, Settings with { MaximumTileCount = 2 }));
            Throws<NotePlacementException>(() => generator.Generate(new[] { 0d, 1, 2 }, Settings with { MaximumTileCount = 2 }));
            Throws<NotePlacementException>(() => generator.Generate(new[] { 0d, 1, 2, 3, 4 }, Settings));
            Throws<NotePlacementException>(() => generator.Generate(new[] { 0d, 1 }, Settings with { MaximumTileCount = 1 }));
            var pair = generator.Generate(new[] { 0d, 1 }, Settings);
            Equal(pair.Count, 2); Assert(pair[0].Tile != pair[1].Tile, "Never overlay notes on one tile");
            var english = Settings with { Language = Languages.English };
            try { generator.Generate(new[] { 0d, 1, 2, 3, 4 }, english); throw new InvalidOperationException("Expected failure"); }
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
            Throws<NotePlacementException>(() => playback.LoadTimeline(new[] { 0d, 1, 2, 3, 4 }, Settings));
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
            // Five hits within the window need five distinct lanes, more than the four visible ones.
            var timeline = new ParsedTimeline(new[] { 0d, 100, 101, 102, 103, 104, 200, 300 },
                new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 6) }, true);
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
            var invalid = new ParsedTimeline(new[] { 0d, 1, 2, 3, 4 });
            loader.SelectedMap = new Visualizer.Application.Cache.MapCacheEntry("bad-chord", "unused", "map.adofai", "chord test",
                MapParseStatus.Supported, invalid, Array.Empty<string>(), "test", "test");
            loader.LoadSelectedSong(); loader.Toggle(); loader.Toggle(); loader.Start();
            Equal(loader.Playback.Notes.Count, 0);
            Assert(loader.Playback.State != PlaybackState.Playing, "UI retry must not revive stale notes");
            Assert(loader.Message.Contains("동타를 배치할 수 없습니다"), "Load/Toggle failure must remain visible");
        });
    }
}
