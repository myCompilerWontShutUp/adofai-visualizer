using System;

namespace AdofaiVisualizerSync
{
    /// <summary>
    /// Where a tick goes on the game's error meter so it sits in the zone of its own grade (user request, display only).
    /// scrHitErrorMeter.AddHit scales every tick by the counted window alone (angle × 60 / counted) onto a fixed picture,
    /// but the perfect and pure edges keep their own minimum times, so on fast tiles a late perfect landed in the middle of
    /// the green. The picture's edges, measured on the game's bar: green to 45 units (everything up to early/late perfect),
    /// orange to 60 (early/late), red past it (AddHit puts those at 60 to 63). Each of the hit's own edges is put on that
    /// edge, and inside the green the middle band and perfect take the angles' 12.5 and 30. Shared with the visualizer's
    /// tests, so it is plain arithmetic.
    /// </summary>
    internal static class MeterUnits
    {
        private static readonly double[] Picture = { 0, 12.5, 30, 45, 60 };

        /// <summary>
        /// The meter units (60 = the counted edge) for a press <paramref name="degrees"/> off, given the hit's own edges in
        /// degrees. Past the counted edge it carries on at the same rate, so the game still puts it at its end.
        /// </summary>
        public static double Units(double degrees, double xPerfect, double pure, double perfect, double counted)
        {
            if (double.IsNaN(degrees) || !(counted > 0)) return degrees;
            var size = Math.Abs(degrees);
            var sign = Math.Sign(degrees);
            if (size >= counted) return sign * (60 + (size - counted) * 60 / counted);
            // Kept in order, as a tiny margin scale can put the middle band past the perfect edge.
            var edges = new[] { 0, xPerfect, pure, perfect, counted };
            for (var i = 1; i < edges.Length; i++) edges[i] = Math.Min(counted, Math.Max(edges[i], edges[i - 1]));
            for (var i = 1; i < edges.Length; i++)
            {
                if (size > edges[i]) continue;
                var span = edges[i] - edges[i - 1];
                var along = span > 0 ? (size - edges[i - 1]) / span : 1;
                return sign * (Picture[i - 1] + along * (Picture[i] - Picture[i - 1]));
            }
            return sign * 60;
        }
    }
}
