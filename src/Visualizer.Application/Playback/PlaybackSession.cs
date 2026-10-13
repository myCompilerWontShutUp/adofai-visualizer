using Visualizer.Core.Localization;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using Visualizer.Core.Maps;

namespace Visualizer.Application.Playback;

public interface IMonotonicClock
{
    double NowMs { get; }
}

public enum PlaybackState { Off, NoTimeline, Waiting, Playing, Paused }

/// <summary>Reset prepares all notes; Start changes only the clock anchor and state.</summary>
public sealed class PlaybackSession(IMonotonicClock clock)
{
    private ParsedTimeline? _timeline;
    private double _startedAtMs;
    private double _pausedAtMs;
    private double _originMs;
    private bool _enabled;
    private int _resetCheckpoint = 1;
    private int _startHitIndex;
    private int? _startTile;
    // Source hit index of each prepared note (auto hits and hold ends have no note), and the source time of the first note.
    private int[] _noteHits = [];
    /// <summary>A hand-made layout for the loaded song; null means the rules decide the lanes as usual.</summary>
    public Library.NoteEdit? Layout { get; set; }
    private double _noteOriginMs;
    private readonly List<double> _calibration = [];
    private bool _calibrationDone;

    public PlaybackState State { get; private set; } = PlaybackState.Off;
    public IReadOnlyList<VisualizerNote> Notes { get; private set; } = Array.Empty<VisualizerNote>();
    public VisualizerSettings Settings { get; private set; } = new();
    /// <summary>Practice-style start tile, or null when playback starts from a checkpoint.</summary>
    public int? StartTile => _startTile;
    /// <summary>
    /// The lane (logical tile) whose key starts playback instead of the start key, or null while the start key does it.
    /// A start inside the song (a later checkpoint or a practice section) listens to its first note's lane, and so does a
    /// song whose first note does not sit on the initial tile: the start key presses its own lane as it starts, so with
    /// the first note moved elsewhere — an edit can move it — that note could never be played in time (user report: the
    /// first tile moved from J to K and showed a miss however well it was pressed).
    /// </summary>
    public int? LaneStartTile => Notes.Count > 0 && (StartsInsideSong || Notes[0].Tile != Settings.InitialTile)
        ? Notes[0].Tile : null;

    private bool StartsInsideSong => _startTile is not null || _startHitIndex > 0;

    /// <summary>Whether <paramref name="tile"/> is a lane of the first prepared (possibly chorded) note that starts playback.</summary>
    public bool StartsOnLane(int tile)
    {
        if (LaneStartTile is null || Notes.Count == 0) return false;
        var first = Notes[0].HitTimeMs;
        for (var i = 0; i < Notes.Count && Notes[i].HitTimeMs == first; i++)
            if (Notes[i].Tile == tile) return true;
        return false;
    }
    /// <summary>Index of the first prepared hit in the source timeline (checkpoint or practice start).</summary>
    public int StartHitIndex => _startHitIndex;
    public double TimelineTimeMs => State == PlaybackState.Playing ? clock.NowMs - _startedAtMs
        : State == PlaybackState.Paused ? _pausedAtMs : 0;
    public double StartWaitRemainingMs => Math.Max(0, Settings.StartDelayMs - TimelineTimeMs);

