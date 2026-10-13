using Visualizer.Core.Settings;

namespace Visualizer.Core.Visualization;

/// <summary>A run of notes that keep the same interval, in prepared (display) time.</summary>
public readonly record struct MagicCircleSection(double StartMs, double EndMs, double IntervalMs)
{
    public bool Contains(double timeMs) => timeMs >= StartMs && timeMs <= EndMs;
}

/// <summary>
/// Finds magic circle sections: at least <see cref="VisualizerSettings.MagicCircleNoteCount"/> notes in a row whose intervals stay
/// within <see cref="VisualizerSettings.MagicCircleIntervalToleranceMs"/> of each other. A chord counts as one note, since its
/// members share a display time; with <see cref="VisualizerSettings.MagicCircleChords"/> off, chords and singles never share a
/// run (user rule). Display only; notes and timing are never changed.
/// </summary>
public static class MagicCircleFinder
{
    public static IReadOnlyList<MagicCircleSection> Find(IReadOnlyList<VisualizerNote> notes, VisualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.MagicCircleJudgmentLine) return [];
        var display = new List<double>();
        var chords = new List<bool>();
        foreach (var note in notes)
        {
            if (display.Count == 0 || note.HitTimeMs != display[^1])
            {
                display.Add(note.HitTimeMs);
                chords.Add(note.StartsChord);
            }
            // A second note at the same display time makes it a chord, whatever the planner split it into.
            else chords[^1] = true;
        }
        return Find(display, settings, chords);
    }

    /// <summary>
    /// The same runs from display times that are already one per chord group (used while notes are being prepared).
    /// <paramref name="chords"/> marks the times that are chords, one per time; without it every time counts the same.
    /// </summary>
    public static IReadOnlyList<MagicCircleSection> Find(IReadOnlyList<double> times, VisualizerSettings settings,
        IReadOnlyList<bool>? chords = null)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(settings);
        if (chords is not null && chords.Count != times.Count)
            throw new ArgumentException("Chord marks must match the times.", nameof(chords));
        var sections = new List<MagicCircleSection>();
        if (settings.MagicCircleChords || chords is null) AddRuns(times, 0, times.Count, settings, sections);
        else
        {
            // Chords and singles never share a run: each stretch of one kind is searched on its own.
            for (var from = 0; from < times.Count;)
            {
                var to = from + 1;
                while (to < times.Count && chords[to] == chords[from]) to++;
                AddRuns(times, from, to, settings, sections);
                from = to;
            }
        }
        return sections.AsReadOnly();
    }

    private static void AddRuns(IReadOnlyList<double> times, int from, int to, VisualizerSettings settings, List<MagicCircleSection> sections)
    {
        var required = Math.Max(VisualizerSettings.MinimumMagicCircleNoteCount, settings.MagicCircleNoteCount);
        var tolerance = VisualizerSettings.MagicCircleIntervalToleranceMs;
        var start = from;
        for (var i = from + 1; i < to; i++)
        {
            // The run keeps going while every interval stays within the tolerance of the run's first one.
            var interval = times[start + 1] - times[start];
            var last = i + 1 < to && Math.Abs(times[i + 1] - times[i] - interval) <= tolerance ? -1 : i;
            if (last < 0) continue;
            var count = last - start + 1;
            // Only fast runs count: an interval at or above the maximum is not a magic circle (user rule).
            if (count >= required && interval > 0 && interval < settings.MagicCircleMaxIntervalMs)
                sections.Add(new(times[start], times[last], (times[last] - times[start]) / (count - 1)));
            start = last;
        }
    }
}
