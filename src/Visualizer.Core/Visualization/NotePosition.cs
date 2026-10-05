namespace Visualizer.Core.Visualization;

public static class NotePosition
{
    /// <summary>0 = top at hit-travel; 1 = judgment line at hit. No frame accumulation.</summary>
    public static double Progress(double hitTimeMs, double timelineTimeMs, double travelTimeMs)
    {
        if (!double.IsFinite(hitTimeMs) || !double.IsFinite(timelineTimeMs)
            || !double.IsFinite(travelTimeMs) || travelTimeMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(travelTimeMs));
        return 1 - (hitTimeMs - timelineTimeMs) / travelTimeMs;
    }
}
