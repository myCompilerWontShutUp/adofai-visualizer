using System.Globalization;
using System.Text.Json.Serialization;
using Visualizer.Core.Localization;
using static Visualizer.Core.Settings.DefaultSettings;

namespace Visualizer.Core.Settings;

/// <summary>A setting that failed validation: its property name and a readable reason.</summary>
public sealed record SettingsError(string Field, string Message);

/// <summary>
/// Immutable configuration snapshot. All time values are milliseconds. Every property starts from Settings/DefaultSettings.jsonc
/// (embedded at build), so initial values are changed in that file, not here.
/// </summary>
public sealed record VisualizerSettings
{
    public const string DefaultMapRoot = @"C:\Program Files (x86)\Steam\steamapps\workshop\content\977950";
    /// <summary>Reserved for pausing together with the game; not configurable.</summary>
    public const string EscapeKey = "Escape";
    /// <summary>Song list details, in the order they are shown.</summary>
    public static IReadOnlyList<string> SongListFieldNames { get; } = ["Artist", "Length", "Notes", "Difficulty", "Checkpoints", "Date", "Path"];
    /// <summary>The widest gap that may be treated as one chord, in the setting and in the note editor.</summary>
    public const double MaximumChordWindowMs = 40;
    /// <summary>The widest trill window. A trill is a fast "따닥", so it stays within one short gap.</summary>
    public const double MaximumTrillWindowMs = 200;
    /// <summary>Tile counts a mode can use, ascending.</summary>
    public static IReadOnlyList<int> Modes { get; } = [1, 2, 4, 6, 8];

    /// <summary>Language file id (Core Localization/Languages), for example "korean".</summary>
    public string Language { get; init; } = Text(nameof(Language));
    /// <summary>Palette file id of the window chrome (App Themes/Palettes); the note field and note rules are unaffected.</summary>
    public string Theme { get; init; } = Text(nameof(Theme));
    /// <summary>Folder scanned read-only by Map Sync on request. Never searched automatically.</summary>
    public string MapRootDirectory { get; init; } = Text(nameof(MapRootDirectory));
    /// <summary>App-owned store for settings and the map cache. Empty keeps the default location.</summary>
    public string DataRootDirectory { get; init; } = Text(nameof(DataRootDirectory));
    /// <summary>Song list order: a SongSortKey name such as "Difficulty" or "Manual".</summary>
    public string SongSortKey { get; init; } = Text(nameof(SongSortKey));
    /// <summary>Whether the larger value, later date or later letters come first in the song list.</summary>
    public bool SongSortHighFirst { get; init; } = Flag(nameof(SongSortHighFirst));
    /// <summary>Which map folder the song list shows: a SongGroupField name such as "Custom" or "All".</summary>
    public string SongGroup { get; init; } = Text(nameof(SongGroup));
    /// <summary>
    /// Which details the song list window shows under each name, space separated and always drawn in the order of
    /// <see cref="SongListFieldNames"/>. Switched in that window; an empty value leaves the names alone.
    /// </summary>
    public string SongListFields { get; init; } = Text(nameof(SongListFields));
    /// <summary>Guide, rule preview and example buttons above the song panel.</summary>
    public bool ShowGuideButtons { get; init; } = Flag(nameof(ShowGuideButtons));
    /// <summary>Song title, song info, start point and tile count over the note field while paused or reset.</summary>
    public bool ShowSongInfo { get; init; } = Flag(nameof(ShowSongInfo));
    public string ResetKey { get; init; } = Text(nameof(ResetKey));
    public string StartTriggerKey { get; init; } = Text(nameof(StartTriggerKey));
    public string VisualizerToggleKey { get; init; } = Text(nameof(VisualizerToggleKey));
    /// <summary>Optional: an empty value leaves checkpoint stepping to the main screen arrows.</summary>
    public string? PreviousCheckpointKey { get; init; } = OptionalText(nameof(PreviousCheckpointKey));
    public string? NextCheckpointKey { get; init; } = OptionalText(nameof(NextCheckpointKey));
    public string? SettingsHotkey { get; init; } = OptionalText(nameof(SettingsHotkey));
    /// <summary>When enabled, keys react only while ADOFAI or this visualizer is the foreground app; when disabled, in any app.</summary>
    public bool OnlyReactWhenAdofaiForeground { get; init; } = Flag(nameof(OnlyReactWhenAdofaiForeground));
    /// <summary>Whether applying a setting also writes it to the store, so the next run starts with it.</summary>
    public bool AutoSaveSettings { get; init; } = Flag(nameof(AutoSaveSettings));

