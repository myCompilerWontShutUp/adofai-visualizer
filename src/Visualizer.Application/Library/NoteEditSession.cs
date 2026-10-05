using Visualizer.Core.Maps;

namespace Visualizer.Application.Library;

/// <summary>
/// The note layout being edited: the notes, what is selected, and the moves the screen offers. Everything here is plain
/// data, so the screen only draws and sends keys. Hit times, the set of notes and the judgment never change — only lanes
/// and chord grouping do.
/// </summary>
public sealed class NoteEditSession
{
    private readonly List<EditNote> _notes;
    private readonly List<EditNote[]> _undo = [];
    private readonly List<EditNote[]> _redo = [];
    private readonly HashSet<int> _selection = [];
    private const int UndoDepth = 200;
    /// <summary>Two notes at the same moment count as one chord; times are compared with this slack (ms).</summary>
    private const double Epsilon = 0.001;

    /// <summary>The tiles on screen, ascending. Notes carry tile numbers, and a lane is a place in this list.</summary>
    public IReadOnlyList<int> Lanes { get; }
    /// <summary>How many lanes the screen shows.</summary>
    public int Tiles => Lanes.Count;
    public string SourceHash { get; }
    public IReadOnlyList<EditNote> Notes => _notes;
    /// <summary>Indexes into <see cref="Notes"/> that the next key applies to.</summary>
    public IReadOnlySet<int> Selection => _selection;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    /// <summary>Whether anything differs from the layout this session started with.</summary>
    public bool Changed { get; private set; }
    /// <summary>
    /// Chords of the opened layout that were split because another note sits between their two hits (older edits could hold
    /// them; the play screen never showed them as chords). The editor says how many.
    /// </summary>
    public int Loosened { get; }

