using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Visualizer.App.Commands;
using Visualizer.App.Input;
using Visualizer.App.Rendering;
using Visualizer.App.ViewModels;
using Visualizer.App.Views;
using Visualizer.Application.Input;
using Visualizer.Application.Playback;
using Visualizer.Core.Localization;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using Visualizer.Infrastructure.Input;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

internal static class RhythmVisualTests
{
    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }

    public static IEnumerable<(string Name, Action Run)> Create(string artifacts)
    {
        yield return ("Frame statistics separate our slow frame, a skipped one and a late handoff", () =>
        {
            var stats = new FrameStats();
            Equal(stats.Report(2), null);
            // Twenty frames 8ms apart, then one the compositor skipped: both its arrival and its own gap are 40ms.
            var at = 0d;
            for (var i = 0; i < 20; i++)
            {
                at += 8;
                stats.Add(at, at, 0.5);
            }
            at += 40;
            stats.Add(at, at, 0.6);
            var report = stats.Report(2)!;
            // The first frame only sets the marks, so twenty-one frames make twenty intervals.
            Assert(report.Frames == 20 && report.MedianMs == 8 && report.MaxMs == 40 && report.LateFrames == 1, "The late frame stands out");
            Assert(report.SkippedFrames == 1 && report.WorkMaxMs <= 0.6, "The compositor never gave that frame, and our work stayed small");
            var lines = FrameStats.Lines(report);
            Assert(lines[0].Contains("125.0 fps", StringComparison.Ordinal) && lines[0].Contains("in 0.2 s", StringComparison.Ordinal),
                "The window covers the samples it kept: " + lines[0]);
            Assert(lines[^1].Contains("compositor skipped", StringComparison.Ordinal), "A skipped frame is named: " + lines[^1]);
            // The same late frame, but the compositor kept its cadence: we were handed it late.
            var late = new FrameStats();
            for (var i = 1; i <= 20; i++) late.Add(i * 8, i * 8, 0.5);
            late.Add(20 * 8 + 40, 21 * 8, 0.5);
            var handed = late.Report(2)!;
            Assert(handed.LateFrames == 1 && handed.SkippedFrames == 0 && handed.HandoffMaxMs > 8, "A late handoff is told apart");
            Assert(FrameStats.Lines(handed)[^1].Contains("reached this app late", StringComparison.Ordinal), "And named");
            // A repeated compositor time is the same frame, not a new one.
            var twice = new FrameStats();
            twice.Add(8, 8, 0.1);
            twice.Add(16, 16, 0.1);
            twice.Add(16.2, 16, 0.1);
            Equal(twice.Report(2)!.Frames, 1);
            // Heavy drawing points at the app instead.
            var busy = new FrameStats();
            for (var i = 0; i < 10; i++) busy.Add((i + 1) * 8, (i + 1) * 8, 6);
            Assert(FrameStats.Lines(busy.Report(0))[^1].Contains("drawing itself", StringComparison.Ordinal), "Heavy drawing is named");
            Assert(FrameStats.Lines(busy.Report(0))[^2].Contains("software", StringComparison.Ordinal), "Render tier 0 is software");
            // A new window forgets the samples.
            busy.Reset();
            Equal(busy.Report(0), null);
            Equal(FrameStats.Lines(null)[0], "No frames measured yet. Play or move the window, then try again.");

            // The frame thread asks Windows for the multimedia scheduling class; a refusal says why and changes nothing.
            var scheduling = MultimediaScheduling.Register();
            Assert(scheduling.Active || scheduling.Problem is not null, "Registered or told why not");
            Equal(scheduling.Task, "Games");
            scheduling.Dispose();
            scheduling.Dispose();
            Assert(!scheduling.Active, "Given back");
            Throws<ArgumentException>(() => MultimediaScheduling.Register(" "));

            // The console report ends with what the scheduling and the collector are set to.
            var window = new MainWindow();
            var printed = ((ICmdHost)window).FrameStats(false);
            Assert(printed[^1].StartsWith("Scheduling  multimedia class", StringComparison.Ordinal)
                && printed[^1].Contains("GC latency", StringComparison.Ordinal), "The report says how it is scheduled: " + printed[^1]);
            window.Close();
        });

        yield return ("Visual input settings: defaults, validation and note-rule identity", () =>
        {
            var s = new VisualizerSettings();
            Assert(s.AutoCheckpointAdvance, "Auto checkpoint defaults ON");
            Equal(s.LaneKeys, "A S D F J K L ;"); Equal(s.JudgmentLineHeightDip, 72d); Equal(s.KeyPressWindowMs, 150d);
            foreach (var value in new[] { -1d, 400.01, double.NaN, double.PositiveInfinity })
                Throws<ArgumentException>(() => (s with { JudgmentLineHeightDip = value }).EnsureValid());
            (s with { JudgmentLineHeightDip = 0 }).EnsureValid();
            (s with { JudgmentLineHeightDip = 400 }).EnsureValid();
            foreach (var value in new[] { 0d, -5, 1000.5, double.NaN })
                Throws<ArgumentException>(() => (s with { KeyPressWindowMs = value }).EnsureValid());
            foreach (var keys in new[] { "A S D F J K L", "A S D F J K L ; Q", "A A D F J K L ;", "A S D F J K L Enter", "A S D F J K - ;" })
                Throws<ArgumentException>(() => (s with { LaneKeys = keys }).EnsureValid());
            foreach (var changed in new[] { s with { AutoCheckpointAdvance = false }, s with { LaneKeys = "Q W E R U I O P" },
                s with { JudgmentLineHeightDip = 10 }, s with { KeyPressWindowMs = 60 },
                s with { ShowInputEffect = false, ShowMissEffect = false, ShowGuideButtons = false },
                s with { NoteColor = "#FF0000", LaneEffectColor = "#FF0000", PressedNoteTransparencyPercent = 20 },
                s with { NextCheckpointKey = "PageUp", MapRootDirectory = @"D:\Maps" } })
                Assert(s.HasSameNoteRulesAs(changed), "Visual and input settings must not regenerate notes");
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            session.LoadTimeline(new[] { 0d, 500, 1000 }, s); session.Toggle(); session.Start(); clock.NowMs = 300;
            var notes = session.Notes;
            session.ApplySettings(s with { JudgmentLineHeightDip = 120, LaneKeys = "Q W E R U I O P", KeyPressWindowMs = 80 });
            Assert(ReferenceEquals(notes, session.Notes), "Visual edits keep prepared notes");
            Equal(session.State, PlaybackState.Playing); Equal(session.TimelineTimeMs, 300d);
        });

        yield return ("Lane keys map tiles 1-8, share only the start key and keep punctuation readable", () =>
        {
            var s = new VisualizerSettings();
            var lanes = ControlBindings.CreateLaneKeys(s);
            Equal(lanes.Count, 8);
            var expected = new[] { Key.A, Key.S, Key.D, Key.F, Key.J, Key.K, Key.L, Key.OemSemicolon };
            for (var i = 0; i < expected.Length; i++) Equal(lanes[KeyInterop.VirtualKeyFromKey(expected[i])], i + 1);
            Equal(KeyInterop.VirtualKeyFromKey(Key.OemSemicolon), 0xBA);
            Equal(ControlBindings.Create(s)[KeyInterop.VirtualKeyFromKey(Key.J)], InputAction.Start);
            // "Return" passes the textual check but is the same virtual key as the Enter reset key.
            Throws<ArgumentException>(() => ControlBindings.CreateLaneKeys(s with { LaneKeys = "A S D F J K L Return" }));
            foreach (var keys in new[] { "A S D F J K L Nope", "A S D F J K L 1,2", "A S D F J K 186 ;", "A S D F J K OemSemicolon ;" })
                Throws<ArgumentException>(() => ControlBindings.Create(s with { LaneKeys = keys }));
            foreach (var key in new[] { Key.A, Key.D7, Key.OemSemicolon, Key.OemComma, Key.Space, Key.Enter, Key.F12, Key.OemQuestion })
            {
                Assert(KeyNames.TryParse(KeyNames.Token(key), out var parsed) && parsed == key, $"Token round trip for {key}");
                Assert(!KeyNames.Token(key).Contains(' '), "Tokens must not contain spaces");
            }
            Equal(KeyNames.Token(Key.OemSemicolon), ";"); Equal(KeyNames.Token(Key.D7), "7");
            Assert(!KeyNames.IsSupported(Key.LeftShift) && !KeyNames.IsSupported(Key.Tab), "Modifiers and Tab cannot be captured");
            var remapped = ControlBindings.CreateLaneKeys(s with { StartTriggerKey = "Space", LaneKeys = "Z X C V M , . /" });
            Equal(remapped[KeyInterop.VirtualKeyFromKey(Key.OemComma)], 6);
        });

        yield return ("Key tracker reports releases, ignores repeats and releases removed devices", () =>
        {
            var tracker = new KeyPressTracker();
            tracker.SetObservedKeys(new[] { 65, 83 });
            Equal(tracker.Transition(1, 65, false), KeyTransition.Pressed);
            Equal(tracker.Transition(1, 65, false), KeyTransition.None);
            Equal(tracker.Transition(1, 65, true), KeyTransition.Released);
            Equal(tracker.Transition(1, 65, true), KeyTransition.None);
            Equal(tracker.Transition(1, 90, false), KeyTransition.None);
            tracker.Transition(2, 83, false); tracker.Transition(2, 65, false);
            Assert(tracker.RemoveDevice(2).Order().SequenceEqual(new[] { 65, 83 }), "Removed device releases its held keys");
            Equal(tracker.Transition(2, 83, true), KeyTransition.None);
        });

        yield return ("Press judge dims the closest unpressed note of that lane within the window", () =>
        {
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            var judge = new NotePressJudge();
            var settings = new VisualizerSettings { DynamicTiming = false, SimultaneousWindowMs = 0 };
            session.LoadTimeline(new[] { 0d, 100, 200, 300, 400, 500, 600, 700 }, settings);
            Sequence(session.Notes.Select(n => n.Tile), 5, 4, 3, 6, 5, 4, 3, 6);
            session.Toggle();
            Equal(judge.Press(session, 5, 150), (int?)null); // Waiting never marks notes.
            session.Start();
            Equal(judge.Press(session, 5, 150), (int?)0);
            Assert(judge.IsPressed(session.Notes, 0), "Pressed note is marked");
            Equal(judge.Press(session, 5, 150), (int?)null); // Already pressed; the next tile-5 note is out of range.
            clock.NowMs = 380; Equal(judge.Press(session, 4, 150), (int?)5);
            clock.NowMs = 450; Equal(judge.Press(session, 5, 150), (int?)4);
            session.SetMapDisplayOffset(100);
            clock.NowMs = 720; Equal(judge.Press(session, 3, 50), (int?)6); // Displayed at 700, so it matches 620 + 100.
            Assert(session.Timeline!.HitTimesMs.SequenceEqual(new[] { 0d, 100, 200, 300, 400, 500, 600, 700 }), "Presses never touch timing");
            session.Reset(settings);
            Assert(!judge.IsPressed(session.Notes, 0), "A new preparation forgets every mark");
            Throws<ArgumentOutOfRangeException>(() => judge.Press(session, 5, -1));

            var chord = new PlaybackSession(new Clock());
            chord.LoadTimeline(new[] { 0d, 5, 800 }, new()); chord.Toggle(); chord.Start();
            Equal(chord.Notes[0].HitTimeMs, chord.Notes[1].HitTimeMs);
            var chordJudge = new NotePressJudge();
            Equal(chordJudge.Press(chord, chord.Notes[1].Tile, 150), (int?)1);
            Equal(chordJudge.Press(chord, chord.Notes[0].Tile, 150), (int?)0);
        });

        yield return ("Missed notes are reported once per note after their press window passes", () =>
        {
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            var judge = new NotePressJudge();
            var settings = new VisualizerSettings { DynamicTiming = false, SimultaneousWindowMs = 0 };
            session.LoadTimeline(new[] { 0d, 500, 1000, 1000 }, settings with { SimultaneousWindowMs = 10 });
            var missed = new List<int>();
            judge.CollectMisses(session, 150, missed);
            Equal(missed.Count, 0); // Not playing.
            session.Toggle(); session.Start();
            Equal(judge.Press(session, session.Notes[0].Tile, 150), (int?)0);
            clock.NowMs = 400; judge.CollectMisses(session, 150, missed);
            Equal(missed.Count, 0); // The pressed note is never a miss; the next one is still in its window.
            clock.NowMs = 650; judge.CollectMisses(session, 150, missed);
            Equal(missed.Count, 0); // Exactly at the window edge.
            clock.NowMs = 651; judge.CollectMisses(session, 150, missed);
            Sequence(missed, session.Notes[1].Tile);
            judge.CollectMisses(session, 150, missed);
            Equal(missed.Count, 1); // Reported once.
            session.Pause(); clock.NowMs = 5000; judge.CollectMisses(session, 150, missed);
            Equal(missed.Count, 1); // Paused time does not pass.
            session.Start(); clock.NowMs += 1000; judge.CollectMisses(session, 150, missed);
            Sequence(missed, session.Notes[1].Tile, session.Notes[2].Tile, session.Notes[3].Tile); // Both chord lanes.
            session.Reset(session.Settings); session.Start(); missed.Clear();
            clock.NowMs += 151; judge.CollectMisses(session, 150, missed);
            Sequence(missed, session.Notes[0].Tile); // A new preparation starts over.

            var field = new NoteField { Session = session, Judge = judge };
            session.Pause(); field.Tick();
            session.Start(); clock.NowMs += 600; field.Tick();
            session.Pause(); field.Tick();
            Assert(field.Tick(), "A miss flash keeps animating after playback stops");
        });

        yield return ("Judgment line sits above the bottom line; notes vanish only at the bottom", () =>
        {
            var g = NoteFieldGeometry.Create(600, 72);
            Equal(g.JudgmentY, 528d); Equal(g.RemovalY, 600d);
            Equal(g.CenterY(1), 528d); Equal(g.CenterY(0), 0d);
            Assert(!g.IsRemoved(g.CenterY(1.1), 3), "A note past the judgment line keeps falling");
            Assert(!g.IsRemoved(602.9, 3), "Still touching the bottom line");
            Assert(g.IsRemoved(603, 3), "Fully past the bottom line disappears");
            Equal(NoteFieldGeometry.Create(100, 400).JudgmentY, 40d);
            Equal(NoteFieldGeometry.Create(600, 0).JudgmentY, 600d);
            var (earliest, latest) = g.VisibleWindow(1000, 1000, 0);
            Equal(latest, 2000d);
            Assert(Math.Abs(g.CenterY(NoteLayout.Progress(earliest, 1000, 1000, new DisplayOffsets(0))) - g.RemovalY) < 1e-9,
                "The earliest visible note sits on the bottom line");
            Throws<ArgumentOutOfRangeException>(() => NoteFieldGeometry.Create(0, 10));
            Throws<ArgumentOutOfRangeException>(() => NoteFieldGeometry.Create(100, double.NaN));
        });

        yield return ("Auto checkpoint OFF holds the selected section during playback", () =>
        {
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            var timeline = new ParsedTimeline(new[] { 0d, 2000, 3000 },
                new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 2, 2000) }, true);
            var off = new VisualizerSettings { AutoCheckpointAdvance = false };
            session.LoadParsedTimeline(timeline, off); session.Toggle(); session.Start();
            clock.NowMs = 10000; Equal(session.ActiveCheckpoint, (int?)1);
            var notes = session.Notes;
            session.ApplySettings(off with { AutoCheckpointAdvance = true });
            Assert(ReferenceEquals(notes, session.Notes), "Turning auto on must not re-prepare");
            Equal(session.State, PlaybackState.Playing); Equal(session.ActiveCheckpoint, (int?)2);

            var model = new MainViewModel();
            model.Playback.LoadParsedTimeline(timeline, model.Settings);
            model.ApplySettings(model.Settings with { AutoCheckpointAdvance = false }); // Switched in setting only.
            Assert(!model.AutoCheckpointActive && !model.Playback.Settings.AutoCheckpointAdvance, "The setting reaches playback");
            model.Toggle(); model.Start();
            var prepared = model.Playback.Notes;
            model.ApplySettings(model.Settings with { AutoCheckpointAdvance = true });
            Assert(model.AutoCheckpointActive, "Switched back on");
            model.ApplySettings(model.Settings with { AutoCheckpointAdvance = false });
            Assert(ReferenceEquals(prepared, model.Playback.Notes), "Switching auto never re-prepares notes");
            Equal(model.Playback.State, PlaybackState.Playing);
        });

        yield return ("Ignored commands stay silent and checkpoint options are reused", () =>
        {
            var model = new MainViewModel();
            var timeline = new ParsedTimeline(new[] { 0d, 5000, 9000 },
                new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 1, 5000) }, true);
            model.Playback.LoadParsedTimeline(timeline, model.Settings);
            model.Toggle(); model.Start();
            var options = model.CheckpointOptions;
            var raised = new List<string>();
            model.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
            // J doubles as a lane key: pressing it during play must not refresh bindings.
            for (var i = 0; i < 50; i++) model.Start();
            Equal(raised.Count, 0);
            model.ApplySettings(model.Settings with { NoteThickness = 1.5 });
            Assert(ReferenceEquals(options, model.CheckpointOptions), "Same timeline and language reuse the picker items");
            model.ApplySettings(model.Settings with { Language = Languages.English });
            Assert(!ReferenceEquals(options, model.CheckpointOptions), "Language changes rebuild labels");
            raised.Clear(); model.PauseResume();
            Assert(raised.Contains(nameof(MainViewModel.StateLabel)), "Real state changes notify");
        });

        yield return ("Static frames skip redraws; playback and lane fades animate", () =>
        {
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            session.LoadTimeline(new[] { 0d, 500 }, new()); session.Toggle();
            var field = new NoteField { Session = session, Judge = new NotePressJudge() };
            field.Tick();
            Assert(!field.Tick() && !field.Tick(), "A waiting screen must not redraw every frame");
            session.Start();
            Assert(field.Tick() && field.Tick(), "Playback redraws every frame");
            session.Pause(); field.Tick(); field.Tick();
            Assert(!field.Tick(), "A paused screen is static");
            field.PressLane(5, false);
            Assert(!field.Tick(), "A steadily held lane is static");
            field.ReleaseLane(5);
            Assert(field.Tick(), "A released lane fades out");
        });

        yield return ("Key capture boxes record punctuation inside the dialog", () =>
        {
            using var source = new HwndSource(new HwndSourceParameters("Key capture test") { WindowStyle = 0, ParentWindow = new nint(-3) });
            var box = new KeyCaptureBox("A");
            Equal(box.Token, "A"); Equal(box.Text, "A");
            var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.OemSemicolon) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            box.RaiseEvent(down);
            Equal(box.Token, ";"); Assert(down.Handled, "The captured key stays inside the dialog box");
            box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Tab) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Equal(box.Token, ";");
            var optional = new KeyCaptureBox(null, allowNone: true) { NoneText = "none" };
            Equal(optional.Token, null); Equal(optional.Text, "none");
            var window = new SettingsPanel(new());
            var fields = (StackPanel)((DockPanel)window.Content).Children.OfType<ScrollViewer>().Single().Content;
            Equal(FindAll<KeyCaptureBox>(fields).Count(), 14); // Reset, start, toggle, two checkpoint keys, setting hotkey and 8 lanes.
        });

        yield return ("Theme files: universe, dark and white are picked like songs and switch live without touching notes", () =>
        {
            var catalog = Visualizer.App.Themes.ThemeCatalog.All;
            Assert(catalog.Select(t => t.Id).SequenceEqual(new[] { "universe", "dark", "white" }), "Theme files in list order");
            foreach (var theme in catalog)
            {
                var declared = Visualizer.App.Themes.ThemeCatalog.DeclaredKeys(theme.Id);
                var missing = Visualizer.App.Themes.ThemeCatalog.BrushKeys.Where(key => !declared.Contains(key)).ToArray();
                Assert(missing.Length == 0, $"{theme.Id} misses {string.Join(", ", missing)}");
            }
            Assert(catalog.Select(t => t.DisplayName(Languages.Korean)).SequenceEqual(new[] { "우주", "다크", "화이트" }), "Korean names");
            Assert(catalog.Select(t => t.DisplayName(Languages.English)).SequenceEqual(new[] { "Universe", "Dark", "White" }), "English names");
            var s = new VisualizerSettings();
            Equal(s.Theme, "universe");
            Assert(s.HasSameNoteRulesAs(s with { Theme = "white" }), "Theme is visual only");
            Throws<ArgumentException>(() => (s with { Theme = "" }).EnsureValid());
            Throws<ArgumentException>(() => (s with { Theme = @"..\dark" }).EnsureValid());
            var dialog = new SettingsPanel(s);
            Assert(dialog.Background is DrawingBrush { IsFrozen: true }, "Universe uses the static starfield");
            var picker = (ComboBox)dialog.FindName("ThemePicker");
            Assert(picker.Items.Cast<string>().SequenceEqual(new[] { "우주", "다크", "화이트" }) && picker.SelectedIndex == 0, "The list shows every theme");
            Assert(dialog.FindName("PreviousThemeButton") is null && dialog.FindName("NextThemeButton") is null, "Themes are picked from the list only");
            var universeAccent = ((SolidColorBrush)dialog.FindResource("AccentBrush")).Color;
            Capture(dialog, Path.Combine(artifacts, "theme-universe-setting.png"), 640, 780);
            picker.SelectedIndex = 1;
            Assert(dialog.Background is SolidColorBrush, "Dark uses a plain background");
            Assert(((SolidColorBrush)dialog.FindResource("AccentBrush")).Color != universeAccent, "The palette was swapped");
            Capture(dialog, Path.Combine(artifacts, "theme-dark-setting.png"), 640, 780);
            picker.SelectedIndex = 2;
            Assert(((SolidColorBrush)dialog.FindResource("WindowBrush")).Color.R > 200, "White is light");
            Capture(dialog, Path.Combine(artifacts, "theme-white-setting.png"), 640, 1400);
            Equal(dialog.BuildSettings().Theme, "white");

            var main = new MainWindow();
            var model = (MainViewModel)main.DataContext;
            model.LoadPreview();
            var notes = model.Playback.Notes;
            var before = ((SolidColorBrush)main.FindResource("WindowBrush")).Color;
            Capture(main, Path.Combine(artifacts, "theme-universe-main.png"), 480, 800);
            model.ApplySettings(model.Settings with { Theme = "dark" });
            Assert(((SolidColorBrush)main.FindResource("WindowBrush")).Color != before, "The main window follows the applied theme");
            Capture(main, Path.Combine(artifacts, "theme-dark-main.png"), 480, 800);
            model.ApplySettings(model.Settings with { Theme = "white" });
            Assert(ReferenceEquals(notes, model.Playback.Notes), "Theme changes keep prepared notes");
            Capture(main, Path.Combine(artifacts, "theme-white-main.png"), 480, 800);
            main.Close();
        });

        yield return ("The press that starts playback plays the whole first chord, not one half of it", () =>
        {
            // User report: a song starting on a chord came back from its end with one key shown pressed and one waiting.
            // The start wait is 0, so the press that starts play lands on the first note and marks it.
            var model = new MainViewModel();
            model.ApplySettings(model.Settings with { StartDelayMs = 0, KeyPressWindowMs = 150 });
            model.Playback.LoadTimeline(new[] { 0d, 0, 400, 800 }, model.Settings);
            model.Toggle();
            var notes = model.Playback.Notes;
            Assert(notes[0].HitTimeMs == notes[1].HitTimeMs && notes[0].Tile != notes[1].Tile, "The song starts on a chord");
            model.Start();
            Assert(model.PressLane(notes[0].Tile, starting: true), "The start key's lane hits its note");
            Assert(model.Presses.IsPressed(notes, 0) && model.Presses.IsPressed(notes, 1), "Both halves of the starting chord are played");
            Assert(!model.Presses.IsPressed(notes, 2) && !model.Presses.IsPressed(notes, 3), "Nothing after the chord is");

            // Any other press marks its own lane only, as before.
            var again = new MainViewModel();
            again.ApplySettings(again.Settings with { StartDelayMs = 0, KeyPressWindowMs = 150 });
            again.Playback.LoadTimeline(new[] { 0d, 0, 400, 800 }, again.Settings);
            again.Toggle(); again.Start();
            Assert(again.PressLane(again.Playback.Notes[0].Tile), "A plain press hits its lane");
            Assert(again.Presses.IsPressed(again.Playback.Notes, 0) && !again.Presses.IsPressed(again.Playback.Notes, 1), "and only its lane");
        });

        yield return ("Rhythm feedback renders beams, dimmed hits and the raised judgment line", () =>
        {
            var window = new MainWindow();
            var model = (MainViewModel)window.DataContext;
            model.ApplySettings(model.Settings with { JudgmentLineHeightDip = 96, NoteTravelTimeMs = 1500 });
            model.Playback.LoadTimeline(new[] { 0d, 0, 400, 800, 1200, 1600, 2000, 2400 }, model.Settings);
            model.Toggle(); model.Start();
            var field = (NoteField)window.FindName("Field");
            foreach (var tile in new[] { model.Playback.Notes[0].Tile, model.Playback.Notes[1].Tile })
                field.PressLane(tile, model.PressLane(tile));
            Assert(model.Presses.IsPressed(model.Playback.Notes, 0) && model.Presses.IsPressed(model.Playback.Notes, 1), "The chord was hit");
            var idle = model.Settings.GetVisibleTiles().First(t => t != model.Playback.Notes[0].Tile && t != model.Playback.Notes[1].Tile);
            field.PressLane(idle, model.PressLane(idle));
            Capture(window, Path.Combine(artifacts, "rhythm-feedback.png"), 480, 800);
            model.ApplySettings(model.Settings with { Language = Languages.English, MaximumTileCount = 8, DynamicTiming = false });
            Assert(field.Tick(), "New settings must request a redraw"); // The app calls this every compositor frame.
            Capture(window, Path.Combine(artifacts, "rhythm-eight-lanes.png"), 480, 800);
            window.Close();

            // Miss flash: a 1ms window lets the first chord pass unpressed right after Start.
            var missWindow = new MainWindow();
            var missModel = (MainViewModel)missWindow.DataContext;
            missModel.ApplySettings(missModel.Settings with { KeyPressWindowMs = 1 });
            missModel.Playback.LoadTimeline(new[] { 0d, 0, 20, 400, 800, 1200 }, missModel.Settings);
            missModel.Toggle(); missModel.Start();
            var missField = (NoteField)missWindow.FindName("Field");
            var held = missModel.Settings.GetVisibleTiles().Last();
            missField.PressLane(held, false);
            System.Threading.Thread.Sleep(60);
            missField.Tick();
            Capture(missWindow, Path.Combine(artifacts, "rhythm-miss.png"), 480, 800);
            missWindow.Close();
        });
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }
}