    /// <summary>
    /// Whether a note's tile count comes from the gap after it instead of the gap before it. Off, a note is as busy as
    /// the run it just came from; on, it is as busy as what is coming, which is what a hand has to be ready for.
    /// </summary>
    public bool NextIntervalTiles { get; init; } = Flag(nameof(NextIntervalTiles));
    /// <summary>
    /// Whether a note's tile count comes from the shorter of its two gaps, before and after (user rule): a note between a
    /// fast gap and a slow one is as busy as the fast side, whichever side that is. Over <see cref="NextIntervalTiles"/>.
    /// </summary>
    public bool BothIntervalTiles { get; init; } = Flag(nameof(BothIntervalTiles));
    public string AdofaiProcessName { get; init; } = Text(nameof(AdofaiProcessName));
    public int MaximumTileCount { get; init; } = Integer(nameof(MaximumTileCount));
    /// <summary>Lowest mode dynamic timing may choose. Equal to the maximum, notes match dynamic timing off.</summary>
    public int MinimumTileCount { get; init; } = Integer(nameof(MinimumTileCount));
    /// <summary>
    /// How many lanes the screen shows even where the rules never use them: 1, 2, 4, 6 or 8, or 0 to follow
    /// <see cref="MaximumTileCount"/> (the default). It may not be below the maximum, since every lane the rules use has
    /// to be on screen.
    /// </summary>
    public int DisplayLaneCount { get; init; } = Integer(nameof(DisplayLaneCount));
    public bool DynamicTiming { get; init; } = Flag(nameof(DynamicTiming));
    /// <summary>Intervals below this are 8K (the 8K ↔ 6K threshold).</summary>
    public double EightKeyBoundaryMs { get; init; } = Number(nameof(EightKeyBoundaryMs));
    /// <summary>Intervals below this are 6K (the 6K ↔ 4K threshold).</summary>
    public double SixKeyBoundaryMs { get; init; } = Number(nameof(SixKeyBoundaryMs));
    public double FourKeyBoundaryMs { get; init; } = Number(nameof(FourKeyBoundaryMs));
    public double TwoKeyBoundaryMs { get; init; } = Number(nameof(TwoKeyBoundaryMs));
    public double SimultaneousWindowMs { get; init; } = Number(nameof(SimultaneousWindowMs));
    /// <summary>
    /// Two single notes this close are a trill: they take the two lanes of one pair so they can be played with two fingers
    /// (user rule). Chords are grouped first and keep their own window, so this only sees the notes they left; 0 switches
    /// trills off and a window below the chord window leaves nothing for them either.
    /// </summary>
    public double TrillWindowMs { get; init; } = Number(nameof(TrillWindowMs));
    /// <summary>Keeps a trill off the pair that crosses the screen center on four lanes, as <see cref="NoChordCenterPair"/> does.</summary>
    public bool NoTrillCenterPair { get; init; } = Flag(nameof(NoTrillCenterPair));
    /// <summary>Which pair a trill takes first; the same choices as <see cref="ChordLanePreference"/>.</summary>
    public string TrillLanePreference { get; init; } = Text(nameof(TrillLanePreference));
    /// <summary>
    /// A trill counts as one note, as a chord does (user rule): the tile count comes from the gaps around it, not the short
    /// one inside it, it is one step of the cycle, and it takes lanes side by side the way a chord does, each note still
    /// falling at its own time. Off, every note of a trill is a step of its own.
    /// </summary>
    public bool TrillAsOneNote { get; init; } = Flag(nameof(TrillAsOneNote));
    /// <summary>
    /// When enabled, fewer tiles start on the note that asks for them instead of at the end of the running cycle, the 1K→2K
    /// and 2K→4K anticipation switches there too, and a switch never repeats the tile just played. When disabled, every switch
    /// waits for the cycle boundary.
    /// </summary>
    public bool ImmediateDownshift { get; init; } = Flag(nameof(ImmediateDownshift));
    /// <summary>
    /// How many notes in a row must ask for fewer tiles before the smaller pattern starts. 1 switches down on the first such
    /// note; larger values ignore a single slow note. A note back at the current mode (or above) starts the count again.
    /// Used only while <see cref="ImmediateDownshift"/> is enabled.
    /// </summary>
    public int DownshiftNoteCount { get; init; } = Integer(nameof(DownshiftNoteCount));
    public double StartDelayMs { get; init; } = Number(nameof(StartDelayMs));
    public double PlaybackSpeedPercent { get; init; } = Number(nameof(PlaybackSpeedPercent));
    /// <summary>Keeps the practice speed when a song is loaded; off (the default) puts it back to 100%.</summary>
    public bool KeepSpeedOnLoad { get; init; } = Flag(nameof(KeepSpeedOnLoad));
    /// <summary>
    /// Whether a pause puts the next note the player has not pressed on the judgment line, as the game restarts on the tile
    /// it stopped at. Off leaves the notes where they were (user rule).
    /// </summary>
    public bool AlignNextNoteOnPause { get; init; } = Flag(nameof(AlignNextNoteOnPause));
    public double NoteTravelTimeMs { get; init; } = Number(nameof(NoteTravelTimeMs));
    /// <summary>
    /// Notes whose press times are averaged to correct the display offset once per play, so a start that is a little early or late
    /// does not shift every note. The correction never leaves the press window.
    /// </summary>
    public int CalibrationNoteCount { get; init; } = Integer(nameof(CalibrationNoteCount));
    /// <summary>Note height scale (1 = <see cref="Visualization.NoteLayout.BaseThicknessDip"/>).</summary>
    public double NoteThickness { get; init; } = Number(nameof(NoteThickness));
    /// <summary>Note width scale; 1 fills the lane.</summary>
    public double NoteWidth { get; init; } = Number(nameof(NoteWidth));
    /// <summary>Long note bar width scale. Never wider than <see cref="NoteWidth"/>; its length comes from the hold time.</summary>
    public double HoldNoteWidth { get; init; } = Number(nameof(HoldNoteWidth));
    public double GlobalDisplayOffsetMs { get; init; } = Number(nameof(GlobalDisplayOffsetMs));
    // Pattern texts are tile sets; the cycle order comes from the (default) preference, so they are written ascending.
    public string EightKeyPattern { get; init; } = Text(nameof(EightKeyPattern));
    public string SixKeyPattern { get; init; } = Text(nameof(SixKeyPattern));
    public string FourKeyPattern { get; init; } = Text(nameof(FourKeyPattern));
    public string TwoKeyPattern { get; init; } = Text(nameof(TwoKeyPattern));
    /// <summary>
    /// Extra lane placement: a spare lane beside each side of a 2-, 4- or 6-lane view, used only by chord groups that cannot be
    /// placed otherwise. The value is kept while it cannot apply (a maximum of 1 or 8 lanes, or no free tile beside the pattern).
    /// </summary>
    public bool ExtraLanes { get; init; } = Flag(nameof(ExtraLanes));
    /// <summary>
    /// Visual only. When enabled, a note past the judgment line covers the distance to the bottom line in the press window instead
    /// of at travel speed. When disabled, unpressed notes vanish when their window ends.
    /// </summary>
    public bool SlowNotesPastJudgmentLine { get; init; } = Flag(nameof(SlowNotesPastJudgmentLine));
    /// <summary>
    /// Unlocks the experimental card of the settings panel. Experiments only count while it is on.
    /// </summary>
    public bool ExperimentalFeatures { get; init; } = Flag(nameof(ExperimentalFeatures));
    /// <summary>
    /// The singles that follow a chord keep its lanes free while it is still close, so no lane carries a chord and a single in
    /// turn (1-2 3 2 3 becomes 1-2 3 4 3). Chords themselves are placed as always, and notes before a chord are untouched.
    /// </summary>
    public bool ChordSectionLanes { get; init; } = Flag(nameof(ChordSectionLanes));
    /// <summary>Whether the chord lane rule applies; kept so files that carry it still load.</summary>
    [JsonIgnore]
    public bool UseChordSectionLanes => ChordSectionLanes;

