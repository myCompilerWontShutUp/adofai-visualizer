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
/// Extra lanes (여분 레인 배치): spare lanes beside a 2- or 4-lane view that take notes only in chord groups that cannot be placed,
/// the offer to turn them on instead of 8 lanes, and switches that keep their value while another setting locks them.
/// </summary>
internal static class ExtraLaneTests
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

    private static ParsedTimeline Group(int size) =>
        new(new[] { 0d }.Concat(Enumerable.Range(0, size).Select(i => 500d + i)).Append(1000));

    public static IEnumerable<(string Name, Action Run)> Create(string artifacts)
    {
        yield return ("Display lanes widen the screen only, and a spare lane is drawn where a note really sits", () =>
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
            // A spare lane is only on screen where a note sits on it: switching extra lanes on does not widen the field
            // by itself (user report).
            var spare = settings with { ExtraLanes = true };
            Sequence(spare.GetExtraTiles(), 2, 7);
            Sequence(spare.GetDisplayTiles(new[] { 3, 4, 5 }), 3, 4, 5, 6);
            Sequence(spare.GetDisplayTiles(new[] { 2, 3, 4 }), 2, 3, 4, 5, 6);
            // Every lane that may appear is still listed for layout and for an edited song's own lanes.
            Sequence(spare.GetDisplayTiles(), 2, 3, 4, 5, 6, 7);
            Sequence((wide with { ExtraLanes = true }).GetDisplayTiles(new[] { 3 }), 1, 2, 3, 4, 5, 6, 7, 8);
        });

        yield return ("Extra lanes sit beside the pattern, apply only to 2 and 4 lanes and keep their value while locked", () =>
        {
            var s = new VisualizerSettings();
            Assert(!s.ExtraLanes && s.GetExtraTiles().Count == 0, "Off by default");
            var on = s with { ExtraLanes = true };
            on.EnsureValid();
            Sequence(on.GetExtraTiles(), 2, 7);
            Sequence(on.GetDisplayTiles(), 2, 3, 4, 5, 6, 7);
            Sequence((on with { MaximumTileCount = 2 }).GetExtraTiles(), 3, 6);
            // A pattern reaching tile 1 or 8 puts both spare lanes on the other side.
            Sequence((on with { FourKeyPattern = "1 2 3 4", DynamicTiming = false }).GetExtraTiles(), 5, 6);
            Sequence((on with { FourKeyPattern = "5 6 7 8", DynamicTiming = false }).GetExtraTiles(), 3, 4);
            Equal((on with { FourKeyPattern = "1 3 6 8", DynamicTiming = false }).GetExtraTiles().Count, 0);
            foreach (var locked in new[] { 1, 8 })
            {
                var settings = on with { MaximumTileCount = locked, DynamicTiming = false };
                Assert(settings.ExtraLanes && !settings.CanUseExtraLanes && settings.GetExtraTiles().Count == 0, $"Locked at {locked} lanes, value kept");
            }
            Assert(!s.HasSameNoteRulesAs(on), "Turning extra lanes on re-prepares notes");
            Assert((s with { MaximumTileCount = 8, DynamicTiming = false }).HasSameNoteRulesAs(on with { MaximumTileCount = 8, DynamicTiming = false }),
                "A locked switch changes no note rule");
            var json = SettingsJson.Export(on);
            Assert(json.Contains("\"ExtraLanes\": true") && !json.Contains("CanUseExtraLanes"), "The stored switch is exported, the derived flag is not");
            Equal(SettingsJson.Import(json, s), on);
        });

        yield return ("Only unplaceable chord groups use spare lanes, as singles; placeable songs are unchanged", () =>
        {
            var generator = new NoteGenerator();
            var basis = new VisualizerSettings { DynamicTiming = false };
            var on = basis with { ExtraLanes = true };
            var placeable = new[] { 0d, 0, 400, 800, 800, 1200, 1600 };
            Assert(generator.Generate(placeable, on).SequenceEqual(generator.Generate(placeable, basis)), "Placeable songs never use spare lanes");
            Assert(generator.Generate(placeable, basis).Count(n => n.StartsChord) == 2, "Chords are marked explicitly");

            var five = new[] { 0d, 1, 2, 3, 4, 500 };
            Throws<NotePlacementException>(() => generator.Generate(five, basis));
            var group = generator.Generate(five, on).Take(5).ToArray();
            Equal(group.Select(n => n.Tile).Distinct().Count(), 5);
            Equal(group.Count(n => n.StartsChord), 2);
            var spareIndex = Array.FindIndex(group, n => n.Tile is 2 or 7);
            Assert(spareIndex >= 0 && group.Count(n => n.Tile is 2 or 7) == 1, "One note goes to a spare lane: " + string.Join(" ", group.Select(n => n.Tile)));
            Assert(!group[spareIndex].StartsChord && (spareIndex == 0 || !group[spareIndex - 1].StartsChord), "The spare note is a single, never part of a chord");
            Assert(group.All(n => n.HitTimeMs == 0 && n.Mode == 4), "Display time and mode stay the group's");

            Equal(generator.Generate(new[] { 0d, 1, 2, 3, 4, 5 }, on).Count(n => n.Tile is 2 or 7), 2);
            Throws<NotePlacementException>(() => generator.Generate(new[] { 0d, 1, 2, 3, 4, 5, 6 }, on));
            // Two lanes: a chord plus a single needs three contiguous lanes; the single goes to a spare lane instead.
            var two = basis with { MaximumTileCount = 2, ExtraLanes = true };
            Throws<NotePlacementException>(() => generator.Generate(new[] { 0d, 1, 2 }, two with { ExtraLanes = false }));
            var twoNotes = generator.Generate(new[] { 0d, 1, 2 }, two);
            Assert(twoNotes.Count(n => n.Tile is 3 or 6) == 1 && twoNotes.Count(n => n.StartsChord) == 1, "2 lanes: " + string.Join(" ", twoNotes.Select(n => n.Tile)));
        });

        yield return ("Unplaceable chords offer extra lanes once; 8 lanes only when extra lanes cannot help", () =>
        {
            var asked = new List<string>();
            var eightAsked = 0;
            var declined = new MainViewModel
            {
                ConfirmExtraLanes = message => { asked.Add(message); return false; },
                ConfirmWiderLanes = _ => { eightAsked++; return false; }
            };
            declined.SelectedMap = Entry("five", Group(5));
            declined.LoadSelectedSong();
            Equal(asked.Count, 1); Equal(eightAsked, 0);
            Assert(asked[0].Contains("여분 레인 배치") && asked[0].Contains("노트 배치 중단"), "The question carries the reason: " + asked[0]);
            Assert(!declined.Settings.ExtraLanes && declined.Playback.Notes.Count == 0, "Declining changes nothing");
            declined.Toggle(); declined.Reset();
            Equal(asked.Count, 1); Equal(eightAsked, 0);

            var accepted = new MainViewModel { ConfirmExtraLanes = _ => true, ConfirmWiderLanes = _ => throw new InvalidOperationException("Must not ask") };
            accepted.SelectedMap = Entry("five", Group(5));
            accepted.LoadSelectedSong();
            Assert(accepted.Settings.ExtraLanes && accepted.Playback.Settings.ExtraLanes, "Accepting turns extra lanes on");
            Equal(accepted.Settings.MaximumTileCount, 4); Equal(accepted.Playback.Notes.Count, 7);
            Assert(accepted.Message.Contains("여분 레인 배치"), "The switch is reported: " + accepted.Message);
            // The forced change is a step of its own that rollback undoes: the song then stays unplaced,
            // and loading it again does not ask a second time.
            var forced = accepted.SettingsHistory[^1];
            Assert(!forced.Before.ExtraLanes && forced.After.ExtraLanes, "Recorded as its own change");
            accepted.RollBack();
            Assert(!accepted.Settings.ExtraLanes && accepted.Playback.Notes.Count == 0 && accepted.SettingsHistory.Count == 0, "Rolled back to the unplaced song");
            accepted.ConfirmExtraLanes = _ => throw new InvalidOperationException("Must not ask again after a rollback");
            accepted.Reset();
            Assert(!accepted.Settings.ExtraLanes, "Stays rolled back");

            // Seven notes do not fit six lanes: extra lanes are not offered, 8 lanes are.
            var eight = new MainViewModel { ConfirmExtraLanes = _ => throw new InvalidOperationException("Must not ask"), ConfirmWiderLanes = _ => true };
            eight.SelectedMap = Entry("seven", Group(7));
            eight.LoadSelectedSong();
            Equal(eight.Settings.MaximumTileCount, 8); Equal(eight.Playback.Notes.Count, 9);

            var none = new MainViewModel
            {
                ConfirmExtraLanes = _ => throw new InvalidOperationException("Must not ask"),
                ConfirmWiderLanes = _ => throw new InvalidOperationException("Must not ask")
            };
            none.SelectedMap = Entry("nine", Group(9));
            none.LoadSelectedSong();
            Equal(none.Playback.Notes.Count, 0); Assert(!none.Settings.ExtraLanes && none.Settings.MaximumTileCount == 4, "Nothing switches");
        });

        yield return ("Setting switches keep their value while locked: extra lanes at 1/8 lanes and the tile preference order", () =>
        {
            var window = new SettingsPanel(new VisualizerSettings(), new MemorySettingsLayoutStore());
            CheckBox Switch(string field) => (CheckBox)window.InputFor(field)!;
            var extra = Switch(nameof(VisualizerSettings.ExtraLanes));
            var maximum = FindAll<ComboBox>(window).First(c => c.ItemsSource is int[]);
            extra.IsChecked = true;
            maximum.SelectedItem = 8;
            Assert(!extra.IsEnabled && extra.IsChecked == true, "Locked at 8 lanes, still on");
            Assert(window.BuildSettings().ExtraLanes, "The stored value survives the lock");
            maximum.SelectedItem = 4;
            Assert(extra.IsEnabled && extra.IsChecked == true, "Back at 4 lanes the switch is on again");

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

        yield return ("Spare lanes render beside the pattern lanes with singles on them", () =>
        {
            const int width = 300, height = 400;
            var session = new PlaybackSession(new Clock());
            // Everything at 0 is the play's first moment and would wear the start color; this checks the spare single as a single.
            var plain = new VisualizerSettings();
            session.LoadTimeline(new[] { 0d, 1, 2, 3, 4, 2000 },
                plain with { DynamicTiming = false, ExtraLanes = true, JudgmentLineHeightDip = 36, StartNoteColor = plain.NoteColor });
            session.Toggle();
            var field = new NoteField { Session = session };
            field.Measure(new Size(width, height));
            field.Arrange(new Rect(0, 0, width, height));
            field.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(field);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            // Six lanes of 50px: tiles 2..7. The judgment line is at y 334; the spare note sits there as a sky blue single.
            var spare = session.Notes.Take(5).Single(n => n.Tile is 2 or 7);
            var x = spare.Tile == 2 ? 25 : 275;
            var index = (334 * width + x) * 4;
            Assert(pixels[index] > 200 && pixels[index + 2] < 150, $"Spare lane single note color at x {x}: B {pixels[index]} R {pixels[index + 2]}");

            var window = new MainWindow();
            var model = (MainViewModel)window.DataContext;
            model.ApplySettings(model.Settings with { ExtraLanes = true, DynamicTiming = false });
            model.Playback.LoadTimeline(new[] { 0d, 1, 2, 3, 4, 600, 1200, 1201, 1800 }, model.Settings);
            model.Toggle();
            Capture(window, Path.Combine(artifacts, "extra-lanes.png"), 480, 800);
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
