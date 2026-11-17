using System;
using System.Collections;
using System.Reflection;

namespace AdofaiVisualizerSync
{
    /// <summary>One reading of the game's own playback state. Times are milliseconds, like everywhere in the visualizer.</summary>
    internal struct GameSample
    {
        public bool Valid;
        /// <summary>Set when a level was there but could not be read this frame, unlike a menu, which has nothing to read.</summary>
        public bool Failed;
        public double SampledMs;
        public double PositionMs;
        public double InputOffsetMs;
        public double VisualOffsetMs;
        public double Pitch;
        public bool Dead;
        public string State;
        public bool Paused;
        /// <summary>
        /// scrController.currentFloorID. The game only ever raises it within one controller (scrController.Update keeps
        /// Math.Max(currentFloorID, currFloor.seqID)), so after a restart it goes on naming the tile the last run reached.
        /// </summary>
        public int Floor;
        /// <summary>
        /// GCS.checkpointNum: the tile a restart puts the player on (0 for the start, a checkpoint's respawn tile, the
        /// practice or editor start tile); -1 when it cannot be read. Tiles up to it are not played in that attempt.
        /// </summary>
        public int RestartFloor;
        /// <summary>GCS.difficulty: 0 Lenient, 1 Normal, 2 Strict; -1 when it cannot be read. It sets the minimum hit windows.</summary>
        public int Difficulty;
        /// <summary>The judgment ease this mod applies in the game now (1 for none), or -1 when it could not patch the game.</summary>
        public double Ease;
        public double Speed;
        public string Path;
        /// <summary>No Fail: 1 on, 0 off, -1 when it cannot be read. link inspect only presses keys while it is on.</summary>
        public int NoFail;
        /// <summary>The first player's judgment counts, one per HitMargin, or null when they cannot be read.</summary>
        public int[]? Judgments;
    }

    /// <summary>
    /// Reads the five values start synchronisation needs out of the running game (song position, state, pause, current tile,
    /// level path) plus the practice speed. Everything is found by reflection and cached, and Assembly-CSharp is never
    /// referenced, so a game update that moves a field only sets <see cref="Problem"/> and leaves the game running.
    /// Read-only by construction: this type writes no game field and calls nothing that changes game state.
    ///
    /// The game throws now and then on its own: a scene being torn down, a song not loaded yet. One frame like that costs
    /// that frame's reading and nothing more (<see cref="Trouble"/>); only members that are not there at all stop it.
    /// </summary>
    internal sealed class GameReader
    {
        /// <summary>A field or a property, whichever the game has under that name, read static or not as it is declared.</summary>
        private sealed class Member
        {
            private readonly FieldInfo? _field;
            private readonly PropertyInfo? _property;
            private readonly bool _static;

            private Member(FieldInfo? field, PropertyInfo? property)
            {
                _field = field;
                _property = property;
                _static = field?.IsStatic ?? property?.GetGetMethod(true)?.IsStatic ?? false;
            }

            /// <summary>The member, or null when the type has none by that name (a game update moved it).</summary>
            public static Member? Of(Type? type, string name)
            {
                if (type == null) return null;
                var field = type.GetField(name, Any);
                if (field != null) return new Member(field, null);
                var property = type.GetProperty(name, Any);
                return property != null && property.GetIndexParameters().Length == 0 && property.GetGetMethod(true) != null
                    ? new Member(null, property)
                    : null;
            }

            public Type ValueType => _field?.FieldType ?? _property!.PropertyType;

            /// <summary>The value on <paramref name="target"/>; null when an instance member has nothing to be read on.</summary>
            public object? Get(object? target)
            {
                if (!_static && target == null) return null;
                return _field != null ? _field.GetValue(_static ? null : target) : _property!.GetValue(_static ? null : target, null);
            }
        }

        /// <summary>How many frames in a row may fail before reading slows to one frame in <see cref="FailingEvery"/>.</summary>
        private const int FailingAfter = 60;
        private const int FailingEvery = 30;
        /// <summary>How many times to look for the game's list of death states before going on without it.</summary>
        private const int DeathTries = 600;

