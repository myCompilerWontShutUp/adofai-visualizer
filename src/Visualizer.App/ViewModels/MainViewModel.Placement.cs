using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;

namespace Visualizer.App.ViewModels;

/// <summary>
/// Note preparation. When chords cannot be placed, a wider maximum is offered: the smallest lane count that places the whole
/// song, which for a saved edit is the lane count it was made for. Nothing changes without consent. (Extra lane placement and
/// link auto's own widening are gone: the maximum tile count's auto takes their place, user rule.)
/// </summary>
public sealed partial class MainViewModel
{
    private (ParsedTimeline Timeline, VisualizerSettings Settings)? _declinedWiderLanes;
    private string _notice = "";

    /// <summary>Asks whether to raise the maximum tile count (to 6 or 8) when the song cannot be placed. Null never switches.</summary>
    public Func<string, bool>? ConfirmWiderLanes { get; set; }

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
        if (OfferWiderLanes(Settings, failure) != true)
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

    private bool? OfferWiderLanes(VisualizerSettings failed, NotePlacementException error)
    {
        // Every larger key count is tried, smallest first: chords usually need 6 or 8, and an edited song can need any.
        if (ConfirmWiderLanes is not { } confirm || failed.MaximumTileCount >= 8 || Playback.Timeline is not { } timeline) return null;
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
        if (declined is { } previous && ReferenceEquals(previous.Timeline, timeline) && previous.Settings == failed) return false;
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
