using Visualizer.Core.Settings;

namespace Visualizer.Core.Visualization;

public sealed class DynamicTimingSelector
{
    private static readonly int[] Faster = [8, 6, 4, 2];
    private readonly VisualizerSettings _settings;

    public DynamicTimingSelector(VisualizerSettings settings)
    {
        settings.EnsureValid();
        _settings = settings;
    }

    /// <summary>
    /// The mode for a hit interval: the largest mode allowed by the maximum whose threshold the interval is below
    /// (8K below the 8K ↔ 6K threshold, then 6K, 4K and 2K), otherwise 1K.
    /// </summary>
    public int Select(double intervalMs)
    {
        if (!_settings.DynamicTiming) return _settings.MaximumTileCount;
        if (!double.IsFinite(intervalMs) || intervalMs < 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMs));
        var mode = 1;
        foreach (var candidate in Faster)
        {
            if (_settings.MaximumTileCount < candidate || intervalMs >= _settings.BoundaryMs(candidate)) continue;
            // A threshold that is not below every slower mode's threshold leaves this mode no interval of its own, which is
            // how a lane count is switched off (user request): 8K 30, 6K 40 and 4K 40 go from four lanes straight to eight.
            if (Faster.Where(slower => slower < candidate).Any(slower => _settings.BoundaryMs(slower) <= _settings.BoundaryMs(candidate)))
                continue;
            mode = candidate;
            break;
        }
        // The minimum tile count floors every request; with minimum = maximum this equals dynamic timing off.
        return Math.Max(mode, _settings.MinimumTileCount);
    }
}