    /// <summary>
    /// Experimental: the single note right before a chord keeps off the chord's own lanes, so "3 3-4" becomes "2 3-4".
    /// Chords are placed after the singles, so this is the only rule that looks back at the note before one.
    /// </summary>
    public bool ChordAvoidBeforeLanes { get; init; } = Flag(nameof(ChordAvoidBeforeLanes));
    [JsonIgnore]
    public bool UseChordAvoidBefore => ExperimentalFeatures && ChordAvoidBeforeLanes;

    /// <summary>
    /// Experimental: a run of notes at one interval starts the pattern from its first tile again, so a three-note burst
    /// reads 4-3-2 / 4-3-2 instead of carrying the cycle's phase across the gap.
    /// </summary>
    public bool RestartPatternOnRun { get; init; } = Flag(nameof(RestartPatternOnRun));
    [JsonIgnore]
    public bool UseRestartOnRun => ExperimentalFeatures && RestartPatternOnRun;

    /// <summary>
    /// Experimental (user rule, from ネコーン - 俳徹句): where chords come back with one to three singles between them, the
    /// chord keeps its lanes and the singles take the lanes beside it (1-2 3 1-2 3, 1-2 3 4 1-2 3 4, 1-2 3 4 3), and four
    /// singles that go back and forth on two lanes before a chord (1 2 1 2 3-4) come last.
    /// </summary>
    public bool EnhancedChordRules { get; init; } = Flag(nameof(EnhancedChordRules));
    [JsonIgnore]
    public bool UseEnhancedChordRules => ExperimentalFeatures && EnhancedChordRules;
    /// <summary>
    /// Which lane pair a chord takes first, as screen lanes: "1-2" (the left hand), "3-4", or "None" for the pattern order.
    /// Only a preference: a taken pair still moves the chord elsewhere.
    /// </summary>
    public string ChordLanePreference { get; init; } = Text(nameof(ChordLanePreference));
    /// <summary>
    /// Keeps a chord off the pair that crosses the screen center on four lanes (lanes 2-3); on by default. Older settings files
    /// name it the other way round, ChordCenterPair, which SettingsJson still reads.
    /// </summary>
    public bool NoChordCenterPair { get; init; } = Flag(nameof(NoChordCenterPair));
    /// <summary>The 0-based lane the preferred pair starts on, or -1 without a preference.</summary>
    [JsonIgnore]
    public int PreferredChordLane => ChordLanePreference.Trim() switch { "1-2" => 0, "3-4" => 2, _ => -1 };
    /// <summary>
    /// The 0-based lanes the preferred pairs start on, empty without a preference. "1-2,3-4" (the default) names both, which
    /// changes nothing on four lanes — the two pairs there are the only ones a chord may take anyway — and keeps chords on
    /// the left half from six lanes up.
    /// </summary>
    public IReadOnlyList<int> GetPreferredChordLanes() => Pairs(ChordLanePreference);

    /// <summary>The 0-based lanes the preferred trill pairs start on; the same reading as the chord preference.</summary>
    public IReadOnlyList<int> GetPreferredTrillLanes() => Pairs(TrillLanePreference);

    private static IReadOnlyList<int> Pairs(string preference) => preference.Trim() switch
    {
        "1-2" => [0], "3-4" => [2], "1-2,3-4" => [0, 2], _ => []
    };
    /// <summary>Whether a chord may cross the screen center on four lanes.</summary>
    [JsonIgnore]
    public bool UseChordCenterPair => !NoChordCenterPair;
    /// <summary>When disabled (default), every mode cycles its tiles in the default order and the order texts are ignored (but kept).</summary>
    public bool EnableTilePreference { get; init; } = Flag(nameof(EnableTilePreference));
    /// <summary>
    /// Tile preference: the order of a mode's pattern tiles as ranks, 1 being the lowest tile number.
    /// The pattern text chooses which tiles are used; this chooses their cycle order when enabled.
    /// </summary>
    public string TwoKeyOrder { get; init; } = Text(nameof(TwoKeyOrder));
    public string FourKeyOrder { get; init; } = Text(nameof(FourKeyOrder));
    public string SixKeyOrder { get; init; } = Text(nameof(SixKeyOrder));
    public string EightKeyOrder { get; init; } = Text(nameof(EightKeyOrder));
    /// <summary>The single tile of 1K, which is also where the first cycle starts on Reset.</summary>
    public int InitialTile { get; init; } = Integer(nameof(InitialTile));
    /// <summary>When false, playback never advances the selected checkpoint by itself.</summary>
    public bool AutoCheckpointAdvance { get; init; } = Flag(nameof(AutoCheckpointAdvance));
    /// <summary>
    /// Turns <see cref="AutoCheckpointAdvance"/> back on whenever a song is loaded (user rule), so a run that switched it
    /// off does not carry over to the next song. On by default.
    /// </summary>
    public bool AutoCheckpointOnLoad { get; init; } = Flag(nameof(AutoCheckpointOnLoad));
    /// <summary>Space-separated key tokens for logical tiles 1..8. The start key may double as a lane key.</summary>
    public string LaneKeys { get; init; } = Text(nameof(LaneKeys));
    /// <summary>Distance in DIP between the judgment line and the bottom line where notes disappear.</summary>
    public double JudgmentLineHeightDip { get; init; } = Number(nameof(JudgmentLineHeightDip));
    /// <summary>Visual-only ± window for estimating which note a lane key press hit.</summary>
    public double KeyPressWindowMs { get; init; } = Number(nameof(KeyPressWindowMs));
    /// <summary>Lane gradient, key highlight and hit glow while a lane key is pressed.</summary>
    public bool ShowInputEffect { get; init; } = Flag(nameof(ShowInputEffect));
    /// <summary>Red lane gradient when a note's press window ends unpressed.</summary>
    public bool ShowMissEffect { get; init; } = Flag(nameof(ShowMissEffect));
    /// <summary>
    /// A timing bar for magic circle sections: runs of at least <see cref="MagicCircleNoteCount"/> notes at the same interval
    /// (chords count as one note), shown <see cref="MagicCircleLeadMs"/> before the run. The settings panel sets it from <see cref="JudgmentBarMode"/>.
    /// </summary>
    public bool MagicCircleJudgmentLine { get; init; } = Flag(nameof(MagicCircleJudgmentLine));

