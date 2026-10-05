using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Visualizer.App.Input;
using Visualizer.App.Localization;
using Visualizer.App.ViewModels;
using Visualizer.App.Views;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Localization;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using Visualizer.Infrastructure.Cache;
using Visualizer.Infrastructure.Maps;
using Visualizer.Infrastructure.Parsing;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

internal static class PlumExampleTests
{
    private sealed class OrdinaryButton : Button
    {
        public void Down(KeyEventArgs e) => OnKeyDown(e);
    }
    private sealed class GuardedButton : PlaybackButton
    {
        public void Down(KeyEventArgs e) => OnKeyDown(e);
        public void Up(KeyEventArgs e) => OnKeyUp(e);
    }
    private static MapParseResult Parse(double[] angles, string actions = "[]") => new AdofaiTimelineParser().Parse(new("fixture.adofai",
        "{\"settings\":{\"version\":15,\"bpm\":120},\"angleData\":" + JsonSerializer.Serialize(angles) + ",\"actions\":" + actions + "}"));
    private static void Times(MapParseResult result, params double[] expected)
    {
        Equal(result.Status, MapParseStatus.Supported);
        var actual = result.Timeline!.HitTimesMs;
        Equal(actual.Count, expected.Length);
        for (var i = 0; i < actual.Count; i++) Assert(Math.Abs(actual[i] - expected[i]) < 0.0001, $"Wrong timing at {i}: {actual[i]} != {expected[i]}");
    }