        private bool _resolved;
        private Member? _conductorInstance;
        private Member? _songPosition;
        private Member? _inputCalibration;
        private Member? _visualCalibration;
        private Member? _song;
        private Member? _pitch;
        private Member? _controllerInstance;
        private Member? _currentState;
        private Member? _deathStates;
        private int[]? _deathValues;
        private int _deathTried;
        private Member? _paused;
        private Member? _currentFloor;
        private Member? _gameInstance;
        private Member? _levelPath;
        private Member? _speed;
        private Member? _levelMakerInstance;
        private Member? _listFloors;
        private Member? _entryTime;
        private Member? _noFail;
        private Member? _noFailOption;
        private Member? _restartFloor;
        private Member? _difficulty;
        private Member? _playerManagerInstance;
        private Member? _mistakesManager;
        private Member? _marginTrackers;
        private Member? _marginCounts;
        private int[]? _lastJudgments;
        private int _lastStateValue = int.MinValue;
        private string _lastStateName = "?";
        private int _failures;
        private int _skipped;

        /// <summary>Why no value can be read at all, or null while everything resolves. This one is for good.</summary>
        public string? Problem { get; private set; }
        /// <summary>What went wrong the last time a frame could not be read, or null. The next frame tries again.</summary>
        public string? Trouble { get; private set; }
        /// <summary>How many frames in a row could not be read.</summary>
        public int Failures => _failures;
        /// <summary>
        /// The names of the game's grades in the order its judgment counts use (the HitMargin enum), or null when it was not
        /// found. The game build of 2026-11-04 added grades in the middle, so the visualizer reads the counts by these names.
        /// </summary>
        public string[]? JudgmentNames { get; private set; }

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                                         | BindingFlags.FlattenHierarchy;

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

                // The stored instance first: the property may go looking for one, which is work done every frame in a menu.
                _conductorInstance = Member.Of(conductor, "_instance") ?? Member.Of(conductor, "instance");
                // The input position, not the visual one. Falling notes say when to press, and the game judges a press
                // against minusi; minusv is where the game draws, which is a different question and a different offset.
                _songPosition = Member.Of(conductor, "songposition_minusi");
                _inputCalibration = Member.Of(conductor, "calibration_i");
                _visualCalibration = Member.Of(conductor, "calibration_v");
                // The real playback rate. The practice speed setting is not it: it is what practice mode would use.
                _song = Member.Of(conductor, "song");
                _pitch = _song == null ? null : Member.Of(_song.ValueType, "pitch");
                _controllerInstance = Member.Of(controller, "_instance") ?? Member.Of(controller, "instance");
                _currentState = Member.Of(controller, "currentState");
                // The game's own list of which states are a death, rather than this mod deciding what counts as one.
                _deathStates = Member.Of(controller, "deathStates");
                _paused = Member.Of(controller, "paused");
                _currentFloor = Member.Of(controller, "currentFloorID");
                _gameInstance = Member.Of(scene, "instance") ?? Member.Of(scene, "_instance");
                _levelPath = Member.Of(scene, "levelPath");
                _speed = Member.Of(constants, "practiceSpeedMultiplier");
                // No Fail, which link inspect requires so a run it plays never counts as a clear: the level's own flag
                // (scrController.noFail), else the option it comes from (GCS.useNoFail).
                _noFail = Member.Of(controller, "noFail");
                _noFailOption = Member.Of(game.GetType("GCS"), "useNoFail");
                // Where a restart carries on: ffxCheckpoint.StartEffect raises it to the checkpoint's respawn tile,
                // SetPracticeMode and scnEditor.Play set it to their start tile, RestartProgress puts it back to 0, and
                // scrController.Start_Rewind starts the attempt there. The app hides the tiles before it while the
                // countdown runs, which currentFloorID cannot tell (it keeps the highest tile reached).
                _restartFloor = Member.Of(game.GetType("GCS"), "checkpointNum");
                // The judgment difficulty the player chose: its minimum times are part of every hit window (scrMisc.GetMinimumTimes).
                _difficulty = Member.Of(game.GetType("GCS"), "difficulty");
                var margins = game.GetType("HitMargin");
                if (margins != null && margins.IsEnum) JudgmentNames = Enum.GetNames(margins);
                // The judgments so far, so link inspect can tell a Miss or an Overload in its run: scrPlayerManager's
                // mistakesManager keeps one scrMarginTracker per player, each counting hits by HitMargin (hitMarginsCount).
                var players = game.GetType("scrPlayerManager");
                _playerManagerInstance = Member.Of(players, "_instance") ?? Member.Of(players, "instance");
                _mistakesManager = Member.Of(players, "mistakesManager");
                _marginTrackers = _mistakesManager == null ? null : Member.Of(_mistakesManager.ValueType, "marginTrackers");
                _marginCounts = Member.Of(game.GetType("scrMarginTracker"), "hitMarginsCount");