    /// <summary>When the judgment bar is drawn: Off, MagicCircle (only in a magic circle) or Always.</summary>
    public string JudgmentBarMode { get; init; } = Text(nameof(JudgmentBarMode));

    /// <summary>
    /// How wide the bar's window is, named after the game's own difficulties. The game keeps a floor in seconds for a
    /// counted hit and these are its numbers (GCS.HITMARGIN_MINIMUM_SECONDS_*): Lenient 91ms, Normal 65ms, Strict 40ms.
    /// </summary>
    public string JudgmentBarDifficulty { get; init; } = Text(nameof(JudgmentBarDifficulty));

    /// <summary>How many recent presses the average arrow under the bar is made of.</summary>
    public int JudgmentAverageCount { get; init; } = Integer(nameof(JudgmentAverageCount));

    public bool ShowJudgmentAverage { get; init; } = Flag(nameof(ShowJudgmentAverage));

    /// <summary>The bar's half window in milliseconds, from the difficulty (the game's counted floor).</summary>
    [JsonIgnore]
    public double JudgmentWindowMs => JudgmentBarDifficulty.Trim() switch { "Lenient" => 91, "Strict" => 40, _ => 65 };
    public int MagicCircleNoteCount { get; init; } = Integer(nameof(MagicCircleNoteCount));
    /// <summary>Only runs faster than this count as a magic circle: an interval above it ends the run.</summary>
    public double MagicCircleMaxIntervalMs { get; init; } = Number(nameof(MagicCircleMaxIntervalMs));
    /// <summary>
    /// Whether chords join a run of singles (user rule). Off, a run ends where chords and singles meet, while a run of chords
    /// alone still counts, each chord being one note.
    /// </summary>
    public bool MagicCircleChords { get; init; } = Flag(nameof(MagicCircleChords));
    /// <summary>How long the bar takes to appear and to disappear, and how early it appears before the run.</summary>
    public double MagicCircleFadeInMs { get; init; } = Number(nameof(MagicCircleFadeInMs));
    public double MagicCircleLeadMs { get; init; } = Number(nameof(MagicCircleLeadMs));
    public double MagicCircleFadeOutMs { get; init; } = Number(nameof(MagicCircleFadeOutMs));
    /// <summary>Holds the tile count at <see cref="MagicCircleTileCount"/> while a magic circle runs. A note rule, not a visual.</summary>
    public bool MagicCircleFixedTiles { get; init; } = Flag(nameof(MagicCircleFixedTiles));
    public int MagicCircleTileCount { get; init; } = Integer(nameof(MagicCircleTileCount));
    /// <summary>Whether the tile count is actually held: the bar and the switch must both be on.</summary>
    [JsonIgnore]
    public bool UseMagicCircleTiles => MagicCircleJudgmentLine && MagicCircleFixedTiles;
    /// <summary>The count actually held, kept inside the maximum and minimum so a locked value still works.</summary>
    [JsonIgnore]
    public int MagicCircleTiles => Math.Clamp(MagicCircleTileCount, MinimumTileCount, MaximumTileCount);
    public string NoteColor { get; init; } = Text(nameof(NoteColor));
    public string ChordColor { get; init; } = Text(nameof(ChordColor));
    /// <summary>Notes that sit on a checkpoint tile; white by default so they stand out from the other two.</summary>
    public string CheckpointColor { get; init; } = Text(nameof(CheckpointColor));
    /// <summary>
    /// The first note of a play (the chord, if it is one) and the song's last note. They outrank every other note color,
    /// the checkpoint color included, since a play from a checkpoint begins on a checkpoint note (user rule).
    /// </summary>
    public string StartNoteColor { get; init; } = Text(nameof(StartNoteColor));
    public string EndNoteColor { get; init; } = Text(nameof(EndNoteColor));
    /// <summary>The judgment line and its hit glow.</summary>
    public string JudgmentLineColor { get; init; } = Text(nameof(JudgmentLineColor));
    /// <summary>How transparent a note looks once hit, in percent: 0 keeps the note color, 100 fades it into the field.</summary>
    public double PressedNoteTransparencyPercent { get; init; } = Number(nameof(PressedNoteTransparencyPercent));
    public string MissEffectColor { get; init; } = Text(nameof(MissEffectColor));
    /// <summary>Empty to follow the theme's lane colors; otherwise eight colors for screen lanes 1..8, left to right.</summary>
    public string LaneBackgroundColors { get; init; } = Text(nameof(LaneBackgroundColors));
    /// <summary>One press gradient color for every lane.</summary>
    public string LaneEffectColor { get; init; } = Text(nameof(LaneEffectColor));

    public const int LaneKeyCount = 8;
    public const double MaximumJudgmentLineHeightDip = 400;
    public const double MaximumKeyPressWindowMs = 1000;
    /// <summary>Range of the note height, note width and long note width scales.</summary>
    public const double MinimumNoteScale = 0.2;
    public const double MaximumNoteScale = 3;
    public const int MaximumCalibrationNoteCount = 20;
    public const int MaximumDownshiftNoteCount = 8;
    /// <summary>Shorter runs are too easy to hit by chance to be a magic circle.</summary>
    public const int MinimumMagicCircleNoteCount = 16;
    /// <summary>Intervals within this many ms of each other count as the same interval.</summary>
    public const double MagicCircleIntervalToleranceMs = 5;

    public static bool IsMode(int tiles) => tiles is 1 or 2 or 4 or 6 or 8;

