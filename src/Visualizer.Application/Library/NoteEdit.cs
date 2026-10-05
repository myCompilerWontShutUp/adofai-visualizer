using Visualizer.Core.Maps;

namespace Visualizer.Application.Library;

/// <summary>
/// A hand-made note layout for one song: which lane every pressed hit takes and which hits are played as one chord.
/// Hit numbers are indexes into the cached hit times, so they do not move when settings change. Nothing here touches a
/// hit's time, the notes that exist, or the judgment; only lanes and chord grouping are the user's to change.
/// </summary>
public sealed record NoteEdit(
    string SourceHash,
    IReadOnlyDictionary<int, int> Lanes,
    IReadOnlyList<IReadOnlyList<int>> Chords)
{
    public const int SchemaVersion = 1;

    /// <summary>The chord a hit belongs to, or null when it is played alone.</summary>
    public IReadOnlyList<int>? ChordOf(int hit) => Chords.FirstOrDefault(group => group.Contains(hit));
}

/// <summary>
/// What a used slot holds, for the slot picker: when the edit was made and last saved, its size, the settings it was made
/// with and the song text it was made from (to tell whether the song changed since).
/// </summary>
public sealed record EditSlotInfo(int Slot, DateTimeOffset? CreatedAt, DateTimeOffset? SavedAt, int Notes, int Chords,
    int? MaximumTileCount, string SourceHash);

/// <summary>One note on the edit screen: the hit it plays, when it shows, its lane, and the chord it belongs to.</summary>
public sealed record EditNote(int Hit, double HitTimeMs, double ShowTimeMs, int Tile, double? HoldEndMs, int Chord)
{
    /// <summary>Whether the note is held; the lane stays busy until <see cref="HoldEndMs"/>.</summary>
    public bool IsHold => HoldEndMs is not null;
    /// <summary>Last moment the lane is busy.</summary>
    public double EndMs => HoldEndMs ?? ShowTimeMs;
}

/// <summary>Why an edit did not happen. <see cref="Blocked"/> is quiet; the rest say a word under the screen.</summary>
public enum EditRefusal { None, Blocked, LaneTaken, HoldInTheWay, BadMerge, BadSplit, MixedChords, NoteBetween, LanesApart }
