using Visualizer.Core.Settings;

namespace Visualizer.Core.Visualization;

/// <summary>One full N-note cycle, including when the initial pattern is rotated.</summary>
public sealed class TilePatternCursor
{
    private readonly VisualizerSettings _settings;
    private IReadOnlyList<int> _pattern;
    private int _offset;
    private int _emittedInCycle;
    /// <summary>The tile emitted last, so a switch can avoid repeating it.</summary>
    private int _lastTile;
    /// <summary>The lane an open trill closes on (its second note comes next), and the whole trill once it has.</summary>
    private int? _trillSecond;
    private (int First, int Second)? _lastTrill;
    /// <summary>The screen lanes, to tell which two tiles a trill may share.</summary>
    private readonly IReadOnlyList<int> _visible;
    /// <summary>The way the patterns run over the lanes, which is the way a trill rolls: -1 right to left (the default).</summary>
    private readonly int _step;

    public int CurrentMode { get; private set; }
    public int? PendingMode { get; private set; }

    public TilePatternCursor(VisualizerSettings settings, int initialMode)
    {
        settings.EnsureValid();
        _settings = settings;
        ValidateMode(initialMode);
        CurrentMode = initialMode;
        _pattern = settings.GetPattern(initialMode);
        _visible = settings.GetVisibleTiles();
        var widest = settings.GetPattern(settings.MaximumTileCount);
        _step = widest.Count >= 2 && widest[1] > widest[0] ? 1 : -1;
        var initialIndex = _pattern.ToList().IndexOf(settings.InitialTile);
        _offset = Math.Max(0, initialIndex);
    }

    /// <summary>
    /// Consumes the generator's latest group-mode request. <paramref name="anticipated"/> marks the 1K to 2K and 2K to 4K
    /// anticipation, which starts the faster cycle on this note instead of waiting for the current one to end.
    /// <paramref name="trillStart"/> marks the first of two notes close enough to be a trill: they are played as a roll in
    /// the pattern's own direction, so the cursor picks both lanes at once (see <see cref="OpenTrill"/>) and the next call
    /// closes it. The single after a trill sits beside it (<see cref="BesideTrill"/>).
    /// </summary>
    public int Next(int requestedMode, bool anticipated = false, bool trillStart = false)
    {
        var tile = Choose(requestedMode, anticipated);
        (int First, int Second)? closed = null;
        if (_trillSecond is { } second)
        {
            closed = (_lastTile, second);
            tile = Place(second);
            _trillSecond = null;
        }
        else if (trillStart && OpenTrill(tile) is { } trill)
        {
            tile = Place(trill.First);
            _trillSecond = trill.Second;
        }
        else if (!trillStart && _lastTrill is { } last && BesideTrill(last) is { } beside) tile = beside;
        _lastTrill = closed;
        return Commit(tile);
    }

    /// <summary>
    /// The two lanes a trill starting here rolls over, in the patterns' direction (user rule: on a 4-3-2-1 pattern a trill
    /// reads 4-3 or 2-1, never 1-2): this tile and its neighbour that way; else the next tile of the pattern and its
    /// neighbour (the wrap from 1 to 4, or the pair across the center that four lanes refuse, is skipped); else a roll that
    /// ends on this tile or the next, starting one lane back. None opens on the tile just played. The neighbour need not be in the
    /// pattern: 2K sits on the center pair, so its trills reach out to the lane beside it. Right after a trill, a pair other
    /// than that trill's comes first (user rule: not 1-2 1-2). Null when no pair a trill may take is found, which leaves the
    /// notes to the pattern.
    /// </summary>
    private (int First, int Second)? OpenTrill(int tile)
    {
        var next = _pattern[(_offset + _emittedInCycle + 1) % _pattern.Count];
        // From this tile, then from the next one; rolling on from it first, then ending on it. Nothing opens on the tile
        // just played.
        (int, int)? On(int start) => Neighbour(start, _step) is { } partner && TrillPair(start, partner) ? (start, partner) : null;
        (int, int)? Into(int end) => Neighbour(end, -_step) is { } behind && TrillPair(behind, end) ? (behind, end) : null;
        var found = new[] { On(tile), _pattern.Count > 1 ? On(next) : null, Into(tile), _pattern.Count > 1 ? Into(next) : null }
            .OfType<(int First, int Second)>().Where(pair => pair.First != _lastTile).Distinct().ToArray();
        bool Repeats((int First, int Second) pair) => _lastTrill is { } last
            && Math.Min(pair.First, pair.Second) == Math.Min(last.First, last.Second)
            && Math.Max(pair.First, pair.Second) == Math.Max(last.First, last.Second);
        return found.Where(pair => !Repeats(pair)).Concat(found).Select(pair => ((int First, int Second)?)pair).FirstOrDefault();
    }