    /// <summary>
    /// How long the last note has been gone, or null while one is still to come (and while nothing is playing). The screen
    /// uses it to say the song is over: a long note counts until it is released.
    /// </summary>
    public double? PastLastNoteMs => State == PlaybackState.Playing && Notes.Count > 0 && NextHitRemainingMs is null
        ? TimelineTimeMs - Math.Max(Notes[^1].HitTimeMs, Notes[^1].HoldEndMs ?? Notes[^1].HitTimeMs)
        : null;
    public double? NextHitRemainingMs
    {
        get
        {
            var now = TimelineTimeMs;
            var low = 0;
            var high = Notes.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (Notes[middle].HitTimeMs <= now) low = middle + 1;
                else high = middle;
            }
            return low < Notes.Count ? Notes[low].HitTimeMs - now : null;
        }
    }
    public ParsedTimeline? Timeline => _timeline;
    public double MapDisplayOffsetMs { get; private set; }
    /// <summary>Display offset from the press calibration of this play; cleared whenever notes are prepared again.</summary>
    public double CalibrationOffsetMs { get; private set; }
    public DisplayOffsets Offsets =>
        new(Settings.GlobalDisplayOffsetMs + (ClockFollowsGame ? 0 : CalibrationOffsetMs), MapDisplayOffsetMs);
    /// <summary>Marks of the lane key presses, when one is watching this session. Used to know a checkpoint note was hit.</summary>
    public NotePressJudge? Presses { get; set; }
    /// <summary>
    /// Whether the game's clock is driving the playhead. While it is, this session must not move the song on its own:
    /// the checkpoint the game restarts from is the game's business, and advancing the app's own choice would change
    /// where the next reset prepares from.
    /// </summary>
    public bool ClockFollowsGame { get; set; }

    /// <summary>
    /// Notes before this (in note time, as <see cref="VisualizerNote.HitTimeMs"/> is) are not drawn. A checkpoint restart
    /// rewinds the song to before its tile, so the tiles leading up to it would fall although the game will not play them.
    /// </summary>
    public double HideNotesBeforeMs { get; set; } = double.NegativeInfinity;

    /// <summary>
    /// How many times notes have been prepared here, counting the attempts that failed. Callers that may do a thing once
    /// per preparation (the console's automatic game follow) watch this: a count of notes would miss a preparation that
    /// ends with the same notes, such as one after a speed change.
    /// </summary>
    public int Prepared { get; private set; }

    public bool AutoCheckpointActive => Settings.AutoCheckpointAdvance && Settings.PlaybackSpeedPercent == 100
        && _startTile is null && !ClockFollowsGame;
    public int? ActiveCheckpoint => _timeline is { CheckpointsVerified: true } timeline
        ? State is not (PlaybackState.Playing or PlaybackState.Paused) || !AutoCheckpointActive ? _resetCheckpoint
            : timeline.Checkpoints.Where(c => c.Number > _resetCheckpoint
                && (c.ActivationTimeMs ?? timeline.HitTimesMs[c.HitEventIndex]) >= _originMs && CheckpointReached(c))
                .Select(c => c.Number).DefaultIfEmpty(_resetCheckpoint).Max()
        : null;

    /// <summary>
    /// A checkpoint is taken when its note is pressed, or when that note's press window ends (user rule).
    /// </summary>
    private bool CheckpointReached(TimelineCheckpoint checkpoint)
    {
        var arrival = CheckpointArrivalMs(checkpoint, out var note);
        return TimelineTimeMs >= arrival + Settings.KeyPressWindowMs
            || (note >= 0 && Presses?.IsPressed(Notes, note) == true);
    }

    /// <summary>
    /// One press deviation (press time minus the note's display time). Once <see cref="VisualizerSettings.CalibrationNoteCount"/>
    /// presses are in, their average shifts the display once, never further than the press window. Visual only.
    /// </summary>
    public void AddCalibrationSample(double deviationMs)
    {
        // Following the game, the lane keys are presses against the game, not against this display, and the game's own
        // calibration is already in the position it sends. The value so far is kept for when following stops.
        if (ClockFollowsGame || _calibrationDone || State != PlaybackState.Playing || !double.IsFinite(deviationMs)) return;
        _calibration.Add(deviationMs);
        if (_calibration.Count < Settings.CalibrationNoteCount) return;
        CalibrationOffsetMs = Math.Clamp(_calibration.Average(), -Settings.KeyPressWindowMs, Settings.KeyPressWindowMs);
        _calibrationDone = true;
    }

    private double CheckpointArrivalMs(TimelineCheckpoint checkpoint) => CheckpointArrivalMs(checkpoint, out _);

    /// <summary>
    /// When the checkpoint tile is displayed, and the prepared note it belongs to (-1 when that hit has no note of its own).
    /// </summary>
    private double CheckpointArrivalMs(TimelineCheckpoint checkpoint, out int noteIndex)
    {
        noteIndex = -1;
        var times = _timeline!.HitTimesMs;
        var at = checkpoint.ActivationTimeMs ?? times[checkpoint.HitEventIndex];
        var low = _startHitIndex;
        var high = times.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (times[middle] < at) low = middle + 1;
            else high = middle;
        }
        // Use the prepared display hit for chord-aligned source tiles; otherwise
        // convert the verified marker time. Travel duration is already before arrival.
        var note = low < times.Count && times[low] == at ? Array.BinarySearch(_noteHits, low) : -1;
        if (note >= 0 && note < Notes.Count) noteIndex = note;
        var arrival = noteIndex >= 0
            ? Notes[noteIndex].HitTimeMs
            : (at - _noteOriginMs) / (Settings.PlaybackSpeedPercent / 100) + Settings.StartDelayMs;
        return arrival + Settings.GlobalDisplayOffsetMs + MapDisplayOffsetMs;
    }

    /// <summary>
    /// The free roam band the song is in at <paramref name="timelineMs"/>, in playback time (the start, the practice speed
    /// and the display offsets applied as they are for notes), or null. The mini-game has no notes, so the screen says so.
    /// </summary>
    public (double StartMs, double EndMs)? SectionAt(double timelineMs)
    {
        if (_timeline is not { } timeline || timeline.Sections.Count == 0 || !double.IsFinite(timelineMs)) return null;
        var rate = Settings.PlaybackSpeedPercent / 100;
        var shift = Settings.StartDelayMs + Offsets.TotalMs;
        foreach (var section in timeline.Sections)
        {
            var start = (section.StartMs - _noteOriginMs) / rate + shift;
            var end = (section.EndMs - _noteOriginMs) / rate + shift;
            if (timelineMs >= start && timelineMs < end) return (start, end);
        }
        return null;
    }

    /// <summary>
    /// Moves a paused song to <paramref name="timelineMs"/> without starting it, so the next note can wait on the judgment
    /// line while the game is paused (user rule).
    /// </summary>
    public bool MovePaused(double timelineMs)
    {
        if (State != PlaybackState.Paused || !double.IsFinite(timelineMs)) return false;
        _pausedAtMs = timelineMs;
        return true;
    }

    public void LoadTimeline(IEnumerable<double> hitTimesMs, VisualizerSettings settings)
    {
        LoadParsedTimeline(new ParsedTimeline(hitTimesMs), settings);
    }

    public void LoadParsedTimeline(ParsedTimeline timeline, VisualizerSettings settings)
    {
        settings.EnsureValid();
        _timeline = timeline;
        _resetCheckpoint = 1;
        _originMs = 0;
        MapDisplayOffsetMs = 0;
        Reset(settings);
    }

    public void Reset(VisualizerSettings settings, int checkpoint = 1)
    {
        settings.EnsureValid();
        // Even an unsupported checkpoint request must stop the running clock.
        StopClock();
        if (checkpoint < 1) throw new ArgumentOutOfRangeException(nameof(checkpoint));
        var index = _timeline?.GetStartIndex(checkpoint) ?? 0;
        _startTile = null;
        // A checkpoint start slices a full play, like a practice section (user rule): its notes keep the lanes they have
        // when the song is played from the beginning, and its first note's lane key starts playback.
        Prepare(settings, index, checkpoint);
    }

    /// <summary>
    /// Prepares from the first hit after <paramref name="tile"/>, as the game's practice mode does. Lanes are
    /// the ones the whole song gets from its beginning; the section is not re-patterned from the Initial Tile.
    /// </summary>
    public void ResetAtTile(VisualizerSettings settings, int tile)
    {
        settings.EnsureValid();
        StopClock();
        if (_timeline is not { } timeline) throw new InvalidOperationException(Languages.Get(settings.Language, "Playback.NoSong"));
        var index = timeline.GetStartIndexForTile(tile);
        // Automatic checkpoint progress continues from the last restart point already behind this start.
        var checkpoint = timeline.CheckpointsVerified
            ? timeline.Checkpoints.Where(c => c.HitEventIndex <= index).Select(c => c.Number).DefaultIfEmpty(1).Max()
            : 1;
        _startTile = tile; // Kept even if note placement fails, so fixing settings re-prepares this tile.
        Prepare(settings, index, checkpoint);
    }

    /// <summary>Re-prepares the current start: the practice tile when one is set, otherwise the checkpoint.</summary>
    public void Restart(VisualizerSettings settings, int checkpoint)
    {
        if (_startTile is int tile) ResetAtTile(settings, tile);
        else Reset(settings, checkpoint);
    }

    public void ApplySettings(VisualizerSettings settings, int checkpoint = 1)
    {
        settings.EnsureValid();
        if (!double.IsFinite(settings.GlobalDisplayOffsetMs + MapDisplayOffsetMs))
            throw new ArgumentOutOfRangeException(nameof(settings), "Combined display offset must be finite.");
        if (!Settings.HasSameNoteRulesAs(settings) || (_timeline is not null && Notes.Count == 0)) Restart(settings, checkpoint);
        else Settings = settings; // Visual and control changes preserve the clock and note list.
    }

    /// <summary>Replaces settings that differ only in display (effects, colors) without touching notes, clock or state.</summary>
    public void UpdateVisualSettings(VisualizerSettings settings)
    {
        settings.EnsureValid();
        if (!Settings.HasSameNoteRulesAs(settings))
            throw new ArgumentException("Only visual settings can be previewed.", nameof(settings));
        Settings = settings;
    }

    public void SetMapDisplayOffset(double offsetMs)
    {
        if (!double.IsFinite(offsetMs) || !double.IsFinite(offsetMs + Settings.GlobalDisplayOffsetMs))
            throw new ArgumentOutOfRangeException(nameof(offsetMs));
        MapDisplayOffsetMs = offsetMs;
    }

    public void Toggle()
    {
        _enabled = !_enabled;
        if (!_enabled) State = PlaybackState.Off;
        else Restart(Settings, _resetCheckpoint);
    }

    public bool Start()
    {
        if (State is not (PlaybackState.Waiting or PlaybackState.Paused)) return false;
        _startedAtMs = clock.NowMs - (State == PlaybackState.Paused ? _pausedAtMs : 0);
        State = PlaybackState.Playing;
        return true;
    }

    /// <summary>
    /// Where a source hit time lands on this session's clock, display offsets included: the same conversion the prepared
    /// notes went through (checkpoint origin, practice speed, start wait). Game sync uses it to follow the game's clock.
    /// </summary>
    public double TimelineTimeOf(double sourceHitMs) => NoteTimeOf(sourceHitMs) + Offsets.TotalMs;

    /// <summary>Where a source hit time lands among the prepared notes, without the display offsets.</summary>
    public double NoteTimeOf(double sourceHitMs) =>
        (sourceHitMs - _noteOriginMs) / (Settings.PlaybackSpeedPercent / 100.0) + Settings.StartDelayMs;

    /// <summary>
    /// Moves the running clock so the playhead reads <paramref name="timelineMs"/> right now. Only the clock moves; the
    /// notes and their times are untouched. Game sync nudges it; nothing else should.
    /// </summary>
    public bool MoveClock(double timelineMs)
    {
        if (State != PlaybackState.Playing || !double.IsFinite(timelineMs)) return false;
        _startedAtMs = clock.NowMs - timelineMs;
        return true;
    }

    /// <summary>Resumes a paused session from <paramref name="timelineMs"/> instead of the paused point.</summary>
    public bool ResumeAt(double timelineMs)
    {
        if (State != PlaybackState.Paused || !double.IsFinite(timelineMs)) return false;
        _pausedAtMs = timelineMs;
        return Start();
    }

    public bool Pause()
    {
        if (State != PlaybackState.Playing) return false;
        _pausedAtMs = TimelineTimeMs;
        State = PlaybackState.Paused;
        return true;
    }

    public void Clear()
    {
        _timeline = null;
        Notes = Array.Empty<VisualizerNote>();
        _originMs = 0;
        _pausedAtMs = 0;
        _resetCheckpoint = 1;
        _startHitIndex = 0;
        _startTile = null;
        _noteHits = [];
        _noteOriginMs = 0;
        HideNotesBeforeMs = double.NegativeInfinity;
        _calibration.Clear();
        _calibrationDone = false;
        CalibrationOffsetMs = 0;
        MapDisplayOffsetMs = 0;
        State = _enabled ? PlaybackState.NoTimeline : PlaybackState.Off;
    }

    private void StopClock()
    {
        if (State is PlaybackState.Playing or PlaybackState.Paused) State = PlaybackState.Waiting;
        _pausedAtMs = 0;
    }

    /// <summary>
    /// Notes for hits played from the first one: times relative to it and scaled by the practice speed, before the start delay.
    /// One note per hit. Throws <see cref="NotePlacementException"/> when the hits cannot be placed.
    /// </summary>
    public static IReadOnlyList<VisualizerNote> GenerateSongNotes(IReadOnlyList<double> hitTimesMs, VisualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(hitTimesMs);
        return Generate(hitTimesMs, null, hitTimesMs.Count == 0 ? 0 : hitTimesMs[0], settings);
    }

    /// <summary>
    /// Notes for the pressed hits of a whole song (taps and long note starts; auto hits and hold ends have none, user rule).
    /// <paramref name="noteHits"/> is the source hit index of each note.
    /// </summary>
    public static IReadOnlyList<VisualizerNote> GenerateSongNotes(ParsedTimeline timeline, VisualizerSettings settings, out IReadOnlyList<int> noteHits)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var times = timeline.HitTimesMs;
        var pressed = Enumerable.Range(0, times.Count).Where(timeline.IsPressed).ToArray();
        noteHits = Array.AsReadOnly(pressed);
        var holdEnds = pressed.Select(i => ParsedTimeline.Opens(timeline.KindOf(i)) ? times[i + 1] : (double?)null).ToArray();
        return Generate(pressed.Select(i => times[i]).ToArray(), holdEnds, times.Count == 0 ? 0 : times[0], settings);
    }

    private static IReadOnlyList<VisualizerNote> Generate(IReadOnlyList<double> hitTimesMs, IReadOnlyList<double?>? holdEndsMs, double originMs,
        VisualizerSettings settings)
    {
        settings.EnsureValid();
        var rate = settings.PlaybackSpeedPercent / 100;
        var scaledTimes = hitTimesMs.Select(t => (t - originMs) / rate).ToArray();
        var scaledEnds = holdEndsMs?.Select(end => end is double value ? (value - originMs) / rate : (double?)null).ToArray();
        static bool OutOfRange(double t, VisualizerSettings s) => !double.IsFinite(t) || !double.IsFinite(t + s.StartDelayMs);
        if (scaledTimes.Any(t => OutOfRange(t, settings)) || scaledEnds?.Any(end => end is double value && OutOfRange(value, settings)) == true)
            throw new NotePlacementException(Languages.Get(settings.Language, "Playback.TimingRange"));
        // Thresholds and chords operate in real playback milliseconds. Delay is
        // a common shift added afterwards, and never changes group membership.
        return new NoteGenerator().Generate(scaledTimes, scaledEnds, settings);
    }

    private void Prepare(VisualizerSettings settings, int index, int checkpoint)
    {
        Prepared++;
        var times = _timeline?.HitTimesMs ?? Array.Empty<double>();
        var origin = times.Count == 0 ? 0 : times[index];
        IReadOnlyList<VisualizerNote> notes;
        int[] noteHits = [];
        var noteOrigin = origin;
        try
        {
            // A later start generates from the first hit and slices, so its lanes match a full play.
            IReadOnlyList<VisualizerNote> generated = [];
            IReadOnlyList<int> hits = [];
            if (_timeline is { } timeline) generated = GenerateSongNotes(timeline, settings, out hits);
            // A saved edit replaces the lanes and the chords; the times and the notes themselves stay as generated.
            if (Layout is { } layout) generated = Library.NoteEditLayout.Apply(generated, hits, layout, settings, out hits);
            var skip = 0;
            while (skip < hits.Count && hits[skip] < index) skip++;
            // Only auto hits (or nothing) remain after the start: nothing to press.
            if (times.Count > 0 && skip == generated.Count)
                throw new NotePlacementException(Languages.Get(settings.Language, "Playback.NoPressedNotes"));
            // The first prepared note is placed on the judgment line, even when it shares a chord time with an earlier hit.
            var zero = generated.Count > skip ? generated[skip].HitTimeMs : 0;
            notes = generated.Skip(skip).Select(n => n.Rebased(zero, settings.StartDelayMs)).ToList().AsReadOnly();
            noteHits = hits.Skip(skip).ToArray();
            if (noteHits.Length > 0) noteOrigin = times[noteHits[0]];
            // Notes on a checkpoint tile get their own color; the hit map says which ones they are.
            if (_timeline is { CheckpointsVerified: true } marked)
            {
                var checkpoints = marked.Checkpoints.Select(c => c.HitEventIndex).ToHashSet();
                notes = notes.Select((note, i) => checkpoints.Contains(noteHits[i]) ? note with { Checkpoint = true } : note)
                    .ToList().AsReadOnly();
            }
        }
        catch (NotePlacementException)
        {
            // Keep the source timeline available for correcting settings, but never
            // allow Start to replay stale notes from a previous successful preparation.
            Notes = Array.Empty<VisualizerNote>();
            State = _enabled ? PlaybackState.NoTimeline : PlaybackState.Off;
            throw;
        }
        _originMs = origin;
        _noteHits = noteHits;
        _noteOriginMs = noteOrigin;
        HideNotesBeforeMs = double.NegativeInfinity;
        // A new note list means a new play: the press calibration starts over.
        _calibration.Clear();
        _calibrationDone = false;
        CalibrationOffsetMs = 0;
        _startHitIndex = index;
        _resetCheckpoint = checkpoint;
        Notes = notes;
        Settings = settings;
        State = !_enabled ? PlaybackState.Off : notes.Count == 0 ? PlaybackState.NoTimeline : PlaybackState.Waiting;
    }
}
