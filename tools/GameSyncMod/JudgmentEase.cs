using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace AdofaiVisualizerSync
{
    /// <summary>
    /// The judgment ease the user asked for, for practice (2026-11-17, AGENTS.md rule 6): the minimum times of the perfect
    /// windows multiplied by the visualizer's setting, and nothing else. scrMisc.GetMinimumTimes works out three minimums,
    /// Counted (0.091 / 0.065 / 0.04 s by difficulty), Perfect (0.03) and Pure (0.02, floored at 0.025), and stores them into
    /// its result; the ease is applied to Perfect and Pure only, where they are stored, and never past Counted.
    /// Counted is what decides whether a press takes the next tile at all (scrPlanet.SwitchChosen moves on only for a counted
    /// margin), so it stays the game's own: the first version multiplied it too, and on fast tiles the window grew longer
    /// than the gap between tiles, any stray press took the next tile, and every press after it hit a tile ahead as "early"
    /// (user report: Halation at 720 BPM, most tiles early). With Counted untouched the presses that take a tile are exactly
    /// the unmodified game's; only how a counted press is graded gets more lenient.
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
        /// <summary>How many of the two stores (Perfect, Pure) the transpiler found; both are needed.</summary>
        private static int _sites;

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
                        _sites = 0;
                        harmony.Patch(method, transpiler: new HarmonyMethod(typeof(JudgmentEase), nameof(Transpiler)));
                        if (_sites == 2) _harmony = harmony;
                        else
                        {
                            // The method is not the one this was written for: it is put back as it was.
                            harmony.UnpatchAll(HarmonyId);
                            log("Judgment ease is off: scrMisc.GetMinimumTimes does not store Counted, Perfect and Pure as expected.");
                        }
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

        /// <summary>
        /// Called by the patched method for the Perfect and the Pure minimum, with the Counted minimum it already stored:
        /// longer by the ease, never past Counted, and never shorter than the game's own.
        /// </summary>
        public static double Ease(double seconds, double counted) => Math.Max(seconds, Math.Min(seconds * _scale, counted));

        /// <summary>
        /// Before each store into the result's Perfect and Pure, the value goes through <see cref="Ease"/> with the result's
        /// Counted, which the method stores first. A method that does not store all three as doubles is left exactly as it is.
        /// </summary>
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            FieldInfo? counted = null;
            CodeInstruction? result = null;
            var countedAt = -1;
            for (var i = 0; i < list.Count && counted == null; i++)
            {
                if (list[i].opcode != OpCodes.Stfld || !(list[i].operand is FieldInfo field) || field.Name != "Counted" || field.FieldType != typeof(double)) continue;
                // The address of the result the store goes into: the nearest load of a local's address before it.
                for (var k = i - 1; k >= 0 && result == null; k--)
                    if (list[k].opcode == OpCodes.Ldloca || list[k].opcode == OpCodes.Ldloca_S) result = list[k];
                counted = field;
                countedAt = i;
            }
            if (counted == null || result == null) return list;
            var ease = AccessTools.Method(typeof(JudgmentEase), nameof(Ease));
            var sites = 0;
            for (var i = countedAt + 1; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Stfld || !(list[i].operand is FieldInfo field) || field.FieldType != typeof(double)
                    || field.DeclaringType != counted.DeclaringType || (field.Name != "Perfect" && field.Name != "Pure")) continue;
                // On the stack: the result's address and the value. The value becomes Ease(value, result.Counted).
                var load = new CodeInstruction(result.opcode, result.operand);
                load.labels.AddRange(list[i].labels);
                list[i].labels.Clear();
                list.InsertRange(i, new[] { load, new CodeInstruction(OpCodes.Ldfld, counted), new CodeInstruction(OpCodes.Call, ease) });
                i += 3;
                sites++;
            }
            _sites = sites;
            return list;
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
