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
/// members share a display time. Display only; notes and timing are never changed.
/// </summary>
public static class MagicCircleFinder
{
    public static IReadOnlyList<MagicCircleSection> Find(IReadOnlyList<VisualizerNote> notes, VisualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.MagicCircleJudgmentLine) return [];
        var display = new List<double>();
        foreach (var note in notes)
            if (display.Count == 0 || note.HitTimeMs != display[^1]) display.Add(note.HitTimeMs);
        return Find(display, settings);
    }

    /// <summary>The same runs from display times that are already one per chord group (used while notes are being prepared).</summary>
    public static IReadOnlyList<MagicCircleSection> Find(IReadOnlyList<double> times, VisualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(settings);
        var required = Math.Max(VisualizerSettings.MinimumMagicCircleNoteCount, settings.MagicCircleNoteCount);
        var tolerance = VisualizerSettings.MagicCircleIntervalToleranceMs;
        var sections = new List<MagicCircleSection>();
        var start = 0;
        for (var i = 1; i < times.Count; i++)
        {
            // The run keeps going while every interval stays within the tolerance of the run's first one.
            var interval = times[start + 1] - times[start];
            var last = i + 1 < times.Count && Math.Abs(times[i + 1] - times[i] - interval) <= tolerance ? -1 : i;
            if (last < 0) continue;
            var count = last - start + 1;
            // Only fast runs count: an interval at or above the maximum is not a magic circle (user rule).
            if (count >= required && interval > 0 && interval < settings.MagicCircleMaxIntervalMs)
                sections.Add(new(times[start], times[last], (times[last] - times[start]) / (count - 1)));
            start = last;
        }
        return sections.AsReadOnly();
    }
}
