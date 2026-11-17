using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Visualizer.App.Rendering;
using Visualizer.App.ViewModels;
using Visualizer.App.Views;
using Visualizer.Application.Cache;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Core.Localization;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using static Visualizer.Tests.Program;
using Path = System.IO.Path;

namespace Visualizer.Tests;

/// <summary>
/// Display lanes, and the maximum tile count's auto (자동), which took the place of extra lane placement (user request):
/// each song is laid out with the fewest tiles it needs, four unless a chord is wider or nothing smaller places it, so a
/// wide song takes its lanes and the next one starts from four again. Also the offer of a wider maximum for a fixed count,
/// and switches that keep their value while another setting locks them.
/// </summary>
internal static class AutoTileTests
{
    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static MapCacheEntry Entry(string id, ParsedTimeline timeline) =>
        new(id, "unused", "map.adofai", id, MapParseStatus.Supported, timeline, Array.Empty<string>(), "test", "test");

    /// <summary>The maximum tile count on auto, with the enhanced 4/8-key rules it widens with.</summary>
    private static VisualizerSettings Auto() => new()
    {
        MaximumTileCount = 8, AutoTileCount = true, ExperimentalFeatures = true, EnhancedFourEightKeys = true
    };

    public static IEnumerable<(string Name, Action Run)> Create(string artifacts)
    {
        yield return ("Display lanes widen the screen only; with auto the screen is as wide as the song was laid out", () =>
        {
            var settings = new VisualizerSettings { MaximumTileCount = 4 };
            Sequence(settings.GetShownTiles(), 3, 4, 5, 6);
            var wide = settings with { DisplayLaneCount = 8 };
            wide.EnsureValid();
            Sequence(wide.GetShownTiles(), 1, 2, 3, 4, 5, 6, 7, 8);
            // The rules are untouched: placement keeps using the maximum's lanes.
            Sequence(wide.GetVisibleTiles(), 3, 4, 5, 6);
            // Fewer lanes than the rules use would hide a note, so it is refused (user rule).
            Throws<ArgumentException>(() => (settings with { DisplayLaneCount = 2 }).EnsureValid());
            Throws<ArgumentException>(() => (settings with { DisplayLaneCount = 5 }).EnsureValid());
            // A fixed count shows its lanes whatever the notes are.
            Sequence(settings.GetDisplayTiles(new[] { 4 }), 3, 4, 5, 6);
            Sequence(settings.GetDisplayTiles(), 3, 4, 5, 6);

            // Auto: the lanes of the tile count the song was laid out with, read off the lanes its notes sit on.
            var auto = Auto();
            auto.EnsureValid();
            Sequence(auto.GetAutoTileCounts(), 4, 6, 8);
            Sequence(auto.GetDisplayTiles(Array.Empty<int>()), 3, 4, 5, 6);
            Sequence(auto.GetDisplayTiles(new[] { 3, 4, 5 }), 3, 4, 5, 6);
            Sequence(auto.GetDisplayTiles(new[] { 2, 3, 4 }), 2, 3, 4, 5, 6, 7);
            Sequence(auto.GetDisplayTiles(new[] { 4, 8 }), 1, 2, 3, 4, 5, 6, 7, 8);
            // Every lane may come on screen, so a saved edit's lanes are never refused.
            Sequence(auto.GetDisplayTiles(), 1, 2, 3, 4, 5, 6, 7, 8);
            // The keys played are those lanes (the game's key limiter takes them).
            Sequence(auto.GetPlayedTiles(new[] { 3 }), 3, 4, 5, 6);
            Sequence(auto.GetPlayedTiles(new[] { 1 }), 1, 2, 3, 4, 5, 6, 7, 8);
            // A display lane count is the least the screen shows there: never a reason to refuse the settings, and no key.
            var six = auto with { DisplayLaneCount = 6 };
            six.EnsureValid();
            Sequence(six.GetDisplayTiles(new[] { 3 }), 2, 3, 4, 5, 6, 7);
            Sequence(six.GetDisplayTiles(new[] { 8 }), 1, 2, 3, 4, 5, 6, 7, 8);
            Sequence(six.GetPlayedTiles(new[] { 3 }), 3, 4, 5, 6);
            // Auto goes with a maximum of eight only; a higher minimum is where it starts.
            Throws<ArgumentException>(() => (auto with { MaximumTileCount = 4 }).EnsureValid());
            Sequence((auto with { MinimumTileCount = 6 }).GetAutoTileCounts(), 6, 8);
            Sequence((auto with { MinimumTileCount = 6 }).GetDisplayTiles(new[] { 4 }), 2, 3, 4, 5, 6, 7);
            // Without the enhanced 4/8-key rules it does not widen (user rule, for now): four tiles.
            var plain = auto with { EnhancedFourEightKeys = false };
            plain.EnsureValid();
            Sequence(plain.GetAutoTileCounts(), 4);
            Sequence((auto with { ExperimentalFeatures = false }).GetAutoTileCounts(), 4);
            // The slower patterns have to sit on the lanes of every count auto may take: tile 1 is on no four-lane screen.
            Throws<ArgumentException>(() => (auto with { InitialTile = 1 }).EnsureValid());
            (auto with { InitialTile = 1, AutoTileCount = false }).EnsureValid();
        });

        yield return ("Auto is a stored switch: off by default, a note rule, exported, and what extra lane placement becomes", () =>
        {
            var s = new VisualizerSettings();
            Assert(!s.AutoTileCount && s.MaximumTileCount == 4, "Off by default, four tiles");
            var auto = Auto();
            Assert(!auto.HasSameNoteRulesAs(auto with { AutoTileCount = false }), "Turning auto on or off re-prepares notes");
            Assert(auto.HasSameNoteRulesAs(auto with { DisplayLaneCount = 8 }), "Display lanes still change no note rule");
            var json = SettingsJson.Export(auto);
            Assert(json.Contains("\"AutoTileCount\": true"), "The switch is exported: " + json);
            Equal(SettingsJson.Import(json, s), auto);
            // A settings file from before, with extra lane placement on and the enhanced 4/8-key rules in force, becomes
            // auto (user decision); any other only loses the key.
            const string enhanced = "\"ExperimentalFeatures\": true, \"EnhancedFourEightKeys\": true";
            var moved = SettingsJson.Import("{ \"MaximumTileCount\": 4, \"ExtraLanes\": true, " + enhanced + " }", s);
            Assert(moved.AutoTileCount && moved.MaximumTileCount == 8, "Extra lanes with the enhanced rules is auto now");
            moved.EnsureValid();
            var off = SettingsJson.Import("{ \"MaximumTileCount\": 4, \"ExtraLanes\": false, " + enhanced + " }", s);
            Assert(!off.AutoTileCount && off.MaximumTileCount == 4, "Extra lanes off stays a fixed count");
            var plain = SettingsJson.Import("{ \"MaximumTileCount\": 4, \"ExtraLanes\": true }", s);
            Assert(!plain.AutoTileCount && plain.MaximumTileCount == 4, "Without the enhanced rules the count stays");
            // A file that says what auto is keeps its own word.
            var both = SettingsJson.Import("{ \"MaximumTileCount\": 8, \"AutoTileCount\": false, \"ExtraLanes\": true, " + enhanced + " }", s);
            Assert(!both.AutoTileCount && both.MaximumTileCount == 8, "The new key wins over the old one");
            Throws<SettingsJsonException>(() => SettingsJson.Import("{ \"ExtraLanes\": \"yes\" }", s));
            Equal(Languages.Get(Languages.Korean, "TileCountAuto"), "자동");
        });

        yield return ("Auto lays a song out with the fewest tiles it needs: four unless a chord is wider or nothing smaller places it", () =>
        {
            var generator = new NoteGenerator();
            var auto = Auto() with { SimultaneousWindowMs = 25 };
            var four = auto.WithTileCount(4);
            int[] Lanes(IEnumerable<VisualizerNote> notes) => auto.GetDisplayTiles(notes.Select(note => note.Tile)).ToArray();
            string Tiles(IEnumerable<VisualizerNote> notes) => string.Join(" ", notes.Select(note => $"{note.Tile}@{note.HitTimeMs}"));

            // A song with nothing wider than four notes at once is exactly the four-lane song (user rule: after a wide song
            // the next one is back on four lanes): fast singles, trills and three- and four-note chords alike.
            var random = new Random(5);
            var gaps = new[] { 30d, 45, 60, 90, 130, 250, 400 };
            var times = new List<double>();
            for (var at = 0d; times.Count < 400; at += gaps[random.Next(gaps.Length)])
            {
                times.Add(at);
                for (var more = random.Next(8) == 0 ? random.Next(1, 4) : 0; more > 0; more--) times.Add(at + 1);
            }
            var song = generator.Generate(times.ToArray(), auto);
            Assert(song.SequenceEqual(generator.Generate(times.ToArray(), four)), "The same notes as with a maximum of four");
            Assert(song.Count == times.Count && song.All(note => note.Tile is >= 3 and <= 6), "Every note on the four-key lanes");
            Sequence(Lanes(song), 3, 4, 5, 6);

            // Five notes at once need six tiles and eight need eight: the whole song is then laid out with that count,
            // singles still on the four-key lanes (the enhanced 4/8-key rules).
            var fiveTimes = new[] { 0d, 1, 2, 3, 4, 600, 1200, 1800 };
            var five = generator.Generate(fiveTimes, auto);
            Assert(five.SequenceEqual(generator.Generate(fiveTimes, auto.WithTileCount(6))), "Six tiles for five notes at once: " + Tiles(five));
            Equal(five.Count(note => note.HitTimeMs == 0), 5);
            Sequence(Lanes(five), 2, 3, 4, 5, 6, 7);
            Assert(five.Skip(5).All(note => note.Tile is >= 3 and <= 6), "Singles keep to the four-key lanes: " + Tiles(five));
            var eightTimes = new[] { 0d, 1, 2, 3, 4, 5, 6, 7, 600, 1200 };
            var eight = generator.Generate(eightTimes, auto);
            Assert(eight.SequenceEqual(generator.Generate(eightTimes, auto.WithTileCount(8))), "Eight tiles for eight notes at once: " + Tiles(eight));
            Equal(eight.Count(note => note.HitTimeMs == 0), 8);
            Sequence(Lanes(eight), 1, 2, 3, 4, 5, 6, 7, 8);
            Assert(eight.Skip(8).All(note => note.Tile is >= 3 and <= 6), "Singles keep to the four-key lanes: " + Tiles(eight));
            // Seven at once are more than six lanes hold.
            Equal(generator.Generate(new[] { 0d, 1, 2, 3, 4, 5, 6, 600 }, auto).Count(note => note.HitTimeMs == 0), 7);
            // A fixed four cuts the same chord at its lanes, which is what auto is there to avoid.
            Equal(generator.Generate(fiveTimes, four).Count(note => note.HitTimeMs == 0), 4);

            // A song the count cannot place takes the next one that can: a chord beside a long note (see HeldChord).
            var session = new PlaybackSession(new Clock());
            session.LoadParsedTimeline(HeldChord(3), auto);
            Sequence(Lanes(session.Notes), 2, 3, 4, 5, 6, 7);
            session.LoadParsedTimeline(HeldChord(4), auto);
            Sequence(Lanes(session.Notes), 1, 2, 3, 4, 5, 6, 7, 8);
            // None places a chord of five there, and the reason is still reported.
            Throws<NotePlacementException>(() => session.LoadParsedTimeline(HeldChord(5), auto));

            // Without the enhanced 4/8-key rules auto does not widen: the four-lane song, cut chords and all.
            var narrow = auto with { EnhancedFourEightKeys = false };
            var cut = generator.Generate(fiveTimes, narrow);
            Assert(cut.SequenceEqual(generator.Generate(fiveTimes, narrow.WithTileCount(4))) && cut.All(note => note.Tile is >= 3 and <= 6),
                "Four tiles without the enhanced rules: " + Tiles(cut));
        });

        yield return ("A fixed count that cannot place a song offers a wider maximum once; auto takes the tiles and asks nothing", () =>
        {
            var asked = new List<string>();
            var declined = new MainViewModel { ConfirmWiderLanes = message => { asked.Add(message); return false; } };
            declined.SelectedMap = Entry("held", HeldChord(3));
            declined.LoadSelectedSong();
            Equal(asked.Count, 1);
            Assert(asked[0].Contains("노트 배치 중단"), "The question carries the reason: " + asked[0]);
            Assert(declined.Settings.MaximumTileCount == 4 && declined.Playback.Notes.Count == 0, "Declining changes nothing");
            declined.Toggle(); declined.Reset();
            Equal(asked.Count, 1);

            var accepted = new MainViewModel { ConfirmWiderLanes = _ => true };
            accepted.SelectedMap = Entry("held", HeldChord(3));
            accepted.LoadSelectedSong();
            Equal(accepted.Settings.MaximumTileCount, 6); Equal(accepted.Playback.Notes.Count, 5);
            // The forced change is a step of its own that rollback undoes: the song then stays unplaced,
            // and loading it again does not ask a second time.
            var forced = accepted.SettingsHistory[^1];
            Assert(forced.Before.MaximumTileCount == 4 && forced.After.MaximumTileCount == 6, "Recorded as its own change");
            accepted.RollBack();
            Assert(accepted.Settings.MaximumTileCount == 4 && accepted.Playback.Notes.Count == 0 && accepted.SettingsHistory.Count == 0,
                "Rolled back to the unplaced song");
            accepted.ConfirmWiderLanes = _ => throw new InvalidOperationException("Must not ask again after a rollback");
            accepted.Reset();
            Equal(accepted.Settings.MaximumTileCount, 4);

            // One lane: the note right after a long note never takes its lane, which a single lane cannot avoid.
            var wider = new MainViewModel { ConfirmWiderLanes = _ => true };
            wider.ApplySettings(wider.Settings with { MaximumTileCount = 1, MinimumTileCount = 1 });
            wider.SelectedMap = Entry("one", new ParsedTimeline(new[] { 0d, 500, 501, 2000 },
                hitKinds: new[] { HitKind.HoldStart, HitKind.HoldEnd, HitKind.Tap, HitKind.Tap }));
            wider.LoadSelectedSong();
            Equal(wider.Settings.MaximumTileCount, 2); Equal(wider.Playback.Notes.Count, 3);

            var none = new MainViewModel { ConfirmWiderLanes = _ => throw new InvalidOperationException("Must not ask") };
            none.SelectedMap = Entry("held", HeldChord(6));
            none.LoadSelectedSong();
            Equal(none.Playback.Notes.Count, 0); Equal(none.Settings.MaximumTileCount, 4);

            // Auto: the song takes the tiles it needs, the settings stay the user's, and the next song is back on four lanes.
            var auto = new MainViewModel { ConfirmWiderLanes = _ => throw new InvalidOperationException("Auto never asks") };
            auto.ApplySettings(Auto());
            var steps = auto.SettingsHistory.Count;
            int[] Lanes() => auto.Settings.GetDisplayTiles(auto.Playback.Notes.Select(note => note.Tile)).ToArray();
            auto.LoadChosen(Entry("held", HeldChord(3)), null);
            Equal(auto.Playback.Notes.Count, 5);
            Sequence(Lanes(), 2, 3, 4, 5, 6, 7);
            auto.LoadChosen(Entry("plain", new ParsedTimeline(new[] { 0d, 500, 1000, 1500 })), null);
            Equal(auto.Playback.Notes.Count, 4);
            Sequence(Lanes(), 3, 4, 5, 6);
            Assert(auto.Settings == Auto() && auto.SettingsHistory.Count == steps, "The settings are untouched");
            // One no count places says why, and still asks nothing.
            auto.LoadChosen(Entry("none", HeldChord(5)), null);
            Assert(auto.Playback.Notes.Count == 0 && auto.Message.Contains("노트 배치 중단"), "The reason is shown: " + auto.Message);
        });

        yield return ("The maximum tile count offers auto, which opens every boundary; the tile preference keeps its order while off", () =>
        {
            var window = new SettingsPanel(new VisualizerSettings(), new MemorySettingsLayoutStore());
            CheckBox Switch(string field) => (CheckBox)window.InputFor(field)!;
            var maximum = FindAll<ComboBox>(window).First(c => c.ItemsSource is int[]);
            Sequence((int[])maximum.ItemsSource, 1, 2, 4, 6, 8, SettingsPanel.AutoTiles);
            var eightKey = window.InputFor(nameof(VisualizerSettings.EightKeyBoundaryMs))!;
            var sixKey = window.InputFor(nameof(VisualizerSettings.SixKeyBoundaryMs))!;
            Assert(!eightKey.IsEnabled && !sixKey.IsEnabled, "At four tiles the 8K and 6K boundaries are locked");
            // User rule: with auto every boundary below applies, since a song may take any count.
            maximum.SelectedItem = SettingsPanel.AutoTiles;
            Assert(eightKey.IsEnabled && sixKey.IsEnabled, "Auto opens every boundary");
            var built = window.BuildSettings();
            Assert(built.AutoTileCount && built.MaximumTileCount == 8, "Auto is the switch with a maximum of eight");
            built.EnsureValid();
            maximum.SelectedItem = 4;
            Assert(!window.BuildSettings().AutoTileCount && window.BuildSettings().MaximumTileCount == 4, "A number turns auto off");
            Assert(!eightKey.IsEnabled, "And locks the boundaries above it again");
            // A panel opened on auto shows it and gives the same settings back.
            var onAuto = new SettingsPanel(Auto(), new MemorySettingsLayoutStore());
            Equal(FindAll<ComboBox>(onAuto).First(c => c.ItemsSource is int[]).SelectedItem, (object)SettingsPanel.AutoTiles);
            Equal(onAuto.BuildSettings(), Auto());
            // Eight and auto share the stored maximum, so the row is marked by the switch between them as well.
            var onEight = new SettingsPanel(Auto() with { AutoTileCount = false }, new MemorySettingsLayoutStore());
            Assert(!onEight.IsMarkedChanged(nameof(VisualizerSettings.MaximumTileCount)), "Nothing is changed yet");
            FindAll<ComboBox>(onEight).First(c => c.ItemsSource is int[]).SelectedItem = SettingsPanel.AutoTiles;
            Assert(onEight.IsMarkedChanged(nameof(VisualizerSettings.MaximumTileCount)) && onEight.SectionState("SectionPattern") == "changed",
                "From eight to auto marks the row and its section");

            var preference = Switch(nameof(VisualizerSettings.EnableTilePreference));
            preference.IsChecked = true;
            var order = FindAll<TextBox>(window).Single(t => t.Text == "4 3 2 1");
            order.Text = "1 2 3 4";
            preference.IsChecked = false;
            var locked = window.BuildSettings();
            Equal(locked.FourKeyOrder, "1 2 3 4"); // Kept aside while the preference is off...
            Sequence(locked.GetPattern(4), 6, 5, 4, 3); // ...while the default order applies.
            preference.IsChecked = true;
            Equal(order.Text, "1 2 3 4");
        });

        yield return ("With auto a wide song is drawn on the lanes it takes and the next on four; display lanes draw unused ones", () =>
        {
            const int width = 300, height = 400;
            var session = new PlaybackSession(new Clock());
            var settings = Auto() with { SimultaneousWindowMs = 25, JudgmentLineHeightDip = 36 };
            session.LoadTimeline(new[] { 0d, 1, 2, 3, 4, 2000 }, settings);
            session.Toggle();
            var field = new NoteField { Session = session };
            field.Measure(new Size(width, height));
            field.Arrange(new Rect(0, 0, width, height));
            field.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(field);
            Equal(field.LaneCount, 6);
            // The next song has nothing wider than two notes at once: four lanes again, with the same settings.
            session.LoadTimeline(new[] { 0d, 1, 600, 1200, 2000 }, settings);
            field.Tick();
            field.InvalidateVisual();
            field.UpdateLayout();
            bitmap.Render(field);
            Equal(field.LaneCount, 4);

            var window = new MainWindow();
            var model = (MainViewModel)window.DataContext;
            model.ApplySettings(model.Settings with { MaximumTileCount = 8, AutoTileCount = true, ExperimentalFeatures = true,
                EnhancedFourEightKeys = true, SimultaneousWindowMs = 25 });
            model.Playback.LoadTimeline(new[] { 0d, 1, 2, 3, 4, 600, 1200, 1201, 1800 }, model.Settings);
            model.Toggle();
            Capture(window, Path.Combine(artifacts, "auto-lanes.png"), 480, 800);
            window.Close();

            // Lanes the rules never use are drawn too when the display lane count asks for them (user request).
            var wide = new MainWindow();
            var wideModel = (MainViewModel)wide.DataContext;
            wideModel.ApplySettings(wideModel.Settings with { MaximumTileCount = 4, DisplayLaneCount = 8, DynamicTiming = false });
            wideModel.Playback.LoadTimeline(new[] { 0d, 600, 1200, 1800 }, wideModel.Settings);
            wideModel.Toggle();
            Capture(wide, Path.Combine(artifacts, "display-lanes.png"), 480, 800);
            wide.Close();
        });
    }
}
