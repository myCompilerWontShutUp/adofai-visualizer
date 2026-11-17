using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace AdofaiVisualizerSync
{
    /// <summary>
    /// The one change this mod makes to the game, asked for by the user for practice (2026-11-17, AGENTS.md rule 6): the
    /// minimum times of the hit windows multiplied by the visualizer's judgment ease. scrMisc.GetMinimumTimes builds the
    /// counted, perfect and pure minimums from time constants only (0.091 / 0.065 / 0.04, 0.03, 0.02, the mobile ones and
    /// the 0.025 floor, each divided by the speed trial), so multiplying every constant multiplies every result, and both
    /// the grading (scrMisc.GetAdjustedAngleBoundaryInDeg / GetAdjustedTimeBoundaries) and the game's error meter follow.
    /// A transpiler does it, so no game type is referenced here and a game that moved the method only loses this feature.
    /// The ease is read from the visualizer's store and applies only while the visualizer runs; 1 is the game's own judgment.
    /// </summary>
    internal static class JudgmentEase
    {
        private const string HarmonyId = "AdofaiVisualizerSync.JudgmentEase";
        private const double MaximumEase = 3;
        private static volatile float _scale = 1;
        private static Harmony? _harmony;
        private static Timer? _timer;

        /// <summary>The ease in force now; 1 when off, not patched or the visualizer is not running.</summary>
        public static float Scale => _scale;
        /// <summary>Whether the game's method is patched, so the visualizer can be told what the game really grades with.</summary>
        public static bool Patched => _harmony != null;

        /// <summary>Patches the game's method once and starts looking at the visualizer's setting. Never throws.</summary>
        public static void Start(Action<string> log)
        {
            if (_harmony == null)
            {
                try
                {
                    var misc = AccessTools.TypeByName("scrMisc");
                    var method = misc == null ? null : AccessTools.Method(misc, "GetMinimumTimes");
                    if (method == null) log("Judgment ease is off: the game has no scrMisc.GetMinimumTimes.");
                    else
                    {
                        var harmony = new Harmony(HarmonyId);
                        harmony.Patch(method, transpiler: new HarmonyMethod(typeof(JudgmentEase), nameof(Transpiler)));
                        _harmony = harmony;
                    }
                }
                catch (Exception error)
                {
                    log("Judgment ease is off: " + error.Message);
                }
            }
            if (_timer == null) _timer = new Timer(_ => Refresh(), null, 0, 1000);
        }

        /// <summary>Puts the game's own judgment back: the scale to 1 and the patch removed. Never throws.</summary>
        public static void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            _scale = 1;
            // Only this mod's own patch: UnpatchAll without an id would take every mod's patches off the game.
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch (Exception) { }
            _harmony = null;
        }

        /// <summary>Called by the patched method for every time constant in it.</summary>
        public static float Ease(float seconds) => seconds * _scale;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var ease = AccessTools.Method(typeof(JudgmentEase), nameof(Ease));
            foreach (var instruction in instructions)
            {
                yield return instruction;
                // Every float the method loads is one of its times; the labels stay on the load, so branches are untouched.
                if (instruction.opcode == OpCodes.Ldc_R4) yield return new CodeInstruction(OpCodes.Call, ease);
            }
        }

        /// <summary>
        /// Once a second, off the game thread: the visualizer's setting, and only while the visualizer runs, so a closed or
        /// crashed visualizer leaves the game's own judgment.
        /// </summary>
        private static void Refresh()
        {
            try
            {
                if (_harmony == null) { _scale = 1; return; }
                _scale = VisualizerRuns() ? (float)Read() : 1;
            }
            catch (Exception) { _scale = 1; }
        }

        private static bool VisualizerRuns()
        {
            var found = Process.GetProcessesByName("Visualizer.App");
            try { return found.Length > 0; }
            finally { foreach (var process in found) process.Dispose(); }
        }

        /// <summary>The ease the visualizer wrote; anything it cannot read as one between 1 and 3 is 1.</summary>
        private static double Read()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdofaiVisualizer",
                "judgment-ease.txt");
            if (!File.Exists(path)) return 1;
            var text = File.ReadAllText(path).Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && !double.IsNaN(value) && value >= 1 && value <= MaximumEase ? value : 1;
        }
    }
}
