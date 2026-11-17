using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Visualizer.Application.Library;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;

namespace Visualizer.App.Rendering;

/// <summary>
/// The note sheet of the edit screen. Time runs upward, so a later note is higher and w moves forward. All eight lanes
/// are always drawn in the main screen's alternating colors; only the lane numbers tell which lanes the rules use. Only
/// the notes inside the view are drawn, brushes are frozen and nothing is allocated per note, so scrolling and zooming
/// stay smooth on long songs.
/// </summary>
public sealed class NoteEditField : FrameworkElement
{
    /// <summary>Pixels per millisecond: how tightly the song is packed. Ctrl+wheel moves it between these ends.</summary>
    public const double MinimumZoom = 0.02;
    public const double MaximumZoom = 6;
    /// <summary>The strip at the top that names the lanes 1..8.</summary>
    public const double HeaderHeight = 22;
    private const double NumberSize = 10;

    private NoteEditSession? _session;
    private NoteFieldPalette? _palette;
    private Brush? _text;
    private Brush? _muted;
    private Brush? _pickedNote;
    private Brush? _pickedChord;
    private Brush? _pickedTrill;
    private Pen? _chordBar;
    private VisualizerSettings _settings = new();
    private HashSet<int> _ruleTiles = [];
    private int _firstTile = 1;
    private double _zoom = 0.2;
    private double _topMs;
    private Rect? _band;
    private IReadOnlyList<EditNote> _ruleNotes = [];
    private bool _showRuleNotes;
    private Brush? _ghost;

