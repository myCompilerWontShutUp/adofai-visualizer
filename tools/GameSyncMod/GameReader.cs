using System;
using System.Reflection;

namespace AdofaiVisualizerSync
{
    /// <summary>One reading of the game's own playback state. Times are milliseconds, like everywhere in the visualizer.</summary>
    internal struct GameSample
    {
        public bool Valid;
        public double SampledMs;
        public double PositionMs;
        public double InputOffsetMs;
        public double VisualOffsetMs;
        public double Pitch;
        public bool Dead;
        public string State;
        public bool Paused;
        public int Floor;
        public double Speed;
        public string Path;
    }

    /// <summary>
    /// Reads the five values start synchronisation needs out of the running game (song position, state, pause, current tile,
    /// level path) plus the practice speed. Everything is found by reflection and cached, and Assembly-CSharp is never
    /// referenced, so a game update that moves a field only sets <see cref="Problem"/> and leaves the game running.
    /// Read-only by construction: this type writes no game field and calls nothing that changes game state.
    /// </summary>
    internal sealed class GameReader
    {
        private bool _resolved;
        private PropertyInfo? _conductorInstance;
        private FieldInfo? _conductorInstanceField;
        private PropertyInfo? _songPosition;
        private PropertyInfo? _inputCalibration;
        private PropertyInfo? _visualCalibration;
        private FieldInfo? _song;
        private PropertyInfo? _pitch;
        private PropertyInfo? _controllerInstance;
        private FieldInfo? _controllerInstanceField;
        private FieldInfo? _currentState;
        private FieldInfo? _deathStates;
        private int[]? _deathValues;
        private PropertyInfo? _paused;
        private FieldInfo? _currentFloor;
        private FieldInfo? _gameInstance;
        private FieldInfo? _levelPath;
        private FieldInfo? _speed;
        private PropertyInfo? _levelMakerInstance;
        private FieldInfo? _levelMakerInstanceField;
        private FieldInfo? _listFloors;
        private FieldInfo? _entryTime;
        private int _lastStateValue = int.MinValue;
        private string _lastStateName = "?";

