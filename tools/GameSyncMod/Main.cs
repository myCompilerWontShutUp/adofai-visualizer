using System;
using System.Diagnostics;
using UnityModManagerNet;

namespace AdofaiVisualizerSync
{
    /// <summary>
    /// Unity Mod Manager entry point. The mod reads the game's own playback state and sends it to the visualizer; it writes
    /// no game field and synthesises no input (AGENTS.md rule 6, docs/game-sync-plan.md). Its one change to the game is the
    /// judgment ease the user asked for (<see cref="JudgmentEase"/>), which is 1, the game's own judgment, unless set, the
    /// error meter's ticks put in the zones of their grades (<see cref="ErrorMeterTicks"/>, display only), and the key
    /// limiter answered with the visualizer's keys while it runs (<see cref="KeyLimiter"/>, never saved).
    /// </summary>
    public static class Main
    {
        private static readonly GameReader Reader = new GameReader();
        private static readonly KeyViewerFocus KeyViewer = new KeyViewerFocus();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static SyncWriter? _writer;
        private static bool _toldProblem;
        private static bool _toldTrouble;
        private static bool _toldKeyViewer;
        private static string? _lastError;
        private static string? _sentLevel;
        private static string? _tryingLevel;
        private static int _tries;
        private static int _confirmIn;

        /// <summary>How many frames to keep asking for a level's tile times before giving up and saying why.</summary>
        private const int LevelTryLimit = 600;
        /// <summary>
        /// How many frames after sending a level's tile times to read them once more. A level still being built may give
        /// times that are not final yet; one more look a second later sends the real ones if they differ.
        /// </summary>
        private const int ConfirmAfter = 60;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            modEntry.OnToggle = OnToggle;
            modEntry.OnUpdate = OnUpdate;
            modEntry.OnLateUpdate = OnLateUpdate;
            modEntry.OnUnload = OnUnload;
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool active)
        {
            if (active)
            {
                Started();
                JudgmentEase.Start(line => modEntry.Logger.Log(line));
                ErrorMeterTicks.Start(line => modEntry.Logger.Log(line));
                KeyLimiter.Start(line => modEntry.Logger.Log(line));
            }
            else
            {
                _writer?.Stop();
                _writer = null;
                KeyViewer.Release();
                JudgmentEase.Stop();
                ErrorMeterTicks.Stop();
                KeyLimiter.Stop();
            }
            return true;
        }

        private static bool OnUnload(UnityModManager.ModEntry modEntry)
        {
            _writer?.Stop();
            _writer = null;
            KeyViewer.Release();
            JudgmentEase.Stop();
            ErrorMeterTicks.Stop();
            KeyLimiter.Stop();
            return true;
        }

        /// <summary>Makes sure the writer is running, and hands the same one back on later calls.</summary>
        private static SyncWriter Started()
        {
            if (_writer is { } running) return running;
            var writer = new SyncWriter();
            writer.Start();
            _writer = writer;
            return writer;
        }

        /// <summary>
        /// Runs on the game thread every frame, like <see cref="OnLateUpdate"/>. Nothing either does may throw into the game:
        /// the manager would log every frame of it, which is a stutter of its own.
        /// </summary>
        private static void OnUpdate(UnityModManager.ModEntry modEntry, float deltaTime)
        {
            try
            {
                // Independent of the sync: it follows the window, not the song, so a game update that moves a field and
                // stops the reading below leaves this working.
                KeyViewer.Follow();
                KeyLimiter.Frame();
            }
            catch (Exception error)
            {
                if (_toldKeyViewer) return;
                _toldKeyViewer = true;
                modEntry.Logger.Log("The key viewer could not follow the window: " + error.Message);
            }
        }

        /// <summary>
        /// The song position is read after every Update of the frame. scrConductor.Update sets it once a frame, and in
        /// Update this mod may well run before it and send the frame before's: one whole frame late, which grows where a
        /// heavy level drops the frame rate. A failure costs this frame's reading and is logged once until it changes.
        /// </summary>
        private static void OnLateUpdate(UnityModManager.ModEntry modEntry, float deltaTime)
        {
            try
            {
                Frame(modEntry);
                _lastError = null;
            }
            catch (Exception error)
            {
                Tell(modEntry, "A frame went unread: " + error.Message);
            }
        }

        /// <summary>Logs a failure once, not once a frame while it lasts.</summary>
        private static void Tell(UnityModManager.ModEntry modEntry, string what)
        {
            if (what == _lastError) return;
            _lastError = what;
            modEntry.Logger.Log(what);
        }

        /// <summary>
        /// Only reads values and hands them over, so the frame cost is a few reflection reads and an uncontended lock; the
        /// pipe is written on the writer's own thread.
        /// </summary>
        private static void Frame(UnityModManager.ModEntry modEntry)
        {
            // The manager only calls this while the mod is on, so no writer here just means OnToggle did not run.
            var writer = Started();
            foreach (var line in writer.TakeLog()) modEntry.Logger.Log(line);
            Reader.Resolve();
            writer.NameJudgments(Reader.JudgmentNames);
            if (Reader.Problem != null)
            {
                if (!_toldProblem)
                {
                    _toldProblem = true;
                    modEntry.Logger.Log("Not sending anything: " + Reader.Problem);
                }
                return;
            }
            var sample = Reader.Read(Clock.Elapsed.TotalMilliseconds);
            sample.Ease = JudgmentEase.Patched ? JudgmentEase.Scale : -1;
            if (sample.Failed)
            {
                // The game threw this frame; the next one tries again. Said once per run of failures.
                if (!_toldTrouble)
                {
                    _toldTrouble = true;
                    modEntry.Logger.Log("Could not read the game this frame, trying again: " + Reader.Trouble);
                }
                return;
            }
            if (_toldTrouble)
            {
                _toldTrouble = false;
                modEntry.Logger.Log("Reading the game again.");
            }
            // Once a level: the game's own tile times settle the offset without watching the player move. A menu forgets
            // which level was sent, so going back into the same one reads it again.
            if (!sample.Valid) { _sentLevel = null; _tryingLevel = null; _confirmIn = 0; }
            else if (sample.Path != _sentLevel)
            {
                // The level path is set before the tiles are built, so this keeps asking instead of losing them on the
                // first frame. Giving up is said out loud rather than leaving the visualizer waiting for nothing.
                if (sample.Path != _tryingLevel) { _tryingLevel = sample.Path; _tries = 0; }
                if (Reader.ReadLevelTimes() is { } times)
                {
                    _sentLevel = sample.Path;
                    _confirmIn = ConfirmAfter;
                    writer.PostLevel(sample.Path, times);
                    modEntry.Logger.Log("Sent the times of " + times.Length + " tiles.");
                }
                else if (++_tries >= LevelTryLimit)
                {
                    _sentLevel = sample.Path;
                    modEntry.Logger.Log("Could not read the tile times: " + (Reader.Trouble ?? "the level has no tiles yet."));
                }
            }
            else if (_confirmIn > 0 && --_confirmIn == 0 && Reader.ReadLevelTimes() is { } again && writer.LevelDiffers(sample.Path, again))
            {
                writer.PostLevel(sample.Path, again);
                modEntry.Logger.Log("The tile times changed after the level loaded; sent the times of " + again.Length + " tiles again.");
            }
            writer.Post(sample);
        }
    }
}