    public static IEnumerable<(string Name, Action Run)> Create(string artifactRoot) => new (string, Action)[]
    {
        ("Two-planet timing: straight, turns, twirl, speed, midspin", () =>
        {
            Times(Parse(new[] { 0d, 0, 0 }), 500, 1000, 1500);
            Times(Parse(new[] { 0d, 90, 90, 180 }), 500, 750, 1250, 1500);
            Times(Parse(new[] { 0d, 90, 90 }, "[{\"floor\":1,\"eventType\":\"Twirl\"}]"), 500, 1250, 1750);
            Times(Parse(new[] { 0d, 90, 90 }, "[{\"floor\":1,\"eventType\":\"SetSpeed\",\"speedType\":\"Bpm\",\"beatsPerMinute\":240}]"), 500, 625, 875);
            Times(Parse(new[] { 0d, 0, 0 }, "[{\"floor\":2,\"eventType\":\"SetSpeed\",\"speedType\":\"Multiplier\",\"bpmMultiplier\":2}]"), 500, 1000, 1250);
            Times(Parse(new[] { 0d, 999, 0 }), 500, 1500);
        }),
        ("Parser rejects unverified timing and timing repeats", () =>
        {
            foreach (var type in new[] { "Hold", "AutoPlayTiles", "FreeRoam", "UnknownEvent" })
                Equal(Parse(new[] { 0d, 0 }, $"[{{\"floor\":1,\"eventType\":\"{type}\"}}]").Status, MapParseStatus.Unsupported);
            // MultiPlanet is read now; with no count in it the game's own default of two planets changes no timing.
            Equal(Parse(new[] { 0d, 0 }, "[{\"floor\":1,\"eventType\":\"MultiPlanet\"}]").Status, MapParseStatus.Supported);
            Equal(Parse(new[] { 0d, 0 }, "[{\"floor\":1,\"eventType\":\"SetSpeed\",\"angleOffset\":90,\"beatsPerMinute\":180}]").Status, MapParseStatus.Unsupported);
            Equal(Parse(new[] { 0d, 0 }, "[{\"floor\":1,\"eventType\":\"Twirl\",\"eventTag\":\"x\"},{\"floor\":1,\"eventType\":\"RepeatEvents\",\"tag\":\"x\"}]").Status, MapParseStatus.Unsupported);
            Equal(Parse(new[] { 0d, 0 }, "[{\"floor\":1,\"eventType\":\"MoveTrack\",\"eventTag\":\"x\"},{\"floor\":1,\"eventType\":\"RepeatEvents\",\"tag\":\"x\"}]").Status, MapParseStatus.Supported);
        }),
        ("Plum R real read-only sample parses and prepares", () =>
        {
            var root = WorkspaceMapCache.FindRepositoryRoot(AppContext.BaseDirectory);
            var path = Path.Combine(root, ".example", "main.adofai");
            var before = SHA256.HashData(File.ReadAllBytes(path));
            Equal(Convert.ToHexString(before), "E7FB46B01C535DE9918EAF7A2121C0059C6891322F103D225693984DDB8A2D3A");
            var stamp = File.GetLastWriteTimeUtc(path);
            var raw = new ReadOnlyDirectoryMapSource(Path.GetDirectoryName(path)!).ReadAsync("main.adofai").GetAwaiter().GetResult();
            var result = new AdofaiTimelineParser().Parse(raw);
            Equal(result.Status, MapParseStatus.Supported); Equal(result.SongName, "Plum - R");
            Equal(result.Timeline!.HitTimesMs.Count, 726);
            Assert(result.Timeline.CheckpointsVerified, "Checkpoint rules must be linked");
            Equal(result.Timeline.Checkpoints.Count, 7);
            // User report: entering a 4K run after a completed 2K cycle ("5 4") must not open one
            // more 2K cycle before the 4K pattern. Checked on runs without chords, with default settings.
            // The held tile count would hide the transition this pins, so the rule is checked on its own.
            var defaults = new VisualizerSettings { MagicCircleFixedTiles = false };
            var hits = result.Timeline.HitTimesMs.Select(t => t - result.Timeline.HitTimesMs[0]).ToArray();
            var plumNotes = new NoteGenerator().Generate(hits, defaults);
            var selector = new DynamicTimingSelector(defaults);
            var transitions = 0;
            for (var i = 2; i + 1 < hits.Length; i++)
            {
                if (Enumerable.Range(i - 1, 3).Any(k => hits[k] - hits[k - 1] <= defaults.SimultaneousWindowMs)) continue;
                if (plumNotes[i - 1].Mode != 2 || plumNotes[i - 1].Tile != 4 || plumNotes[i - 2].Tile != 5) continue;
                if (selector.Select(hits[i] - hits[i - 1]) != 2 || selector.Select(hits[i + 1] - hits[i]) != 4) continue;
                Equal(plumNotes[i].Mode, 4); Equal(plumNotes[i].Tile, 6);
                transitions++;
            }
            Assert(transitions > 0, "Plum - R contains 2K -> 4K entries after a completed 2K cycle");
            Console.WriteLine($"Plum R: {transitions} 2K -> 4K entries start the 4K pattern on the first close hit.");
            var main = new MainWindow();
            var model = (MainViewModel)main.DataContext;
            model.SelectedMap = new("plum-r-test", Path.GetDirectoryName(path)!, "main.adofai", result.SongName!, result.Status,
                result.Timeline, result.Warnings, Convert.ToHexString(before), new AdofaiTimelineParser().Version);
            model.LoadSelectedSong(); // Loading also switches the visualizer on.
            Equal(model.Playback.State, PlaybackState.Waiting); Equal(model.Playback.Notes[0].HitTimeMs, 0d);
            Capture(main, Path.Combine(artifactRoot, "plum-r-window.png"), 1100, 780); main.Close();
            Assert(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Read-only sample changed");
            Equal(File.GetLastWriteTimeUtc(path), stamp);
            Console.WriteLine($"Plum R: {result.Timeline.HitTimesMs.Count} hits; first={result.Timeline.HitTimesMs[0]:F6}ms; last={result.Timeline.HitTimesMs[^1]:F6}ms.");
        }),
        ("Focused button keyboard activation is blocked without consuming input", () =>
        {
            using var source = new HwndSource(new HwndSourceParameters("Button input test") { WindowStyle = 0, ParentWindow = new nint(-3) });
            var ordinary = new OrdinaryButton(); var ordinaryClicks = 0;
            ordinary.Click += (_, _) => ordinaryClicks++;
            ordinary.Down(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent, Source = ordinary });
            Equal(ordinaryClicks, 1); // Reproduces the independent WPF activation path.
            var guarded = new GuardedButton(); var guardedClicks = 0;
            guarded.Click += (_, _) => guardedClicks++;
            foreach (var key in new[] { Key.Enter, Key.Space })
            {
                var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent, Source = guarded };
                guarded.Down(down); guarded.Up(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyUpEvent, Source = guarded });
                Assert(!down.Handled, "Do not consume the key");
            }
            Equal(guardedClicks, 0);
            var remapped = ControlBindings.Create(new() { ResetKey = "R" });
            Assert(!remapped.ContainsKey(KeyInterop.VirtualKeyFromKey(Key.Enter)), "Old Reset key must not remain bound");
            Equal(remapped[KeyInterop.VirtualKeyFromKey(Key.R)], Visualizer.Application.Input.InputAction.Reset);
        }),
        ("Korean and English setting labels, language changes keep playback", () =>
        {
            Equal(new VisualizerSettings().Language, Languages.Korean);
            Equal(UiText.Get(Languages.Korean, "Reset Key"), "재설정 키");
            Equal(UiText.Get(Languages.English, "Reset Key"), "Reset key");
            // The window name follows the language.
            Equal(UiText.Get(Languages.Korean, "setting"), "설정");
            Equal(UiText.Get(Languages.English, "setting"), "setting");
            var model = new MainViewModel(); model.LoadPreview(); model.Start(); var notes = model.Playback.Notes;
            model.ApplySettings(model.Settings with { Language = Languages.English });
            Equal(model.Playback.State, PlaybackState.Playing); Assert(ReferenceEquals(notes, model.Playback.Notes), "Language must not reset notes");
            foreach (var language in new[] { Languages.Korean, Languages.English })
            {
                var window = new SettingsPanel(new() { Language = language });
                Capture(window, Path.Combine(artifactRoot, $"setting-{language}.png"), 720, 1000);
            }
        })
    };
}
