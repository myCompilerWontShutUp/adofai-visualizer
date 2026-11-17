using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace AdofaiVisualizerSync
{
    /// <summary>
    /// The game's key limiter ("only these keys count as input") takes the visualizer's keys while the visualizer runs (user
    /// request 2026-11-17, AGENTS.md rule 6): the lane keys of the tiles it shows and its reset key, nothing else, so the game
    /// counts what the visualizer draws. The game checks each press against Persistence.keyLimiterKeys' unityKeysCache and
    /// asyncKeysCache (RDInputType_Keyboard.MainIgnoreActive and RDInputType_AsyncKeyboard.Main, read from the IL) and only
    /// while it is not empty; a postfix answers those two getters, for that list only, with the visualizer's keys. The keys
    /// the game saves (unityKeys and asyncKeys, kept in its settings) are never touched, so none of this is saved, and the
    /// game's own list is back within a second of the visualizer closing. Async keys are the game's own native codes, from
    /// SkyHook's mapper, worked out on the game thread.
    /// </summary>
    internal static class KeyLimiter
    {
        private const string HarmonyId = "AdofaiVisualizerSync.KeyLimiter";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static Harmony? _harmony;
        private static Timer? _timer;
        private static FieldInfo? _limiterField;
        private static FieldInfo? _labelMap;
        private static MethodInfo? _toNative;
        private static Action<string>? _log;
        /// <summary>The game's own key limiter list, the one object whose caches are answered for.</summary>
        private static object? _limiter;
        /// <summary>The key names the visualizer wrote, read off the game thread once a second; null while it does not run.</summary>
        private static volatile string? _wanted;
        /// <summary>The names the keys below were built from, on the game thread.</summary>
        private static string? _built = "";
        private static volatile HashSet<KeyCode>? _unityKeys;
        private static volatile HashSet<ushort>? _asyncKeys;

        /// <summary>Whether the visualizer's keys are in force now.</summary>
        public static bool Active => _unityKeys != null;

        /// <summary>Patches the two getters once and starts looking at the visualizer's keys. Never throws.</summary>
        public static void Start(Action<string> log)
        {
            _log = log;
            if (_harmony == null)
            {
                try
                {
                    _limiterField = AccessTools.TypeByName("Persistence")?.GetField("keyLimiterKeys", Any);
                    var setting = AccessTools.TypeByName("KeysSetting");
                    var unity = setting == null ? null : AccessTools.PropertyGetter(setting, "unityKeysCache");
                    var async = setting == null ? null : AccessTools.PropertyGetter(setting, "asyncKeysCache");
                    var mapper = AccessTools.TypeByName("SkyHook.SkyHookKeyMapper");
                    _labelMap = mapper?.GetField("UnityKeyToAsyncKeyMap", Any);
                    _toNative = mapper == null ? null : AccessTools.Method(mapper, "KeyLabelToNativeKeyCode");
                    if (_limiterField == null || unity == null || async == null || _labelMap == null || _toNative == null
                        || unity.ReturnType != typeof(HashSet<KeyCode>) || async.ReturnType != typeof(HashSet<ushort>))
                    {
                        log("The key limiter stays the game's own: a member it needs was not found.");
                    }
                    else
                    {
                        var harmony = new Harmony(HarmonyId);
                        harmony.Patch(unity, postfix: new HarmonyMethod(typeof(KeyLimiter), nameof(UnityPostfix)));
                        harmony.Patch(async, postfix: new HarmonyMethod(typeof(KeyLimiter), nameof(AsyncPostfix)));
                        _harmony = harmony;
                    }
                }
                catch (Exception error)
                {
                    log("The key limiter stays the game's own: " + error.Message);
                }
            }
            if (_harmony != null && _timer == null) _timer = new Timer(_ => Refresh(), null, 0, 1000);
        }

        /// <summary>Puts the game's own list back and takes the patch off, only this one. Never throws.</summary>
        public static void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            _unityKeys = null;
            _asyncKeys = null;
            _wanted = null;
            _built = "";
            // UnpatchAll without an id would take every mod's patches off the game.
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch (Exception) { }
            _harmony = null;
        }

        /// <summary>
        /// On the game thread every frame: builds the keys once the visualizer's list has changed. SkyHook's native mapping is
        /// called here rather than on the timer's thread. A key this game or SkyHook does not know leaves the game's own list
        /// in force: a lane key missing from the limiter would never count.
        /// </summary>
        public static void Frame()
        {
            if (_harmony == null) return;
            var wanted = _wanted;
            if (wanted == _built) return;
            _built = wanted;
            try { Build(wanted); }
            catch (Exception error)
            {
                // Never into the game: the game's own list stays, and this list is not tried again until it changes.
                _log?.Invoke("The key limiter stays the game's own: " + error.Message);
                Off();
            }
        }

        private static void Build(string? wanted)
        {
            if (string.IsNullOrEmpty(wanted))
            {
                if (Active) _log?.Invoke("The key limiter is the game's own again.");
                Off();
                return;
            }
            _limiter ??= _limiterField!.GetValue(null);
            var map = _labelMap!.GetValue(null) as IDictionary;
            var unity = new HashSet<KeyCode>();
            var async = new HashSet<ushort>();
            foreach (var name in wanted!.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Enum.IsDefined(typeof(KeyCode), name) || map == null) { Refuse(name); return; }
                var code = (KeyCode)Enum.Parse(typeof(KeyCode), name);
                if (!map.Contains(code) || map[code] is not { } label || label.ToString() == "Unknown") { Refuse(name); return; }
                var native = Convert.ToUInt16(_toNative!.Invoke(null, new[] { label }));
                if (native is 0 or ushort.MaxValue) { Refuse(name); return; }
                unity.Add(code);
                async.Add(native);
            }
            if (unity.Count == 0 || _limiter == null) { Off(); return; }
            _unityKeys = unity;
            _asyncKeys = async;
            _log?.Invoke("The key limiter takes the visualizer's keys: " + wanted);
        }

        private static void Refuse(string name)
        {
            _log?.Invoke("The key limiter stays the game's own: no key " + name + " in this game.");
            Off();
        }

        private static void Off()
        {
            _unityKeys = null;
            _asyncKeys = null;
        }

        private static void UnityPostfix(object __instance, ref HashSet<KeyCode> __result)
        {
            var keys = _unityKeys;
            if (keys != null && ReferenceEquals(__instance, _limiter)) __result = keys;
        }

        private static void AsyncPostfix(object __instance, ref HashSet<ushort> __result)
        {
            var keys = _asyncKeys;
            if (keys != null && ReferenceEquals(__instance, _limiter)) __result = keys;
        }

        /// <summary>Once a second, off the game thread: the visualizer's keys, and only while it runs.</summary>
        private static void Refresh()
        {
            try { _wanted = VisualizerRuns() ? Read() : null; }
            catch (Exception) { _wanted = null; }
        }

        private static bool VisualizerRuns()
        {
            var found = Process.GetProcessesByName("Visualizer.App");
            try { return found.Length > 0; }
            finally { foreach (var process in found) process.Dispose(); }
        }

        /// <summary>The Unity key names the visualizer wrote, one line; nothing when it wrote none.</summary>
        private static string? Read()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdofaiVisualizer",
                "key-limiter.txt");
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
    }
}
