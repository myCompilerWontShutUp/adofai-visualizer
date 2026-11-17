using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;
using Visualizer.App.Localization;
using Visualizer.Application.Cache;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;

namespace Visualizer.App.ViewModels;

/// <summary>One applied change of the settings; rollback puts back what it touched.</summary>
public sealed record SettingsChange(VisualizerSettings Before, VisualizerSettings After);

/// <summary>
/// Main screen state and commands. The song library lives in MainViewModel.Songs.cs, note preparation with its lane
/// offers in MainViewModel.Placement.cs, and the song info shown while stopped in MainViewModel.Overlay.cs.
/// </summary>
public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private sealed class StopwatchClock : IMonotonicClock
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        public double NowMs => _watch.Elapsed.TotalMilliseconds;
    }

    public sealed record CheckpointOption(int Number, string Label);

    // Commands raise only these: a full refresh would rebuild every binding, including the checkpoint list.
    /// <summary>
    /// The names raised when playback changes. A screen element whose Show… property is missing from it keeps the last
    /// answer on screen, so the tests check the list itself.
    /// </summary>
    public static IReadOnlyList<string> PlaybackNames => PlaybackProperties;

    private static readonly string[] PlaybackProperties =
        [nameof(StateLabel), nameof(StateKind), nameof(PauseLabel), nameof(PauseGlyph), nameof(CanPauseResume), nameof(IsVisualizerOn), nameof(KeyHint),
         nameof(AutoCheckpoint), nameof(AutoCheckpointActive), nameof(CanToggleAutoCheckpoint), nameof(AutoCheckpointHint), nameof(ShowActivationHint),
         nameof(ShowSongOverlay), nameof(OverlayProgress), nameof(OverlayTiles), nameof(OverlayTileParts), nameof(CanMoveCheckpoint),
         // The speed, tile and slot go once the song runs, so their band has to hear about it too (user report: they
         // stayed on screen while the game played).
         nameof(ShowFieldControls), nameof(ShowOverlayBand), nameof(ShowHideOverlay), nameof(ShowSongEndNotice),
         nameof(ShowLastCheckpointButton), nameof(CanMoveSong), nameof(LoadPending),
         nameof(StartPending)];

    /// <summary>After a pause, a lane key finds its note within this distance of the paused time.</summary>
    public const double ResumeSearchMs = 1000;

    private readonly object _libraryLock = new();
    private int _checkpoint = 1;
    private bool _everActivated;
    private bool _manualCheckpointPending;
    /// <summary>What is typed in the main window's speed box before it is applied; null while it shows the applied speed.</summary>
    private string? _speedEdit;
    /// <summary>How much the arrows beside the speed box change the practice speed.</summary>
    private const double SpeedStep = 5;
    private string _sectionNotice = "";
    private string _sectionText = "";
    private bool _showFullMessage;
    private UiText? _text;
    private (ParsedTimeline? Timeline, string Language, IReadOnlyList<CheckpointOption> Options)? _checkpointOptions;
    private (PlaybackState State, long Countdown) _lastStateKey = ((PlaybackState)(-1), -1);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The clock the playback session runs on. Game sync stamps its readings with it so the two can be compared.</summary>
    public IMonotonicClock PlaybackClock { get; }

    public PlaybackSession Playback { get; }
    public NotePressJudge Presses { get; } = new();
    public VisualizerSettings Settings { get; private set; } = new();
    public UiText Text => _text is { } text && text.Language == Settings.Language ? text : _text = new(Settings.Language);
    public ObservableCollection<MapCacheEntry> Library { get; } = [];
    /// <summary>The song list filtered by the title typed in the song box.</summary>
    public ICollectionView LibraryView { get; }
    /// <summary>
    /// The latest message. Every new one is new by its time as well (user report: loading the same song again, or another
    /// version of it, left "n minutes ago" in front of the fresh words), so setting it starts its age again.
    /// </summary>
    public string Message
    {
        get => _message;
        private set
        {
            _message = value;
            _messageAt = DateTime.Now;
            _messageMinutes = 0;
            _messageAge = MessageAge();
        }
    }
    private string _message = "";
    private DateTime _messageAt = DateTime.Now;
    private string _messageAge = "";

    /// <summary>
    /// The message with how long ago it arrived in front of it (user rule): just now under a minute, the minutes up to
    /// ten, and "a while ago" after that. The frame loop asks for it, so it only tells the window when the words change.
    /// </summary>
    public string MessageText => Message.Length == 0 ? "" : MessageAge() + " " + Message;

    private string MessageAge() => AgeText(Text, Minutes());

    /// <summary>
    /// How an age reads in front of a message (user rule): just now under a minute, the minutes up to ten, and "a while
    /// ago" from eleven. Pure, so the wording and its two boundaries can be checked without waiting for a clock.
    /// </summary>
    public static string AgeText(UiText text, int minutes)
    {
        ArgumentNullException.ThrowIfNull(text);
        return minutes < 1 ? text["JustNow"] : minutes <= 10 ? text.Format("MinutesAgo", minutes) : text["LongAgo"];
    }

    /// <summary>Whole minutes since the message, capped where the words stop changing (11 and 99 read the same).</summary>
    private int Minutes() => Math.Min(11, (int)(DateTime.Now - _messageAt).TotalMinutes);

    private int _messageMinutes;

    /// <summary>
    /// Called every frame: compares the minute alone and only builds the words when that number has moved, so the frame
    /// loop does not make a string it will throw away (performance rule).
    /// </summary>
    public void RefreshMessageAge()
    {
        if (Message.Length == 0) return;
        var minutes = Minutes();
        if (minutes == _messageMinutes) return;
        _messageMinutes = minutes;
        _messageAge = MessageAge();
        Raise(nameof(MessageText));
    }
    public string Details { get; private set; } = "";
    public string InputStatus { get; private set; }

    /// <param name="clock">The clock playback runs on; the real one when null. A test passes its own to step time.</param>
    public MainViewModel(IMonotonicClock? clock = null)
    {
        PlaybackClock = clock ?? new StopwatchClock();
        Playback = new PlaybackSession(PlaybackClock);
        // The session asks the press marks whether a checkpoint note was hit.
        Playback.Presses = Presses;
        // A cache read can finish off the UI thread; the filtered view must accept those library updates.
        BindingOperations.EnableCollectionSynchronization(Library, _libraryLock);
        LibraryView = CollectionViewSource.GetDefaultView(Library);
        LibraryView.Filter = item => item is MapCacheEntry entry && MatchesSongQuery(entry);
        InputStatus = Text["InputWaiting"];
    }

    public IReadOnlyList<CheckpointOption> CheckpointOptions
    {
        get
        {
            // Cached per timeline and language so repeated notifications never regenerate the picker.
            var timeline = Playback.Timeline;
            if (_checkpointOptions is { } cached && ReferenceEquals(cached.Timeline, timeline) && cached.Language == Settings.Language)
                return cached.Options;
            IReadOnlyList<CheckpointOption> options = timeline is not { CheckpointsVerified: true } verified
                ? [new CheckpointOption(1, Text["FromBeginning"])]
                : verified.Checkpoints.Select(c => new CheckpointOption(c.Number, CheckpointLabel(c))).ToArray();
            _checkpointOptions = (timeline, Settings.Language, options);
            return options;
        }
    }

    public int Checkpoint
    {
        get => _checkpoint;
        set
        {
            // Choosing the checkpoint that already contains a practice start still returns to that checkpoint.
            if (value == _checkpoint && Playback.StartTile is null) return;
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), Text["CheckpointInvalid"]);
            if (Playback.Timeline is { CheckpointsVerified: true } timeline)
            {
                _ = timeline.GetStartIndex(value);
                if (!TryPrepare(() => Playback.Reset(Settings, value)))
                {
                    NotifyPlayback();
                    return;
                }
            }
            _checkpoint = value;
            _manualCheckpointPending = Playback.Timeline?.CheckpointsVerified != true;
            ClearSectionText();
            Raise(nameof(Checkpoint));
            NotifyPlayback();
        }
    }

    /// <summary>Practice-mode start tile typed next to the auto toggle. Empty returns to checkpoint starts.</summary>
    public string SectionText
    {
        get => _sectionText;
        set
        {
            var text = (value ?? "").Trim();
            if (text.Length == 0)
            {
                if (Playback.StartTile is not null) ReturnToCheckpoint();
                ClearSectionText();
                return;
            }
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var tile))
            {
                // The box applies as it is typed, so a value that is not a tile number only reports; what was typed stays.
                SetMessage(Text["SectionInvalid"]);
                return;
            }
            StartAtTile(tile);
        }
    }

    public bool StartAtTile(int tile)
    {
        try
        {
            if (!TryPrepare(() => Playback.ResetAtTile(Settings, tile)))
            {
                // The tile is valid and remembered; only the current settings cannot place its notes.
                SetSectionText(tile);
                NotifyPlayback();
                return false;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            SetMessage(error.Message);
            Raise(nameof(SectionText));
            return false;
        }
        _manualCheckpointPending = false;
        _checkpoint = Playback.ActiveCheckpoint ?? 1;
        SetSectionText(tile);
        SetMessage(TakeNotice() + Text.Format("SectionStarted", tile, StartKeyLabel));
        Raise(nameof(Checkpoint));
        NotifyPlayback();
        return true;
    }

    /// <summary>The toggle on the checkpoint row: shows whether automatic progress runs and switches the setting.</summary>
    public bool AutoCheckpoint
    {
        get => Playback.AutoCheckpointActive;
        set
        {
            if (!CanToggleAutoCheckpoint || value == Settings.AutoCheckpointAdvance)
            {
                Raise(nameof(AutoCheckpoint));
                return;
            }
            var next = Settings with { AutoCheckpointAdvance = value };
            try { Playback.ApplySettings(next, Checkpoint); }
            catch (NotePlacementException error)
            {
                // Keep the model and the session on the same settings; the toggle snaps back.
                SetMessage(error.Message);
                Raise(nameof(AutoCheckpoint));
                return;
            }
            Record(Settings, next);
            Settings = next;
            NotifyPlayback();
            SettingsCommitted?.Invoke();
        }
    }

    public bool AutoCheckpointActive => Playback.AutoCheckpointActive;

    /// <summary>
    /// The game disables checkpoints at practice speeds and in practice sections, so the toggle is locked off there.
    /// The setting itself is kept and returns once the lock is gone.
    /// </summary>
    public bool CanToggleAutoCheckpoint => Playback.Settings.PlaybackSpeedPercent == 100 && Playback.StartTile is null;

    public string AutoCheckpointHint => Playback.Settings.PlaybackSpeedPercent != 100
            ? Text.Format("AutoOffSpeed", Playback.Settings.PlaybackSpeedPercent)
        : Playback.StartTile is not null ? Text["AutoOffSection"]
        : Playback.Settings.AutoCheckpointAdvance ? Text["AutoCheckpointHelp"] : Text["AutoOffSetting"];

    public string StateLabel
    {
        get
        {
            var state = Playback.State;
            if (state == PlaybackState.Playing)
            {
                if (Playback.StartWaitRemainingMs is var wait and > 0) return $"{Text["StartDelay"]} {wait / 1000:F1}s";
                if (Playback.NextHitRemainingMs is double remaining && remaining > Settings.NoteTravelTimeMs)
                    return $"{Text["NextHit"]} {remaining / 1000:F1}s";
            }
            return Text[state switch
            {
                PlaybackState.Off => "Off",
                PlaybackState.Waiting => "Waiting",
                PlaybackState.Playing => "Playing",
                PlaybackState.Paused => "Paused",
                _ => "NoTimeline"
            }];
        }
    }

    /// <summary>Whether Start would begin playing now; the button leads the eye only then.</summary>
    public bool StartPending => Playback.State == PlaybackState.Waiting && Playback.Notes.Count > 0;

    private bool _showDetails;

    /// <summary>Shows the whole status message on several lines instead of one trimmed line.</summary>
    public bool ShowFullMessage
    {
        get => _showFullMessage;
        set
        {
            if (_showFullMessage == value) return;
            _showFullMessage = value;
            Raise(nameof(ShowFullMessage));
            Raise(nameof(DetailsToggleLabel));
        }
    }

    public string DetailsToggleLabel => Text["DetailsShow"];

    /// <summary>Whether the status panel below the message is open. Its button matches the message one.</summary>
    public bool ShowDetails
    {
        get => _showDetails;
        set
        {
            if (_showDetails == value) return;
            _showDetails = value;
            Raise(nameof(ShowDetails));
            Raise(nameof(StatusToggleLabel));
        }
    }

    public string StatusToggleLabel => Text["DetailsStatus"];

    public string StateKind => Playback.State.ToString();
    public bool IsVisualizerOn => Playback.State != PlaybackState.Off;
    public string PauseLabel => Text[Playback.State == PlaybackState.Paused ? "Resume" : "Pause"];
    public string PauseGlyph => Playback.State == PlaybackState.Paused ? char.ConvertFromUtf32(0xE768) : char.ConvertFromUtf32(0xE769);
    public bool CanPauseResume => Playback.State is PlaybackState.Playing or PlaybackState.Paused;
    /// <summary>The key that starts playback now: the first note's lane key for a practice start, otherwise the start key.</summary>
    public string StartKeyLabel => Playback.LaneStartTile is int tile ? LaneKeyLabel(tile) : Settings.StartTriggerKey;
    /// <summary>A cleared checkpoint key reads as unassigned; the arrows beside the list still work.</summary>
    private string KeyLabel(string? key) => key ?? Text["Unassigned"];
    public string PreviousCheckpointTip => Text.Format("PreviousCheckpoint", KeyLabel(Settings.PreviousCheckpointKey));
    public string NextCheckpointTip => Text.Format("NextCheckpoint", KeyLabel(Settings.NextCheckpointKey));
    public bool ShowGuideButtons => Settings.ShowGuideButtons;
    /// <summary>Shown beside the Off state until the visualizer has been switched on once in this session.</summary>
    public bool ShowActivationHint => !_everActivated && Playback.State == PlaybackState.Off;
    public string ActivationHint => Text.Format("ActivateHint", Settings.VisualizerToggleKey);
    public string VersionLabel => Text.Format("Version", AppInfo.Version);
    public string KeyHint =>
        $"{Settings.VisualizerToggleKey} {Text["Toggle"]} · {Settings.ResetKey} {Text["Reset"]} · {StartKeyLabel} {Text["Start"]} · Esc {Text["Pause"]}"
        + $" · {KeyLabel(Settings.PreviousCheckpointKey)}/{KeyLabel(Settings.NextCheckpointKey)} {Text["CheckpointKeys"]}"
        + $"\n{Text["Lanes"]} {Settings.LaneKeys} · {Text[Settings.OnlyReactWhenAdofaiForeground ? "ForegroundHint" : "GlobalHint"]}";

    private const int MaximumHistory = 200;
    private readonly List<SettingsChange> _history = [];

    /// <summary>Every applied change of the settings in this run, oldest first, whatever made it; rollback steps back through them.</summary>
    public IReadOnlyList<SettingsChange> SettingsHistory => _history;

    /// <summary>Raised after the applied settings changed, whatever changed them, so the main window can save them.</summary>
    public event Action? SettingsCommitted;

    /// <summary>Starts the history empty, after the settings of the last run were restored (restoring is not a change).</summary>
    public void ForgetSettingsHistory() => _history.Clear();

    private void Record(VisualizerSettings before, VisualizerSettings after)
    {
        if (before == after) return;
        _history.Add(new SettingsChange(before, after));
        if (_history.Count > MaximumHistory) _history.RemoveAt(0);
    }

    public void ApplySettings(VisualizerSettings settings)
    {
        settings.EnsureValid();
        var rulesChanged = !Settings.HasSameNoteRulesAs(settings) || (Playback.Timeline is not null && Playback.Notes.Count == 0);
        try { Playback.ApplySettings(settings, Checkpoint); }
        catch (NotePlacementException error)
        {
            if (OfferWiderLanes(settings, error) == true) return;
            SetMessage(error.Message);
            throw;
        }
        var listChanged = ListChanged(Settings, settings);
        Record(Settings, settings);
        Settings = settings;
        if (listChanged) RebuildLibrary();
        if (rulesChanged) _manualCheckpointPending = false;
        // The notes were placed again: the tiles changed, and the settings are why (user rule).
        Message = rulesChanged && Playback.Timeline is not null ? Text.Format("TilesChanged", Text["TilesBySettings"])
            : Text[rulesChanged ? "NotesRebuilt" : "SettingsApplied"];
        NotifyAll();
        SettingsCommitted?.Invoke();
    }

    private static bool ListChanged(VisualizerSettings before, VisualizerSettings after) =>
        before.SongSortKey != after.SongSortKey || before.SongSortHighFirst != after.SongSortHighFirst || before.SongGroup != after.SongGroup
        || before.HidePracticeMaps != after.HidePracticeMaps
        // The marks after the titles are in the app's language.
        || before.Language != after.Language;

    /// <summary>
    /// Undoes the newest change: every setting it touched goes back to what it was, the rest stays as it is now. The settings
    /// are kept even when the song cannot be placed with them (a lane change is often why they were changed), and nothing is
    /// offered again for them. Null when there is nothing to undo.
    /// </summary>
    public SettingsChange? RollBack()
    {
        if (_history.Count == 0) return null;
        var change = _history[^1];
        var restored = SettingsFields.Restore(Settings, change.Before, change.After);
        restored.EnsureValid();
        _history.RemoveAt(_history.Count - 1);
        var listChanged = ListChanged(Settings, restored);
        try
        {
            Playback.ApplySettings(restored, Checkpoint);
            Message = !restored.HasSameNoteRulesAs(change.After) && Playback.Timeline is not null
                ? Text.Format("TilesChanged", Text["TilesBySettings"]) : Text["SettingsApplied"];
        }
        catch (NotePlacementException error)
        {
            if (Playback.Timeline is { } timeline)
            {
                _declinedWiderLanes = (timeline, restored);
            }
            SetMessage(error.Message);
        }
        Settings = restored;
        _manualCheckpointPending = false;
        if (listChanged) RebuildLibrary();
        NotifyAll();
        SettingsCommitted?.Invoke();
        return change;
    }

    public void Reset()
    {
        RefreshPlaybackInfo();
        try
        {
            // A practice start tile is kept across resets.
            if (TryPrepare(() => Playback.Restart(Settings, Checkpoint))) _manualCheckpointPending = false;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { SetMessage(error.Message); }
        NotifyPlayback();
    }

    /// <summary>
    /// Puts the loaded song back as loading leaves it: from its beginning, stopped, nothing pressed or hidden. Closing the
    /// game link does this (user rule): without the game the notes must not run on from wherever it left them. Returns
    /// whether it did.
    /// </summary>
    public bool BackToLoaded()
    {
        FollowingGame = false;
        if (Playback.Timeline is null || Playback.State is PlaybackState.Off or PlaybackState.NoTimeline) return false;
        var prepared = TryPrepare(() => Playback.Reset(Settings, 1));
        if (prepared)
        {
            _checkpoint = 1;
            _manualCheckpointPending = false;
            ClearSectionText();
            Raise(nameof(Checkpoint));
            SetMessage(Text["LinkClosedReset"]);
        }
        NotifyPlayback();
        return prepared;
    }

    public string SongEndTitle => Text["SongEnded"];

    /// <summary>The offer's first button (user rule: always in the same place): the song from its beginning.</summary>
    public void RestartFromBeginning() => StartOver(1);

    /// <summary>The offer's second button: the song from its last checkpoint, which only shows when there is one.</summary>
    public void RestartAtLastCheckpoint()
    {
        if (Playback.Timeline?.Checkpoints is { Count: > 0 } markers) StartOver(markers[^1].Number);
    }

    /// <summary>
    /// The reset key. While the song-end offer was showing it takes the offer's own way back (user rule): the last
    /// checkpoint when that button shows, the beginning otherwise. <paramref name="offered"/> is read before the key
    /// counts as input, since any input puts the offer away.
    /// </summary>
    public void ResetFromKey(bool offered)
    {
        if (!offered) Reset();
        else if (LastCheckpointOffered) RestartAtLastCheckpoint();
        else RestartFromBeginning();
    }

    /// <summary>
    /// Puts the song back at a checkpoint and waits there, exactly as picking that checkpoint does. It goes through even
    /// when the number is the one already chosen, since the point is to play the song again.
    /// </summary>
    private void StartOver(int checkpoint)
    {
        NoteInput();
        if (Playback.Timeline is null) return;
        if (!TryPrepare(() => Playback.Reset(Settings, checkpoint)))
        {
            NotifyPlayback();
            return;
        }
        _checkpoint = checkpoint;
        _manualCheckpointPending = Playback.Timeline?.CheckpointsVerified != true;
        ClearSectionText();
        Raise(nameof(Checkpoint));
        NotifyPlayback();
    }

    private void ReturnToCheckpoint()
    {
        try { Playback.Reset(Settings, Checkpoint); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { SetMessage(error.Message); }
        NotifyPlayback();
    }

    private void SetSectionText(int tile)
    {
        _sectionText = tile.ToString(CultureInfo.InvariantCulture);
        Raise(nameof(SectionText));
    }

    private void ClearSectionText()
    {
        _sectionText = "";
        Raise(nameof(SectionText));
    }

    /// <summary>Whether the checkpoint arrows (and keys) have another checkpoint to go to; the buttons grey out otherwise.</summary>
    public bool CanMoveCheckpoint => Playback.Timeline is { CheckpointsVerified: true } timeline && timeline.Checkpoints.Count > 1;

    /// <summary>Selects the previous or next checkpoint, wrapping around at either end of the list.</summary>
    public bool MoveCheckpoint(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (!CanMoveCheckpoint) return false;
        RefreshPlaybackInfo();
        var markers = Playback.Timeline!.Checkpoints;
        var index = -1;
        for (var i = 0; i < markers.Count; i++)
            if (markers[i].Number == Checkpoint) { index = i; break; }
        if (index < 0) return false;
        var next = (index + direction + markers.Count) % markers.Count;
        var target = markers[next].Number;
        Checkpoint = target; // Manual selection prepares immediately, without automatic-activation delay.
        return Checkpoint == target;
    }

    public void Start()
    {
        // Ignored presses (for example J pressed as a gameplay key while playing) cost nothing.
        if (Playback.Start()) NotifyPlayback();
    }

    /// <summary>The start button. While the visualizer is off it says how to switch it on; the start key stays silent.</summary>
    public void StartFromButton()
    {
        if (Playback.State == PlaybackState.Off)
        {
            SetMessage(Text.Format("PressToActivate", Settings.VisualizerToggleKey));
            return;
        }
        Start();
    }

    /// <summary>Shows the effects and colors being edited in the settings dialog on the note field, before Apply.</summary>
    public void PreviewVisuals(VisualizerSettings edited)
    {
        var preview = Playback.Settings.WithVisuals(edited);
        if (preview.Validate().Count != 0) return;
        Playback.UpdateVisualSettings(preview);
        Raise(nameof(OverlayBrush));
    }

    /// <summary>Returns the note field to the applied effects and colors (the settings dialog closed without Apply).</summary>
    public void EndVisualPreview()
    {
        var restored = Playback.Settings.WithVisuals(Settings);
        if (restored.Validate().Count != 0) return;
        Playback.UpdateVisualSettings(restored);
        Raise(nameof(OverlayBrush));
    }

    /// <summary>Start key binding. A practice start listens to its first note's lane key instead.</summary>
    public void StartFromKey()
    {
        if (Playback.LaneStartTile is null) Start();
    }

    /// <summary>Lane key press: starts a practice section when it is the lane of the first note.</summary>
    public void StartFromLane(int tile)
    {
        if (Playback.StartsOnLane(tile)) Start();
    }

    /// <summary>Escape pauses together with the game's pause menu; the key itself still reaches the game.</summary>
    public bool PauseFromKey()
    {
        if (!Playback.Pause()) return false;
        AlignPausedNote();
        SetMessage(Text["EscPaused"]);
        NotifyPlayback();
        return true;
    }

    /// <summary>
    /// Puts the next note the player has not pressed on the judgment line while the song is paused, as the game restarts on
    /// the tile it stopped at (user rule). Off by the setting, the notes stay where the pause caught them.
    /// </summary>
    private void AlignPausedNote()
    {
        // The game decides where the song is while it drives the clock, so a pause stays exactly where it caught it.
        if (Playback.ClockFollowsGame || !Settings.AlignNextNoteOnPause || Presses.FirstUnpressed(Playback) is not int index) return;
        var note = Playback.Notes[index];
        Presses.SkipMissesBefore(Playback, note.HitTimeMs);
        // Anything from here on is ahead of the playhead again, so its marks are not presses on notes that were shown.
        Presses.ForgetFrom(Playback, index);
        Playback.MovePaused(note.HitTimeMs + Playback.Offsets.TotalMs);
    }

    /// <summary>The practice speed the main window's box shows; what is typed waits there until it is applied.</summary>
    public string SpeedText
    {
        get => _speedEdit ?? SpeedOf(Settings.PlaybackSpeedPercent);
        // The box owns the text while it is being typed: telling it again would put the value back mid-number.
        set => _speedEdit = value ?? "";
    }

    /// <summary>Drops what was typed and shows the applied speed again (Escape in the box, and after every change).</summary>
    public void CancelTypedSpeed()
    {
        _speedEdit = null;
        Raise(nameof(SpeedText));
    }

    /// <summary>Applies what the speed box holds (Enter or leaving the box). False with a message when it is not a number.</summary>
    public bool ApplyTypedSpeed()
    {
        var text = (_speedEdit ?? "").Trim().TrimEnd('%').Trim();
        if (_speedEdit is null) return true;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            SetMessage(Text["SpeedInvalid"]);
            CancelTypedSpeed();
            return false;
        }
        return ApplySpeed(percent);
    }

    /// <summary>The ◀ ▶ beside the speed box: 5% at a time, kept inside the range the settings accept.</summary>
    public bool StepSpeed(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var stepped = Math.Round(Settings.PlaybackSpeedPercent / SpeedStep) * SpeedStep + direction * SpeedStep;
        return ApplySpeed(Math.Clamp(stepped, SpeedStep, 1000));
    }

    /// <summary>
    /// Sets the practice speed from the main window (user rule). It is a settings change like any other: the notes are made
    /// again, rollback can undo it and it is saved.
    /// </summary>
    public bool ApplySpeed(double percent)
    {
        if (!double.IsFinite(percent))
        {
            SetMessage(Text["SpeedInvalid"]);
            CancelTypedSpeed();
            return false;
        }
        if (Math.Abs(percent - Settings.PlaybackSpeedPercent) < 1e-9)
        {
            CancelTypedSpeed();
            return true;
        }
        var next = Settings with { PlaybackSpeedPercent = percent };
        if (next.Validate().Count > 0)
        {
            SetMessage(Text["SpeedInvalid"]);
            CancelTypedSpeed();
            return false;
        }
        try { ApplySettings(next); }
        // The reason is already on the screen; the speed stays as it was.
        catch (NotePlacementException) { }
        CancelTypedSpeed();
        return true;
    }

    private static string SpeedOf(double percent) => percent.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// While paused, a lane key resumes on that lane's note near the pause (the game counts down before play
    /// continues): the note is placed on the judgment line and every other note moves by the same amount.
    /// </summary>
    public bool ResumeFromLane(int tile)
    {
        if (Playback.State != PlaybackState.Paused) return false;
        // Following the game, a lane key only hands the playhead back; the game says where to carry on from.
        if (Playback.ClockFollowsGame) return Playback.Start();
        if (Presses.FindResumeNote(Playback, tile, ResumeSearchMs) is not int index)
        {
            SetMessage(Text.Format("ResumeNoNote", LaneKeyLabel(tile)));
            return false;
        }
        var note = Playback.Notes[index];
        Presses.SkipMissesBefore(Playback, note.HitTimeMs);
        Presses.ForgetFrom(Playback, index);
        if (!Playback.ResumeAt(note.HitTimeMs + Playback.Offsets.TotalMs)) return false;
        SetMessage(Text.Format("ResumedAtLane", LaneKeyLabel(tile)));
        NotifyPlayback();
        return true;
    }

    public void PauseResume()
    {
        var pausing = Playback.State != PlaybackState.Paused;
        var changed = pausing ? Playback.Pause() : Playback.Start();
        if (changed && pausing) AlignPausedNote();
        if (changed) NotifyPlayback();
    }

    public void Toggle()
    {
        // Accepting a lane offer already re-prepares the switched-on session, so nothing is retried.
        TryPrepare(Playback.Toggle, () => { });
        NotifyPlayback();
    }

    private string LaneKeyLabel(int tile)
    {
        var tokens = Settings.GetLaneKeyTokens();
        return tile >= 1 && tile <= tokens.Count ? tokens[tile - 1] : tile.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Visual feedback only: marks the note this lane press most likely hit. <paramref name="starting"/> is the press that
    /// started playback, which plays the whole first chord.
    /// </summary>
    public bool PressLane(int tile, bool starting = false)
    {
        if (Presses.Press(Playback, tile, Settings.KeyPressWindowMs) is not int index) return false;
        if (starting) Presses.PressTogether(Playback, index);
        return true;
    }

    /// <summary>Visual feedback only: whether this lane release ended a held long note early.</summary>
    public bool ReleaseLane(int tile) => Presses.Release(Playback, tile, Settings.KeyPressWindowMs);

    public void SetMessage(string message)
    {
        Message = message;
        Raise(nameof(Message));
        Raise(nameof(MessageText));
    }

    public void SetInputStatus(string status)
    {
        InputStatus = status;
        Raise(nameof(InputStatus));
    }

    /// <summary>
    /// How long the song has to be over, with nothing touched, before the screen offers to play it again (user rule).
    /// </summary>
    private const double SongEndQuietMs = 1000;
    private double _lastInputMs = double.NegativeInfinity;
    private bool _songEnded;

    /// <summary>
    /// Says the user just did something, so the song-end offer waits again. Every key the app sees and every click on the
    /// window come through here; it costs one assignment on the lane-key path.
    /// </summary>
    public void NoteInput()
    {
        _lastInputMs = PlaybackClock.NowMs;
        if (!_songEnded) return;
        _songEnded = false;
        Raise(nameof(ShowSongEndNotice));
    }

    /// <summary>Whether the note area offers to play the song again: it is over and nothing has been touched since.</summary>
    public bool ShowSongEndNotice => _songEnded;

    /// <summary>
    /// Whether the offer includes the last checkpoint. Only with automatic checkpoints on and a song that has one (user
    /// rule); the song's own start is not counted as a checkpoint.
    /// </summary>
    public bool ShowLastCheckpointButton => _songEnded && LastCheckpointOffered;

    private bool LastCheckpointOffered => Settings.AutoCheckpointAdvance
        && Playback.Timeline is { CheckpointsVerified: true } timeline && timeline.CheckpointCount > 0;

    public void RefreshPlaybackInfo()
    {
        RefreshMessageAge();
        // The song is over once its last note is gone; the offer waits for a quiet second after that.
        var ended = Playback.PastLastNoteMs is double past && past >= 0
            && PlaybackClock.NowMs - Math.Max(PlaybackClock.NowMs - past, _lastInputMs) >= SongEndQuietMs;
        if (ended != _songEnded)
        {
            _songEnded = ended;
            Raise(nameof(ShowSongEndNotice));
            Raise(nameof(ShowLastCheckpointButton));
            Raise(nameof(SongEndTitle));
        }
        var state = Playback.State;
        if (!_everActivated && state != PlaybackState.Off)
        {
            _everActivated = true;
            Raise(nameof(ShowActivationHint));
        }
        long countdown = -1;
        if (state == PlaybackState.Playing)
        {
            if (Playback.StartWaitRemainingMs is var wait and > 0) countdown = (long)Math.Round(wait / 100);
            else if (Playback.NextHitRemainingMs is double remaining && remaining > Settings.NoteTravelTimeMs)
                countdown = 1_000_000 + (long)Math.Round(remaining / 100);
        }
        if (_lastStateKey != (state, countdown))
        {
            _lastStateKey = (state, countdown);
            Raise(nameof(StateLabel));
        }
        if (Playback.AutoCheckpointActive && state is PlaybackState.Playing or PlaybackState.Paused && !_manualCheckpointPending
            && Playback.ActiveCheckpoint is int checkpoint && checkpoint != _checkpoint)
        {
            _checkpoint = checkpoint;
            Raise(nameof(Checkpoint));
        }
        // The mini-game has no notes, so the note area says the song is in one and when the notes come back.
        var notice = state is PlaybackState.Playing or PlaybackState.Paused
            && Playback.SectionAt(Playback.TimelineTimeMs) is var band && band is not null
            ? Text.Format("MiniGame", ((band.Value.EndMs - Playback.TimelineTimeMs) / 1000).ToString("0.0", CultureInfo.InvariantCulture))
            : "";
        if (notice != _sectionNotice)
        {
            var shown = _sectionNotice.Length > 0;
            _sectionNotice = notice;
            Raise(nameof(SectionNotice));
            if (shown != (notice.Length > 0)) Raise(nameof(ShowSectionNotice));
        }
    }

    /// <summary>What the note area says while the game plays a mini-game (free roam) by itself; empty at any other time.</summary>
    public string SectionNotice => _sectionNotice;

    public bool ShowSectionNotice => _sectionNotice.Length > 0;

    private string CheckpointLabel(TimelineCheckpoint checkpoint)
    {
        if (checkpoint.Number == 1) return Text["FromBeginning"];
        var name = $"{Text["Checkpoint"]} {checkpoint.Number - 1}";
        if (checkpoint.SourceFloor is not int floor) return name;
        return checkpoint.RespawnFloor is int respawn && respawn != floor
            ? $"{name} · {Text["Tile"]} {floor} → {respawn}"
            : $"{name} · {Text["Tile"]} {floor}";
    }

    /// <summary>
    /// Raises the playback bindings after something outside the view model moved the session, such as game sync starting
    /// it. Without this the window keeps showing the state it last heard about, overlay and all.
    /// </summary>
    public void NotifyPlaybackChanged() => NotifyPlayback();

    /// <summary>
    /// Whether the game's clock is driving the playhead. While it is, the app must not move the playhead on its own:
    /// putting the next note on the judgment line would fight the game, which owns where the song is.
    /// </summary>
    public bool FollowingGame
    {
        get => Playback.ClockFollowsGame;
        set
        {
            if (Playback.ClockFollowsGame == value) return;
            Playback.ClockFollowsGame = value;
            // Everything the game took over goes back at once: the hidden tiles, the display calibration (which is only
            // suspended, never lost), the checkpoint advance, the pause alignment and the lane-key resume.
            if (!value) Playback.HideNotesBeforeMs = double.NegativeInfinity;
            NotifyPlayback();
        }
    }

    private void NotifyPlayback()
    {
        if (Playback.State != PlaybackState.Off) _everActivated = true;
        foreach (var name in PlaybackProperties) Raise(name);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void NotifyAll()
    {
        if (Playback.State != PlaybackState.Off) _everActivated = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
