namespace Visualizer.Core.Maps;

public sealed record TimelineCheckpoint(int Number, int HitEventIndex,
    double? ActivationTimeMs = null, int? SourceFloor = null, int? RespawnFloor = null);

/// <summary>
/// A stretch the game plays by itself and the player presses nothing: the free roam mini-game. Times are hit times of the
/// same timeline, so they move with the notes when the practice speed or the start changes.
/// </summary>
public sealed record TimelineSection(double StartMs, double EndMs);

/// <summary>
/// How the player plays a hit. <see cref="Tap"/>, <see cref="HoldStart"/> and <see cref="HoldChain"/> hits are pressed and
/// become notes.
/// </summary>
public enum HitKind
{
    Tap = 0,
    /// <summary>Pressed, then held until the next hit, which is a <see cref="HoldEnd"/> or a <see cref="HoldChain"/>.</summary>
    HoldStart = 1,
    /// <summary>Played by releasing the held key (scrPlayer.UpdateHoldBehavior); not a separate note.</summary>
    HoldEnd = 2,
    /// <summary>Played by the game (AutoPlayTiles); never shown as a note.</summary>
    Auto = 3,
    /// <summary>
    /// Two holds in a row: this hit ends the one before it and starts the next, and it is pressed. The game takes a key
    /// press of its own here: scrPlayer.UpdateHoldKeys consumes the queued press as soon as _nextTileIsHold is set
    /// (currFloor.nextfloor.holdLength > -1), without waiting for the running hold's holdCompletion
    /// (cosMo@暴走P - THE END OF HATSUNE MIKU).
    /// </summary>
    HoldChain = 4
}

/// <summary>Immutable, settings-independent timing. Never contains lanes or visual offsets.</summary>
public sealed class ParsedTimeline
{
    public IReadOnlyList<double> HitTimesMs { get; }
    public IReadOnlyList<TimelineCheckpoint> Checkpoints { get; }
    public bool CheckpointsVerified { get; }
    /// <summary>Game floor number of each hit; null for synthetic timelines and caches written before tile numbers.</summary>
    public IReadOnlyList<int>? HitFloors { get; }
    /// <summary>How each hit is played; null means every hit is a tap (synthetic timelines and caches written before hit kinds).</summary>
    public IReadOnlyList<HitKind>? HitKinds { get; }
    /// <summary>Stretches with no notes that the game plays itself (free roam), in hit time; empty for most songs.</summary>
    public IReadOnlyList<TimelineSection> Sections { get; }
    /// <summary>
    /// The real tempo each hit is reached at: the beats a minute of the tile the planet turns on before it (the game grades
    /// with conductor.bpm × planetarySystem.speed) times the song's pitch. Null for synthetic timelines and older caches.
    /// Only the hit windows read it (<see cref="HitGrading"/>); it moves no hit time.
    /// </summary>
    public IReadOnlyList<double>? HitTempos { get; }
    /// <summary>The hit tile's margin scale (ScaleMargin, 1 = 100%), or null when every hit has 1 or it is not known.</summary>
    public IReadOnlyList<double>? HitMarginScales { get; }

    public ParsedTimeline(IEnumerable<double> hitTimesMs, IEnumerable<TimelineCheckpoint>? checkpoints = null,
        bool checkpointsVerified = false, IEnumerable<int>? hitFloors = null, IEnumerable<HitKind>? hitKinds = null,
        IEnumerable<TimelineSection>? sections = null, IEnumerable<double>? hitTempos = null, IEnumerable<double>? hitMarginScales = null)
    {
        var times = hitTimesMs.ToArray();
        if (times.Any(t => !double.IsFinite(t) || t < 0)
            || times.Zip(times.Skip(1)).Any(pair => pair.First > pair.Second))
            throw new ArgumentException("Timeline times must be finite, nonnegative and nondecreasing.");
        var markers = checkpoints?.ToArray() ?? [];
        if (!checkpointsVerified && markers.Length != 0)
            throw new ArgumentException("Unverified checkpoint markers cannot be used.");
        if (checkpointsVerified && (markers.Length == 0 || markers[0] != new TimelineCheckpoint(1, 0)
            || markers.Any(c => c.Number < 1 || c.HitEventIndex < 0 || c.HitEventIndex >= times.Length)
            || markers.Any(c => c.ActivationTimeMs is double at && (!double.IsFinite(at) || at < 0)
                || c.SourceFloor is < 0 || c.RespawnFloor is < 0)
            || markers.Zip(markers.Skip(1)).Any(p => p.First.Number >= p.Second.Number || p.First.HitEventIndex >= p.Second.HitEventIndex)))
            throw new ArgumentException("Verified checkpoints must start at checkpoint 1 / hit 0 and be strictly ordered.");
        var floors = hitFloors?.ToArray();
        if (floors is not null && (floors.Length != times.Length || floors.Any(floor => floor < 1)
            || floors.Zip(floors.Skip(1)).Any(pair => pair.First >= pair.Second)))
            throw new ArgumentException("Hit floors must match the hits, start at floor 1 or later and strictly increase.");
        var kinds = hitKinds?.ToArray();
        if (kinds is not null && (kinds.Length != times.Length || kinds.Any(kind => !Enum.IsDefined(kind))
            || Enumerable.Range(0, kinds.Length).Any(i => Opens(kinds[i]) != (i + 1 < kinds.Length && Closes(kinds[i + 1])))
            || Enumerable.Range(0, kinds.Length).Any(i => Closes(kinds[i]) && (i == 0 || !Opens(kinds[i - 1])))
            || markers.Any(c => Closes(kinds[c.HitEventIndex]))))
            throw new ArgumentException("Hit kinds must match the hits, pair every hold start with the next hit as its end, and never restart on a hold end.");
        HitTimesMs = Array.AsReadOnly(times);
        Checkpoints = Array.AsReadOnly(markers);
        CheckpointsVerified = checkpointsVerified;
        HitFloors = floors is null ? null : Array.AsReadOnly(floors);
        HitKinds = kinds is null ? null : Array.AsReadOnly(kinds);
        var bands = sections?.ToArray() ?? [];
        if (bands.Any(band => !double.IsFinite(band.StartMs) || !double.IsFinite(band.EndMs) || band.StartMs < 0 || band.EndMs <= band.StartMs)
            || bands.Zip(bands.Skip(1)).Any(pair => pair.First.EndMs > pair.Second.StartMs))
            throw new ArgumentException("Sections must be finite, forward and in order.");
        Sections = Array.AsReadOnly(bands);
        HitTempos = PerHit(hitTempos, times.Length, zeroAllowed: false, "Hit tempos");
        // A margin scale of zero leaves only the minimum times, which the game allows.
        HitMarginScales = PerHit(hitMarginScales, times.Length, zeroAllowed: true, "Hit margin scales");
    }

