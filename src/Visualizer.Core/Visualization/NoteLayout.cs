namespace Visualizer.Core.Visualization;

/// <summary>Visual-only offsets. Positive values delay display; raw timing is never modified.</summary>
public readonly record struct DisplayOffsets(double GlobalMs, double MapMs = 0)
{
    public double TotalMs => GlobalMs + MapMs;
}

public static class NoteLayout
{
    /// <summary>Note height at scale 1.0.</summary>
    public const double BaseThicknessDip = 18;

    public static double Progress(double hitTimeMs, double elapsedMs, double travelTimeMs, DisplayOffsets offsets)
    {
        if (!double.IsFinite(offsets.TotalMs)) throw new ArgumentOutOfRangeException(nameof(offsets));
        return NotePosition.Progress(hitTimeMs + offsets.TotalMs, elapsedMs, travelTimeMs);
    }

    public static double ThicknessDip(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(BaseThicknessDip * scale))
            throw new ArgumentOutOfRangeException(nameof(scale));
        return BaseThicknessDip * scale;
    }
}

/// <summary>
/// Vertical note-field layout. Notes reach the judgment line at progress 1, keep falling at the
/// same speed, and are removed only once they have fully passed the bottom removal line.
/// </summary>
public readonly record struct NoteFieldGeometry(double JudgmentY, double RemovalY)
{
    /// <summary>Keeps enough travel distance above the judgment line on short windows.</summary>
    public const double MaximumGapRatio = 0.6;

    public static NoteFieldGeometry Create(double noteAreaHeight, double judgmentLineHeight)
    {
        if (!double.IsFinite(noteAreaHeight) || noteAreaHeight <= 0) throw new ArgumentOutOfRangeException(nameof(noteAreaHeight));
        if (!double.IsFinite(judgmentLineHeight) || judgmentLineHeight < 0) throw new ArgumentOutOfRangeException(nameof(judgmentLineHeight));
        var gap = Math.Min(judgmentLineHeight, noteAreaHeight * MaximumGapRatio);
        return new(noteAreaHeight - gap, noteAreaHeight);
    }

    public double CenterY(double progress) => progress * JudgmentY;

    public bool IsRemoved(double centerY, double halfThickness) => centerY - halfThickness >= RemovalY;

    /// <summary>Display-time range (hit + offsets) of notes that can overlap the field.</summary>
    public (double EarliestMs, double LatestMs) VisibleWindow(double timelineMs, double travelTimeMs, double halfThickness) =>
        (timelineMs + travelTimeMs * (1 - (RemovalY + halfThickness) / JudgmentY),
         timelineMs + travelTimeMs * (1 + halfThickness / JudgmentY));
}
