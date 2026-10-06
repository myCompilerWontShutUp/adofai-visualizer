using System.Globalization;
using System.Windows.Media;
using Visualizer.App.Rendering;
using Visualizer.Application.Playback;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;

namespace Visualizer.App.ViewModels;

/// <summary>
/// Translucent song info over the note field while a loaded song is stopped (reset or paused): title, artist and difficulty,
/// start point with speed and checkpoint state, and the hit tile count. Recomputed only on commands, never per frame.
/// </summary>
public sealed partial class MainViewModel
{
    private (VisualizerSettings Settings, Brush Brush)? _overlayBrush;
    private (IReadOnlyList<VisualizerNote> Notes, VisualizerSettings Settings, int Count)? _magicCircles;

    /// <summary>
    /// Whether the song's details are folded away so the tiles behind them can be seen. It is a button on the band and
    /// nothing else: it lasts while the app runs and is never written to the settings (user rule).
    /// </summary>
    private bool _songOverlayHidden;

    public bool SongOverlayHidden
    {
        get => _songOverlayHidden;
        set
        {
            if (_songOverlayHidden == value) return;
            _songOverlayHidden = value;
            Raise(nameof(SongOverlayHidden));
            Raise(nameof(ShowSongOverlay));
            Raise(nameof(ShowOverlayBand));
            Raise(nameof(HideOverlayLabel));
        }
    }

    /// <summary>What the fold button says: it has to name what pressing it does.</summary>
    public string HideOverlayLabel => Text[SongOverlayHidden ? "ShowSongInfoAgain" : "HideSongInfo"];

    public bool ShowSongOverlay => !SongOverlayHidden && Settings.ShowSongInfo && _loadedEntry is not null
        && Playback.State is PlaybackState.Waiting or PlaybackState.Paused && Playback.Notes.Count > 0;

    /// <summary>
    /// Whether the speed, the practice tile and the editor are on the field (user rule): a loaded song that has not
    /// started. They go once it plays and stay away while it is paused, where the notes matter more than the buttons.
    /// </summary>
    public bool ShowFieldControls => _loadedEntry is not null && Playback.State == PlaybackState.Waiting && Playback.Notes.Count > 0;

    /// <summary>
    /// Whether the band over the field is there at all. The details and the controls share one wash with nothing between
    /// them (user rule), so the band shows while either half does. Folded, none of it is (user rule): only the button that
    /// brings it back stays.
    /// </summary>
    public bool ShowOverlayBand => !SongOverlayHidden && (ShowSongOverlay || ShowFieldControls);

    /// <summary>
    /// Whether the fold button is on the field. It is only worth showing while there are details to fold, which is the
    /// same song and state the details themselves need; folded, it stays to bring them back.
    /// </summary>
    public bool ShowHideOverlay => Settings.ShowSongInfo && _loadedEntry is not null
        && Playback.State is PlaybackState.Waiting or PlaybackState.Paused && Playback.Notes.Count > 0;

