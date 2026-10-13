using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Visualizer.Application.Sync;

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

    private readonly GameSyncLink _link;
    private readonly string _name;
    private CancellationTokenSource? _stopping;
    private Task? _loop;

    /// <summary>
    /// <paramref name="pipeName"/> is only for tests, which must not fight a running app for the one shared name;
    /// everything else uses <see cref="PipeName"/>, which is what the mod connects to.
    /// </summary>
    public NamedPipeGameSyncServer(GameSyncLink link, string? pipeName = null)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _name = string.IsNullOrWhiteSpace(pipeName) ? PipeName : pipeName;
    }

    /// <summary>Whether the pipe is open and waiting. A loop that ended on its own is not listening, whatever it was asked.</summary>
    public bool Listening => _loop is { IsCompleted: false };
    /// <summary>Whether a mod is connected right now.</summary>
    public bool Connected { get; private set; }
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
        // The loop only waits on the pipe, so it comes back at once; a short wait keeps Dispose from racing it.
        try { loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        stopping.Dispose();
        Connected = false;
    }

    private async Task Listen(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                // A restarted game is a new mod with a clock from zero; what the last one said must not refuse it.
                _link.BeginConnection();
                Connected = true;
                Problem = null;
                using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                    Take(line, _link.NowMs);
            }
            catch (OperationCanceledException) { break; }
            // A mod that dies mid-line, the name already taken by another visualizer, or one made by another account: say
            // so and try again later, never spin on it and never stop listening for good.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Problem = error.Message + " Another visualizer may already have the pipe.";
                try { await Task.Delay(BusyDelayMs, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally { Connected = false; }
        }
    }

    /// <summary>One line from the mod. Whatever is in it, it costs that line at most, never the connection.</summary>
    private void Take(string line, double arrivedMs)
    {
        if (line.Length == 0) return;
        try
        {
            if (ParseMod(line) is { } mod) _link.SetMod(mod);
            else if (ParseLevel(line) is { } level) _link.SetLevel(level);
            else if (Parse(line) is { } message) _link.Add(message, arrivedMs);
            else _link.Refuse("a line was not a reading this app understands");
        }
        catch (ArgumentException) { _link.Refuse("a line held a value this app cannot take"); }
    }

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
            return new GameSyncMessage((int)protocol, clock, position, state, paused, floor, speed, path, pitch, input, visual, dead,
                noFail, judgments);
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
