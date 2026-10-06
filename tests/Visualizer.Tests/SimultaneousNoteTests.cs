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
        yield return ("Notes keep one hand's flow: no key twice in a row, singles beside chords, trills roll with the pattern", () =>
        {
            // User report (Plum - Megamix): 1 2 3 2 1 zigzags, trills against the 4-3-2-1 pattern (1-2 instead of 2-1), the same
            // key twice in a row (3 3 2 3 2, 3-4 3-4) and singles away from the chord (1 3-4 1 3-4 instead of 2 3-4 2 3-4).
            var settings = new VisualizerSettings
            {
                SimultaneousWindowMs = 20, TrillWindowMs = 40, MaximumTileCount = 4, InitialTile = 4, EnableTilePreference = false,
                EightKeyBoundaryMs = 50, SixKeyBoundaryMs = 70, FourKeyBoundaryMs = 120, TwoKeyBoundaryMs = 500,
                ChordLanePreference = "1-2,3-4", NoChordCenterPair = true, NoTrillCenterPair = true, MagicCircleFixedTiles = false
            };
            var visible = settings.GetVisibleTiles().ToList();
            var generator = new NoteGenerator();
            // Events as screen lanes from the left (1-based): one lane for a single, two for a chord.
            List<(double Time, int[] Lanes)> Events(IReadOnlyList<VisualizerNote> notes)
            {
                var events = new List<(double, int[])>();
                for (var i = 0; i < notes.Count; i++)
                {
                    var lanes = notes[i].StartsChord ? new[] { notes[i], notes[++i] } : new[] { notes[i] };
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

            // Trills among singles at 2K and 4K pace (user rules): every trill rolls right to left on a pair it may take,
            // two trills in a row take two pairs (not 1-2 1-2), and the single after a trill sits beside it (1-2 3, 3-4 2).
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
                    Assert(first - second == 1 && second != 2, $"A trill rolls right to left on an outer pair at {flow[i].Time}: " + Text(flow));
                    Assert(lastTrill is null || lastTrill.Min() != second, $"Two trills in a row take two pairs at {flow[i].Time}: " + Text(flow));
                    lastTrill = [first, second];
                    i++;
                    continue;
                }
                if (lastTrill is { } pair)
                    Assert(pair.Any(lane => Math.Abs(lane - flow[i].Lanes[0]) == 1) && !pair.Contains(flow[i].Lanes[0]),
                        $"The single after a trill sits beside it at {flow[i].Time}: " + Text(flow));
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
        });
        yield return ("Wide chords split into two-note chords plus the latest single", () =>
        {
            var generator = new NoteGenerator();
            // Checks every same-time group: members pair in order on adjacent lanes, an odd last member is a single,
            // no tile repeats, and a 4K chord uses an outer pair.
            void Check(IReadOnlyList<VisualizerNote> notes, VisualizerSettings settings, double[] times)
            {
                Equal(notes.Count, times.Length);
                var visible = settings.GetVisibleTiles().ToList();
                foreach (var group in notes.GroupBy(n => n.HitTimeMs))
                {
                    var members = group.ToArray();
                    Equal(members.Select(n => n.Tile).Distinct().Count(), members.Length);
                    for (var m = 0; m + 1 < members.Length; m += 2)
                    {
                        Equal(Math.Abs(visible.IndexOf(members[m].Tile) - visible.IndexOf(members[m + 1].Tile)), 1);
                        if (members[m].Mode == 4)
                        {
                            var pair = new[] { members[m].Tile, members[m + 1].Tile }.Order().ToArray();
                            var active = settings.GetPattern(4).Order().ToArray();
                            Assert(pair.SequenceEqual(active[..2]) || pair.SequenceEqual(active[2..]), "A 4K chord must occupy an outer pair");
                        }
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
            Equal(generator.Generate(new[] { 0d, 1, 2, 3 }, Settings with { FourKeyPattern = "1 2 3 4" }).Count, 4);
        });
        yield return ("With four lanes, chords never use the middle lanes 2-3 in any mode", () =>
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
                foreach (var group in notes.GroupBy(n => n.HitTimeMs).Select(g => g.ToArray()))
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
