using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.Json;
using Visualizer.Application.Sync;
using static Visualizer.Core.Localization.ConsoleText;

namespace Visualizer.Infrastructure.Sync;

/// <summary>
/// Listens for the read-only sync mod on a local named pipe and hands what it sends to a <see cref="GameSyncLink"/>.
/// One way: this end only reads. The pipe is local, so there is no port and no firewall prompt, and only the same user can
/// open it. Nothing received is written to disk (AGENTS.md rule 5). See <c>docs/game-sync-plan.md</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeGameSyncServer : IDisposable
{
    /// <summary>The name the mod connects to; both sides have it as a constant so neither has to guess.</summary>
    public const string PipeName = "adofai-visualizer-sync";

    /// <summary>How long to wait before trying again when the name is taken, so a second app does not spin on it.</summary>
    private const int BusyDelayMs = 2000;
    /// <summary>
    /// How many games may be connected at once. ADOFAI sometimes starts twice (user report), and a game that hangs on to the
    /// pipe must not keep out the one being played, so more than one is taken in and one of them is followed (see Choose).
    /// </summary>
    public const int MaxGames = 4;
    /// <summary>A game silent this long is not the one being played, and another that still sends is followed instead.</summary>
    public const double QuietMs = 500;

    private readonly GameSyncLink _link;
    private readonly string _name;
    private readonly Func<uint> _foreground;
    private readonly object _clientsGate = new();
    private readonly List<Client> _clients = [];
    private readonly List<Task> _serving = [];
    private Client? _active;
    private CancellationTokenSource? _stopping;
    private Task? _loop;

    /// <summary>One connected game: who it is, what it said first, and when it last sent a reading.</summary>
    private sealed class Client(uint processId)
    {
        public uint ProcessId { get; } = processId;
        public string? Mod { get; set; }
        public IReadOnlyList<string>? JudgmentNames { get; set; }
        public GameSyncLevel? Level { get; set; }
        public double LastReadingMs { get; set; } = double.NaN;
    }

    /// <summary>
    /// <paramref name="pipeName"/> is only for tests, which must not fight a running app for the one shared name;
    /// everything else uses <see cref="PipeName"/>, which is what the mod connects to. <paramref name="foreground"/> says which
    /// process owns the window in front (tests pass their own); the game in front is the one followed.
    /// </summary>
    public NamedPipeGameSyncServer(GameSyncLink link, string? pipeName = null, Func<uint>? foreground = null)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _name = string.IsNullOrWhiteSpace(pipeName) ? PipeName : pipeName;
        _foreground = foreground ?? (() => Input.WindowsForegroundObserver.ForegroundProcessId());
    }

    /// <summary>Whether the pipe is open and waiting. A loop that ended on its own is not listening, whatever it was asked.</summary>
    public bool Listening => _loop is { IsCompleted: false };
    /// <summary>Whether a mod is connected right now.</summary>
    public bool Connected { get { lock (_clientsGate) return _clients.Count > 0; } }
    /// <summary>How many games are connected; more than one when the game runs twice.</summary>
    public int Games { get { lock (_clientsGate) return _clients.Count; } }
    /// <summary>
    /// The process id of the game followed, 0 when none is or Windows does not say. Only read, never opened: the log and
    /// inspect name it, so a second game running is told apart from the one followed.
    /// </summary>
    public uint ClientProcessId { get { lock (_clientsGate) return _active?.ProcessId ?? 0; } }
    /// <summary>The last thing that went wrong, or null. Kept for the console to show; it does not stop the listening.</summary>
    public string? Problem { get; private set; }

    public void Start()
    {
        if (_loop != null) return;
        _stopping = new CancellationTokenSource();
        Problem = null;
        _loop = Task.Run(() => Listen(_stopping.Token));
    }

    public void Stop()
    {
        var stopping = _stopping;
        var loop = _loop;
        _stopping = null;
        _loop = null;
        if (stopping == null) return;
        stopping.Cancel();
        Task[] serving;
        lock (_clientsGate) serving = [.. _serving];
        // Every loop only waits on its pipe, so they come back at once; a short wait keeps Dispose from racing them.
        try { Task.WaitAll([.. serving, .. loop is null ? Array.Empty<Task>() : [loop]], TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        stopping.Dispose();
        lock (_clientsGate)
        {
            _clients.Clear();
            _serving.Clear();
            _active = null;
        }
    }

    private async Task Listen(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // With no instance of this app's open, the new one must be the first of its name: another visualizer already
                // listening keeps it, as when only one instance was ever made.
                var options = PipeOptions.Asynchronous | (Games == 0 ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                pipe = new NamedPipeServerStream(_name, PipeDirection.In, MaxGames, PipeTransmissionMode.Byte, options);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                var client = new Client(GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id) ? id : 0);
                var connected = pipe;
                pipe = null;
                lock (_clientsGate)
                {
                    _clients.Add(client);
                    _serving.RemoveAll(task => task.IsCompleted);
                    _serving.Add(Task.Run(() => Serve(connected, client, token)));
                }
                Problem = null;
                // Every instance taken: wait for a game to leave before offering another.
                while (Games >= MaxGames) await Task.Delay(BusyDelayMs / 4, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            // The name already taken by another visualizer, or by another account: say so and try again later, never spin
            // on it and never stop listening for good.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Problem = T("PipeTaken", error.Message);
                try { await Task.Delay(BusyDelayMs, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally { pipe?.Dispose(); }
        }
    }

    /// <summary>Reads one game until it leaves. A game that dies mid-line costs its own connection, never another's.</summary>
    private async Task Serve(NamedPipeServerStream pipe, Client client, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                Take(client, line, _link.NowMs);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        finally
        {
            // The pipe goes before the game is forgotten, so no instance of this app's is left when none is counted.
            pipe.Dispose();
            lock (_clientsGate)
            {
                _clients.Remove(client);
                // The next reading from another game picks it; the link keeps the last word, which goes stale on its own.
                if (_active == client) _active = null;
            }
        }
    }

    /// <summary>One line from a game. Whatever is in it, it costs that line at most, never the connection.</summary>
    private void Take(Client client, string line, double arrivedMs)
    {
        if (line.Length == 0) return;
        lock (_clientsGate)
        {
            try
            {
                // With no game followed, the first one to speak is: its hello and level count before its first reading,
                // so link auto can load the song as soon as the game opens it.
                if (ParseMod(line) is { } mod)
                {
                    client.Mod = mod;
                    client.JudgmentNames = ParseJudgmentNames(line);
                    if (_active is null) Follow(client);
                    else if (client == _active)
                    {
                        _link.SetMod(mod);
                        _link.SetJudgmentNames(client.JudgmentNames);
                    }
                }
                else if (ParseLevel(line) is { } level)
                {
                    client.Level = level;
                    if (_active is null) Follow(client);
                    else if (client == _active) _link.SetLevel(level);
                }
                else if (Parse(line) is { } message)
                {
                    client.LastReadingMs = arrivedMs;
                    if (Choose(arrivedMs) == client) _link.Add(message, arrivedMs);
                }
                else if (client == _active) _link.Refuse(T("LineNotReading"));
            }
            catch (ArgumentException) { if (client == _active) _link.Refuse(T("LineBadValue")); }
        }
    }

    /// <summary>
    /// Which game is followed (user report: ADOFAI sometimes runs twice, and a game in trouble may keep the pipe while it
    /// sends nothing): the one whose window is in front while it sends; else the one followed so far while it still sends;
    /// else the one that sent last. Changing game starts the link over from that game's own hello and level.
    /// </summary>
    private Client? Choose(double nowMs)
    {
        bool Sends(Client client) => double.IsFinite(client.LastReadingMs) && nowMs - client.LastReadingMs <= QuietMs;
        var front = _foreground();
        var pick = _clients.FirstOrDefault(client => front != 0 && client.ProcessId == front && Sends(client))
            ?? (_active is { } active && Sends(active) ? active : null)
            ?? _clients.Where(Sends).OrderByDescending(client => client.LastReadingMs).FirstOrDefault();
        if (pick is not null && pick != _active) Follow(pick);
        return _active;
    }

    /// <summary>
    /// Follows this game from now on. What the link knew belongs to the other connection: a restarted game counts from
    /// zero again and another game plays another run, so the last word is dropped rather than mixed in, and the link
    /// starts from this game's own hello and level.
    /// </summary>
    private void Follow(Client client)
    {
        _active = client;
        _link.BeginConnection();
        if (client.Mod is { } mod) _link.SetMod(mod);
        _link.SetJudgmentNames(client.JudgmentNames);
        if (client.Level is { } level) _link.SetLevel(level);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    /// <summary>Reads one line, or null when it is not a reading. Values are checked by the link, not here.</summary>
    internal static GameSyncMessage? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!Number(root, "v", out var protocol) || !Number(root, "t", out var clock) || !Number(root, "pos", out var position))
                return null;
            var state = Text(root, "st");
            var path = Text(root, "path");
            if (state == null || path == null) return null;
            var paused = root.TryGetProperty("paused", out var pausedValue) && pausedValue.ValueKind == JsonValueKind.True;
            var floor = Number(root, "floor", out var floorValue) ? (int)floorValue : -1;
            var speed = Number(root, "speed", out var speedValue) ? speedValue : 1;
            var pitch = Number(root, "pitch", out var pitchValue) ? pitchValue : 1;
            var input = Number(root, "ci", out var inputValue) ? inputValue : 0;
            var visual = Number(root, "cv", out var visualValue) ? visualValue : 0;
            var dead = root.TryGetProperty("dead", out var deadValue) && deadValue.ValueKind == JsonValueKind.True;
            bool? noFail = root.TryGetProperty("nf", out var noFailValue)
                ? noFailValue.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
                : null;
            int[]? judgments = null;
            if (root.TryGetProperty("hm", out var counts) && counts.ValueKind == JsonValueKind.Array)
                judgments = counts.EnumerateArray().All(count => count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var whole) && whole >= 0)
                    ? counts.EnumerateArray().Select(count => count.GetInt32()).ToArray()
                    : null;
            // An older mod sends no restart tile; anything but a whole tile number is the same as not knowing it.
            int? restart = root.TryGetProperty("cp", out var restartValue) && restartValue.ValueKind == JsonValueKind.Number
                && restartValue.TryGetInt32(out var restartFloor) && restartFloor >= 0 ? restartFloor : null;
            // Lenient, Normal or Strict; any other number is not a difficulty this app knows.
            int? difficulty = root.TryGetProperty("df", out var difficultyValue) && difficultyValue.ValueKind == JsonValueKind.Number
                && difficultyValue.TryGetInt32(out var level) && level is >= 0 and <= 2 ? level : null;
            // The judgment ease in force in the game; anything outside what the settings allow is not one.
            double? ease = Number(root, "ez", out var easeValue) && easeValue >= 1 && easeValue <= Visualizer.Core.Maps.HitGrading.MaximumEase
                ? easeValue : null;
            return new GameSyncMessage((int)protocol, clock, position, state, paused, floor, speed, path, pitch, input, visual, dead,
                noFail, judgments, restart, difficulty, ease);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The grade names a mod's hello carries, or null when it has none (an older mod) or they are not all text.</summary>
    internal static IReadOnlyList<string>? ParseJudgmentNames(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("hmn", out var names) || names.ValueKind != JsonValueKind.Array) return null;
            var list = new List<string>();
            foreach (var name in names.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())) return null;
                list.Add(name.GetString()!);
            }
            return list.AsReadOnly();
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Reads the mod's own version, which it sends first on every connection, or null when the line is not one.</summary>
    internal static string? ParseMod(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!Number(root, "v", out var protocol) || (int)protocol != GameSyncLink.Protocol) return null;
            return Text(root, "mod") is { } version && !string.IsNullOrWhiteSpace(version) ? version : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Reads a level's tile times, or null when the line is not one. Sent once when the game loads a level.</summary>
    internal static GameSyncLevel? ParseLevel(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!Number(root, "v", out var protocol) || (int)protocol != GameSyncLink.Protocol) return null;
            if (Text(root, "lvl") is not { } path) return null;
            if (!root.TryGetProperty("times", out var times) || times.ValueKind != JsonValueKind.Array) return null;
            var read = new double[times.GetArrayLength()];
            var index = 0;
            foreach (var time in times.EnumerateArray())
            {
                if (time.ValueKind != JsonValueKind.Number || !time.TryGetDouble(out read[index])) return null;
                index++;
            }
            return new GameSyncLevel(path, read);
        }
        catch (JsonException) { return null; }
    }

    private static bool Number(JsonElement root, string name, out double value)
    {
        value = 0;
        return root.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Number && found.TryGetDouble(out value);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String ? found.GetString() : null;

    public void Dispose() => Stop();
}
