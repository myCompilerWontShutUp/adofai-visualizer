using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace AdofaiVisualizerSync
{
    /// <summary>
    /// Sends samples to the visualizer over a local named pipe, one JSON line each. The game thread only drops a sample in
    /// (<see cref="Post"/>) and never waits: connecting and writing happen on this thread, so a missing or slow reader can
    /// never hold up a frame. Nothing is read back - the pipe is one way.
    /// </summary>
    internal sealed class SyncWriter
    {
        /// <summary>The visualizer opens this pipe when its sync setting is on. Local only, so no port and no firewall.</summary>
        public const string PipeName = "adofai-visualizer-sync";
        /// <summary>
        /// The message shape. The app refuses anything else, so both sides can change without guessing. Raised to 2 when
        /// "pos" became the input-calibrated position and the two calibrations were added.
        /// </summary>
        public const int Protocol = 2;

        private const double MinIntervalMs = 4;
        private const int RetryDelayMs = 2000;
        private const int ConnectTimeoutMs = 200;

        private readonly object _gate = new object();
        private readonly Action<string> _log;
        private Thread? _thread;
        private GameSample _pending;
        private bool _hasPending;
        private string? _levelPath;
        private double[]? _levelTimes;
        private bool _levelSent;
        private bool _helloSent;
        private volatile bool _stop;
        private double _lastSentMs = double.NegativeInfinity;

        public SyncWriter(Action<string> log) => _log = log;

        /// <summary>Whether the pipe is connected right now, for the mod's own status line.</summary>
        public bool Connected { get; private set; }
        /// <summary>How many lines went out since the writer started.</summary>
        public long Sent { get; private set; }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "AdofaiVisualizerSync" };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            lock (_gate) Monitor.Pulse(_gate);
            _thread = null;
        }

        /// <summary>
        /// The tile times the game worked out for a level. They are kept, not just sent: the visualizer may start after the
        /// game, so every new connection gets them again. The line is built on the writer's thread, not here, because a
        /// long map is thousands of numbers and the game thread must not spend that.
        /// </summary>
        public void PostLevel(string path, double[] timesMs)
        {
            lock (_gate)
            {
                _levelPath = path;
                _levelTimes = timesMs;
                _levelSent = false;
                Monitor.Pulse(_gate);
            }
        }

        /// <summary>Called from the game thread. Takes a lock that is never held while writing, so it cannot block a frame.</summary>
        public void Post(GameSample sample)
        {
            lock (_gate)
            {
                _pending = sample;
                _hasPending = true;
                Monitor.Pulse(_gate);
            }
        }

        private void Loop()
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                while (!_stop)
                {
                    GameSample sample;
                    lock (_gate)
                    {
                        // The wait also wakes on its own, so a disconnect is noticed while the game sits in a menu.
                        if (!_hasPending) Monitor.Wait(_gate, 500);
                        if (_stop) break;
                        if (!_hasPending) continue;
                        sample = _pending;
                        _hasPending = false;
                    }
                    if (!sample.Valid) continue;
                    if (sample.SampledMs - _lastSentMs < MinIntervalMs) continue;

                    if (pipe == null || !pipe.IsConnected)
                    {
                        Connected = false;
                        pipe?.Dispose();
                        pipe = Connect();
                        if (pipe == null) { Thread.Sleep(RetryDelayMs); continue; }
                        Connected = true;
                        // A fresh reader knows nothing, so who this is and the level it is playing go out again.
                        lock (_gate) { _levelSent = false; _helloSent = false; }
                        _log("Connected to the visualizer.");
                    }

                    string? levelPath = null;
                    double[]? levelTimes = null;
                    var hello = false;
                    lock (_gate)
                    {
                        if (!_helloSent) { hello = true; _helloSent = true; }
                        if (!_levelSent && _levelTimes != null)
                        {
                            levelPath = _levelPath;
                            levelTimes = _levelTimes;
                            _levelSent = true;
                        }
                    }
                    try
                    {
                        if (hello)
                        {
                            var who = Encoding.UTF8.GetBytes(HelloLine());
                            pipe.Write(who, 0, who.Length);
                        }
                        if (levelTimes != null)
                        {
                            var level = Encoding.UTF8.GetBytes(LevelLine(levelPath, levelTimes));
                            pipe.Write(level, 0, level.Length);
                        }
                        var bytes = Encoding.UTF8.GetBytes(Line(sample));
                        pipe.Write(bytes, 0, bytes.Length);
                        pipe.Flush();
                        _lastSentMs = sample.SampledMs;
                        Sent++;
                    }
                    catch (IOException)
                    {
                        // The visualizer closed or turned sync off; drop the pipe and try again later.
                        lock (_gate) { _levelSent = false; _helloSent = false; }
                        Connected = false;
                        pipe.Dispose();
                        pipe = null;
                    }
                }
            }
            catch (Exception error)
            {
                _log("Stopped sending: " + error.Message);
            }
            finally
            {
                Connected = false;
                pipe?.Dispose();
            }
        }

        private NamedPipeClientStream? Connect()
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(ConnectTimeoutMs);
                return pipe;
            }
            catch (TimeoutException)
            {
                // No visualizer listening: the normal case, so it is not worth a log line every two seconds.
                pipe?.Dispose();
                return null;
            }
            catch (IOException)
            {
                pipe?.Dispose();
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                pipe?.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Which mod this is, sent first on every connection. The visualizer compares it with its own version, so a mod
        /// left over from an older build is named instead of looking like a mod that simply sends nothing.
        /// </summary>
        private static string HelloLine() =>
            "{\"v\":" + Protocol + ",\"mod\":\"" + Escape(Version) + "\"}\n";

        /// <summary>The build this mod came from; the same number as the app it was built with.</summary>
        public static string Version =>
            typeof(SyncWriter).Assembly.GetName().Version is { } version
                ? version.Major + "." + version.Minor + "." + version.Build
                : "unknown";

        /// <summary>The level's own tile times, so the visualizer can line its reading of the map up with the game's.</summary>
        private static string LevelLine(string? path, double[] timesMs)
        {
            var text = new StringBuilder(timesMs.Length * 9 + 64);
            text.Append("{\"v\":").Append(Protocol).Append(",\"lvl\":\"").Append(Escape(path ?? string.Empty)).Append("\",\"times\":[");
            for (var index = 0; index < timesMs.Length; index++)
            {
                if (index > 0) text.Append(',');
                text.Append(Number(timesMs[index]));
            }
            return text.Append("]}\n").ToString();
        }

        private static string Line(GameSample sample)
        {
            var text = new StringBuilder(160);
            text.Append("{\"v\":").Append(Protocol)
                .Append(",\"t\":").Append(Number(sample.SampledMs))
                .Append(",\"pos\":").Append(Number(sample.PositionMs))
                .Append(",\"st\":\"").Append(Escape(sample.State))
                .Append("\",\"paused\":").Append(sample.Paused ? "true" : "false")
                .Append(",\"floor\":").Append(sample.Floor.ToString(CultureInfo.InvariantCulture))
                .Append(",\"speed\":").Append(Number(sample.Speed))
                .Append(",\"pitch\":").Append(Number(sample.Pitch))
                .Append(",\"dead\":").Append(sample.Dead ? "true" : "false")
                .Append(",\"ci\":").Append(Number(sample.InputOffsetMs))
                .Append(",\"cv\":").Append(Number(sample.VisualOffsetMs))
                .Append(",\"path\":\"").Append(Escape(sample.Path))
                .Append("\"}\n");
            return text.ToString();
        }

        private static string Number(double value) =>
            double.IsNaN(value) || double.IsInfinity(value) ? "0" : value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Escape(string value)
        {
            var text = new StringBuilder(value.Length + 8);
            foreach (var letter in value)
            {
                if (letter == '"' || letter == '\\') text.Append('\\').Append(letter);
                else if (letter < ' ') text.Append(' ');
                else text.Append(letter);
            }
            return text.ToString();
        }
    }
}
