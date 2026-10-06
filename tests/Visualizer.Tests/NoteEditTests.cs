using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Visualizer.App.Rendering;
using Visualizer.App.Views;
using Visualizer.App.ViewModels;
using Visualizer.Application.Cache;
using Visualizer.Application.Library;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Core.Localization;
using Visualizer.Infrastructure.Storage;
using Visualizer.Core.Visualization;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

/// <summary>The note editor's data and moves: selection, lane moves, chords, undo and the saved layout.</summary>
internal static class NoteEditTests
{
    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }

    /// <summary>Four notes half a second apart, one per lane of a 4-lane screen.</summary>
    private static NoteEditSession Four() => new(
        [
            new EditNote(0, 0, 0, 1, null, 0),
            new EditNote(1, 500, 500, 2, null, 0),
            new EditNote(2, 1000, 1000, 3, null, 0),
            new EditNote(3, 1500, 1500, 4, null, 0)
        ], [1, 2, 3, 4]);

    private static int[] Lanes(NoteEditSession session) => session.Notes.Select(note => note.Tile).ToArray();

    /// <summary>The window's own pixels, to tell drawings apart without a desktop window (as Capture renders them).</summary>
    private static byte[] Shot(Window window, int width = 620, int height = 900)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(content);
        var pixels = new byte[width * 4 * height];
        image.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    public static IEnumerable<(string Name, Action Run)> Create(string artifactRoot) => new (string, Action)[]
    {
        ("A first note an edit moved off the initial tile is what starts the song, so it can be played", () =>
        {
            // User report: moving the first note from J to K in the editor made it show a miss however well it was
            // pressed. The start delay is 0, so the first note sits exactly where the song starts: the start key plays it
            // by pressing its own lane as it starts, and once the note is somewhere else nothing can reach it in time.
            // A start inside the song already listens to its first note's lane; this is the same case.
            var timeline = new ParsedTimeline(new[] { 0d, 500, 1000 }, null, false, new[] { 1, 2, 3 });
            var settings = new VisualizerSettings { DynamicTiming = false, MaximumTileCount = 4, InitialTile = 5, StartDelayMs = 0 };
            var session = new PlaybackSession(new Clock());
            session.LoadParsedTimeline(timeline, settings);
            session.Reset(settings);
            // Laid out by the rules the first note is on the initial tile, so the fixed start key still starts.
            Equal(session.Notes[0].Tile, 5);
            Assert(session.LaneStartTile is null && !session.StartsOnLane(5), "The start key starts a song that begins where it expects");

            // The same song with the first note moved to lane 6, as the editor saves it.
            session.Layout = new NoteEdit("hash",
                session.Notes.Select((note, i) => (i, note.Tile)).ToDictionary(pair => pair.i, pair => pair.i == 0 ? 6 : pair.Tile), []);
            session.Reset(settings);
            Equal(session.Notes[0].Tile, 6);
            Assert(session.LaneStartTile == 6, "Its own lane starts it now");
            Assert(session.StartsOnLane(6) && !session.StartsOnLane(5), "and only that lane");
        }),

        ("The rule notes drawn behind an edit sit on the map's own clock, not the playback one", () =>
        {
            // User report: the grey rule notes showed about one tile earlier than the notes they belong to. The playback
            // clock starts at the first hit, so taking the times from the prepared notes moved every one of them by that
            // first hit's own time; a saved layout is built from the map's times, and the two have to agree.
            var timeline = new ParsedTimeline(new[] { 700d, 1200, 1700, 2200 }, null, false, new[] { 1, 2, 3, 4 });
            var settings = new VisualizerSettings { DynamicTiming = false, MaximumTileCount = 4 };
            var rules = MainWindow.FromRules(timeline, settings, "hash");
            Sequence(rules.Notes.Select(note => (int)note.HitTimeMs), 700, 1200, 1700, 2200);
            Sequence(rules.Notes.Select(note => (int)note.ShowTimeMs), 700, 1200, 1700, 2200);
            // Which is exactly where a saved layout of the same lanes puts them.
            var edit = new NoteEdit("hash", rules.Notes.ToDictionary(note => note.Hit, note => note.Tile), []);
            Sequence(NoteEditSession.BuildNotes(timeline, edit).Select(note => (int)note.ShowTimeMs), 700, 1200, 1700, 2200);
        }),

        ("Typing a lane slides the whole selection, and a wall stops it", () =>
        {
            var session = Four();
            // One note: the typed lane is simply where it goes.
            session.Select([0]);
            Equal(session.ToTile(3), EditRefusal.None);
            Equal(session.Notes[0].Tile, 3);
            Equal(session.ToTile(1), EditRefusal.None);
            Equal(session.Notes[0].Tile, 1);

            // The user's example: lanes 1 and 2 held, 3 makes it 2-3 and 4 makes it 3-4.
            session = Four();
            session.Select([0, 1]);
            Equal(session.ToTile(3), EditRefusal.None);
            Sequence(Lanes(session), 2, 3, 3, 4);
            Equal(session.ToTile(4), EditRefusal.None);
            Sequence(Lanes(session), 3, 4, 3, 4);
            // A lane inside the span changes nothing.
            Equal(session.ToTile(4), EditRefusal.Blocked);
            Equal(session.ToTile(3), EditRefusal.Blocked);

            // Every lane held: nothing can move, quietly.
            session = Four();
            session.SelectAll();
            foreach (var tile in new[] { 1, 2, 3, 4 }) Equal(session.ToTile(tile), EditRefusal.Blocked);
            Sequence(Lanes(session), 1, 2, 3, 4);

            // a and d move one lane; a wall stops the whole selection.
            session = Four();
            session.Select([0, 1]);
            Equal(session.Nudge(1), EditRefusal.None);
            Sequence(Lanes(session), 2, 3, 3, 4);
            Equal(session.Nudge(-1), EditRefusal.None);
            Sequence(Lanes(session), 1, 2, 3, 4);
            Equal(session.Nudge(-1), EditRefusal.Blocked); // Lane 1 is already at the wall.
            Sequence(Lanes(session), 1, 2, 3, 4);

            static void Sequence(int[] actual, params int[] expected) =>
                Assert(actual.SequenceEqual(expected), "Lanes: " + string.Join(" ", actual));
        }),

        ("A lane another note is using, or a long note is holding, refuses the move", () =>
        {
            // Two notes at the same moment: moving one onto the other is refused.
            var session = new NoteEditSession(
            [
                new EditNote(0, 0, 0, 1, null, 0),
                new EditNote(1, 0, 0, 2, null, 0)
            ], [1, 2, 3, 4]);
            session.Select([0]);
            Equal(session.ToTile(2), EditRefusal.LaneTaken);
            Equal(session.Notes[0].Tile, 1);
            Equal(session.ToTile(3), EditRefusal.None); // A free lane is fine.

            // A long note keeps its lane busy for its whole length.
            session = new NoteEditSession(
            [
                new EditNote(0, 0, 0, 1, 1000, 0),
                new EditNote(1, 500, 500, 2, null, 0),
                new EditNote(2, 2000, 2000, 2, null, 0)
            ], [1, 2, 3, 4]);
            session.Select([1]);
            Equal(session.ToTile(1), EditRefusal.HoldInTheWay);
            session.Select([2]);
            Equal(session.ToTile(1), EditRefusal.None); // After the hold ends the lane is free.

            // Moving the long note itself onto a lane that is busy during the hold is refused too.
            session = new NoteEditSession(
            [
                new EditNote(0, 0, 0, 1, 1000, 0),
                new EditNote(1, 500, 500, 2, null, 0)
            ], [1, 2, 3, 4]);
            session.Select([0]);
            Equal(session.ToTile(2), EditRefusal.HoldInTheWay);
        }),

        ("Merging needs two notes within 40ms on different lanes; splitting puts them back", () =>
        {
            var session = new NoteEditSession(
            [
                new EditNote(0, 0, 0, 1, null, 0),
                new EditNote(1, 30, 30, 2, null, 0),
                new EditNote(2, 200, 200, 3, null, 0)
            ], [1, 2, 3, 4]);

            session.Select([0]);
            Equal(session.Merge(40), EditRefusal.BadMerge);        // One note is not a chord.
            session.Select([0, 2]);
            Equal(session.Merge(40), EditRefusal.BadMerge);        // 200ms apart.
            session.Select([0, 1]);
            Equal(session.Merge(40), EditRefusal.None);
            Assert(session.Notes[0].Chord > 0 && session.Notes[0].Chord == session.Notes[1].Chord, "Both are one chord");
            // The chord shows at its first hit, and the hit times stay where they were.
            Equal(session.Notes[1].ShowTimeMs, 0d);
            Equal(session.Notes[1].HitTimeMs, 30d);
            Equal(session.Notes[1].Tile, 2);

            // Picking one member picks the chord, so the pair moves as one.
            session.Select([1]);
            Equal(session.Selection.Count, 2);
            Equal(session.Nudge(1), EditRefusal.None);
            Assert(session.Notes[0].Tile == 2 && session.Notes[1].Tile == 3, "The chord moved together");

            // Splitting sends each hit back to its own time.
            Equal(session.Split(), EditRefusal.None);
            Assert(session.Notes.All(note => note.Chord == 0), "No chord is left");
            Equal(session.Notes.Single(note => note.Hit == 1).ShowTimeMs, 30d);
            session.Select([2]);
            Equal(session.Split(), EditRefusal.BadSplit);          // Not a chord.

            // The window never goes past 40ms, whatever the setting says.
            session = new NoteEditSession(
            [
                new EditNote(0, 0, 0, 1, null, 0),
                new EditNote(1, 60, 60, 2, null, 0)
            ], [1, 2, 3, 4]);
            session.SelectAll();
            Equal(session.Merge(1000), EditRefusal.BadMerge);
            Assert(new VisualizerSettings { SimultaneousWindowMs = 41 }.ValidateFields()
                .Any(error => error.Field == nameof(VisualizerSettings.SimultaneousWindowMs)), "The setting stops at 40");
            Assert(!new VisualizerSettings { SimultaneousWindowMs = 40 }.ValidateFields()
                .Any(error => error.Field == nameof(VisualizerSettings.SimultaneousWindowMs)), "40 itself is allowed");
        }),

        ("A chord is two notes in a row on lanes side by side; the play screen shows what the editor shows", () =>
        {
            // The benchmark (AnythingBecomeMoe - Yararara, hits 730-732): three hits 0.014ms apart. The rules make 1-2 a
            // chord and 3 a single; the saved edit had split 1-2 and merged 1-3 past 2, so the song played all three apart.
            int[] All8 = [1, 2, 3, 4, 5, 6, 7, 8];
            var session = new NoteEditSession(
            [
                new EditNote(0, 0, 0, 3, null, 0),
                new EditNote(1, 1000, 1000, 4, null, 1),
                new EditNote(2, 1000.014236, 1000, 5, null, 1),
                new EditNote(3, 1000.028473, 1000.028473, 3, null, 0),
                new EditNote(4, 2000, 2000, 4, null, 0)
            ], All8);
            int Index(int hit) => session.Notes.ToList().FindIndex(note => note.Hit == hit);
            session.Select([Index(1)]);
            Equal(session.Split(), EditRefusal.None);
            // 1-3 past a note between them, however close: refused (user rule).
            session.Select([Index(1), Index(3)]);
            Equal(session.Merge(40), EditRefusal.NoteBetween);
            Equal(session.MergeAll(40), EditRefusal.NoteBetween);
            // 2-3 in a row, but lanes 5 and 3 are not side by side: refused.
            session.Select([Index(2), Index(3)]);
            Equal(session.Merge(40), EditRefusal.LanesApart);
            // With 3 moved next to 2 (lane 6) the pair becomes a chord.
            session.Select([Index(3)]);
            Equal(session.ToTile(6), EditRefusal.None);
            session.Select([Index(2), Index(3)]);
            Equal(session.Merge(40), EditRefusal.None);
            var layout = session.ToLayout();
            Assert(layout.Chords.Count == 1 && layout.Chords[0].SequenceEqual([2, 3]), "The chord is 2-3");
            // A chord leaves the selection as a whole, so Ctrl+click never splits it for a move.
            session.Select([Index(2)]);
            session.Toggle(Index(3));
            Equal(session.Selection.Count, 0);

            // The play screen draws the same chord.
            var timeline = new ParsedTimeline([0, 1000, 1000.014236, 1000.028473, 2000]);
            var settings = new VisualizerSettings { MaximumTileCount = 4, MinimumTileCount = 1, SimultaneousWindowMs = 20, FourKeyPattern = "3 4 5 6" };
            var rules = PlaybackSession.GenerateSongNotes(timeline, settings, out var hits);
            var laid = NoteEditLayout.Apply(rules, hits, layout, settings, out var order);
            var chord = Enumerable.Range(0, laid.Count - 1).Where(i => laid[i].StartsChord).Select(i => (order[i], order[i + 1])).ToArray();
            Assert(chord.SequenceEqual([(2, 3)]), "Play shows 2-3: " + string.Join(" ", chord));

            // An edit saved with the old 1-3 chord plays as three single notes, and the editor opens it split, saying so.
            var skipping = layout with { Chords = [new[] { 1, 3 }] };
            var played = NoteEditLayout.Apply(rules, hits, skipping, settings, out _);
            Assert(!played.Any(note => note.StartsChord), "No chord on the play screen");
            var reopened = NoteEditSession.FromLayout(timeline, skipping, All8);
            Assert(reopened.Loosened == 1 && reopened.Changed && reopened.Notes.All(note => note.Chord == 0), "Opened split, one chord loosened");
        }),

        ("Undo and redo step through the edits, and the layout round trips", () =>
        {
            var session = Four();
            session.Select([0]);
            Assert(!session.Changed && !session.CanUndo, "A fresh session has nothing to undo");
            session.ToTile(4);
            session.Select([1]);
            session.Nudge(1);
            Assert(session.Changed && session.CanUndo, "Two edits are on the stack");
            Equal(session.Notes.Single(note => note.Hit == 0).Tile, 4);

            Assert(session.Undo(), "Undo steps back");
            Equal(session.Notes.Single(note => note.Hit == 1).Tile, 2);
            Assert(session.Undo(), "And back again");
            Equal(session.Notes.Single(note => note.Hit == 0).Tile, 1);
            Assert(!session.Undo() && !session.CanUndo && !session.Changed, "Back at the start");
            Assert(session.Redo() && session.Redo(), "Redo replays both");
            Equal(session.Notes.Single(note => note.Hit == 0).Tile, 4);
            Assert(!session.Redo(), "Nothing left to redo");

            // The layout holds a lane for every note and the chords as hit numbers.
            var close = new NoteEditSession(
            [
                new EditNote(0, 0, 0, 1, null, 0),
                new EditNote(1, 20, 20, 2, null, 0),
                new EditNote(2, 1000, 1000, 3, null, 0),
                new EditNote(3, 1500, 1500, 4, null, 0)
            ], [1, 2, 3, 4]);
            close.Select([0, 1]);
            Equal(close.Merge(40), EditRefusal.None);
            var layout = close.ToLayout();
            Equal(layout.Lanes.Count, 4);
            Equal(layout.Chords.Count, 1);
            Sequence(layout.Chords[0], 0, 1);
            Equal(layout.ChordOf(1)?.Count, 2);
            Assert(layout.ChordOf(3) is null, "A note alone has no chord");

            static void Sequence(IReadOnlyList<int> actual, params int[] expected) =>
                Assert(actual.SequenceEqual(expected), "Chord: " + string.Join(" ", actual));
        }),

        ("override puts whole lanes in at once and refuses anything the rules would not allow", () =>
        {
            // User request: the console hands the editor the JSON that note -json prints. Only the numbers and the lanes
            // are used; a time that is there must match, and nothing changes unless every line passes.
            static NoteEditSession Four8() => new(
                [
                    new EditNote(0, 0, 0, 1, null, 0),
                    new EditNote(1, 500, 500, 2, null, 0),
                    new EditNote(2, 1000, 1000, 3, null, 0),
                    new EditNote(3, 1015, 1015, 4, null, 0)
                ], [1, 2, 3, 4, 5, 6, 7, 8], "hash");

            var session = Four8();
            Equal(session.Override([new(1, 5, null, 0), new(2, 7, null, 500)], 40), null);
            Sequence(session.Notes.Select(note => note.Tile).ToArray(), [5, 7, 3, 4]);
            Assert(session.CanUndo && session.Changed, "One undo step takes the whole thing back");
            session.Undo();
            Sequence(session.Notes.Select(note => note.Tile).ToArray(), [1, 2, 3, 4]);

            // A time that does not match the note is how a stale or made-up print is caught.
            session = Four8();
            Assert(session.Override([new(2, 5, null, 499)], 40)?.Contains("not 499", StringComparison.Ordinal) == true, "A wrong time stops it");
            Sequence(session.Notes.Select(note => note.Tile).ToArray(), [1, 2, 3, 4]);
            Assert(!session.Changed, "And nothing moved");

            // The chord rules are the merge rules: two notes, next to each other, inside the window.
            session = Four8();
            Equal(session.Override([new(3, 3, 1, null), new(4, 4, 1, null)], 40), null);
            Assert(session.Notes[2].Chord != 0 && session.Notes[2].Chord == session.Notes[3].Chord, "The pair is one chord");
            session = Four8();
            Assert(session.Override([new(3, 3, 1, null), new(4, 6, 1, null)], 40)?.Contains("lanes next to each other", StringComparison.Ordinal) == true,
                "Lanes apart cannot be a chord");
            session = Four8();
            Assert(session.Override([new(1, 1, 1, null), new(3, 2, 1, null)], 40)?.Contains("too far apart", StringComparison.Ordinal) == true,
                "Too far apart in time cannot be a chord");

            // Two notes on one lane inside a hand's reach of each other cannot be played.
            session = Four8();
            Assert(session.Override([new(3, 6, null, null), new(4, 6, null, null)], 40)?.Contains("less than", StringComparison.Ordinal) == true,
                "Stacked notes are refused");
            // Naming one note of a chord takes the other out of it too: half a chord is not something the rest of the
            // app can read (it would save a one-note chord group).
            session = Four8();
            Equal(session.Override([new(3, 3, 1, null), new(4, 4, 1, null)], 40), null);
            Equal(session.Override([new(3, 6, null, null)], 40), null);
            Assert(session.Notes.All(note => note.Chord == 0), "The pair is gone, not half of it");
            Assert(session.ToLayout().Chords.Count == 0, "And nothing one-sided is saved");

            // A lane that is not on the screen, and a note that is not in the song.
            session = new NoteEditSession([new EditNote(0, 0, 0, 1, null, 0)], [1, 2, 3, 4], "hash");
            Assert(session.Override([new(1, 7, null, null)], 40)?.Contains("not on the screen", StringComparison.Ordinal) == true, "Lane 7 of four");
            Assert(session.Override([new(9, 1, null, null)], 40)?.Contains("not in this song", StringComparison.Ordinal) == true, "No note 9");
        }),

        ("The editor shows the rules' notes behind the edited ones and never lets them be picked", () =>
        {
            // User request: a layer under the edited notes showing where the current settings would put them, dimmed,
            // position only, not clickable. The options that switch it live in the panel that slides out of the right.
            var store = new MemorySettingsLayoutStore();
            var settings = new VisualizerSettings();
            var session = new NoteEditSession(
                [new EditNote(0, 0, 0, 1, null, 0), new EditNote(1, 500, 500, 2, null, 0)],
                [1, 2, 3, 4, 5, 6, 7, 8], "hash");
            // The rules put the second note on lane 7; this edit moved it to lane 2, so the sheet shows both places.
            var window = new NoteEditWindow(session, settings, "Plum - R", currentSlot: null, laneWidth: 70, layout: store)
            {
                RuleNotes = [new EditNote(0, 0, 0, 1, null, 0), new EditNote(1, 500, 500, 7, null, 0)]
            };
            var sheet = FindAll<NoteEditField>(window).Single();
            Assert(sheet.ShowRuleNotes, "The layer starts on");
            sheet.TopMs = 800;
            Capture(window, Path.Combine(artifactRoot, "note-edit-ghost.png"), 620, 900);

            var lane = sheet.ActualWidth / 8;
            var edited = new Point(lane * 1 + lane / 2, sheet.YOf(500));
            var ghost = new Point(lane * 6 + lane / 2, sheet.YOf(500));
            Equal(sheet.NoteAt(edited), 1);
            // The layer is a reference, not something to edit, so the mouse goes straight through it.
            Equal(sheet.NoteAt(ghost), -1);
            Assert(sheet.NotesIn(new Rect(ghost.X - 20, ghost.Y - 20, 40, 40)).Count == 0, "A rubber band cannot catch it either");

            // It is really drawn: the same sheet without it comes out different. (The same sheet twice is the same
            // pixels, so the comparison below is about the layer and not about rendering noise.)
            var shown = Shot(window);
            Assert(shown.SequenceEqual(Shot(window)), "The same sheet draws the same twice");
            sheet.ShowRuleNotes = false;
            Assert(!shown.SequenceEqual(Shot(window)), "The layer changes what is drawn");
            sheet.ShowRuleNotes = true;

            // The tools stand beside the sheet the whole time (user rule): no gear to open them, nothing sliding. The toggle
            // is UI state kept for this run only.
            Capture(window, Path.Combine(artifactRoot, "note-edit-options.png"), 900, 900);
            Assert(FindAll<System.Windows.Controls.Primitives.ToggleButton>(window).All(toggle => toggle is CheckBox), "No gear button");
            window.GhostNotes = false;
            Assert(!sheet.ShowRuleNotes, "The toggle turns the layer off");
            var again = new NoteEditWindow(session, settings, "Plum - R", currentSlot: null, laneWidth: 70, layout: store);
            Assert(!again.GhostNotes, "The next editor opens the way the last one was left");
            var fresh = new NoteEditWindow(session, settings, "Plum - R", currentSlot: null, laneWidth: 70, layout: new MemorySettingsLayoutStore());
            Assert(fresh.GhostNotes, "A first run has the layer on");

            // Every key the editor takes has a button too (user rule), and a button does what its key does.
            var tools = fresh.Tools;
            foreach (var name in new[] { "EditSelectAll", "EditSelectNone", "EditNotePrevious", "EditNoteNext", "EditLaneLeft", "EditLaneRight",
                "EditFlip", "EditMerge", "EditSplit", "EditAuto", "EditUndo", "EditRedo", "EditSaveOver", "EditConsole",
                "EditZoomIn", "EditZoomOut", "EditZoomReset" })
                Assert(tools.ContainsKey(name), "A button for " + name);
            Assert(Enumerable.Range(1, 8).All(lane => tools.ContainsKey("EditToLane" + lane)), "A button for each lane");
            Assert(tools.Values.All(button => !button.Focusable), "No button takes the keys away from the sheet");
            Assert(!tools["EditSaveOver"].IsEnabled, "An original has no slot to save over");
            void Click(string name) => tools[name].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Click("EditSelectAll");
            Equal(fresh.Session.Selection.Count, 2);
            Click("EditSelectNone");
            Equal(fresh.Session.Selection.Count, 0);
            fresh.Session.Select([1]);
            Click("EditToLane5");
            Equal(fresh.Session.Notes[1].Tile, 5);
            Click("EditUndo");
            Equal(fresh.Session.Notes[1].Tile, 2);

            // The zoom reads in whole percent of where the sheet opened, and resets to it (user rule).
            Equal(fresh.ZoomText, "100%");
            Click("EditZoomIn");
            Equal(fresh.ZoomText, "115%");
            fresh.Wheel(120, ModifierKeys.Control, new Point(10, 300));
            Equal(fresh.ZoomText, "132%");
            Click("EditZoomReset");
            Equal(fresh.ZoomText, "100%");
        }),

        ("The edit window draws the sheet, picks notes and saves into a slot", () =>
        {
            var timeline = new ParsedTimeline(Enumerable.Range(0, 40).Select(i => i * 250d).ToArray());
            var settings = new VisualizerSettings { MaximumTileCount = 4, MinimumTileCount = 1, DynamicTiming = false };
            var notes = PlaybackSession.GenerateSongNotes(timeline, settings, out var hits);
            var edits = notes.Select((note, i) => new EditNote(hits[i], timeline.HitTimesMs[hits[i]], note.HitTimeMs,
                note.Tile, note.HoldEndMs, 0)).ToArray();
            // All eight lanes, each as wide as a lane of the main screen.
            var window = new NoteEditWindow(new NoteEditSession(edits, [1, 2, 3, 4, 5, 6, 7, 8], "hash"), settings, "Plum - R",
                currentSlot: 2, laneWidth: 70)
            {
                Confirm = (_, _, _) => true
            };
            Capture(window, Path.Combine(artifactRoot, "note-edit.png"), 620, 900);
            Equal(window.Session.Tiles, 8);
            var sheet = FindAll<NoteEditField>(window).Single();
            Equal(sheet.Width, 560d);

            // The wheel follows the sheet: time runs upward, so turning the wheel up shows later notes.
            var before = sheet.TopMs;
            sheet.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.MouseWheelEvent });
            Equal(sheet.TopMs, before); // An editor without the focus ignores the wheel.
            var middle = new Point(100, 300);
            window.Wheel(120, ModifierKeys.None, middle);
            var step = sheet.TopMs - before;
            Assert(step > 0, $"Wheel up moves later: {before} -> {sheet.TopMs}");
            window.Wheel(-240, ModifierKeys.None, middle);
            Assert(sheet.TopMs < before, "Wheel down moves earlier");
            // Shift is ten times faster and Ctrl zooms; there is no slow scroll, and Alt is a plain wheel.
            // Downward, so the ten-fold step stays clear of the end of the song where scrolling stops.
            var from = sheet.TopMs;
            window.Wheel(-120, ModifierKeys.Shift, middle);
            Assert(Math.Abs(from - sheet.TopMs - step * 10) < 0.001, $"Shift: {from - sheet.TopMs} for {step}");
            from = sheet.TopMs;
            window.Wheel(120, ModifierKeys.Alt, middle);
            Assert(Math.Abs(sheet.TopMs - from - step) < 0.001, $"Alt: {sheet.TopMs - from} for {step}");
            var zoom = sheet.Zoom;
            window.Wheel(120, ModifierKeys.Control, middle);
            Assert(sheet.Zoom > zoom, "Ctrl zooms in");
            // It zooms in much further than before (1.5 px/ms).
            sheet.Zoom = 100;
            Equal(sheet.Zoom, NoteEditField.MaximumZoom);
            Assert(NoteEditField.MaximumZoom >= 6, "Deep zoom is allowed");
            sheet.Zoom = 0.2;

            // Lanes alternate as on the main screen, counted from its first lane (tile 3 for four keys), with no dimmed lanes.
            Equal(NoteEditField.LaneColorIndex(3, 3, 8), 0);
            Equal(NoteEditField.LaneColorIndex(6, 3, 8), 3);
            Equal(NoteEditField.LaneColorIndex(1, 3, 8), 6);
            Equal(NoteEditField.LaneColorIndex(2, 2, 8) % 2, NoteEditField.LaneColorIndex(4, 2, 8) % 2);
            // Picked notes are drawn with a ring, not in the judgment line color.
            window.Session.Select([1, 2, 3]);
            sheet.Reveal(1);
            Capture(window, Path.Combine(artifactRoot, "note-edit-picked.png"), 620, 900);
            window.Session.Select([]);

            // Save always asks for the slot first; backing out of the picker saves nothing.
            Equal(window.CurrentSlot, 2);
            window.Session.Select([0, 1]);
            Equal(window.Session.Nudge(1), EditRefusal.None);
            Assert(window.Session.Changed, "The move is an edit");
            var asked = 0;
            window.PickSlot = () => { asked++; return null; };
            window.Save();
            Assert(asked == 1 && window.Result is null, "Backing out of the picker saves nothing");
            window.PickSlot = () => { asked++; return 5; };
            window.Save();
            Equal(asked, 2);
            Equal(window.Result?.Slot, 5);
            Equal(window.Result?.Layout.Lanes.Count, edits.Length);
        }),

        ("The slot picker marks the slot being edited and warns before replacing one", () =>
        {
            var settings = new VisualizerSettings { Language = "korean" };
            var made = new DateTimeOffset(2026, 9, 20, 5, 30, 0, TimeSpan.Zero);
            var used = new[]
            {
                new EditSlotInfo(2, made, made.AddHours(1), 120, 4, 4, "hash"),
                new EditSlotInfo(5, made, made, 120, 0, 6, "older song")
            };
            var dialog = new EditSlotDialog(settings, "2*Plum - R", 2, used, "hash");
            Equal(dialog.Selected, 2);
            Assert(FindAll<TextBlock>(dialog).Any(text => text.Text == Languages.Format("korean", "EditSlotCurrent", 2)), "Names the slot being edited");
            Assert(dialog.Overwrites && dialog.Warning.Contains('2', StringComparison.Ordinal), "Replacing a used slot is said: " + dialog.Warning);
            Equal(dialog.SaveLabel, Languages.Get("korean", "EditSlotReplace"));
            Assert(!dialog.DetailsOpen, "Details start folded");
            var when = made.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            Assert(dialog.DetailLines.Any(line => line.StartsWith(Languages.Get("korean", "EditSlotCreated"), StringComparison.Ordinal) && line.EndsWith(when, StringComparison.Ordinal)),
                "Details say when the edit was made: " + string.Join(" | ", dialog.DetailLines));
            dialog.DetailsOpen = true;
            Capture(dialog, Path.Combine(artifactRoot, "edit-slot.png"), 420, 600);
            dialog.Select(5);
            Assert(dialog.DetailLines.Any(line => line.EndsWith(Languages.Get("korean", "EditSlotSourceChanged"), StringComparison.Ordinal)),
                "An edit made from another version of the song says so");
            dialog.Select(3);
            Assert(!dialog.Overwrites && dialog.Warning.Length == 0 && dialog.DetailLines.Count == 0, "An empty slot warns about nothing");
            Equal(dialog.SaveLabel, Languages.Get("korean", "Save"));
            dialog.Select(10);
            Equal(dialog.Selected, 3);

            // An original song starts on its first empty slot; with all nine used, nothing is replaced unasked.
            Equal(new EditSlotDialog(settings, "Plum - R", null, used, "hash").Selected, 1);
            var full = Enumerable.Range(1, 9).Select(slot => new EditSlotInfo(slot, made, made, 1, 0, 4, "hash")).ToArray();
            var none = new EditSlotDialog(settings, "Plum - R", null, full, "hash");
            Assert(none.Selected is null && !FindAll<Button>(none).Single(button => button.IsDefault).IsEnabled, "Nothing picked, nothing to save");
        }),

        ("Deleting an edit takes its whole slot; removing keeps only the newest for restore", () =>
        {
            var home = Path.Combine(artifactRoot, "edit-hold-home");
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
            var maps = new MapLibrary(AppStorage.At(home));
            var store = new NoteEditStore(maps);
            var original = new MapCacheEntry("c", maps.CustomRoot, Path.Combine("1234", "main.adofai"), "Plum - R",
                MapParseStatus.Supported, null, [], "hash", "test");
            var edit = new NoteEdit("hash", new Dictionary<int, int> { [0] = 3 }, []);
            foreach (var slot in new[] { 2, 3, 5 }) store.Write(original, "{}", edit, new VisualizerSettings(), slot);
            MapCacheEntry Copy(int slot) => original with
            {
                Id = "e" + slot, RootDirectory = maps.EditedRoot, RelativePath = Path.Combine("1234", slot.ToString(CultureInfo.InvariantCulture), "main.adofai")
            };
            string Folder(int slot) => Path.Combine(maps.EditedRoot, "1234", slot.ToString(CultureInfo.InvariantCulture));

            // Deleting takes the edit file too, so the slot is free again (a leftover edit file would make it look used).
            maps.Remove(Copy(5));
            Assert(!Directory.Exists(Folder(5)) && store.UsedSlots("1234").SequenceEqual([2, 3]), "Slot 5 is free");

            // Removing keeps the song for restore, and only the newest one.
            Assert(maps.HeldName is null, "Nothing kept yet");
            maps.Hold(Copy(2), "2*Plum - R");
            Assert(maps.HeldName == "2*Plum - R" && !Directory.Exists(Folder(2)), "Kept, and out of the list");
            maps.Hold(Copy(3), "3*Plum - R");
            Assert(maps.HeldName == "3*Plum - R" && store.UsedSlots("1234").Count == 0, "Only the newest is kept");

            // Restore puts it back in its own slot, or the first free one when that slot was used since.
            store.Write(original, "{}", edit, new VisualizerSettings(), 3);
            Equal(maps.Restore(), 1);
            Assert(File.Exists(Path.Combine(Folder(1), "edit.json")) && File.Exists(Path.Combine(Folder(1), "main.adofai")), "Restored whole");
            Assert(maps.HeldName is null, "Nothing is kept after a restore");
            Throws<InvalidOperationException>(() => maps.Restore());
            Throws<InvalidOperationException>(() => maps.Hold(original, "Plum - R"));
            Directory.Delete(home, recursive: true);
        }),

        ("Edits file under their song, remember when they were made and describe every slot", () =>
        {
            var home = Path.Combine(artifactRoot, "edit-store-home");
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
            var maps = new MapLibrary(AppStorage.At(home));
            var store = new NoteEditStore(maps);
            var original = new MapCacheEntry("c", maps.CustomRoot, Path.Combine("1234", "main.adofai"), "Plum - R",
                MapParseStatus.Supported, null, [], "hash", "test");
            Equal(store.SongFolderOf(original), "1234");
            var edit = new NoteEdit("hash", new Dictionary<int, int> { [0] = 3, [1] = 4 }, [[0, 1]]);
            var first = new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero);
            store.Write(original, "{}", edit, new VisualizerSettings { MaximumTileCount = 6 }, 2, first);

            // Slot 2's copy is a song of its own; editing it again still files under 1234, never under a folder "2".
            var copy = original with { Id = "e", RootDirectory = maps.EditedRoot, RelativePath = Path.Combine("1234", "2", "main.adofai") };
            Equal(maps.SlotOf(copy), 2);
            Equal(store.SongFolderOf(copy), "1234");
            store.Write(copy, "{}", edit, new VisualizerSettings { MaximumTileCount = 8 }, 2, first.AddHours(3));
            store.Write(copy, "{}", edit, new VisualizerSettings { MaximumTileCount = 8 }, 7, first.AddHours(4));
            Assert(!Directory.Exists(Path.Combine(maps.EditedRoot, "2")), "Nothing is filed under the slot's folder");

            var slots = store.SlotsOf("1234");
            Assert(slots.Select(info => info.Slot).SequenceEqual([2, 7]), "Used slots: " + string.Join(" ", slots.Select(info => info.Slot)));
            Equal(slots[0].CreatedAt, first); // Saved back into its own slot, the edit keeps its birth.
            Equal(slots[0].SavedAt, first.AddHours(3));
            Equal(slots[1].CreatedAt, first.AddHours(4)); // Another slot is a new edit.
            Equal(slots[0].Notes, 2);
            Equal(slots[0].Chords, 1);
            Equal(slots[0].MaximumTileCount, 8);
            Equal(slots[0].SourceHash, "hash");

            // The slot is the folder the file sits in, so a song folder named like a slot is not mistaken for one.
            var odd = original with { RootDirectory = maps.EditedRoot, RelativePath = Path.Combine("3", "5", "main.adofai") };
            Equal(maps.SlotOf(odd), 5);
            Equal(store.SongFolderOf(odd), "3");
            Directory.Delete(home, recursive: true);
        }),

        ("Ctrl+F flips within the lanes the selection spans; Ctrl+R, Ctrl+M and Ctrl+U merge and split what they can", () =>
        {
            int[] All8 = [1, 2, 3, 4, 5, 6, 7, 8];
            // Five lanes selected (3..7): the middle lane 5 stays, 3 and 7 swap, 4 and 6 swap.
            var odd = new NoteEditSession(
            [
                new EditNote(0, 0, 0, 3, null, 0),
                new EditNote(1, 500, 500, 4, null, 0),
                new EditNote(2, 1000, 1000, 5, null, 0),
                new EditNote(3, 1500, 1500, 7, null, 0)
            ], All8);
            odd.SelectAll();
            Equal(odd.Flip(), EditRefusal.None);
            Program.Sequence(Lanes(odd), 7, 6, 5, 3);
            odd.Select([2]);
            Equal(odd.Flip(), EditRefusal.Blocked); // One lane has nothing to flip.
            // The user's case: the song uses lanes 3..6, and notes on 5 and 6 swap there instead of moving to 4 and 3.
            var inside = new NoteEditSession(
            [
                new EditNote(0, 0, 0, 3, null, 0),
                new EditNote(1, 500, 500, 4, null, 0),
                new EditNote(2, 1000, 1000, 5, null, 0),
                new EditNote(3, 1500, 1500, 6, null, 0),
                new EditNote(4, 2000, 2000, 5, null, 0)
            ], All8);
            inside.Select([2, 3, 4]);
            Equal(inside.Flip(), EditRefusal.None);
            Program.Sequence(Lanes(inside), 3, 4, 6, 5, 6);
            inside.SelectAll();
            Equal(inside.Flip(), EditRefusal.None);
            Program.Sequence(Lanes(inside), 6, 5, 3, 4, 3);
            // Four lanes (1..4): 1 and 4 swap, 2 and 3 swap; a note in the way stops it as a move does.
            var even = Four();
            even.SelectAll();
            Equal(even.Flip(), EditRefusal.None);
            Program.Sequence(Lanes(even), 4, 3, 2, 1);
            // Lanes 1 and 3 selected: the note flipping onto lane 3 meets the one already there at the same time.
            var crowded = new NoteEditSession([new EditNote(0, 0, 0, 1, null, 0), new EditNote(1, 0, 0, 3, null, 0), new EditNote(2, 500, 500, 3, null, 0)], All8);
            crowded.Select([0, 2]);
            Equal(crowded.Flip(), EditRefusal.LaneTaken);
            Program.Sequence(Lanes(crowded), 1, 3, 3);

            // Ctrl+M and Ctrl+R merge every pair they can, earliest first: 0-1 (20ms) and 3-4 (10ms); 2 is 200ms from 3.
            var pairs = new NoteEditSession(
            [
                new EditNote(0, 0, 0, 3, null, 0),
                new EditNote(1, 20, 20, 4, null, 0),
                new EditNote(2, 500, 500, 5, null, 0),
                new EditNote(3, 700, 700, 6, null, 0),
                new EditNote(4, 710, 710, 5, null, 0)
            ], All8);
            pairs.SelectAll();
            Equal(pairs.SwitchChords(40), EditRefusal.None);
            int ChordOf(NoteEditSession session, int hit) => session.Notes.Single(note => note.Hit == hit).Chord;
            Assert(ChordOf(pairs, 0) > 0 && ChordOf(pairs, 0) == ChordOf(pairs, 1) && ChordOf(pairs, 3) > 0 && ChordOf(pairs, 3) == ChordOf(pairs, 4)
                && ChordOf(pairs, 0) != ChordOf(pairs, 3) && ChordOf(pairs, 2) == 0, "Two chords, one single");
            Assert(pairs.Undo() && pairs.Notes.All(note => note.Chord == 0), "One undo takes both pairs back");
            pairs.Redo();
            // Chords and a single together: Ctrl+R does nothing; Ctrl+U splits only the chords.
            pairs.SelectAll();
            Equal(pairs.SwitchChords(40), EditRefusal.MixedChords);
            Equal(pairs.Split(), EditRefusal.None);
            Assert(pairs.Notes.All(note => note.Chord == 0 && note.ShowTimeMs == note.HitTimeMs), "Every chord split, times back");
            // Ctrl+M leaves chords alone and merges the singles; Ctrl+R on chords only splits them.
            pairs.Select([0, 1]);
            Equal(pairs.MergeAll(40), EditRefusal.None);
            pairs.SelectAll();
            Equal(pairs.MergeAll(40), EditRefusal.None);
            Assert(ChordOf(pairs, 0) == ChordOf(pairs, 1) && ChordOf(pairs, 3) == ChordOf(pairs, 4), "The first chord stayed, the second joined");
            pairs.Select([pairs.Notes.ToList().FindIndex(note => note.Hit == 0)]);
            Equal(pairs.SwitchChords(40), EditRefusal.None);
            Equal(ChordOf(pairs, 0), 0);
            // A single alone has nothing to pair with.
            pairs.Select([pairs.Notes.ToList().FindIndex(note => note.Hit == 2)]);
            Equal(pairs.SwitchChords(40), EditRefusal.BadMerge);
        }),

        ("Editor keys: selection count, Ctrl+S and Ctrl+Shift+S, Ctrl+X, the key manual and orange risky buttons", () =>
        {
            var settings = new VisualizerSettings { Language = "korean" };
            var asked = new List<string>();
            var picks = 0;
            var window = new NoteEditWindow(Four(), settings, "2*Plum - R", currentSlot: 2)
            {
                Confirm = (_, message, _) => { asked.Add(message); return true; },
                PickSlot = () => { picks++; return null; }
            };
            // Two or more picked notes are counted beside the buttons.
            Assert(window.Press(Key.A, ModifierKeys.Control), "Ctrl+A");
            Equal(window.PickedText, Languages.Format("korean", "EditSelected", 4));
            window.Press(Key.Escape, ModifierKeys.None);
            Equal(window.PickedText, "");
            window.Press(Key.A, ModifierKeys.Control);
            Assert(window.Press(Key.F, ModifierKeys.Control), "Ctrl+F");
            Program.Sequence(Lanes(window.Session), 4, 3, 2, 1);
            Assert(!window.Press(Key.M, ModifierKeys.None) && !window.Press(Key.U, ModifierKeys.None), "Plain m and u do nothing now");

            // Ctrl+S opens the slot picker; Ctrl+Shift+S asks about the open slot only and saves there.
            window.Press(Key.S, ModifierKeys.Control);
            Assert(picks == 1 && window.Result is null, "Ctrl+S picks a slot");
            window.Press(Key.S, ModifierKeys.Control | ModifierKeys.Shift);
            Assert(picks == 1 && asked[^1] == Languages.Format("korean", "EditSaveHere", 2) && window.Result?.Slot == 2, "Ctrl+Shift+S overwrites slot 2");
            // An original song has no slot yet, so Ctrl+Shift+S opens the picker.
            var original = new NoteEditWindow(Four(), settings, "Plum - R", currentSlot: null) { Confirm = (_, _, _) => true, PickSlot = () => 3 };
            original.Press(Key.S, ModifierKeys.Control | ModifierKeys.Shift);
            Equal(original.Result?.Slot, 3);

            // Ctrl+X closes, asking first while there are unsaved changes; saying no keeps the editor.
            var closed = false;
            var closing = new NoteEditWindow(Four(), settings, "Plum - R", currentSlot: null)
            {
                Confirm = (_, message, _) => { asked.Add(message); return false; },
                Left = -20000, Top = -20000, ShowActivated = false
            };
            closing.Closed += (_, _) => closed = true;
            closing.Show();
            closing.Press(Key.A, ModifierKeys.Control);
            closing.Press(Key.F, ModifierKeys.Control);
            Assert(closing.Press(Key.X, ModifierKeys.Control) && !closed && asked[^1] == Languages.Get("korean", "EditDiscard"), "Asked first");
            closing.Confirm = (_, _, _) => true;
            closing.Press(Key.X, ModifierKeys.Control);
            Assert(closed, "Yes closes");

            // The manual lists the keys; the one-line help under the sheet is gone.
            var keys = window.ToggleManual().Keys;
            foreach (var key in new[] { "Ctrl+F", "Ctrl+R", "Ctrl+M", "Ctrl+U", "Ctrl+S", "Ctrl+Shift+S", "Ctrl+X", "Ctrl+휠", "Shift+휠" })
                Assert(keys.Contains(key), key + " is in the manual: " + string.Join(", ", keys));
            Assert(!FindAll<TextBlock>(window).Any(text => text.Text.Contains("Ctrl+휠 확대", StringComparison.Ordinal)), "No help line under the sheet");
            Assert(!keys.Contains("Alt+휠"), "Nothing uses Alt any more");
            Capture(window.ToggleManual(), Path.Combine(artifactRoot, "edit-keys.png"), 520, 820);

            // A question whose yes throws something away wears the orange outline, never the blue "go ahead".
            Equal(new ConfirmDialog(settings, "t", "m", "Close", "Cancel", risky: true).AcceptStyle, "DangerButton");
            Equal(new ConfirmDialog(settings, "t", "m", "OK", "Cancel").AcceptStyle, "PrimaryButton");
            var made = DateTimeOffset.UtcNow;
            var picker = new EditSlotDialog(settings, "Plum - R", 2, [new EditSlotInfo(2, made, made, 4, 0, 4, "hash")], "hash");
            var accept = FindAll<Button>(picker).Single(button => button.IsDefault);
            Assert(ReferenceEquals(accept.Style, picker.FindResource("DangerButton")), "Overwrite is orange");
            picker.Select(3);
            Assert(ReferenceEquals(accept.Style, picker.FindResource("PrimaryButton")), "An empty slot saves in blue");
        }),

        ("An edit that needs lanes the screen does not show offers the lane change that fits it", () =>
        {
            var timeline = new ParsedTimeline([0, 500, 1000, 1500, 2000]);
            MapCacheEntry Entry() => new("e", @"C:\store\maps\edited", @"1234\2\main.adofai", "Plum - R", MapParseStatus.Supported, timeline, [], "hash", "test");
            var four = new VisualizerSettings { MaximumTileCount = 4, MinimumTileCount = 1, FourKeyPattern = "3 4 5 6", SixKeyPattern = "2 3 4 5 6 7", ExtraLanes = false };
            // Five lanes, tiles 3..7: a 4-key screen shows 3..6, and a 6-key one shows every one of them. Spare lanes hold
            // chord notes only, so an edit is never offered them (user rule): it is told what it was made for instead.
            var fiveLanes = new NoteEdit("hash", new Dictionary<int, int> { [0] = 3, [1] = 4, [2] = 5, [3] = 6, [4] = 7 }, []);
            var asked = new List<string>();
            var declined = new MainViewModel
            {
                ConfirmExtraLanes = _ => throw new InvalidOperationException("An edit is never offered spare lanes"),
                ConfirmWiderLanes = message => { asked.Add(message); return false; },
                SongLayout = _ => fiveLanes
            };
            declined.ApplySettings(four);
            declined.SelectedMap = Entry();
            declined.LoadSelectedSong();
            Assert(asked.Count == 1 && asked[0].Contains("(7)", StringComparison.Ordinal) && asked[0].Contains("6", StringComparison.Ordinal),
                "Asked for six lanes, naming the lane: " + string.Join(" | ", asked));
            Assert(declined.Playback.Notes.Count == 0 && declined.Settings == four, "Declined: nothing changes and the song waits");

            var sixKeys = new MainViewModel
            {
                ConfirmExtraLanes = _ => throw new InvalidOperationException("An edit is never offered spare lanes"),
                ConfirmWiderLanes = _ => true,
                SongLayout = _ => fiveLanes
            };
            sixKeys.ApplySettings(four);
            sixKeys.SelectedMap = Entry();
            sixKeys.LoadSelectedSong();
            Assert(sixKeys.Settings.MaximumTileCount == 6 && !sixKeys.Settings.ExtraLanes && sixKeys.Playback.Notes.Count == 5, "Six lanes placed it");

            // Tile 1 is on no 4- or 6-key screen: eight keys are offered, and the change can be rolled back.
            var fromOne = new NoteEdit("hash", new Dictionary<int, int> { [0] = 1, [1] = 2, [2] = 3, [3] = 4, [4] = 5 }, []);
            var wider = new MainViewModel { ConfirmExtraLanes = _ => throw new InvalidOperationException("An edit is never offered spare lanes"), ConfirmWiderLanes = _ => true, SongLayout = _ => fromOne };
            wider.ApplySettings(four);
            wider.SelectedMap = Entry();
            wider.LoadSelectedSong();
            Assert(wider.Settings.MaximumTileCount == 8 && wider.Playback.Notes.Count == 5, "Eight keys placed it");
            Equal(wider.SettingsHistory[^1].After.MaximumTileCount, 8);
        }),

        ("A saved edit decides the lanes and chords when the song is played", () =>
        {
            // Six hits, half a second apart; the rules would lay them out on their own.
            var timeline = new ParsedTimeline([0, 500, 1000, 1500, 2000, 2020]);
            // Eight keys show every tile the edit uses.
            var settings = new VisualizerSettings { MaximumTileCount = 8, MinimumTileCount = 1, DynamicTiming = false };
            var plain = PlaybackSession.GenerateSongNotes(timeline, settings, out var hits);
            Equal(plain.Count, 6);

            // The edit sends every note to a lane of its own choosing and makes the last two a chord.
            var edit = new NoteEdit("hash",
                new Dictionary<int, int> { [0] = 4, [1] = 3, [2] = 2, [3] = 1, [4] = 2, [5] = 3 }, [[4, 5]]);
            var laid = NoteEditLayout.Apply(plain, hits, edit, settings, out var ordered);
            Sequence(laid.Select(note => note.Tile).ToArray(), 4, 3, 2, 1, 2, 3);
            Sequence(ordered.ToArray(), 0, 1, 2, 3, 4, 5);
            Assert(laid[4].StartsChord && !laid[5].StartsChord, "The first chord member carries the mark");
            // A chord shows at its first hit; the source times never move.
            Equal(laid[5].HitTimeMs, laid[4].HitTimeMs);
            Equal(timeline.HitTimesMs[5], 2020d);

            // A tile the screen does not show is refused with the note and the lane named, so loading the
            // edit offers the lane change that fits it; the play field no longer adds lanes on its own.
            Sequence(NoteEditLayout.TilesOf(edit).ToArray(), 1, 2, 3, 4);
            var four = settings with { MaximumTileCount = 4, MinimumTileCount = 1 };
            var reason = "";
            try { NoteEditLayout.Apply(plain, hits, edit, four, out _); }
            catch (NotePlacementException error) { reason = error.Message; }
            Assert(reason.Contains("(2)", StringComparison.Ordinal) || reason.Contains("(1)", StringComparison.Ordinal), "The message names the lane: " + reason);
            var broken = edit with { Lanes = new Dictionary<int, int>(edit.Lanes) { [0] = 9 } };
            Throws<NotePlacementException>(() => NoteEditLayout.Apply(plain, hits, broken, settings, out _));

            // The 4-key pattern already uses tiles 3..6, so keeping a note on tile 6 must load (it was once refused).
            var fourKey = new VisualizerSettings { MaximumTileCount = 4, MinimumTileCount = 4, DynamicTiming = false };
            var ruled = PlaybackSession.GenerateSongNotes(timeline, fourKey, out var ruledHits);
            Assert(ruled.Any(note => note.Tile > 4), "The 4-key pattern reaches past tile 4");
            var keep = new NoteEdit("hash", ruled.Select((note, i) => (ruledHits[i], note.Tile)).ToDictionary(pair => pair.Item1, pair => pair.Item2), []);
            Sequence(NoteEditLayout.Apply(ruled, ruledHits, keep, fourKey, out _).Select(note => note.Tile).ToArray(),
                ruled.Select(note => note.Tile).ToArray());

            // Playback takes the same path: the session lays the edit over what the rules made.
            var session = new PlaybackSession(new Clock()) { Layout = edit };
            session.Toggle();
            session.LoadParsedTimeline(timeline, settings);
            Sequence(session.Notes.Select(note => note.Tile).ToArray(), 4, 3, 2, 1, 2, 3);
            session.Layout = null;
            session.Reset(settings);
            Assert(!session.Notes.Select(note => note.Tile).SequenceEqual([4, 3, 2, 1, 2, 3]), "Without the edit the rules decide");

            static void Sequence(int[] actual, params int[] expected) =>
                Assert(actual.SequenceEqual(expected), "Got: " + string.Join(" ", actual));
        }),

        ("A saved layout rebuilds the same notes from the timeline", () =>
        {
            // Hit 2 is a long note, so hit 3 is its release and never becomes a note of its own.
            var timeline = new ParsedTimeline([0, 100, 200, 800, 1000], Array.Empty<TimelineCheckpoint>(), false, null,
                [HitKind.Tap, HitKind.Tap, HitKind.HoldStart, HitKind.HoldEnd, HitKind.Tap]);
            var edit = new NoteEdit("hash", new Dictionary<int, int> { [0] = 1, [1] = 2, [2] = 3, [4] = 4 }, [[0, 1]]);
            var notes = NoteEditSession.BuildNotes(timeline, edit);

            Equal(notes.Count, 4);                                  // The release is not a note.
            Assert(notes.All(note => note.Hit != 3), "The hold's end is not a note");
            Equal(notes.Single(note => note.Hit == 2).HoldEndMs, (double?)800);
            // The chord shows at its first hit; both keep their own hit times.
            Equal(notes.Single(note => note.Hit == 1).ShowTimeMs, 0d);
            Equal(notes.Single(note => note.Hit == 1).HitTimeMs, 100d);
            Assert(notes.Single(note => note.Hit == 1).Chord == notes.Single(note => note.Hit == 0).Chord, "One chord");
            Sequence(notes.OrderBy(note => note.Hit).Select(note => note.Tile).ToArray(), 1, 2, 3, 4);

            // Opening it again gives a session whose layout matches what was saved.
            var session = NoteEditSession.FromLayout(timeline, edit, [1, 2, 3, 4]);
            Equal(session.SourceHash, "hash");
            var again = session.ToLayout();
            Assert(again.Lanes.OrderBy(pair => pair.Key).SequenceEqual(edit.Lanes.OrderBy(pair => pair.Key)), "Same lanes");
            Sequence(again.Chords[0].ToArray(), 0, 1);

            static void Sequence(int[] actual, params int[] expected) =>
                Assert(actual.SequenceEqual(expected), "Got: " + string.Join(" ", actual));
        }),
    };
}
