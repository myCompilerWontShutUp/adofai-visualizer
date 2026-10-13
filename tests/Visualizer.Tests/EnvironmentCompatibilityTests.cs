using System.IO;
using Visualizer.App.ViewModels;
using Visualizer.App.Views;
using Visualizer.Application.Cache;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Localization;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using Visualizer.Infrastructure.Parsing;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

internal static class EnvironmentCompatibilityTests
{
    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }
    private static MapParseResult Parse(string extra = "", int version = 18) => new AdofaiTimelineParser().Parse(
        new("fixture.adofai", $"{{\"settings\":{{\"version\":{version},\"bpm\":120{extra}}},\"angleData\":[0,0,0],\"actions\":[]}}"));
    public static IEnumerable<(string Name, Action Run)> Create(string artifacts)
    {
        yield return ("Environment settings defaults, validation and rule identity", () =>
        {
            var settings = new VisualizerSettings();
            Equal(settings.StartDelayMs, 0d); Equal(settings.PlaybackSpeedPercent, 100d);
            foreach (var value in new[] { -1d, double.NaN, double.PositiveInfinity })
                Throws<ArgumentException>(() => (settings with { StartDelayMs = value }).EnsureValid());
            foreach (var value in new[] { 0d, -1, 0.99, 1000.01, double.NaN, double.PositiveInfinity })
                Throws<ArgumentException>(() => (settings with { PlaybackSpeedPercent = value }).EnsureValid());
            foreach (var value in new[] { 1d, 75, 1000 }) (settings with { PlaybackSpeedPercent = value }).EnsureValid();
            Assert(!settings.HasSameNoteRulesAs(settings with { StartDelayMs = 100 }), "Delay change must reset");
            Assert(!settings.HasSameNoteRulesAs(settings with { PlaybackSpeedPercent = 50 }), "Speed change must reset");
        });
        yield return ("Start delay and speed scale playback without touching source", () =>
        {
            var clock = new Clock(); var playback = new PlaybackSession(clock);
            var settings = new VisualizerSettings { PlaybackSpeedPercent = 50, StartDelayMs = 1500 };
            var timeline = new ParsedTimeline(new[] { 500d, 1000, 4500 });
            playback.LoadParsedTimeline(timeline, settings); playback.Toggle();
            Assert(playback.Notes.Select(n => n.HitTimeMs).SequenceEqual(new[] { 1500d, 2500, 9500 }), "Scale intervals before common delay");
            Assert(timeline.HitTimesMs.SequenceEqual(new[] { 500d, 1000, 4500 }), "Never change cached source");
            var prepared = playback.Notes;
            playback.Start(); clock.NowMs = 1000;
            Equal(playback.StartWaitRemainingMs, 500d); Equal(playback.NextHitRemainingMs, (double?)500);
            clock.NowMs = 2500;
            Equal(playback.StartWaitRemainingMs, 0d); Equal(playback.NextHitRemainingMs, (double?)7000);
            clock.NowMs = 9501; Equal(playback.NextHitRemainingMs, null);
            Assert(ReferenceEquals(prepared, playback.Notes), "Start and long rests never replan");
            playback.Reset(settings); Assert(prepared.SequenceEqual(playback.Notes), "Reset repeats delay once");
        });
        yield return ("Scaled real-time intervals govern chords and dynamic modes", () =>
        {
            var playback = new PlaybackSession(new Clock());
            // The chord window is pinned so editing the initial settings does not change this check.
            playback.LoadTimeline(new[] { 0d, 15 }, new() { SimultaneousWindowMs = 10 });
            Equal(playback.Notes[1].HitTimeMs, 15d);
            playback.ApplySettings(new() { SimultaneousWindowMs = 10, PlaybackSpeedPercent = 200, StartDelayMs = 500 });
            Equal(playback.Notes[0].HitTimeMs, 500d); Equal(playback.Notes[1].HitTimeMs, 500d);
            // The 4K boundary is pinned so editing DefaultSettings.jsonc does not change this check.
            // At 200% the 60ms gap is 30ms, a trill: counted per note here, so the gap itself decides.
            playback.LoadTimeline(new[] { 0d, 60 }, new() { FourKeyBoundaryMs = 50, TwoKeyBoundaryMs = 500, TrillAsOneNote = false });
            Equal(playback.Notes[0].Mode, 2);
            playback.ApplySettings(new() { FourKeyBoundaryMs = 50, TwoKeyBoundaryMs = 500, PlaybackSpeedPercent = 200, TrillAsOneNote = false });
            Equal(playback.Notes[0].Mode, 4);
            Throws<NotePlacementException>(() => playback.LoadTimeline(new[] { 0d, double.MaxValue }, new() { PlaybackSpeedPercent = 1 }));
            Equal(playback.Notes.Count, 0); Assert(!playback.Start(), "Overflow cannot leave playable stale notes");
        });
        yield return ("Pause resumes the same clock including a partially elapsed start delay", () =>
        {
            var clock = new Clock(); var playback = new PlaybackSession(clock);
            var settings = new VisualizerSettings { StartDelayMs = 2000 };
            playback.LoadTimeline(new[] { 0d, 500 }, settings); playback.Toggle();
            Assert(!playback.Pause(), "Cannot pause Waiting");
            playback.Start(); clock.NowMs = 750; Assert(playback.Pause(), "Pause Playing");
            var notes = playback.Notes;
            clock.NowMs = 60000;
            Equal(playback.TimelineTimeMs, 750d); Equal(playback.StartWaitRemainingMs, 1250d);
            Assert(!playback.Pause(), "Repeated pause cannot change anchor");
            playback.ApplySettings(settings with { GlobalDisplayOffsetMs = 10 });
            Equal(playback.State, PlaybackState.Paused);
            Assert(playback.Start(), "Start resumes Paused");
            Equal(playback.TimelineTimeMs, 750d); Assert(ReferenceEquals(notes, playback.Notes), "Resume cannot move prepared notes");
            clock.NowMs += 1250; Equal(playback.TimelineTimeMs, 2000d);
            playback.Pause(); playback.Reset(settings); Equal(playback.TimelineTimeMs, 0d);
            playback.Start(); Equal(playback.TimelineTimeMs, 0d);
            playback.Pause(); playback.Toggle(); Equal(playback.State, PlaybackState.Off);
            playback.Toggle(); Equal(playback.State, PlaybackState.Waiting);
        });
        yield return ("Checkpoint activation follows delay and paused time; practice speed turns it off", () =>
        {
            var clock = new Clock(); var playback = new PlaybackSession(clock);
            var settings = new VisualizerSettings { StartDelayMs = 1000 };
            var timeline = new ParsedTimeline(new[] { 500d, 1500, 2500 },
                new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 2, 1500) }, true);
            playback.LoadParsedTimeline(timeline, settings); playback.Toggle(); playback.Start();
            // The event tile is displayed at 2000 (1000ms start delay); unpressed, it is taken when its press window ends.
            clock.NowMs = 2149; Equal(playback.ActiveCheckpoint, (int?)1);
            clock.NowMs = 2150; Equal(playback.ActiveCheckpoint, (int?)2);
            playback.Pause(); clock.NowMs = 100000; Equal(playback.ActiveCheckpoint, (int?)2);
            playback.Reset(settings, 2); Equal(playback.Notes[0].HitTimeMs, 1000d);
            Equal(playback.ActiveCheckpoint, (int?)2);
            // At a practice speed the game disables checkpoints, so automatic progress stays on the start.
            var fast = settings with { PlaybackSpeedPercent = 200 };
            playback.Reset(fast, 1); playback.Start(); clock.NowMs += 100000;
            Equal(playback.ActiveCheckpoint, (int?)1);
            Equal(playback.Notes[1].HitTimeMs, 1500d); // Speed still scales the prepared notes.
        });
        yield return ("Version and countdown diagnostics never guess timing", () =>
        {
            foreach (var flag in new[] { "true", "false", "\"Enabled\"", "\"Disabled\"" })
            {
                var parsed = Parse($",\"offset\":-123.5,\"countdownTicks\":8,\"separateCountdownTime\":{flag}");
                Equal(parsed.Status, MapParseStatus.Supported);
                Assert(parsed.Timeline!.HitTimesMs.SequenceEqual(new[] { 500d, 1000, 1500 }), "Start metadata cannot alter note intervals");
                Assert(parsed.Warnings.Any(w => w.Contains("version 18") && w.Contains("offset=-123.5ms") && w.Contains("countdownTicks=8")), "Source metadata needs diagnostics");
            }
            foreach (var invalid in new[] { ",\"offset\":1e400", ",\"countdownTicks\":-1", ",\"countdownTicks\":1.5", ",\"separateCountdownTime\":\"unknown\"" })
                Equal(Parse(invalid).Status, MapParseStatus.Error);
            var future = Parse(version: 99);
            Equal(future.Status, MapParseStatus.Unsupported);
            Assert(future.Warnings[0].Contains("99"), "Report actual unknown format version");
            // The level's pitch plays the song faster or slower, so every real time is divided by it
            // (scrConductor.SetupConductorWithLevelData sets song.pitch = pitch * 0.01, and CalculateFloorEntryTimes keeps
            // entryTimePitchAdj = entryTime / song.pitch). cardboard box - He He He's "low effect" is pitch 200.
            var fast = Parse(",\"pitch\":200");
            Assert(fast.Status == MapParseStatus.Supported, string.Join(" ", fast.Warnings));
            Assert(fast.Timeline!.HitTimesMs.Zip(new[] { 250d, 500, 750 }).All(p => Math.Abs(p.First - p.Second) < 1e-9),
                "Pitch 200 halves every time: " + string.Join(" ", fast.Timeline.HitTimesMs));
            var slow = Parse(",\"pitch\":50");
            Assert(slow.Status == MapParseStatus.Supported && slow.Timeline!.HitTimesMs[0] == 1000, "Pitch 50 doubles them");
            // get_pitch unboxes an int, and a pitch of zero or less would stop the song, so neither is read.
            foreach (var bad in new[] { ",\"pitch\":0", ",\"pitch\":-100", ",\"pitch\":12.5" })
                Equal(Parse(bad).Status, MapParseStatus.Unsupported);
            Equal(Parse(",\"requiredMods\":[\"custom-mod\"]").Status, MapParseStatus.Unsupported);
        });
        yield return ("Stale cache diagnostics and localized pause UI render", () =>
        {
            foreach (var language in new[] { Languages.Korean, Languages.English })
            {
                var window = new MainWindow(); var model = (MainViewModel)window.DataContext;
                model.ApplySettings(new() { Language = language, StartDelayMs = 1000 });
                model.SelectedMap = new MapCacheEntry("old", "unused", "fixture.adofai", "Environment test",
                    MapParseStatus.Supported, new ParsedTimeline(new[] { 0d, 4000 }), Array.Empty<string>(), "test", "adofai-v3-legacy-pause");
                model.LoadSelectedSong();
                Assert(model.Message.Contains(language == Languages.Korean ? "이전 파서" : "Older parser"), "Stale diagnostics must be visible");
                model.Start(); model.PauseResume(); // Loading already switched the visualizer on.
                Equal(model.Playback.State, PlaybackState.Paused);
                Equal(model.PauseLabel, language == Languages.Korean ? "이어하기" : "Resume");
                Capture(window, Path.Combine(artifacts, $"environment-paused-{language}.png"), 400, 640);
                var setting = new SettingsPanel(model.Settings);
                Capture(setting, Path.Combine(artifacts, $"environment-setting-{language}.png"), 720, 760);
                window.Close();
            }
        });
    }
}
