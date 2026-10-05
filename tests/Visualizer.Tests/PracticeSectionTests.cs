using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using Visualizer.App.ViewModels;
using Visualizer.App.Views;
using Visualizer.Application.Cache;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Infrastructure.Cache;
using Visualizer.Infrastructure.Parsing;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

internal static class PracticeSectionTests
{
    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }

    // Floors 0..7 with a midspin on floor 3, and a checkpoint on floor 2: hit floors 1 2 4 5 6 7.
    private static ParsedTimeline Timeline() => new AdofaiTimelineParser().Parse(new("fixture.adofai",
        "{\"settings\":{\"version\":15,\"bpm\":120},\"angleData\":" + JsonSerializer.Serialize(new[] { 0d, 0, 0, 999, 0, 0, 0 })
        + ",\"actions\":[{\"floor\":2,\"eventType\":\"Checkpoint\"}]}")).Timeline!;

    public static IEnumerable<(string Name, Action Run)> Create(string artifacts)
    {
        yield return ("Practice start tile begins at the first hit after that tile, like checkpoint restarts", () =>
        {
            var timeline = Timeline();
            Sequence(timeline.HitFloors!, 1, 2, 4, 5, 6, 7);
            Sequence(new[] { 0, 1, 2, 4, 6 }.Select(timeline.GetStartIndexForTile), 0, 1, 2, 3, 5);
            // Same rule as the verified checkpoint restart on floor 2.
            Equal(timeline.GetStartIndexForTile(2), timeline.GetStartIndex(2));
            foreach (var invalid in new[] { -1, 3, 7, 100 })
                Throws<ArgumentOutOfRangeException>(() => timeline.GetStartIndexForTile(invalid));
            Throws<InvalidOperationException>(() => new ParsedTimeline(new[] { 0d, 500 }).GetStartIndexForTile(0));
            Throws<ArgumentException>(() => new ParsedTimeline(new[] { 0d, 500 }, hitFloors: new[] { 1 }));
            Throws<ArgumentException>(() => new ParsedTimeline(new[] { 0d, 500 }, hitFloors: new[] { 2, 2 }));
            Throws<ArgumentException>(() => new ParsedTimeline(new[] { 0d, 500 }, hitFloors: new[] { 0, 1 }));
        });

        yield return ("Session keeps the practice tile across reset, toggle and rule changes", () =>
        {
            var timeline = Timeline();
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            var settings = new VisualizerSettings { DynamicTiming = false, SimultaneousWindowMs = 0 };
            session.LoadParsedTimeline(timeline, settings);
            session.Toggle();
            session.ResetAtTile(settings, 4);
            Equal(session.StartTile, (int?)4);
            Equal(session.Notes.Count, 3); Equal(session.Notes[0].HitTimeMs, 0d);
            Equal(session.ActiveCheckpoint, (int?)2); // Checkpoint 2 restarts before this start.
            session.Start(); clock.NowMs = 300;
            session.ApplySettings(settings with { MaximumTileCount = 2 });
            Equal(session.State, PlaybackState.Waiting); Equal(session.StartTile, (int?)4); Equal(session.Notes.Count, 3);
            session.Toggle(); session.Toggle();
            Equal(session.StartTile, (int?)4); Equal(session.Notes.Count, 3);
            session.Restart(session.Settings, 1);
            Equal(session.Notes.Count, 3);
            Throws<ArgumentOutOfRangeException>(() => session.ResetAtTile(session.Settings, 3));
            Equal(session.StartTile, (int?)4);
            session.Reset(session.Settings, 1);
            Equal(session.StartTile, null); Equal(session.Notes.Count, 6);
            Assert(timeline.HitTimesMs.Count == 6, "Practice starts never change the source timeline");
        });

        yield return ("Practice starts keep full-song lanes and start on the first note's lane key", () =>
        {
            var timeline = Timeline();
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            var settings = new VisualizerSettings { DynamicTiming = false, SimultaneousWindowMs = 0, StartDelayMs = 250 };
            session.LoadParsedTimeline(timeline, settings);
            session.Toggle();
            var full = session.Notes;
            Sequence(full.Select(n => n.Tile), 5, 4, 3, 6, 5, 4);
            Equal(session.LaneStartTile, null); // From the beginning, the start key starts.
            session.ResetAtTile(settings, 4);
            // Not re-patterned from the Initial Tile: the same lanes as a full play, shifted to the judgment line.
            Sequence(session.Notes.Select(n => n.Tile), 6, 5, 4);
            Equal(session.Notes[0].HitTimeMs, 250d);
            for (var i = 0; i < session.Notes.Count; i++)
                Assert(Math.Abs(session.Notes[i].HitTimeMs - (full[i + 3].HitTimeMs - full[3].HitTimeMs + 250)) < 1e-9, "Relative timing is kept");
            Equal(session.LaneStartTile, (int?)6);
            Assert(session.StartsOnLane(6) && !session.StartsOnLane(5), "Only the first note's lane starts");
            // A later checkpoint slices the full play as well (user rule): same lanes, and its first note's lane key starts.
            session.Reset(settings, 2);
            var checkpointStart = timeline.GetStartIndex(2);
            Assert(checkpointStart > 0, "The fixture has a checkpoint after the first hit");
            Sequence(session.Notes.Select(n => n.Tile), full.Skip(checkpointStart).Select(n => n.Tile).ToArray());
            Equal(session.StartTile, null);
            Equal(session.LaneStartTile, (int?)session.Notes[0].Tile);
            Assert(session.StartsOnLane(session.Notes[0].Tile), "The checkpoint's first note lane starts");
            session.Reset(settings, 1);
            Equal(session.LaneStartTile, null); Assert(!session.StartsOnLane(5), "From the beginning no lane key starts");

            var model = new MainViewModel();
            model.ApplySettings(model.Settings with { DynamicTiming = false, SimultaneousWindowMs = 0 });
            model.Playback.LoadParsedTimeline(Timeline(), model.Settings);
            model.Toggle();
            Assert(model.KeyHint.Contains("J " + model.Text["Start"]), "From the beginning, J starts");
            model.SectionText = "4";
            Equal(model.StartKeyLabel, "K"); // Tile 6 is the sixth lane key.
            Assert(model.KeyHint.Contains("K " + model.Text["Start"]) && model.Message.EndsWith('K'), "The dynamic start key is shown");
            model.StartFromKey(); Equal(model.Playback.State, PlaybackState.Waiting);
            model.StartFromLane(5); Equal(model.Playback.State, PlaybackState.Waiting);
            model.StartFromLane(6); Equal(model.Playback.State, PlaybackState.Playing);
            model.SectionText = "";
            // Back at the checkpoint that contains the section: a later checkpoint also starts on its first note's lane key.
            var firstTile = model.Playback.Notes[0].Tile;
            Equal(model.StartKeyLabel, model.Checkpoint == 1 ? "J" : model.Settings.GetLaneKeyTokens()[firstTile - 1]);
            if (model.Checkpoint == 1) model.StartFromKey();
            else model.StartFromLane(firstTile);
            Equal(model.Playback.State, PlaybackState.Playing);
        });

        yield return ("Section box starts, keeps, rejects and clears practice tiles", () =>
        {
            var model = new MainViewModel();
            model.Playback.LoadParsedTimeline(Timeline(), model.Settings);
            model.Toggle();
            model.SectionText = "4";
            Equal(model.Playback.StartTile, (int?)4); Equal(model.SectionText, "4"); Equal(model.Checkpoint, 2);
            Assert(model.Message.Contains('4'), "The prepared tile is reported");
            model.Start(); model.Reset();
            Equal(model.Playback.StartTile, (int?)4); Equal(model.Playback.Notes.Count, 3);
            model.SectionText = "3";
            Equal(model.Playback.StartTile, (int?)4); Assert(model.Message.Contains("중간 회전"), "Midspin starts are rejected with a reason");
            model.SectionText = "abc";
            Equal(model.Playback.StartTile, (int?)4); Equal(model.SectionText, "4");
            model.SectionText = "";
            Equal(model.Playback.StartTile, null); Equal(model.SectionText, ""); Equal(model.Playback.Notes.Count, 4);
            model.SectionText = "1";
            Equal(model.Checkpoint, 1);
            model.Checkpoint = 1; // Picking the containing checkpoint returns to that checkpoint.
            Equal(model.Playback.StartTile, null); Equal(model.SectionText, ""); Equal(model.Playback.Notes.Count, 6);
            model.SectionText = "5";
            Assert(model.MoveCheckpoint(-1) || model.Checkpoint == 2, "Arrow navigation is available");
            model.LoadPreview();
            Equal(model.SectionText, ""); Equal(model.Playback.StartTile, null);
            model.SectionText = "2";
            Assert(model.Message.Contains("타일 번호가 없는"), "Synthetic timelines have no game tiles");
        });

        yield return ("Hit floors survive the app cache; older caches load without them", () =>
        {
            var root = Path.Combine(artifacts, "practice-cache", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "Test-owned fixture");
            File.WriteAllText(Path.Combine(root, "Visualizer.sln"), "Test-owned fixture");
            var maps = Path.Combine(root, "maps"); Directory.CreateDirectory(maps);
            var cache = new WorkspaceMapCache(root);
            MapCacheEntry Entry(string id, ParsedTimeline timeline) => new(id, maps, id + ".adofai", id, MapParseStatus.Supported,
                timeline, Array.Empty<string>(), "test", AdofaiTimelineParser.CurrentVersion);
            cache.ReplaceAsync(new[] { Entry("floors", Timeline()), Entry("legacy", new ParsedTimeline(new[] { 0d, 500 })) }).GetAwaiter().GetResult();
            var restored = new WorkspaceMapCache(root).ReadAsync().GetAwaiter().GetResult().ToDictionary(e => e.Id);
            Sequence(restored["floors"].Timeline!.HitFloors!, 1, 2, 4, 5, 6, 7);
            Equal(restored["legacy"].Timeline!.HitFloors, null);
            // The mini-game bands ride along with the hits.
            var banded = new ParsedTimeline(new[] { 0d, 500, 4500 }, null, false, null, null, new[] { new TimelineSection(500, 4500) });
            cache.ReplaceAsync(new[] { Entry("band", banded), Entry("legacy", new ParsedTimeline(new[] { 0d, 500 })) }).GetAwaiter().GetResult();
            var withBands = new WorkspaceMapCache(root).ReadAsync().GetAwaiter().GetResult().ToDictionary(e => e.Id);
            Assert(withBands["band"].Timeline!.Sections.SequenceEqual(new[] { new TimelineSection(500, 4500) })
                && withBands["legacy"].Timeline!.Sections.Count == 0, "Sections survive the cache");
            cache.ReplaceAsync(new[] { Entry("floors", Timeline()), Entry("legacy", new ParsedTimeline(new[] { 0d, 500 })) }).GetAwaiter().GetResult();
            var path = Path.Combine(cache.StorageDirectory, "library.json");
            File.WriteAllText(path, Regex.Replace(File.ReadAllText(path), ",\\s*\"HitFloors\":\\s*(null|\\[[^\\]]*\\])", ""));
            var older = new WorkspaceMapCache(root).ReadAsync().GetAwaiter().GetResult();
            Assert(older.All(e => e.Timeline!.HitFloors is null), "A cache written before tile numbers still loads");
        });

        yield return ("Section box renders next to the auto toggle", () =>
        {
            var window = new MainWindow();
            var model = (MainViewModel)window.DataContext;
            model.Playback.LoadParsedTimeline(Timeline(), model.Settings with { SimultaneousWindowMs = 0 });
            model.ApplySettings(model.Settings with { SimultaneousWindowMs = 0 });
            model.Toggle();
            Capture(window, Path.Combine(artifacts, "practice-section-empty.png"), 480, 800);
            model.SectionText = "4";
            Equal(((TextBox)window.FindName("SectionBox")).Text, "4");
            Capture(window, Path.Combine(artifacts, "practice-section-4.png"), 400, 640);
            window.Close();
        });
    }
}
