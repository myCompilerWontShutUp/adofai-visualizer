using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Visualizer.App.Input;
using Visualizer.App.Localization;
using Visualizer.App.Rendering;
using Visualizer.App.ViewModels;
using Visualizer.App.Views;
using Visualizer.Application.Cache;
using Visualizer.Application.Input;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Localization;
using Visualizer.Core.Settings;
using Visualizer.Infrastructure.Cache;
using Visualizer.Infrastructure.Maps;
using Visualizer.Infrastructure.Parsing;
using static Visualizer.Tests.Program;
using Path = System.IO.Path;

namespace Visualizer.Tests;

/// <summary>
/// Settings window (sections, markers, help marks, colors, blank defaults, JSON apply/cancel, reset), Escape pause and lane resume,
/// checkpoint auto rules, main screen layout, example and 8-lane offer, visualization toggles, colors, preview and song search.
/// </summary>
internal static class AdvancedFeatureTests
{
    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }

    private sealed class FixedCache(IReadOnlyList<MapCacheEntry> entries) : IReadOnlyMapCache
    {
        public Task<IReadOnlyList<MapCacheEntry>> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(entries);
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    /// <summary>Whether a click on this would go nowhere: the window takes it before the control can act on it.</summary>
    private static bool Swallowed(UIElement element)
    {
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseDownEvent };
        element.RaiseEvent(press);
        return press.Handled;
    }

    private static IReadOnlyList<string> HelpTips(DependencyObject root) => FindAll<Border>(root)
        .Where(b => b.ToolTip is ToolTip).Select(b => ((TextBlock)((ToolTip)b.ToolTip).Content).Text).ToList();

    private static MapCacheEntry Entry(string id, ParsedTimeline timeline) =>
        new(id, "unused", "map.adofai", id, MapParseStatus.Supported, timeline, Array.Empty<string>(), "test", "test");

    /// <summary>The card index of a settings section, so a new card between them does not move every expectation.</summary>
    private static int SectionIndex(SettingsPanel window, string key)
    {
        var expanders = FindAll<Expander>(window).ToList();
        for (var i = 0; i < expanders.Count; i++)
            if (window.SectionState(key) is not null && FindAll<TextBlock>((DependencyObject)expanders[i].Header)
                .Any(block => block.Text == UiText.Get(Languages.Korean, key))) return i;
        throw new InvalidOperationException("No card for " + key);
    }

    public static IEnumerable<(string Name, Action Run)> Create(string artifacts)
    {
        yield return ("Settings JSON exports, merges partial input and rejects bad input with reasons and fields", () =>
        {
            var current = new VisualizerSettings { NoteTravelTimeMs = 650, EnableTilePreference = true, FourKeyOrder = "1 2 3 4" };
            var json = SettingsJson.Export(current);
            Assert(json.Contains("\"Language\": \"korean\"") && json.Contains("\"FourKeyOrder\": \"1 2 3 4\""), "Readable JSON with file ids: " + json);
            Equal(SettingsJson.Import(json, new VisualizerSettings()), current);
            Equal(SettingsJson.Import("{}", current), current);
            Equal(SettingsJson.Import("{ \"notetraveltimems\": 900, // slower\n \"Theme\": \"dark\", }", current),
                current with { NoteTravelTimeMs = 900, Theme = "dark" });
            Assert(Fail("{ \"HitEffectColor\": \"#FFFFFF\" }").Fields.SequenceEqual(new[] { "HitEffectColor" }), "The hit glow uses the judgment line color");
            SettingsJsonException Fail(string? text, VisualizerSettings? basis = null)
            {
                try { SettingsJson.Import(text, basis ?? current); }
                catch (SettingsJsonException error) { return error; }
                throw new InvalidOperationException("Expected a rejection: " + text);
            }
            string Reason(string? text, VisualizerSettings? basis = null) => Fail(text, basis).Message;
            Assert(Reason("").Contains("JSON을 입력") && Reason(null).Contains("JSON을 입력"), "Empty input");
            var syntax = Fail("{\n  \"NoteTravelTimeMs\": 900,\n  oops\n}");
            Assert(syntax.Message.Contains("줄 3") && syntax.Line == 3 && syntax.Column >= 1, "Syntax errors report a position: " + syntax.Message);
            Assert(Reason("[1, 2]").Contains("객체") && Reason("null").Contains("객체"), "Only an object is accepted");
            Assert(Fail("{ \"Speed\": 2, \"Zoom\": 1 }").Fields.SequenceEqual(new[] { "Speed", "Zoom" }), "Unknown keys are named");
            Assert(Fail("{ \"OneKeyPattern\": \"5\" }").Fields.SequenceEqual(new[] { "OneKeyPattern" }), "The merged 1K pattern key is gone");
            Assert(Fail("{ \"NoteTravelTimeMs\": \"fast\" }").Fields.SequenceEqual(new[] { "NoteTravelTimeMs" }), "Wrong types name the setting");
            Assert(Fail("{ \"MaximumTileCount\": null }").Fields.SequenceEqual(new[] { "MaximumTileCount" }), "Null for a number is rejected");
            Assert(Fail("{ \"Language\": \"Klingon\" }").Fields.SequenceEqual(new[] { "Language" }), "Unknown language files are rejected");
            Assert(Reason("{ \"Language\": 7 }").Contains("Language"), "A number is not a language id");
            Assert(Fail("{ \"Theme\": \"a/b\" }").Fields.SequenceEqual(new[] { "Theme" }), "A theme id is a file name");
            var invalid = Fail("{ \"NoteTravelTimeMs\": -5 }");
            Assert(invalid.Message.Contains("노트 이동 시간") && invalid.Fields.SequenceEqual(new[] { "NoteTravelTimeMs" }), "Validation names the setting");
            Assert(Fail("{ \"MinimumTileCount\": 8 }").Fields.Contains("MinimumTileCount"), "Minimum above maximum");
            Assert(Fail("{ \"FourKeyOrder\": \"1 1 2 3\" }").Fields.SequenceEqual(new[] { "FourKeyOrder" }), "Tile preference is validated when enabled");
            Assert(Fail("{ \"NoteColor\": \"blue\" }").Fields.SequenceEqual(new[] { "NoteColor" }), "Colors are validated");
            Assert(Reason("{ \"PlaybackSpeedPercent\": 1e400 }").Length > 0, "Out-of-range numbers are rejected");
            Assert(Fail("{ \"NoteTravelTimeMs\": 1, \"notetraveltimems\": 2 }").Fields.SequenceEqual(new[] { "NoteTravelTimeMs" }), "Repeated keys are rejected");
            Assert(Reason("{ \"NoteTravelTimeMs\": 1, \"NoteTravelTimeMs\": 2 }").Length > 0, "Exactly repeated keys are rejected");
            Assert(Reason("{ \"A\": " + new string('[', 40) + "}").Length > 0, "Deep nesting is rejected");
            Assert(Reason(new string(' ', SettingsJson.MaximumLength) + "{}").Contains("너무"), "Oversized input is rejected");
            Assert(Reason("[]", current with { Language = Languages.English }).Contains("object"), "English reasons");
            Equal(current.NoteTravelTimeMs, 650d);
        });

        yield return ("Escape pauses; a lane key resumes on that lane's note within one second", () =>
        {
            var bindings = ControlBindings.Create(new VisualizerSettings());
            Equal(bindings[KeyInterop.VirtualKeyFromKey(Key.Escape)], InputAction.Pause);
            Throws<ArgumentException>(() => new VisualizerSettings { ResetKey = "Escape" }.EnsureValid());
            Throws<ArgumentException>(() => new VisualizerSettings { LaneKeys = "A S D F J K Escape ;" }.EnsureValid());

            var clock = new Clock();
            var session = new PlaybackSession(clock);
            var judge = new NotePressJudge();
            session.LoadTimeline(new[] { 0d, 400, 800, 1200, 1600, 2600, 3000 }, new VisualizerSettings { DynamicTiming = false, SimultaneousWindowMs = 0 });
            Sequence(session.Notes.Select(n => n.Tile), 5, 4, 3, 6, 5, 4, 3);
            session.Toggle(); session.Start();
            clock.NowMs = 800; Equal(judge.Press(session, 3, 150), (int?)2);
            var missed = new List<int>();
            clock.NowMs = 1000; judge.CollectMisses(session, 150, missed); missed.Clear();
            Equal(judge.FindResumeNote(session, 6, 1000), null);
            Assert(session.Pause(), "Paused at 1000ms");
            Equal(judge.FindResumeNote(session, 6, 1000), (int?)3);
            Equal(judge.FindResumeNote(session, 5, 1000), (int?)4);
            Equal(judge.FindResumeNote(session, 4, 1000), (int?)1);
            Equal(judge.FindResumeNote(session, 1, 1000), null);
            Equal(judge.FindResumeNote(session, 3, 1000), (int?)2);
            Equal(judge.FindResumeNote(session, 3, 2000), (int?)6);
            Throws<ArgumentOutOfRangeException>(() => judge.FindResumeNote(session, 3, -1));
            judge.SkipMissesBefore(session, 3000);
            Assert(session.ResumeAt(3000), "Resume on the chosen note");
            Equal(session.State, PlaybackState.Playing); Equal(session.TimelineTimeMs, 3000d);
            clock.NowMs += 151; judge.CollectMisses(session, 150, missed);
            Sequence(missed, 3);
            Assert(!session.ResumeAt(0), "ResumeAt needs a paused session");
            session.Pause(); Assert(!session.ResumeAt(double.NaN), "Time must be finite");

            var model = new MainViewModel();
            model.ApplySettings(model.Settings with { DynamicTiming = false, SimultaneousWindowMs = 0 });
            model.Playback.LoadTimeline(new[] { 0d, 400, 800, 1200 }, model.Settings);
            model.Toggle(); model.Start();
            Assert(model.PauseFromKey() && model.Message.Contains("Esc"), "Escape pauses while playing");
            Assert(!model.PauseFromKey(), "Already paused");
            Assert(!model.ResumeFromLane(1) && model.Playback.State == PlaybackState.Paused, "No lane-1 note: stays paused");
            Assert(model.ResumeFromLane(4), "The 400ms note's lane resumes");
            Equal(model.Playback.State, PlaybackState.Playing);
            Assert(Math.Abs(model.Playback.TimelineTimeMs - 400) < 200, $"The note is on the judgment line: {model.Playback.TimelineTimeMs}");
            Assert(!model.ResumeFromLane(4), "Only while paused");
            Assert(model.KeyHint.Contains("Esc") && model.KeyHint.Contains("-/+"), "Escape and checkpoint keys are listed");
        });

        yield return ("Checkpoint auto toggle lives on the main screen and is locked off at practice speed or from a section", () =>
        {
            var timeline = new ParsedTimeline(new[] { 0d, 2000, 3000, 4000 },
                new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 2, 2000) }, true, hitFloors: new[] { 1, 2, 3, 4 });
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            session.LoadParsedTimeline(timeline, new VisualizerSettings()); session.Toggle(); session.Start();
            Assert(session.AutoCheckpointActive, "On by default at 100%");
            clock.NowMs = 3000; Equal(session.ActiveCheckpoint, (int?)2);
            session.Reset(new VisualizerSettings { PlaybackSpeedPercent = 50 }); session.Start(); clock.NowMs += 60000;
            Assert(!session.AutoCheckpointActive, "Practice speed disables it"); Equal(session.ActiveCheckpoint, (int?)1);
            session.ResetAtTile(new VisualizerSettings(), 1); session.Start(); clock.NowMs += 60000;
            Assert(!session.AutoCheckpointActive, "A section disables it"); Equal(session.ActiveCheckpoint, (int?)1);

            var model = new MainViewModel();
            model.Playback.LoadParsedTimeline(timeline, model.Settings);
            Assert(model.AutoCheckpoint && model.CanToggleAutoCheckpoint && model.AutoCheckpointHint.Contains("누르면 비활성화합니다"), "Active and clickable");
            model.AutoCheckpoint = false;
            Assert(!model.Settings.AutoCheckpointAdvance && !model.AutoCheckpointActive && model.AutoCheckpointHint.Contains("비활성화"), "Switched off on the main screen");
            model.AutoCheckpoint = true;
            Assert(model.AutoCheckpointActive, "Switched back on");
            model.ApplySettings(model.Settings with { PlaybackSpeedPercent = 50 });
            Assert(!model.AutoCheckpoint && !model.CanToggleAutoCheckpoint && model.AutoCheckpointHint.Contains("50%"), "Speed reason: " + model.AutoCheckpointHint);
            model.ApplySettings(model.Settings with { PlaybackSpeedPercent = 100 });
            model.SectionText = "1";
            Assert(!model.CanToggleAutoCheckpoint && model.AutoCheckpointHint.Contains("구간"), "Section reason: " + model.AutoCheckpointHint);
            model.SectionText = "";
            Assert(model.AutoCheckpointActive && model.CanToggleAutoCheckpoint, "Clearing the section restores it");

            var window = new MainWindow();
            Assert(window.FindName("AutoCheckpointToggle") is ToggleButton, "A clickable toggle on the checkpoint row");
            window.Close();
        });

        yield return ("A session moved from outside still tells the window about it", () =>
        {
            // Game sync starts the session itself, so without this the window keeps showing the song overlay over
            // notes that are already falling.
            var model = new MainViewModel();
            var raised = new List<string>();
            model.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
            model.NotifyPlaybackChanged();
            foreach (var name in new[] { nameof(model.ShowSongOverlay), nameof(model.StateLabel), nameof(model.CanPauseResume) })
                Assert(raised.Contains(name), name + " is raised");
        });

        yield return ("Following the game, a pause stays where the game left it", () =>
        {
            // The game owns where the song is, so putting the next note on the judgment line would fight it.
            var timeline = new ParsedTimeline(new[] { 0d, 200, 400, 600, 800, 1000 });
            var model = new MainViewModel();
            model.ApplySettings(model.Settings with { AlignNextNoteOnPause = true, StartDelayMs = 2000, GlobalDisplayOffsetMs = 0 });
            model.SelectedMap = new MapCacheEntry("s", @"C:\maps", @"s\main.adofai", "Song", MapParseStatus.Supported, timeline,
                Array.Empty<string>(), "", "test");
            model.LoadSelectedSong();
            model.Reset();
            model.Start();
            Assert(model.AutoCheckpointActive, "Auto checkpoint is on to begin with");
            model.FollowingGame = true;
            // Auto checkpoint moved the app's own choice, and the next reset then prepared from there instead of the start.
            Assert(!model.AutoCheckpointActive, "It locks while the game drives the clock");
            var caught = model.Playback.TimelineTimeMs;
            Assert(model.PauseFromKey(), "Escape pauses");
            Assert(Math.Abs(model.Playback.TimelineTimeMs - caught) < 50, "It stays where the pause caught it");
            // A lane key then only hands the playhead back; the game says where to carry on from.
            Assert(model.ResumeFromLane(model.Playback.Notes[0].Tile), "A lane key resumes");
            Equal(model.Playback.State, PlaybackState.Playing);

            // Switching following off gives every piece of it back in one go.
            model.Playback.HideNotesBeforeMs = 1234;
            model.FollowingGame = false;
            Assert(double.IsNegativeInfinity(model.Playback.HideNotesBeforeMs), "The hidden tiles come back");
            Assert(model.AutoCheckpointActive, "So does the checkpoint advance");
            model.Start();
            Assert(model.PauseFromKey(), "Escape pauses again");
            Equal(model.Playback.TimelineTimeMs,
                model.Playback.Notes[model.Presses.FirstUnpressed(model.Playback) ?? 0].HitTimeMs + model.Playback.Offsets.TotalMs);
        });

        yield return ("A pause drops the marks on notes the playhead never reached", () =>
        {
            // The app watches lane keys everywhere, and the input policy lets the game be the foreground window, so playing
            // ADOFAI marks notes in the app too. A pause that moves back then left those notes drawn as already played.
            var timeline = new ParsedTimeline(new[] { 0d, 200, 400, 600, 800, 1000 });
            var model = new MainViewModel();
            model.ApplySettings(model.Settings with { AlignNextNoteOnPause = true, StartDelayMs = 2000, GlobalDisplayOffsetMs = 0 });
            model.SelectedMap = new MapCacheEntry("s", @"C:\maps", @"s\main.adofai", "Song", MapParseStatus.Supported, timeline,
                Array.Empty<string>(), "", "test");
            model.LoadSelectedSong();
            model.Reset();
            model.Start();
            // A lane other than the first note's, so its earliest note is past the one that will wait on the line.
            var lane = model.Playback.Notes.First(note => note.Tile != model.Playback.Notes[0].Tile).Tile;
            var marked = model.Presses.Press(model.Playback, lane, 1e9);
            Assert(marked is > 0, $"A note on lane {lane} ahead of the first is marked: {marked}");

            Assert(model.PauseFromKey(), "Escape pauses");
            var line = model.Presses.FirstUnpressed(model.Playback) ?? model.Playback.Notes.Count;
            var left = Enumerable.Range(0, model.Playback.Notes.Count)
                .Where(i => model.Presses.IsPressed(model.Playback.Notes, i)).ToArray();
            Assert(left.All(i => i < line), "Nothing ahead of the line stays marked: " + string.Join(",", left));
            Equal(model.Playback.TimelineTimeMs,
                model.Playback.Notes[line].HitTimeMs + model.Playback.Offsets.TotalMs);
        });

        yield return ("A pause puts the next unpressed note on the line, and the panel header sets the practice speed", () =>
        {
            // User request: Escape used to freeze the notes where they were; now the next note the player has not pressed waits
            // on the judgment line, as the game restarts on the tile it stopped at.
            var timeline = new ParsedTimeline(new[] { 0d, 500, 1000, 1500 });
            MapCacheEntry Song() => new("s", @"C:\maps", @"s\main.adofai", "Song", MapParseStatus.Supported, timeline,
                Array.Empty<string>(), "", "test");
            var model = new MainViewModel();
            model.ApplySettings(model.Settings with { AlignNextNoteOnPause = true, StartDelayMs = 2000, GlobalDisplayOffsetMs = 0 });
            model.SelectedMap = Song();
            model.LoadSelectedSong();
            model.Reset();
            model.Start();
            Equal(model.Playback.State, PlaybackState.Playing);
            Assert(model.PauseFromKey(), "Escape pauses");
            var first = model.Playback.Notes[0];
            Equal(model.Playback.TimelineTimeMs, first.HitTimeMs + model.Playback.Offsets.TotalMs);
            // Switched off, the pause leaves the notes where it caught them.
            model.ApplySettings(model.Settings with { AlignNextNoteOnPause = false });
            model.Reset();
            model.Start();
            Assert(model.PauseFromKey() && model.Playback.TimelineTimeMs < first.HitTimeMs, "Paused where it was");

            // The speed box and its arrows: a settings change like any other, so rollback undoes it.
            // The box carries the unit now, and the separate 100% button is gone.
            Equal(model.SpeedText, "100%");
            Assert(model.StepSpeed(1) && model.Settings.PlaybackSpeedPercent == 105, "The arrow steps 5%");
            Assert(model.StepSpeed(-1) && model.Settings.PlaybackSpeedPercent == 100, "And back");
            model.SpeedText = "62.5";
            Assert(model.ApplyTypedSpeed() && model.Settings.PlaybackSpeedPercent == 62.5 && model.SpeedText == "62.5%", "A typed speed applies");
            model.SpeedText = "not a number";
            Assert(!model.ApplyTypedSpeed() && model.Settings.PlaybackSpeedPercent == 62.5 && model.SpeedText == "62.5%", "A bad number is refused");
            model.SpeedText = "5000";
            Assert(!model.ApplyTypedSpeed() && model.Settings.PlaybackSpeedPercent == 62.5, "Out of range is refused");
            Assert(model.ApplySpeed(100) && model.Settings.PlaybackSpeedPercent == 100, "Back to 100%");
            Equal(model.SettingsHistory[^1].After.PlaybackSpeedPercent, 100d);
            model.RollBack();
            Equal(model.Settings.PlaybackSpeedPercent, 62.5);

            // It sits on the note field with the song's details now (user rule), not in the song panel.
            var window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false };
            window.Show();
            var box = (TextBox)window.FindName("SpeedBox");
            var panel = (Expander)window.FindName("MapPanel");
            var field = (Border)window.FindName("FieldControls");
            Assert(!panel.IsAncestorOf(box) && field.IsAncestorOf(box), "The speed box is on the field");
            panel.IsExpanded = false;
            window.UpdateLayout();
            // It is on the field, so folding the song panel leaves it alone; it is only away because no song is loaded.
            Assert(!panel.IsAncestorOf(box), "Folding the panel does not take it with it");
            window.Close();
        });

        yield return ("The mini-game band shows on the note area until the notes come back", () =>
        {
            // User request: the free roam mini-game has no notes, so the screen says the game is playing it.
            var timeline = new ParsedTimeline(new[] { 0d, 500, 20500, 21000 }, null, false, null, null,
                new[] { new TimelineSection(500, 20500) });
            var model = new MainViewModel();
            model.ApplySettings(model.Settings with { StartDelayMs = 0, GlobalDisplayOffsetMs = 0, AlignNextNoteOnPause = false });
            model.SelectedMap = new MapCacheEntry("m", @"C:\maps", @"m\main.adofai", "Song", MapParseStatus.Supported, timeline,
                Array.Empty<string>(), "", "test");
            model.LoadSelectedSong();
            model.Reset();
            model.Start();
            model.PauseFromKey();
            model.RefreshPlaybackInfo();
            Assert(!model.ShowSectionNotice, "No band at the start");
            // Paused, the clock stands where it is put, so the band can be checked at an exact time.
            Assert(model.Playback.MovePaused(10000), "Moved into the band");
            model.RefreshPlaybackInfo();
            Assert(model.ShowSectionNotice && model.SectionNotice.Contains("10.5", StringComparison.Ordinal),
                "Counts to the next note: " + model.SectionNotice);
            Assert(model.Playback.MovePaused(20500), "Moved to its end");
            model.RefreshPlaybackInfo();
            Assert(!model.ShowSectionNotice, "The band ends where the notes come back");
            // Half speed stretches the band with the notes.
            model.ApplySettings(model.Settings with { PlaybackSpeedPercent = 50 });
            model.Reset();
            model.Start();
            model.PauseFromKey();
            Assert(model.Playback.MovePaused(40000), "Half speed doubles the band");
            model.RefreshPlaybackInfo();
            Assert(model.ShowSectionNotice && model.SectionNotice.Contains("1.0", StringComparison.Ordinal), "Near its end: " + model.SectionNotice);
        });

        yield return ("The song box: Enter loads what was typed after a load, and opening the list never eats typed letters", () =>
        {
            // User reports: after one song was loaded, typing "end time" and Enter loaded that song again; and typing
            // "hul" sometimes left only "l". Both came from the box's own list: opening it selects all the text, and its Enter takes
            // the song highlighted when the list opened.
            var window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false };
            var model = (MainViewModel)window.DataContext;
            model.ApplySettings(model.Settings with { SongSortKey = "Title", SongSortHighFirst = false });
            MapCacheEntry Song(string id, string title) => new(id, @"C:\maps", id + @"\main.adofai", title, MapParseStatus.Supported,
                new ParsedTimeline(new[] { 0d, 500, 1000, 1500 }), Array.Empty<string>(), "", "test", new MapSongInfo(null, title, null, null));
            void Pump()
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            // Shown, so the keys go through a real window. It leaves the app store alone in tests, so the list is only these songs.
            window.Show();
            Pump();
            Assert(model.Library.Count == 0, "The shown window read no song cache");
            model.SetLibrary([Song("a", "Emerald"), Song("e", "End Time"), Song("h", "Raimukun - C†hulhu"), Song("o", "Omega")]);
            var picker = (ComboBox)window.FindName("SongPicker");
            picker.ApplyTemplate();
            var box = (TextBox)picker.Template.FindName("PART_EditableTextBox", picker);
            // A key as the keyboard sends it: the preview first, the key itself unless the preview took it, then the release.
            void Stroke(Key key, bool release = true)
            {
                var source = PresentationSource.FromVisual(box)!;
                var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                box.RaiseEvent(down);
                if (!down.Handled) box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
                if (release) Up(key);
                Pump();
            }
            void Up(Key key) => box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, key) { RoutedEvent = Keyboard.KeyUpEvent });
            // Typing puts the letter where the caret or selection is, as a key press does.
            void Type(string letter)
            {
                box.SelectedText = letter;
                box.Select(box.SelectionStart + box.SelectionLength, 0);
            }
            // This window is never activated, so the box has no keyboard focus and a title change after a pump moves the caret to
            // the start (ShowSongTitleStart); while typing, the app keeps the focus and the caret stays at the end.
            void Settle()
            {
                Pump();
                box.Select(box.Text.Length, 0);
            }

            // The first song: search for Emerald and load it.
            box.Focus();
            box.SelectAll();
            Type("emer");
            Up(System.Windows.Input.Key.R);
            Pump();
            Stroke(System.Windows.Input.Key.Enter);
            Equal(model.LoadedEntry?.Id, "a");

            // The reported case: the loaded title is replaced by typing, the list opens on the first letter while the loaded song
            // still matches, and Enter loads what was typed, not the song highlighted when the list opened.
            box.Focus();
            box.SelectAll();
            Type("e");
            Up(System.Windows.Input.Key.E);
            Settle();
            Assert(model.LibraryView.Cast<MapCacheEntry>().Any(entry => entry.Id == "a") && picker.IsDropDownOpen, "Emerald still matches e as the list opens");
            foreach (var letter in "nd time")
            {
                Type(letter.ToString());
                Up(System.Windows.Input.Key.A);
            }
            Settle();
            Equal(box.Text, "end time");
            Stroke(System.Windows.Input.Key.Enter);
            Equal(model.LoadedEntry?.Id, "e");
            Assert(!picker.IsDropDownOpen, "Enter closes the list");

            // Letters typed before the list opened stay: h and u are down before h comes up, which opens the list.
            box.Focus();
            box.SelectAll();
            Type("h");
            Type("u");
            Up(System.Windows.Input.Key.H);
            Type("l");
            Up(System.Windows.Input.Key.U);
            Up(System.Windows.Input.Key.L);
            Pump();
            Equal(box.Text, "hul");
            Assert(picker.IsDropDownOpen, "The list is open");
            Stroke(System.Windows.Input.Key.Enter);
            Equal(model.LoadedEntry?.Id, "h");

            // The arrows pick in the searched list and Enter loads the picked song; the search stays as typed.
            box.Focus();
            box.SelectAll();
            Type("a");
            Up(System.Windows.Input.Key.A);
            Pump();
            var searched = model.LibraryView.Cast<MapCacheEntry>().Select(entry => entry.Id).ToArray();
            Stroke(System.Windows.Input.Key.Down);
            Stroke(System.Windows.Input.Key.Down);
            Assert(model.LibraryView.Cast<MapCacheEntry>().Select(entry => entry.Id).SequenceEqual(searched), "The search stays: " + string.Join(" ", searched));
            Equal(model.SelectedMap?.Id, searched[1]);
            Stroke(System.Windows.Input.Key.Enter);
            Equal(model.LoadedEntry?.Id, searched[1]);
            window.Close();
        });

        yield return ("The bundled example is loaded directly and never listed in the song library", () =>
        {
            var root = Path.Combine(artifacts, "example-library", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "Test-owned fixture");
            File.WriteAllText(Path.Combine(root, "Visualizer.sln"), "Test-owned fixture");
            var example = Path.Combine(root, ".example");
            var maps = Path.Combine(root, "maps");
            var timeline = new ParsedTimeline(new[] { 0d, 500 });
            var cache = new WorkspaceMapCache(root);
            cache.ReplaceAsync(new[]
            {
                new MapCacheEntry("old-example", example, "main.adofai", "Plum - R", MapParseStatus.Supported, timeline, Array.Empty<string>(), "", "old"),
                new MapCacheEntry("workshop", maps, Path.Combine("2301231290", "main.adofai"), "Plum - R", MapParseStatus.Supported, timeline, Array.Empty<string>(), "", "old")
            }).GetAwaiter().GetResult();
            var model = new MainViewModel { ExampleRoot = example + Path.DirectorySeparatorChar };
            model.RefreshLibraryAsync(cache).GetAwaiter().GetResult();
            Assert(model.Library.Select(e => e.Id).SequenceEqual(new[] { "workshop" }), "Only the Workshop Plum - R is listed");

            var repo = WorkspaceMapCache.FindRepositoryRoot(AppContext.BaseDirectory);
            var source = new ReadOnlyDirectoryMapSource(Path.Combine(repo, ".example"));
            var parsed = new AdofaiTimelineParser().Parse(source.ReadAsync("main.adofai").GetAwaiter().GetResult());
            model.LoadExample(new MapCacheEntry("bundled-example", example, "main.adofai", parsed.SongName!, parsed.Status,
                parsed.Timeline, parsed.Warnings.ToArray(), "", new AdofaiTimelineParser().Version));
            Equal(model.SongName, "Plum - R"); Equal(model.Playback.Notes.Count, 726);
            Assert(model.Library.Select(e => e.Id).SequenceEqual(new[] { "workshop" }), "Loading the example does not add it to the library");
            Equal(model.SelectedMap?.Id, "workshop");
        });

        yield return ("4-lane chord failures offer the smallest wider maximum once and switch on consent", () =>
        {
            static ParsedTimeline Wide() => HeldChord(3);
            var asked = new List<string>();
            var declined = new MainViewModel { ConfirmWiderLanes = message => { asked.Add(message); return false; } };
            declined.SelectedMap = Entry("wide", Wide());
            declined.LoadSelectedSong();
            // Three notes beside the long note fit six lanes, so 6 (not 8) is offered.
            Equal(asked.Count, 1); Assert(asked[0].Contains("6개") && asked[0].Contains("노트 배치 중단"), "The question carries the reason: " + asked[0]);
            Equal(declined.Settings.MaximumTileCount, 4); Equal(declined.Playback.Notes.Count, 0);
            Assert(declined.Message.Contains("동타를 배치할 수 없습니다"), "Declining keeps the reason visible");
            declined.Toggle(); declined.Reset();
            Equal(asked.Count, 1);

            var accepted = new MainViewModel { ConfirmWiderLanes = _ => true };
            accepted.SelectedMap = Entry("wide", Wide());
            accepted.LoadSelectedSong();
            Equal(accepted.Settings.MaximumTileCount, 6); Equal(accepted.Playback.Settings.MaximumTileCount, 6);
            Equal(accepted.Playback.Notes.Count, 5); Assert(accepted.Message.Contains("6키"), "The switch is reported: " + accepted.Message);
            accepted.ApplySettings(accepted.Settings with { MaximumTileCount = 4, NoteTravelTimeMs = 900 });
            Equal(accepted.Settings.MaximumTileCount, 6); Equal(accepted.Settings.NoteTravelTimeMs, 900d);

            var none = new MainViewModel { ConfirmWiderLanes = _ => throw new InvalidOperationException("Must not ask") };
            none.SelectedMap = Entry("wider", HeldChord(5));
            none.LoadSelectedSong();
            Equal(none.Playback.Notes.Count, 0); Equal(none.Settings.MaximumTileCount, 4);
            var silent = new MainViewModel();
            silent.SelectedMap = Entry("wide", Wide());
            silent.LoadSelectedSong();
            Equal(silent.Settings.MaximumTileCount, 4);
        });

        yield return ("Settings window: collapsible sections, help marks, colors, merged 1K tile, preference lock, blank defaults, JSON and reset", () =>
        {
            var start = new VisualizerSettings { NoteTravelTimeMs = 650 };
            var window = new SettingsPanel(start, new MemorySettingsLayoutStore());
            var copied = "";
            var confirms = new List<string>();
            var answer = true;
            window.CopyText = text => copied = text;
            window.Confirm = (title, message, accept) => { confirms.Add(title + "|" + accept); return answer; };
            var labels = FindAll<TextBlock>(window).Select(t => t.Text).ToList();
            Assert(labels.Contains("시각화") && labels.Contains("디자인") && labels.Contains("체크포인트") && !labels.Contains("노트 표시"), "Sections");
            int At(string text) => labels.IndexOf(text);
            Assert(At("배속 (%)") < At("동타 판정 간격 (ms↓)") && At("동타 판정 간격 (ms↓)") < At("시작 대기 시간 (ms)")
                && At("시작 대기 시간 (ms)") < At("판정 보정 노트 수") && At("배속 (%)") > 0, "Timing order");
            foreach (var row in new[] { "언어(Language)", "맵 폴더", "안내 버튼", "곡 정보 표시", "이전 체크포인트 키", "다음 체크포인트 키", "입력 효과", "놓친 타일 효과",
                         "단타 노트", "동타 노트", "판정선", "실패 효과", "레인 입력 효과", "최소 타일 수", "타일 순서 선호도", "1키 패턴 · 시작 타일",
                         "여분 레인 배치", "순서 선호도 사용" })
                Assert(At(row) >= 0, "Missing row: " + row);
            Assert(!labels.Contains("시작 타일 (1~8)") && !labels.Contains("1키 패턴"), "The 1K pattern and initial tile are one field");
            Assert(!labels.Any(t => t.StartsWith("체크포인트 타일이 판정선에", StringComparison.Ordinal)), "Explanations are no longer paragraphs");
            var tips = HelpTips(window);
            Assert(tips.Any(t => t.Contains("표시 보정을 한 번")), "Calibration help");
            Assert(tips.Any(t => t.Contains("0이면 정확히 같은 시각만")), "Chord window help");
            Assert(tips.Any(t => t.Contains("순서") && t.Contains("고급 설정")), "Preference help points to Advanced");
            Assert(tips.Count >= 10, "Help marks: " + tips.Count);
            Assert(FindAll<Border>(window).Where(b => b.ToolTip is ToolTip).All(b => b.Child is TextBlock { Text: "?" }), "Round ? marks");

            var expanders = FindAll<Expander>(window).ToList();
            // Twelve cards (the song list, the checkpoints and the judgment bar have their own); the last holds the
            // experiments, and the advanced card before it is the only one closed.
            Equal(expanders.Count, 12);
            Assert(expanders.Take(10).All(e => e.IsExpanded) && !expanders[^2].IsExpanded, "Every section is open except Advanced");
            // While experiments are off the card stays on screen and only stops taking input (user rule): hiding it took
            // everything in it away, the editor button included.
            Assert(FindAll<Border>(window).Any(b => b.Child == expanders[^1] && b.Visibility == Visibility.Visible),
                "The experimental card is still shown while experiments are off");
            Assert(expanders[^1].Content is UIElement { IsEnabled: false }, "but nothing in it takes input");
            var advanced = expanders[^2];
            Equal(FindAll<KeyCaptureBox>(window).Count(), 14);

            // The tile preference is off by default: order boxes are locked to the default order.
            var order = FindAll<TextBox>(window).Single(t => t.Text == "4 3 2 1");
            Assert(!order.IsEnabled, "Order boxes are locked while the preference is off");
            var preference = (CheckBox)window.InputFor(nameof(VisualizerSettings.EnableTilePreference))!;
            preference.IsChecked = true;
            Assert(order.IsEnabled, "Enabling unlocks the order boxes");
            order.Text = "1 2 3 4";
            Equal(window.BuildSettings().FourKeyOrder, "1 2 3 4");
            preference.IsChecked = false;
            // The locked box shows the default order; the own order is kept for when the preference returns.
            Equal(order.Text, "4 3 2 1"); Equal(window.BuildSettings().FourKeyOrder, "1 2 3 4"); Sequence(window.BuildSettings().GetPattern(4), 6, 5, 4, 3);
            preference.IsChecked = true;
            Equal(order.Text, "1 2 3 4");
            preference.IsChecked = false;

            var initial = FindAll<TextBox>(window).Single(t => t.Text == "5");
            initial.Text = "4";
            Equal(window.BuildSettings().InitialTile, 4); Sequence(window.BuildSettings().GetPattern(1), 4);
            initial.Text = "5";

            // Pattern boxes are tile sets: any order is accepted and Apply stores them ascending.
            var fourTiles = FindAll<TextBox>(window).Single(t => t.Text == "3 4 5 6");
            var patternState = window.SectionState("SectionPattern"); // Already changed: the kept order above differs from the applied one.
            fourTiles.Text = "6 5 3 4";
            Equal(window.SectionState("SectionPattern"), patternState);
            Equal(window.BuildSettings().FourKeyPattern, "3 4 5 6"); Equal(fourTiles.Text, "3 4 5 6");

            // Blank fields return to defaults; colors accept RGB and store HEX.
            var travel = FindAll<TextBox>(window).Single(t => t.Text == "650");
            travel.Text = "  ";
            Equal(window.BuildSettings().NoteTravelTimeMs, 600d); Equal(travel.Text, "600");
            // Design colors are kept as "R, G, B"; the row shows one box per channel over this hidden text.
            var noteColor = FindAll<TextBox>(window).Single(t => t.Text == "94, 218, 242");
            noteColor.Text = "rgb(255, 0, 0)";
            Equal(window.BuildSettings().NoteColor, "255, 0, 0");
            noteColor.Text = "";
            Equal(window.BuildSettings().NoteColor, "94, 218, 242"); Equal(noteColor.Text, "94, 218, 242");
            noteColor.Text = "blue";
            Throws<ArgumentException>(() => window.BuildSettings());
            noteColor.Text = "94, 218, 242";
            // Lane backgrounds follow the theme and have no row in the window; the setting stays empty.
            Assert(!FindAll<TextBlock>(window).Any(t => t.Text == UiText.Get(Languages.Korean, "Lane background colors")), "No lane background row");
            Equal(FindAll<TextBox>(window).Count(t => t.FontSize == 9 && t.Text.Length == 0), 0);
            Equal(window.BuildSettings().LaneBackgroundColors, "");

            var buttons = FindAll<Button>(window).ToList();
            Button ByText(string key) => buttons.Single(b => Equals(b.Content, UiText.Get(Languages.Korean, key)));
            var reset = ByText("ResetSettings");
            Assert(ReferenceEquals(reset.Style, window.FindResource("DangerButton")), "Reset is emphasized");
            var buttonRow = (UniformGrid)LogicalTreeHelper.GetParent(reset);
            Assert(buttonRow.Columns == 3 && buttonRow.Children.Count == 3 && buttonRow.Margin.Top >= 12, "Three equal buttons (export, import, reset) set apart from the fields");
            Click(ByText("ExportSettings"));
            Equal(copied, SettingsJson.ExportPortable(start));
            Assert(!copied!.Contains("MapRootDirectory", StringComparison.Ordinal) && !copied.Contains("DataRootDirectory", StringComparison.Ordinal),
                "The copy leaves this PC's folders out");
            var json = FindAll<TextBox>(window).Single(t => t.AcceptsReturn);
            var panel = (FrameworkElement)LogicalTreeHelper.GetParent(json);
            Equal(panel.Visibility, Visibility.Collapsed);
            Click(ByText("ImportSettings"));
            Equal(panel.Visibility, Visibility.Visible);
            var jsonButtons = FindAll<Button>(panel).ToList();
            Assert(jsonButtons.Select(b => b.Content).SequenceEqual(new object[] { "적용", "취소" }), "Apply, then cancel");
            string Status() => string.Join("\n", FindAll<TextBlock>(window).Select(t => t.Text));

            json.Text = "{ \"NoteTravelTimeMs\": -1, \"Zoom\": 2 }";
            Click(jsonButtons[0]);
            Equal(panel.Visibility, Visibility.Visible);
            Assert(Status().Contains("문제 항목: Zoom"), "Unknown setting is named");
            Equal(json.SelectedText, "\"Zoom\"");
            json.Text = "{ \"NoteTravelTimeMs\": -5 }";
            Click(jsonButtons[0]);
            Assert(Status().Contains("NoteTravelTimeMs (노트 이동 시간 (ms))"), "Validation names the setting and its label");
            Equal(json.SelectedText, "\"NoteTravelTimeMs\"");
            json.Text = "{ \"ResetKey\": \"Nope\" }";
            Click(jsonButtons[0]);
            Assert(Status().Contains("ResetKey (재설정 키)"), "Unsupported key names its setting");
            json.Text = "{\n  \"NoteTravelTimeMs\": 900,\n  oops\n}";
            Click(jsonButtons[0]);
            Assert(Status().Contains("줄 3") && json.SelectionStart > 0, "Syntax error position is selected");
            Equal(window.BuildSettings().NoteTravelTimeMs, 600d);

            json.Text = "{ \"NoteTravelTimeMs\": 900, \"EnableTilePreference\": true, \"FourKeyOrder\": \"1 2 3 4\", \"ResetKey\": \"R\" }";
            Click(jsonButtons[0]);
            Equal(panel.Visibility, Visibility.Collapsed); Equal(json.Text, "");
            Assert(FindAll<TextBox>(window).Any(t => t.Text == "900") && order.Text == "1 2 3 4" && order.IsEnabled, "The form shows the imported values");
            Assert(FindAll<KeyCaptureBox>(window).Any(b => b.Token == "R"), "Key boxes show imported keys");
            Equal(window.AppliedSettings, null);
            Equal(window.BuildSettings().NoteTravelTimeMs, 900d);
            Equal(confirms.Count, 0);

            Click(ByText("ImportSettings"));
            json.Text = "{ }";
            answer = false;
            Click(jsonButtons[1]);
            Equal(panel.Visibility, Visibility.Visible); Equal(json.Text, "{ }"); Equal(confirms.Count, 1);
            answer = true;
            Click(jsonButtons[1]);
            Equal(panel.Visibility, Visibility.Collapsed); Equal(json.Text, ""); Equal(confirms[^1], "입력 닫기|닫기");
            Click(ByText("ImportSettings"));
            Click(jsonButtons[1]);
            Equal(panel.Visibility, Visibility.Collapsed); Equal(confirms.Count, 2);

            answer = false;
            Click(reset);
            Equal(window.BuildSettings().NoteTravelTimeMs, 900d);
            answer = true;
            Click(reset);
            Equal(confirms[^1], "설정 초기화|확인");
            Equal(window.BuildSettings(), new VisualizerSettings());
            Equal(window.AppliedSettings, null);

            // The state word beside a toggle follows it and never repeats the setting name.
            var guide = (CheckBox)window.InputFor(nameof(VisualizerSettings.ShowGuideButtons))!;
            string StateOf(CheckBox box) => ((StackPanel)LogicalTreeHelper.GetParent(box)).Children.OfType<TextBlock>().Single().Text;
            Equal(StateOf(guide), "표시");
            guide.IsChecked = false;
            Equal(StateOf(guide), "숨김");
            guide.IsChecked = true;
            var scope = (CheckBox)window.InputFor(nameof(VisualizerSettings.OnlyReactWhenAdofaiForeground))!;
            Equal(StateOf(scope), "게임·앱에서만");
            scope.IsChecked = false;
            Equal(StateOf(scope), "모든 앱에서");
            scope.IsChecked = true;
            var missEffect = (CheckBox)window.InputFor(nameof(VisualizerSettings.ShowMissEffect))!;
            Equal(StateOf(missEffect), "효과 표시");
            missEffect.IsChecked = false;
            Equal(StateOf(missEffect), "효과 없음");
            missEffect.IsChecked = true;
            // Every toggle says what its state does; plain on/off told too little.
            var toggles = typeof(VisualizerSettings).GetProperties().Where(property => property.PropertyType == typeof(bool))
                .Select(property => property.Name).Where(name => window.InputFor(name) is CheckBox).ToList();
            Assert(toggles.Count >= 16, "Toggles found: " + toggles.Count);
            foreach (var name in toggles)
            {
                var box = (CheckBox)window.InputFor(name)!;
                var was = box.IsChecked;
                foreach (var state in new[] { true, false })
                {
                    box.IsChecked = state;
                    Assert(StateOf(box) is { Length: > 0 } word && word != "켜짐" && word != "꺼짐", $"{name} says '{StateOf(box)}'");
                }
                box.IsChecked = was;
            }

            // Enter applies while no setting holds the focus, or a toggle does; typing, lists and buttons keep Enter.
            Assert(window.EnterApplies(null) && window.EnterApplies(window), "Nothing focused: Enter applies");
            Assert(window.EnterApplies(guide), "A toggle has no Enter of its own");
            Assert(!window.EnterApplies(FindAll<TextBox>(window).First()), "A text box keeps Enter");
            Assert(!window.EnterApplies(FindAll<ComboBox>(window).First()), "A list keeps Enter");
            Assert(!window.EnterApplies(reset), "A plain button keeps Enter");

            advanced.IsExpanded = true;
            Capture(window, Path.Combine(artifacts, "settings-advanced.png"), 520, 3600);
            // The map sync and the rebuild share the song list row as equal quarters (a margin once made one narrower).
            var editSong = FindAll<Button>(window).Single(b => Equals(b.Content, UiText.Get(Languages.Korean, "Sync")));
            var resync = FindAll<Button>(window).Single(b => Equals(b.Content, UiText.Get(Languages.Korean, "Resync")));
            // Layout rounding snaps each edge to a whole device pixel, so equal cells can still differ by one of them
            // (0.8 dip at 125% scaling). A margin bug is worth several pixels, so one is the right tolerance here.
            var pixel = 1 / VisualTreeHelper.GetDpi(window).DpiScaleX;
            Assert(editSong.ActualWidth > 0 && Math.Abs(editSong.ActualWidth - resync.ActualWidth) <= pixel + 0.01,
                $"Equal widths: {editSong.ActualWidth:0.##} and {resync.ActualWidth:0.##} (one device pixel is {pixel:0.##})");
            // Every button label fits its cell: a four-column row once clipped two of them.
            foreach (var label in new[] { "설정 내보내기", "설정 불러오기", "설정 초기화" })
            {
                var button = FindAll<Button>(window).Single(b => Equals(b.Content, label));
                var room = button.ActualWidth - button.Padding.Left - button.Padding.Right;
                var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch),
                    button.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(button).PixelsPerDip).Width;
                Assert(room >= text, $"\"{label}\" is clipped: {room:0.#} of {text:0.#} needed");
            }
            // Turning on experiments unlocks their card.
            var experiments = (CheckBox)window.InputFor(nameof(VisualizerSettings.ExperimentalFeatures))!;
            experiments.IsChecked = true;
            Assert(FindAll<Border>(window).Any(b => b.Child == expanders[^1] && b.Visibility == Visibility.Visible),
                "The experimental card appears once experiments are on");
            Equal(window.BuildSettings().ExperimentalFeatures, true);
            var experimentalCard = (FrameworkElement)expanders[^1].Content;
            // Four placement rules are being tried out; they only apply while experiments are on.
            Assert(FindAll<CheckBox>(experimentalCard).Count() == 4 && !FindAll<ComboBox>(experimentalCard).Any(),
                "The experimental card holds the four rules");
            Capture(window, Path.Combine(artifacts, "settings-experimental.png"), 520, 3600);
            // A switched-on toggle is the accent blue of the pill toggles with a barely visible gradient, not the purple of the
            // primary buttons.
            var track = (Border)experiments.Template.FindName("Track", experiments);
            var accent = ((SolidColorBrush)window.FindResource("AccentBrush")).Color;
            // Flat colour, never a gradient (user rule): blue means active, grey off, red careful.
            Assert(track.Background is SolidColorBrush on && on.Color == accent,
                "The switched-on track is the flat accent: " + track.Background);
            Assert(!ReferenceEquals(track.Background, window.FindResource("PrimaryBrush")), "Not the primary gradient");
            experiments.IsChecked = false;
            // The theme left the header for the top of the Design card (user rule).
            var theme = (FrameworkElement)window.FindName("ThemeSelector");
            var header = (FrameworkElement)window.FindName("HeaderBar");
            Assert(!header.IsAncestorOf(theme), "The theme is out of the header");
            var design = (FrameworkElement)LogicalTreeHelper.GetParent(expanders[SectionIndex(window, "SectionColors")]);
            Assert(design.IsAncestorOf(theme), "and in the Design card");

            var english = new SettingsPanel(new VisualizerSettings { Language = Languages.English }, new MemorySettingsLayoutStore());
            var englishLabels = FindAll<TextBlock>(english).Select(t => t.Text).ToList();
            Assert(englishLabels.Contains("Advanced") && englishLabels.Contains("Visualization") && englishLabels.Contains("Design"), "English headers");
        });

        yield return ("Searching the settings hides the cards with nothing in them and leaves their own state alone", () =>
        {
            // User request: the search keeps the cards, hides a card only when none of its rows match, opens what it
            // finds, and puts a card the user had closed back as it was once the box is empty.
            var store = new MemorySettingsLayoutStore();
            var panel = new SettingsPanel(new VisualizerSettings(), store);
            var search = (TextBox)panel.FindName("SearchBox")!;
            var none = (TextBlock)panel.FindName("SearchNone")!;

            // The cursor has to stand exactly where the first letter will be, which is where the grey prompt starts
            // (user report: it sat inside the prompt's first letter). The box is bordered and padded, so the prompt
            // cannot pick a round number of its own.
            panel.Measure(new Size(500, 900));
            panel.Arrange(new Rect(0, 0, 500, 900));
            panel.UpdateLayout();
            var hint = (TextBlock)panel.FindName("SearchHint")!;
            var caret = search.GetRectFromCharacterIndex(0);
            var promptAt = hint.TranslatePoint(new Point(0, 0), search);
            Assert(Math.Abs(caret.X - promptAt.X) < 0.6, $"The cursor starts where the prompt does: {caret.X} vs {promptAt.X}");
            Assert(Math.Abs((caret.Y + caret.Height / 2) - (promptAt.Y + hint.ActualHeight / 2)) < 0.6,
                $"And on the same line: caret {caret.Y}+{caret.Height}, prompt {promptAt.Y}+{hint.ActualHeight}");
            var cards = FindAll<Expander>(panel).ToList();
            static Border Card(Expander card) => (Border)card.Parent;
            cards[1].IsExpanded = false;
            Equal(store.IsExpanded(panel.SectionKeys[1]), false);

            panel.Search("언어");
            Assert(cards[0].IsExpanded && Card(cards[0]).Visibility == Visibility.Visible, "The card holding the match opens");
            Assert(cards.Skip(1).All(card => Card(card).Visibility == Visibility.Collapsed), "Cards with no match are hidden");
            Assert(none.Visibility == Visibility.Collapsed, "Something was found");
            var rows = FindAll<TextBlock>(cards[0]).Count(text => text.Text.Contains("언어", StringComparison.Ordinal)
                && text.IsVisible == false);
            search.Text = "언어";
            Capture(panel, Path.Combine(artifacts, "settings-search.png"), 480, 800);

            // A word nothing answers to says so, and the cards are all out of the way.
            panel.Search("존재하지않는설정이름");
            Assert(none.Visibility == Visibility.Visible && cards.All(card => Card(card).Visibility == Visibility.Collapsed),
                "Nothing matches and it says so");

            // Emptying the box brings every card back, the one that was closed still closed.
            panel.Search("");
            Assert(cards.All(card => Card(card).Visibility == Visibility.Visible || card == cards[^1]), "The cards are back");
            Assert(!cards[1].IsExpanded && store.IsExpanded(panel.SectionKeys[1]) == false, "The card that was closed stays closed");
            Assert(rows >= 0, "The rows were counted");
        });

        yield return ("The settings preview plays the template with the settings it holds, applying none of them", () =>
        {
            // User request: a preview button of its own. The settings step aside, the notes come down with the rules as
            // they stand in the panel, nothing may be loaded or changed meanwhile, and going back applies nothing.
            MainWindow.UseAppStore = false;
            var window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false, Width = 480, Height = 800 };
            window.Show();
            var model = (MainViewModel)window.DataContext;
            var entry = new MapCacheEntry("song", "root", "main.adofai", "Loaded song", MapParseStatus.Supported,
                new ParsedTimeline(new[] { 0d, 500, 1000 }), [], "hash", "parser");
            model.LoadCached(entry);
            Click((ButtonBase)window.FindName("SettingsButton")!);
            Pump();
            var host = (Border)window.FindName("SettingsHost")!;
            var panel = (SettingsPanel)host.Child!;

            // A change that has not been applied: the preview shows it, the app keeps the old value.
            ((TextBox)panel.InputFor(nameof(VisualizerSettings.NoteTravelTimeMs))!).Text = "900";
            panel.ShowPreview();
            Equal(model.Playback.Settings.NoteTravelTimeMs, 900d);
            Equal(model.Settings.NoteTravelTimeMs, 600d);
            Assert(model.Playback.Notes.Count > 0 && model.Playback.State is PlaybackState.Playing or PlaybackState.Waiting,
                $"The template plays by itself: {model.Playback.State}");
            var bar = (Border)window.FindName("PreviewBar")!;
            Assert(bar.Visibility == Visibility.Visible && !host.IsHitTestVisible && ReferenceEquals(host.Child, panel),
                "The settings step aside without being put away");
            window.UpdateLayout();
            Capture(window, Path.Combine(artifacts, "settings-preview.png"), 480, 800);

            // Nothing else answers the mouse, so no song can be loaded or changed under the preview.
            Assert(Swallowed((ButtonBase)window.FindName("LoadButton")!), "Load does nothing while the preview plays");
            Assert(Swallowed((ComboBox)window.FindName("SongPicker")!), "Nor does the song box");
            var back = (ButtonBase)window.FindName("PreviewBackButton")!;
            Assert(!Swallowed(back), "The way back is the exception");

            Click(back);
            Assert(model.LoadedSongName == "Loaded song" && bar.Visibility == Visibility.Collapsed && host.IsHitTestVisible,
                "Back to the settings, with the song that was loaded before");
            Equal(((TextBox)panel.InputFor(nameof(VisualizerSettings.NoteTravelTimeMs))!).Text, "900");
            Equal(model.Settings.NoteTravelTimeMs, 600d);
            window.Close();
        });

        yield return ("A message says how long ago it arrived, and the frame loop only rebuilds those words once a minute", () =>
        {
            // User request: (방금 전) under a minute, the minutes up to ten, then (오래 됨). The frame loop asks every
            // frame, so it must not build a string it throws away (performance rule).
            var model = new MainViewModel();
            model.SetMessage("곡을 불러왔습니다.");
            Equal(model.MessageText, UiText.Get(Languages.Korean, "JustNow") + " 곡을 불러왔습니다.");
            // The two boundaries of the rule: the tenth minute still counts, the eleventh is "a while ago".
            var text = model.Text;
            Equal(MainViewModel.AgeText(text, 0), UiText.Get(Languages.Korean, "JustNow"));
            Equal(MainViewModel.AgeText(text, 1), UiText.Get(Languages.Korean, "MinutesAgo").Replace("{0}", "1", StringComparison.Ordinal));
            Equal(MainViewModel.AgeText(text, 10), UiText.Get(Languages.Korean, "MinutesAgo").Replace("{0}", "10", StringComparison.Ordinal));
            Equal(MainViewModel.AgeText(text, 11), UiText.Get(Languages.Korean, "LongAgo"));
            Equal(MainViewModel.AgeText(text, 600), UiText.Get(Languages.Korean, "LongAgo"));

            // The frame loop asks every frame and must not tell the window anything while the words are the same.
            var raised = 0;
            model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.MessageText)) raised++; };
            for (var i = 0; i < 500; i++) model.RefreshMessageAge();
            Equal(raised, 0);
            // An empty message has no age in front of it.
            model.SetMessage("");
            Equal(model.MessageText, "");
        });

        yield return ("A settings panel is built while the app is idle, so the button does not wait for one", () =>
        {
            // Building a panel and laying it out costs about four tenths of a second and does not get cheaper the second
            // time, so one is made in advance and used as it is. It is never shown before it is used: no state carries over.
            MainWindow.UseAppStore = false;
            var window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false, Width = 480, Height = 800 };
            window.Show();
            Assert(!window.SettingsReady, "Nothing is built before the app is idle");
            Pump(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert(window.SettingsReady, "A spare is waiting once it is");

            Click((ButtonBase)window.FindName("SettingsButton")!);
            Assert(!window.SettingsReady, "Opening the settings uses it up");
            var host = (Border)window.FindName("SettingsHost")!;
            Assert(host.Child is SettingsPanel, "And that is what opened");
            // A spare is not built over an open panel; closing them starts the next one.
            Pump(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert(!window.SettingsReady, "None is made while the settings are open");
            ((SettingsPanel)host.Child!).Leave();
            Pump(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert(window.SettingsReady, "The next one is ready for the next press");

            // Changing the settings makes the spare stale, and a fresh one takes its place.
            var model = (MainViewModel)window.DataContext;
            model.ApplySettings(model.Settings with { NoteTravelTimeMs = 777 });
            Assert(!window.SettingsReady, "A spare from older settings is not used");
            Pump(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert(window.SettingsReady, "A new one is built from the settings as they are");
            window.Close();
        });

        yield return ("The settings open inside the main window, cover it and leave with Escape", () =>
        {
            // User request: the settings are not a window of their own any more. They slide in over the note field from
            // the right, so the console and everything else stay reachable while they are open.
            MainWindow.UseAppStore = false;
            var window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false, Width = 480, Height = 800 };
            window.Show();
            var host = (Border)window.FindName("SettingsHost")!;
            Assert(host.Child is null && !host.IsHitTestVisible, "Nothing is there until the settings are asked for");

            var button = (System.Windows.Controls.Primitives.ButtonBase)window.FindName("SettingsButton")!;
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
            // The settings are simply there once they are asked for: no movement to wait for (user rule).
            var slide = (TranslateTransform)window.FindName("SettingsSlide")!;
            Equal(slide.X, 0d);
            var panel = host.Child as SettingsPanel;
            Assert(panel is not null && host.IsHitTestVisible, "The settings are in the window itself");
            // All of the window's body, and never squeezed below the width the rows were built for. The body, not the
            // window: Window.ActualWidth counts the frame, and a host that wide hung past the left edge (user report).
            var body = (FrameworkElement)window.FindName("WindowBody")!;
            Assert(Math.Abs(host.Width - body.ActualWidth) < 0.5, $"All of the body: {host.Width} of {body.ActualWidth}");
            Assert(body.ActualWidth <= window.ActualWidth, "The body is inside the window");
            window.UpdateLayout();
            Assert(host.TranslatePoint(new Point(0, 0), body).X > -0.5, "The panel starts at the left edge, not before it");
            Capture(window, Path.Combine(artifacts, "settings-panel.png"), 480, 800);

            // Escape is what the dialog's cancel button used to be. Nothing was changed, so it leaves without asking.
            panel!.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
            { RoutedEvent = UIElement.PreviewKeyDownEvent });
            Assert(!host.IsHitTestVisible, "Escape puts them away");
            window.Close();
        });

        yield return ("The note beside Apply follows the focused or pointed setting; toggles carry no text", () =>
        {
            // User request: the text that used to sit beside each toggle now appears beside Apply, for whichever
            // setting the pointer is level with, and a focused input wins over the pointer.
            var window = new SettingsPanel(new VisualizerSettings(), new MemorySettingsLayoutStore());
            var note = (TextBlock)window.FindName("SettingsNote");
            Equal(note.Text, UiText.Get(Languages.Korean, "SettingsNote"));
            Assert(FindAll<CheckBox>(window).All(box => box.Content is null), "No text beside a toggle");

            // The buttons keep one height whether the note beside them takes one line or two.
            window.Measure(new Size(640, 900));
            window.Arrange(new Rect(0, 0, 640, 900));
            var apply = (Button)window.FindName("ApplyButton");
            var wrapped = apply.ActualHeight;

            var travel = window.InputFor(nameof(VisualizerSettings.NoteTravelTimeMs))!;
            travel.RaiseEvent(new RoutedEventArgs(UIElement.GotFocusEvent, travel));
            Equal(note.Text, UiText.Get(Languages.Korean, "Hint.NoteTravelTimeMs"));
            window.UpdateLayout();
            // The three bottom buttons are one size now (user rule). Measuring the settings when they were a window
            // left every ActualHeight at 0, so this line once passed without ever looking at a button.
            Assert(Math.Abs(apply.ActualHeight - wrapped) < 0.5 && wrapped is > 27.5 and < 28.5,
                $"One height for the buttons: {wrapped} vs {apply.ActualHeight}");
            // The rows' own controls follow the smaller text too (user report: only the letters had shrunk). Collapsed
            // cards are not arranged, so only the controls that were actually laid out are measured.
            var fields = (FrameworkElement)window.FindName("Fields")!;
            var laid = FindAll<FrameworkElement>(fields).Where(each => each.ActualHeight > 0).ToList();
            var tall = laid.Where(each => each is ComboBox or TextBox || (each is ButtonBase and not CheckBox))
                .Where(each => each.ActualHeight > SettingsPanel.RowControlHeight + 0.5).ToList();
            Assert(tall.Count == 0, "Controls taller than the row height: "
                + string.Join(", ", tall.Take(4).Select(each => $"{each.GetType().Name} {each.ActualHeight:0.#}")));
            Assert(UiText.Get(Languages.Korean, "Hint.NoteTravelTimeMs").EndsWith("니다.", StringComparison.Ordinal), "Hints are sentences");
            var effect = window.InputFor(nameof(VisualizerSettings.ShowInputEffect))!;
            effect.RaiseEvent(new RoutedEventArgs(UIElement.GotFocusEvent, effect));
            Equal(note.Text, UiText.Get(Languages.Korean, "Hint.ShowInputEffect"));
            effect.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, effect));
            Equal(note.Text, UiText.Get(Languages.Korean, "SettingsNote"));

            // R, G and B are written once above the color rows, not on every row.
            var captions = FindAll<TextBlock>(window).Select(block => block.Text).ToList();
            foreach (var channel in new[] { "R", "G", "B" }) Equal(captions.Count(text => text == channel), 1);
            // The "?" badges stay and explain in full.
            Assert(HelpTips(window).Any(tip => tip.Length > 80), "Help marks explain in detail");
            // The heading follows the language.
            Equal(((TextBlock)window.FindName("HeaderText")!).Text, UiText.Get(Languages.Korean, "setting"));
            Capture(window, Path.Combine(artifacts, "settings-hint.png"), 520, 900);
        });

        yield return ("Guide buttons hold the pressed look, the preview returns to what was there, and the status buttons share a width", () =>
        {
            // User request: the three guide buttons look pressed like the power button, the preview and the example
            // go back to the previous state when pressed again, and neither folds the song panel.
            var window = new MainWindow();
            var model = (MainViewModel)window.DataContext;
            var preview = (ToggleButton)window.FindName("PreviewButton")!;
            var example = (ToggleButton)window.FindName("ExampleButton")!;
            var help = (ToggleButton)window.FindName("HelpButton")!;
            var panel = (Expander)window.FindName("MapPanel")!;
            Assert(help is not null && preview.IsChecked == false && example.IsChecked == false, "All three are toggles, none pressed");

            void Press(ToggleButton button, bool on)
            {
                button.IsChecked = on;
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
            }

            Press(preview, true);
            Assert(model.LoadedSongName is not null && model.Playback.Timeline is not null, "The preview is loaded");
            Assert(panel.IsExpanded, "The song and checkpoint rows stay open");
            Press(preview, false);
            Assert(model.LoadedSongName is null && model.Playback.Timeline is null, "Pressing again returns to the start state");

            // With a song loaded first, releasing the preview brings that song back.
            var entry = new MapCacheEntry("song", "root", "main.adofai", "Loaded song", MapParseStatus.Supported,
                new ParsedTimeline(new[] { 0d, 500, 1000 }), [], "hash", "parser");
            model.LoadCached(entry);
            Press(preview, true);
            Equal(model.LoadedEntry, null);
            Press(preview, false);
            Equal(model.LoadedSongName, "Loaded song");
            Assert(panel.IsExpanded, "Releasing the preview leaves the panel open");

            // The two status buttons share one width and keep their labels while pressed.
            window.Measure(new Size(480, 800));
            window.Arrange(new Rect(0, 0, 480, 800));
            var message = (ToggleButton)window.FindName("MessageDetailsToggle")!;
            var status = (ToggleButton)window.FindName("StatusDetailsToggle")!;
            Assert(Math.Abs(message.ActualWidth - status.ActualWidth) < 0.5, $"Same width: {message.ActualWidth} vs {status.ActualWidth}");
            model.ShowFullMessage = true;
            model.ShowDetails = true;
            Equal(model.DetailsToggleLabel, UiText.Get(Languages.Korean, "DetailsShow"));
            Equal(model.StatusToggleLabel, UiText.Get(Languages.Korean, "DetailsStatus"));
        });

        yield return ("Section markers: sky blue at defaults, orange when changed, red when invalid; open state is remembered outside the JSON", () =>
        {
            var store = new MemorySettingsLayoutStore();
            var window = new SettingsPanel(new VisualizerSettings(), store);
            string[] keys = ["SectionGeneral", "SectionSongs", "SectionControls", "SectionDisplay", "SectionColors", "SectionTiming",
                "SectionPattern", "SectionMagic", "SectionAdvanced"];
            Assert(keys.All(key => window.SectionState(key) == "default"), "All sections start at defaults");
            var markers = FindAll<Expander>(window).Select(e => FindAll<Ellipse>((DependencyObject)e.Header).Single()).ToList();
            Assert(markers.All(m => ReferenceEquals(m.Fill, window.FindResource("AccentBrush"))), "Default sections are sky blue");

            var travel = FindAll<TextBox>(window).Single(t => t.Text == "600");
            travel.Text = "650";
            Equal(window.SectionState("SectionDisplay"), "changed"); Equal(window.SectionState("SectionTiming"), "default");
            Assert(ReferenceEquals(markers[SectionIndex(window, "SectionDisplay")].Fill, window.FindResource("FireBrush")), "Changed sections are orange");
            travel.Text = "fast";
            Equal(window.SectionState("SectionDisplay"), "error");
            Assert(ReferenceEquals(markers[SectionIndex(window, "SectionDisplay")].Fill, window.FindResource("ErrorBrush")), "Invalid sections are red");
            travel.Text = "";
            Equal(window.SectionState("SectionDisplay"), "default");

            var process = FindAll<TextBox>(window).Single(t => t.Text == "A Dance of Fire and Ice");
            process.Text = "a/b";
            Equal(window.SectionState("SectionAdvanced"), "error");
            process.Text = "ADOFAI";
            Equal(window.SectionState("SectionAdvanced"), "changed");
            process.Text = "A Dance of Fire and Ice";
            Equal(window.SectionState("SectionAdvanced"), "default");
            var noteColor = FindAll<TextBox>(window).Single(t => t.Text == "94, 218, 242");
            noteColor.Text = "#GG0000";
            Equal(window.SectionState("SectionColors"), "error");
            noteColor.Text = "#5edaf2";
            Equal(window.SectionState("SectionColors"), "default");
            FindAll<ComboBox>(window).Where(c => c.ItemsSource is int[]).ElementAt(1).SelectedItem = 8; // Minimum above the maximum.
            Equal(window.SectionState("SectionPattern"), "error");

            FindAll<Expander>(window).ElementAt(2).IsExpanded = false;
            var again = new SettingsPanel(new VisualizerSettings(), store);
            var reopened = FindAll<Expander>(again).ToList();
            // The last card is the advanced one, which starts closed; counting from the end keeps this off the section order.
            Assert(!reopened[2].IsExpanded && reopened[0].IsExpanded && !reopened[^2].IsExpanded, "The open state is remembered");
            reopened[^2].IsExpanded = true; // Advanced, followed by the experimental card.
            Equal(store.IsExpanded("SectionAdvanced"), (bool?)true);
            Assert(!SettingsJson.Export(new VisualizerSettings()).Contains("Expanded"), "The open state is not part of the JSON");
        });

        yield return ("Main screen: guide bar with X, aligned song and checkpoint rows, activation hint, button order and sync-first notice", () =>
        {
            var window = new MainWindow();
            var model = (MainViewModel)window.DataContext;
            Capture(window, Path.Combine(artifacts, "main-layout.png"), 480, 800);
            var content = (UIElement)window.Content;
            Point At(string name) => ((UIElement)window.FindName(name)).TranslatePoint(new Point(0, 0), content);
            double Width(string name) => ((FrameworkElement)window.FindName(name)).ActualWidth;
            double Right(string name) => At(name).X + Width(name);
            Assert(window.FindName("MapPanel") is Expander { IsExpanded: true }, "The song and checkpoint rows fold again, open by default");
            Assert(At("HelpButton").X < At("PreviewButton").X && At("PreviewButton").X < At("ExampleButton").X, "Guide, rule preview, example");
            Assert(window.FindName("HideGuideButton") is null && window.FindName("GuideBar") is null, "No guide bar box or X");
            Assert(At("GuideButtons").Y < At("SongPicker").Y && At("SongPicker").Y < At("CheckpointPicker").Y, "Guide buttons, song row, checkpoint row");
            Assert(At("ActivationHint").X - (At("SongTitle").X + Width("SongTitle")) >= 16, "Room between the title and the state");
            // Two rows now (user rule): the song with Load, the checkpoint with its auto toggle. The map sync moved into
            // the settings and the practice tile onto the note field.
            Assert(window.FindName("SyncButton") is null, "No sync button on the main window");
            Assert(At("SongPicker").X < At("LoadButton").X, "Song title, then load");
            Assert(Math.Abs(Width("LoadButton") - Width("AutoCheckpointToggle")) < 0.5 && Math.Abs(At("LoadButton").X - At("AutoCheckpointToggle").X) < 0.5,
                "Load and auto share a column");
            Assert(Math.Abs(At("PreviousSongButton").X - At("PreviousCheckpointButton").X) < 0.5 && Math.Abs(Right("NextSongButton") - Right("NextCheckpointButton")) < 0.5
                && Math.Abs(Width("SongPicker") - Width("CheckpointPicker")) < 0.5, "Song and checkpoint arrows and lists share columns");
            Assert(Width("LoadButton") < 148 && Width("SongPicker") > 200, "The load button leaves room for the lists");
            Assert(At("StartButton").X < At("PauseButton").X && At("PauseButton").X < At("ResetButton").X, "Start, pause, reset");
            Assert(At("ActivationHint").X < At("StateBadge").X, "The hint sits beside the Off state");
            Assert(((ComboBox)window.FindName("SongPicker")).IsEditable, "The song box accepts typing");

            var hint = (TextBlock)window.FindName("ActivationHint");
            Equal(hint.Visibility, Visibility.Visible); Equal(hint.Text, "F8을(를) 눌러 활성화");
            model.StartFromButton();
            Assert(model.Message.Contains("F8") && model.Playback.State == PlaybackState.Off, "Start while off explains how to switch on");
            model.ApplySettings(model.Settings with { VisualizerToggleKey = "F9" });
            Assert(model.ActivationHint.Contains("F9"), "The hint follows the configured key");
            model.Toggle();
            Equal(hint.Visibility, Visibility.Collapsed);
            model.Toggle();
            Assert(!model.ShowActivationHint, "Gone for good after the first activation");

            model.ApplySettings(model.Settings with { ShowGuideButtons = false });
            Equal(((FrameworkElement)window.FindName("GuideButtons")).Visibility, Visibility.Collapsed);
            Capture(window, Path.Combine(artifacts, "main-guide-hidden.png"), 480, 800);
            model.ApplySettings(model.Settings with { ShowGuideButtons = true });
            Equal(((FrameworkElement)window.FindName("GuideButtons")).Visibility, Visibility.Visible);
            var panel = (Expander)window.FindName("MapPanel");
            panel.IsExpanded = false;
            Capture(window, Path.Combine(artifacts, "main-song-panel-folded.png"), 480, 800);
            Assert(((FrameworkElement)window.FindName("Field")).ActualHeight > 560, "Folding gives the note field more room");
            panel.IsExpanded = true;

            var notices = new List<string>();
            model.NotifySyncFirst = notices.Add;
            model.LoadSelectedSong();
            Equal(notices.Count, 1); Assert(notices[0].Contains("동기화"), "Load before any sync asks to sync first");
            window.Close();

            var guide = new HelpWindow(model.Settings with { ResetKey = "R", NextCheckpointKey = "PageUp" });
            var text = string.Join("\n", FindAll<TextBlock>(guide).Select(t => t.Text));
            Assert(text.Contains("재설정(R)") && text.Contains("PageUp(다음)") && text.Contains("일시정지 (Esc)") && text.Contains("고급 설정"), "The guide uses current keys");
            Capture(guide, Path.Combine(artifacts, "help-window.png"), 520, 720);
            guide.Close();
            var english = new HelpWindow(model.Settings with { Language = Languages.English });
            Assert(FindAll<TextBlock>(english).Any(t => t.Text == "Pause (Esc)"), "English guide");
            english.Close();
            var confirm = new ConfirmDialog(model.Settings, UiText.Get(Languages.Korean, "ResetTitle"), UiText.Get(Languages.Korean, "ResetMessage"), "확인", "취소");
            Assert(FindAll<Button>(confirm).Select(b => b.Content).SequenceEqual(new object[] { "확인", "취소" }), "Confirm first, then cancel");
            Capture(confirm, Path.Combine(artifacts, "reset-confirm.png"), 440, 220);
            confirm.Close();
            var notice = new ConfirmDialog(model.Settings, UiText.Get(Languages.Korean, "SyncFirstTitle"), UiText.Get(Languages.Korean, "SyncFirstMessage"), "확인", null);
            Equal(FindAll<Button>(notice).Count(), 1);
            notice.Close();
        });

        yield return ("Visualization: effect switches hide gradients, key highlight and glow; colors reach the note field and preview before Apply", () =>
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
            // Tile 5 is the third of four lanes. The judgment line is at y 334, the bottom line at y 370, key caps below it.
            static int Index(int x, int y) => (y * width + x) * 4;
            static int Brightness(byte[] pixels, int x, int y) => pixels[Index(x, y)] + pixels[Index(x, y) + 1] + pixels[Index(x, y) + 2];

            var clock = new Clock();
            var session = new PlaybackSession(clock);
            // The pixel positions below assume a 36px judgment line gap (the default is 72).
            session.LoadTimeline(new[] { 0d, 5000 }, new VisualizerSettings { DynamicTiming = false, SimultaneousWindowMs = 0, JudgmentLineHeightDip = 36 });
            session.Toggle();
            var field = new NoteField { Session = session, Judge = new NotePressJudge() };
            field.Tick();
            var plain = Render(field);
            field.PressLane(5, false);
            var lit = Render(field);
            Assert(Brightness(lit, 125, 360) > Brightness(plain, 125, 360), "The input gradient lights the lane");
            Assert(Brightness(lit, 125, 385) > Brightness(plain, 125, 385), "The key highlight shows while held");
            session.ApplySettings(session.Settings with { ShowInputEffect = false });
            field.Tick();
            var off = Render(field);
            Equal(Brightness(off, 125, 360), Brightness(plain, 125, 360));
            Equal(Brightness(off, 125, 385), Brightness(plain, 125, 385)); // No key highlight either.

            var missField = new NoteField { Session = session, Judge = new NotePressJudge() };
            session.Start(); clock.NowMs = 200;
            missField.Tick();
            session.Pause();
            missField.Tick();
            var withMiss = Render(missField);
            session.ApplySettings(session.Settings with { ShowMissEffect = false });
            missField.Tick();
            var withoutMiss = Render(missField);
            Assert(withMiss[Index(125, 360) + 2] > withoutMiss[Index(125, 360) + 2], "The missed-tile gradient is red and can be turned off");

            // The play's first note wears the start color, so the second one is played up to the line to see the note color.
            var coloredClock = new Clock();
            var colored = new PlaybackSession(coloredClock);
            colored.LoadTimeline(new[] { 0d, 1000, 5000 }, new VisualizerSettings
            {
                DynamicTiming = false, SimultaneousWindowMs = 0, JudgmentLineHeightDip = 36, NoteColor = "#FF0000", JudgmentLineColor = "#00FF00",
                LaneBackgroundColors = "#102030 #08FFFFFF #00FFFFFF #08FFFFFF #00FFFFFF #08FFFFFF #00FFFFFF #08FFFFFF",
                LaneEffectColor = "#FF0000", ShowMissEffect = false
            });
            colored.Toggle();
            colored.Start();
            coloredClock.NowMs = 1000;
            var colorField = new NoteField { Session = colored };
            colorField.PressLane(5, false);
            var pixels = Render(colorField);
            Assert(Enumerable.Range(0, 200).Any(x => pixels[Index(x, 334) + 2] > 200 && pixels[Index(x, 334) + 1] < 60 && pixels[Index(x, 334)] < 60),
                "Single notes use the note color");
            Assert(pixels[Index(25, 100) + 2] == 0x10 && pixels[Index(25, 100) + 1] == 0x20 && pixels[Index(25, 100)] == 0x30, "Lane 1 background color");
            Assert(pixels[Index(125, 360) + 2] > pixels[Index(125, 360)] + 20, "Lane 3 press gradient uses its color");
            Assert(pixels[Index(25, 334) + 1] > pixels[Index(25, 334) + 2], "The judgment line uses its color");

            var model = new MainViewModel();
            model.Playback.LoadTimeline(new[] { 0d, 5000 }, model.Settings);
            var notes = model.Playback.Notes;
            var dialog = new SettingsPanel(model.Settings, new MemorySettingsLayoutStore()) { Preview = model.PreviewVisuals };
            ((CheckBox)dialog.InputFor(nameof(VisualizerSettings.ShowInputEffect))!).IsChecked = false;
            Assert(!model.Playback.Settings.ShowInputEffect && model.Settings.ShowInputEffect, "Effects preview on the note field before Apply");
            var noteColor = FindAll<TextBox>(dialog).Single(t => t.Text == "94, 218, 242");
            noteColor.Text = "rgb(255, 0, 0)";
            Equal(model.Playback.Settings.NoteColor, "255, 0, 0");
            noteColor.Text = "nope";
            Equal(model.Playback.Settings.NoteColor, "94, 218, 242"); // An unreadable color keeps the applied one.
            FindAll<TextBox>(dialog).Single(t => t.Text == "600").Text = "900";
            Equal(model.Playback.Settings.NoteTravelTimeMs, 600d); // Only effects and colors preview.
            Assert(ReferenceEquals(notes, model.Playback.Notes), "Preview never re-prepares notes");
            model.EndVisualPreview();
            Assert(model.Playback.Settings.ShowInputEffect && model.Playback.Settings.NoteColor == "94, 218, 242", "Closing without Apply restores the applied look");

            var entries = new[] { "Plum - R", "Sheet Music Boss - RUSH E", "cardboard box - HeHeHe" }
                .Select((name, i) => new MapCacheEntry($"song{i}", @"C:\maps", $"{i}\\main.adofai", name, MapParseStatus.Supported,
                    new ParsedTimeline(new[] { 0d, 500 }), Array.Empty<string>(), "", "test")).ToArray();
            var library = new MainViewModel();
            library.RefreshLibraryAsync(new FixedCache(entries)).GetAwaiter().GetResult();
            Equal(library.LibraryView.Cast<object>().Count(), 3);
            library.SelectedMap = null;
            library.SongQuery = "rush";
            Assert(library.LibraryView.Cast<MapCacheEntry>().Select(e => e.Id).SequenceEqual(new[] { "song1" }), "Typing filters by title");
            library.LoadSelectedSong();
            Equal(library.SongName, "Sheet Music Boss - RUSH E");
            library.SelectedMap = entries[2];
            library.SongQuery = "cardboard box - HeHeHe";
            Equal(library.LibraryView.Cast<object>().Count(), 3);
            library.SongQuery = "";
            Equal(library.LibraryView.Cast<object>().Count(), 3);
        });
    }
}
