using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Visualizer.App.Rendering;
using Visualizer.App.ViewModels;
using Visualizer.App.Views;
using Visualizer.Application.Playback;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using Visualizer.Infrastructure.Maps;

namespace Visualizer.Tests;

internal static class Program
{
    private sealed class FakeClock : IMonotonicClock { public double NowMs { get; set; } }
    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    internal static void Equal<T>(T actual, T expected) => Assert(EqualityComparer<T>.Default.Equals(actual, expected), $"Expected {expected}, got {actual}");
    internal static void Sequence(IEnumerable<int> actual, params int[] expected) => Assert(actual.SequenceEqual(expected), "Wrong tile sequence: " + string.Join(" ", actual));
    internal static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // Windows built here must never read or write the real settings and song list; tests use their own folders.
        Visualizer.App.Views.MainWindow.UseAppStore = false;
        if (args.Length is not (1 or 3 or 4) || !Path.IsPathFullyQualified(args[0])
            || args.Length >= 3 && (args[1] != "--read-only-map-root" || !Path.IsPathFullyQualified(args[2]))
            || args.Length == 4 && args[3] != "--compare-game-rules")
            throw new ArgumentException("Pass the absolute workspace-local artifact directory, optionally --read-only-map-root PATH.");
        var artifactRoot = args[0];
        var tests = new (string Name, Action Run)[]
        {
            ("Defaults and settings validation", () =>
            {
                var s = new VisualizerSettings();
                s.EnsureValid();
                Equal(s.ResetKey, "Enter"); Equal(s.StartTriggerKey, "J");
                Equal(s.MaximumTileCount, 4); Equal(s.NoteTravelTimeMs, 600d); Equal(s.NoteThickness, 1d);
                Equal(NoteLayout.ThicknessDip(s.NoteThickness), 18d); // The former ×3 look is the default.
                Equal(s.MinimumTileCount, 1); Equal(s.MapRootDirectory, Visualizer.Infrastructure.Maps.MapLocations.DefaultWorkshopRoot);
                Equal(s.PreviousCheckpointKey, "-"); Equal(s.NextCheckpointKey, "+"); // Optional keys.
                Assert(s.ShowInputEffect && s.ShowMissEffect && s.ShowGuideButtons, "Visual toggles default on");
                foreach (var folder in new[] { "", "maps", @"\maps" })
                    Throws<ArgumentException>(() => (s with { MapRootDirectory = folder }).EnsureValid());
                foreach (var invalid in new[] { 0, 3, 5, 16 }) Throws<ArgumentException>(() => (s with { MaximumTileCount = invalid }).EnsureValid());
                foreach (var invalid in new[] { 0d, -1, double.NaN, double.PositiveInfinity })
                {
                    Throws<ArgumentException>(() => (s with { NoteTravelTimeMs = invalid }).EnsureValid());
                    Throws<ArgumentException>(() => (s with { NoteThickness = invalid }).EnsureValid());
                }
                (s with { NoteThickness = 0.5 }).EnsureValid();
                (s with { NoteThickness = 1.25 }).EnsureValid();
                // Both boundaries of each pair are given, so these stay out of order whatever the initial values are.
                Throws<ArgumentException>(() => (s with { MaximumTileCount = 8, SixKeyBoundaryMs = 30, EightKeyBoundaryMs = 50 }).EnsureValid());
                Throws<ArgumentException>(() => (s with { FourKeyBoundaryMs = 120, TwoKeyBoundaryMs = 49 }).EnsureValid());
                Throws<ArgumentException>(() => (s with { ResetKey = "J" }).EnsureValid());
                Throws<ArgumentException>(() => (s with { InitialTile = 9 }).EnsureValid());
            }),
            ("Pattern validation and independent custom patterns", () =>
            {
                var s = new VisualizerSettings();
                foreach (var invalid in new[] { "", "1 2 3", "1 2 3 9", "1 1 2 3", "1 2 3 a", "1 2 3 4 5" })
                    Throws<ArgumentException>(() => (s with { FourKeyPattern = invalid }).EnsureValid());
                var custom = s with { FourKeyPattern = "8  2 7 1", TwoKeyPattern = "3 6", InitialTile = 1, DynamicTiming = false };
                custom.EnsureValid();
                // Pattern text chooses the tiles; the order is the default (highest first) unless the tile preference is enabled.
                Sequence(custom.GetPattern(4), 8, 7, 2, 1);
                Sequence(custom.GetPattern(2), 6, 3);
                Sequence(custom.GetPattern(1), 1); // The 1K pattern is the initial tile.
                Sequence((custom with { FourKeyOrder = "4 2 3 1" }).GetPattern(4), 8, 7, 2, 1); // Ignored while disabled.
                var preferred = custom with { EnableTilePreference = true };
                Sequence((preferred with { FourKeyOrder = "4 2 3 1" }).GetPattern(4), 8, 2, 7, 1);
                Sequence((preferred with { TwoKeyOrder = "1 2" }).GetPattern(2), 3, 6);
                Sequence(s.GetPattern(4), 6, 5, 4, 3); Sequence(s.GetPattern(2), 5, 4); Sequence(s.GetPattern(8), 8, 7, 6, 5, 4, 3, 2, 1);
                Equal(s.TwoKeyOrder, "2 1"); Equal(s.FourKeyOrder, "4 3 2 1"); Equal(s.EightKeyOrder, "8 7 6 5 4 3 2 1");
                Assert(!s.EnableTilePreference, "The tile preference defaults off");
                foreach (var invalid in new[] { "", "4 3 2", "4 3 2 2", "5 3 2 1", "0 1 2 3", "a b c d", "4 3 2 1 1", "-1 2 3 4" })
                {
                    Throws<ArgumentException>(() => (s with { EnableTilePreference = true, FourKeyOrder = invalid }).EnsureValid());
                    (s with { FourKeyOrder = invalid }).EnsureValid(); // Not checked while disabled.
                }
                Throws<ArgumentException>(() => (s with { EnableTilePreference = true, TwoKeyOrder = "2 2" }).EnsureValid());
                Throws<ArgumentException>(() => (s with { EnableTilePreference = true, EightKeyOrder = "1 2 3 4 5 6 7 9" }).EnsureValid());
                Assert(!s.HasSameNoteRulesAs(s with { EnableTilePreference = true, FourKeyOrder = "1 2 3 4" }), "Tile preference changes regenerate notes");
                Assert(s.HasSameNoteRulesAs(s with { FourKeyOrder = "1 2 3 4" }), "Order text is ignored while the preference is off");
                Equal(s.EightKeyPattern, "1 2 3 4 5 6 7 8"); Equal(s.FourKeyPattern, "3 4 5 6"); Equal(s.TwoKeyPattern, "4 5");
                Equal(VisualizerSettings.SortTiles(" 6  3 5 4 "), "3 4 5 6"); Equal(VisualizerSettings.SortTiles("6 x"), "6 x");
                Assert(s.HasSameNoteRulesAs(s with { FourKeyPattern = "6 5 4 3", TwoKeyPattern = "5  4" }), "The same tiles in another order are the same rule");
                Sequence((s with { FourKeyPattern = "5 3 6 4" }).GetPattern(4), 6, 5, 4, 3);
                Assert(!s.HasSameNoteRulesAs(s with { FourKeyPattern = "3 4 5 7", EightKeyPattern = "1 2 3 4 5 6 7 8" }), "Different tiles regenerate notes");
                Assert((s with { InitialTile = 1 }).ValidateFields().Any(e => e.Field == "InitialTile"), "A dynamic 1K tile must be visible");
                Sequence(new NoteGenerator().Generate(new[] { 0d, 1000, 2000, 3000 },
                    new() { DynamicTiming = false, SimultaneousWindowMs = 0, EnableTilePreference = true, FourKeyOrder = "1 2 3 4", InitialTile = 3 }).Select(n => n.Tile), 3, 4, 5, 6);
            }),
            ("Exact dynamic thresholds and maximum cap", () =>
            {
                // Boundaries are pinned so editing DefaultSettings.jsonc does not change this rule check.
                var s = new VisualizerSettings { MaximumTileCount = 8, MinimumTileCount = 1, EightKeyBoundaryMs = 20, SixKeyBoundaryMs = 30,
                    FourKeyBoundaryMs = 50, TwoKeyBoundaryMs = 500 };
                var selector = new DynamicTimingSelector(s);
                // 6K sits between 8K and 4K: 8K below 20ms, 6K below 30ms, 4K below 50ms.
                var intervals = new[] { 0d, 19.999, 20, 29.999, 30, 49.999, 50, 499.999, 500, 1000 };
                Sequence(intervals.Select(selector.Select), 8, 8, 6, 6, 4, 4, 2, 2, 1, 1);
                foreach (var cap in new[] { 1, 2, 4, 6, 8 })
                    Equal(new DynamicTimingSelector(s with { MaximumTileCount = cap }).Select(10), cap);
                Equal(new DynamicTimingSelector(s with { EightKeyBoundaryMs = 10 }).Select(10), 6);
                Throws<ArgumentOutOfRangeException>(() => selector.Select(-1));
                Throws<ArgumentOutOfRangeException>(() => selector.Select(double.NaN));
            }),
            ("Dynamic OFF ignores intervals", () =>
            {
                foreach (var cap in new[] { 1, 2, 4, 8 })
                    Equal(new DynamicTimingSelector(new() { MaximumTileCount = cap, DynamicTiming = false }).Select(double.NaN), cap);
                // Trills off: this is about the cycle, and two notes 10ms apart would otherwise be split onto a pair.
                var notes = new NoteGenerator().Generate(new[] { 0d, 10, 1000, 2000 },
                    new() { DynamicTiming = false, SimultaneousWindowMs = 0, TrillWindowMs = 0 });
                Sequence(notes.Select(n => n.Tile), 5, 4, 3, 6);
            }),
            ("Initial rotation, fallback and full rotated cycle", () =>
            {
                var s = new VisualizerSettings { MaximumTileCount = 8 };
                var four = new TilePatternCursor(s, 4);
                Sequence(Enumerable.Range(0, 8).Select(_ => four.Next(4)).ToArray(), 5, 4, 3, 6, 5, 4, 3, 6);
                var eight = new TilePatternCursor(s, 8);
                Sequence(Enumerable.Range(0, 8).Select(_ => eight.Next(8)).ToArray(), 5, 4, 3, 2, 1, 8, 7, 6);
                var absent = new TilePatternCursor(s with { InitialTile = 1 }, 4);
                Equal(absent.Next(4), 6);
                var rotated = new TilePatternCursor(s, 4);
                Sequence(new[] { rotated.Next(4), rotated.Next(8), rotated.Next(8), rotated.Next(8), rotated.Next(8) }, 5, 4, 3, 6, 8);
            }),
            ("Pending change waits, persists or cancels at boundary", () =>
            {
                var s = new VisualizerSettings { MaximumTileCount = 8, InitialTile = 6, SimultaneousWindowMs = 0 };
                var cursor = new TilePatternCursor(s, 4);
                Equal(cursor.Next(4), 6); Equal(cursor.Next(4), 5);
                Equal(cursor.Next(8), 4); Equal(cursor.PendingMode, (int?)8);
                Equal(cursor.Next(8), 3); Equal(cursor.CurrentMode, 4);
                Equal(cursor.Next(8), 8); Equal(cursor.CurrentMode, 8);
                var cancelled = new TilePatternCursor(s, 4);
                Sequence(new[] { cancelled.Next(4), cancelled.Next(4), cancelled.Next(8), cancelled.Next(8), cancelled.Next(4) }, 6, 5, 4, 3, 6);
                Equal(cancelled.PendingMode, null);
                // A request for fewer tiles replaces a pending increase and takes effect at once.
                var replaced = new TilePatternCursor(s, 4);
                Sequence(new[] { replaced.Next(4), replaced.Next(8), replaced.Next(2), replaced.Next(2), replaced.Next(2) }, 6, 5, 4, 5, 4);
                Equal(replaced.CurrentMode, 2);
            }),
            ("Decrease switches at once, and never repeats the tile just played", () =>
            {
                // User rule: fewer tiles do not wait for the running cycle to end.
                var cursor = new TilePatternCursor(new() { MaximumTileCount = 8, InitialTile = 8 }, 8);
                Sequence(Enumerable.Range(0, 8).Select(_ => cursor.Next(4)).ToArray(), 6, 5, 4, 3, 6, 5, 4, 3);
                Equal(cursor.Next(4), 6);
                // The 2K pattern starts at 5; the tile just played was 6, so nothing repeats.
                Sequence(new[] { cursor.Next(2), cursor.Next(2), cursor.Next(2), cursor.Next(2) }, 5, 4, 5, 4);
                Equal(cursor.Next(1), 8); Equal(cursor.CurrentMode, 1); // The 1K pattern is the initial tile.
                // Leaving 4K on tile 5 would repeat it, so the 2K pattern starts one tile later.
                var repeat = new TilePatternCursor(new() { MaximumTileCount = 8, InitialTile = 6 }, 4);
                Sequence(new[] { repeat.Next(4), repeat.Next(4), repeat.Next(2), repeat.Next(2), repeat.Next(2) }, 6, 5, 4, 5, 4);
                // One tile cannot avoid a repeat: 1K keeps its own tile.
                var single = new TilePatternCursor(new() { MaximumTileCount = 4, InitialTile = 5 }, 2);
                Sequence(new[] { single.Next(2), single.Next(1), single.Next(1) }, 5, 5, 5);
            }),
            ("Generator uses preceding interval and first pair", () =>
            {
                // The boundaries are pinned to the values this case was written for (30ms is 4K, 10ms is 8K).
                var s = new VisualizerSettings { MaximumTileCount = 8, InitialTile = 6, SimultaneousWindowMs = 0,
                    EightKeyBoundaryMs = 20, SixKeyBoundaryMs = 30, FourKeyBoundaryMs = 80, TwoKeyBoundaryMs = 500 };
                var notes = new NoteGenerator().Generate(new[] { 0d, 30, 40, 50, 60, 70 }, s);
                Sequence(notes.Select(n => n.Tile), 6, 5, 4, 3, 8, 7);
                Sequence(notes.Select(n => n.Mode), 4, 4, 4, 4, 8, 8);
                Equal(new NoteGenerator().Generate(new[] { 0d }, s)[0].Mode, 1);
                Equal(new NoteGenerator().Generate(Array.Empty<double>(), s).Count, 0);
                Throws<ArgumentException>(() => new NoteGenerator().Generate(new[] { 1d, 0 }, s));
                Throws<ArgumentException>(() => new NoteGenerator().Generate(new[] { double.NaN }, s));
                Equal(new NoteGenerator().Generate(new[] { 0d, 0 }, s)[0].Mode, 1);
            }),
            ("2K to 4K anticipates the close run instead of opening one more 2K cycle", () =>
            {
                // The boundaries are pinned to the values this case was written for (300ms is 2K, 30ms is 4K).
                var settings = new VisualizerSettings { SimultaneousWindowMs = 0,
                    EightKeyBoundaryMs = 20, SixKeyBoundaryMs = 30, FourKeyBoundaryMs = 80, TwoKeyBoundaryMs = 500 };
                var generator = new NoteGenerator();
                // User report (Plum - R): lanes "4 3 2 1 4 3 2 1" were shown as "3 2 4 3 2 1 4 3 2 1".
                var times = new[] { 0d, 300, 600, 630, 660, 690, 720, 750, 780, 810 };
                var notes = generator.Generate(times, settings);
                Sequence(notes.Select(n => n.Tile), 5, 4, 6, 5, 4, 3, 6, 5, 4, 3);
                Sequence(notes.Select(n => n.Mode), 2, 2, 4, 4, 4, 4, 4, 4, 4, 4);
                Assert(notes.Select(n => n.HitTimeMs).SequenceEqual(times), "Hit times must remain unchanged");
                // Mid-cycle the faster pattern also starts on the note before the close run: the 2K cycle is not
                // opened again just to be completed. Its first tile would bring 5 straight back (5 6-5), so it goes on
                // from 5 instead (user rule: 3 4 3 reads 3 2-1). The 30ms pairs are trills (4-3, 6-5) and the single after
                // the 6-5 trill sits beside it on the side it rolled to, 4.
                Sequence(generator.Generate(new[] { 0d, 300, 330, 360, 390, 420 }, settings).Select(n => n.Tile), 5, 4, 3, 6, 5, 4);
                // 4K -> 8K keeps the preceding-interval rule.
                var eight = settings with { MaximumTileCount = 8, InitialTile = 6 };
                Sequence(generator.Generate(new[] { 0d, 30, 40, 50, 60, 70 }, eight).Select(n => n.Mode), 4, 4, 4, 4, 8, 8);
                Sequence(generator.Generate(new[] { 0d, 30, 60, 70, 80, 90, 100, 110, 120, 130 }, eight).Select(n => n.Mode), 4, 4, 4, 4, 8, 8, 8, 8, 8, 8);
            }),
            ("Color settings accept HEX and RGB forms, normalize both ways and name invalid colors", () =>
            {
                var s = new VisualizerSettings();
                // Design colors are written as "R, G, B"; every accepted form still parses.
                Equal(s.NoteColor, "94, 218, 242"); Equal(s.ChordColor, "74, 222, 128"); Equal(s.JudgmentLineColor, "255, 209, 102");
                Equal(s.MissEffectColor, "255, 72, 72"); // The hit glow uses the judgment line color.
                Equal(VisualizerSettings.ParseColor(s.NoteColor), 0xFF5EDAF2u);
                Equal(ColorText.ToRgb(0xFF5EDAF2u), "94, 218, 242"); Equal(ColorText.NormalizeRgb("#5EDAF2"), "94, 218, 242");
                // Lane backgrounds follow the theme by default (empty setting); own colors are 8 lanes. One press color serves every lane.
                Equal(s.LaneBackgroundColors, ""); Equal(s.GetLaneBackgroundColors().Length, 0); Equal(s.GetLaneEffectColor(), 0xFF8CD2FFu);
                Equal((s with { LaneBackgroundColors = "#00FFFFFF #08FFFFFF #00FFFFFF #08FFFFFF #00FFFFFF #08FFFFFF #00FFFFFF #08FFFFFF" }).GetLaneBackgroundColors()[1], 0x08FFFFFFu);
                foreach (var (text, hex) in new[] { ("#5edaf2", "#5EDAF2"), ("5EDAF2", "#5EDAF2"), ("#abc", "#AABBCC"), ("#80FF0000", "#80FF0000"),
                    ("rgb(94, 218, 242)", "#5EDAF2"), ("RGBA(255,0,0,0.5)", "#80FF0000"), ("255, 0, 0", "#FF0000"), (" #FFFFFFFF ", "#FFFFFF") })
                    Equal(ColorText.Normalize(text), hex);
                foreach (var bad in new[] { "", "#12", "#GGGGGG", "rgb(256, 0, 0)", "rgb(1, 2)", "rgba(1, 2, 3, 2)", "1 2 3", "red", null })
                    Equal(ColorText.Normalize(bad), null);
                Assert(ColorText.SplitList("rgb(1, 2, 3) #FFF  #000000").SequenceEqual(new[] { "rgb(1, 2, 3)", "#FFF", "#000000" }), "Lists keep rgb() together");
                var fields = (s with { NoteColor = "blue", LaneEffectColor = "#FFFF" }).ValidateFields().Select(e => e.Field).ToArray();
                Assert(fields.Contains("NoteColor") && fields.Contains("LaneEffectColor"), "Invalid colors are named");
                Assert(s.HasSameNoteRulesAs(s with { NoteColor = "#FF0000", LaneBackgroundColors = "#000 #000 #000 #000 #000 #000 #000 #000" }), "Colors never regenerate notes");
            }),
            ("Minimum tile count floors dynamic modes; minimum equal to maximum matches dynamic off", () =>
            {
                var s = new VisualizerSettings { SimultaneousWindowMs = 0 };
                foreach (var invalid in new[] { 0, 3, 16 }) Throws<ArgumentException>(() => (s with { MinimumTileCount = invalid }).EnsureValid());
                var above = (s with { MinimumTileCount = 8 }).ValidateFields().Select(e => e.Field).ToArray();
                Assert(above.Contains(nameof(VisualizerSettings.MinimumTileCount)) && above.Contains(nameof(VisualizerSettings.MaximumTileCount)),
                    "A minimum above the maximum names both settings");
                var min2 = s with { MinimumTileCount = 2 };
                min2.EnsureValid();
                Assert(!min2.IsThresholdEnabled(2) && min2.IsThresholdEnabled(4), "The 2K/1K threshold is unused");
                Equal(new DynamicTimingSelector(min2).Select(5000), 2);
                var times = new[] { 0d, 900, 1800, 1850, 1900, 1950, 2000, 2600, 3500, 3520, 3540, 3560, 4400, 5400 };
                Assert(new NoteGenerator().Generate(times, min2).All(n => n.Mode >= 2), "No 1K pattern with minimum 2");
                Assert(!s.HasSameNoteRulesAs(min2), "Minimum changes regenerate notes");
                foreach (var count in new[] { 1, 2, 4, 8 })
                {
                    var same = new VisualizerSettings { SimultaneousWindowMs = 0, MaximumTileCount = count, MinimumTileCount = count };
                    same.EnsureValid();
                    Assert(!same.IsThresholdEnabled(2) && !same.IsThresholdEnabled(4) && !same.IsThresholdEnabled(8), "No threshold is used");
                    var dynamic = new NoteGenerator().Generate(times, same);
                    var off = new NoteGenerator().Generate(times, same with { DynamicTiming = false, MinimumTileCount = 1 });
                    Assert(dynamic.SequenceEqual(off), $"Minimum = maximum = {count} must equal dynamic timing off");
                }
                // Thresholds the minimum makes unused are not order-checked.
                (s with { MinimumTileCount = 4, TwoKeyBoundaryMs = 10 }).EnsureValid();
            }),
            ("Only 1K to 2K and 2K to 4K anticipate the close run", () =>
            {
                // The downshift wait and the boundaries are pinned so this anticipation check does not depend on the initial settings.
                var settings = new VisualizerSettings
                {
                    MaximumTileCount = 8, InitialTile = 3, SimultaneousWindowMs = 0, DownshiftNoteCount = 1,
                    EightKeyBoundaryMs = 20, SixKeyBoundaryMs = 30, FourKeyBoundaryMs = 80, TwoKeyBoundaryMs = 500,
                    TwoKeyPattern = "3 2", FourKeyPattern = "3 2 1 4", EnableTilePreference = true, FourKeyOrder = "3 2 1 4"
                };
                var generator = new NoteGenerator();
                var times = new[] { 0d, 600, 700, 800, 900, 1000 };
                foreach (var cap in new[] { 2, 4, 8 })
                {
                    var notes = generator.Generate(times, settings with { MaximumTileCount = cap });
                    // The 2K pattern opens on its second tile, because its first would repeat the 1K note (user rule: 3 3 2 3 2
                    // asked for the same key twice in a row).
                    Sequence(notes.Select(n => n.Tile), 3, 2, 3, 2, 3, 2);
                    Sequence(notes.Select(n => n.Mode), 1, 2, 2, 2, 2, 2);
                    Assert(notes.Select(n => n.HitTimeMs).SequenceEqual(times), "Hit times must remain unchanged");
                    Assert(notes.SequenceEqual(generator.Generate(times, settings with { MaximumTileCount = cap })), "Regeneration must be deterministic");
                }
                // An isolated close pair completes its cycle, then returns to 1K.
                Sequence(generator.Generate(new[] { 0d, 600, 700, 1300 }, settings).Select(n => n.Tile), 3, 2, 3, 3);
                Sequence(generator.Generate(new[] { 0d, 600, 700, 1300 }, settings).Select(n => n.Mode), 1, 2, 2, 1);
                // Exact threshold and end of input must not trigger anticipation.
                Sequence(generator.Generate(new[] { 0d, 600, 1100 }, settings).Select(n => n.Mode), 1, 1, 1);
                Sequence(generator.Generate(new[] { 0d, 600 }, settings).Select(n => n.Mode), 1, 1);
                // Direct 1K -> 4K/8K still starts on the second member.
                foreach (var (gap, mode) in new[] { (30d, 4), (10d, 8) })
                    Sequence(generator.Generate(new[] { 0d, 600, 600 + gap }, settings).Select(n => n.Mode), 1, 1, mode);
                Sequence(generator.Generate(times, settings with { MaximumTileCount = 2, DynamicTiming = false }).Select(n => n.Tile), 3, 2, 3, 2, 3, 2);
                Sequence(generator.Generate(times, settings with { MaximumTileCount = 1 }).Select(n => n.Tile), 3, 3, 3, 3, 3, 3);
                Sequence(generator.Generate(new[] { 0d, 100 }, settings with { InitialTile = 2 }).Select(n => n.Tile), 2, 3);
            }),
            ("Reset freezes, Start preserves placement, restart is deterministic", () =>
            {
                var clock = new FakeClock { NowMs = 10000 };
                var session = new PlaybackSession(clock);
                Assert(!session.Start(), "Cannot start before loading a timeline");
                var settings = new VisualizerSettings { DynamicTiming = false };
                session.LoadTimeline(new[] { 1000d, 1100, 1200, 1300 }, settings);
                session.Toggle();
                Equal(session.State, PlaybackState.Waiting);
                Equal(session.TimelineTimeMs, 0d);
                var before = session.Notes;
                clock.NowMs += 7000;
                Equal(session.TimelineTimeMs, 0d);
                Assert(session.Start(), "Start must succeed");
                Assert(ReferenceEquals(before, session.Notes), "Start must not regenerate notes");
                Equal(session.TimelineTimeMs, 0d);
                clock.NowMs += 1000;
                Equal(session.TimelineTimeMs, 1000d);
                Assert(!session.Start(), "Repeated start must not reset clock");
                Equal(session.TimelineTimeMs, 1000d);
                session.Reset(settings);
                Equal(session.State, PlaybackState.Waiting);
                Equal(session.TimelineTimeMs, 0d);
                Sequence(session.Notes.Select(n => n.Tile), 5, 4, 3, 6);
                session.Clear(); Equal(session.State, PlaybackState.NoTimeline);
            }),
            ("Absolute position has no frame drift", () =>
            {
                Equal(NotePosition.Progress(5000, 2000, 3000), 0d);
                Equal(NotePosition.Progress(5000, 3500, 3000), 0.5d);
                Equal(NotePosition.Progress(5000, 5000, 3000), 1d);
                var clock = new FakeClock();
                var session = new PlaybackSession(clock);
                session.LoadTimeline(new[] { 5000d, 8000 }, new()); session.Toggle(); session.Start();
                foreach (var t in new[] { 1d, 16.7, 1200.1, 2999.9, 3000 })
                {
                    clock.NowMs = t;
                    Equal(session.TimelineTimeMs, t);
                }
                Equal(NotePosition.Progress(session.Notes[1].HitTimeMs, session.TimelineTimeMs, 3000), 1d);
            }),
            ("Read-only fixture, traversal and cancellation", () =>
            {
                var root = Path.Combine(artifactRoot, "fixtures", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, "example.adofai");
                const string sourceText = "{\"testOnly\":true}";
                File.WriteAllText(path, sourceText); // Test-owned workspace fixture only.
                var modified = File.GetLastWriteTimeUtc(path);
                var source = new ReadOnlyDirectoryMapSource(root);
                Equal(source.ReadAsync("example.adofai").GetAwaiter().GetResult().Text, sourceText);
                Equal(File.ReadAllText(path), sourceText); Equal(File.GetLastWriteTimeUtc(path), modified);
                Equal(Directory.GetFiles(root).Length, 1);
                foreach (var invalid in new[] { "../escape.adofai", path, "file.txt", "example.adofai:stream" })
                    Throws<ArgumentException>(() => source.ReadAsync(invalid).GetAwaiter().GetResult());
                Throws<OperationCanceledException>(() => source.ReadAsync("example.adofai", new CancellationToken(true)).GetAwaiter().GetResult());
            }),
            ("Checkpoint and settings application", () =>
            {
                var model = new MainViewModel();
                Equal(model.Checkpoint, 1); model.Checkpoint = 3;
                Throws<ArgumentOutOfRangeException>(() => model.Checkpoint = 0);
                model.LoadPreview(); model.Start();
                model.ApplySettings(model.Settings with { MaximumTileCount = 2, DynamicTiming = false, InitialTile = 4, SimultaneousWindowMs = 0 });
                Equal(model.Playback.State, PlaybackState.Waiting); Equal(model.Playback.Notes[0].Tile, 4);
            }),
            ("WPF windows and rendered preview", () =>
            {
                Directory.CreateDirectory(artifactRoot);
                var main = new MainWindow();
                ((MainViewModel)main.DataContext).LoadPreview();
                Capture(main, Path.Combine(artifactRoot, "main-window.png"), 960, 640);
                var settings = new SettingsPanel(new());
                Capture(settings, Path.Combine(artifactRoot, "settings-window.png"), 600, 760);
                ((DockPanel)settings.Content).Children.OfType<ScrollViewer>().Single().ScrollToEnd();
                Capture(settings, Path.Combine(artifactRoot, "settings-window-bottom.png"), 600, 760);
                main.Close();
            })
        }.Concat(ExtendedTests.Create(artifactRoot)).Concat(PlumExampleTests.Create(artifactRoot))
            .Concat(SimultaneousNoteTests.Create(artifactRoot))
            .Concat(EnvironmentCompatibilityTests.Create(artifactRoot))
            .Concat(CheckpointControlTests.Create())
            .Concat(RhythmVisualTests.Create(artifactRoot))
            .Concat(PracticeSectionTests.Create(artifactRoot))
            .Concat(AdvancedFeatureTests.Create(artifactRoot))
            .Concat(ExtraLaneTests.Create(artifactRoot))
            .Concat(SixKeyTests.Create(artifactRoot))
            .Concat(LocalizationThemeTests.Create(artifactRoot))
            .Concat(CmdConsoleTests.Create(artifactRoot))
            .Concat(LongNoteTests.Create(artifactRoot))
            .Concat(DesignAndTimingTests.Create(artifactRoot))
            .Concat(CheckpointLayoutTests.Create(artifactRoot))
            .Concat(SettingsStoreTests.Create(artifactRoot))
            .Concat(SongListTests.Create(artifactRoot))
            .Concat(NoteEditTests.Create(artifactRoot))
            .Concat(GameSyncTests.Create(artifactRoot))
            .Concat(MapCompatibilityTests.Create(artifactRoot, args.Length >= 3 ? args[2] : null, args.Length == 4)).ToArray();
        var failed = 0;
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} test groups passed.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Runs what the app put off to a later frame (a slide that waits for the layout, say). A test has no message loop, so
    /// the queue is emptied by hand up to the priority the app used.
    /// </summary>
    internal static void Pump(System.Windows.Threading.DispatcherPriority priority = System.Windows.Threading.DispatcherPriority.Background)
    {
        // The classic message pump: a frame that ends once everything queued above this priority has run.
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(priority, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    internal static void Capture(Window window, string path, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        content.DataContext = window.DataContext;
        Capture(content, path, width, height, window.Background);
    }

    /// <summary>
    /// The same for something that is not a window of its own (the settings panel), which is drawn where it sits.
    /// </summary>
    internal static void Capture(FrameworkElement content, string path, int width, int height, Brush? background = null)
    {
        // Render the content without showing or launching a desktop window.
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var plate = new DrawingVisual();
        using (var drawing = plate.RenderOpen())
            drawing.DrawRectangle(background ?? (content as Control)?.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
        image.Render(plate);
        image.Render(content);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        png.Save(stream);
    }
}
