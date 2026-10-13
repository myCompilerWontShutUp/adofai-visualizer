using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Visualizer.App.Input;
using Visualizer.Application.Playback;
using Visualizer.Core.Localization;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using static Visualizer.App.Rendering.NoteFieldPalette;

namespace Visualizer.App.Rendering;

/// <summary>
/// Draws from absolute timeline time. It does not advance playback or assign tiles. Frames are only
/// invalidated when something visible can change (see <see cref="Tick"/>), and drawing reuses frozen resources.
/// </summary>
public sealed class NoteField : FrameworkElement
{
    private const double KeyStripHeight = 30;
    private const double NoteInset = 4;
    private const double BeamReleaseMs = 170;
    private const double HitFlashMs = 200;
    private const double MissFadeMs = 450;
    private const double MagicMarkMs = 1200;
    private const double MagicBarHeight = 12;
    /// <summary>How much of the way the average arrow moves towards its target each frame: quick, but not a jump.</summary>
    private const double ArrowEase = 0.22;
    /// <summary>Where the arrow is drawn now, eased towards the average; NaN until the bar has been shown.</summary>
    private double _arrowX = double.NaN;
    private const int TileSlots = 9; // Logical tiles are 1..8.

    private static readonly Brush FieldBrush = Freeze(new LinearGradientBrush(FieldColor, Color.FromRgb(16, 21, 31), 90));
    private static readonly Pen DividerPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255))), 1));
    private static readonly Pen RemovalPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(48, 255, 255, 255))), 1));
    // The judgment bar's three zones, in the game's own ratio (counted 60 deg, perfect 45, pure 30) and its colours.
    private static readonly Brush JudgmentCountedBrush = Freeze(new SolidColorBrush(Color.FromArgb(210, 214, 69, 69)));
    private static readonly Brush JudgmentPerfectBrush = Freeze(new SolidColorBrush(Color.FromArgb(210, 226, 190, 64)));
    private static readonly Brush JudgmentPureBrush = Freeze(new SolidColorBrush(Color.FromArgb(210, 76, 190, 104)));
    private static readonly Brush JudgmentArrowBrush = Freeze(new SolidColorBrush(Colors.White));
    // A dark plate under the bar: the note field behind it is busy, and the zones need something to sit on.
    private static readonly Brush JudgmentPlateBrush = Freeze(new SolidColorBrush(Color.FromArgb(190, 6, 9, 14)));
    private static readonly Pen JudgmentPlatePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1));
    // A press mark is white with a dark edge, so it reads on the green, the yellow and the red alike.
    private static readonly Brush JudgmentMarkBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Pen JudgmentMarkPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1));
    private static readonly Brush KeyCapBrush = Freeze(new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)));
    private static readonly Brush KeyCapHeldBrush = Freeze(new SolidColorBrush(Color.FromArgb(90, 94, 218, 242)));
    private static readonly Brush KeyTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(124, 135, 153)));
    private static readonly Brush TopFadeBrush = Freeze(new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromArgb(255, 10, 13, 19), 0), new(Color.FromArgb(0, 10, 13, 19), 1)
    }, new Point(0, 0), new Point(0, 1)));
    private static readonly Brush ProgressBrush = Freeze(new SolidColorBrush(Color.FromArgb(160, 94, 218, 242)));
    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly bool[] _held = new bool[TileSlots];
    private readonly double[] _releasedAt = new double[TileSlots];
    private readonly double[] _flashAt = new double[TileSlots];
    private readonly double[] _missAt = new double[TileSlots];
    private readonly List<int> _missedTiles = [];
    private readonly int[] _laneByTile = new int[TileSlots];
    private readonly Dictionary<string, FormattedText> _labelCache = [];
    private VisualizerSettings? _laneSettings;
    private object? _laneNotes;
    private NoteFieldPalette _palette = From(new VisualizerSettings());
    private int[] _tiles = [];
    private string[] _labels = [];
    private double _labelDpi;
    private RectangleGeometry? _clip;
    private LinearGradientBrush? _beamBrush;
    private LinearGradientBrush? _missBrush;
    private object? _magicNotes;
    private VisualizerSettings? _magicSettings;
    private IReadOnlyList<MagicCircleSection> _magicSections = [];
    private RenderSignature _lastSignature;
    private bool _wasAnimating;
    private object? _holdNotes;
    private double _longestHoldMs;

    private readonly record struct RenderSignature(PlaybackState? State, object? Notes, object? Settings,
        double OffsetMs, double TimelineMs, int PressVersion);

    public NoteField()
    {
        Array.Fill(_releasedAt, double.NegativeInfinity);
        Array.Fill(_flashAt, double.NegativeInfinity);
        Array.Fill(_missAt, double.NegativeInfinity);
    }

    public PlaybackSession? Session { get; set; }
    public NotePressJudge? Judge { get; set; }
    private double Now => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Text color that reads on the lanes of <paramref name="settings"/>: white on dark lanes, black on light ones.</summary>
    public static Color OverlayTextColor(VisualizerSettings settings) => OverlayText(settings);

    public void PressLane(int tile, bool hit)
    {
        if (tile is < 1 or >= TileSlots) return;
        _held[tile] = true;
        if (hit) _flashAt[tile] = Now;
        InvalidateVisual();
    }

    public void ReleaseLane(int tile)
    {
        if (tile is < 1 or >= TileSlots || !_held[tile]) return;
        _held[tile] = false;
        _releasedAt[tile] = Now;
        InvalidateVisual();
    }

    public void ReleaseAllLanes()
    {
        for (var tile = 1; tile < TileSlots; tile++) ReleaseLane(tile);
    }

    /// <summary>Starts the red miss gradient on a lane now (a long note let go early).</summary>
    public void MarkMiss(int tile)
    {
        if (tile is < 1 or >= TileSlots) return;
        _missAt[tile] = Now;
        InvalidateVisual();
    }

    /// <summary>Called once per compositor frame. Returns whether a redraw was requested.</summary>
    public bool Tick()
    {
        var session = Session;
        if (session is not null && Judge is { } judge)
        {
            _missedTiles.Clear();
            judge.CollectMisses(session, session.Settings.KeyPressWindowMs, _missedTiles);
            foreach (var tile in _missedTiles)
                if (tile is >= 1 and < TileSlots) _missAt[tile] = Now;
        }
        var animating = session?.State == PlaybackState.Playing || EffectsAnimating(Now);
        var signature = new RenderSignature(session?.State, session?.Notes, session?.Settings,
            session?.Offsets.TotalMs ?? 0, session?.TimelineTimeMs ?? 0, Judge?.Version ?? 0);
        // One extra frame after an animation ends clears its final partial state.
        var needed = animating || _wasAnimating || signature != _lastSignature;
        _wasAnimating = animating;
        _lastSignature = signature;
        if (needed) InvalidateVisual();
        return needed;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        dc.DrawRectangle(FieldBrush, null, new Rect(0, 0, width, height));
        var session = Session;
        if (session is null || session.State == PlaybackState.Off || height <= KeyStripHeight + 40) return;
        var settings = session.Settings;
        EnsureLanes(settings, session.Notes);
        if (_tiles.Length == 0) return;
        var palette = _palette;
        var laneWidth = width / _tiles.Length;
        var geometry = NoteFieldGeometry.Create(height - KeyStripHeight, settings.JudgmentLineHeightDip);
        var now = Now;
        // Without the input effect, a pressed lane shows no gradient, key highlight or hit glow.
        var inputEffect = settings.ShowInputEffect;

        for (var lane = 0; lane < _tiles.Length; lane++)
        {
            var x = lane * laneWidth;
            dc.DrawRectangle(palette.LaneBackgrounds[lane], null, new Rect(x, 0, laneWidth, geometry.RemovalY));
            if (lane > 0) dc.DrawLine(DividerPen, new Point(x, 0), new Point(x, height));
            var top = geometry.JudgmentY * 0.3;
            var beamRect = new Rect(x + 1, top, Math.Max(1, laneWidth - 2), geometry.RemovalY - top);
            var miss = 1 - (now - _missAt[_tiles[lane]]) / MissFadeMs;
            if (settings.ShowMissEffect && miss is > 0 and <= 1)
            {
                dc.PushOpacity(miss);
                dc.DrawRectangle(LaneBrush(ref _missBrush, palette.MissStops, top, geometry.RemovalY), null, beamRect);
                dc.Pop();
            }
            if (!inputEffect) continue;
            var intensity = BeamIntensity(_tiles[lane], now);
            if (intensity <= 0) continue;
            dc.PushOpacity(intensity);
            dc.DrawRectangle(LaneBrush(ref _beamBrush, palette.BeamStops, top, geometry.RemovalY), null, beamRect);
            dc.Pop();
        }

        if (_clip is null || _clip.Rect.Width != width || _clip.Rect.Height != geometry.RemovalY)
        {
            _clip = new RectangleGeometry(new Rect(0, 0, width, geometry.RemovalY));
            _clip.Freeze();
        }
        // The judgment line sits under the notes so a note on the line stays fully visible.
        dc.DrawLine(palette.JudgmentGlow, new Point(0, geometry.JudgmentY), new Point(width, geometry.JudgmentY));
        dc.DrawLine(palette.Judgment, new Point(0, geometry.JudgmentY), new Point(width, geometry.JudgmentY));
        dc.PushClip(_clip);
        DrawNotes(dc, session, settings, geometry, laneWidth, palette);
        DrawMagicCircle(dc, session, settings, geometry, width, palette);
        dc.Pop();

        // Keep the spawn fade short: a dimmed note now means "pressed", so fading must not look like it.
        dc.DrawRectangle(TopFadeBrush, null, new Rect(0, 0, width, Math.Min(18, geometry.JudgmentY * 0.05)));
        dc.DrawLine(RemovalPen, new Point(0, geometry.RemovalY), new Point(width, geometry.RemovalY));

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var lane = 0; lane < _tiles.Length; lane++)
        {
            var tile = _tiles[lane];
            var x = lane * laneWidth;
            var age = now - _flashAt[tile];
            if (inputEffect && age is >= 0 and < HitFlashMs)
            {
                var t = age / HitFlashMs;
                dc.PushOpacity(1 - t);
                dc.DrawEllipse(palette.Flash, null, new Point(x + laneWidth / 2, geometry.JudgmentY), laneWidth * (0.45 + 0.25 * t), 12 + 10 * t);
                dc.Pop();
            }
            var held = inputEffect && _held[tile];
            var cap = new Rect(x + 4, geometry.RemovalY + 4, Math.Max(1, laneWidth - 8), KeyStripHeight - 8);
            dc.DrawRoundedRectangle(held ? KeyCapHeldBrush : KeyCapBrush, null, cap, 5, 5);
            var label = Label(_labels[lane], held, pixelsPerDip);
            dc.DrawText(label, new Point(x + (laneWidth - label.Width) / 2, cap.Y + (cap.Height - label.Height) / 2));
        }

        var notes = session.Notes;
        if (notes.Count > 0 && (notes[^1].HoldEndMs ?? notes[^1].HitTimeMs) is var songEnd and > 0)
        {
            var progress = Math.Clamp(session.TimelineTimeMs / songEnd, 0, 1);
            if (progress > 0) dc.DrawRectangle(ProgressBrush, null, new Rect(0, 0, width * progress, 2));
        }
    }

    private void DrawNotes(DrawingContext dc, PlaybackSession session, VisualizerSettings settings, NoteFieldGeometry geometry,
        double laneWidth, NoteFieldPalette palette)
    {
        var notes = session.Notes;
        if (notes.Count == 0) return;
        var offsets = session.Offsets;
        var timeline = session.TimelineTimeMs;
        var travel = settings.NoteTravelTimeMs;
        var thickness = NoteLayout.ThicknessDip(settings.NoteThickness);
        var half = thickness / 2;
        var radius = Math.Min(3, half);
        var window = settings.KeyPressWindowMs;
        var slow = settings.SlowNotesPastJudgmentLine;
        // Width scales: the lane width without its inset, scaled around the lane center. A long note bar is
        // never wider than a note (checked by the settings).
        var singleWidth = Math.Max(1, (laneWidth - 2 * NoteInset) * settings.NoteWidth);
        var holdWidth = Math.Max(1, (laneWidth - 2 * NoteInset) * settings.HoldNoteWidth);
        static double Centered(double start, double span, double barWidth) => start + (span - barWidth) / 2;
        var (earliest, latest) = geometry.VisibleWindow(timeline, travel, half);
        // An unpressed note stays until its press window ends (a slowed note stays exactly that long).
        earliest = Math.Min(earliest, timeline - window);
        var judge = Judge;
        // The play's first note and the song's last outrank every other color (user rule); a chord shares its time. They
        // keep it when pressed (user rule): the start press marks the first note as played, so it would otherwise fade
        // the moment the play begins and its color would never be seen.
        var startMs = notes.Count > 0 ? notes[0].HitTimeMs : double.NaN;
        var endMs = notes.Count > 0 ? notes[^1].HitTimeMs : double.NaN;
        Brush BrushOf(int index, bool chord, bool pressed)
        {
            var note = notes[index];
            if (note.HitTimeMs == startMs) return palette.Start;
            if (note.HitTimeMs == endMs) return palette.End;
            if (note.Checkpoint) return pressed ? palette.PressedCheckpoint : palette.Checkpoint;
            return chord ? (pressed ? palette.PressedChord : palette.Chord) : (pressed ? palette.PressedNote : palette.Note);
        }
        double NoteY(VisualizerNote note)
        {
            var late = timeline - (note.HitTimeMs + offsets.TotalMs);
            // Slowed: past the line, the note reaches the bottom line exactly when its press window ends.
            return slow && late > 0
                ? geometry.JudgmentY + (geometry.RemovalY - geometry.JudgmentY) * Math.Min(1, late / window)
                : geometry.CenterY(NoteLayout.Progress(note.HitTimeMs, timeline, travel, offsets));
        }
        bool Shown(VisualizerNote note, double y, bool pressed)
        {
            // A note left unpressed is removed when its press window ends, together with the red miss gradient.
            // Pressed notes keep falling to the bottom line, or with the slowdown also end at the window.
            if (timeline - (note.HitTimeMs + offsets.TotalMs) > window && (!pressed || slow)) return false;
            return y + half >= 0 && !geometry.IsRemoved(y, half);
        }
        void DrawMember(int index, bool chord)
        {
            var member = notes[index];
            var memberLane = _laneByTile[member.Tile];
            if (memberLane < 0) return;
            var pressed = judge?.IsPressed(notes, index) == true;
            // A start, end or checkpoint note carries its own color, whether it is a single or part of a chord.
            var brush = BrushOf(index, chord, pressed);
            if (member.HoldEndMs is not double end)
            {
                var tapY = NoteY(member);
                if (Shown(member, tapY, pressed))
                    dc.DrawRoundedRectangle(brush, null,
                        new Rect(Centered(memberLane * laneWidth, laneWidth, singleWidth), tapY - half, singleWidth, thickness), radius, radius);
                return;
            }
            var x = Centered(memberLane * laneWidth, laneWidth, holdWidth);
            var lateStart = timeline - (member.HitTimeMs + offsets.TotalMs);
            var lateEnd = timeline - (end + offsets.TotalMs);
            // Unpressed: removed when its press window ends, like a tap. Let go early, or held to its end: the long note is over.
            if ((!pressed && lateStart > window) || judge?.IsReleasedEarly(notes, index) == true || (pressed && lateEnd > 0)) return;
            var headY = geometry.CenterY(NoteLayout.Progress(member.HitTimeMs, timeline, travel, offsets));
            var tailY = geometry.CenterY(NoteLayout.Progress(end, timeline, travel, offsets));
            // Held, the bar is used up at the judgment line; slowed and not pressed yet, its head waits there until the window ends.
            if (pressed || (slow && lateStart > 0)) headY = Math.Min(headY, geometry.JudgmentY);
            var top = Math.Min(headY, tailY) - half;
            var bottom = Math.Max(headY, tailY) + half;
            if (bottom < 0 || top >= geometry.RemovalY) return;
            // The long note width is the held part's only (user rule): head and tail are notes of the note width, so a
            // narrower hold reads as an I between them rather than one thin bar.
            dc.DrawRectangle(brush, null, new Rect(x, Math.Min(headY, tailY), holdWidth, Math.Abs(tailY - headY)));
            var capX = Centered(memberLane * laneWidth, laneWidth, singleWidth);
            dc.DrawRoundedRectangle(brush, null, new Rect(capX, tailY - half, singleWidth, thickness), radius, radius);
            dc.DrawRoundedRectangle(brush, null, new Rect(capX, headY - half, singleWidth, thickness), radius, radius);
        }
        // Notes are sorted by display time, so only the on-screen slice is visited, from earlier by the longest long note
        // (its tail can still be on screen).
        for (var i = FirstAtOrAfter(notes, earliest - offsets.TotalMs - LongestHoldMs(notes)); i < notes.Count; i++)
        {
            var note = notes[i];
            if (note.HitTimeMs + offsets.TotalMs > latest) break;
            // Tiles the game rewound past on a checkpoint restart: they fall in front of the playhead but are not played.
            if (note.HitTimeMs < session.HideNotesBeforeMs) continue;
            // A chord is the notes marked StartsChord in a row and the one after the last (two or more, user rule).
            var chordEnd = i;
            while (chordEnd + 1 < notes.Count && notes[chordEnd].StartsChord) chordEnd++;
            // A long note, or a chord with one, is drawn as separate bars in the note (or chord) color.
            if (note.IsHold || Enumerable.Range(i, chordEnd - i + 1).Any(k => notes[k].IsHold))
            {
                var chord = chordEnd > i;
                for (var k = i; k <= chordEnd; k++) DrawMember(k, chord);
                i = chordEnd;
                continue;
            }
            var y = NoteY(note);
            var lane = _laneByTile[note.Tile];
            if (lane < 0) continue;
            var pressed = judge?.IsPressed(notes, i) == true;
            var shown = Shown(note, y, pressed);
            // The planner marks chords explicitly: a spare-lane single beside its group is never merged into a chord.
            if (chordEnd > i)
            {
                var members = Enumerable.Range(i, chordEnd - i + 1).Select(k => (Index: k, Lane: _laneByTile[notes[k].Tile],
                    Pressed: judge?.IsPressed(notes, k) == true)).ToArray();
                var memberShown = members.Select(member => member.Lane >= 0 && Shown(notes[member.Index], y, member.Pressed)).ToArray();
                var memberLanes = members.Select(member => member.Lane).Order().ToArray();
                var together = memberShown.All(seen => seen) && memberLanes[0] >= 0
                    && memberLanes[^1] - memberLanes[0] == memberLanes.Length - 1 && memberLanes.Distinct().Count() == memberLanes.Length;
                if (!together)
                {
                    // A member has left (its window ended unpressed): the others stay as one-lane chord notes.
                    for (var k = 0; k < members.Length; k++)
                        if (memberShown[k]) DrawSingle(members[k].Lane, BrushOf(members[k].Index, true, members[k].Pressed));
                    i = chordEnd;
                    continue;
                }
                var span = memberLanes.Length;
                var width = Math.Max(1, (span * laneWidth - 2 * NoteInset) * settings.NoteWidth);
                var rect = new Rect(Centered(memberLanes[0] * laneWidth, span * laneWidth, width), y - half, width, thickness);
                dc.DrawRoundedRectangle(BrushOf(i, true, members.All(member => member.Pressed)), null, rect, radius, radius);
                if (members.Any(member => member.Pressed) && !members.All(member => member.Pressed))
                {
                    // Fade only the pressed members' part; the chord stays one gapless tile.
                    var part = rect.Width / span;
                    foreach (var member in members.Where(member => member.Pressed))
                        dc.DrawRoundedRectangle(BrushOf(i, true, true), null,
                            new Rect(rect.X + (member.Lane - memberLanes[0]) * part, rect.Y, part, thickness), radius, radius);
                }
                i = chordEnd;
                continue;
            }
            if (shown) DrawSingle(lane, BrushOf(i, false, pressed));

            void DrawSingle(int singleLane, Brush brush) => dc.DrawRoundedRectangle(brush, null,
                new Rect(Centered(singleLane * laneWidth, laneWidth, singleWidth), y - half, singleWidth, thickness), radius, radius);
        }
    }

    /// <summary>
    /// The magic circle timing bar: while a section of equal intervals is coming up or running, a plate with the judgment line in
    /// its middle shows the last presses, early to the left and late to the right. Display only.
    /// </summary>
    private void DrawMagicCircle(DrawingContext dc, PlaybackSession session, VisualizerSettings settings, NoteFieldGeometry geometry,
        double width, NoteFieldPalette palette)
    {
        var mode = settings.JudgmentBarMode.Trim();
        if (mode == "Off" || session.State != PlaybackState.Playing) { _arrowX = double.NaN; return; }
        var timeline = session.TimelineTimeMs - session.Offsets.TotalMs;
        var opacity = 1d;
        if (mode != "Always")
        {
            var sections = MagicSections(session.Notes, settings);
            if (sections.Count == 0) return;
            opacity = 0;
            foreach (var section in sections)
            {
                var appears = section.StartMs - settings.MagicCircleLeadMs;
                var fadeOut = settings.MagicCircleFadeOutMs;
                if (timeline < appears || timeline > section.EndMs + fadeOut) continue;
                var fadeIn = settings.MagicCircleFadeInMs;
                opacity = timeline <= section.EndMs
                    ? fadeIn > 0 ? Math.Min(1, (timeline - appears) / fadeIn) : 1
                    : fadeOut > 0 ? 1 - (timeline - section.EndMs) / fadeOut : 0;
                break;
            }
            if (opacity <= 0) return;
        }
        var barWidth = Math.Min(width - 32, 280);
        if (barWidth < 60) return;
        var centerX = width / 2;
        var rect = new Rect(centerX - barWidth / 2, geometry.JudgmentY * 0.5, barWidth, MagicBarHeight);
        dc.PushOpacity(opacity);
        // The game grades by angle: counted 60 deg, perfect 45, pure 30 (GCS.HITMARGIN_*). Those ratios put green in
        // the middle, yellow around it and red at the ends, which is what its own bar looks like.
        var half = barWidth / 2;
        var plate = new Rect(rect.X - 6, rect.Y - 5, rect.Width + 12, rect.Height + 20);
        dc.DrawRoundedRectangle(JudgmentPlateBrush, JudgmentPlatePen, plate, 7, 7);
        dc.DrawRoundedRectangle(JudgmentCountedBrush, null, rect, 5, 5);
        dc.DrawRectangle(JudgmentPerfectBrush, null, new Rect(centerX - half * 0.75, rect.Top, half * 1.5, MagicBarHeight));
        dc.DrawRectangle(JudgmentPureBrush, null, new Rect(centerX - half * 0.5, rect.Top, half, MagicBarHeight));
        dc.DrawLine(palette.MagicCenter, new Point(centerX, rect.Top + 2), new Point(centerX, rect.Bottom - 2));
        // The window is the one the difficulty names, not this app's press window: the bar stands for the game's grading.
        var window = settings.JudgmentWindowMs;
        var recent = Judge?.RecentPresses ?? [];
        foreach (var (pressTime, deviation) in recent)
        {
            var age = timeline - pressTime;
            if (age < 0 || age > MagicMarkMs) continue;
            // The ends of the bar are that window: an early press sits left of the middle, a late one right.
            var x = centerX + Math.Clamp(deviation / window, -1, 1) * (half - 3);
            dc.PushOpacity(1 - age / MagicMarkMs);
            // Taller than the bar and outlined, so a press is visible wherever it lands.
            dc.DrawRoundedRectangle(JudgmentMarkBrush, JudgmentMarkPen, new Rect(x - 2, rect.Top - 3, 4, MagicBarHeight + 6), 2, 2);
            dc.Pop();
        }
        // Where the recent presses sit on average, so a steady lean shows up even when single marks scatter.
        if (settings.ShowJudgmentAverage && recent.Count > 0)
        {
            var taken = Math.Min(settings.JudgmentAverageCount, recent.Count);
            var average = 0d;
            for (var i = recent.Count - taken; i < recent.Count; i++) average += recent[i].DeviationMs;
            average /= taken;
            var target = centerX + Math.Clamp(average / window, -1, 1) * (half - 3);
            // Eased towards the average instead of snapping: a jumping arrow is harder to read than a moving one.
            _arrowX = double.IsNaN(_arrowX) ? target : _arrowX + (target - _arrowX) * ArrowEase;
            var x = _arrowX;
            var arrow = new StreamGeometry();
            using (var draw = arrow.Open())
            {
                draw.BeginFigure(new Point(x, rect.Bottom + 3), true, true);
                draw.LineTo(new Point(x - 6, rect.Bottom + 13), true, false);
                draw.LineTo(new Point(x + 6, rect.Bottom + 13), true, false);
            }
            arrow.Freeze();
            dc.DrawGeometry(JudgmentArrowBrush, JudgmentMarkPen, arrow);
        }
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var fast = Label(Languages.Get(settings.Language, "MagicCircleFast"), held: false, pixelsPerDip);
        var late = Label(Languages.Get(settings.Language, "MagicCircleSlow"), held: false, pixelsPerDip);
        dc.DrawText(fast, new Point(rect.Left, rect.Bottom + 2));
        dc.DrawText(late, new Point(rect.Right - late.Width, rect.Bottom + 2));
        dc.Pop();
    }

    /// <summary>Magic circle sections of a prepared note list, found once per note list and settings snapshot.</summary>
    private IReadOnlyList<MagicCircleSection> MagicSections(IReadOnlyList<VisualizerNote> notes, VisualizerSettings settings)
    {
        if (ReferenceEquals(notes, _magicNotes) && ReferenceEquals(settings, _magicSettings)) return _magicSections;
        _magicNotes = notes;
        _magicSettings = settings;
        _magicSections = MagicCircleFinder.Find(notes, settings);
        return _magicSections;
    }

    /// <summary>Longest long note of a prepared note list, computed once per list.</summary>
    private double LongestHoldMs(IReadOnlyList<VisualizerNote> notes)
    {
        if (!ReferenceEquals(notes, _holdNotes))
        {
            _holdNotes = notes;
            _longestHoldMs = notes.Max(note => note.HoldEndMs - note.HitTimeMs) ?? 0;
        }
        return _longestHoldMs;
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

    /// <summary>How many lanes are drawn now; 0 before the first frame. The edit sheet matches its lane width to this.</summary>
    public int LaneCount => _tiles.Length;

    private void EnsureLanes(VisualizerSettings settings, IReadOnlyList<VisualizerNote> notes)
    {
        if (ReferenceEquals(settings, _laneSettings) && ReferenceEquals(notes, _laneNotes)) return;
        _laneSettings = settings;
        _laneNotes = notes;
        _palette = From(settings);
        _beamBrush = null;
        _missBrush = null;
        // Spare lanes (extra lanes) sit beside the pattern lanes in tile order, and only where a note really sits on one:
        // extra lane placement adds a lane when a chord needs it, so the switch alone must not widen the screen (user report).
        // An edited song never needs more: one that does is not placed until the settings show its lanes.
        _tiles = settings.GetDisplayTiles(notes.Select(note => note.Tile)).ToArray();
        Array.Fill(_laneByTile, -1);
        for (var lane = 0; lane < _tiles.Length; lane++) _laneByTile[_tiles[lane]] = lane;
        var tokens = settings.GetLaneKeyTokens();
        _labels = _tiles.Select(tile => tile <= tokens.Count
            ? KeyNames.TryParse(tokens[tile - 1], out var key) ? KeyNames.Token(key) : tokens[tile - 1]
            : tile.ToString(CultureInfo.InvariantCulture)).ToArray();
        _labelCache.Clear();
    }

    private FormattedText Label(string text, bool held, double pixelsPerDip)
    {
        if (pixelsPerDip != _labelDpi)
        {
            _labelCache.Clear();
            _labelDpi = pixelsPerDip;
        }
        var key = held ? "held:" + text : text;
        if (!_labelCache.TryGetValue(key, out var formatted))
        {
            formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 13,
                held ? Brushes.White : KeyTextBrush, pixelsPerDip);
            _labelCache[key] = formatted;
        }
        return formatted;
    }

    /// <summary>An absolute-mapped gradient shared by every frame of one lane effect; rebuilt only when the layout or colors change.</summary>
    private static Brush LaneBrush(ref LinearGradientBrush? cached, GradientStopCollection stops, double top, double bottom)
    {
        if (cached is null || cached.StartPoint.Y != top || cached.EndPoint.Y != bottom)
        {
            cached = new LinearGradientBrush(stops, new Point(0, top), new Point(0, bottom)) { MappingMode = BrushMappingMode.Absolute };
            cached.Freeze();
        }
        return cached;
    }

    private bool EffectsAnimating(double now)
    {
        for (var tile = 1; tile < TileSlots; tile++)
            if ((!_held[tile] && now - _releasedAt[tile] < BeamReleaseMs) || now - _flashAt[tile] < HitFlashMs
                || now - _missAt[tile] < MissFadeMs) return true;
        return false;
    }

    private double BeamIntensity(int tile, double now) =>
        _held[tile] ? 1 : Math.Max(0, 1 - (now - _releasedAt[tile]) / BeamReleaseMs);
}
