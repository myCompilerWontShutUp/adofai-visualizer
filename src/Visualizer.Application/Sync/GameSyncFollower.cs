using System.Globalization;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using static Visualizer.Core.Localization.ConsoleText;

namespace Visualizer.Application.Sync;

/// <summary>Why following is not happening, or <see cref="Following"/> while it is.</summary>
public enum GameSyncFollowState
{
    Off, NoLevel, WrongSong, NoNotes, WaitingForGame, HeldHere, Dropped, LevelChanged, GameOver, GameStopped, OtherLevel,
    OtherRate, Silent, Following
}

/// <summary>
/// Keeps the app's playhead on the game's own clock while the game plays.
///
/// The game reports the song position it judges presses by; every tile of the map sits at that position plus one fixed offset,
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
    /// <summary>
    /// How long readings may keep arriving with the very same position before the game counts as standing still. The
    /// game moves the position every frame it plays, so only a stopped clock repeats it; a hitch sends nothing at all and
    /// is not mistaken for one.
    /// </summary>
    public const double StillMs = 150;
    /// <summary>
    /// How far ahead of the song a tile the game names may lie while it is played: an early hit moves the tile on a little
    /// before its time. A tile further ahead is not the one being played.
    /// </summary>
    public const double AheadTileMs = 300;
    /// <summary>
    /// How far ahead of a rewound song a checkpoint's respawn tile may lie and still be where the rewind carries on: the
    /// countdown before it, a few beats.
    /// </summary>
    public const double LeadInMs = 6000;
    /// <summary>How far the game's real rate may be from 100% and still be followed: the falling notes run at 100%.</summary>
    private const double RateTolerance = 0.001;

    private double _lastAppliedMs = double.NaN;
    private IReadOnlyList<double> _tileTimes = [];
    private ParsedTimeline? _prepared;
    private GameSyncLevel? _level;
    private string? _path;
    private double _frozenMs = double.NaN;
    private double _seenPositionMs = double.NaN;
    private double _seenSinceMs = double.NaN;
    private bool _moving;
    private double _gameRate = 1;
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
        if (session.Timeline is not { } timeline) { State = GameSyncFollowState.NoNotes; return T("FollowNoSong"); }
        // The app's own practice speed would draw the notes falling at a different rate from the game they follow.
        if (session.Settings.PlaybackSpeedPercent != 100)
            { State = GameSyncFollowState.WrongSong; return T("FollowSpeed"); }
        var check = GameTileCheck.Compare(level.TimesMs, timeline);
        if (check.Matched == 0) { State = GameSyncFollowState.WrongSong; return T("FollowNoTiles"); }
        var spread = check.MaxOffsetMs - check.MinOffsetMs;
        if (spread > GameTileCheck.Tolerance || check.Unmatched > 0)
        {
            State = GameSyncFollowState.WrongSong;
            return T("FollowNotLinedUp", spread.ToString("0.0", CultureInfo.InvariantCulture), check.Unmatched);
        }
        // Prepared from a checkpoint or a practice tile, the app has no notes before that point, so the game playing
        // from the start would leave the playhead a long way in front of the first one with nothing on screen.
        if (session.StartHitIndex > 0)
        {
            State = GameSyncFollowState.WrongSong;
            return T("FollowFromMiddle", session.StartHitIndex + 1);
        }
        OffsetMs = check.MedianOffsetMs;
        _tileTimes = level.TimesMs;
        _level = level;
        _path = level.Path;
        _prepared = timeline;
        session.HideNotesBeforeMs = double.NegativeInfinity;
        // From here the game owns the song: the session stops moving it on its own and stops taking calibration presses.
        session.ClockFollowsGame = true;
        State = GameSyncFollowState.WaitingForGame;
        _snaps = 0;
        _pulls = 0;
        _lastAppliedMs = double.NaN;
        _frozenMs = double.NaN;
        _seenPositionMs = double.NaN;
        _moving = false;
        return null;
    }

    /// <summary>Forgets the offset; the next <see cref="Prepare"/> works it out again.</summary>
    public void Stop()
    {
        OffsetMs = null;
        _tileTimes = [];
        _prepared = null;
        _level = null;
        _path = null;
        _frozenMs = double.NaN;
        State = GameSyncFollowState.Off;
        LastErrorMs = double.NaN;
    }

    /// <summary>Ends following and hands the session back, with <paramref name="why"/> left for the console.</summary>
    private void Drop(PlaybackSession session, GameSyncFollowState why)
    {
        Stop();
        session.ClockFollowsGame = false;
        session.HideNotesBeforeMs = double.NegativeInfinity;
        State = why;
    }

    /// <summary>
    /// The game sent this level's tiles again: every new connection does, and its editor can change them under the same
    /// name. The same tiles change nothing; others are checked like the first ones, and following ends if they no longer
    /// line up rather than carrying on with an offset that belongs to another map.
    /// </summary>
    private bool Recheck(GameSyncLevel level, PlaybackSession session)
    {
        if (level.TimesMs.SequenceEqual(_tileTimes)) { _level = level; return true; }
        var check = GameTileCheck.Compare(level.TimesMs, _prepared!);
        if (check.Matched == 0 || check.Unmatched > 0 || check.MaxOffsetMs - check.MinOffsetMs > GameTileCheck.Tolerance)
        {
            Drop(session, GameSyncFollowState.LevelChanged);
            return false;
        }
        OffsetMs = check.MedianOffsetMs;
        _tileTimes = level.TimesMs;
        _level = level;
        return true;
    }

    /// <summary>
    /// The notes stand where they are: the game is over, silent, on another level or at another rate. Nothing starts from
    /// here, and a running playhead is held on the spot it had, so it neither runs on alone nor jumps.
    /// </summary>
    private bool Stand(PlaybackSession session, GameSyncFollowState why)
    {
        State = why;
        if (session.State != PlaybackState.Playing) return false;
        if (double.IsNaN(_frozenMs)) _frozenMs = session.TimelineTimeMs;
        return session.MoveClock(_frozenMs);
    }

    /// <summary>
    /// One frame's worth of following. <paramref name="reading"/> is the newest one the game sent and
    /// <paramref name="arrivedMs"/> when it came, on the same clock as <paramref name="nowMs"/>, so the position is carried
    /// forward over the time since. <paramref name="level"/> is the newest tile times the game sent, which may have been
    /// sent again since <see cref="Prepare"/>. Returns whether the clock was moved.
    /// </summary>
    public bool Follow(GameSyncMessage? reading, double arrivedMs, double nowMs, PlaybackSession session, GameSyncLevel? level = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (OffsetMs is null) return false;
        // One place decides that following is no longer what the app is doing. Anything that re-prepares the notes or
        // moves the song on its own would quietly fight the game, so it ends following instead (user rule).
        if (!ReferenceEquals(session.Timeline, _prepared) || session.StartHitIndex > 0
            || session.Settings.PlaybackSpeedPercent != 100)
        {
            Drop(session, GameSyncFollowState.Dropped);
            return false;
        }
        if (level is not null && !ReferenceEquals(level, _level) && level.Path == _path && !Recheck(level, session)) return false;
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
        var playing = session.State == PlaybackState.Playing;
        if (reading is null || !double.IsFinite(arrivedMs) || !double.IsFinite(nowMs) || nowMs - arrivedMs > StaleMs)
            return Stand(session, playing ? GameSyncFollowState.Silent : GameSyncFollowState.WaitingForGame);
        // The offset belongs to one map: the game playing another must not move these notes.
        if (reading.Path != _path) return Stand(session, GameSyncFollowState.OtherLevel);
        // The game keeps the song running through a game over, so following it would carry the notes on past the end.
        // They stand still where the run ended until it restarts, which then puts them back by itself.
        if (reading.Dead) return Stand(session, GameSyncFollowState.GameOver);
        // A level waiting for its first key (scrController state Start, also where a restart lands) reports a song position
        // that runs on in real time while nothing is played (measured: about 3 s in 3 s on PLUM MEGAMIX 2's start screen).
        // Followed, the notes would run with it; they wait for the countdown instead.
        if (reading.State is "Start" or "None")
            return Stand(session, playing ? GameSyncFollowState.GameStopped : GameSyncFollowState.WaitingForGame);
        // The notes fall at 100%; a game played faster or slower would have them chase it. Checked after a game over,
        // which slows the music down by itself.
        if (!double.IsFinite(reading.PitchRate) || Math.Abs(reading.PitchRate - 1) > RateTolerance)
        {
            _gameRate = reading.PitchRate;
            return Stand(session, GameSyncFollowState.OtherRate);
        }
        _frozenMs = double.NaN;
        // An older linker sends no restart tile, so the tile the game names is all there is. A lower one than the cut is a
        // level loaded afresh (the tile only goes down with a new controller) and shows the notes from there again.
        var restart = RestartFloorOf(reading);
        if (restart is null && double.IsFinite(session.HideNotesBeforeMs) && (uint)reading.Floor < (uint)_tileTimes.Count)
            session.HideNotesBeforeMs = Math.Min(session.HideNotesBeforeMs, CutAfter(reading.Floor, session));

        // A paused game, or one whose clock has stopped, is exactly where it says: nothing is carried forward, and the
        // notes wait there with it instead of running on and jumping back. One reading alone does not show a moving
        // clock either, so a game sitting on its start screen starts nothing.
        if (reading.PositionMs != _seenPositionMs)
        {
            _moving = !double.IsNaN(_seenPositionMs);
            _seenPositionMs = reading.PositionMs;
            _seenSinceMs = arrivedMs;
        }
        var standing = reading.Paused || !_moving || arrivedMs - _seenSinceMs >= StillMs;
        // The reading is as old as the time since it arrived, and the song has moved on by that much.
        var position = standing ? reading.PositionMs : reading.PositionMs + (nowMs - arrivedMs);
        var target = session.TimelineTimeOf(position - OffsetMs.Value);
        // Without the restart tile, the tile the game names stays on the one the last run reached until the player passes
        // it (user report, Wont Let You Go: after a practice restart it named tile 745, seconds ahead of the song, and every
        // note up to there stayed hidden). Once the player has control the tile being played cannot lie far ahead of the
        // song, so such a tile is not believed and the notes from where the song is are drawn.
        if (restart is null && double.IsFinite(session.HideNotesBeforeMs) && reading.State == "PlayerControl" && !standing
            && (uint)reading.Floor < (uint)_tileTimes.Count && _tileTimes[reading.Floor] > position + AheadTileMs)
            session.HideNotesBeforeMs = Math.Min(session.HideNotesBeforeMs, session.NoteTimeOf(position - OffsetMs.Value));
        if (!playing)
        {
            // Nothing to start while the game stands still; it starts with the game.
            if (standing) { State = GameSyncFollowState.WaitingForGame; return false; }
            // The game started: begin here rather than waiting for a key.
            if (!session.Start()) return false;
            session.MoveClock(target);
            session.Presses?.ForgetAfter(session, target);
            // A run started on a later tile (practice mode, the editor's play from here) counts down over the tiles before
            // it, which it does not play.
            if (restart is { } first) session.HideNotesBeforeMs = CutAfter(first, session);
            State = GameSyncFollowState.Following;
            LastErrorMs = 0;
            _lastAppliedMs = target;
            _snaps++;
            return true;
        }

        var error = target - session.TimelineTimeMs;
        LastErrorMs = error;
        State = standing ? GameSyncFollowState.GameStopped : GameSyncFollowState.Following;
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
                // so they must not fall during its countdown, and every one after it must (user rule).
                session.HideNotesBeforeMs = RestartCut(reading, restart, position, session);
            }
            _lastAppliedMs = target;
            _snaps++;
            return true;
        }
        if (standing)
        {
            session.MoveClock(target);
            _lastAppliedMs = target;
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

    /// <summary>The tile the game restarts this attempt from, when the linker sends one that belongs to this level.</summary>
    private int? RestartFloorOf(GameSyncMessage reading) =>
        reading.RestartFloor is int floor && (uint)floor < (uint)_tileTimes.Count ? floor : null;

    /// <summary>
    /// Where the notes of a rewound attempt begin, in note time. The game says which tile it restarts from (GCS.checkpointNum),
    /// which settles it. An older linker does not, and the tile it names is no help: the game only ever raises it, so after a
    /// game over it still names the tile the run died on (user report: the notes to play only showed when the countdown
    /// ended). Then a checkpoint respawning just ahead of the rewound song is taken, the latest one the run reached; failing
    /// that, only what is already behind the song is hidden, so every note to play falls during the countdown.
    /// </summary>
    private double RestartCut(GameSyncMessage reading, int? restart, double position, PlaybackSession session)
    {
        if (restart is { } floor) return CutAfter(floor, session);
        int? best = null;
        if (session.Timeline is { CheckpointsVerified: true } timeline)
            foreach (var checkpoint in timeline.Checkpoints)
                if (checkpoint.RespawnFloor is int respawn && respawn <= reading.Floor && (uint)respawn < (uint)_tileTimes.Count
                    && respawn > (best ?? -1) && _tileTimes[respawn] - position is var ahead && ahead >= -SnapMs && ahead <= LeadInMs)
                    best = respawn;
        return best is { } found ? CutAfter(found, session) : session.NoteTimeOf(position - OffsetMs!.Value);
    }

    /// <summary>
    /// The cut for an attempt that starts on <paramref name="floor"/>, in note time. The player stands on that tile through the
    /// countdown, so its own hit is not played either (the game's checkpoint and practice starts play the first hit after
    /// it); the cut lies halfway to the next tile that comes later, so the small difference between the game's tile times
    /// and ours cannot put the standing tile's note on the wrong side. Tile 0 is the start: nothing is hidden.
    /// </summary>
    private double CutAfter(int floor, PlaybackSession session)
    {
        if (floor <= 0) return double.NegativeInfinity;
        var at = _tileTimes[floor];
        var next = floor + 1;
        while (next < _tileTimes.Count && _tileTimes[next] - at <= GameTileCheck.Tolerance) next++;
        var cut = next < _tileTimes.Count ? (at + _tileTimes[next]) / 2 : at + GameTileCheck.Tolerance;
        return session.NoteTimeOf(cut - OffsetMs!.Value);
    }

    /// <summary>The follower's state as the console prints it, in the console's language.</summary>
    public IReadOnlyList<string> Lines()
    {
        string Ms(double value) => double.IsFinite(value) ? value.ToString("0.0", CultureInfo.InvariantCulture) : "-";
        static string Percent(double rate) => double.IsFinite(rate) ? (rate * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%" : T("UnknownRate");
        var what = State switch
        {
            GameSyncFollowState.OtherRate => T("Follow.OtherRate", Percent(_gameRate)),
            var state => T("Follow." + state)
        };
        var lines = new List<string> { T("FollowLine", what) };
        if (OffsetMs is { } offset) lines.Add(T("FollowOffset", Ms(offset), Ms(LastErrorMs)));
        if (_snaps + _pulls > 0) lines.Add(T("FollowMoves", _snaps, _pulls));
        // An empty screen is worth explaining: the song may simply not have reached its first note yet.
        if (FirstNoteWaitMs > 2000) lines.Add(T("FollowFirst", Ms(FirstNoteWaitMs / 1000)));
        return lines;
    }
}