    private int? Neighbour(int tile, int step)
    {
        var at = IndexOf(tile);
        var to = at + step;
        return at >= 0 && to >= 0 && to < _visible.Count ? _visible[to] : null;
    }

    /// <summary>Plays this tile, and when the pattern holds it the pattern walks on from there.</summary>
    private int Place(int tile)
    {
        var at = IndexInPattern(tile);
        if (at >= 0) _offset = ((at - _emittedInCycle) % _pattern.Count + _pattern.Count) % _pattern.Count;
        return tile;
    }

    /// <summary>
    /// The single after a trill sits beside it (user rule: after a roll on 1-2 comes 3, after 3-4 comes 2): on the side of
    /// the trill's second note when there is a lane there, otherwise on the other side. The pattern then walks on from that
    /// tile. Null when the tile is not in the pattern or would repeat the one just played.
    /// </summary>
    private int? BesideTrill((int First, int Second) trill)
    {
        var first = IndexOf(trill.First);
        var second = IndexOf(trill.Second);
        if (first < 0 || second < 0 || Math.Abs(first - second) != 1) return null;
        var step = second - first;
        foreach (var lane in new[] { second + step, first - step })
        {
            if (lane < 0 || lane >= _visible.Count || IndexInPattern(_visible[lane]) < 0 || _visible[lane] == _lastTile) continue;
            return Place(_visible[lane]);
        }
        return null;
    }

    private int IndexInPattern(int tile)
    {
        for (var i = 0; i < _pattern.Count; i++)
            if (_pattern[i] == tile) return i;
        return -1;
    }

    private int Choose(int requestedMode, bool anticipated)
    {
        ValidateMode(requestedMode);
        // With immediate switching off, every change waits for the cycle boundary.
        if (!_settings.ImmediateDownshift) anticipated = false;
        if (anticipated && requestedMode > CurrentMode)
        {
            CurrentMode = requestedMode;
            _pattern = _settings.GetPattern(requestedMode);
            _emittedInCycle = 0;
            _offset = FreshStart();
            PendingMode = null;
            return Peek();
        }
        // Fewer tiles never wait for the cycle to end (user rule): the smaller pattern starts on the very note whose
        // interval asks for it. When its first tile would repeat the tile just played, it starts one tile later instead.
        if (_settings.ImmediateDownshift && requestedMode < CurrentMode)
        {
            CurrentMode = requestedMode;
            _pattern = _settings.GetPattern(requestedMode);
            _emittedInCycle = 0;
            PendingMode = null;
            _offset = FreshStart();
            return Peek();
        }
        // A reverted condition cancels a pending request; a changed condition replaces it.
        PendingMode = requestedMode == CurrentMode ? null : requestedMode;
        if (_emittedInCycle == CurrentMode)
        {
            if (PendingMode is int nextMode)
            {
                CurrentMode = nextMode;
                _pattern = _settings.GetPattern(nextMode);
                // Initial Tile applies to a new play only, not mode switches.
                _offset = FreshStart();
            }
            _emittedInCycle = 0;
            PendingMode = null;
        }
        return Peek();
    }

    /// <summary>
    /// Where a new pattern starts: its first tile, or the second when the first would repeat the tile just played. The
    /// same key twice in a row is hard to play (user rule), and a switch is exactly where a new pattern meets the old one.
    /// </summary>
    private int FreshStart() => _pattern.Count > 1 && _pattern[0] == _lastTile ? 1 : 0;

    /// <summary>Two tiles a trill may share: neighbours on screen, and on four lanes not the pair across the center while that is refused.</summary>
    private bool TrillPair(int first, int second) =>
        SimultaneousNotePlanner.IsTrillPair(_visible.Count, IndexOf(first), IndexOf(second), _settings.NoTrillCenterPair);

    private int IndexOf(int tile)
    {
        for (var i = 0; i < _visible.Count; i++)
            if (_visible[i] == tile) return i;
        return -1;
    }

    /// <summary>
    /// Starts the pattern again from its first tile without changing the tile count. A run of notes at one interval reads
    /// the same every time that way, instead of carrying the phase of whatever came before it. The first tile is skipped
    /// when it would repeat the one just played, as an immediate switch does.
    /// </summary>
    public void RestartCycle()
    {
        _emittedInCycle = 0;
        _offset = FreshStart();
    }

    private int Peek() => _pattern[(_offset + _emittedInCycle) % _pattern.Count];

    private int Commit(int tile)
    {
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
