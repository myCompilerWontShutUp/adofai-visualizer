using Visualizer.Application.Cache;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;

namespace Visualizer.App.ViewModels;

/// <summary>
/// Note preparation. When chords cannot be placed and extra lane placement would place the song, the user is asked once per song
/// and settings to enable it; spare lanes only ever hold chord notes, so that question is asked for an original song only (user
/// rule). Otherwise a wider maximum is offered: the smallest lane count that places the whole song, which for a saved edit is the
/// lane count it was made for. Nothing changes without consent.
/// </summary>
public sealed partial class MainViewModel
{
    private (ParsedTimeline Timeline, VisualizerSettings Settings)? _declinedExtraLanes;
    private (ParsedTimeline Timeline, VisualizerSettings Settings)? _declinedWiderLanes;
    private string _notice = "";

    /// <summary>Asks whether to enable extra lane placement when chords cannot be placed. Null never switches.</summary>
    public Func<string, bool>? ConfirmExtraLanes { get; set; }
    /// <summary>Asks whether to raise the maximum tile count (to 6 or 8) when extra lanes cannot help. Null never switches.</summary>
    public Func<string, bool>? ConfirmWiderLanes { get; set; }
    /// <summary>While link auto loads the game's song: a wider maximum is taken without asking, and spare lanes are not offered.</summary>
    private bool _widenWithoutAsking;

    /// <summary>
    /// Loads the song the game moved to for link auto (user rule): the maximum tile count first goes up, without asking, to
    /// what the song needs whole: its widest chord (an eight-note chord takes eight lanes, straight from four if need be, spare
    /// lanes or not). Should it still not place (a saved edit made for more lanes), the smallest wider count that does is
    /// taken the same way. Never down; each change is a settings step of its own, so rollback undoes it.
    /// </summary>
    public void LoadForGame(MapCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _widenWithoutAsking = true;
        int? widened = null;
        try
        {
            if (entry.Timeline is { } timeline)
            {
                var speed = Settings.KeepSpeedOnLoad ? Settings : Settings with { PlaybackSpeedPercent = 100 };
                var need = PlaybackSession.WidestChord(timeline, speed);
                if (need > Settings.MaximumTileCount
                    && VisualizerSettings.Modes.FirstOrDefault(count => count >= need) is var wider and > 0 && Widen(wider)) widened = wider;
            }
            LoadChosen(entry, null);
        }
        finally { _widenWithoutAsking = false; }
        // A song that still did not place keeps saying why.
        if (widened is { } count && Playback.Notes.Count > 0) SetMessage(Text.Format("AutoWiderLanes", count));
    }

    /// <summary>
    /// link auto found the game's song loaded already (by hand, before link auto was on): it gets the lanes it needs whole as
    /// well, the song prepared again with them (user rule). False when nothing changed.
    /// </summary>
    public bool WidenLoadedForGame()
    {
        if (LoadedEntry is null || Playback.Timeline is not { } timeline) return false;
        var need = PlaybackSession.WidestChord(timeline, Settings);
        int wider;
        if (need > Settings.MaximumTileCount) wider = VisualizerSettings.Modes.FirstOrDefault(count => count >= need);
        // A song that does not place at all takes the smallest count that places it, as loading it here would.
        else if (Playback.Notes.Count == 0)
            wider = VisualizerSettings.Modes.Where(count => count > Settings.MaximumTileCount)
                .FirstOrDefault(count => Settings with { MaximumTileCount = count } is var proposed && proposed.Validate().Count == 0 && Places(timeline, proposed));
        else return false;
        if (wider == 0 || !Widen(wider)) return false;
        SetMessage(Text.Format("AutoWiderLanes", wider));
        return true;
    }

    /// <summary>Raises the maximum tile count to <paramref name="count"/> as a settings step of its own; false when it cannot be.</summary>
    private bool Widen(int count)
    {
        var before = Settings;
        var proposed = before with { MaximumTileCount = count };
        if (proposed.Validate().Count != 0) return false;
        // The song still loaded is prepared again with it; the game's song is loaded right after.
        try { Playback.ApplySettings(proposed, Checkpoint); }
        catch (NotePlacementException) { }
        Record(before, proposed);
        Settings = proposed;
        _manualCheckpointPending = false;
        NotifyAll();
        SettingsCommitted?.Invoke();
        return true;
    }

