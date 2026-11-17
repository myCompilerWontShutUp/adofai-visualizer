using Visualizer.Core.Maps;
using Visualizer.Core.Visualization;

namespace Visualizer.Application.Playback;

/// <summary>
/// A press that matched a prepared note: when it was shown, how late it was (negative is early) and which note. When the
/// game is linked, <paramref name="GameGrade"/> is the grade the game itself gave it.
/// </summary>
public sealed record PressMark(double DisplayTimeMs, double DeviationMs, int Note, HitGrade? GameGrade = null);

/// <summary>
/// Visual-only estimate of which prepared note a lane key press hit. It never changes notes or
/// timing, keeps no key history, and forgets every mark when the prepared note list changes.
/// </summary>
public sealed class NotePressJudge
{
    private IReadOnlyList<VisualizerNote>? _notes;
    private bool[] _pressed = [];
    private bool[] _releasedEarly = [];
    private double _longestHoldMs;
    private int _missCursor;
    private readonly List<PressMark> _recent = [];

    /// <summary>How many recent presses are remembered for the magic circle timing bar.</summary>
    private const int RecentPressCount = 8;

    /// <summary>
    /// The last few presses that matched a note. Only for drawing the timing bar; nothing is written down and the list is
    /// cleared with every new note list.
    /// </summary>
    public IReadOnlyList<PressMark> RecentPresses => _recent;

    /// <summary>
    /// Gives the grades the game reported to the latest presses that have none yet: the judgment counts the linker sends
    /// went up by these since the last reading. Only presses shown within <paramref name="withinMs"/> before
    /// <paramref name="nowDisplayMs"/> take one, since an older press was graded long ago.
    /// The counts do not say which press got which grade, and the game grades presses this app never matched to a note
    /// (a key off the drawn lane in a fast run), so several grades at once are paired with the presses by
    /// <paramref name="expected"/>, the grade each press's own timing gives: the closest grade first. When the grades and the
    /// presses are one for one, every press takes one; otherwise only a grade within one step of what was expected, and the
    /// rest keep their own (user report: in a fast run the late extras went to presses near the middle). Returns how many
    /// were given.
    /// </summary>
    public int TakeGameGrades(IReadOnlyList<HitGrade> grades, double nowDisplayMs, double withinMs, Func<PressMark, HitGrade> expected)
    {
        ArgumentNullException.ThrowIfNull(grades);
        ArgumentNullException.ThrowIfNull(expected);
        if (grades.Count == 0 || !double.IsFinite(nowDisplayMs) || !double.IsFinite(withinMs) || withinMs < 0) return 0;
        var open = Enumerable.Range(0, _recent.Count)
            .Where(i => _recent[i].GameGrade is null && nowDisplayMs - _recent[i].DisplayTimeMs is var age && age >= 0 && age <= withinMs)
            .ToList();
        var oneForOne = open.Count == grades.Count;
        var left = grades.ToList();
        var given = 0;
        while (open.Count > 0 && left.Count > 0)
        {
            // The closest pair of all that are left; ties go to the older press and the earlier grade.
            var (press, grade, cost) = (-1, -1, int.MaxValue);
            foreach (var i in open)
            {
                var want = (int)expected(_recent[i]);
                for (var g = 0; g < left.Count; g++)
                    if (Math.Abs((int)left[g] - want) is var distance && distance < cost) (press, grade, cost) = (i, g, distance);
            }
            if (!oneForOne && cost > 1) break;
            _recent[press] = _recent[press] with { GameGrade = left[grade] };
            open.Remove(press);
            left.RemoveAt(grade);
            given++;
        }
        if (given > 0) Version++;
        return given;
    }

    /// <summary>Changes whenever a mark is added or cleared, so renderers can skip static frames.</summary>
    public int Version { get; private set; }

    public bool IsPressed(IReadOnlyList<VisualizerNote> notes, int index) =>
        ReferenceEquals(notes, _notes) && (uint)index < (uint)_pressed.Length && _pressed[index];

    /// <summary>Whether a pressed long note was let go before its end window (drawn as ended, with the miss effect).</summary>
    public bool IsReleasedEarly(IReadOnlyList<VisualizerNote> notes, int index) =>
        ReferenceEquals(notes, _notes) && (uint)index < (uint)_releasedEarly.Length && _releasedEarly[index];