    public string OverlayInfo
    {
        get
        {
            var info = _loadedEntry?.Info;
            var parts = new List<string>();
            if (info?.Artist is { } artist) parts.Add(Text.Format("Overlay.Artist", artist));
            if (info?.Difficulty is int difficulty) parts.Add(Text.Format("Overlay.Difficulty", difficulty));
            return parts.Count == 0 ? Text["Overlay.NoInfo"] : string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Where it starts and whether checkpoints follow playback. The speed and the practice tile are buttons on the field
    /// now, so they are not written out here as well (user rule).
    /// </summary>
    public string OverlayProgress
    {
        get
        {
            var speed = Playback.Settings.PlaybackSpeedPercent;
            var checkpoints = speed != 100 || Playback.StartTile is not null ? Text["Overlay.CheckpointsOff"]
                : Text[Playback.AutoCheckpointActive ? "Overlay.AutoOn" : "Overlay.AutoOff"];
            if (Playback.StartTile is not null) return checkpoints;
            var start = CheckpointOptions.FirstOrDefault(option => option.Number == Checkpoint)?.Label ?? Text["FromBeginning"];
            return string.Join(" · ", start, checkpoints);
        }
    }

    /// <summary>
    /// Song length (time left while paused) and the pressed hit tiles of the song, or those left from a practice section start.
    /// Auto tiles and long note ends are not counted.
    /// </summary>
    public string OverlayTiles => string.Join(" · ", OverlayTileParts.Select(part => part.Text));

    /// <summary>
    /// The bottom line, piece by piece, with each piece saying whether one of the controls under it has moved it away from
    /// what the song alone would show (user rule): the view underlines those. The speed stretches the clock and the magic
    /// circle windows with it, a practice tile cuts the song short, and a slot says which version of it is laid out.
    /// </summary>
    public IReadOnlyList<OverlayPart> OverlayTileParts
    {
        get
        {
            var timeline = Playback.Timeline;
            var total = timeline?.CountPressedHits() ?? 0;
            var section = Playback.StartTile is not null;
            var speed = Playback.Settings.PlaybackSpeedPercent != 100;
            var parts = new List<OverlayPart>
            {
                new(Text.Format("Overlay.Length", Clock(LengthMs())), speed || section),
                new(Text.Format("Overlay.Remaining", Clock(LeftMs())), speed || section),
                new(section
                    ? Text.Format("Overlay.TilesLeft", timeline?.CountPressedHits(Playback.StartHitIndex) ?? 0, total)
                    : Text.Format("Overlay.Tiles", total), section)
            };
            // The magic circle count is shown only while its timing bar is switched on, and it is never marked as moved
            // (user rule): a magic circle is a run of equal gaps, so the speed leaves the count alone, and a count that
            // only shrinks with the notes is not what the underline is for.
            if (Playback.Settings.MagicCircleJudgmentLine)
                parts.Add(new OverlayPart(Text.Format("Overlay.MagicCircles", MagicCircleCount()), false));
            // The slot is chosen on the field now, so this is where it is still read off (user rule).
            if (SelectedSlot > 0) parts.Add(new OverlayPart(Text.Format("SlotNumber", SelectedSlot), true));
            return parts;
        }
    }

    /// <summary>Magic circle sections of the prepared notes, found once per note list and settings snapshot.</summary>
    private int MagicCircleCount()
    {
        var notes = Playback.Notes;
        var settings = Playback.Settings;
        if (_magicCircles is { } cached && ReferenceEquals(cached.Notes, notes) && ReferenceEquals(cached.Settings, settings))
            return cached.Count;
        var count = MagicCircleFinder.Find(notes, settings).Count;
        _magicCircles = (notes, settings, count);
        return count;
    }

    /// <summary>The prepared song length, and what is left of it (all of it unless it is paused part way through).</summary>
    private double LengthMs()
    {
        var notes = Playback.Notes;
        return notes.Count == 0 ? 0 : (notes[^1].HoldEndMs ?? notes[^1].HitTimeMs) - notes[0].HitTimeMs;
    }

    private double LeftMs()
    {
        var notes = Playback.Notes;
        if (notes.Count == 0) return 0;
        var end = notes[^1].HoldEndMs ?? notes[^1].HitTimeMs;
        return Playback.State == PlaybackState.Paused ? end - Playback.TimelineTimeMs : LengthMs();
    }

    private static string Clock(double milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>White on dark lanes, black on light ones (see <see cref="NoteField.OverlayTextColor"/>).</summary>
    public Brush OverlayBrush
    {
        get
        {
            var settings = Playback.Settings;
            if (_overlayBrush is { } cached && ReferenceEquals(cached.Settings, settings)) return cached.Brush;
            var brush = new SolidColorBrush(NoteField.OverlayTextColor(settings));
            brush.Freeze();
            _overlayBrush = (settings, brush);
            return brush;
        }
    }

    private (VisualizerSettings Settings, Brush Brush)? _overlayBack;

    /// <summary>
    /// A wash behind the overlay text and the controls on the field, so the notes running past them do not break the
    /// letters up (user rule). It is the opposite of the text: dark behind white letters, light behind black ones.
    /// </summary>
    public Brush OverlayBackBrush
    {
        get
        {
            var settings = Playback.Settings;
            if (_overlayBack is { } cached && ReferenceEquals(cached.Settings, settings)) return cached.Brush;
            var text = NoteField.OverlayTextColor(settings);
            var brush = new SolidColorBrush(text.R > 128
                ? Color.FromArgb(224, 4, 6, 12)
                : Color.FromArgb(224, 248, 249, 252));
            brush.Freeze();
            _overlayBack = (settings, brush);
            return brush;
        }
    }
}

/// <summary>One piece of the bottom overlay line; <see cref="Changed"/> is underlined, since a control moved it.</summary>
public sealed record OverlayPart(string Text, bool Changed);
