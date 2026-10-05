using System.Globalization;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;

namespace Visualizer.Application.Sync;

/// <summary>Why following is not happening, or <see cref="Following"/> while it is.</summary>
public enum GameSyncFollowState { Off, NoLevel, WrongSong, NoNotes, WaitingForGame, HeldHere, Dropped, GameOver, Following }

/// <summary>
/// Keeps the app's playhead on the game's own clock while the game plays.
///
/// The game reports the song position it draws with; every tile of the map sits at that position plus one fixed offset,
/// which <see cref="GameTileCheck"/> measures from the game's own tile times. So a reading becomes a point on this app's
/// clock, and the playhead is pulled towards it rather than snapped to it: snapping every reading would show the game's
/// frame rate as judder, while a gentle pull rides out the small differences and still follows a drifting rate.
///
/// It only moves the clock. Notes, hit times and judging are untouched (AGENTS.md: unchanging data).
/// </summary>
public sealed class GameSyncFollower
{
    /// <summary>How far out before the playhead is put there at once instead of pulled: a restart, not drift.</summary>
    public const double SnapMs = 60;
    /// <summary>How much of the remaining difference to take back per reading; the rest rides out the noise.</summary>
    private const double Pull = 0.15;
    /// <summary>A reading older than this is stale: the game stopped sending, so following stops.</summary>
    public const double StaleMs = 500;

    private double _lastAppliedMs = double.NaN;
    private IReadOnlyList<double> _tileTimes = [];
    private ParsedTimeline? _prepared;
    private double _frozenMs = double.NaN;
    private int _snaps;
    private int _pulls;

    /// <summary>Where the game's clock and ours meet: the game's tile time minus ours, in milliseconds.</summary>
    public double? OffsetMs { get; private set; }
    /// <summary>What the follower did last, for the console to show.</summary>
    public GameSyncFollowState State { get; private set; } = GameSyncFollowState.Off;
    /// <summary>How far the playhead was out at the last reading, or NaN before one.</summary>
    public double LastErrorMs { get; private set; } = double.NaN;
    /// <summary>How long until the first prepared note, when the playhead is still in front of it.</summary>
    public double FirstNoteWaitMs { get; private set; }
    /// <summary>How many times it had to jump, and how many times it only pulled.</summary>
    public int Snaps => _snaps;
    public int Pulls => _pulls;

    /// <summary>
    /// Works out the offset from the game's own tile times and this song's hit times. Returns null with a reason when the
    /// two do not line up, which is the only honest answer: syncing to a song the game is not playing is worse than not
    /// syncing at all.
    /// </summary>
    public string? Prepare(GameSyncLevel level, PlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(session);
        OffsetMs = null;
        if (session.Timeline is not { } timeline) { State = GameSyncFollowState.NoNotes; return "no song is loaded."; }
        // The app's own practice speed would draw the notes falling at a different rate from the game they follow.
        if (session.Settings.PlaybackSpeedPercent != 100)
            { State = GameSyncFollowState.WrongSong; return "this app's playback speed is not 100%. The game sets the pace while it is followed."; }
        var check = GameTileCheck.Compare(level.TimesMs, timeline);
        if (check.Matched == 0) { State = GameSyncFollowState.WrongSong; return "none of this song's tiles is one the game has."; }
        var spread = check.MaxOffsetMs - check.MinOffsetMs;
        if (spread > GameTileCheck.Tolerance || check.Unmatched > 0)
        {
            State = GameSyncFollowState.WrongSong;
            return $"the tiles do not line up ({spread.ToString("0.0", CultureInfo.InvariantCulture)} ms apart,"
                   + $" {check.Unmatched} missing), so this is not the song the game is playing.";
        }
        // Prepared from a checkpoint or a practice tile, the app has no notes before that point, so the game playing
        // from the start would leave the playhead a long way in front of the first one with nothing on screen.
        if (session.StartHitIndex > 0)
        {
            State = GameSyncFollowState.WrongSong;
            return $"the app is prepared from hit {session.StartHitIndex + 1}, not the start of the song."
                   + " Reset it to the beginning, then follow again.";
        }
        OffsetMs = check.MedianOffsetMs;
        _tileTimes = level.TimesMs;
        _prepared = timeline;
        session.HideNotesBeforeMs = double.NegativeInfinity;
        // From here the game owns the song: the session stops moving it on its own and stops taking calibration presses.
        session.ClockFollowsGame = true;
        State = GameSyncFollowState.WaitingForGame;
        _snaps = 0;
        _pulls = 0;
        _lastAppliedMs = double.NaN;
        return null;
    }

    /// <summary>Forgets the offset; the next <see cref="Prepare"/> works it out again.</summary>
    public void Stop()
    {
        OffsetMs = null;
        _tileTimes = [];
        _prepared = null;
        _frozenMs = double.NaN;
        State = GameSyncFollowState.Off;
        LastErrorMs = double.NaN;
    }