    /// <summary>
    /// Builds the session from the notes the app prepared. <paramref name="tiles"/> is how many lanes are on screen, so a
    /// note can never be moved out of sight.
    /// </summary>
    public NoteEditSession(IEnumerable<EditNote> notes, IReadOnlyList<int> lanes, string sourceHash = "")
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(lanes);
        if (lanes.Count is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(lanes));
        _notes = notes.OrderBy(note => note.ShowTimeMs).ThenBy(note => note.Hit).ToList();
        Lanes = lanes.OrderBy(tile => tile).ToArray();
        SourceHash = sourceHash;
        // A chord that skips a note in between cannot be one (user rule), so it opens split; saving keeps that.
        var skipping = _notes.Where(note => note.Chord > 0).GroupBy(note => note.Chord)
            .Where(group => HasNoteBetween(group.Min(note => note.Hit), group.Max(note => note.Hit))).Select(group => group.Key).ToHashSet();
        if (skipping.Count == 0) return;
        for (var index = 0; index < _notes.Count; index++)
            if (skipping.Contains(_notes[index].Chord))
                _notes[index] = _notes[index] with { Chord = 0, ShowTimeMs = _notes[index].HitTimeMs };
        _notes.Sort((left, right) => left.ShowTimeMs != right.ShowTimeMs ? left.ShowTimeMs.CompareTo(right.ShowTimeMs) : left.Hit.CompareTo(right.Hit));
        Loosened = skipping.Count;
        Changed = true;
    }

    /// <summary>Whether a note of the song lies between these two hits, however close in time (user rule).</summary>
    private bool HasNoteBetween(int firstHit, int lastHit) =>
        _notes.Any(note => note.Hit > Math.Min(firstHit, lastHit) && note.Hit < Math.Max(firstHit, lastHit));

    /// <summary>Where a tile sits on screen, 0-based, or -1 when it is not shown.</summary>
    public int LaneOf(int tile)
    {
        for (var lane = 0; lane < Lanes.Count; lane++) if (Lanes[lane] == tile) return lane;
        return -1;
    }

    /// <summary>Builds the session from a timeline and a layout, which is how a saved edit is opened again.</summary>
    public static NoteEditSession FromLayout(ParsedTimeline timeline, NoteEdit edit, IReadOnlyList<int> lanes)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(edit);
        return new NoteEditSession(BuildNotes(timeline, edit), lanes, edit.SourceHash);
    }

    /// <summary>The notes a layout stands for: one per pressed hit, a chord showing at its first hit's time.</summary>
    public static IReadOnlyList<EditNote> BuildNotes(ParsedTimeline timeline, NoteEdit edit)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(edit);
        var notes = new List<EditNote>();
        var chordOf = new Dictionary<int, int>();
        for (var group = 0; group < edit.Chords.Count; group++)
            foreach (var hit in edit.Chords[group]) chordOf[hit] = group + 1;
        for (var hit = 0; hit < timeline.HitTimesMs.Count; hit++)
        {
            if (!IsPressed(timeline, hit) || !edit.Lanes.TryGetValue(hit, out var tile)) continue;
            var chord = chordOf.TryGetValue(hit, out var id) ? id : 0;
            var show = chord == 0 ? timeline.HitTimesMs[hit]
                : edit.Chords[chord - 1].Where(other => IsPressed(timeline, other)).Min(other => timeline.HitTimesMs[other]);
            notes.Add(new EditNote(hit, timeline.HitTimesMs[hit], show, tile, HoldEnd(timeline, hit), chord));
        }
        return notes;
    }

    /// <summary>Whether this hit is one the player presses, which is what becomes a note.</summary>
    public static bool IsPressed(ParsedTimeline timeline, int hit)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (hit < 0 || hit >= timeline.HitTimesMs.Count) return false;
        var kind = timeline.HitKinds is { } kinds && hit < kinds.Count ? kinds[hit] : HitKind.Tap;
        return kind is HitKind.Tap or HitKind.HoldStart or HitKind.HoldChain;
    }

    private static double? HoldEnd(ParsedTimeline timeline, int hit)
    {
        if (timeline.HitKinds is not { } kinds || hit >= kinds.Count || !ParsedTimeline.Opens(kinds[hit])) return null;
        for (var next = hit + 1; next < kinds.Count; next++)
            if (ParsedTimeline.Closes(kinds[next])) return timeline.HitTimesMs[next];
        return null;
    }

    /// <summary>The layout as it stands, ready to be saved.</summary>
    public NoteEdit ToLayout() => new(SourceHash,
        _notes.ToDictionary(note => note.Hit, note => note.Tile),
        _notes.Where(note => note.Chord > 0).GroupBy(note => note.Chord)
            .Select(group => (IReadOnlyList<int>)group.Select(note => note.Hit).OrderBy(hit => hit).ToArray()).ToArray());

    // ---- selection -------------------------------------------------------

    /// <summary>Replaces the selection. A held note always brings the rest of its chord along.</summary>
    public void Select(IEnumerable<int> notes, bool add = false)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (!add) _selection.Clear();
        foreach (var index in notes)
        {
            if (index < 0 || index >= _notes.Count) continue;
            _selection.Add(index);
            // A chord is pressed as one thing, so its members are picked together.
            if (_notes[index].Chord > 0)
                for (var other = 0; other < _notes.Count; other++)
                    if (_notes[other].Chord == _notes[index].Chord) _selection.Add(other);
        }
    }

    public void Toggle(int index)
    {
        if (index < 0 || index >= _notes.Count) return;
        if (!_selection.Contains(index))
        {
            Select([index], add: true);
            return;
        }
        // A chord leaves the selection as a whole too, so no move can pull its two notes apart.
        var chord = _notes[index].Chord;
        _selection.RemoveWhere(other => other == index || (chord > 0 && _notes[other].Chord == chord));
    }

    public void SelectRange(int from, int to)
    {
        if (from > to) (from, to) = (to, from);
        Select(Enumerable.Range(Math.Max(0, from), Math.Max(0, Math.Min(_notes.Count - 1, to) - Math.Max(0, from) + 1)));
    }

    public void SelectAll() => Select(Enumerable.Range(0, _notes.Count));
    public void ClearSelection() => _selection.Clear();

    /// <summary>The note after (or before) the selection, for w and s.</summary>
    public int Step(int direction)
    {
        if (_notes.Count == 0) return -1;
        if (_selection.Count == 0) return direction >= 0 ? 0 : _notes.Count - 1;
        var edge = direction >= 0 ? _selection.Max() + 1 : _selection.Min() - 1;
        return Math.Clamp(edge, 0, _notes.Count - 1);
    }

    // ---- moves -----------------------------------------------------------

    /// <summary>Moves the selection one lane left (-1) or right (+1). It stops as a whole when either edge hits a wall.</summary>
    public EditRefusal Nudge(int direction) => Shift(direction);

    /// <summary>
    /// Moves the selection towards a lane the user typed. The whole selection slides so that its near edge lands on
    /// <paramref name="tile"/>; a tile already inside the selection's span changes nothing.
    /// </summary>
    public EditRefusal ToTile(int tile)
    {
        if (_selection.Count == 0 || tile < 1 || tile > Tiles) return EditRefusal.Blocked;
        var lanes = _selection.Select(index => LaneOf(_notes[index].Tile)).ToArray();
        if (lanes.Any(lane => lane < 0)) return EditRefusal.Blocked;
        var wanted = tile - 1;
        var delta = wanted > lanes.Max() ? wanted - lanes.Max() : wanted < lanes.Min() ? wanted - lanes.Min() : 0;
        return delta == 0 ? EditRefusal.Blocked : Shift(delta);
    }

    private EditRefusal Shift(int delta)
    {
        if (_selection.Count == 0 || delta == 0) return EditRefusal.Blocked;
        var moving = _selection.ToArray();
        // A wall stops the whole selection, so the shape the user sees is kept.
        var wanted = new int[moving.Length];
        for (var i = 0; i < moving.Length; i++)
        {
            var lane = LaneOf(_notes[moving[i]].Tile) + delta;
            if (lane < 0 || lane >= Lanes.Count) return EditRefusal.Blocked;
            wanted[i] = Lanes[lane];
        }
        for (var i = 0; i < moving.Length; i++)
        {
            var refusal = Free(moving[i], wanted[i], moving);
            if (refusal != EditRefusal.None) return refusal;
        }
        Remember();
        for (var i = 0; i < moving.Length; i++) _notes[moving[i]] = _notes[moving[i]] with { Tile = wanted[i] };
        return EditRefusal.None;
    }

    /// <summary>
    /// Mirrors the selection left to right within the lanes it spans (user rule): notes on lanes 5 and 6 swap and stay there,
    /// the outermost lanes of the selection swap, and with an odd number of lanes the middle one stays. A selection on one
    /// lane has nothing to flip. Anything in the way stops it, as a move does.
    /// </summary>
    public EditRefusal Flip()
    {
        if (_selection.Count == 0) return EditRefusal.Blocked;
        var used = _selection.Select(index => LaneOf(_notes[index].Tile)).Where(lane => lane >= 0).ToArray();
        if (used.Length == 0) return EditRefusal.Blocked;
        var (low, high) = (used.Min(), used.Max());
        var moving = _selection.ToArray();
        var wanted = new int[moving.Length];
        for (var i = 0; i < moving.Length; i++)
        {
            var lane = LaneOf(_notes[moving[i]].Tile);
            if (lane < 0) return EditRefusal.Blocked;
            wanted[i] = Lanes[low + high - lane];
        }
        // Everything already on the middle lane: nothing would move.
        if (moving.Select((index, i) => _notes[index].Tile == wanted[i]).All(same => same)) return EditRefusal.Blocked;
        for (var i = 0; i < moving.Length; i++)
        {
            var refusal = Free(moving[i], wanted[i], moving);
            if (refusal != EditRefusal.None) return refusal;
        }
        Remember();
        for (var i = 0; i < moving.Length; i++) _notes[moving[i]] = _notes[moving[i]] with { Tile = wanted[i] };
        return EditRefusal.None;
    }

    /// <summary>Whether a note may take this lane: nothing else may be on it while the note is there.</summary>
    private EditRefusal Free(int index, int tile, IReadOnlyCollection<int> moving)
    {
        var note = _notes[index];
        for (var other = 0; other < _notes.Count; other++)
        {
            if (other == index || moving.Contains(other)) continue;
            var rival = _notes[other];
            if (rival.Tile != tile) continue;
            if (rival.ShowTimeMs > note.EndMs + Epsilon || rival.EndMs + Epsilon < note.ShowTimeMs) continue;
            return rival.IsHold || note.IsHold ? EditRefusal.HoldInTheWay : EditRefusal.LaneTaken;
        }
        return EditRefusal.None;
    }

    // ---- chords ----------------------------------------------------------

    /// <summary>
    /// Whether two single notes may be one chord (user rules): within <paramref name="windowMs"/> (40ms at
    /// most), no other note between their hits however close, and on lanes side by side.
    /// </summary>
    private EditRefusal CanMerge(EditNote first, EditNote second, double windowMs)
    {
        if (first.Chord > 0 || second.Chord > 0) return EditRefusal.BadMerge;
        if (first.Tile == second.Tile) return EditRefusal.LaneTaken;
        var window = Math.Min(windowMs, Core.Settings.VisualizerSettings.MaximumChordWindowMs);
        if (Math.Abs(second.HitTimeMs - first.HitTimeMs) > window + Epsilon) return EditRefusal.BadMerge;
        if (HasNoteBetween(first.Hit, second.Hit)) return EditRefusal.NoteBetween;
        if (Math.Abs(LaneOf(first.Tile) - LaneOf(second.Tile)) != 1) return EditRefusal.LanesApart;
        return EditRefusal.None;
    }

    /// <summary>Makes the two selected notes one chord: the later one shows at the earlier one's time.</summary>
    public EditRefusal Merge(double windowMs)
    {
        if (_selection.Count != 2) return EditRefusal.BadMerge;
        var pair = _selection.OrderBy(index => _notes[index].ShowTimeMs).ThenBy(index => _notes[index].Hit).ToArray();
        var (first, second) = (_notes[pair[0]], _notes[pair[1]]);
        if (CanMerge(first, second, windowMs) is var refusal and not EditRefusal.None) return refusal;
        Remember();
        var id = _notes.Count == 0 ? 1 : _notes.Max(note => note.Chord) + 1;
        _notes[pair[0]] = first with { Chord = id };
        // The chord shows at its first hit, exactly as an automatic chord does.
        _notes[pair[1]] = second with { Chord = id, ShowTimeMs = first.ShowTimeMs };
        Sort();
        return EditRefusal.None;
    }

    /// <summary>
    /// Merges the selected single notes in pairs, earliest first, wherever a pair can be one chord (within the window, on
    /// different lanes); a pair that cannot is skipped and its later note tries the next one.
    /// Chords in the selection stay as they are. One undo step takes all the pairs back.
    /// </summary>
    public EditRefusal MergeAll(double windowMs)
    {
        var singles = _selection.Where(index => _notes[index].Chord == 0)
            .OrderBy(index => _notes[index].ShowTimeMs).ThenBy(index => _notes[index].Hit).ToArray();
        var pairs = new List<(int First, int Second)>();
        var reason = EditRefusal.BadMerge;
        for (var k = 0; k + 1 < singles.Length;)
        {
            var refusal = CanMerge(_notes[singles[k]], _notes[singles[k + 1]], windowMs);
            if (refusal == EditRefusal.None)
            {
                pairs.Add((singles[k], singles[k + 1]));
                k += 2;
                continue;
            }
            // The first reason is the one said when nothing merges.
            if (pairs.Count == 0 && reason == EditRefusal.BadMerge) reason = refusal;
            k++;
        }
        if (pairs.Count == 0) return reason;
        Remember();
        var id = _notes.Max(note => note.Chord) + 1;
        foreach (var (first, second) in pairs)
        {
            _notes[first] = _notes[first] with { Chord = id };
            // The chord shows at its first hit, exactly as an automatic chord does.
            _notes[second] = _notes[second] with { Chord = id, ShowTimeMs = _notes[first].ShowTimeMs };
            id++;
        }
        Sort();
        return EditRefusal.None;
    }

    /// <summary>
    /// Ctrl+R: splits when only chords are picked and merges when only single notes are. A mix of both does nothing, since
    /// either choice would be a guess; Ctrl+M and Ctrl+U act on one kind and leave the other.
    /// </summary>
    public EditRefusal SwitchChords(double windowMs)
    {
        if (_selection.Count == 0) return EditRefusal.Blocked;
        var chords = _selection.Any(index => _notes[index].Chord > 0);
        var singles = _selection.Any(index => _notes[index].Chord == 0);
        if (chords && singles) return EditRefusal.MixedChords;
        return chords ? Split() : MergeAll(windowMs);
    }

    /// <summary>Breaks the selected chords apart; every hit goes back to showing at its own time.</summary>
    public EditRefusal Split()
    {
        var chords = _selection.Select(index => _notes[index].Chord).Where(chord => chord > 0).ToHashSet();
        if (chords.Count == 0) return EditRefusal.BadSplit;
        Remember();
        for (var index = 0; index < _notes.Count; index++)
            if (chords.Contains(_notes[index].Chord))
                _notes[index] = _notes[index] with { Chord = 0, ShowTimeMs = _notes[index].HitTimeMs };
        Sort();
        return EditRefusal.None;
    }

    // ---- history ---------------------------------------------------------

    /// <summary>One note of an override: the lane it goes on, and the group it shares with the note it is a chord with.</summary>
    public readonly record struct NoteOverride(int Hit, int Tile, int? Chord, double? HitTimeMs);

    /// <summary>
    /// How close two notes on one lane have to be before the hand could not hit both. Used only to refuse an override
    /// that stacks notes; the placement rules themselves are elsewhere.
    /// </summary>
    public const double SameLaneGuardMs = 50;

    /// <summary>
    /// Puts whole lanes in at once, as the console's override does with the JSON note prints. Notes not named are left
    /// where they are. Nothing is changed unless every line passes: a time that does not match the note, a lane off the
    /// screen, two notes landing on one lane too close together, or a chord the rules would not allow all stop it, and the
    /// reason comes back instead (user rule). The whole thing is one undo step.
    /// </summary>
    public string? Override(IReadOnlyList<NoteOverride> notes, double windowMs)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var byHit = new Dictionary<int, int>();
        var wanted = _notes.ToArray();
        foreach (var note in notes)
        {
            var index = Array.FindIndex(wanted, each => each.Hit == note.Hit - 1);
            if (index < 0) return $"note {note.Hit} is not in this song.";
            if (LaneOf(note.Tile) < 0) return $"note {note.Hit}: lane {note.Tile} is not on the screen.";
            if (note.HitTimeMs is { } time && Math.Abs(time - wanted[index].HitTimeMs) > 0.5)
                return $"note {note.Hit} is at {wanted[index].HitTimeMs:0} ms, not {time:0} ms.";
            if (!byHit.TryAdd(note.Hit, note.Tile)) return $"note {note.Hit} is named twice.";
            // Naming a note takes it out of whatever chord it was in, and the note it was paired with comes out with it:
            // half a chord is not a thing the rest of the app knows how to read.
            var was = wanted[index].Chord;
            if (was != 0)
                for (var other = 0; other < wanted.Length; other++)
                    if (wanted[other].Chord == was)
                        wanted[other] = wanted[other] with { Chord = 0, ShowTimeMs = wanted[other].HitTimeMs };
            wanted[index] = wanted[index] with { Tile = note.Tile, Chord = 0, ShowTimeMs = wanted[index].HitTimeMs };
        }
        // Chords are rebuilt from the groups the lines share, and each pair goes through the same rules as a merge.
        var groups = notes.Where(note => note.Chord is > 0).GroupBy(note => note.Chord!.Value).ToArray();
        var id = 0;
        foreach (var group in groups)
        {
            var members = group.Select(note => Array.FindIndex(wanted, each => each.Hit == note.Hit - 1)).OrderBy(index => index).ToArray();
            if (members.Length != 2) return $"chord {group.Key} has {members.Length} notes; a chord is two.";
            var (first, second) = (wanted[members[0]], wanted[members[1]]);
            if (Math.Abs(second.HitTimeMs - first.HitTimeMs) > Math.Min(windowMs, Core.Settings.VisualizerSettings.MaximumChordWindowMs) + Epsilon)
                return $"notes {first.Hit + 1} and {second.Hit + 1} are too far apart to be a chord.";
            if (Math.Abs(LaneOf(first.Tile) - LaneOf(second.Tile)) != 1)
                return $"notes {first.Hit + 1} and {second.Hit + 1} are not on lanes next to each other.";
            if (wanted.Any(each => each.Hit > first.Hit && each.Hit < second.Hit))
                return $"another note lies between {first.Hit + 1} and {second.Hit + 1}, so they cannot be a chord.";
            id++;
            wanted[members[0]] = first with { Chord = id };
            wanted[members[1]] = second with { Chord = id, ShowTimeMs = first.ShowTimeMs };
        }
        // Two notes on one lane within a hand's reach of each other cannot be played, however they were asked for.
        var order = wanted.OrderBy(note => note.HitTimeMs).ToArray();
        for (var i = 1; i < order.Length; i++)
            for (var back = i - 1; back >= 0 && order[i].HitTimeMs - order[back].HitTimeMs < SameLaneGuardMs; back--)
                if (order[back].Tile == order[i].Tile && order[back].Hit != order[i].Hit)
                    return $"notes {order[back].Hit + 1} and {order[i].Hit + 1} are on lane {order[i].Tile} less than"
                           + $" {SameLaneGuardMs:0} ms apart.";
        Remember();
        _notes.Clear();
        _notes.AddRange(wanted);
        Sort();
        return null;
    }

    public bool Undo() => Step(_undo, _redo);
    public bool Redo() => Step(_redo, _undo);

    private bool Step(List<EditNote[]> from, List<EditNote[]> to)
    {
        if (from.Count == 0) return false;
        to.Add(_notes.ToArray());
        _notes.Clear();
        _notes.AddRange(from[^1]);
        from.RemoveAt(from.Count - 1);
        Changed = _undo.Count > 0;
        _selection.RemoveWhere(index => index >= _notes.Count);
        return true;
    }

    private void Remember()
    {
        _undo.Add(_notes.ToArray());
        if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
        _redo.Clear();
        Changed = true;
    }

    private void Sort()
    {
        var picked = _selection.Select(index => _notes[index]).ToHashSet();
        _notes.Sort((left, right) => left.ShowTimeMs != right.ShowTimeMs
            ? left.ShowTimeMs.CompareTo(right.ShowTimeMs) : left.Hit.CompareTo(right.Hit));
        _selection.Clear();
        for (var index = 0; index < _notes.Count; index++)
            if (picked.Contains(_notes[index])) _selection.Add(index);
    }
}
