namespace Visualizer.Application.Playback;

/// <summary>Explicit synthetic UI fixture, never derived from a loaded map.</summary>
public static class RulePreviewTimeline
{
    /// <summary>How far apart the two notes of a preview chord are: inside any chord window the settings allow.</summary>
    private const double ChordGapMs = 15;

    /// <summary>
    /// Slow to fast and back, then a long run of one interval so the magic circle timing bar can be tried, and a few
    /// two-note chords at the end. The chords are left out when only one lane is allowed, since the point of a preview is
    /// to show the rules working rather than to fail to place them.
    /// </summary>
    public static IReadOnlyList<double> Create(int maximumTiles)
    {
        var times = new List<double> { 0 };
        foreach (var interval in new[] { 650d, 120d, 30d, 10d, 30d, 120d, 650d })
            for (var i = 0; i < 24; i++) times.Add(times[^1] + interval);
        for (var i = 0; i < 48; i++) times.Add(times[^1] + 120);
        if (maximumTiles < 2) return times.AsReadOnly();
        for (var i = 0; i < 6; i++)
        {
            times.Add(times[^1] + 500);
            times.Add(times[^1] + ChordGapMs);
        }
        return times.AsReadOnly();
    }
}