                // The game's own tile schedule. Reading it beats watching the player move: currentFloorID only rises when
                // the player hits a tile, so it carries their timing, while entryTime is what the game itself worked out.
                var maker = game.GetType("scrLevelMaker");
                _levelMakerInstance = Member.Of(maker, "_instance") ?? Member.Of(maker, "instance");
                _listFloors = Member.Of(maker, "listFloors");
                _entryTime = Member.Of(game.GetType("scrFloor"), "entryTime");

                if (_conductorInstance == null || _controllerInstance == null || _gameInstance == null
                    || _songPosition == null || _currentState == null || _levelPath == null)
                    Problem = "The game moved a member this mod reads (instance / songposition_minusi / currentState / levelPath).";
            }
            catch (Exception error)
            {
                Problem = "Could not look at the game types: " + error.Message;
            }
        }

        /// <summary>
        /// Reads one sample: valid while a level runs, invalid in a menu, and <see cref="GameSample.Failed"/> when the game
        /// threw this frame. Called from the game thread; never throws.
        /// </summary>
        public GameSample Read(double sampledMs)
        {
            var sample = new GameSample { SampledMs = sampledMs, State = "?", Path = string.Empty, Pitch = 1, Speed = 1, Floor = -1,
                RestartFloor = -1, Difficulty = -1, NoFail = -1 };
            if (Problem != null) return sample;
            // A state that throws every frame is not worth an exception every frame inside the game: after a second of it,
            // one frame in so many tries again.
            if (_failures >= FailingAfter && ++_skipped % FailingEvery != 0)
            {
                sample.Failed = true;
                return sample;
            }
            try
            {
                var conductor = Alive(_conductorInstance!.Get(null));
                var controller = Alive(_controllerInstance!.Get(null));
                var scene = Alive(_gameInstance!.Get(null));
                // No conductor or no level means a menu: nothing to report, and the app keeps its own clock.
                if (conductor == null || controller == null || scene == null) return Recovered(sample);

                var path = _levelPath!.Get(scene) as string;
                if (string.IsNullOrEmpty(path)) return Recovered(sample);

                var seconds = Number(_songPosition!.Get(conductor));
                if (seconds == null) return Recovered(sample);
                // The clock the game judges by must be a time: anything else would put the notes somewhere arbitrary.
                if (double.IsNaN(seconds.Value) || double.IsInfinity(seconds.Value))
                    return Fail(sample, "the song position is not a number.");

                var state = _currentState!.Get(controller);
                sample.PositionMs = seconds.Value * 1000;
                sample.State = StateName(state);
                sample.Path = path!;
                // Only shown or only refining what the app does, so one of these failing never costs the position.
                sample.InputOffsetMs = Optional(_inputCalibration, conductor) is double input ? input * 1000 : 0;
                sample.VisualOffsetMs = Optional(_visualCalibration, conductor) is double visual ? visual * 1000 : 0;
                sample.Pitch = Optional(_pitch, Alive(Optional(_song, conductor))) is double pitch ? pitch : 1;
                sample.Dead = IsDeath(state, controller);
                sample.Paused = OptionalObject(_paused, controller) is bool paused && paused;
                sample.Floor = OptionalObject(_currentFloor, controller) is int floor ? floor : -1;
                sample.RestartFloor = OptionalObject(_restartFloor, null) is int restart && restart >= 0 ? restart : -1;
                sample.Difficulty = OptionalObject(_difficulty, null) is { } difficulty && difficulty.GetType().IsEnum
                    ? Convert.ToInt32(difficulty) : -1;
                sample.Speed = Optional(_speed, null) is double speed ? speed : 1;
                sample.NoFail = OptionalObject(_noFail, controller) is bool noFail ? (noFail ? 1 : 0)
                    : OptionalObject(_noFailOption, null) is bool option ? (option ? 1 : 0) : -1;
                sample.Judgments = Judgments();
                sample.Valid = true;
                return Recovered(sample);
            }
            catch (Exception error)
            {
                return Fail(sample, Reason(error));
            }
        }

        /// <summary>
        /// The first player's judgment counts. A new array only when they changed: the writer sends a sample from its own
        /// thread, so an array once handed over is never written again. Null when they cannot be read.
        /// </summary>
        private int[]? Judgments()
        {
            var manager = Alive(OptionalObject(_playerManagerInstance, null));
            var mistakes = OptionalObject(_mistakesManager, manager);
            if (!(OptionalObject(_marginTrackers, mistakes) is Array trackers) || trackers.Length == 0) return null;
            if (!(OptionalObject(_marginCounts, trackers.GetValue(0)) is int[] counts)) return null;
            if (_lastJudgments != null && _lastJudgments.Length == counts.Length)
            {
                var same = true;
                for (var index = 0; index < counts.Length && same; index++) same = counts[index] == _lastJudgments[index];
                if (same) return _lastJudgments;
            }
            _lastJudgments = (int[])counts.Clone();
            return _lastJudgments;
        }

        private GameSample Recovered(GameSample sample)
        {
            _failures = 0;
            _skipped = 0;
            return sample;
        }

        private GameSample Fail(GameSample sample, string why)
        {
            _failures++;
            Trouble = why;
            sample.Failed = true;
            return sample;
        }

        /// <summary>
        /// The time the game worked out for every tile, in milliseconds, or null when it cannot be read yet. Only worth
        /// asking for once a level: it walks every tile.
        /// </summary>
        public double[]? ReadLevelTimes()
        {
            if (Problem != null || _listFloors == null || _entryTime == null || _levelMakerInstance == null) return null;
            try
            {
                var maker = Alive(_levelMakerInstance.Get(null));
                if (maker == null) return null;
                if (!(_listFloors.Get(maker) is IList floors) || floors.Count == 0) return null;
                var times = new double[floors.Count];
                for (var index = 0; index < floors.Count; index++)
                {
                    var floor = floors[index];
                    if (floor == null || !(Number(_entryTime.Get(floor)) is double seconds) || double.IsNaN(seconds)
                        || double.IsInfinity(seconds)) return null;
                    times[index] = seconds * 1000;
                }
                // Tiles that are all at one time have not been worked out yet; asking again later gets the real ones.
                if (times.Length > 1 && times[times.Length - 1] == times[0]) return null;
                return times;
            }
            catch (Exception error)
            {
                Trouble = "reading the tile times: " + Reason(error);
                return null;
            }
        }

        /// <summary>
        /// Whether the state is one the game itself counts as a death. The list is read once it is there, then compared as
        /// numbers; without it no state counts as one, which only means the notes run on through a game over.
        /// </summary>
        private bool IsDeath(object? state, object controller)
        {
            if (state == null || _deathStates == null) return false;
            if (_deathValues == null)
            {
                if (_deathTried >= DeathTries) return false;
                _deathTried++;
                if (!(OptionalObject(_deathStates, controller) is IEnumerable states)) return false;
                var values = new System.Collections.Generic.List<int>();
                foreach (var value in states) if (value != null) values.Add(Convert.ToInt32(value));
                if (values.Count == 0) return false;
                _deathValues = values.ToArray();
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

        /// <summary>A number the game keeps as a float or a double, or null when it is neither.</summary>
        private static double? Number(object? value) => value is double d ? d : value is float f ? f : (double?)null;

        private static double? Optional(Member? member, object? target) => Number(OptionalObject(member, target));

        /// <summary>A value that is nice to have: when the game throws reading it, the sample goes without it.</summary>
        private static object? OptionalObject(Member? member, object? target)
        {
            if (member == null) return null;
            try { return member.Get(target); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The object, or null when it is missing or a Unity object that was destroyed: such an object still looks like an
        /// object to C#, and reading through it is exactly what throws while a scene is torn down.
        /// </summary>
        private static object? Alive(object? value) => value is UnityEngine.Object unity ? (unity != null ? value : null) : value;

        /// <summary>What a getter that threw actually said, not the reflection wrapper around it.</summary>
        private static string Reason(Exception error)
        {
            var inner = error is TargetInvocationException { InnerException: { } cause } ? cause : error;
            return inner.GetType().Name + ": " + inner.Message;
        }
    }
}
