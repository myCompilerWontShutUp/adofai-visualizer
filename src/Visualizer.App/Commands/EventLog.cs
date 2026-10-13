namespace Visualizer.App.Commands;

/// <summary>
/// What happened while the app ran, for the console's log (user request: a way to read afterwards why the notes went
/// missing during play). Kept in memory only, the newest <see cref="Capacity"/> lines, and written into a note only when
/// report -log is asked to. No key that is pressed is ever kept (AGENTS.md rule 5).
/// </summary>
public sealed class EventLog
{
    /// <summary>The most lines kept; the oldest go first.</summary>
    public const int Capacity = 5000;

    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();
    private readonly Func<DateTime> _now;

    /// <param name="now">The clock the lines are stamped with; tests hand in their own.</param>
    public EventLog(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.Now);

    public int Count { get { lock (_gate) return _lines.Count; } }

    /// <summary>Adds one line, stamped with the time of day to the millisecond.</summary>
    public void Add(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = _now().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "  " + text;
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > Capacity) _lines.Dequeue();
        }
    }

    /// <summary>Every line kept, oldest first.</summary>
    public IReadOnlyList<string> Lines() { lock (_gate) return _lines.ToArray(); }

    public void Clear() { lock (_gate) _lines.Clear(); }
}
