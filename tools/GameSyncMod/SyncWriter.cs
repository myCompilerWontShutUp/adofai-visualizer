using System;
using System.Collections.Generic;
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
        /// <summary>How many log lines wait for the game thread at most; a writer that keeps failing must not fill memory.</summary>
        private const int LogCapacity = 32;

        private readonly object _gate = new object();
        /// <summary>
        /// Lines for the mod's log. Unity Mod Manager's logger is not made for other threads, so they wait here until the
        /// game thread takes them (<see cref="TakeLog"/>).
        /// </summary>
        private readonly Queue<string> _log = new Queue<string>();
        private Thread? _thread;
        private GameSample _pending;
        private bool _hasPending;
        private string? _levelPath;
        private double[]? _levelTimes;
        private bool _levelSent;
        private bool _helloSent;
        private volatile bool _stop;
        private double _lastSentMs = double.NegativeInfinity;

        /// <summary>Hands the waiting log lines to the game thread, oldest first.</summary>
        public string[] TakeLog()
        {
            lock (_log)
            {
                if (_log.Count == 0) return Array.Empty<string>();
                var lines = _log.ToArray();
                _log.Clear();
                return lines;
            }
        }

        private void Log(string line)
        {
            lock (_log)
            {
                if (_log.Count < LogCapacity) _log.Enqueue(line);
            }
        }

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

        /// <summary>Whether these tile times are not the ones last given for this level.</summary>
        public bool LevelDiffers(string path, double[] timesMs)
        {
            lock (_gate)
            {
                if (_levelPath != path || _levelTimes == null || _levelTimes.Length != timesMs.Length) return true;
                for (var index = 0; index < timesMs.Length; index++)
                    if (_levelTimes[index] != timesMs[index]) return true;
                return false;
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
            string? lastFailure = null;
            try
            {
                while (!_stop)
                {
                    try
                    {
                        pipe = Step(pipe);
                        lastFailure = null;
                    }
                    // Whatever goes wrong costs this connection, never the thread: the pipe is dropped and made again
                    // later, and the same failure is only logged once.
                    catch (Exception error)
                    {
                        if (error.Message != lastFailure) Log("Sending failed, trying again: " + error.Message);
                        lastFailure = error.Message;
                        lock (_gate) { _levelSent = false; _helloSent = false; }
                        Connected = false;
                        pipe?.Dispose();
                        pipe = null;
                        if (!_stop) Thread.Sleep(RetryDelayMs);
                    }
                }
            }
            finally
            {
                Connected = false;
                pipe?.Dispose();
            }
        }

        /// <summary>Sends the newest sample, connecting first when needed. Returns the pipe to use next time.</summary>
        private NamedPipeClientStream? Step(NamedPipeClientStream? pipe)
        {
            GameSample sample;
            lock (_gate)
            {
                // The wait also wakes on its own, so a disconnect is noticed while the game sits in a menu.
                if (!_hasPending) Monitor.Wait(_gate, 500);
                if (_stop || !_hasPending) return pipe;
                sample = _pending;
                _hasPending = false;
            }
            if (!sample.Valid) return pipe;
            if (sample.SampledMs - _lastSentMs < MinIntervalMs) return pipe;

            if (pipe == null || !pipe.IsConnected)
            {
                Connected = false;
                pipe?.Dispose();
                pipe = Connect();
                if (pipe == null) { Thread.Sleep(RetryDelayMs); return null; }
                Connected = true;
                // A fresh reader knows nothing, so who this is and the level it is playing go out again.
                lock (_gate) { _levelSent = false; _helloSent = false; }
                Log("Connected to the visualizer.");
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
                return pipe;
            }
            catch (Exception error)
            {
                lock (_gate) { _levelSent = false; _helloSent = false; }
                Connected = false;
                pipe.Dispose();
                // The visualizer closed or turned sync off: drop the pipe and try again later. Not worth a log line,
                // since it is what closing the app looks like from here; anything else is logged by the loop.
                if (error is IOException || error is ObjectDisposedException || error is InvalidOperationException) return null;
                throw;
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
                .Append("\",\"nf\":").Append(sample.NoFail < 0 ? "null" : sample.NoFail == 1 ? "true" : "false");
            if (sample.Judgments is { } judgments)
            {
                text.Append(",\"hm\":[");
                for (var index = 0; index < judgments.Length; index++)
                {
                    if (index > 0) text.Append(',');
                    text.Append(judgments[index].ToString(CultureInfo.InvariantCulture));
                }
                text.Append(']');
            }
            text.Append("}\n");
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