        /// <summary>Why no value can be read, or null while everything resolves.</summary>
        public string? Problem { get; private set; }

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        /// <summary>Finds the members once. Called from the game thread; never throws.</summary>
        public void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                Assembly? game = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name == "Assembly-CSharp") { game = assembly; break; }
                }
                if (game == null) { Problem = "Assembly-CSharp is not loaded."; return; }

                var conductor = game.GetType("scrConductor");
                var controller = game.GetType("scrController");
                var scene = game.GetType("scnGame");
                var constants = game.GetType("GCNS");
                if (conductor == null || controller == null || scene == null)
                {
                    Problem = "The game types scrConductor/scrController/scnGame were not found.";
                    return;
                }

                _conductorInstance = conductor.GetProperty("instance", Any);
                _conductorInstanceField = conductor.GetField("_instance", Any);
                // The input position, not the visual one. Falling notes say when to press, and the game judges a press
                // against minusi; minusv is where the game draws, which is a different question and a different offset.
                _songPosition = conductor.GetProperty("songposition_minusi", Any);
                _inputCalibration = conductor.GetProperty("calibration_i", Any);
                _visualCalibration = conductor.GetProperty("calibration_v", Any);
                // The real playback rate. The practice speed setting is not it: it is what practice mode would use.
                _song = conductor.GetField("song", Any);
                _pitch = _song?.FieldType.GetProperty("pitch", BindingFlags.Public | BindingFlags.Instance);
                _controllerInstance = controller.GetProperty("instance", Any);
                _controllerInstanceField = controller.GetField("_instance", Any);
                _currentState = controller.GetField("currentState", Any);
                // The game's own list of which states are a death, rather than this mod deciding what counts as one.
                _deathStates = controller.GetField("deathStates", Any);
                _paused = controller.GetProperty("paused", Any);
                _currentFloor = controller.GetField("currentFloorID", Any);
                _gameInstance = scene.GetField("instance", Any);
                _levelPath = scene.GetField("levelPath", Any);
                _speed = constants?.GetField("practiceSpeedMultiplier", Any);

                // The game's own tile schedule. Reading it beats watching the player move: currentFloorID only rises when
                // the player hits a tile, so it carries their timing, while entryTime is what the game itself worked out.
                var maker = game.GetType("scrLevelMaker");
                var tile = game.GetType("scrFloor");
                _levelMakerInstance = maker?.GetProperty("instance", Any);
                _levelMakerInstanceField = maker?.GetField("_instance", Any);
                _listFloors = maker?.GetField("listFloors", Any);
                _entryTime = tile?.GetField("entryTime", Any);

                if (_songPosition == null || _currentState == null || _levelPath == null)
                    Problem = "The game moved a field this mod reads (songposition_minusv / currentState / levelPath).";
            }
            catch (Exception error)
            {
                Problem = "Could not look at the game types: " + error.Message;
            }
        }

        /// <summary>Reads one sample, or an invalid one when no level is running. Called from the game thread; never throws.</summary>
        public GameSample Read(double sampledMs)
        {
            var sample = new GameSample { SampledMs = sampledMs, State = "?", Path = string.Empty };
            if (Problem != null) return sample;
            try
            {
                var conductor = _conductorInstance?.GetValue(null, null) ?? _conductorInstanceField?.GetValue(null);
                var controller = _controllerInstance?.GetValue(null, null) ?? _controllerInstanceField?.GetValue(null);
                var scene = _gameInstance?.GetValue(null);
                // No conductor or no level means a menu: nothing to report, and the app keeps its own clock.
                if (conductor == null || controller == null || scene == null) return sample;

                var path = _levelPath?.GetValue(scene) as string;
                if (string.IsNullOrEmpty(path)) return sample;

                var position = _songPosition?.GetValue(conductor, null);
                if (!(position is double seconds)) return sample;

                sample.PositionMs = seconds * 1000;
                sample.InputOffsetMs = _inputCalibration?.GetValue(null, null) is float input ? input * 1000 : 0;
                sample.VisualOffsetMs = _visualCalibration?.GetValue(null, null) is float visual ? visual * 1000 : 0;
                var source = _song?.GetValue(conductor);
                sample.Pitch = source != null && _pitch?.GetValue(source, null) is float pitch ? pitch : 1;
                var state = _currentState?.GetValue(controller);
                sample.State = StateName(state);
                sample.Dead = IsDeath(state);
                sample.Paused = _paused?.GetValue(controller, null) is bool paused && paused;
                sample.Floor = _currentFloor?.GetValue(controller) is int floor ? floor : -1;
                sample.Speed = _speed?.GetValue(null) is float speed ? speed : 1.0;
                sample.Path = path!;
                sample.Valid = true;
                return sample;
            }
            catch (Exception error)
            {
                // One failure is enough: stop reading rather than repeat it every frame inside the game.
                Problem = "Reading the game state failed: " + error.Message;
                return sample;
            }
        }

        /// <summary>
        /// The time the game worked out for every tile, in milliseconds, or null when it cannot be read. Only worth asking
        /// for once a level: it walks every tile.
        /// </summary>
        public double[]? ReadLevelTimes()
        {
            if (Problem != null || _listFloors == null || _entryTime == null) return null;
            try
            {
                var maker = _levelMakerInstance?.GetValue(null, null) ?? _levelMakerInstanceField?.GetValue(null);
                if (maker == null) return null;
                if (!(_listFloors.GetValue(maker) is System.Collections.IList floors) || floors.Count == 0) return null;
                var times = new double[floors.Count];
                for (var index = 0; index < floors.Count; index++)
                {
                    var floor = floors[index];
                    if (floor == null || !(_entryTime.GetValue(floor) is double seconds)) return null;
                    times[index] = seconds * 1000;
                }
                return times;
            }
            catch (Exception error)
            {
                Problem = "Reading the tile times failed: " + error.Message;
                return null;
            }
        }

        /// <summary>Whether the state is one the game itself counts as a death. Read once, then compared as numbers.</summary>
        private bool IsDeath(object? state)
        {
            if (state == null) return false;
            if (_deathValues == null)
            {
                if (!(_deathStates?.GetValue(null) is Array states)) { _deathValues = new int[0]; return false; }
                var values = new int[states.Length];
                for (var index = 0; index < states.Length; index++)
                {
                    var value = states.GetValue(index);
                    values[index] = value == null ? int.MinValue : Convert.ToInt32(value);
                }
                _deathValues = values;
            }
            var now = Convert.ToInt32(state);
            foreach (var value in _deathValues) if (value == now) return true;
            return false;
        }

        /// <summary>The enum name, cached so a frame does not allocate one every time the state stays the same.</summary>
        private string StateName(object? value)
        {
            if (value == null) return "?";
            var number = Convert.ToInt32(value);
            if (number == _lastStateValue) return _lastStateName;
            _lastStateValue = number;
            _lastStateName = value.ToString() ?? "?";
            return _lastStateName;
        }
    }
}
