using System.Windows;
using System.Windows.Media;

namespace Visualizer.App.Themes;

/// <summary>Static night-sky backdrop for dialogs. Built once, cached as a bitmap and frozen; nothing animates.</summary>
public static class SpaceBackdrop
{
    private const double SkyWidth = 1000;
    private const double SkyHeight = 1400;

    public static Brush Brush { get; } = Create();

    private static Brush Create()
    {
        var sky = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, SkyWidth, SkyHeight)) };
        using (var dc = sky.Open())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(13, 14, 40), Color.FromRgb(5, 6, 17), 90), null,
                new Rect(0, 0, SkyWidth, SkyHeight));
            Nebula(dc, new Point(820, 170), 560, 380, Color.FromArgb(120, 76, 54, 168));
            Nebula(dc, new Point(140, 1160), 620, 440, Color.FromArgb(100, 28, 78, 156));
            Nebula(dc, new Point(930, 1290), 320, 240, Color.FromArgb(56, 176, 74, 58)); // A faint fire-planet glow.
            var random = new Random(977950); // Fixed seed (ADOFAI's Steam app id): the same sky every time.
            for (var i = 0; i < 170; i++)
            {
                var center = new Point(random.NextDouble() * SkyWidth, random.NextDouble() * SkyHeight);
                var bright = random.NextDouble() < 0.06;
                var radius = bright ? 1.8 + random.NextDouble() : 0.5 + random.NextDouble() * 1.1;
                var alpha = (byte)(bright ? 230 : 60 + random.Next(150));
                var tint = random.NextDouble();
                var color = tint < 0.7 ? Color.FromArgb(alpha, 255, 255, 255)
                    : tint < 0.9 ? Color.FromArgb(alpha, 190, 228, 255)
                    : Color.FromArgb(alpha, 255, 214, 176);
                if (bright)
                    dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(70, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B)),
                        null, center, radius * 5, radius * 5);
                dc.DrawEllipse(new SolidColorBrush(color), null, center, radius, radius);
            }
        }
        var brush = new DrawingBrush(sky) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Right, AlignmentY = AlignmentY.Top };
        RenderOptions.SetCachingHint(brush, CachingHint.Cache); // Scrolling reuses the bitmap instead of redrawing stars.
        brush.Freeze();
        return brush;
    }

    private static void Nebula(DrawingContext dc, Point center, double radiusX, double radiusY, Color color) =>
        dc.DrawEllipse(new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B)), null, center, radiusX, radiusY);
}