    /// <summary>
    /// One frame's worth of following. <paramref name="reading"/> is the newest one the game sent and
    /// <paramref name="arrivedMs"/> when it came, on the same clock as <paramref name="nowMs"/>, so the position is carried
    /// forward over the time since. Returns whether the clock was moved.
    /// </summary>
    public bool Follow(GameSyncMessage? reading, double arrivedMs, double nowMs, PlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (OffsetMs is not { } offset) return false;
        // One place decides that following is no longer what the app is doing. Anything that re-prepares the notes or
        // moves the song on its own would quietly fight the game, so it ends following instead (user rule).
        if (!ReferenceEquals(session.Timeline, _prepared) || session.StartHitIndex > 0
            || session.Settings.PlaybackSpeedPercent != 100)
        {
            Stop();
            session.ClockFollowsGame = false;
            session.HideNotesBeforeMs = double.NegativeInfinity;
            State = GameSyncFollowState.Dropped;
            return false;
        }
        if (reading is null || !double.IsFinite(arrivedMs) || !double.IsFinite(nowMs) || nowMs - arrivedMs > StaleMs)
        {
            State = GameSyncFollowState.WaitingForGame;
            return false;
        }
        // The game keeps its own clock running through a fail, so only a paused game means "do not move".
        if (reading.Paused) { State = GameSyncFollowState.WaitingForGame; return false; }
        if (session.State is not (PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Waiting))
        {
            State = GameSyncFollowState.NoNotes;
            return false;
        }
        // A pause here is the player taking the playhead back; resuming it from under them would make Escape do nothing.
        if (session.State == PlaybackState.Paused)
        {
            State = GameSyncFollowState.HeldHere;
            return false;
        }
        // The game keeps the song running through a game over, so following it would carry the notes on past the end.
        // They stand still where the run ended until it restarts, which then puts them back by itself.
        if (reading.Dead && session.State == PlaybackState.Playing)
        {
            if (double.IsNaN(_frozenMs)) _frozenMs = session.TimelineTimeMs;
            session.MoveClock(_frozenMs);
            State = GameSyncFollowState.GameOver;
            return true;
        }
        _frozenMs = double.NaN;

        // The reading is as old as the time since it arrived, and the song has moved on by that much.
        var position = reading.PositionMs + (nowMs - arrivedMs);
        var target = session.TimelineTimeOf(position - offset);
        if (session.State != PlaybackState.Playing)
        {
            // The game started: begin here rather than waiting for a key.
            if (!session.Start()) return false;
            session.MoveClock(target);
            session.Presses?.ForgetAfter(session, target);
            State = GameSyncFollowState.Following;
            LastErrorMs = 0;
            _lastAppliedMs = target;
            _snaps++;
            return true;
        }

        var error = target - session.TimelineTimeMs;
        LastErrorMs = error;
        State = GameSyncFollowState.Following;
        FirstNoteWaitMs = session.Notes.Count == 0 ? 0
            : Math.Max(0, session.Notes[0].HitTimeMs + session.Offsets.TotalMs - target);
        if (Math.Abs(error) > SnapMs)
        {
            session.MoveClock(target);
            // A jump is a restart or a checkpoint, so whatever was marked ahead of here was never shown at this point.
            if (error < 0)
            {
                session.Presses?.ForgetAfter(session, target);
                // The game rewound to the tile it will carry on from; the tiles before it are not going to be played,
                // so they must not fall during its countdown.
                if ((uint)reading.Floor < (uint)_tileTimes.Count)
                    session.HideNotesBeforeMs = session.NoteTimeOf(_tileTimes[reading.Floor] - offset);
            }
            _lastAppliedMs = target;
            _snaps++;
            return true;
        }
        var moved = session.TimelineTimeMs + error * Pull;
        // Without this the clock would be set from the same reading every frame, which is the reading's own rate, not ours.
        if (double.IsFinite(_lastAppliedMs) && Math.Abs(moved - _lastAppliedMs) < 0.001) return false;
        session.MoveClock(moved);
        _lastAppliedMs = moved;
        _pulls++;
        return true;
    }

    /// <summary>The follower's state as the console prints it (English, like the rest of the console).</summary>
    public IReadOnlyList<string> Lines()
    {
        string Ms(double value) => double.IsFinite(value) ? value.ToString("0.0", CultureInfo.InvariantCulture) : "-";
        var what = State switch
        {
            GameSyncFollowState.Off => "not following.",
            GameSyncFollowState.NoLevel => "waiting for the game to load a level.",
            GameSyncFollowState.WrongSong => "the loaded song is not the one the game has.",
            GameSyncFollowState.NoNotes => "no notes are prepared.",
            GameSyncFollowState.WaitingForGame => "waiting for the game to play.",
            GameSyncFollowState.HeldHere => "paused here. Resume in the app to follow the game again.",
            GameSyncFollowState.Dropped => "stopped: the song, the start point or the playback speed changed here. Use link follow again.",
            GameSyncFollowState.GameOver => "the game is over; the notes wait here until it starts again.",
            _ => "following the game."
        };
        var lines = new List<string> { "Follow   " + what };
        if (OffsetMs is { } offset)
            lines.Add($"Offset   {Ms(offset)} ms between the game's clock and ours"
                      + $" · off by {Ms(LastErrorMs)} ms at the last reading");
        if (_snaps + _pulls > 0) lines.Add($"Moves    {_snaps} jumps · {_pulls} nudges");
        // An empty screen is worth explaining: the song may simply not have reached its first note yet.
        if (FirstNoteWaitMs > 2000)
            lines.Add($"First    the first note is {Ms(FirstNoteWaitMs / 1000)} s away, so the screen is empty until then.");
        return lines;
    }
}