    /// <summary>
    /// A lane key release. The game plays the hit after a hold on release and fails an early one (scrPlayer.UpdateHoldBehavior);
    /// here a pressed long note of <paramref name="tile"/> released before its end minus the press window is only marked as ended early.
    /// Returns whether that happened. Visual feedback only; nothing is recorded.
    /// </summary>
    public bool Release(PlaybackSession session, int tile, double windowMs)
    {
        if (!double.IsFinite(windowMs) || windowMs < 0) throw new ArgumentOutOfRangeException(nameof(windowMs));
        var notes = Sync(session);
        if (session.State != PlaybackState.Playing || _longestHoldMs <= 0) return false;
        var target = session.TimelineTimeMs - session.Offsets.TotalMs;
        for (var i = FirstAtOrAfter(notes, target - _longestHoldMs); i < notes.Count && notes[i].HitTimeMs <= target; i++)
        {
            if (notes[i].Tile != tile || notes[i].HoldEndMs is not double end || !_pressed[i] || _releasedEarly[i]) continue;
            if (target >= end - windowMs) continue;
            _releasedEarly[i] = true;
            Version++;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Marks the unpressed note of <paramref name="tile"/> closest to the displayed time. As the game takes a tile
    /// (scrPlanet.SwitchChosen moves on only for a counted margin), a press earlier than that note's own counted window does
    /// not take it: it is shown as too early and the note stays to be pressed. Taking it made one stray press pull every
    /// later press of the lane a note ahead, each of them early, for as long as the lane's notes came within the press
    /// window (user report: one early mark, then a run of them). A late press keeps the press window.
    /// </summary>
    public int? Press(PlaybackSession session, int tile, double windowMs)
    {
        if (!double.IsFinite(windowMs) || windowMs < 0) throw new ArgumentOutOfRangeException(nameof(windowMs));
        var notes = Sync(session);
        if (session.State != PlaybackState.Playing || notes.Count == 0) return null;
        // A note is visually on the judgment line when timeline time equals hit + display offsets.
        var target = session.TimelineTimeMs - session.Offsets.TotalMs;
        var from = target - windowMs;
        var low = 0;
        var high = notes.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (notes[middle].HitTimeMs < from) low = middle + 1;
            else high = middle;
        }
        int? best = null;
        var bestDistance = double.MaxValue;
        for (var i = low; i < notes.Count && notes[i].HitTimeMs <= target + windowMs; i++)
        {
            if (notes[i].Tile != tile || _pressed[i]) continue;
            var distance = Math.Abs(notes[i].HitTimeMs - target);
            if (distance < bestDistance) { best = i; bestDistance = distance; }
        }
        if (best is not int index) return null;
        var deviation = target - notes[index].HitTimeMs;
        var difficulty = HitGrading.DifficultyOf(session.Settings.JudgmentBarDifficulty);
        var windows = session.HitWindowsOf(index, difficulty) ?? HitGrading.Fallback(session.GameDifficulty ?? difficulty, session.JudgmentEase);
        var taken = -deviation <= windows.CountedMs;
        if (taken)
        {
            _pressed[index] = true;
            // The press calibration averages how late (or early) the first presses of a play are.
            session.AddCalibrationSample(deviation);
        }
        // Too early is drawn as well, at the bar's early end as the game shows it.
        if (_recent.Count == RecentPressCount) _recent.RemoveAt(0);
        _recent.Add(new PressMark(target, deviation, index));
        Version++;
        return taken ? index : null;
    }

    /// <summary>
    /// Marks every note sharing the hit time of <paramref name="index"/>: the press that starts playback plays the whole
    /// first chord, since a start key reaches one lane and the other half would otherwise show as waiting (user rule).
    /// </summary>
    public void PressTogether(PlaybackSession session, int index)
    {
        var notes = Sync(session);
        if ((uint)index >= (uint)notes.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var time = notes[index].HitTimeMs;
        var changed = false;
        for (var i = index; i >= 0 && notes[i].HitTimeMs == time; i--) changed |= Mark(i);
        for (var i = index + 1; i < notes.Count && notes[i].HitTimeMs == time; i++) changed |= Mark(i);
        if (changed) Version++;

        bool Mark(int i)
        {
            if (_pressed[i]) return false;
            _pressed[i] = true;
            return true;
        }
    }

    /// <summary>
    /// Appends the tile of each note whose press window has fully passed the displayed time without a press,
    /// once per note, in order. Visual feedback only; nothing is recorded.
    /// </summary>
    public void CollectMisses(PlaybackSession session, double windowMs, List<int> missedTiles)
    {
        if (!double.IsFinite(windowMs) || windowMs < 0) throw new ArgumentOutOfRangeException(nameof(windowMs));
        var notes = Sync(session);
        if (session.State != PlaybackState.Playing) return;
        var passed = session.TimelineTimeMs - session.Offsets.TotalMs - windowMs;
        // Forward-only: an offset change that moves time back never reports a note twice.
        // A note hidden by a game's rewind is never played there, so it never misses either.
        for (; _missCursor < notes.Count && notes[_missCursor].HitTimeMs < passed; _missCursor++)
            if (!_pressed[_missCursor] && notes[_missCursor].HitTimeMs >= session.HideNotesBeforeMs) missedTiles.Add(notes[_missCursor].Tile);
    }

    /// <summary>
    /// While paused, the note of <paramref name="tile"/> displayed closest to the paused time within ±<paramref name="rangeMs"/>,
    /// preferring notes not yet marked pressed. The pause can land a little late (Escape delay), so the pressed lane
    /// decides the note rather than whichever note is nearest overall.
    /// </summary>
    public int? FindResumeNote(PlaybackSession session, int tile, double rangeMs)
    {
        if (!double.IsFinite(rangeMs) || rangeMs < 0) throw new ArgumentOutOfRangeException(nameof(rangeMs));
        var notes = Sync(session);
        if (session.State != PlaybackState.Paused || notes.Count == 0) return null;
        var target = session.TimelineTimeMs - session.Offsets.TotalMs;
        int? best = null;
        var bestKey = (Pressed: true, Distance: double.MaxValue);
        for (var i = FirstAtOrAfter(notes, target - rangeMs); i < notes.Count && notes[i].HitTimeMs <= target + rangeMs; i++)
        {
            if (notes[i].Tile != tile) continue;
            var key = (Pressed: _pressed[i], Distance: Math.Abs(notes[i].HitTimeMs - target));
            if (best is null || (!key.Pressed && bestKey.Pressed) || (key.Pressed == bestKey.Pressed && key.Distance < bestKey.Distance))
            {
                best = i;
                bestKey = key;
            }
        }
        return best;
    }

    /// <summary>
    /// The first note that has not been pressed, however late it is. A pause puts that one on the judgment line, since what
    /// the player pressed decides where the run continues (user rule). Null when every note was pressed.
    /// </summary>
    public int? FirstUnpressed(PlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var notes = session.Notes;
        if (!ReferenceEquals(notes, _notes)) return notes.Count > 0 ? 0 : null;
        for (var i = 0; i < notes.Count && i < _pressed.Length; i++)
            if (!_pressed[i]) return i;
        return null;
    }

    /// <summary>
    /// Forgets the marks on <paramref name="fromIndex"/> and everything after it. Moving the paused song back to a note
    /// puts later notes in front of the playhead again, and a note the playhead has not reached cannot have been pressed:
    /// the app watches lane keys globally, so playing the game itself leaves marks on notes it never showed.
    /// </summary>
    public void ForgetFrom(PlaybackSession session, int fromIndex)
    {
        var notes = Sync(session);
        if (fromIndex < 0) fromIndex = 0;
        var changed = false;
        for (var i = fromIndex; i < notes.Count && i < _pressed.Length; i++)
        {
            changed |= _pressed[i] || _releasedEarly[i];
            _pressed[i] = false;
            _releasedEarly[i] = false;
        }
        if (changed) Version++;
        _recent.Clear();
    }

    /// <summary>
    /// Forgets the marks on every note that is still ahead of <paramref name="timelineMs"/>. A game restart throws the
    /// playhead back to the countdown, and the marks the last attempt left would then sit on notes it never showed.
    /// </summary>
    public void ForgetAfter(PlaybackSession session, double timelineMs)
    {
        var notes = Sync(session);
        if (!double.IsFinite(timelineMs)) return;
        var from = notes.Count;
        for (var i = 0; i < notes.Count; i++)
            if (notes[i].HitTimeMs + session.Offsets.TotalMs >= timelineMs) { from = i; break; }
        // Misses are counted forward only, so without this the notes played again after a restart could never miss.
        _missCursor = Math.Min(_missCursor, from);
        ForgetFrom(session, from);
    }

    public void SkipMissesBefore(PlaybackSession session, double hitTimeMs)
    {
        var notes = Sync(session);
        _missCursor = Math.Max(_missCursor, FirstAtOrAfter(notes, hitTimeMs));
    }

    private static int FirstAtOrAfter(IReadOnlyList<VisualizerNote> notes, double hitTimeMs)
    {
        var low = 0;
        var high = notes.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (notes[middle].HitTimeMs < hitTimeMs) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private IReadOnlyList<VisualizerNote> Sync(PlaybackSession session)
    {
        var notes = session.Notes;
        if (!ReferenceEquals(notes, _notes))
        {
            _notes = notes;
            _pressed = new bool[notes.Count];
            _releasedEarly = new bool[notes.Count];
            _longestHoldMs = notes.Max(note => note.HoldEndMs - note.HitTimeMs) ?? 0;
            _recent.Clear();
            _missCursor = 0;
            Version++;
        }
        return notes;
    }
}
