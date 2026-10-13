using Visualizer.Core.Maps;
using static Visualizer.Core.Localization.ConsoleText;

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
        // A chord that skips a note in between cannot be one (user rule), so it opens split; saving keeps that. Its own
        // members between its first and last hit are the chord itself (a chord of three or more).
        var skipping = _notes.Where(note => note.Chord > 0).GroupBy(note => note.Chord)
            .Where(group => HasNoteBetween(group.Min(note => note.Hit), group.Max(note => note.Hit), group.Select(note => note.Hit)))
            .Select(group => group.Key).ToHashSet();
        if (skipping.Count == 0) return;
        for (var index = 0; index < _notes.Count; index++)
            if (skipping.Contains(_notes[index].Chord))
                _notes[index] = _notes[index] with { Chord = 0, ShowTimeMs = _notes[index].HitTimeMs };
        _notes.Sort((left, right) => left.ShowTimeMs != right.ShowTimeMs ? left.ShowTimeMs.CompareTo(right.ShowTimeMs) : left.Hit.CompareTo(right.Hit));
        Loosened = skipping.Count;
        Changed = true;
    }

    /// <summary>
    /// Whether a note of the song lies between these two hits, however close in time (user rule). <paramref name="members"/>
    /// are the chord's own hits, which do not count.
    /// </summary>
    private bool HasNoteBetween(int firstHit, int lastHit, IEnumerable<int>? members = null)
    {
        var own = members?.ToHashSet() ?? [];
        return _notes.Any(note => note.Hit > Math.Min(firstHit, lastHit) && note.Hit < Math.Max(firstHit, lastHit) && !own.Contains(note.Hit));
    }

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
    /// Whether single notes may be one chord (user rules): two or more, the last within <paramref name="windowMs"/> (40ms at
    /// most) of the first, so start and end are both in it; no other note between their hits however close; and on lanes
    /// side by side, one lane each.
    /// </summary>
    private EditRefusal CanMerge(IReadOnlyList<EditNote> members, double windowMs)
    {
        if (members.Count < 2 || members.Any(note => note.Chord > 0)) return EditRefusal.BadMerge;
        if (members.Select(note => note.Tile).Distinct().Count() != members.Count) return EditRefusal.LaneTaken;
        var window = Math.Min(windowMs, Core.Settings.VisualizerSettings.MaximumChordWindowMs);
        if (members.Max(note => note.HitTimeMs) - members.Min(note => note.HitTimeMs) > window + Epsilon) return EditRefusal.BadMerge;
        if (HasNoteBetween(members.Min(note => note.Hit), members.Max(note => note.Hit), members.Select(note => note.Hit))) return EditRefusal.NoteBetween;
        var lanes = members.Select(note => LaneOf(note.Tile)).Order().ToArray();
        if (lanes[0] < 0 || lanes[^1] - lanes[0] != lanes.Length - 1) return EditRefusal.LanesApart;
        return EditRefusal.None;
    }

    private EditRefusal CanMerge(EditNote first, EditNote second, double windowMs) => CanMerge([first, second], windowMs);

    /// <summary>Makes the selected notes one chord, two or more of them: the later ones show at the earliest one's time.</summary>
    public EditRefusal Merge(double windowMs)
    {
        if (_selection.Count < 2) return EditRefusal.BadMerge;
        var members = _selection.OrderBy(index => _notes[index].ShowTimeMs).ThenBy(index => _notes[index].Hit).ToArray();
        if (CanMerge(members.Select(index => _notes[index]).ToArray(), windowMs) is var refusal and not EditRefusal.None) return refusal;
        Remember();
        var id = _notes.Count == 0 ? 1 : _notes.Max(note => note.Chord) + 1;
        var shown = _notes[members[0]].ShowTimeMs;
        // The chord shows at its first hit, exactly as an automatic chord does.
        foreach (var index in members) _notes[index] = _notes[index] with { Chord = id, ShowTimeMs = shown };
        Sort();
        return EditRefusal.None;
    }

    /// <summary>
    /// Merges the selected single notes, earliest first, into as wide chords as the rules allow (within the window, on lanes
    /// side by side, user rule: two or more); a note that joins none is skipped and the next one tries.
    /// Chords in the selection stay as they are. One undo step takes all of them back.
    /// </summary>
    public EditRefusal MergeAll(double windowMs)
    {
        var singles = _selection.Where(index => _notes[index].Chord == 0)
            .OrderBy(index => _notes[index].ShowTimeMs).ThenBy(index => _notes[index].Hit).ToArray();
        var chords = new List<int[]>();
        var reason = EditRefusal.BadMerge;
        for (var k = 0; k + 1 < singles.Length;)
        {
            var refusal = CanMerge(_notes[singles[k]], _notes[singles[k + 1]], windowMs);
            if (refusal != EditRefusal.None)
            {
                // The first reason is the one said when nothing merges.
                if (chords.Count == 0 && reason == EditRefusal.BadMerge) reason = refusal;
                k++;
                continue;
            }
            var end = k + 1;
            while (end + 1 < singles.Length
                && CanMerge(singles[k..(end + 2)].Select(index => _notes[index]).ToArray(), windowMs) == EditRefusal.None) end++;
            chords.Add(singles[k..(end + 1)]);
            k = end + 1;
        }
        if (chords.Count == 0) return reason;
        Remember();
        var id = _notes.Max(note => note.Chord) + 1;
        foreach (var members in chords)
        {
            var shown = _notes[members[0]].ShowTimeMs;
            // The chord shows at its first hit, exactly as an automatic chord does.
            foreach (var index in members) _notes[index] = _notes[index] with { Chord = id, ShowTimeMs = shown };
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
            if (index < 0) return T("OverrideNoNote", note.Hit);
            if (LaneOf(note.Tile) < 0) return T("OverrideLaneOff", note.Hit, note.Tile);
            if (note.HitTimeMs is { } time && Math.Abs(time - wanted[index].HitTimeMs) > 0.5)
                return T("OverrideTime", note.Hit, wanted[index].HitTimeMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                    time.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            if (!byHit.TryAdd(note.Hit, note.Tile)) return T("OverrideTwice", note.Hit);
            // Naming a note takes it out of whatever chord it was in, and the note it was paired with comes out with it:
            // half a chord is not a thing the rest of the app knows how to read.
            var was = wanted[index].Chord;
            if (was != 0)
                for (var other = 0; other < wanted.Length; other++)
                    if (wanted[other].Chord == was)
                        wanted[other] = wanted[other] with { Chord = 0, ShowTimeMs = wanted[other].HitTimeMs };
            wanted[index] = wanted[index] with { Tile = note.Tile, Chord = 0, ShowTimeMs = wanted[index].HitTimeMs };
        }
        // Chords are rebuilt from the groups the lines share, and each goes through the same rules as a merge: two notes or
        // more, the last within the window of the first, on lanes side by side, nothing else between.
        var groups = notes.Where(note => note.Chord is > 0).GroupBy(note => note.Chord!.Value).ToArray();
        var id = 0;
        foreach (var group in groups)
        {
            var members = group.Select(note => Array.FindIndex(wanted, each => each.Hit == note.Hit - 1)).OrderBy(index => index).ToArray();
            if (members.Length < 2) return T("OverrideChordSize", group.Key, members.Length);
            var (first, last) = (wanted[members[0]], wanted[members[^1]]);
            if (members.Max(index => wanted[index].HitTimeMs) - members.Min(index => wanted[index].HitTimeMs)
                > Math.Min(windowMs, Core.Settings.VisualizerSettings.MaximumChordWindowMs) + Epsilon)
                return T("OverrideChordFar", first.Hit + 1, last.Hit + 1);
            var lanes = members.Select(index => LaneOf(wanted[index].Tile)).Order().ToArray();
            if (lanes.Distinct().Count() != lanes.Length || lanes[^1] - lanes[0] != lanes.Length - 1)
                return T("OverrideChordLanes", first.Hit + 1, last.Hit + 1);
            var own = members.Select(index => wanted[index].Hit).ToHashSet();
            if (wanted.Any(each => each.Hit > first.Hit && each.Hit < last.Hit && !own.Contains(each.Hit)))
                return T("OverrideChordBetween", first.Hit + 1, last.Hit + 1);
            id++;
            var shown = first.ShowTimeMs;
            foreach (var index in members) wanted[index] = wanted[index] with { Chord = id, ShowTimeMs = shown };
        }
        // Two notes on one lane within a hand's reach of each other cannot be played, however they were asked for.
        var order = wanted.OrderBy(note => note.HitTimeMs).ToArray();
        for (var i = 1; i < order.Length; i++)
            for (var back = i - 1; back >= 0 && order[i].HitTimeMs - order[back].HitTimeMs < SameLaneGuardMs; back--)
                if (order[back].Tile == order[i].Tile && order[back].Hit != order[i].Hit)
                return T("OverrideSameLane", order[back].Hit + 1, order[i].Hit + 1, order[i].Tile,
                    SameLaneGuardMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
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