    /// <summary>Runs a note preparation; on a placement failure offers a lane change and retries after consent.</summary>
    private bool TryPrepare(Action prepare, Action? retry = null)
    {
        _notice = "";
        NotePlacementException failure;
        try
        {
            prepare();
            return true;
        }
        catch (NotePlacementException error) { failure = error; }
        if (!OfferLayoutChange(Settings, failure))
        {
            SetMessage(failure.Message);
            return false;
        }
        try
        {
            (retry ?? prepare)();
            return true;
        }
        catch (NotePlacementException error)
        {
            SetMessage(error.Message);
            return false;
        }
    }

    /// <summary>Extra lanes when they would place the song (accepted or declined, a wider maximum is then not asked); otherwise a wider maximum.</summary>
    private bool OfferLayoutChange(VisualizerSettings failed, NotePlacementException error) => _widenWithoutAsking
        ? OfferWiderLanes(failed, error, _ => true) ?? false
        : OfferExtraLanes(failed, error) ?? OfferWiderLanes(failed, error) ?? false;

    private bool? OfferExtraLanes(VisualizerSettings failed, NotePlacementException error)
    {
        // Spare lanes take chord notes only, so an edit that needs lanes the screen does not show is offered a wider maximum
        // instead; extra lane placement itself still works for an edited song once it is on (user rule).
        if (ConfirmExtraLanes is not { } confirm || Playback.Layout is not null
            || !failed.CanUseExtraLanes || failed.ExtraLanes || Playback.Timeline is not { } timeline) return null;
        var proposed = failed with { ExtraLanes = true };
        if (proposed.Validate().Count != 0 || proposed.GetExtraTiles().Count == 0) return null;
        return Offer(confirm, proposed, failed, timeline, ref _declinedExtraLanes,
            Text.Format("ExtraLanesQuestion", error.Message), Text["ExtraLanesApplied"]);
    }

    private bool? OfferWiderLanes(VisualizerSettings failed, NotePlacementException error, Func<string, bool>? answer = null)
    {
        // Every larger key count is tried, smallest first: chords usually need 6 or 8, and an edited song can need any.
        if ((answer ?? ConfirmWiderLanes) is not { } confirm || failed.MaximumTileCount >= 8 || Playback.Timeline is not { } timeline) return null;
        foreach (var wider in VisualizerSettings.Modes.Where(count => count > failed.MaximumTileCount))
        {
            var proposed = failed with { MaximumTileCount = wider };
            if (proposed.Validate().Count != 0 || !Places(timeline, proposed)) continue;
            // A saved edit was made for the lanes it uses, so it says so instead of naming chords as the reason.
            var question = Playback.Layout is null
                ? Text.Format("WiderLanesQuestion", error.Message, failed.MaximumTileCount, wider)
                : Text.Format("EditedLanesQuestion", error.Message, wider, failed.MaximumTileCount);
            return Offer(confirm, proposed, failed, timeline, ref _declinedWiderLanes, question, Text.Format("WiderLanesApplied", wider));
        }
        return null;
    }

    private bool Places(ParsedTimeline timeline, VisualizerSettings settings)
    {
        try
        {
            // A saved edit decides lanes as well, so a lane change is tried with it in place.
            new PlaybackSession(new StopwatchClock()) { Layout = Playback.Layout }.LoadParsedTimeline(timeline, settings);
            return true;
        }
        catch (NotePlacementException) { return false; }
    }

    /// <summary>Null when <paramref name="proposed"/> would not place the whole song; false when declined; true when applied.</summary>
    private bool? Offer(Func<string, bool> confirm, VisualizerSettings proposed, VisualizerSettings failed, ParsedTimeline timeline,
        ref (ParsedTimeline Timeline, VisualizerSettings Settings)? declined, string question, string applied)
    {
        if (proposed.Validate().Count != 0 || !Places(timeline, proposed)) return null;
        // link auto does not ask, so an earlier no to this song does not hold it back either.
        if (!_widenWithoutAsking && declined is { } previous && ReferenceEquals(previous.Timeline, timeline) && previous.Settings == failed) return false;
        if (!confirm(question))
        {
            declined = (timeline, failed);
            return false;
        }
        try { Playback.ApplySettings(proposed, Checkpoint); }
        catch (NotePlacementException retryError)
        {
            SetMessage(retryError.Message);
            return false;
        }
        // Kept as its own step, so rollback can undo the forced change alone (after the change that made it necessary).
        if (failed != Settings) Record(Settings, failed);
        Record(failed, proposed);
        Settings = proposed;
        _manualCheckpointPending = false;
        _notice = applied + " ";
        Message = applied;
        NotifyAll();
        SettingsCommitted?.Invoke();
        return true;
    }

    private string TakeNotice()
    {
        var notice = _notice;
        _notice = "";
        return notice;
    }
}
