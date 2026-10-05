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
            Sequence(notes.Select(n => n.Tile), 3, 1, 2, 4, 3, 1, 2, 4, 3);
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
