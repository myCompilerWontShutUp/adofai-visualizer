using System.Windows;
using System.Windows.Media;
using Visualizer.Core.Settings;

namespace Visualizer.App.Rendering;

/// <summary>
/// Frozen brushes built from the color settings once per settings snapshot. Notes must stay solid brushes: a bounding-box
/// gradient is re-realized for every note rectangle and measurably slowed composition (about 80 fps instead of 120 fps).
/// </summary>
internal sealed record NoteFieldPalette(Brush Note, Brush Chord, Brush Checkpoint, Brush PressedCheckpoint,
    Brush PressedNote, Brush PressedChord, Brush Start, Brush PressedStart, Brush End, Brush PressedEnd, Pen Judgment, Pen JudgmentGlow,
    Brush Flash, Brush[] LaneBackgrounds, GradientStopCollection BeamStops, GradientStopCollection MissStops,
    Brush MagicBar, Pen MagicCenter, Brush MagicMarker)
{
    /// <summary>The top of the field background gradient; lane colors are drawn over it.</summary>
    public static readonly Color FieldColor = Color.FromRgb(10, 13, 19);
    private static readonly Color FieldMiddle = Color.FromRgb(13, 17, 25);

    public static NoteFieldPalette From(VisualizerSettings settings)
    {
        var note = ToColor(VisualizerSettings.ParseColor(settings.NoteColor));
        var chord = ToColor(VisualizerSettings.ParseColor(settings.ChordColor));
        var checkpoint = ToColor(VisualizerSettings.ParseColor(settings.CheckpointColor));
        var start = ToColor(VisualizerSettings.ParseColor(settings.StartNoteColor));
        var end = ToColor(VisualizerSettings.ParseColor(settings.EndNoteColor));
        var judgment = ToColor(VisualizerSettings.ParseColor(settings.JudgmentLineColor));
        var miss = ToColor(VisualizerSettings.ParseColor(settings.MissEffectColor));
        // A hit note keeps this much of its color; the rest fades into the field, which looks like the transparency setting
        // without blending a translucent brush every frame.
        var strength = settings.PressedNoteStrength;
        return new(
            Freeze(new SolidColorBrush(note)),
            Freeze(new SolidColorBrush(chord)),
            Freeze(new SolidColorBrush(checkpoint)),
            Freeze(new SolidColorBrush(Blend(FieldColor, checkpoint, strength))),
            Freeze(new SolidColorBrush(Blend(FieldColor, note, strength))),
            Freeze(new SolidColorBrush(Blend(FieldColor, chord, strength))),
            Freeze(new SolidColorBrush(start)),
            Freeze(new SolidColorBrush(Blend(FieldColor, start, strength))),
            Freeze(new SolidColorBrush(end)),
            Freeze(new SolidColorBrush(Blend(FieldColor, end, strength))),
            Freeze(new Pen(Freeze(new SolidColorBrush(judgment)), 2)),
            Freeze(new Pen(Freeze(new SolidColorBrush(WithAlpha(judgment, 64))), 9)),
            // The hit glow belongs to the judgment line and uses its color.
            Freeze(new RadialGradientBrush(WithAlpha(Blend(judgment, Colors.White, 0.6), 215), WithAlpha(judgment, 0))),
            LaneColors(settings).Select(color => (Brush)Freeze(new SolidColorBrush(color))).ToArray(),
            // One press color for every lane, kept light so a held lane does not hide the notes falling through it.
            Stops(ToColor(settings.GetLaneEffectColor()), 22, 64),
            // A missed note flashes stronger so it stands out briefly.
            Stops(miss, 46, 130),
            // Magic circle timing bar: a dim plate, the judgment line color in its middle, and press marks in the note color.
            Freeze(new SolidColorBrush(Color.FromArgb(170, FieldColor.R, FieldColor.G, FieldColor.B))),
            Freeze(new Pen(Freeze(new SolidColorBrush(WithAlpha(judgment, 220))), 1)),
            Freeze(new SolidColorBrush(note)));
    }

    /// <summary>
    /// White on a dark field and black on a light one: the average of the lanes on screen, composited over the field,
    /// decides. Used for the translucent song info shown while stopped.
    /// </summary>
    public static Color OverlayText(VisualizerSettings settings)
    {
        var lanes = settings.GetDisplayTiles().Count;
        double r = 0, g = 0, b = 0;
        foreach (var color in LaneColors(settings).Take(lanes))
        {
            var alpha = color.A / 255d;
            r += color.R * alpha + FieldMiddle.R * (1 - alpha);
            g += color.G * alpha + FieldMiddle.G * (1 - alpha);
            b += color.B * alpha + FieldMiddle.B * (1 - alpha);
        }
        var luminance = (0.299 * r + 0.587 * g + 0.114 * b) / Math.Max(1, lanes) / 255;
        return luminance > 0.55 ? Colors.Black : Colors.White;
    }

    /// <summary>The lane background colors in use: the setting's own colors, or the theme's when the setting is empty.</summary>
    public static IReadOnlyList<Color> LaneColors(VisualizerSettings settings) => settings.FollowsThemeLaneBackgrounds
        ? Themes.ThemeCatalog.LaneBackgrounds(settings.Theme)
        : settings.GetLaneBackgroundColors().Select(ToColor).ToArray();

    private static GradientStopCollection Stops(Color color, byte middle, byte bottom) => Freeze(new GradientStopCollection
    {
        new(WithAlpha(color, 0), 0), new(WithAlpha(color, middle), 0.6), new(WithAlpha(color, bottom), 1)
    });

    internal static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    internal static Color WithAlpha(Color color, byte alpha) => Color.FromArgb((byte)(alpha * color.A / 255), color.R, color.G, color.B);

    internal static Color Blend(Color from, Color to, double amount) => Color.FromArgb(to.A,
        (byte)Math.Round(from.R + (to.R - from.R) * amount), (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    internal static T Freeze<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
