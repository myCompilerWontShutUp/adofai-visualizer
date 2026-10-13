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
    /// <summary>Whether the note being placed (or the trill it heads) follows the one before it closely: only then does a skipped lane or the same lane again read as a broken run.</summary>
    private bool _closeToLast;
    /// <summary>Asked how many notes on the tile just played would come back, for the note being placed (see <see cref="Next"/>).</summary>
    private Func<int, bool>? _tooSoon;
    /// <summary>Asked whether a tile, played that many notes on, would come back within the quick window.</summary>
    private Func<int, int, bool>? _recent;

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
    /// closes it. The single after a trill sits beside it (<see cref="BesideTrill"/>). <paramref name="leadsTrill"/> marks a
    /// single that a trill follows closely: the three are one roll (<see cref="RollHead"/>). <paramref name="closeToLast"/>
    /// says the note being placed, or for <paramref name="leadsTrill"/> the trill after it, comes close after the previous
    /// note, which is when a skipped lane or the same lane again reads as a broken run. <paramref name="tooSoon"/> is asked,
    /// when a new pattern starts, how many notes on the tile just played would come back (see <see cref="FreshStart"/>).
    /// </summary>
    public int Next(int requestedMode, bool anticipated = false, bool trillStart = false, bool leadsTrill = false, bool closeToLast = false,
        Func<int, bool>? tooSoon = null, Func<int, int, bool>? recent = null)
    {
        _closeToLast = closeToLast;
        _tooSoon = tooSoon;
        _recent = recent;
        var tile = Choose(requestedMode, anticipated, tooSoon);
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
        else if (leadsTrill && RollHead(tile) is { } head) tile = head;
        _lastTrill = closed;
        return Commit(tile);
    }

    /// <summary>
    /// The two lanes a trill starting here rolls over, in the patterns' direction (user rule: on a 4-3-2-1 pattern a trill
    /// reads 4-3 or 2-1, never 1-2): this tile and its neighbour that way; else the next tile of the pattern and its
    /// neighbour; else a roll that ends on this tile or the next, starting one lane back. None opens on the tile just played.
    /// The neighbour need not be in the pattern: 2K sits on the center pair, so its trills reach out to the lane beside it.
    /// <para>
    /// Pairs that keep the flow come first (user rule): a pair that does not start on this tile skips a lane of the run
    /// (4 2-1 instead of 4 3-2), and one that ends on the tile just played brings it straight back (3 4-3, 1 2-1). The pair
    /// across the center is the last resort while the setting says so: it is taken only when every other pair breaks the
    /// flow, which is exactly where 4 3-2 1 would otherwise read 4 2-1 3. Right after a trill, a pair other than that
    /// trill's comes first (user rule: not 1-2 1-2). Null when no pair is found, which leaves the notes to the pattern.
    /// </para>
    /// </summary>
    private (int First, int Second)? OpenTrill(int tile)
    {
        var next = _pattern[(_offset + _emittedInCycle + 1) % _pattern.Count];
        // From this tile, then from the next one; rolling on from it first, then ending on it.
        (int, int)? On(int start) => Neighbour(start, _step) is { } partner && Adjacent(start, partner) ? (start, partner) : null;
        (int, int)? Into(int end) => Neighbour(end, -_step) is { } behind && Adjacent(behind, end) ? (behind, end) : null;
        // At the far edge a roll has nowhere to go that way, so it rolls back (user rule: 3 2 1 | 2-3-4 rather than jumping
        // back up to 4-3 and bringing a lane straight back).
        (int, int)? Back(int start) => Neighbour(start, _step) is null && Neighbour(start, -_step) is { } partner && Adjacent(start, partner)
            ? (start, partner) : null;
        var reversed = new[] { Back(tile), _pattern.Count > 1 ? Back(next) : null }.OfType<(int First, int Second)>().ToHashSet();
        var found = new[] { On(tile), _pattern.Count > 1 ? On(next) : null, Into(tile), _pattern.Count > 1 ? Into(next) : null, Back(tile), _pattern.Count > 1 ? Back(next) : null }
            .OfType<(int First, int Second)>().Where(pair => pair.First != _lastTile).Distinct().ToArray();
        bool Repeats((int First, int Second) pair) => _lastTrill is { } last
            && Math.Min(pair.First, pair.Second) == Math.Min(last.First, last.Second)
            && Math.Max(pair.First, pair.Second) == Math.Max(last.First, last.Second);
        int Breaks((int First, int Second) pair) => !_closeToLast ? 0 : (pair.First != tile ? 1 : 0) + (pair.Second == _lastTile ? 1 : 0);
        // Trills in a row keep off the lanes the last ones played while those are still in the quick window (user rule: a
        // lane does not come back that soon while another is free), so they take turns: 4-3 2-1 4-3.
        int Recent((int First, int Second) pair) => (_recent?.Invoke(pair.First, 0) == true ? 1 : 0) + (_recent?.Invoke(pair.Second, 1) == true ? 1 : 0);
        return found
            .OrderBy(Breaks)
            .ThenBy(Recent)
            .ThenBy(pair => reversed.Contains(pair) ? 1 : 0)
            .ThenBy(pair => TrillPair(pair.First, pair.Second) ? 0 : 1)
            .ThenBy(pair => Repeats(pair) ? 1 : 0)
            .Select(pair => ((int First, int Second)?)pair).FirstOrDefault();
    }

    /// <summary>
    /// The head of a three-lane roll for a single that a trill follows closely (user rule: a b c close together read 4 3-2,
    /// not 2 4-3): a tile with two lanes after it in the patterns' direction, so the trill rolls on from it. A roll whose
    /// trill keeps off the center pair comes first (user rule: 3 2-1 before 4 3-2; the center is taken only where no tile
    /// heads another roll, and 4 2-1 is never made here). Null when this tile already heads the best roll there is, or when
    /// no tile heads one; the nearest to this tile comes first.
    /// </summary>
    private int? RollHead(int tile)
    {
        bool Leads(int head, bool center) => IndexInPattern(head) >= 0 && Neighbour(head, _step) is { } second
            && Neighbour(second, _step) is { } third && (center ? Adjacent(second, third) : TrillPair(second, third));
        var at = IndexOf(tile);
        // A tile at the far edge already leads a roll when the pattern goes round from it (2 1 4-3): moving it would only
        // break the cycle the trill continues.
        var round = IndexInPattern(tile) is var place && place >= 0 ? _pattern[(place + 1) % _pattern.Count] : 0;
        if (Neighbour(tile, _step) is null && round != 0 && Neighbour(round, _step) is { } partner && TrillPair(round, partner)) return null;
        // A roll over the tile just played would bring it straight back (3 4-3) right after it. For a roll off the center,
        // anywhere inside the quick window is too soon (1 3 2-1 with lane 4 free): that is where 3 cannot be the head and the
        // center roll is taken. The center roll is the fallback, so only a close tile just played stops it: what is left
        // without it (2 4-3) comes back as soon and jumps back as well.
        bool Clear(int head, bool center)
        {
            if (head == _lastTile) return false;
            var second = Neighbour(head, _step);
            var lanes = new[] { head, second ?? 0, second is { } middle ? Neighbour(middle, _step) ?? 0 : 0 };
            var back = Array.IndexOf(lanes, _lastTile);
            return back < 0 || !(_closeToLast || (!center && _tooSoon?.Invoke(back) == true));
        }
        foreach (var center in new[] { false, true })
        {
            if (Leads(tile, center) && Clear(tile, center)) return null;
            var heads = _visible.Where(head => head != _lastTile && Leads(head, center) && Clear(head, center))
                .OrderBy(head => Math.Abs(IndexOf(head) - at))
                .ToArray();
            if (heads.Length > 0) return Place(heads[0]);
        }
        return null;
    }

    /// <summary>Two lanes side by side on screen, the pair across the center included.</summary>
    private bool Adjacent(int first, int second) => SimultaneousNotePlanner.IsTrillPair(_visible.Count, IndexOf(first), IndexOf(second), false);

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
    /// The single after a trill sits beside it on the side the roll ran to (user rule: after a roll on 1-2 comes 3, after
    /// 3-4 comes 2). A roll that ran into the edge has no lane there, and then the pattern simply goes on from the trill's
    /// last note: going back to the lane on the other side left a lane out of the cycle (4 3-2 1 3 2-1 instead of
    /// 4 3-2 1 4 3-2 1, user report). The pattern then walks on from that tile. Null when the tile is not in the pattern or
    /// would repeat the one just played.
    /// </summary>
    private int? BesideTrill((int First, int Second) trill)
    {
        var first = IndexOf(trill.First);
        var second = IndexOf(trill.Second);
        if (first < 0 || second < 0 || Math.Abs(first - second) != 1) return null;
        var lane = second + (second - first);
        if (lane < 0 || lane >= _visible.Count || IndexInPattern(_visible[lane]) < 0 || _visible[lane] == _lastTile) return null;
        return Place(_visible[lane]);
    }

    private int IndexInPattern(int tile)
    {
        for (var i = 0; i < _pattern.Count; i++)
            if (_pattern[i] == tile) return i;
        return -1;
    }

    private int Choose(int requestedMode, bool anticipated, Func<int, bool>? tooSoon)
    {
        ValidateMode(requestedMode);
        // With immediate switching off, every change waits for the cycle boundary.
        if (!_settings.ImmediateDownshift) anticipated = false;
        if (anticipated && requestedMode > CurrentMode)
        {
            CurrentMode = requestedMode;
            _pattern = _settings.GetPattern(requestedMode);
            _emittedInCycle = 0;
            _offset = FreshStart(tooSoon);
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
            _offset = FreshStart(tooSoon);
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
                _offset = FreshStart(tooSoon);
            }
            _emittedInCycle = 0;
            PendingMode = null;
        }
        return Peek();
    }

    /// <summary>
    /// Where a new pattern starts: its first tile, or the second when the first would repeat the tile just played. The
    /// same key twice in a row is hard to play (user rule), and a switch is exactly where a new pattern meets the old one.
    /// When the first tile would bring the tile just played straight back on the note after it (a switch from 2K to 4K
    /// after 3 read 3 4 3, user report), the pattern goes on from that tile instead (3 2 1). The same holds further on:
    /// when <paramref name="tooSoon"/> says the tile just played would come back too soon (2 | 4 3 2 at 47 ms with lane 1
    /// free, user report), the pattern goes on from that tile as if it had been running all along (2 | 1 4 3 2).
    /// </summary>
    private int FreshStart(Func<int, bool>? tooSoon = null)
    {
        if (_pattern.Count <= 1) return 0;
        var start = _pattern[0] == _lastTile ? 1 : _pattern.Count > 2 && _pattern[1] == _lastTile ? 2 : 0;
        var at = IndexInPattern(_lastTile);
        if (tooSoon is not null && at >= 0 && tooSoon(((at - start) % _pattern.Count + _pattern.Count) % _pattern.Count))
            start = (at + 1) % _pattern.Count;
        return start;
    }

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
    /// when it would repeat the one just played, as an immediate switch does. <paramref name="tooSoon"/> is asked how many
    /// notes on the tile just played would come back; when it says too soon the cycle goes on instead. Returns whether it
    /// restarted.
    /// </summary>
    public bool RestartCycle(Func<int, bool>? tooSoon = null)
    {
        var start = FreshStart();
        for (var notes = 0; tooSoon is not null && notes < _pattern.Count; notes++)
        {
            if (_pattern[(start + notes) % _pattern.Count] != _lastTile) continue;
            if (tooSoon(notes)) return false;
            break;
        }
        _emittedInCycle = 0;
        _offset = start;
        return true;
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
