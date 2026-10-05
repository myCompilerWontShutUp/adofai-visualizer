using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Visualizer.App.Rendering;
using Visualizer.App.ViewModels;
using Visualizer.App.Views;
using Visualizer.Application.Maps;
using Visualizer.Application.Cache;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Infrastructure.Cache;
using Visualizer.Infrastructure.Maps;
using Visualizer.Infrastructure.Parsing;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

internal static class CheckpointLayoutTests
{
    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }
    private static MapParseResult Parse(string actions, double[]? angles = null) => new AdofaiTimelineParser().Parse(new("fixture.adofai",
        "{\"settings\":{\"version\":15,\"bpm\":120},\"angleData\":" + JsonSerializer.Serialize(angles ?? new double[12])
        + ",\"actions\":" + actions + "}"));

    public static IEnumerable<(string Name, Action Run)> Create(string artifactRoot) => new (string, Action)[]
    {
        ("A press on the song-end offer reaches its button instead of taking the offer away", () =>
        {
            var clock = new Clock();
            MainWindow.PlaybackClock = clock;
            try
            {
                var window = new MainWindow();
                var model = (MainViewModel)window.DataContext;
                model.SelectedMap = new MapCacheEntry("end", "unused", "map.adofai", "end", MapParseStatus.Supported,
                    new ParsedTimeline([0, 500]), [], "test", "test");
                model.LoadSelectedSong();
                model.StartFromKey();
                clock.NowMs += 4000; // Past the last note, and well past the quiet second after it.
                model.RefreshPlaybackInfo();
                Assert(model.ShowSongEndNotice, "The song is over and the offer is up");
                var first = (FrameworkElement)window.FindName("RestartFromStartButton");
                // A press on the button tunnels through the window's own handler first. Counting that as activity would
                // hide the offer on the way down, taking the button out from under the press so its click never comes.
                first.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseDownEvent
                });
                Assert(model.ShowSongEndNotice, "The offer is still there for the button to be clicked");
                // A press anywhere else is activity, and the offer waits for another quiet second.
                ((FrameworkElement)window.FindName("Field")).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseDownEvent
                });
                Assert(!model.ShowSongEndNotice, "A press on the note area postpones it");
                window.Close();
            }
            finally { MainWindow.PlaybackClock = null; }
        }),
        ("The song-end offer draws both buttons without moving the first one", () =>
        {
            var window = new MainWindow();
            var card = (Border)window.FindName("SongEndCard");
            var first = (FrameworkElement)window.FindName("RestartFromStartButton");
            var second = (FrameworkElement)window.FindName("LastCheckpointButton");
            // Local values stand in for a finished song, so the layout can be measured without waiting the clock out.
            card.Visibility = Visibility.Visible;
            second.Visibility = Visibility.Visible;
            Capture(window, Path.Combine(artifactRoot, "song-end.png"), 480, 800);
            var content = (FrameworkElement)window.Content;
            double TopOf(FrameworkElement element) => element.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
            var withBoth = TopOf(first);
            Assert(withBoth > 0 && first.ActualHeight > 0, $"The offer must be laid out: y={withBoth} h={first.ActualHeight}");
            Equal(first.ActualHeight, second.ActualHeight);
            // Hidden, not collapsed: the second button gives up its picture but keeps its row, so the first one stays put
            // wherever the song has no checkpoint to go back to (user rule).
            second.Visibility = Visibility.Hidden;
            Capture(window, Path.Combine(artifactRoot, "song-end-alone.png"), 480, 800);
            Equal(TopOf(first), withBoth);
            second.Visibility = Visibility.Collapsed;
            Capture(window, Path.Combine(artifactRoot, "song-end-collapsed.png"), 480, 800);
            Assert(TopOf(first) > withBoth, "Collapsing it would be the mistake: the first button would move down");
            window.Close();
        }),
        ("Checkpoint offsets, clamping and non-advancing events", () =>
        {
            var result = Parse("""
                [{"floor":2,"eventType":"Checkpoint"},
                 {"floor":4,"eventType":"Checkpoint","tileOffset":2},
                 {"floor":7,"eventType":"Checkpoint","tileOffset":-2},
                 {"floor":9,"eventType":"Checkpoint","tileOffset":-1},
                 {"floor":10,"eventType":"Checkpoint","tileOffset":999}]
                """);
            Equal(result.Status, MapParseStatus.Supported);
            var markers = result.Timeline!.Checkpoints;
            Equal(markers.Count, 5); // floor 7 cannot move the saved respawn floor backwards.
            Equal(markers[1], new TimelineCheckpoint(2, 2, 1000, 2, 2));
            Equal(markers[2], new TimelineCheckpoint(3, 6, 2000, 4, 6));
            Equal(markers[3], new TimelineCheckpoint(4, 8, 4500, 9, 8));
            Equal(markers[4], new TimelineCheckpoint(5, 11, 5000, 10, 11));
            Equal(Parse("[{\"floor\":1,\"eventType\":\"Checkpoint\",\"tileOffset\":-999}]").Timeline!.Checkpoints.Count, 1);
            Equal(Parse("[{\"floor\":1,\"eventType\":\"Checkpoint\",\"tileOffset\":0.5}]").Status, MapParseStatus.Error);
            Equal(Parse("[{\"floor\":1,\"eventType\":\"Checkpoint\",\"angleOffset\":90}]").Status, MapParseStatus.Unsupported);
            // A checkpoint on a midspin tile restarting on a midspin tile stays unsupported.
            Equal(Parse("[{\"floor\":1,\"eventType\":\"Checkpoint\"}]", [0, 999, 0]).Status, MapParseStatus.Unsupported);
            // The event may sit on a midspin tile (the game runs a floor's effects when the planet moves onto it) as long as its
            // restart tile is a normal one (Unstable haze).
            var midspin = Parse("[{\"floor\":1,\"eventType\":\"Checkpoint\",\"tileOffset\":1}]", [0, 999, 0, 0, 0]);
            Equal(midspin.Status, MapParseStatus.Supported);
            Equal(midspin.Timeline!.Checkpoints.Count, 2);
            Equal(midspin.Timeline.Checkpoints[1].RespawnFloor, 2);
            // A normal tile whose offset lands on a midspin tile is still rejected.
            Equal(Parse("[{\"floor\":1,\"eventType\":\"Checkpoint\",\"tileOffset\":1}]", [0, 0, 999, 0, 0]).Status, MapParseStatus.Unsupported);
            Equal(Parse("[{\"floor\":1,\"eventType\":\"Checkpoint\",\"eventTag\":\"c\"},{\"floor\":2,\"eventType\":\"RepeatEvents\",\"tag\":\"c\"}]").Status, MapParseStatus.Unsupported);
        }),
        ("Checkpoint activation precedes offset restart; reset and start keep timing", () =>
        {
            var timeline = Parse("[{\"floor\":4,\"eventType\":\"Checkpoint\",\"tileOffset\":2}]").Timeline!;
            var clock = new Clock(); var session = new PlaybackSession(clock);
            session.LoadParsedTimeline(timeline, new()); session.Toggle(); session.Start();
            clock.NowMs = 1649; Equal(session.ActiveCheckpoint, (int?)1);
            clock.NowMs = 1650; Equal(session.ActiveCheckpoint, (int?)2); // floor 4 arrival + press window, not respawn floor 6.
            session.Reset(session.Settings, 2);
            Equal(session.Notes.Count, 6); Equal(session.Notes[0].HitTimeMs, 0d);
            Equal(session.Notes[1].HitTimeMs, 500d); Equal(session.ActiveCheckpoint, (int?)2);
            var notes = session.Notes; session.Start();
            Assert(ReferenceEquals(notes, session.Notes), "Start must not regenerate checkpoint notes");
            clock.NowMs += 200; Equal(session.TimelineTimeMs, 200d);
            session.Toggle(); session.Toggle(); Equal(session.ActiveCheckpoint, (int?)2);
            Equal(session.State, PlaybackState.Waiting);
        }),
        ("Checkpoint metadata survives app-owned cache roundtrip", () =>
        {
            var root = Path.Combine(artifactRoot, "checkpoint-cache", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "Test-owned fixture");
            File.WriteAllText(Path.Combine(root, "Visualizer.sln"), "Test-owned fixture");
            var maps = Path.Combine(root, "maps"); Directory.CreateDirectory(maps);
            var parsed = Parse("[{\"floor\":4,\"eventType\":\"Checkpoint\",\"tileOffset\":2}]");
            var cache = new WorkspaceMapCache(root);
            cache.ReplaceAsync(new[] { new MapCacheEntry("test", maps, "fixture.adofai", "test", MapParseStatus.Supported,
                parsed.Timeline, Array.Empty<string>(), "test", new AdofaiTimelineParser().Version) }).GetAwaiter().GetResult();
            var restored = new WorkspaceMapCache(root).ReadAsync().GetAwaiter().GetResult().Single().Timeline!;
            Equal(restored.Checkpoints[1], parsed.Timeline!.Checkpoints[1]);
            Equal(restored.GetStartIndex(2), 6);
        }),
        ("Visible lanes respect maximum mode and reject hidden dynamic notes", () =>
        {
            foreach (var count in new[] { 1, 2, 4, 8 })
            {
                var settings = new VisualizerSettings { MaximumTileCount = count };
                settings.EnsureValid(); Equal(settings.GetVisibleTiles().Count, count);
                foreach (var mode in new[] { 1, 2, 4, 8 }.Where(mode => mode <= count))
                    Assert(settings.GetPattern(mode).All(settings.GetVisibleTiles().Contains), "Dynamic notes must remain visible");
            }
            Assert(new VisualizerSettings().GetVisibleTiles().SequenceEqual(new[] { 3, 4, 5, 6 }), "Keep logical tile identities");
            Throws<ArgumentException>(() => new VisualizerSettings { FourKeyPattern = "1 2 3 4" }.EnsureValid());
            new VisualizerSettings { DynamicTiming = false, FourKeyPattern = "1 2 3 4" }.EnsureValid();
        }),
        ("Plum checkpoints and compact WPF layout at 1/2/4/8 lanes", () =>
        {
            var root = WorkspaceMapCache.FindRepositoryRoot(AppContext.BaseDirectory);
            var source = new ReadOnlyDirectoryMapSource(Path.Combine(root, ".example"));
            var result = new AdofaiTimelineParser().Parse(source.ReadAsync("main.adofai").GetAwaiter().GetResult());
            var timeline = result.Timeline!;
            Assert(timeline.Checkpoints.Skip(1).Select(c => c.SourceFloor).SequenceEqual(new int?[] { 64, 130, 159, 205, 358, 490 }), "All six real checkpoints");
            Equal(timeline.Checkpoints[5].RespawnFloor, (int?)361);
            foreach (var count in new[] { 1, 2, 4, 8 })
            {
                var window = new MainWindow(); var model = (MainViewModel)window.DataContext;
                // A little under the 520 the settings used to open at, back when they were a window (user rule).
                Equal(window.Width, 500d);
                model.ApplySettings(model.Settings with { MaximumTileCount = count });
                model.SelectedMap = new("plum", source.RootDirectory, "main.adofai", "Plum - R", MapParseStatus.Supported,
                    timeline, result.Warnings, "test", new AdofaiTimelineParser().Version);
                model.LoadSelectedSong(); // Loading switches the visualizer on.
                model.Checkpoint = 6; Equal(model.Playback.State, PlaybackState.Waiting);
                Equal(model.Playback.Notes[0].HitTimeMs, 0d);
                Capture(window, Path.Combine(artifactRoot, $"compact-{count}k.png"), 480, 800);
                var picker = (ComboBox)window.FindName("CheckpointPicker");
                Equal(picker.Items.Count, 7); Equal((int)picker.SelectedValue, 6);
                picker.SelectedValue = 3;
                Equal(model.Checkpoint, 3); Equal(model.Playback.Notes.Count, 726 - timeline.GetStartIndex(3));
                var field = (NoteField)window.FindName("Field");
                Assert(field.ActualHeight > 500, $"Note area too short: {field.ActualHeight}");
                var gear = (Button)window.FindName("SettingsButton");
                Assert(gear.TranslatePoint(new Point(0, 0), (UIElement)window.Content).Y > 600, "Settings gear must be at bottom");
                if (count == 4)
                {
                    model.ApplySettings(model.Settings with { ShowGuideButtons = false });
                    Capture(window, Path.Combine(artifactRoot, "compact-guide-hidden.png"), 480, 800);
                }
                window.Close();
            }
        })
    };
}