    private static IReadOnlyList<double>? PerHit(IEnumerable<double>? values, int hits, bool zeroAllowed, string what)
    {
        var array = values?.ToArray();
        if (array is null) return null;
        if (array.Length != hits || array.Any(value => !double.IsFinite(value) || value < 0 || (value == 0 && !zeroAllowed)))
            throw new ArgumentException(what + " must match the hits and be finite and positive.");
        return Array.AsReadOnly(array);
    }

    /// <summary>
    /// The game's windows for hit <paramref name="index"/> at <paramref name="difficulty"/>, with the song played at
    /// <paramref name="rate"/> (1 = 100%) and the minimum times eased by <paramref name="ease"/>. A timeline without tempos
    /// gets the difficulty's fallback windows.
    /// </summary>
    public HitWindows WindowsOf(int index, int difficulty, double rate = 1, double ease = 1)
    {
        if ((uint)index >= (uint)HitTimesMs.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (!double.IsFinite(rate) || rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
        return HitTempos is { } tempos
            ? HitGrading.Windows(tempos[index] * rate, HitMarginScales?[index] ?? 1, difficulty, ease)
            : HitGrading.Fallback(difficulty, ease);
    }

    /// <summary>Checkpoints the map places. Checkpoint 1 is the start of the song, so it is not counted.</summary>
    public int CheckpointCount => Math.Max(0, Checkpoints.Count - 1);

    public HitKind KindOf(int index) => HitKinds?[index] ?? HitKind.Tap;

    /// <summary>Whether the player presses this hit (a tap or the start of a hold).</summary>
    public bool IsPressed(int index) => KindOf(index) is HitKind.Tap or HitKind.HoldStart or HitKind.HoldChain;

    /// <summary>Whether a hit begins a long note: a start, or a chain hit that ends one and begins the next.</summary>
    public static bool Opens(HitKind kind) => kind is HitKind.HoldStart or HitKind.HoldChain;

    /// <summary>Whether a hit ends the long note before it.</summary>
    public static bool Closes(HitKind kind) => kind is HitKind.HoldEnd or HitKind.HoldChain;

    /// <summary>Hits of one kind. A timeline without kinds holds only taps.</summary>
    public int CountHits(HitKind kind) =>
        HitKinds is { } kinds ? kinds.Count(hit => hit == kind) : kind == HitKind.Tap ? HitTimesMs.Count : 0;

    /// <summary>Pressed hits from <paramref name="fromIndex"/> to the end.</summary>
    public int CountPressedHits(int fromIndex = 0)
    {
        var count = 0;
        for (var i = Math.Max(0, fromIndex); i < HitTimesMs.Count; i++)
            if (IsPressed(i)) count++;
        return count;
    }

    public int GetStartIndex(int checkpoint)
    {
        if (checkpoint == 1) return 0;
        if (!CheckpointsVerified) throw new InvalidOperationException("이 맵의 체크포인트 위치는 검증되지 않았습니다.");
        return Checkpoints.FirstOrDefault(c => c.Number == checkpoint)?.HitEventIndex
            ?? throw new ArgumentOutOfRangeException(nameof(checkpoint), "해당 체크포인트가 없습니다.");
    }

    /// <summary>
    /// Practice-mode start. The game stores the chosen tile in the same restart slot as checkpoints
    /// (PracticeTimeline.SetPositions → GCS.checkpointNum), so play begins with the first hit after that tile.
    /// </summary>
    public int GetStartIndexForTile(int tile)
    {
        if (HitFloors is not { Count: > 0 } floors)
            throw new InvalidOperationException("타일 번호가 없는 타임라인입니다. 맵 동기화 후 곡을 다시 불러오세요.");
        if (tile < 0 || tile >= floors[^1])
            throw new ArgumentOutOfRangeException(nameof(tile), $"시작 타일은 0~{floors[^1] - 1} 사이여야 합니다.");
        var low = 0;
        var high = floors.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (floors[middle] <= tile) low = middle + 1;
            else high = middle;
        }
        // A positive tile that is not a hit floor is a midspin; restarting there is unverified (as for checkpoints).
        if (tile > 0 && (low == 0 || floors[low - 1] != tile))
            throw new ArgumentOutOfRangeException(nameof(tile), $"타일 {tile}은(는) 중간 회전 타일이라 시작 위치가 검증되지 않았습니다.");
        // The first hit after a hold tile is played by releasing a key that was never pressed; that restart is unverified.
        if (low < floors.Count && Closes(KindOf(low)))
            throw new ArgumentOutOfRangeException(nameof(tile), $"타일 {tile}은(는) 롱노트 도중이라 시작 위치가 검증되지 않았습니다.");
        return low;
    }
}