    private static readonly Brush Field = Frozen(new SolidColorBrush(NoteFieldPalette.FieldColor));
    private static readonly Brush Header = Frozen(new SolidColorBrush(Color.FromArgb(235, 14, 18, 26)));
    private static readonly Pen Separator = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255))), 1));
    private static readonly Pen Second = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(14, 255, 255, 255))), 1));
    private static readonly Brush BandFill = Frozen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)));
    private static readonly Pen BandEdge = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255))), 1));
    // A picked note gets a white ring with a dark gap around it, which reads on any note or lane color.
    private static readonly Pen Ring = Frozen(new Pen(Brushes.White, 1.5));
    private static readonly Brush Gap = Frozen(new SolidColorBrush(NoteFieldPalette.FieldColor));
    private static readonly Typeface Face = new("Segoe UI");

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    public NoteEditSession? Session
    {
        get => _session;
        set { _session = value; _firstTile = FirstTile(); InvalidateVisual(); }
    }

    /// <summary>
    /// The main screen's first lane for this song, which the lane colors count from. With the auto tile count that depends
    /// on the lanes the notes sit on, as they were when the sheet opened.
    /// </summary>
    private int FirstTile() => _settings.GetDisplayTiles(_session?.Notes.Select(note => note.Tile) ?? []).FirstOrDefault(1);

    public VisualizerSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value ?? new VisualizerSettings();
            _ruleTiles = _settings.GetVisibleTiles().ToHashSet();
            _firstTile = FirstTile();
            _palette = null;
            _text = null;
            _muted = null;
            _pickedNote = null;
            _pickedChord = null;
            _pickedTrill = null;
            _chordBar = null;
            InvalidateVisual();
        }
    }

    /// <summary>Time at the top edge of the notes (just under the header), in ms: the latest time on screen.</summary>
    public double TopMs
    {
        get => _topMs;
        set { _topMs = value; InvalidateVisual(); }
    }

    public double Zoom
    {
        get => _zoom;
        set { _zoom = Math.Clamp(value, MinimumZoom, MaximumZoom); InvalidateVisual(); }
    }

    private double SheetHeight => Math.Max(1, ActualHeight - HeaderHeight);

    public double WindowMs => SheetHeight / Zoom;

    /// <summary>The rubber band being dragged, in screen points; null when nothing is being dragged.</summary>
    public Rect? Band
    {
        get => _band;
        set { _band = value; InvalidateVisual(); }
    }

    public double TimeAt(double y) => _topMs - (y - HeaderHeight) / Zoom;

    public double YOf(double timeMs) => HeaderHeight + (_topMs - timeMs) * Zoom;

    /// <summary>The note under a point, or -1. Notes are hit tested with the same box they are drawn in.</summary>
    public int NoteAt(Point point)
    {
        if (_session is null || point.Y < HeaderHeight) return -1;
        for (var index = 0; index < _session.Notes.Count; index++)
            if (BoxOf(_session.Notes[index]) is { } box && box.Contains(point)) return index;
        return -1;
    }

    public IReadOnlyList<int> NotesIn(Rect band)
    {
        var found = new List<int>();
        if (_session is null) return found;
        for (var index = 0; index < _session.Notes.Count; index++)
            if (BoxOf(_session.Notes[index]) is { } box && box.IntersectsWith(band)) found.Add(index);
        return found;
    }

    /// <summary>Scrolls so a note is on screen without sticking to an edge.</summary>
    public void Reveal(int index)
    {
        if (_session is null || index < 0 || index >= _session.Notes.Count || ActualHeight <= 0) return;
        var time = _session.Notes[index].ShowTimeMs;
        var margin = WindowMs * 0.25;
        if (time > _topMs - margin) TopMs = time + margin;
        else if (time < _topMs - WindowMs + margin) TopMs = time + WindowMs - margin;
    }

    private double LaneWidth => _session is null || _session.Tiles == 0 ? 1 : ActualWidth / _session.Tiles;

    private Rect? BoxOf(EditNote note)
    {
        if (_session is null || ActualWidth <= 0) return null;
        var lane = _session.LaneOf(note.Tile);
        if (lane < 0) return null;
        var width = LaneWidth;
        var height = Math.Max(6, NoteLayout.BaseThicknessDip * _settings.NoteThickness * 0.6);
        var y = YOf(note.ShowTimeMs) - height / 2;
        var top = note.IsHold ? YOf(note.HoldEndMs!.Value) : y;
        return new Rect(width * lane + 3, Math.Min(y, top), width - 6, Math.Max(height, y - top + height));
    }

    /// <summary>
    /// Where the rules would put the notes with the settings as they are now. Drawn behind the edited ones, dark enough
    /// to read as a backdrop, and never picked up by the mouse: it is a reference, not something to edit.
    /// </summary>
    public IReadOnlyList<EditNote> RuleNotes
    {
        get => _ruleNotes;
        set { _ruleNotes = value ?? []; InvalidateVisual(); }
    }

    public bool ShowRuleNotes
    {
        get => _showRuleNotes;
        set { if (_showRuleNotes == value) return; _showRuleNotes = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        drawing.DrawRectangle(Field, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_session is null || ActualWidth <= 0 || ActualHeight <= HeaderHeight) return;
        var palette = _palette ??= NoteFieldPalette.From(_settings);
        _text ??= Frozen(new SolidColorBrush(NoteFieldPalette.OverlayText(_settings)));
        _muted ??= Frozen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)));
        // Picked notes turn lighter and get a ring; the judgment line color (often yellow) was too faint on the notes.
        _pickedNote ??= Frozen(new SolidColorBrush(Lighter(_settings.NoteColor)));
        _pickedChord ??= Frozen(new SolidColorBrush(Lighter(_settings.ChordColor)));
        _pickedTrill ??= Frozen(new SolidColorBrush(Lighter(_settings.TrillColor)));
        _chordBar ??= Frozen(new Pen(palette.Chord, 2));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var lanes = _session.Tiles;
        var width = LaneWidth;
        var sheet = new Rect(0, HeaderHeight, ActualWidth, SheetHeight);

        // Lanes alternate exactly as on the main screen, counted from its first lane, so a tile looks the same in both.
        for (var lane = 0; lane < lanes; lane++)
        {
            var box = new Rect(lane * width, HeaderHeight, width, SheetHeight);
            drawing.DrawRectangle(palette.LaneBackgrounds[LaneColorIndex(_session.Lanes[lane], _firstTile, palette.LaneBackgrounds.Length)], null, box);
            if (lane > 0) drawing.DrawLine(Separator, new Point(lane * width, HeaderHeight), new Point(lane * width, ActualHeight));
        }

        // A faint line every second keeps the sense of time without a busy grid.
        drawing.PushClip(new RectangleGeometry(sheet));
        var bottomMs = _topMs - WindowMs;
        for (var second = Math.Ceiling(Math.Max(0, bottomMs) / 1000) * 1000; second <= _topMs; second += 1000)
        {
            var y = YOf(second);
            drawing.DrawLine(Second, new Point(0, y), new Point(ActualWidth, y));
        }

        // The rules' own placement, under everything the editor draws, so a moved note can be compared with where it
        // would have gone. Only drawn: the mouse works on the edited notes alone.
        if (_showRuleNotes && _ruleNotes.Count > 0)
        {
            _ghost ??= Frozen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)));
            for (var index = LowerBound(_ruleNotes, bottomMs - 2000); index < _ruleNotes.Count; index++)
            {
                if (_ruleNotes[index].ShowTimeMs > _topMs + 2000) break;
                if (BoxOf(_ruleNotes[index]) is { } ghost) drawing.DrawRectangle(_ghost, null, ghost);
            }
        }

        // Only the notes inside the view are touched: the list is in time order, so the ends are found by search.
        var notes = _session.Notes;
        var first = LowerBound(notes, bottomMs - 2000);
        var numbers = Zoom > 0.08;
        Func<int, bool> tap = k => notes[k].Chord == 0 && notes[k].HoldEndMs is null;
        Func<int, double> time = k => notes[k].HitTimeMs;
        for (var index = first; index < notes.Count; index++)
        {
            var note = notes[index];
            if (note.ShowTimeMs > _topMs + 2000) break;
            if (BoxOf(note) is not { } box) continue;
            var picked = _session.Selection.Contains(index);
            if (picked) drawing.DrawRectangle(Gap, Ring, new Rect(box.X - 3, box.Y - 3, box.Width + 6, box.Height + 6));
            // Trills in their own color, as on the main screen, by the same rule over the notes as edited.
            var trill = note.Chord == 0 && TrillNotes.IsTrill(notes.Count, index, _settings.TrillWindowMs, tap, time);
            drawing.DrawRectangle(note.Chord > 0 ? picked ? _pickedChord : palette.Chord
                : trill ? picked ? _pickedTrill : palette.Trill : picked ? _pickedNote : palette.Note, null, box);
            // A thin bar ties each of a chord's notes to the next one to its right, whichever lanes they are on.
            if (note.Chord > 0 && Members(notes, index).Select(BoxOf).OfType<Rect>().Where(other => other.X > box.X)
                    .OrderBy(other => other.X).Select(other => (Rect?)other).FirstOrDefault() is { } next)
            {
                var y = box.Y + box.Height / 2;
                drawing.DrawLine(_chordBar, new Point(box.Right, y), new Point(next.X, y));
            }
            if (!numbers || Label(notes, index) is not { } label) continue;
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, NumberSize, _muted, dpi);
            drawing.DrawText(text, new Point(box.X + 1, box.Y - NumberSize - 3));
        }
        if (_band is { } band) drawing.DrawRectangle(BandFill, BandEdge, band);
        drawing.Pop();

        // The header names the lanes 1..8, which is also what the number keys type.
        drawing.DrawRectangle(Header, null, new Rect(0, 0, ActualWidth, HeaderHeight));
        for (var lane = 0; lane < lanes; lane++)
        {
            var name = new FormattedText((lane + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Face, 11, _ruleTiles.Contains(_session.Lanes[lane]) ? _text : _muted, dpi);
            drawing.DrawText(name, new Point(lane * width + (width - name.Width) / 2, (HeaderHeight - name.Height) / 2));
        }
        drawing.DrawLine(Separator, new Point(0, HeaderHeight), new Point(ActualWidth, HeaderHeight));
    }

    /// <summary>The lane color of a tile: the main screen gives its first lane the first color and alternates from there.</summary>
    public static int LaneColorIndex(int tile, int firstTile, int count) => ((tile - firstTile) % count + count) % count;

    private static Color Lighter(string color) =>
        NoteFieldPalette.Blend(NoteFieldPalette.ToColor(VisualizerSettings.ParseColor(color)), Colors.White, 0.55);

    /// <summary>The other notes of a chord: they sit next to each other in the list, a chord being at most the lanes on screen.</summary>
    private static IEnumerable<EditNote> Members(IReadOnlyList<EditNote> notes, int index)
    {
        var chord = notes[index].Chord;
        for (var other = Math.Max(0, index - 8); other < Math.Min(notes.Count, index + 9); other++)
            if (other != index && notes[other].Chord == chord) yield return notes[other];
    }

    /// <summary>The hit number over a note; a chord shows its first and last numbers once, as in "44-45" or "44-46".</summary>
    private static string? Label(IReadOnlyList<EditNote> notes, int index)
    {
        var note = notes[index];
        if (note.Chord == 0) return (note.Hit + 1).ToString(CultureInfo.InvariantCulture);
        var others = Members(notes, index).ToArray();
        if (others.Length == 0) return (note.Hit + 1).ToString(CultureInfo.InvariantCulture);
        // Only the earliest hit carries the label, so the chord is not written more than once.
        return others.Any(other => other.Hit < note.Hit) ? null : $"{note.Hit + 1}-{others.Max(other => other.Hit) + 1}";
    }

    private static int LowerBound(IReadOnlyList<EditNote> notes, double timeMs)
    {
        int low = 0, high = notes.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (notes[middle].ShowTimeMs < timeMs) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
