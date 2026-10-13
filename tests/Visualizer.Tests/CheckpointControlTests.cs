using System.Windows.Input;
using Visualizer.App.Input;
using Visualizer.App.ViewModels;
using Visualizer.Application.Cache;
using Visualizer.Application.Input;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

internal static class CheckpointControlTests
{
    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }

    private static MapCacheEntry Entry(string id, ParsedTimeline timeline) =>
        new(id, "unused", "map.adofai", id, MapParseStatus.Supported, timeline, Array.Empty<string>(), "test", "test");
    public static IEnumerable<(string Name, Action Run)> Create()
    {
        yield return ("The song-end offer waits a quiet second, and its first button never moves", () =>
        {
            var clock = new Clock();
            var model = new MainViewModel(clock);
            // Two checkpoints: the song's start and one real one, so the second button has something to go to.
            var timeline = new ParsedTimeline(new[] { 0d, 500, 1000 },
                new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 1, 500) }, true);
            model.SelectedMap = Entry("end", timeline);
            model.LoadSelectedSong();
            model.StartFromKey();
            Assert(!model.ShowSongEndNotice, "Nothing while the song runs");
            // The last note is due at 1000ms, so after four seconds the song has been over for three quiet ones.
            clock.NowMs += 4000;
            model.RefreshPlaybackInfo();
            Assert(model.ShowSongEndNotice, "Past the last note and quiet: the offer is up");
            Assert(model.ShowLastCheckpointButton, "The song has a checkpoint and auto checkpoints are on");
            // Any input puts it away and starts the second over (user rule: one second, it was three).
            model.NoteInput();
            Assert(!model.ShowSongEndNotice, "Input takes it away");
            clock.NowMs += 900;
            model.RefreshPlaybackInfo();
            Assert(!model.ShowSongEndNotice, "Less than a second is not enough");
            clock.NowMs += 100;
            model.RefreshPlaybackInfo();
            Assert(model.ShowSongEndNotice, "A quiet second brings it back");
            // The checkpoint button follows the automatic checkpoint setting (user rule).
            model.ApplySettings(model.Settings with { AutoCheckpointAdvance = false });
            model.StartFromKey();
            clock.NowMs += 4000;
            model.RefreshPlaybackInfo();
            Assert(model.ShowSongEndNotice && !model.ShowLastCheckpointButton, "No checkpoint button without automatic checkpoints");
            // A song with no checkpoint of its own gets the first button only.
            var plain = new MainViewModel(clock);
            plain.SelectedMap = Entry("plain", new ParsedTimeline(new[] { 0d, 500 }));
            plain.LoadSelectedSong();
            plain.StartFromKey();
            clock.NowMs += 4000;
            plain.RefreshPlaybackInfo();
            Assert(plain.ShowSongEndNotice && !plain.ShowLastCheckpointButton, "Only the first button");
            // Both buttons put the song back in its waiting state, ready for the start key.
            model.ApplySettings(model.Settings with { AutoCheckpointAdvance = true });
            model.RestartAtLastCheckpoint();
            Equal(model.Checkpoint, 2);
            Equal(model.Playback.State, PlaybackState.Waiting);
            model.RestartFromBeginning();
            Equal(model.Checkpoint, 1);
            Equal(model.Playback.State, PlaybackState.Waiting);
            Assert(!model.ShowSongEndNotice, "The offer is gone once it is taken");

            // The reset key answers the offer the way its buttons do (user rule): the last checkpoint when that button
            // shows, the beginning when it does not. Without the offer it resets as it always did.
            model.StartFromKey();
            clock.NowMs += 4000;
            model.RefreshPlaybackInfo();
            Assert(model.ShowLastCheckpointButton, "The offer is up with its checkpoint button");
            var offered = model.ShowSongEndNotice;
            model.NoteInput(); // As the window does: the key is input before it is handled.
            model.ResetFromKey(offered);
            Assert(model.Checkpoint == 2 && model.Playback.State == PlaybackState.Waiting && !model.ShowSongEndNotice, "Back at the last checkpoint");
            model.ApplySettings(model.Settings with { AutoCheckpointAdvance = false });
            model.Start(); // From checkpoint 2 the first note's lane key starts it; Start is what that key calls.
            clock.NowMs += 4000;
            model.RefreshPlaybackInfo();
            offered = model.ShowSongEndNotice;
            Assert(offered && !model.ShowLastCheckpointButton, "The offer is up without its checkpoint button");
            model.NoteInput();
            model.ResetFromKey(offered);
            Assert(model.Checkpoint == 1 && model.Playback.State == PlaybackState.Waiting, "No checkpoint button: back to the beginning");
            plain.ResetFromKey(true);
            Equal(plain.Checkpoint, 1);
            Equal(plain.Playback.State, PlaybackState.Waiting);
            // The notice is in the playback notify list, or the screen would keep the last answer.
            Assert(MainViewModel.PlaybackNames.Contains(nameof(MainViewModel.ShowSongEndNotice))
                && MainViewModel.PlaybackNames.Contains(nameof(MainViewModel.ShowLastCheckpointButton)),
                "Both are raised with the playback bindings");
        });

        yield return ("Checkpoint arrows reserve keys, respect focus and ignore key repeat", () =>
        {
            var settings = new VisualizerSettings();
            var bindings = ControlBindings.Create(settings);
            // "+" is next and "-" is previous; the arrow keys stay free (ADOFAI uses Left/Right in-game).
            Equal(bindings[KeyInterop.VirtualKeyFromKey(Key.OemMinus)], InputAction.PreviousCheckpoint);
            Equal(bindings[KeyInterop.VirtualKeyFromKey(Key.OemPlus)], InputAction.NextCheckpoint);
            foreach (var free in new[] { Key.Left, Key.Right, Key.Up, Key.Down })
                Assert(!bindings.ContainsKey(KeyInterop.VirtualKeyFromKey(free)), $"{free} is not bound");
            // The defaults are "-" and "+", so those are the keys another control clashes with.
            Throws<ArgumentException>(() => (settings with { ResetKey = "+" }).EnsureValid());
            Throws<ArgumentException>(() => ControlBindings.Create(settings with { SettingsHotkey = "-" }));
            (settings with { ResetKey = "Left", SettingsHotkey = "Right" }).EnsureValid();
            // Both may be cleared; stepping then works only with the arrows beside the list.
            var cleared = settings with { PreviousCheckpointKey = null, NextCheckpointKey = null };
            cleared.EnsureValid();
            var without = ControlBindings.Create(cleared).Values.ToArray();
            Assert(!without.Contains(InputAction.NextCheckpoint) && !without.Contains(InputAction.PreviousCheckpoint),
                "Cleared keys are not bound");
            (cleared with { ResetKey = "-" }).EnsureValid();
            // Checkpoint keys are configurable; the freed arrow keys can then be used elsewhere.
            var remapped = ControlBindings.Create(settings with { PreviousCheckpointKey = "PageDown", NextCheckpointKey = "PageUp", ResetKey = "Up" });
            Equal(remapped[KeyInterop.VirtualKeyFromKey(Key.PageUp)], InputAction.NextCheckpoint);
            Equal(remapped[KeyInterop.VirtualKeyFromKey(Key.PageDown)], InputAction.PreviousCheckpoint);
            Equal(remapped[KeyInterop.VirtualKeyFromKey(Key.Up)], InputAction.Reset);
            var clash = (settings with { NextCheckpointKey = "Enter" }).ValidateFields().Select(e => e.Field).ToArray();
            Assert(clash.Contains("NextCheckpointKey") && clash.Contains("ResetKey"), "A clash names both keys: " + string.Join(", ", clash));
            Throws<ArgumentException>(() => (settings with { PreviousCheckpointKey = "Escape" }).EnsureValid());
            Throws<ArgumentException>(() => (settings with { NextCheckpointKey = " " }).EnsureValid());
            var gated = settings with { OnlyReactWhenAdofaiForeground = true }; // The global input scope is on by default.
            foreach (var action in new[] { InputAction.PreviousCheckpoint, InputAction.NextCheckpoint })
            {
                Assert(!InputPolicy.ShouldReact(action, gated, false, false), "Foreground filter applies");
                Assert(!InputPolicy.ShouldReact(action, gated, true, true), "Editing suppresses navigation");
                Assert(InputPolicy.ShouldReact(action, gated, true, false), "Game foreground enables navigation");
                Assert(InputPolicy.ShouldReact(action, settings with { OnlyReactWhenAdofaiForeground = false }, false, false),
                    "With the input scope off, navigation reacts in any app");
            }
            var tracker = new KeyPressTracker(); tracker.SetObservedKeys(bindings.Keys);
            var key = KeyInterop.VirtualKeyFromKey(Key.OemPlus);
            Assert(tracker.Observe(1, key, false), "First key down");
            Assert(!tracker.Observe(1, key, false), "Held key cannot skip many checkpoints");
            tracker.Observe(1, key, true); Assert(tracker.Observe(1, key, false), "Next press");
        });
        yield return ("Manual checkpoint navigation uses marker order and wraps around at both ends", () =>
        {
            var model = new MainViewModel();
            Assert(!model.CanMoveCheckpoint && !model.MoveCheckpoint(1), "No map: arrows greyed out");
            model.Playback.LoadParsedTimeline(new ParsedTimeline(new[] { 0d, 100 }, new[] { new TimelineCheckpoint(1, 0) }, true), model.Settings);
            Assert(!model.CanMoveCheckpoint && !model.MoveCheckpoint(-1), "A single checkpoint has nowhere to go");
            var timeline = new ParsedTimeline(new[] { 0d, 100, 200, 300 },
                new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(3, 1), new TimelineCheckpoint(5, 3) }, true);
            model.Playback.LoadParsedTimeline(timeline, model.Settings);
            Assert(model.CanMoveCheckpoint, "Several checkpoints enable the arrows");
            Assert(model.MoveCheckpoint(-1), "Previous from the first wraps to the last"); Equal(model.Checkpoint, 5);
            Assert(model.MoveCheckpoint(1), "Next from the last wraps to the first"); Equal(model.Checkpoint, 1);
            model.Toggle(); model.Start();
            Assert(model.MoveCheckpoint(1), "Next marker"); Equal(model.Checkpoint, 3);
            Equal(model.Playback.State, PlaybackState.Waiting); Equal(model.Playback.Notes[0].HitTimeMs, 0d);
            Assert(model.MoveCheckpoint(1), "Next actual number"); Equal(model.Checkpoint, 5);
            Assert(model.MoveCheckpoint(1), "Last wraps to first"); Equal(model.Checkpoint, 1);
            Assert(model.MoveCheckpoint(-1), "First wraps to last"); Equal(model.Checkpoint, 5);
            Assert(model.MoveCheckpoint(-1), "Previous marker"); Equal(model.Checkpoint, 3);
            model.Toggle(); Assert(model.MoveCheckpoint(-1), "Prepare while OFF");
            Equal(model.Playback.State, PlaybackState.Off); Equal(model.Checkpoint, 1);
        });
        yield return ("Automatic checkpoint follows a pressed note, or the end of its press window", () =>
        {
            var clock = new Clock(); var session = new PlaybackSession(clock);
            var timeline = new ParsedTimeline(new[] { 0d, 2000, 3000 },
                new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 2, 2000) }, true);
            var judge = new NotePressJudge();
            session.Presses = judge;
            session.LoadParsedTimeline(timeline, new()); session.Toggle(); session.Start();
            Equal(session.Settings.NoteTravelTimeMs, 600d); Equal(session.Settings.KeyPressWindowMs, 150d);
            // Unpressed: the checkpoint is taken when the note's press window ends.
            foreach (var time in new[] { 1000d, 2000, 2149.999 })
            { clock.NowMs = time; Equal(session.ActiveCheckpoint, (int?)1); }
            clock.NowMs = 2150; Equal(session.ActiveCheckpoint, (int?)2);
            session.Reset(session.Settings, 2); Equal(session.ActiveCheckpoint, (int?)2);
            // Pressed: the checkpoint follows that press at once, well before the window ends.
            session.Reset(session.Settings); session.Start();
            clock.NowMs += 1950; Equal(session.ActiveCheckpoint, (int?)1);
            Assert(judge.Press(session, session.Notes[1].Tile, session.Settings.KeyPressWindowMs) == 1, "The checkpoint note is pressed");
            Equal(session.ActiveCheckpoint, (int?)2);
            // Travel duration never moves that boundary; it is the displayed hit that counts.
            session.Reset(session.Settings with { NoteTravelTimeMs = 5000 }); session.Start();
            clock.NowMs += 2149; Equal(session.ActiveCheckpoint, (int?)1);
            clock.NowMs += 1; Equal(session.ActiveCheckpoint, (int?)2);
        });
        yield return ("Checkpoints follow the displayed chord, offsets and pause; practice speed turns them off", () =>
        {
            var clock = new Clock(); var session = new PlaybackSession(clock);
            var timeline = new ParsedTimeline(new[] { 0d, 2000, 2010, 4000 },
                new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 3, 2010) }, true);
            var settings = new VisualizerSettings { StartDelayMs = 500, GlobalDisplayOffsetMs = 50 };
            session.LoadParsedTimeline(timeline, settings); session.SetMapDisplayOffset(25); session.Toggle(); session.Start();
            // Displayed chord reaches at 2500 + 75, not the second raw note's 2510; the press window ends 150ms later.
            clock.NowMs = 2724.999; Equal(session.ActiveCheckpoint, (int?)1);
            session.Pause(); clock.NowMs = 20000; Equal(session.ActiveCheckpoint, (int?)1);
            session.Start(); clock.NowMs += 0.01; Equal(session.ActiveCheckpoint, (int?)2);
            // The game disables checkpoints at practice speeds, so automatic progress stays on the start.
            session.Reset(settings with { PlaybackSpeedPercent = 200 }); session.Start(); clock.NowMs += 60000;
            Assert(!session.AutoCheckpointActive, "Practice speed turns automatic progress off");
            Equal(session.ActiveCheckpoint, (int?)1);
        });
    }
}
