using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Visualizer.App.Rendering;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

/// <summary>
/// Magic circle sections and their timing bar, the one-time press calibration, the note and long note
/// width scales with the transparency of a hit note, and the single lane press color.
/// </summary>
internal static class DesignAndTimingTests
{
    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }

    private static IReadOnlyList<VisualizerNote> Run(IEnumerable<double> times) =>
        times.Select(time => new VisualizerNote(time, 5, 1)).ToList();

    public static IEnumerable<(string Name, Action Run)> Create(string artifactRoot)
    {
        yield return ("Magic circle sections are runs of one interval, off unless the bar is switched on", () =>
        {
            var on = new VisualizerSettings { MagicCircleJudgmentLine = true, MagicCircleNoteCount = 16 };
            var steady = Run(Enumerable.Range(0, 20).Select(i => i * 100d));
            Assert(MagicCircleFinder.Find(steady, new VisualizerSettings()).Count == 0, "The bar is off by default");
            var found = MagicCircleFinder.Find(steady, on).Single();
            Equal(found.StartMs, 0d); Equal(found.EndMs, 1900d); Equal(found.IntervalMs, 100d);
            Assert(found.Contains(1000) && !found.Contains(1900.001), "A section covers its own notes");
            // Intervals within ±5ms are the same interval; a longer gap ends the run.
            Assert(MagicCircleFinder.Find(Run(Enumerable.Range(0, 20).Select(i => i * 100d + (i % 2 == 0 ? 0 : 2))), on).Count == 1, "Small wobble stays one run");
            Assert(MagicCircleFinder.Find(Run(Enumerable.Range(0, 15).Select(i => i * 100d)), on).Count == 0, "A short run is not a magic circle");
            // A run of 20 at 100ms, then ten notes at 250ms: only the first is long enough.
            var split = Enumerable.Range(0, 20).Select(i => i * 100d).Concat(Enumerable.Range(1, 10).Select(i => 1900 + i * 250d));
            Equal(MagicCircleFinder.Find(Run(split), on).Count, 1);
            Equal(MagicCircleFinder.Find(Run(split), on with { MagicCircleNoteCount = 20 }).Count, 1);
            Equal(MagicCircleFinder.Find(Run(split), on with { MagicCircleNoteCount = 21 }).Count, 0);
            // A chord shares one display time, so it counts as one note of the run.
            var chord = steady.Take(19).Append(new VisualizerNote(1800, 4, 2, true)).OrderBy(n => n.HitTimeMs).ToList();
            Equal(MagicCircleFinder.Find(chord, on).Single().IntervalMs, 100d);
            // Chords in magic circles (user rule): on by default. Off, chords and singles never share a run, so a chord in
            // the middle of 20 singles leaves two runs too short to count...
            Equal(new VisualizerSettings().MagicCircleChords, true);
            var apart = on with { MagicCircleChords = false };
            var middle = Enumerable.Range(0, 20).Select(i => new VisualizerNote(i * 100d, 1 + i % 4, 4, i == 10))
                .Append(new VisualizerNote(1000, 2, 4)).OrderBy(n => n.HitTimeMs).ToList();
            Equal(MagicCircleFinder.Find(middle, on).Count, 1);
            Equal(MagicCircleFinder.Find(middle, apart).Count, 0);
            // ...while a run of chords alone still is one, each chord being one note.
            var chordsOnly = Enumerable.Range(0, 20).SelectMany(i => new[] { new VisualizerNote(i * 100d, 1, 4, true), new VisualizerNote(i * 100d, 2, 4) }).ToList();
            Equal(MagicCircleFinder.Find(chordsOnly, apart).Single().IntervalMs, 100d);
            // The same marks while notes are prepared: one per display time.
            var groupTimes = Enumerable.Range(0, 20).Select(i => i * 100d).ToArray();
            var groupChords = Enumerable.Range(0, 20).Select(i => i == 10).ToArray();
            Equal(MagicCircleFinder.Find(groupTimes, apart, groupChords).Count, 0);
            Equal(MagicCircleFinder.Find(groupTimes, on, groupChords).Count, 1);
            Throws<ArgumentException>(() => MagicCircleFinder.Find(groupTimes, apart, [true]));
            foreach (var invalid in new[] { 15, 0, -1 })
                Throws<ArgumentException>(() => (on with { MagicCircleNoteCount = invalid }).EnsureValid());
            foreach (var invalid in new[] { -1d, double.NaN })
                Throws<ArgumentException>(() => (on with { MagicCircleLeadMs = invalid }).EnsureValid());
            // The bar itself is visual; only holding the tile count changes the notes.
            var visual = on with { MagicCircleFixedTiles = false };
            Assert(visual.HasSameNoteRulesAs(visual with { MagicCircleNoteCount = 99, MagicCircleLeadMs = 0, MagicCircleJudgmentLine = false }),
                "The bar never changes the notes");
            Assert(!visual.HasSameNoteRulesAs(visual with { MagicCircleFixedTiles = true }), "Holding the tile count is a note rule");
            Assert(visual.HasSameNoteRulesAs(visual with { MagicCircleChords = false }), "Without fixed tiles, which runs count is visual");
            var fixedTiles = on with { MagicCircleFixedTiles = true };
            Assert(!fixedTiles.HasSameNoteRulesAs(fixedTiles with { MagicCircleChords = false }), "With fixed tiles it moves notes");

            // The song info counts the sections, but only while the bar is switched on.
            var model = new Visualizer.App.ViewModels.MainViewModel();
            var run = Enumerable.Range(0, 20).Select(i => i * 100d).ToArray();
            model.ApplySettings(model.Settings with
            {
                DynamicTiming = false, SimultaneousWindowMs = 0, MagicCircleJudgmentLine = true, MagicCircleNoteCount = 16
            });
            model.Playback.LoadTimeline(run, model.Settings);
            Assert(model.OverlayTiles.Contains("마법진 1개"), "The count follows the tiles: " + model.OverlayTiles);
            model.ApplySettings(model.Settings with { MagicCircleJudgmentLine = false });
            Assert(!model.OverlayTiles.Contains("마법진"), "Nothing is shown while the bar is off: " + model.OverlayTiles);
        });

        yield return ("Tiles from the next gap, and the two placement rules being tried out", () =>
        {
            // Note by note: the 15 and 30ms runs below would otherwise be trills counted as one note each.
            var wide = new VisualizerSettings { MaximumTileCount = 8, TrillAsOneNote = false };
            wide.EnsureValid();
            // Five notes 15 ms apart (8K) and then a long gap. The last note of the run is the one the two readings
            // disagree about: what is coming is slow, although what it came from was fast. Going up still waits for the
            // cycle boundary either way, so this is where the choice actually shows.
            var quick = wide with { DownshiftNoteCount = 1 };
            double[] times = [0, 15, 30, 45, 60, 560, 1060];
            var before = new NoteGenerator().Generate(times, quick);
            var after = new NoteGenerator().Generate(times, quick with { NextIntervalTiles = true });
            Assert(after[4].Mode < before[4].Mode,
                $"The run ends on the slower count: {string.Join(",", before.Select(n => n.Mode))} then {string.Join(",", after.Select(n => n.Mode))}");

            // Three bursts of three, each after a gap: with the rule on, every burst reads the same way.
            double[] bursts = [0, 30, 60, 400, 430, 460, 800, 830, 860];
            // The first burst starts the song, so it keeps the Initial Tile; every burst after it reads the same way.
            var restart = new NoteGenerator().Generate(bursts, wide with { ExperimentalFeatures = true, RestartPatternOnRun = true });
            Sequence(restart.Skip(6).Take(3).Select(n => n.Tile), restart.Skip(3).Take(3).Select(n => n.Tile).ToArray());
            // Off it is the long-standing behaviour, so the rule has to be asked for.
            Assert(!(wide with { RestartPatternOnRun = true }).UseRestartOnRun, "Experiments apply only while they are on");

            // A single, then a chord: with the rule on the single keeps off the chord's two lanes.
            double[] chord = [0, 200, 205];
            var plain = new NoteGenerator().Generate(chord, wide);
            var apart = new NoteGenerator().Generate(chord, wide with { ExperimentalFeatures = true, ChordAvoidBeforeLanes = true });
            var pair = new[] { apart[1].Tile, apart[2].Tile };
            Assert(!pair.Contains(apart[0].Tile), $"The single moved off {string.Join("-", pair)}: it is on {apart[0].Tile}");
            Assert(plain.Count == apart.Count, "And no note was gained or lost");
        });

        yield return ("The judgment bar has its own switch, the game's windows and a checked average", () =>
        {
            // The bar used to be a magic circle switch; it is its own choice now (user rule).
            var settings = new VisualizerSettings();
            Equal(settings.JudgmentBarMode, "MagicCircle");
            Equal(settings.JudgmentBarDifficulty, "Normal");
            Assert(settings.ShowJudgmentAverage, "The average arrow is on by default");
            Equal(settings.JudgmentAverageCount, 8);

            // The windows are the game's own counted floors (GCS.HITMARGIN_MINIMUM_SECONDS_EASY/NORMAL/HARD).
            Equal((settings with { JudgmentBarDifficulty = "Lenient" }).JudgmentWindowMs, 91d);
            Equal(settings.JudgmentWindowMs, 65d);
            Equal((settings with { JudgmentBarDifficulty = "Strict" }).JudgmentWindowMs, 40d);

            foreach (var mode in new[] { "Off", "MagicCircle", "Always" }) (settings with { JudgmentBarMode = mode }).EnsureValid();
            foreach (var hard in new[] { "Lenient", "Normal", "Strict" }) (settings with { JudgmentBarDifficulty = hard }).EnsureValid();
            Throws<ArgumentException>(() => (settings with { JudgmentBarMode = "Sometimes" }).EnsureValid());
            Throws<ArgumentException>(() => (settings with { JudgmentBarDifficulty = "Brutal" }).EnsureValid());
            Throws<ArgumentException>(() => (settings with { JudgmentAverageCount = 0 }).EnsureValid());
            Throws<ArgumentException>(() => (settings with { JudgmentAverageCount = 51 }).EnsureValid());
        });

        yield return ("The judgment bar grades as the game does: each tile's tempo, margin scale and difficulty, nine grades", () =>
        {
            // User report: on fast tiles a press inside the game's green was graded late. The game takes each edge as the larger
            // of a fixed angle and a minimum time (scrMisc.GetAdjustedAngleBoundaryInDeg); its bar paints the inner edges at a
            // fixed 0.75 and 0.5 of the counted one, which only holds on slower tiles.
            static void Near(double actual, double expected, string what) =>
                Assert(Math.Abs(actual - expected) < 1e-9, $"{what}: {actual} instead of {expected}");
            var slow = HitGrading.Windows(200, 1, 1);
            Near(slow.CountedMs, 100, "Counted at 200 BPM is 60 degrees");
            Near(slow.PerfectMs, 75, "Perfect at 200 BPM is 45 degrees");
            Near(slow.PureMs, 50, "Pure at 200 BPM is 30 degrees");
            Near(slow.XPerfectMs, 12.5 * 60000 / 200 / 180, "The middle band is 12.5 degrees");
            var fast = HitGrading.Windows(400, 1, 1);
            Near(fast.CountedMs, 65, "Normal's counted minimum");
            Near(fast.PerfectMs, 37.5, "Perfect is still the angle");
            Near(fast.PureMs, 25, "Pure stops at its minimum");
            Near(fast.XPerfectMs, 1000.0 / 60, "The middle band stops at 1/60 s");
            // The report: 30 ms late on such a tile is a late perfect, although it sits inside the half of the bar the game
            // paints green.
            Equal(fast.Grade(30), HitGrade.LatePerfect);
            Assert(30 / fast.CountedMs < 0.5, "Inside the game's painted green");
            // The difficulty only moves the counted minimum; the margin scale only the angles.
            Near(HitGrading.Windows(400, 1, 0).CountedMs, 91, "Lenient");
            Near(HitGrading.Windows(400, 1, 2).CountedMs, 50, "Strict keeps the angle here");
            Near(HitGrading.Windows(400, 1, 0).PureMs, 25, "Lenient leaves perfect alone");
            var scaled = HitGrading.Windows(200, 0.5, 1);
            Near(scaled.CountedMs, 65, "Half the angles");
            Near(scaled.PerfectMs, 37.5, "Half the angles");
            Near(scaled.PureMs, 25, "Half the angles, floored");
            Near(HitGrading.Windows(200, 0, 1).PureMs, 25, "No angle at all leaves the minimum");
            // The edges as scrMisc.SelectHitMarginByTimeBoundary draws them.
            Equal(slow.Grade(0), HitGrade.XPerfect);
            Equal(slow.Grade(slow.XPerfectMs), HitGrade.XPerfect);
            Equal(slow.Grade(-slow.XPerfectMs), HitGrade.PerfectMinus);
            Equal(slow.Grade(slow.XPerfectMs + 0.1), HitGrade.PerfectPlus);
            Equal(slow.Grade(50), HitGrade.PerfectPlus);
            Equal(slow.Grade(50.1), HitGrade.LatePerfect);
            Equal(slow.Grade(75.1), HitGrade.VeryLate);
            Equal(slow.Grade(100), HitGrade.VeryLate);
            Equal(slow.Grade(100.1), HitGrade.TooLate);
            Equal(slow.Grade(-49.9), HitGrade.PerfectMinus);
            Equal(slow.Grade(-50), HitGrade.EarlyPerfect);
            Equal(slow.Grade(-75), HitGrade.VeryEarly);
            Equal(slow.Grade(-100), HitGrade.TooEarly);
            // A press sits in the zone of its own grade on a bar drawn with another tile's windows, and at its deviation over
            // the counted window on its own.
            Near(fast.Place(25, slow), 0.5, "Its perfect edge on the bar's perfect edge");
            Near(fast.Place(-37.5, slow), -0.75, "Its early perfect edge on the bar's");
            Near(fast.Place(51, slow), 0.75 + (51 - 37.5) / (65 - 37.5) * 0.25, "Evenly in between");
            Near(fast.Place(80, slow), 1, "Past the counted window, at the end");
            Near(slow.Place(30, slow), 0.3, "On its own windows, the deviation over the counted window");
            Near(slow.Place(0, fast), 0, "The middle");
            Equal(HitGrading.DifficultyOf("Lenient"), 0);
            Equal(HitGrading.DifficultyOf("Normal"), 1);
            Equal(HitGrading.DifficultyOf("Strict"), 2);
            Throws<ArgumentOutOfRangeException>(() => HitGrading.Windows(0, 1, 1));
            Throws<ArgumentOutOfRangeException>(() => HitGrading.Windows(200, 1, 3));

            // A prepared note knows its tile's windows: the practice speed plays the tile slower, as the game's practice
            // mode does, and the game's difficulty comes before the one in the settings.
            var settings = new VisualizerSettings { StartDelayMs = 0, DynamicTiming = false };
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            session.LoadParsedTimeline(new ParsedTimeline([0, 150, 300], hitFloors: [1, 2, 3], hitTempos: [400, 400, 200]), settings);
            session.Toggle();
            Near(session.HitWindowsOf(0, 1)!.PureMs, 25, "A fast tile");
            Near(session.HitWindowsOf(2, 1)!.PureMs, 50, "A slower one");
            session.GameDifficulty = 0;
            Near(session.HitWindowsOf(0, 1)!.CountedMs, 91, "The game's Lenient");
            session.GameDifficulty = null;
            session.ApplySettings(settings with { PlaybackSpeedPercent = 50 });
            Near(session.HitWindowsOf(2, 1)!.PureMs, 100, "Half speed is half the tempo");
            Equal(session.HitWindowsOf(3, 1), null);
            // A cache from before tempos were kept gets the difficulty's floor with the angle ratios.
            var older = new PlaybackSession(new Clock());
            older.LoadParsedTimeline(new ParsedTimeline([0, 150]), settings);
            older.Toggle();
            Near(older.HitWindowsOf(0, 1)!.CountedMs, 65, "Normal's floor");
            Near(older.HitWindowsOf(0, 1)!.PerfectMs, 48.75, "With the angle ratio");
            Throws<ArgumentException>(() => new ParsedTimeline([0, 150], hitTempos: [400]));
            Throws<ArgumentException>(() => new ParsedTimeline([0, 150], hitTempos: [400, 0]));
            Throws<ArgumentException>(() => new ParsedTimeline([0, 150], hitMarginScales: [1, -1]));

            // The game's own grade goes to the press just made (link). The counts do not say which press got which grade, so
            // they are paired by the grade each press's own timing gives, and never with a press made long before.
            session.ApplySettings(settings);
            var judge = new NotePressJudge();
            session.Presses = judge;
            session.Start();
            HitGrade Expected(PressMark mark) => session.HitWindowsOf(mark.Note, 1)!.Grade(mark.DeviationMs);
            clock.NowMs = 20;
            Assert(judge.Press(session, session.Notes[0].Tile, 150) is 0, "The first note is pressed 20 ms late");
            clock.NowMs = 120;
            Assert(judge.Press(session, session.Notes[1].Tile, 150) is 1, "The second 30 ms early");
            Equal(judge.TakeGameGrades([HitGrade.LatePerfect, HitGrade.EarlyPerfect], 150, 400, Expected), 2);
            Equal(judge.RecentPresses[0].GameGrade, (HitGrade?)HitGrade.LatePerfect);
            Equal(judge.RecentPresses[1].GameGrade, (HitGrade?)HitGrade.EarlyPerfect);
            Equal(judge.RecentPresses[1].Note, 1);
            // User report: in a fast run the game also graded presses this app never matched to a note, and those late extras
            // went to presses near the middle. Extras nowhere near what a press's timing gives are not taken.
            clock.NowMs = 300;
            Assert(judge.Press(session, session.Notes[2].Tile, 150) is 2, "The third on time");
            Equal(judge.TakeGameGrades([HitGrade.TooLate, HitGrade.VeryLate], 310, 400, Expected), 0);
            Equal(judge.RecentPresses[2].GameGrade, null);
            Equal(judge.TakeGameGrades([HitGrade.TooLate, HitGrade.XPerfect, HitGrade.VeryLate], 310, 400, Expected), 1);
            Equal(judge.RecentPresses[2].GameGrade, (HitGrade?)HitGrade.XPerfect);
            Equal(judge.TakeGameGrades([HitGrade.XPerfect], 300 + 500, 400, Expected), 0);

            // Drawn: on a fast tile the green ends where perfect really ends (25 of 65 ms), not at half the bar as the game
            // paints it, so 30 ms late lands in yellow. Bar 280 px wide in a 400 px field: centre 200, 140 px a side.
            var barClock = new Clock();
            var barSession = new PlaybackSession(barClock);
            // Drawn to scale (the fixed bar switched off).
            var barSettings = settings with { JudgmentBarMode = "Always", ShowJudgmentAverage = false, FixedJudgmentBar = false };
            barSession.LoadParsedTimeline(new ParsedTimeline([0, 150, 300, 450], hitFloors: [1, 2, 3, 4], hitTempos: [400, 400, 400, 400]), barSettings);
            barSession.Toggle();
            var barJudge = new NotePressJudge();
            barSession.Presses = barJudge;
            barSession.Start();
            barClock.NowMs = 140;
            barJudge.Press(barSession, barSession.Notes[1].Tile, 150);
            var field = new NoteField { Session = barSession, Judge = barJudge, Width = 400, Height = 600 };
            field.Measure(new Size(400, 600));
            field.Arrange(new Rect(0, 0, 400, 600));
            field.UpdateLayout();
            field.Tick();
            var bitmap = new RenderTargetBitmap(400, 600, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(field);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(artifactRoot, "judgment-bar-fast-tile.png"))) png.Save(stream);
            var pixels = new byte[400 * 600 * 4];
            bitmap.CopyPixels(pixels, 400 * 4, 0);
            (byte R, byte G, byte B) Pixel(int x, int y) { var i = (y * 400 + x) * 4; return (pixels[i + 2], pixels[i + 1], pixels[i]); }
            static bool Green((byte R, byte G, byte B) c) => c.G > c.R + 40 && c.G > c.B + 40;
            static bool Yellow((byte R, byte G, byte B) c) => c.R > 140 && c.G > 120 && c.B < 100;
            static bool Red((byte R, byte G, byte B) c) => c.R > 140 && c.G < 100;
            // The bar's middle row: where its red end is, found rather than worked out from the field's own layout.
            var reds = Enumerable.Range(0, 600).Where(y => Red(Pixel(200 - 130, y))).ToArray();
            Assert(reds.Length > 0, "The bar is drawn");
            var row = reds[reds.Length / 2];
            (byte R, byte G, byte B) At(int x) => Pixel(x, row);
            Assert(Green(At(200 + 45)), $"Perfect at 21 ms: {At(245)}");
            Assert(Yellow(At(200 + 64)), $"Late perfect at 30 ms, inside the half the game paints green: {At(264)}");
            Assert(Red(At(200 + 100)), $"Very late at 46 ms: {At(300)}");
            Assert(Yellow(At(200 - 64)), $"The same on the early side: {At(136)}");

            // Fixed (user request, on by default): the game's picture whatever the tile, 12.5 / 30 / 45 / 60 of a 140 px side,
            // and the ends say the tile's counted window, early negative.
            var fixedClock = new Clock();
            var fixedSession = new PlaybackSession(fixedClock);
            fixedSession.LoadParsedTimeline(new ParsedTimeline([0, 150, 300, 450], hitFloors: [1, 2, 3, 4], hitTempos: [400, 400, 400, 400]),
                barSettings with { FixedJudgmentBar = true });
            fixedSession.Toggle();
            var fixedJudge = new NotePressJudge();
            fixedSession.Presses = fixedJudge;
            fixedSession.Start();
            fixedClock.NowMs = 140;
            fixedJudge.Press(fixedSession, fixedSession.Notes[1].Tile, 150);
            field.Session = fixedSession;
            field.Judge = fixedJudge;
            field.Tick();
            field.InvalidateVisual();
            field.UpdateLayout();
            var fixedBitmap = new RenderTargetBitmap(400, 600, 96, 96, PixelFormats.Pbgra32);
            fixedBitmap.Render(field);
            fixedBitmap.CopyPixels(pixels, 400 * 4, 0);
            Assert(Green(At(200 + 50)) && Green(At(200 - 50)), $"Perfect to half a side: {At(250)} {At(150)}");
            Assert(Yellow(At(200 + 90)) && Yellow(At(200 - 90)), $"Early/late perfect to three quarters: {At(290)} {At(110)}");
            Assert(Red(At(200 + 125)) && Red(At(200 - 125)), $"Early/late past that: {At(325)} {At(75)}");
            var english = barSettings with { FixedJudgmentBar = true, Language = "english" };
            Equal(NoteField.BarLabels(english, HitGrading.Windows(400, 1, 1)), ("Early -65ms", "Late +65ms"));
            Equal(NoteField.BarLabels(english, HitGrading.Windows(400, 1, 0, 1.4)), ("Early -127ms", "Late +127ms"));
            Equal(NoteField.BarLabels(english with { FixedJudgmentBar = false }, HitGrading.Windows(400, 1, 1)), ("Early", "Late"));
            Equal(NoteField.BarLabels(english with { Language = "korean" }, HitGrading.Windows(400, 1, 2)), ("빠름 -50ms", "느림 +50ms")); // 60 degrees at 400 BPM, past Strict's 40 ms
        });

        yield return ("The judgment ease widens the game's minimum times only, and the bar draws what the game applies", () =>
        {
            // User request: for practice, the game's minimum hit times longer by a scale the visualizer sets; the linker
            // multiplies the time constants of scrMisc.GetMinimumTimes, so every minimum grows by it and nothing else does.
            static void Near(double actual, double expected, string what) =>
                Assert(Math.Abs(actual - expected) < 1e-9, $"{what}: {actual} instead of {expected}");
            var eased = HitGrading.Windows(400, 1, 1, 1.4);
            Near(eased.CountedMs, 91, "65 ms times 1.4");
            Near(eased.PerfectMs, 42, "30 ms times 1.4");
            Near(eased.PureMs, 35, "25 ms times 1.4");
            Near(eased.XPerfectMs, 1000.0 / 60, "The middle band's 1/60 s is not one of the minimum times");
            // A slow tile's angles are already wider, so it stays as it was.
            var slow = HitGrading.Windows(200, 1, 1, 1.4);
            Near(slow.CountedMs, 100, "Still 60 degrees");
            Near(slow.PureMs, 50, "Still 30 degrees");
            Near(HitGrading.Fallback(1, 1.4).CountedMs, 91, "The fallback eases too");
            Throws<ArgumentOutOfRangeException>(() => HitGrading.Windows(400, 1, 1, 0.9));
            Throws<ArgumentOutOfRangeException>(() => HitGrading.Windows(400, 1, 1, 3.1));

            // A setting from 1 to 3, off (1) by default.
            var settings = new VisualizerSettings { StartDelayMs = 0, DynamicTiming = false };
            Equal(settings.JudgmentEaseScale, 1d);
            (settings with { JudgmentEaseScale = 3 }).EnsureValid();
            foreach (var invalid in new[] { 0.99, 3.01, double.NaN })
                Throws<ArgumentException>(() => (settings with { JudgmentEaseScale = invalid }).EnsureValid());
            Assert(settings.HasSameNoteRulesAs(settings with { JudgmentEaseScale = 2 }), "It moves no note");

            // The bar uses what the linked game applies; with no game, the setting.
            var session = new PlaybackSession(new Clock());
            session.LoadParsedTimeline(new ParsedTimeline([0, 150], hitFloors: [1, 2], hitTempos: [400, 400]), settings with { JudgmentEaseScale = 1.4 });
            session.Toggle();
            Near(session.HitWindowsOf(0, 1)!.PureMs, 35, "The setting, with no game linked");
            session.GameEase = 1;
            Near(session.HitWindowsOf(0, 1)!.PureMs, 25, "A linked game that applies none");
            session.GameEase = 2;
            Near(session.HitWindowsOf(0, 1)!.PureMs, 50, "The game's own ease");
            session.GameEase = 9;
            Near(session.HitWindowsOf(0, 1)!.PureMs, 35, "Nonsense from the game is not taken");
        });

        yield return ("Fewer tiles wait for repeated slow notes (downshift wait)", () =>
        {
            var s = new VisualizerSettings();
            Equal(s.DownshiftNoteCount, 3);
            Assert(s.ImmediateDownshift, "Immediate switching defaults on (app 0.1.5)");
            foreach (var invalid in new[] { 0, -1, 9 }) Throws<ArgumentException>(() => (s with { DownshiftNoteCount = invalid }).EnsureValid());
            Assert(!s.HasSameNoteRulesAs(s with { DownshiftNoteCount = 1 }), "The wait changes the prepared notes");
            // Thresholds pinned: below 80ms is 4K, below 500ms is 2K.
            var settings = new VisualizerSettings
            {
                SimultaneousWindowMs = 0, MaximumTileCount = 4, MinimumTileCount = 1,
                FourKeyBoundaryMs = 80, TwoKeyBoundaryMs = 500, DownshiftNoteCount = 3
            };
            var generator = new NoteGenerator();
            // One slow note among fast ones never drops the tile count; the count starts again after it.
            var single = generator.Generate(new[] { 0d, 50, 100, 150, 350, 400, 450 }, settings);
            Assert(single.All(n => n.Mode == 4), "A single slow note keeps 4K: " + string.Join(" ", single.Select(n => n.Mode)));
            // Three slow notes in a row: the third one switches down.
            var repeated = generator.Generate(new[] { 0d, 50, 100, 150, 350, 550, 750 }, settings);
            Sequence(repeated.Select(n => n.Mode), 4, 4, 4, 4, 4, 4, 2);
            // With a wait of 1 the first slow note already switches.
            Sequence(generator.Generate(new[] { 0d, 50, 100, 150, 350, 550, 750 }, settings with { DownshiftNoteCount = 1 }).Select(n => n.Mode),
                4, 4, 4, 4, 2, 2, 2);

            // Switched off, every change waits for the cycle to end.
            var old = settings with { ImmediateDownshift = false };
            // The 8K cycle is played out first; eight lanes are played by hand (user rule), so it starts on the initial tile's
            // place in the hand (8 is the hand's lane 4) and 4K then reads inside the hand.
            var cursor = new TilePatternCursor(new VisualizerSettings { MaximumTileCount = 8, InitialTile = 8, ImmediateDownshift = false }, 8);
            Sequence(Enumerable.Range(0, 8).Select(_ => cursor.Next(4)).ToArray(), 4, 3, 2, 1, 8, 7, 6, 5);
            Equal(cursor.Next(4), 4); Equal(cursor.CurrentMode, 4);
            // The wait is not used at all while the toggle is off.
            var times = new[] { 0d, 50, 100, 150, 350, 550, 750 };
            Assert(generator.Generate(times, old with { DownshiftNoteCount = 1 })
                .SequenceEqual(generator.Generate(times, old with { DownshiftNoteCount = 8 })), "The wait is ignored while switching waits for the cycle");
            Assert(old.HasSameNoteRulesAs(old with { DownshiftNoteCount = 8 }), "A locked wait never re-prepares notes");
            Assert(!old.HasSameNoteRulesAs(settings), "The toggle itself changes the notes");
        });

        yield return ("Magic circle: a maximum interval, fade times and the held tile count", () =>
        {
            // User rules: only fast runs count, the bar fades in and out on its own times, and the tile count may be
            // held through a run.
            // The 4K boundary is pinned to the value this case was written for (a 100ms run is 2K).
            var settings = new VisualizerSettings { SimultaneousWindowMs = 0, MagicCircleNoteCount = 16, FourKeyBoundaryMs = 80 };
            Equal(settings.MagicCircleJudgmentLine, true);
            Equal(settings.MagicCircleMaxIntervalMs, 200d);
            Equal(settings.MagicCircleFadeInMs, 500d); Equal(settings.MagicCircleLeadMs, 500d); Equal(settings.MagicCircleFadeOutMs, 500d);
            Equal(settings.MagicCircleFixedTiles, true); Equal(settings.MagicCircleTileCount, 4);

            double[] Times(double interval) => Enumerable.Range(0, 20).Select(i => i * interval).ToArray();
            Equal(MagicCircleFinder.Find(Times(100), settings).Count, 1);
            // 200ms is the maximum, so a run at that interval is no longer a magic circle.
            Equal(MagicCircleFinder.Find(Times(200), settings).Count, 0);
            Equal(MagicCircleFinder.Find(Times(200), settings with { MagicCircleMaxIntervalMs = 400 }).Count, 1);

            // A 100ms run is 2K by interval; the hold puts the whole run on four tiles instead.
            var held = settings;
            var notes = new NoteGenerator().Generate(Times(100), held);
            Assert(notes.Skip(1).All(note => note.Mode == 4), "The run is held at four tiles");
            var free = new NoteGenerator().Generate(Times(100), settings with { MagicCircleFixedTiles = false });
            Assert(free.Skip(1).All(note => note.Mode == 2), "Without the hold the run follows the interval");
            // A held count above the maximum is kept but clamped while it applies.
            Equal((held with { MagicCircleTileCount = 8, MaximumTileCount = 4 }).MagicCircleTiles, 4);
            (held with { MagicCircleTileCount = 8, MaximumTileCount = 4 }).EnsureValid();
            Throws<ArgumentException>(() => (held with { MagicCircleTileCount = 3 }).EnsureValid());
            foreach (var invalid in new[] { 0d, -1, double.NaN })
                Throws<ArgumentException>(() => (settings with { MagicCircleMaxIntervalMs = invalid }).EnsureValid());
            foreach (var invalid in new[] { -1d, double.NaN })
                Throws<ArgumentException>(() => (settings with { MagicCircleFadeInMs = invalid }).EnsureValid());
        });

        yield return ("Trills put two close singles on one lane pair, after the chords and with their own rules", () =>
        {
            // User request: two single notes inside the trill window are played with two fingers, so they take the two
            // lanes of a pair. Chords are grouped first (their window wins), the center pair is the last resort on four lanes
            // (taken only where every other pair breaks a run) and the preference picks the pair; a trill is not colored
            // differently.
            // These are the rules for trills counted note by note; counted as one note, a trill is laid out like a chord.
            var settings = new VisualizerSettings { DynamicTiming = false, SimultaneousWindowMs = 20, MagicCircleFixedTiles = false, TrillAsOneNote = false };
            Equal(settings.TrillWindowMs, 40d);
            Equal(settings.TrillCenterPair, "LastResort");
            Equal(settings.TrillLanePreference, "1-2,3-4");
            Assert(!settings.HasSameNoteRulesAs(settings with { TrillWindowMs = 0 }), "The trill window is a note rule");
            Assert(!settings.HasSameNoteRulesAs(settings with { TrillCenterPair = "Allowed" }), "The center pair is a note rule");
            Assert(!settings.HasSameNoteRulesAs(settings with { TrillCenterPair = "Never" }), "Each choice is a note rule");
            Throws<ArgumentException>(() => (settings with { TrillCenterPair = "Sometimes" }).EnsureValid());
            Assert(!settings.HasSameNoteRulesAs(settings with { TrillLanePreference = "None" }), "The preference is a note rule");
            Throws<ArgumentException>(() => (settings with { TrillWindowMs = -1 }).EnsureValid());
            Throws<ArgumentException>(() => (settings with { TrillWindowMs = 201 }).EnsureValid());
            Throws<ArgumentException>(() => (settings with { TrillLanePreference = "2-3" }).EnsureValid());

            var generator = new NoteGenerator();
            int[] Tiles(IEnumerable<VisualizerNote> notes) => notes.Select(note => note.Tile).ToArray();
            // 0 and 30ms: past the chord window, inside the trill window. The cursor would put them on 5 and 4, the pair
            // across the center; with no run before them to carry on, the pattern skips a step and rolls 4-3 in its own
            // direction (user rule: on a 6-5-4-3 pattern a trill reads 4-3 or 6-5, never 5-6). The next trill opens the
            // next pair.
            var trill = generator.Generate(new[] { 0d, 30, 1000, 1030 }, settings);
            Assert(trill.All(note => !note.StartsChord), "A trill is two notes, not a chord");
            Sequence(Tiles(trill), 4, 3, 6, 5);
            // The same notes with trills off keep the cycle's lanes.
            Sequence(Tiles(generator.Generate(new[] { 0d, 30, 1000, 1030 }, settings with { TrillWindowMs = 0 })), 5, 4, 3, 6);
            // Wider apart than the window: not a trill.
            Sequence(Tiles(generator.Generate(new[] { 0d, 50, 1000, 1050 }, settings)), 5, 4, 3, 6);
            // Inside the chord window it is a chord, which is grouped first and keeps its own placement.
            var chord = generator.Generate(new[] { 0d, 10, 1000 }, settings);
            Assert(chord[0].StartsChord && chord[0].HitTimeMs == chord[1].HitTimeMs, "A chord, not a trill");
            // With the center pair allowed the cursor's own pair is already fine, so nothing moves.
            Sequence(Tiles(generator.Generate(new[] { 0d, 30 }, settings with { TrillCenterPair = "Allowed" })), 5, 4);
            // Three in a row are taken in twos, as chord members are: the third opens the next pair.
            var three = generator.Generate(new[] { 0d, 30, 60 }, settings);
            Assert(three.Count == 3 && three[0].Tile != three[1].Tile, "The first two share a pair: " + string.Join(" ", Tiles(three)));
            // A long note on the partner lane is not taken over.
            var held = generator.Generate(new[] { 0d, 1000, 1030 }, new double?[] { 2000, null, null }, settings);
            Assert(held.All(note => note.Tile != held[0].Tile || note == held[0]), "Nothing starts on the held lane: " + string.Join(" ", Tiles(held)));
        });

        yield return ("Chord lanes: the chosen pair, the center pair and the checkpoint color", () =>
        {
            // User rules: the lane preference is a four-way choice and the center pair is allowed only on request.
            // Both sit in the timing card and apply without the experimental switch.
            var defaults = new VisualizerSettings { SimultaneousWindowMs = 20, MagicCircleFixedTiles = false };
            // The default names both left-hand pairs, which is the same as none on four lanes and keeps chords on
            // lanes 1-2 and 3-4 from six lanes up (user request).
            Equal(defaults.ChordLanePreference, "1-2,3-4");
            Sequence(defaults.GetPreferredChordLanes(), 0, 2);
            var experiments = defaults;
            Sequence((defaults with { ChordLanePreference = "None" }).GetPreferredChordLanes());
            Sequence((defaults with { ChordLanePreference = "1-2" }).GetPreferredChordLanes(), 0);
            Sequence((defaults with { ChordLanePreference = "3-4" }).GetPreferredChordLanes(), 2);
            Assert(!defaults.HasSameNoteRulesAs(defaults with { ChordLanePreference = "3-4" }), "The preference is a note rule");
            Assert(defaults.HasSameNoteRulesAs(defaults with { ExperimentalFeatures = true }), "The experimental switch changes no notes");
            Throws<ArgumentException>(() => (defaults with { ChordLanePreference = "2-3" }).EnsureValid());

            var generator = new NoteGenerator();
            // Chords 640ms apart with a single in between, so each chord opens its own block and may follow the preference.
            var times = Enumerable.Range(0, 6).SelectMany(i => new[] { i * 640d, i * 640 + 1, i * 640 + 320 }).ToArray();
            var visible = defaults.GetVisibleTiles().ToList();
            int Lane(VisualizerNote note) => visible.IndexOf(note.Tile);
            foreach (var (preference, expected) in new[] { ("1-2", 0), ("3-4", 2) })
            {
                var notes = generator.Generate(times, experiments with { ChordLanePreference = preference });
                var chords = notes.Where(note => note.StartsChord).ToList();
                Equal(chords.Count, 6);
                Assert(chords.All(chord => Lane(chord) == expected),
                    $"{preference}: chords on lanes " + string.Join(" ", chords.Select(chord => Lane(chord) + 1)));
            }
            // The center pair is never a chord's, on four lanes nor on six (user rule: no setting turns it on any more).
            var pairsOnly = Enumerable.Range(0, 40).SelectMany(i => new[] { i * 300d, i * 300 + 1 }).ToArray();
            foreach (var lanes in new[] { 4, 6 })
            {
                var wide = defaults with { MaximumTileCount = lanes, ChordLanePreference = "None" };
                var shown = wide.GetVisibleTiles().ToList();
                var lows = generator.Generate(pairsOnly, wide).GroupBy(note => note.HitTimeMs).Where(chord => chord.Count() == 2)
                    .Select(chord => chord.Min(note => shown.IndexOf(note.Tile))).ToArray();
                Assert(lows.Length == 40 && lows.All(low => low != lanes / 2 - 1), $"{lanes} lanes: chords from lanes " + string.Join(" ", lows.Select(low => low + 1)));
            }

            // Checkpoint notes carry their own color setting, white by default.
            Equal(defaults.CheckpointColor, "255, 255, 255");
            var timeline = new ParsedTimeline(new[] { 0d, 500, 1000, 1500 },
                new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 2, 1000) }, true);
            var session = new PlaybackSession(new Clock());
            session.LoadParsedTimeline(timeline, defaults);
            session.Toggle();
            Assert(session.Notes[2].Checkpoint && !session.Notes[1].Checkpoint, "Only the checkpoint tile's note is marked");
        });

        yield return ("The first note of a play and the song's last note outrank the checkpoint color", () =>
        {
            const int width = 200, height = 400;
            static byte[] Render(FrameworkElement element)
            {
                element.Measure(new Size(width, height));
                element.Arrange(new Rect(0, 0, width, height));
                element.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(element);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                return pixels;
            }
            // Notes are solid, so a note of a color leaves pixels of exactly that color somewhere on the field.
            static bool Shows(byte[] pixels, string color)
            {
                var argb = VisualizerSettings.ParseColor(color);
                var (r, g, b) = ((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
                for (var i = 0; i < pixels.Length; i += 4)
                    if (Math.Abs(pixels[i] - b) <= 2 && Math.Abs(pixels[i + 1] - g) <= 2 && Math.Abs(pixels[i + 2] - r) <= 2) return true;
                return false;
            }
            var defaults = new VisualizerSettings();
            // User rule: visible, and apart from every other note color.
            Equal(defaults.StartNoteColor, "255, 208, 0");
            Equal(defaults.EndNoteColor, "255, 208, 0");
            Assert(new[] { defaults.NoteColor, defaults.ChordColor, defaults.CheckpointColor }.All(other =>
                other != defaults.StartNoteColor && other != defaults.EndNoteColor), "The two colors are their own");
            Assert(defaults.HasSameNoteRulesAs(defaults with { StartNoteColor = "0, 0, 0", EndNoteColor = "0, 0, 0" }), "Colors never move notes");
            Equal(defaults.WithVisuals(defaults with { StartNoteColor = "1, 2, 3" }).StartNoteColor, "1, 2, 3");
            Throws<ArgumentException>(() => (defaults with { EndNoteColor = "not a color" }).EnsureValid());

            // Four singles, the third on a checkpoint; the whole song is on screen while it waits.
            var settings = defaults with { DynamicTiming = false, SimultaneousWindowMs = 0, JudgmentLineHeightDip = 36, NoteTravelTimeMs = 4000 };
            var timeline = new ParsedTimeline(new[] { 0d, 1000, 2000, 3000 },
                new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 2, 2000) }, true);
            var session = new PlaybackSession(new Clock());
            session.LoadParsedTimeline(timeline, settings);
            session.Toggle();
            var whole = Render(new NoteField { Session = session });
            Assert(Shows(whole, settings.StartNoteColor) && Shows(whole, settings.EndNoteColor), "The first and last notes take their colors");
            Assert(Shows(whole, settings.NoteColor) && Shows(whole, settings.CheckpointColor), "The notes between keep theirs");

            // From the checkpoint, the play begins on the checkpoint note: the start color wins over the checkpoint's.
            session.Reset(settings, 2);
            Equal(session.Notes.Count, 2);
            var fromCheckpoint = Render(new NoteField { Session = session });
            Assert(Shows(fromCheckpoint, settings.StartNoteColor) && !Shows(fromCheckpoint, settings.CheckpointColor),
                "The checkpoint note that starts the play wears the start color");
            Assert(Shows(fromCheckpoint, settings.EndNoteColor) && !Shows(fromCheckpoint, settings.NoteColor), "and the other is the last note");

            // Pressed, they keep their colors (user rule): the start press marks the first note as played, and with a strong
            // press transparency the play's first note faded the moment it began, so its color was never seen (user report).
            var clock = new Clock();
            // Two colors of their own, or the last note on screen would pass for the first.
            var faded = settings with { PressedNoteTransparencyPercent = 80, StartNoteColor = "255, 79, 216", EndNoteColor = "255, 150, 30" };
            var played = new PlaybackSession(clock) { Presses = new NotePressJudge() };
            played.LoadParsedTimeline(timeline, faded);
            played.Toggle();
            played.Start();
            Equal(played.Presses.Press(played, played.Notes[0].Tile, faded.KeyPressWindowMs), (int?)0);
            clock.NowMs = 50;
            Assert(Shows(Render(new NoteField { Session = played, Judge = played.Presses }), faded.StartNoteColor), "The start note keeps its color");
            clock.NowMs = 3000;
            Equal(played.Presses.Press(played, played.Notes[^1].Tile, faded.KeyPressWindowMs), (int?)(played.Notes.Count - 1));
            clock.NowMs = 3050;
            Assert(Shows(Render(new NoteField { Session = played, Judge = played.Presses }), faded.EndNoteColor), "So does the last one");
        });

        yield return ("Press calibration shifts the display once, within the press window", () =>
        {
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            var judge = new NotePressJudge();
            session.Presses = judge;
            var settings = new VisualizerSettings { DynamicTiming = false, SimultaneousWindowMs = 0, CalibrationNoteCount = 1 };
            session.LoadParsedTimeline(new ParsedTimeline(new[] { 0d, 500, 1000, 1500 }), settings);
            session.Toggle(); session.Start();
            Equal(session.CalibrationOffsetMs, 0d);
            // Pressed 40ms late: every note is shown 40ms later from now on, once.
            clock.NowMs = 40;
            judge.Press(session, session.Notes[0].Tile, settings.KeyPressWindowMs);
            Equal(session.CalibrationOffsetMs, 40d);
            Equal(session.Offsets.TotalMs, 40d);
            clock.NowMs = 620;
            judge.Press(session, session.Notes[1].Tile, settings.KeyPressWindowMs);
            Equal(session.CalibrationOffsetMs, 40d); // Only once per play.

            // Three notes average their deviations, and a new preparation starts over.
            var many = new PlaybackSession(clock) { Presses = judge };
            many.LoadParsedTimeline(new ParsedTimeline(new[] { 0d, 500, 1000, 1500 }), settings with { CalibrationNoteCount = 3 });
            many.Toggle(); clock.NowMs = 0; many.Start();
            foreach (var (time, index) in new[] { (30d, 0), (520d, 1), (1010d, 2) })
            {
                clock.NowMs = time;
                judge.Press(many, many.Notes[index].Tile, settings.KeyPressWindowMs);
            }
            Assert(Math.Abs(many.CalibrationOffsetMs - 20) < 1e-9, $"Average of 30, 20 and 10: {many.CalibrationOffsetMs}");
            many.Reset(many.Settings);
            Equal(many.CalibrationOffsetMs, 0d);
            // The correction never leaves the press window, whatever the presses say.
            Assert(Math.Abs(many.CalibrationOffsetMs) <= many.Settings.KeyPressWindowMs, "Correction stays inside the window");
        });

        yield return ("Note width, long note width, transparency and the single lane color are visual only", () =>
        {
            var s = new VisualizerSettings();
            Equal(s.NoteWidth, 1d); Equal(s.HoldNoteWidth, 1d); Equal(s.PressedNoteTransparencyPercent, 80d);
            Assert(Math.Abs(s.PressedNoteStrength - 0.2) < 1e-9, "80% transparent keeps 20% of the note color");
            foreach (var invalid in new[] { 0d, 0.1, 3.5, double.NaN })
            {
                Throws<ArgumentException>(() => (s with { NoteWidth = invalid }).EnsureValid());
                Throws<ArgumentException>(() => (s with { HoldNoteWidth = invalid, NoteWidth = 3 }).EnsureValid());
                Throws<ArgumentException>(() => (s with { NoteThickness = invalid }).EnsureValid());
            }
            // A long note bar is drawn inside the note width.
            Throws<ArgumentException>(() => (s with { NoteWidth = 0.5, HoldNoteWidth = 1 }).EnsureValid());
            (s with { NoteWidth = 1, HoldNoteWidth = 0.5 }).EnsureValid();
            foreach (var invalid in new[] { -1d, 101, double.NaN })
                Throws<ArgumentException>(() => (s with { PressedNoteTransparencyPercent = invalid }).EnsureValid());
            foreach (var invalid in new[] { 0, 21, -1 })
                Throws<ArgumentException>(() => (s with { CalibrationNoteCount = invalid }).EnsureValid());
            Assert(s.HasSameNoteRulesAs(s with { NoteWidth = 0.5, HoldNoteWidth = 0.4, PressedNoteTransparencyPercent = 10, LaneEffectColor = "#FF0000" }),
                "Sizes and colors never regenerate notes");
            var previewed = s.WithVisuals(s with { NoteWidth = 0.5, HoldNoteWidth = 0.4, PressedNoteTransparencyPercent = 10, LaneEffectColor = "255, 0, 0" });
            Equal(previewed.NoteWidth, 0.5); Equal(previewed.HoldNoteWidth, 0.4); Equal(previewed.LaneEffectColor, "255, 0, 0");

            const int width = 200, height = 400;
            static byte[] Render(FrameworkElement element)
            {
                element.Measure(new Size(width, height));
                element.Arrange(new Rect(0, 0, width, height));
                element.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(element);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                return pixels;
            }
            // Four lanes of 50px: tile 5 is the third (x 100..150). With a 36px gap the judgment line is at y 334.
            static int Blue(byte[] pixels, int x, int y) => pixels[(y * width + x) * 4];
            // The first note wears the start color; it is pinned to the note color so the blue channel finds it.
            var settings = new VisualizerSettings { DynamicTiming = false, SimultaneousWindowMs = 0, JudgmentLineHeightDip = 36,
                NoteColor = "94, 218, 242", StartNoteColor = "94, 218, 242" };
            var clock = new Clock();
            var full = new PlaybackSession(clock);
            full.LoadTimeline(new[] { 0d, 5000 }, settings);
            full.Toggle();
            // Just above the judgment line, so only the note itself colors the pixel.
            var wide = Render(new NoteField { Session = full });
            Assert(Blue(wide, 106, 328) > 200, "A full-width note reaches the edge of its lane");
            var narrow = new PlaybackSession(clock);
            narrow.LoadTimeline(new[] { 0d, 5000 }, settings with { NoteWidth = 0.5, HoldNoteWidth = 0.5 });
            narrow.Toggle();
            var thin = Render(new NoteField { Session = narrow });
            Assert(Blue(thin, 106, 328) < 60 && Blue(thin, 125, 328) > 200, "Half width keeps the middle and clears the edge");
        });

        yield return ("The magic circle bar appears with its lead time and fades after the section", () =>
        {
            const int width = 200, height = 400;
            static byte[] Render(FrameworkElement element, string? capture = null)
            {
                element.Measure(new Size(width, height));
                element.Arrange(new Rect(0, 0, width, height));
                element.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(element);
                if (capture is not null)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(capture);
                    encoder.Save(stream);
                }
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                return pixels;
            }
            // The bar sits at half the judgment line height (y 167 of 334) across the middle of the field.
            static int Brightness(byte[] pixels, int x, int y) =>
                pixels[(y * width + x) * 4] + pixels[(y * width + x) * 4 + 1] + pixels[(y * width + x) * 4 + 2];
            var settings = new VisualizerSettings
            {
                DynamicTiming = false, SimultaneousWindowMs = 0, JudgmentLineHeightDip = 36,
                MagicCircleJudgmentLine = true, MagicCircleNoteCount = 16, MagicCircleLeadMs = 1000
            };
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            session.LoadTimeline(Enumerable.Range(0, 20).Select(i => 3000d + i * 100), settings);
            session.Toggle(); session.Start();
            var judge = new NotePressJudge();
            session.Presses = judge;
            var field = new NoteField { Session = session, Judge = judge };
            // Notes start at 0 after the first hit is normalized, so the run itself starts at 0: before it, the lead shows the bar.
            clock.NowMs = 0;
            field.Tick();
            var during = Render(field, Path.Combine(artifactRoot, "magic-circle.png"));
            Assert(Brightness(during, 100, 167) > 0 && Brightness(during, 100, 172) > 0, "The bar is drawn over the field");
            var plain = new PlaybackSession(clock);
            plain.LoadTimeline(Enumerable.Range(0, 20).Select(i => 3000d + i * 100), settings with { MagicCircleJudgmentLine = false });
            plain.Toggle(); plain.Start();
            var without = Render(new NoteField { Session = plain });
            Assert(Brightness(during, 100, 167) != Brightness(without, 100, 167), "Without the setting nothing is drawn there");
            // Well past the run (last note at 1900) the bar has faded out.
            clock.NowMs = 3000;
            field.Tick();
            var after = Render(field);
            Equal(Brightness(after, 100, 167), Brightness(without, 100, 167));
        });
    }
}
