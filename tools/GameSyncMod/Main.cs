using System.Diagnostics;
using UnityModManagerNet;

namespace AdofaiVisualizerSync
{
    /// <summary>
    /// Unity Mod Manager entry point. The mod reads the game's own playback state and sends it to the visualizer; it writes
    /// nothing back, synthesises no input and patches no game method (AGENTS.md rule 6, docs/game-sync-plan.md).
    /// </summary>
    public static class Main
    {
        private static readonly GameReader Reader = new GameReader();
        private static readonly KeyViewerFocus KeyViewer = new KeyViewerFocus();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static SyncWriter? _writer;
        private static bool _toldProblem;
        private static string? _sentLevel;
        private static string? _tryingLevel;
        private static int _tries;

        /// <summary>How many frames to keep asking for a level's tile times before giving up and saying why.</summary>
        private const int LevelTryLimit = 600;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            modEntry.OnToggle = OnToggle;
            modEntry.OnUpdate = OnUpdate;
            modEntry.OnUnload = OnUnload;
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool active)
        {
            if (active) Started(modEntry);
            else
            {
                _writer?.Stop();
                _writer = null;
                KeyViewer.Release();
            }
            return true;
        }

        private static bool OnUnload(UnityModManager.ModEntry modEntry)
        {
            _writer?.Stop();
            _writer = null;
            KeyViewer.Release();
            return true;
        }

        /// <summary>Makes sure the writer is running, and hands the same one back on later calls.</summary>
        private static SyncWriter Started(UnityModManager.ModEntry modEntry)
        {
            if (_writer is { } running) return running;
            var writer = new SyncWriter(message => modEntry.Logger.Log(message));
            writer.Start();
            _writer = writer;
            return writer;
        }

        /// <summary>
        /// Runs on the game thread every frame. It only reads values and hands them over, so the frame cost is a few
        /// reflection reads and an uncontended lock; the pipe is written on the writer's own thread.
        /// </summary>
        private static void OnUpdate(UnityModManager.ModEntry modEntry, float deltaTime)
        {
            // Independent of the sync: it follows the window, not the song, so a game update that moves a field and
            // stops the reading below leaves this working.
            KeyViewer.Follow();
            // The manager only calls this while the mod is on, so no writer here just means OnToggle did not run.
            var writer = Started(modEntry);
            Reader.Resolve();
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
            // Once a level: the game's own tile times settle the offset without watching the player move. A menu forgets
            // which level was sent, so going back into the same one reads it again.
            if (!sample.Valid) { _sentLevel = null; _tryingLevel = null; }
            else if (sample.Path != _sentLevel)
            {
                // The level path is set before the tiles are built, so this keeps asking instead of losing them on the
                // first frame. Giving up is said out loud rather than leaving the visualizer waiting for nothing.
                if (sample.Path != _tryingLevel) { _tryingLevel = sample.Path; _tries = 0; }
                if (Reader.ReadLevelTimes() is { } times)
                {
                    _sentLevel = sample.Path;
                    writer.PostLevel(sample.Path, times);
                    modEntry.Logger.Log("Sent the times of " + times.Length + " tiles.");
                }
                else if (++_tries >= LevelTryLimit)
                {
                    _sentLevel = sample.Path;
                    modEntry.Logger.Log("Could not read the tile times: " + (Reader.Problem ?? "the level has no tiles yet."));
                }
            }
            writer.Post(sample);
        }
    }
}
