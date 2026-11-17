using Visualizer.Core.Maps;

namespace Visualizer.Application.Sync;

/// <summary>
/// Turns the judgment counts the linker sends (scrMarginTracker.hitMarginsCount) into the grades the game gave since the
/// last counts, so the judgment bar can show the game's own grade of each press. Reads nothing from the game itself.
/// </summary>
public sealed class GameGrades
{
    /// <summary>The HitMargin order before the game build of 2026-11-04 (12 values), which had one Perfect in the middle.</summary>
    private static readonly string[] OldOrder =
        ["TooEarly", "VeryEarly", "EarlyPerfect", "Perfect", "LatePerfect", "VeryLate", "TooLate", "Multipress", "FailMiss",
         "FailOverload", "Auto", "OverPress"];

    private IReadOnlyList<int>? _last;

    /// <summary>
    /// The grade a count index stands for: by the names the linker sent, or by the count's length for an older linker that
    /// sends none (12 is the order before 2026-11-04, 16 the one after, which is <see cref="HitGrade"/>'s own). Null for
    /// what is not a press grade (a miss, an overload, auto) or an order this app does not know.
    /// </summary>
    public static HitGrade? GradeAt(int index, int count, IReadOnlyList<string>? names)
    {
        if (index < 0) return null;
        if (names is not null) return index < names.Count ? GradeNamed(names[index]) : null;
        if (count == OldOrder.Length) return index < OldOrder.Length ? GradeNamed(OldOrder[index]) : null;
        if (count == 16) return index <= (int)HitGrade.TooLate ? (HitGrade)index : null;
        return null;
    }

    /// <summary>
    /// A grade by the game's name for it. The one Perfect of the older order stands for the middle, as XPerfect does now;
    /// only the colour of the mark depends on it.
    /// </summary>
    public static HitGrade? GradeNamed(string name) =>
        name == "Perfect" ? HitGrade.XPerfect
        : Enum.TryParse<HitGrade>(name, ignoreCase: false, out var grade) && Enum.IsDefined(grade) && name == grade.ToString() ? grade : null;

    /// <summary>
    /// The grades given since the counts last taken, earliest grade first; empty for the first counts, unchanged ones, or
    /// counts that went down (the game started over and counts from zero again).
    /// </summary>
    public IReadOnlyList<HitGrade> Take(IReadOnlyList<int>? counts, IReadOnlyList<string>? names)
    {
        if (counts is null || ReferenceEquals(counts, _last)) return [];
        var before = _last;
        _last = counts;
        if (before is null || before.Count != counts.Count) return [];
        for (var index = 0; index < counts.Count; index++)
            if (counts[index] < before[index]) return [];
        var grades = new List<HitGrade>();
        for (var index = 0; index < counts.Count; index++)
            if (counts[index] > before[index] && GradeAt(index, counts.Count, names) is { } grade)
                for (var added = before[index]; added < counts[index] && grades.Count < 64; added++) grades.Add(grade);
        grades.Sort();
        return grades;
    }

    /// <summary>Forgets the last counts: a new connection counts from its own first reading.</summary>
    public void Reset() => _last = null;
}
