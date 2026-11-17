using System;
using System.Reflection;
using HarmonyLib;

namespace AdofaiVisualizerSync
{
    /// <summary>
    /// The game's error meter with each tick in the zone of its own grade (user request 2026-11-17, AGENTS.md rule 6): display
    /// only, always on while this mod is. A prefix on scrHitErrorMeter.AddHit moves the angle it is given so that, after the
    /// meter's own scaling by the counted window, the tick lands where <see cref="MeterUnits"/> puts it. The edges come from
    /// the game's own scrMisc.GetAdjustedAngleBoundaryInDeg with the arguments AddHit uses (GCS.difficulty, the conductor's
    /// bpm times the hit floor's speed, the song's pitch, the margin scale), so they are the ones it grades by. Grading is
    /// untouched: AddHit only draws. Anything that cannot be read leaves the tick where the game put it.
    /// </summary>
    internal static class ErrorMeterTicks
    {
        private const string HarmonyId = "AdofaiVisualizerSync.ErrorMeterTicks";
        private const float RadiansToDegrees = 57.29578f;
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                                         | BindingFlags.FlattenHierarchy;

        private static Harmony? _harmony;
        private static MethodInfo? _boundaries;
        private static FieldInfo? _difficulty;
        private static MemberInfo? _conductor;
        private static FieldInfo? _bpm;
        private static FieldInfo? _song;
        private static FieldInfo? _speed;
        private static FieldInfo? _player;
        private static PropertyInfo? _currFloor;
        private static FieldInfo? _prevFloor;
        private static FieldInfo? _counted;
        private static FieldInfo? _perfect;
        private static FieldInfo? _pure;
        private static FieldInfo? _xPerfect;

        /// <summary>Patches the meter once. Never throws; what it cannot find is logged and the meter stays the game's.</summary>
        public static void Start(Action<string> log)
        {
            if (_harmony != null) return;
            try
            {
                var meter = AccessTools.TypeByName("scrHitErrorMeter");
                var addHit = meter == null ? null : AccessTools.Method(meter, "AddHit");
                var misc = AccessTools.TypeByName("scrMisc");
                _boundaries = misc == null ? null : AccessTools.Method(misc, "GetAdjustedAngleBoundaryInDeg");
                var constants = AccessTools.TypeByName("GCS");
                _difficulty = constants == null ? null : constants.GetField("difficulty", Any);
                var conductor = AccessTools.TypeByName("scrConductor");
                _conductor = conductor == null ? null
                    : (MemberInfo?)conductor.GetField("_instance", Any) ?? conductor.GetProperty("instance", Any);
                _bpm = conductor?.GetField("bpm", Any);
                _song = conductor?.GetField("song", Any);
                var floor = AccessTools.TypeByName("scrFloor");
                _speed = floor?.GetField("speed", Any);
                _prevFloor = floor?.GetField("prevfloor", Any);
                var planet = AccessTools.TypeByName("scrPlanet");
                _player = planet?.GetField("player", Any);
                _currFloor = _player?.FieldType.GetProperty("currFloor", Any);
                var edges = _boundaries?.ReturnType;
                _counted = edges?.GetField("Counted", Any);
                _perfect = edges?.GetField("Perfect", Any);
                _pure = edges?.GetField("Pure", Any);
                _xPerfect = edges?.GetField("XPerfect", Any);
                if (addHit == null || _boundaries == null || _difficulty == null || _conductor == null || _bpm == null || _song == null
                    || _speed == null || _counted == null || _perfect == null || _pure == null || _xPerfect == null)
                {
                    log("The error meter stays as the game draws it: a member it needs was not found.");
                    return;
                }
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(addHit, prefix: new HarmonyMethod(typeof(ErrorMeterTicks), nameof(Prefix)));
                _harmony = harmony;
            }
            catch (Exception error)
            {
                log("The error meter stays as the game draws it: " + error.Message);
            }
        }

        /// <summary>Takes the patch off again, only this one. Never throws.</summary>
        public static void Stop()
        {
            // UnpatchAll without an id would take every mod's patches off the game.
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch (Exception) { }
            _harmony = null;
        }

        /// <summary>Runs before AddHit draws a tick. It must never throw into the game, so any failure keeps the game's tick.</summary>
        private static void Prefix(ref float angleDiff, float marginScale, object? planet, object? hitFloor)
        {
            try
            {
                var conductor = Value(_conductor, null);
                if (conductor == null || _boundaries == null) return;
                // AddHit's own choice of floor: the hit floor, else the one before the planet's current one.
                var floor = hitFloor;
                if (floor == null && planet != null && _player != null && _currFloor != null && _prevFloor != null)
                {
                    var player = _player.GetValue(planet);
                    var current = player == null ? null : _currFloor.GetValue(player, null);
                    floor = current == null ? null : _prevFloor.GetValue(current);
                }
                var speed = floor == null ? 1.0 : Convert.ToDouble(_speed!.GetValue(floor));
                var bpm = Convert.ToDouble(_bpm!.GetValue(conductor)) * speed;
                var song = _song!.GetValue(conductor);
                var pitch = song == null ? 1.0 : Convert.ToDouble(song.GetType().GetProperty("pitch", Any)?.GetValue(song, null) ?? 1f);
                var edges = _boundaries.Invoke(null, new[] { _difficulty!.GetValue(null), bpm, pitch, (double)marginScale });
                if (edges == null) return;
                var counted = Convert.ToDouble(_counted!.GetValue(edges));
                if (!(counted > 0)) return;
                var degrees = angleDiff * -RadiansToDegrees;
                var units = MeterUnits.Units(degrees, Convert.ToDouble(_xPerfect!.GetValue(edges)), Convert.ToDouble(_pure!.GetValue(edges)),
                    Convert.ToDouble(_perfect!.GetValue(edges)), counted);
                // AddHit turns the angle into degrees and scales it by 60 / counted; this angle comes out at those units.
                angleDiff = (float)(units * counted / 60 / -RadiansToDegrees);
            }
            catch (Exception) { }
        }

        private static object? Value(MemberInfo? member, object? target) => member switch
        {
            FieldInfo field => field.GetValue(target),
            PropertyInfo property => property.GetValue(target, null),
            _ => null
        };
    }
}