    public IReadOnlyList<string> GetLaneKeyTokens() =>
        (LaneKeys ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The threshold below which intervals use <paramref name="upperMode"/> (2, 4, 6 or 8) instead of the next smaller mode.
    /// </summary>
    public double BoundaryMs(int upperMode) => upperMode switch
    {
        8 => EightKeyBoundaryMs,
        6 => SixKeyBoundaryMs,
        4 => FourKeyBoundaryMs,
        2 => TwoKeyBoundaryMs,
        _ => throw new ArgumentOutOfRangeException(nameof(upperMode))
    };

    /// <summary>A threshold between two modes is used only when dynamic timing can choose both modes.</summary>
    public bool IsThresholdEnabled(int upperMode) => DynamicTiming && upperMode is 2 or 4 or 6 or 8
        && MaximumTileCount >= upperMode && MinimumTileCount < upperMode;

    /// <summary>Whether extra lane placement can apply at this maximum (1 and 8 lanes never use it). Derived, so not part of the JSON.</summary>
    [JsonIgnore]
    public bool CanUseExtraLanes => MaximumTileCount is 2 or 4 or 6;

    /// <summary>True when the lane backgrounds follow the theme (the setting is empty).</summary>
    [JsonIgnore]
    public bool FollowsThemeLaneBackgrounds => string.IsNullOrWhiteSpace(LaneBackgroundColors);

    public bool HasSameNoteRulesAs(VisualizerSettings other) =>
        MaximumTileCount == other.MaximumTileCount && MinimumTileCount == other.MinimumTileCount && DynamicTiming == other.DynamicTiming
        && EightKeyBoundaryMs == other.EightKeyBoundaryMs && SixKeyBoundaryMs == other.SixKeyBoundaryMs
        && FourKeyBoundaryMs == other.FourKeyBoundaryMs && TwoKeyBoundaryMs == other.TwoKeyBoundaryMs && InitialTile == other.InitialTile
        && SimultaneousWindowMs == other.SimultaneousWindowMs && TrillWindowMs == other.TrillWindowMs
        && NoTrillCenterPair == other.NoTrillCenterPair && TrillLanePreference.Trim() == other.TrillLanePreference.Trim()
        && TrillAsOneNote == other.TrillAsOneNote
        && ImmediateDownshift == other.ImmediateDownshift
        && (!ImmediateDownshift || DownshiftNoteCount == other.DownshiftNoteCount)
        && StartDelayMs == other.StartDelayMs && PlaybackSpeedPercent == other.PlaybackSpeedPercent
        && SortTiles(EightKeyPattern) == SortTiles(other.EightKeyPattern) && SortTiles(SixKeyPattern) == SortTiles(other.SixKeyPattern)
        && SortTiles(FourKeyPattern) == SortTiles(other.FourKeyPattern) && SortTiles(TwoKeyPattern) == SortTiles(other.TwoKeyPattern)
        && (ExtraLanes && CanUseExtraLanes) == (other.ExtraLanes && other.CanUseExtraLanes)
        && UseChordSectionLanes == other.UseChordSectionLanes && ChordLanePreference.Trim() == other.ChordLanePreference.Trim()
        && UseChordCenterPair == other.UseChordCenterPair && UseMagicCircleTiles == other.UseMagicCircleTiles
        && (!UseMagicCircleTiles || (MagicCircleTiles == other.MagicCircleTiles
            && MagicCircleNoteCount == other.MagicCircleNoteCount && MagicCircleMaxIntervalMs == other.MagicCircleMaxIntervalMs
            && MagicCircleChords == other.MagicCircleChords))
        && NextIntervalTiles == other.NextIntervalTiles && BothIntervalTiles == other.BothIntervalTiles
        && UseChordAvoidBefore == other.UseChordAvoidBefore && UseRestartOnRun == other.UseRestartOnRun
        && UseEnhancedChordRules == other.UseEnhancedChordRules
        && EnableTilePreference == other.EnableTilePreference
        && (!EnableTilePreference || (TwoKeyOrder == other.TwoKeyOrder && FourKeyOrder == other.FourKeyOrder
            && SixKeyOrder == other.SixKeyOrder && EightKeyOrder == other.EightKeyOrder));

    /// <summary>This snapshot with the visual-only settings (effects and colors) of <paramref name="source"/>.</summary>
    public VisualizerSettings WithVisuals(VisualizerSettings source) => this with
    {
        ShowInputEffect = source.ShowInputEffect,
        ShowMissEffect = source.ShowMissEffect,
        NoteColor = source.NoteColor,
        ChordColor = source.ChordColor,
        CheckpointColor = source.CheckpointColor,
        StartNoteColor = source.StartNoteColor,
        EndNoteColor = source.EndNoteColor,
        JudgmentLineColor = source.JudgmentLineColor,
        MissEffectColor = source.MissEffectColor,
        PressedNoteTransparencyPercent = source.PressedNoteTransparencyPercent,
        LaneBackgroundColors = source.LaneBackgroundColors,
        LaneEffectColor = source.LaneEffectColor,
        NoteThickness = source.NoteThickness,
        NoteWidth = source.NoteWidth,
        HoldNoteWidth = source.HoldNoteWidth
    };

    /// <summary>The mode's pattern tiles in cycle order. 1K is the initial tile; other modes order their tiles by the (default) preference.</summary>
    public IReadOnlyList<int> GetPattern(int mode)
    {
        if (mode == 1) return [InitialTile];
        var tiles = ParsePattern(PatternText(mode), mode);
        var ranks = ParseOrder(EnableTilePreference ? OrderText(mode) : DefaultOrder(mode), mode);
        var sorted = tiles.Order().ToArray();
        return ranks.Select(rank => sorted[rank - 1]).ToArray();
    }

    /// <summary>The tiles of the maximum-mode pattern, ascending. Patterns and chords use only these lanes.</summary>
    public IReadOnlyList<int> GetVisibleTiles() => GetPattern(MaximumTileCount).Order().ToArray();

    /// <summary>
    /// Spare lanes when extra lane placement applies: the tile just left and just right of the visible tiles, or the two nearest
    /// on the other side when the pattern already reaches tile 1 or 8. Empty when unused.
    /// </summary>
    public IReadOnlyList<int> GetExtraTiles()
    {
        if (!ExtraLanes || !CanUseExtraLanes) return [];
        var visible = GetVisibleTiles();
        int low = visible[0], high = visible[^1];
        if (low > 1 && high < LaneKeyCount) return [low - 1, high + 1];
        var side = low > 1 ? Enumerable.Range(1, low - 1).Reverse() : Enumerable.Range(high + 1, LaneKeyCount - high);
        return side.Take(2).Order().ToArray();
    }

    /// <summary>
    /// How many lanes the screen shows: the display setting when it names one, otherwise the maximum tile count. It is
    /// never below the maximum, so every lane the rules use is on screen.
    /// </summary>
    [JsonIgnore]
    public int ShownLaneCount => DisplayLaneCount > 0 ? Math.Max(DisplayLaneCount, MaximumTileCount) : MaximumTileCount;

    /// <summary>The lanes the screen shows, ascending, whether the rules use them or not.</summary>
    public IReadOnlyList<int> GetShownTiles() => GetPattern(ShownLaneCount).Union(GetVisibleTiles()).Order().ToArray();

    /// <summary>Every lane that may be on screen, ascending: the shown lanes plus any spare lanes.</summary>
    public IReadOnlyList<int> GetDisplayTiles() => GetShownTiles().Concat(GetExtraTiles()).Distinct().Order().ToArray();

    /// <summary>
    /// The lanes to draw for a set of placed notes: the shown lanes, plus only the spare lanes a note really sits on.
    /// Extra lane placement adds a lane when a chord needs one, so switching it on must not widen the screen by itself.
    /// </summary>
    public IReadOnlyList<int> GetDisplayTiles(IEnumerable<int> usedTiles)
    {
        ArgumentNullException.ThrowIfNull(usedTiles);
        var spare = GetExtraTiles();
        if (spare.Count == 0) return GetShownTiles();
        var used = usedTiles.ToHashSet();
        return GetShownTiles().Concat(spare.Where(used.Contains)).Distinct().Order().ToArray();
    }

    /// <summary>ARGB colors of screen lanes 1..8 (valid settings only); empty when the lanes follow the theme.</summary>
    public uint[] GetLaneBackgroundColors() => FollowsThemeLaneBackgrounds ? [] : ParseColorList(LaneBackgroundColors);
    /// <summary>The one press gradient color every lane uses.</summary>
    public uint GetLaneEffectColor() => ParseColor(LaneEffectColor);
    /// <summary>How much of the note color a hit note keeps (1 = unchanged), from the transparency setting.</summary>
    [JsonIgnore]
    public double PressedNoteStrength => Math.Clamp(1 - PressedNoteTransparencyPercent / 100, 0, 1);

    public static uint ParseColor(string text) =>
        ColorText.TryParse(text, out var argb) ? argb : throw new ArgumentException($"Invalid color: {text}", nameof(text));

    public IReadOnlyList<string> Validate() => ValidateFields().Select(error => error.Message).Distinct().ToArray();

    /// <summary>Every problem with the setting it belongs to; one reason can name several settings.</summary>
    public IReadOnlyList<SettingsError> ValidateFields()
    {
        var errors = new List<SettingsError>();
        void Add(string message, params string[] fields)
        {
            foreach (var field in fields) errors.Add(new(field, message));
        }
        if (!double.IsFinite(StartDelayMs) || StartDelayMs < 0) Add(L("Error.StartDelay"), nameof(StartDelayMs));
        if (!double.IsFinite(PlaybackSpeedPercent) || PlaybackSpeedPercent is < 1 or > 1000) Add(L("Error.PlaybackSpeed"), nameof(PlaybackSpeedPercent));
        // 40ms is the widest chord the note editor may merge, so the setting stops there too.
        if (!double.IsFinite(SimultaneousWindowMs) || SimultaneousWindowMs is < 0 or > MaximumChordWindowMs)
            Add(L("Error.SimultaneousWindow"), nameof(SimultaneousWindowMs));
        if (!double.IsFinite(TrillWindowMs) || TrillWindowMs is < 0 or > MaximumTrillWindowMs)
            Add(L("Error.TrillWindow"), nameof(TrillWindowMs));
        if (TrillLanePreference.Trim() is not ("None" or "1-2" or "3-4" or "1-2,3-4")) Add(L("Error.TrillLane"), nameof(TrillLanePreference));
        if (!Languages.Exists(Language))
            Add(L("Error.Language", string.Join(", ", Languages.All.Select(language => language.Id))), nameof(Language));
        if (string.IsNullOrWhiteSpace(Theme) || Theme.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            Add(L("Error.Theme"), nameof(Theme));
        if (string.IsNullOrWhiteSpace(MapRootDirectory) || !Path.IsPathFullyQualified(MapRootDirectory)) Add(L("Error.MapFolder"), nameof(MapRootDirectory));
        if (SongListFields.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(field => !SongListFieldNames.Contains(field, StringComparer.OrdinalIgnoreCase)))
            Add(L("Error.SongListFields", string.Join(", ", SongListFieldNames)), nameof(SongListFields));
        if (!Enum.TryParse<SongGroupField>(SongGroup, ignoreCase: true, out _))
            Add(L("Error.SongGroup", string.Join(", ", Enum.GetNames<SongGroupField>())), nameof(SongGroup));
        if (!Enum.TryParse<SongSortField>(SongSortKey, ignoreCase: true, out _))
            Add(L("Error.SongSort", string.Join(", ", Enum.GetNames<SongSortField>())), nameof(SongSortKey));
        // Empty keeps the default store; a given path must be absolute so it never depends on the working directory.
        var dataRoot = DataRootDirectory.Trim();
        if (dataRoot.Length > 0 && !Path.IsPathFullyQualified(dataRoot)) Add(L("Error.DataFolder"), nameof(DataRootDirectory));
        // The store must stay clear of the map folder, which the app only ever reads.
        else if (dataRoot.Length > 0 && Path.IsPathFullyQualified(MapRootDirectory) && Overlaps(dataRoot, MapRootDirectory))
            Add(L("Error.DataFolderMaps"), nameof(DataRootDirectory));
        var validMaximum = IsMode(MaximumTileCount);
        if (!validMaximum) Add(L("Error.MaximumTiles"), nameof(MaximumTileCount));
        if (!IsMode(MinimumTileCount)) Add(L("Error.MinimumTiles"), nameof(MinimumTileCount));
        else if (validMaximum && MinimumTileCount > MaximumTileCount) Add(L("Error.MinimumAboveMaximum"), nameof(MinimumTileCount), nameof(MaximumTileCount));
        if (InitialTile is < 1 or > 8) Add(L("Error.InitialTile"), nameof(InitialTile));
        if (!Positive(NoteTravelTimeMs)) Add(L("Error.TravelTime"), nameof(NoteTravelTimeMs));
        var scaleRange = L("Error.Scale", MinimumNoteScale.ToString("0.##", CultureInfo.InvariantCulture),
            MaximumNoteScale.ToString("0.##", CultureInfo.InvariantCulture));
        foreach (var (field, scale) in new[]
        {
            (nameof(NoteThickness), NoteThickness), (nameof(NoteWidth), NoteWidth), (nameof(HoldNoteWidth), HoldNoteWidth)
        })
        {
            if (!double.IsFinite(scale) || scale < MinimumNoteScale || scale > MaximumNoteScale) Add(scaleRange, field);
        }
        // A long note bar is drawn inside the note width, so it is never the wider of the two.
        if (double.IsFinite(HoldNoteWidth) && double.IsFinite(NoteWidth) && HoldNoteWidth > NoteWidth)
            Add(L("Error.HoldWidth"), nameof(HoldNoteWidth), nameof(NoteWidth));
        if (!double.IsFinite(PressedNoteTransparencyPercent) || PressedNoteTransparencyPercent is < 0 or > 100)
            Add(L("Error.PressedTransparency"), nameof(PressedNoteTransparencyPercent));
        if (CalibrationNoteCount is < 1 || CalibrationNoteCount > MaximumCalibrationNoteCount)
            Add(L("Error.CalibrationNotes", MaximumCalibrationNoteCount), nameof(CalibrationNoteCount));
        if (DownshiftNoteCount is < 1 || DownshiftNoteCount > MaximumDownshiftNoteCount)
            Add(L("Error.DownshiftNotes", MaximumDownshiftNoteCount), nameof(DownshiftNoteCount));
        if (MagicCircleNoteCount < MinimumMagicCircleNoteCount)
            Add(L("Error.MagicCircleNotes", MinimumMagicCircleNoteCount), nameof(MagicCircleNoteCount));
        foreach (var (field, value) in new[] { (nameof(MagicCircleLeadMs), MagicCircleLeadMs), (nameof(MagicCircleFadeInMs), MagicCircleFadeInMs),
                     (nameof(MagicCircleFadeOutMs), MagicCircleFadeOutMs) })
            if (!double.IsFinite(value) || value < 0) Add(L("Error.MagicCircleLead"), field);
        if (!Positive(MagicCircleMaxIntervalMs)) Add(L("Error.MagicCircleInterval"), nameof(MagicCircleMaxIntervalMs));
        // The held count is one of the modes; a value above the maximum is kept and simply clamped while it applies.
        if (!Modes.Contains(MagicCircleTileCount)) Add(L("Error.MagicCircleTiles", MaximumTileCount), nameof(MagicCircleTileCount));
        if (ChordLanePreference.Trim() is not ("None" or "1-2" or "3-4" or "1-2,3-4")) Add(L("Error.ChordLane"), nameof(ChordLanePreference));
        if (JudgmentBarMode.Trim() is not ("Off" or "MagicCircle" or "Always")) Add(L("Error.JudgmentBarMode"), nameof(JudgmentBarMode));
        if (JudgmentBarDifficulty.Trim() is not ("Lenient" or "Normal" or "Strict")) Add(L("Error.JudgmentDifficulty"), nameof(JudgmentBarDifficulty));
        if (JudgmentAverageCount is < 1 or > 50) Add(L("Error.JudgmentAverage"), nameof(JudgmentAverageCount));
        if (!double.IsFinite(GlobalDisplayOffsetMs)) Add(L("Error.DisplayOffset"), nameof(GlobalDisplayOffsetMs));
        if (string.IsNullOrWhiteSpace(AdofaiProcessName) || AdofaiProcessName.IndexOfAny(['/', '\\', ':']) >= 0) Add(L("Error.ProcessName"), nameof(AdofaiProcessName));

        foreach (var (field, text) in new[]
        {
            (nameof(NoteColor), NoteColor), (nameof(ChordColor), ChordColor), (nameof(JudgmentLineColor), JudgmentLineColor),
            (nameof(MissEffectColor), MissEffectColor), (nameof(LaneEffectColor), LaneEffectColor),
            (nameof(CheckpointColor), CheckpointColor), (nameof(StartNoteColor), StartNoteColor), (nameof(EndNoteColor), EndNoteColor)
        })
        {
            if (!ColorText.TryParse(text, out _)) Add(L("Error.Color"), field);
        }
        // Lane backgrounds may follow the theme (empty); otherwise they are eight colors.
        if (!string.IsNullOrWhiteSpace(LaneBackgroundColors))
        {
            var tokens = ColorText.SplitList(LaneBackgroundColors);
            if (tokens.Count != LaneKeyCount || tokens.Any(token => !ColorText.TryParse(token, out _)))
                Add(L("Error.LaneColors"), nameof(LaneBackgroundColors));
        }

        // The screen may show more lanes than the rules use, never fewer (user rule).
        if (DisplayLaneCount != 0 && (!Modes.Contains(DisplayLaneCount) || DisplayLaneCount < MaximumTileCount))
            Add(L("Error.DisplayLanes"), nameof(DisplayLaneCount));

        var boundaries = new List<string>();
        foreach (var upper in new[] { 8, 6, 4, 2 })
            if (!Positive(BoundaryMs(upper))) boundaries.Add(BoundaryField(upper));
        // Only thresholds between modes that can both be chosen must be ordered (8K <= 6K <= 4K <= 2K). Two equal
        // thresholds are allowed and leave the faster mode unused (user request), so only a threshold above the next is wrong.
        foreach (var (upper, next) in new[] { (8, 6), (6, 4), (4, 2) })
            if (MaximumTileCount >= upper && MinimumTileCount < next && BoundaryMs(upper) > BoundaryMs(next))
                boundaries.AddRange([BoundaryField(upper), BoundaryField(next)]);
        if (boundaries.Count > 0) Add(L("Error.Thresholds"), boundaries.Distinct().ToArray());

        var controls = new List<(string Field, string? Key)>
        {
            (nameof(ResetKey), ResetKey), (nameof(StartTriggerKey), StartTriggerKey), (nameof(VisualizerToggleKey), VisualizerToggleKey)
        };
        foreach (var (field, key) in new[] { (nameof(PreviousCheckpointKey), PreviousCheckpointKey), (nameof(NextCheckpointKey), NextCheckpointKey),
                     (nameof(SettingsHotkey), SettingsHotkey) })
            if (key is not null) controls.Add((field, key));
        var blank = controls.Where(c => string.IsNullOrWhiteSpace(c.Key)).Select(c => c.Field).ToArray();
        if (blank.Length > 0) Add(L("Error.ControlKeysBlank"), blank);
        else
        {
            // Escape is reserved for pausing with the game, so it counts as a taken key without a field of its own.
            var duplicated = controls.Append((Field: "", Key: (string?)EscapeKey)).GroupBy(c => c.Key!, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).SelectMany(group => group).Select(c => c.Field).Where(field => field.Length > 0).Distinct().ToArray();
            if (duplicated.Length > 0) Add(L("Error.ControlKeysDuplicate"), duplicated);
        }
        if (!double.IsFinite(JudgmentLineHeightDip) || JudgmentLineHeightDip is < 0 or > MaximumJudgmentLineHeightDip)
            Add(L("Error.JudgmentLineHeight", MaximumJudgmentLineHeightDip.ToString("0", CultureInfo.InvariantCulture)), nameof(JudgmentLineHeightDip));
        if (!Positive(KeyPressWindowMs) || KeyPressWindowMs > MaximumKeyPressWindowMs)
            Add(L("Error.PressWindow", MaximumKeyPressWindowMs.ToString("0", CultureInfo.InvariantCulture)), nameof(KeyPressWindowMs));
        var laneKeys = GetLaneKeyTokens();
        if (laneKeys.Count != LaneKeyCount || laneKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != LaneKeyCount)
            Add(L("Error.LaneKeysCount"), nameof(LaneKeys));
        else if (blank.Length == 0)
        {
            // The start key is allowed to double as a lane key: the first hit also starts playback.
            var reserved = controls.Where(c => c.Field != nameof(StartTriggerKey)).Append((Field: "", Key: (string?)EscapeKey)).ToArray();
            if (reserved.FirstOrDefault(c => laneKeys.Contains(c.Key!, StringComparer.OrdinalIgnoreCase)) is { Key: { } conflict } taken)
                Add(L("Error.LaneKeyConflict", conflict), taken.Field.Length > 0 ? new[] { nameof(LaneKeys), taken.Field } : new[] { nameof(LaneKeys) });
        }
        foreach (var mode in new[] { 2, 4, 6, 8 })
        {
            try { _ = ParsePattern(PatternText(mode), mode); }
            catch (ArgumentException error) { Add(error.Message, PatternField(mode)); }
            if (!EnableTilePreference) continue; // The order text is ignored, so it is not checked.
            try { _ = ParseOrder(OrderText(mode), mode); }
            catch (ArgumentException error) { Add(error.Message, OrderField(mode)); }
        }
        if (errors.Count == 0 && DynamicTiming)
        {
            var visible = GetVisibleTiles();
            var hidden = new[] { 1, 2, 4, 6 }.Where(mode => mode < MaximumTileCount && mode >= MinimumTileCount)
                .Where(mode => GetPattern(mode).Except(visible).Any()).Select(PatternField).ToArray();
            if (hidden.Length > 0) Add(L("Error.HiddenTiles"), hidden);
        }
        return errors.AsReadOnly();
    }

    public void EnsureValid()
    {
        var errors = Validate();
        if (errors.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
    }

    public static string DefaultOrder(int mode) => string.Join(" ", Enumerable.Range(1, mode).Reverse());

    /// <summary>A pattern text with its tile numbers ascending and single-spaced; text that is not all numbers is only trimmed.</summary>
    public static string SortTiles(string? text)
    {
        var tokens = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var tiles = new List<int>();
        foreach (var token in tokens)
        {
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var tile)) return (text ?? "").Trim();
            tiles.Add(tile);
        }
        return string.Join(" ", tiles.Order());
    }

    /// <summary>The song list details that are on, as written in <see cref="SongListFieldNames"/>.</summary>
    public IReadOnlyList<string> GetSongListFields()
    {
        var chosen = SongListFields.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return SongListFieldNames.Where(field => chosen.Contains(field, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;

    /// <summary>Whether two folders are the same or one contains the other. Text only; nothing on disk is touched.</summary>
    private static bool Overlaps(string left, string right)
    {
        static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var (a, b) = (Full(left), Full(right));
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private string L(string key, params object?[] args) => Languages.Format(Language, key, args);

    private static uint[] ParseColorList(string text) => ColorText.SplitList(text).Select(ParseColor).ToArray();

    private static string BoundaryField(int upperMode) => upperMode switch
    {
        8 => nameof(EightKeyBoundaryMs),
        6 => nameof(SixKeyBoundaryMs),
        4 => nameof(FourKeyBoundaryMs),
        _ => nameof(TwoKeyBoundaryMs)
    };

    private static string PatternField(int mode) => mode switch
    {
        8 => nameof(EightKeyPattern),
        6 => nameof(SixKeyPattern),
        4 => nameof(FourKeyPattern),
        2 => nameof(TwoKeyPattern),
        _ => nameof(InitialTile)
    };

    private static string OrderField(int mode) => mode switch
    {
        8 => nameof(EightKeyOrder),
        6 => nameof(SixKeyOrder),
        4 => nameof(FourKeyOrder),
        _ => nameof(TwoKeyOrder)
    };

    private string PatternText(int mode) => mode switch
    {
        8 => EightKeyPattern,
        6 => SixKeyPattern,
        4 => FourKeyPattern,
        2 => TwoKeyPattern,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private string OrderText(int mode) => mode switch { 8 => EightKeyOrder, 6 => SixKeyOrder, 4 => FourKeyOrder, _ => TwoKeyOrder };

    private IReadOnlyList<int> ParsePattern(string text, int mode)
    {
        var tokens = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var tiles = new List<int>();
        foreach (var token in tokens)
        {
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var tile) || tile is < 1 or > 8)
                throw new ArgumentException(L("Error.PatternTiles", mode));
            tiles.Add(tile);
        }
        if (tiles.Count != mode || tiles.Distinct().Count() != mode) throw new ArgumentException(L("Error.PatternCount", mode));
        return tiles.AsReadOnly();
    }

    private int[] ParseOrder(string text, int mode)
    {
        var tokens = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var ranks = new List<int>();
        foreach (var token in tokens)
        {
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var rank) || rank < 1 || rank > mode)
            {
                ranks.Clear();
                break;
            }
            ranks.Add(rank);
        }
        if (ranks.Count != mode || ranks.Distinct().Count() != mode) throw new ArgumentException(L("Error.Order", mode, DefaultOrder(mode)));
        return ranks.ToArray();
    }
}
