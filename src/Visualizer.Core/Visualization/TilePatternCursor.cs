using Visualizer.Core.Settings;

namespace Visualizer.Core.Visualization;

/// <summary>One full N-note cycle, including when the initial pattern is rotated.</summary>
public sealed class TilePatternCursor
{
    private readonly VisualizerSettings _settings;
    private IReadOnlyList<int> _pattern;
    private int _offset;
    private int _emittedInCycle;
    /// <summary>The tile emitted last, so an immediate switch can avoid repeating it.</summary>
    private int _lastTile;

    public int CurrentMode { get; private set; }
    public int? PendingMode { get; private set; }

    public TilePatternCursor(VisualizerSettings settings, int initialMode)
    {
        settings.EnsureValid();
        _settings = settings;
        ValidateMode(initialMode);
        CurrentMode = initialMode;
        _pattern = settings.GetPattern(initialMode);
        var initialIndex = _pattern.ToList().IndexOf(settings.InitialTile);
        _offset = Math.Max(0, initialIndex);
    }

    /// <summary>
    /// Consumes the generator's latest group-mode request. <paramref name="anticipated"/> marks the 1K to 2K and 2K to 4K
    /// anticipation, which starts the faster cycle on this note instead of waiting for the current one to end.
    /// </summary>
    public int Next(int requestedMode, bool anticipated = false)
    {
        ValidateMode(requestedMode);
        // With immediate switching off, every change waits for the cycle boundary.
        if (!_settings.ImmediateDownshift) anticipated = false;
        if (anticipated && requestedMode > CurrentMode)
        {
            CurrentMode = requestedMode;
            _pattern = _settings.GetPattern(requestedMode);
            _emittedInCycle = 0;
            _offset = 0;
            PendingMode = null;
            return Emit();
        }
        // Fewer tiles never wait for the cycle to end (user rule): the smaller pattern starts on the very note whose
        // interval asks for it. When its first tile would repeat the tile just played, it starts one tile later instead.
        if (_settings.ImmediateDownshift && requestedMode < CurrentMode)
        {
            CurrentMode = requestedMode;
            _pattern = _settings.GetPattern(requestedMode);
            _emittedInCycle = 0;
            PendingMode = null;
            _offset = _pattern.Count > 1 && _pattern[0] == _lastTile ? 1 : 0;
            return Emit();
        }
        // A reverted condition cancels a pending request; a changed condition replaces it.
        PendingMode = requestedMode == CurrentMode ? null : requestedMode;
        if (_emittedInCycle == CurrentMode)
        {
            if (PendingMode is int nextMode)
            {
                CurrentMode = nextMode;
                _pattern = _settings.GetPattern(nextMode);
                _offset = 0; // Initial Tile applies to a new play only, not mode switches.
            }
            _emittedInCycle = 0;
            PendingMode = null;
        }
        return Emit();
    }

    /// <summary>
    /// Starts the pattern again from its first tile without changing the tile count. A run of notes at one interval reads
    /// the same every time that way, instead of carrying the phase of whatever came before it. The first tile is skipped
    /// when it would repeat the one just played, as an immediate switch does.
    /// </summary>
    public void RestartCycle()
    {
        _emittedInCycle = 0;
        _offset = _pattern.Count > 1 && _pattern[0] == _lastTile ? 1 : 0;
    }

    private int Emit()
    {
        var tile = _pattern[(_offset + _emittedInCycle) % _pattern.Count];
        _emittedInCycle++;
        _lastTile = tile;
        return tile;
    }

    private void ValidateMode(int mode)
    {
        if (!VisualizerSettings.IsMode(mode) || mode > _settings.MaximumTileCount)
            throw new ArgumentOutOfRangeException(nameof(mode));
    }
}
