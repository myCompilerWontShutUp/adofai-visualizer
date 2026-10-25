using System.Globalization;
using Visualizer.App.Input;
using Visualizer.App.Themes;
using Visualizer.Core.Localization;
using Visualizer.Core.Settings;
using static Visualizer.App.Commands.SettingKind;
using static Visualizer.Core.Localization.ConsoleText;

namespace Visualizer.App.Commands;

/// <summary>What a setting holds; it decides how set reads a value and what property lists for it.</summary>
public enum SettingKind { Toggle, Choice, Number, Whole, Text, Key, OptionalKey, Color, Tiles, Folder, DataFolder }

/// <summary>
/// One setting as the console names it, <c>section.key</c>. <paramref name="Lane"/> picks one of
/// the eight lane keys out of <see cref="VisualizerSettings.LaneKeys"/>; it is -1 for every other setting.
/// </summary>
public sealed record SettingPath(string Section, string Key, string Property, SettingKind Kind, string Unit = "", int Lane = -1)
{
    public string Name => Section + "." + Key;
}

/// <summary>
/// The settings the settings window shows, under short names for get, set and property. Names use a hyphen for a space and
/// never repeat across sections. The experimental section opens only while advanced.experimental is true; it has no settings
/// for now.
/// </summary>
public static class SettingPaths
{
    public const string ExperimentalSection = "experimental";

    /// <summary>The sections in the order of the settings window.</summary>
    private static readonly string[] SectionNames =
        ["general", "songs", "keys", "tiles", "timing", "magic", "display", "colors", "advanced", ExperimentalSection];

    /// <summary>Sections in the order of the settings window, each with the line property shows for it, in the console's language.</summary>
    public static IReadOnlyList<(string Name, string About)> Sections => SectionNames.Select(name => (name, T("Section." + name))).ToArray();

    public static IReadOnlyList<SettingPath> All { get; } = Build();

    /// <summary>Settings without a row in the settings window, which the console leaves out as well.</summary>
    public static IReadOnlyList<string> NotInWindow { get; } =
        [nameof(VisualizerSettings.SongListFields), nameof(VisualizerSettings.LaneBackgroundColors),
         // The bar has its own choice now (judgment.bar); this one only stays so an older settings file still reads.
         nameof(VisualizerSettings.MagicCircleJudgmentLine)];

    private static SettingPath[] Build()
    {
        var list = new List<SettingPath>
        {
            new("general", "language", nameof(VisualizerSettings.Language), Choice),
            new("general", "theme", nameof(VisualizerSettings.Theme), Choice),
            new("general", "information-buttons", nameof(VisualizerSettings.ShowGuideButtons), Toggle),
            new("general", "song-info", nameof(VisualizerSettings.ShowSongInfo), Toggle),
            new("general", "game-only", nameof(VisualizerSettings.OnlyReactWhenAdofaiForeground), Toggle),
            new("general", "autosave", nameof(VisualizerSettings.AutoSaveSettings), Toggle),
            new("songs", "sort", nameof(VisualizerSettings.SongSortKey), Choice),
            new("songs", "high-first", nameof(VisualizerSettings.SongSortHighFirst), Toggle),
            new("songs", "group", nameof(VisualizerSettings.SongGroup), Choice),
            new("keys", "reset", nameof(VisualizerSettings.ResetKey), Key),
            new("keys", "start", nameof(VisualizerSettings.StartTriggerKey), Key),
            new("keys", "toggle", nameof(VisualizerSettings.VisualizerToggleKey), Key),
            new("checkpoint", "auto", nameof(VisualizerSettings.AutoCheckpointAdvance), Toggle),
            new("design", "display-lanes", nameof(VisualizerSettings.DisplayLaneCount), Choice),
            new("checkpoint", "auto-on-load", nameof(VisualizerSettings.AutoCheckpointOnLoad), Toggle),
            new("keys", "previous", nameof(VisualizerSettings.PreviousCheckpointKey), OptionalKey),
            new("keys", "next", nameof(VisualizerSettings.NextCheckpointKey), OptionalKey),
            new("keys", "settings", nameof(VisualizerSettings.SettingsHotkey), OptionalKey)
        };
        for (var lane = 1; lane <= VisualizerSettings.LaneKeyCount; lane++)
            list.Add(new("keys", $"lane-{lane}", nameof(VisualizerSettings.LaneKeys), Key, Lane: lane - 1));
        list.AddRange(
        [
            new("tiles", "max", nameof(VisualizerSettings.MaximumTileCount), Choice),
            new("tiles", "min", nameof(VisualizerSettings.MinimumTileCount), Choice),
            new("tiles", "dynamic", nameof(VisualizerSettings.DynamicTiming), Toggle),
            new("tiles", "8k-6k", nameof(VisualizerSettings.EightKeyBoundaryMs), Number, "ms"),
            new("tiles", "6k-4k", nameof(VisualizerSettings.SixKeyBoundaryMs), Number, "ms"),
            new("tiles", "4k-2k", nameof(VisualizerSettings.FourKeyBoundaryMs), Number, "ms"),
            new("tiles", "2k-1k", nameof(VisualizerSettings.TwoKeyBoundaryMs), Number, "ms"),
            new("tiles", "instant-down", nameof(VisualizerSettings.ImmediateDownshift), Toggle),
            new("tiles", "down-notes", nameof(VisualizerSettings.DownshiftNoteCount), Whole, "notes"),
            new("tiles", "pattern-8k", nameof(VisualizerSettings.EightKeyPattern), Tiles),
            new("tiles", "pattern-6k", nameof(VisualizerSettings.SixKeyPattern), Tiles),
            new("tiles", "pattern-4k", nameof(VisualizerSettings.FourKeyPattern), Tiles),
            new("tiles", "pattern-2k", nameof(VisualizerSettings.TwoKeyPattern), Tiles),
            new("tiles", "initial", nameof(VisualizerSettings.InitialTile), Choice),
            new("tiles", "order-8k", nameof(VisualizerSettings.EightKeyOrder), Tiles),
            new("tiles", "order-6k", nameof(VisualizerSettings.SixKeyOrder), Tiles),
            new("tiles", "order-4k", nameof(VisualizerSettings.FourKeyOrder), Tiles),
            new("tiles", "order-2k", nameof(VisualizerSettings.TwoKeyOrder), Tiles),
            new("timing", "speed", nameof(VisualizerSettings.PlaybackSpeedPercent), Number, "%"),
            new("timing", "chord-window", nameof(VisualizerSettings.SimultaneousWindowMs), Number, "ms"),
            new("timing", "no-center-pair", nameof(VisualizerSettings.NoChordCenterPair), Toggle),
            new("timing", "chord-side", nameof(VisualizerSettings.ChordLanePreference), Choice),
            new("timing", "chords-above-four", nameof(VisualizerSettings.ChordsOnlyAboveFourKeys), Toggle),
            new("timing", "trill-window", nameof(VisualizerSettings.TrillWindowMs), Number, "ms"),
            new("timing", "no-trill-center-pair", nameof(VisualizerSettings.NoTrillCenterPair), Toggle),
            new("timing", "trill-as-one", nameof(VisualizerSettings.TrillAsOneNote), Toggle),
            new("timing", "trill-side", nameof(VisualizerSettings.TrillLanePreference), Choice),
            new("judgment", "bar", nameof(VisualizerSettings.JudgmentBarMode), Choice),
            new("judgment", "window", nameof(VisualizerSettings.JudgmentBarDifficulty), Choice),
            new("judgment", "average", nameof(VisualizerSettings.ShowJudgmentAverage), Toggle),
            new("judgment", "average-notes", nameof(VisualizerSettings.JudgmentAverageCount), Whole, "notes"),
            new("timing", "start-delay", nameof(VisualizerSettings.StartDelayMs), Number, "ms"),
            new("timing", "calibration", nameof(VisualizerSettings.CalibrationNoteCount), Whole, "notes"),
            new("magic", "notes", nameof(VisualizerSettings.MagicCircleNoteCount), Whole, "notes"),
            new("magic", "max-interval", nameof(VisualizerSettings.MagicCircleMaxIntervalMs), Number, "ms"),
            new("magic", "chords", nameof(VisualizerSettings.MagicCircleChords), Toggle),
            new("magic", "fade-in", nameof(VisualizerSettings.MagicCircleFadeInMs), Number, "ms"),
            new("magic", "lead", nameof(VisualizerSettings.MagicCircleLeadMs), Number, "ms"),
            new("magic", "fade-out", nameof(VisualizerSettings.MagicCircleFadeOutMs), Number, "ms"),
            new("magic", "fixed", nameof(VisualizerSettings.MagicCircleFixedTiles), Toggle),
            new("magic", "fixed-count", nameof(VisualizerSettings.MagicCircleTileCount), Choice),
            new("display", "travel", nameof(VisualizerSettings.NoteTravelTimeMs), Number, "ms"),
            new("display", "line-height", nameof(VisualizerSettings.JudgmentLineHeightDip), Number, "px"),
            new("display", "press-window", nameof(VisualizerSettings.KeyPressWindowMs), Number, "ms"),
            new("display", "thickness", nameof(VisualizerSettings.NoteThickness), Number, "x"),
            new("display", "width", nameof(VisualizerSettings.NoteWidth), Number, "x"),
            new("display", "hold-width", nameof(VisualizerSettings.HoldNoteWidth), Number, "x"),
            new("display", "offset", nameof(VisualizerSettings.GlobalDisplayOffsetMs), Number, "ms"),
            new("display", "input-effect", nameof(VisualizerSettings.ShowInputEffect), Toggle),
            new("display", "miss-effect", nameof(VisualizerSettings.ShowMissEffect), Toggle),
            new("colors", "lane-effect", nameof(VisualizerSettings.LaneEffectColor), Color),
            new("colors", "note", nameof(VisualizerSettings.NoteColor), Color),
            new("colors", "chord", nameof(VisualizerSettings.ChordColor), Color),
            new("colors", "start-note", nameof(VisualizerSettings.StartNoteColor), Color),
            new("colors", "end-note", nameof(VisualizerSettings.EndNoteColor), Color),
            new("colors", "checkpoint", nameof(VisualizerSettings.CheckpointColor), Color),
            new("colors", "judgment-line", nameof(VisualizerSettings.JudgmentLineColor), Color),
            new("colors", "miss", nameof(VisualizerSettings.MissEffectColor), Color),
            new("colors", "pressed-transparency", nameof(VisualizerSettings.PressedNoteTransparencyPercent), Number, "%"),
            new("advanced", "experimental", nameof(VisualizerSettings.ExperimentalFeatures), Toggle),
            new(ExperimentalSection, "chord-before", nameof(VisualizerSettings.ChordAvoidBeforeLanes), Toggle),
            new(ExperimentalSection, "run-restart", nameof(VisualizerSettings.RestartPatternOnRun), Toggle),
            new(ExperimentalSection, "enhanced-chords", nameof(VisualizerSettings.EnhancedChordRules), Toggle),
            new(ExperimentalSection, "one-hand-chords", nameof(VisualizerSettings.OneHandFourChords), Toggle),
            new("advanced", "extra-lanes", nameof(VisualizerSettings.ExtraLanes), Toggle),
            new("advanced", "slowdown", nameof(VisualizerSettings.SlowNotesPastJudgmentLine), Toggle),
            new("advanced", "tile-order", nameof(VisualizerSettings.EnableTilePreference), Toggle),
            new("advanced", "chord-lanes", nameof(VisualizerSettings.ChordSectionLanes), Toggle),
            new("advanced", "keep-speed", nameof(VisualizerSettings.KeepSpeedOnLoad), Toggle),
            new("advanced", "next-gap", nameof(VisualizerSettings.NextIntervalTiles), Toggle),
            new("advanced", "both-gaps", nameof(VisualizerSettings.BothIntervalTiles), Toggle),
            new("timing", "pause-align", nameof(VisualizerSettings.AlignNextNoteOnPause), Toggle),
            new("advanced", "map-folder", nameof(VisualizerSettings.MapRootDirectory), Folder),
            new("advanced", "data-folder", nameof(VisualizerSettings.DataRootDirectory), DataFolder),
            new("advanced", "process", nameof(VisualizerSettings.AdofaiProcessName), Text)
        ]);
        return list.ToArray();
    }

    /// <summary>Whether a section can be read and set now: the experimental one only while experiments are on.</summary>
    public static bool IsOpen(string section, VisualizerSettings settings) =>
        !string.Equals(section, ExperimentalSection, StringComparison.OrdinalIgnoreCase) || settings.ExperimentalFeatures;

    public static IEnumerable<string> OpenSections(VisualizerSettings settings) =>
        Sections.Select(section => section.Name).Where(name => IsOpen(name, settings));

    public static IEnumerable<SettingPath> In(string section) =>
        All.Where(path => string.Equals(path.Section, section, StringComparison.OrdinalIgnoreCase));

    public static SettingPath? Find(string? name) =>
        All.FirstOrDefault(path => string.Equals(path.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The value as get shows it; set takes the same text back.</summary>
    public static string Format(SettingPath path, VisualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(settings);
        if (path.Lane >= 0)
        {
            var tokens = settings.GetLaneKeyTokens();
            return path.Lane < tokens.Count ? tokens[path.Lane] : "";
        }
        var value = SettingsFields.Get(settings, path.Property);
        return path.Kind switch
        {
            Toggle => value is true ? "true" : "false",
            OptionalKey => value is string { Length: > 0 } key ? key : "none",
            DataFolder => value is string { Length: > 0 } folder ? folder : "auto",
            Number => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture),
            Whole => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            Choice => Show(path, value),
            _ => value as string ?? ""
        };
    }

    /// <summary>The values a toggle or a list takes, as set accepts them; empty for free values.</summary>
    public static IReadOnlyList<string> Choices(SettingPath path, VisualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(settings);
        if (path.Kind == Toggle) return ["true", "false"];
        if (path.Kind == OptionalKey) return ["none"];
        if (path.Kind == DataFolder) return ["auto"];
        if (path.Kind != Choice) return [];
        return path.Property switch
        {
            nameof(VisualizerSettings.Language) => Languages.All.Select(language => language.Id).ToArray(),
            nameof(VisualizerSettings.Theme) => ThemeCatalog.All.Select(theme => theme.Id).ToArray(),
            nameof(VisualizerSettings.SongSortKey) => Enum.GetNames<SongSortField>().Select(Kebab).ToArray(),
            nameof(VisualizerSettings.SongGroup) => Enum.GetNames<SongGroupField>().Select(Kebab).ToArray(),
            nameof(VisualizerSettings.MaximumTileCount) => Numbers(VisualizerSettings.Modes),
            // Only counts the maximum allows, as the settings window lists them.
            nameof(VisualizerSettings.MinimumTileCount) or nameof(VisualizerSettings.MagicCircleTileCount) =>
                Numbers(VisualizerSettings.Modes.Where(mode => mode <= settings.MaximumTileCount)),
            nameof(VisualizerSettings.InitialTile) => Numbers(Enumerable.Range(1, VisualizerSettings.LaneKeyCount)),
            // "max" follows the maximum tile count; the rest are the lane counts at or above it.
            nameof(VisualizerSettings.DisplayLaneCount) =>
                ["max", .. Numbers(VisualizerSettings.Modes.Where(mode => mode >= settings.MaximumTileCount))],
            nameof(VisualizerSettings.ChordLanePreference) or nameof(VisualizerSettings.TrillLanePreference) =>
                ["none", "1-2", "3-4", "1-2,3-4"],
            nameof(VisualizerSettings.JudgmentBarMode) => ["off", "magiccircle", "always"],
            nameof(VisualizerSettings.JudgmentBarDifficulty) => ["lenient", "normal", "strict"],
            _ => []
        };
    }

    /// <summary>What property says a free value looks like.</summary>
    public static string Describe(SettingPath path) => path.Kind switch
    {
        Number => T("TakesNumberIn", Unit(path)),
        Whole => T("TakesWholeOf", path.Unit),
        Key => T("TakesKey"),
        OptionalKey => T("TakesOptionalKey"),
        Color => T("TakesColor"),
        Tiles => T("TakesTiles"),
        Folder => T("TakesFolder"),
        DataFolder => T("TakesDataFolder"),
        Text => T("TakesText"),
        _ => T("TakesAbove")
    };

    private static string Unit(SettingPath path) => path.Unit switch { "x" => T("UnitTimes"), "%" => T("UnitPercent"), _ => path.Unit };

    /// <summary>
    /// Reads what set was given. The result still has to pass the settings' own checks; this only turns the text into the
    /// kind of value the setting holds. <paramref name="error"/> says why the text was not usable.
    /// </summary>
    public static bool TryParse(SettingPath path, string? text, VisualizerSettings settings, out object? value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(path);
        value = null;
        error = null;
        var input = (text ?? "").Trim();
        if (input.Length == 0 && path.Kind is not (Text or Folder))
        {
            error = T("ValueNeededTyped", path.Name);
            return false;
        }
        switch (path.Kind)
        {
            case Toggle:
                // get always shows true or false; set takes the other usual words as well.
                if (input.ToLowerInvariant() is "true" or "1" or "on" or "able" or "enable") value = true;
                else if (input.ToLowerInvariant() is "false" or "0" or "off" or "disable") value = false;
                else error = T("UseToggle");
                break;
            case Choice:
                var choice = Choices(path, settings).FirstOrDefault(option => option.Equals(input, StringComparison.OrdinalIgnoreCase));
                if (choice is null) error = T("UseOneOf", string.Join(", ", Choices(path, settings)));
                else value = Store(path, choice);
                break;
            case Number:
                if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) value = number;
                else error = T("UseNumberIn", Unit(path));
                break;
            case Whole:
                if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) value = whole;
                else error = T("UseWhole");
                break;
            case Key or OptionalKey:
                if (path.Kind == OptionalKey && input.Equals("none", StringComparison.OrdinalIgnoreCase)) value = null;
                else if (KeyNames.TryParse(input, out var key) && KeyNames.IsSupported(key)) value = KeyNames.Token(key);
                else error = T("NotAKey", input);
                break;
            case Color:
                if (ColorText.NormalizeRgb(input) is { } color) value = color;
                else error = T("UseColor");
                break;
            case Tiles:
                var spaced = string.Join(' ', input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                // Patterns are tile sets and are kept ascending, as the settings window keeps them; orders keep their order.
                value = path.Key.StartsWith("pattern", StringComparison.Ordinal) ? VisualizerSettings.SortTiles(spaced) : spaced;
                break;
            case DataFolder:
                value = input.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "" : input;
                break;
            default:
                value = input;
                break;
        }
        return error is null;
    }

    /// <summary>A copy of <paramref name="settings"/> with the setting replaced; nothing is validated here.</summary>
    public static VisualizerSettings With(VisualizerSettings settings, SettingPath path, object? value)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(path);
        if (path.Lane < 0) return SettingsFields.With(settings, path.Property, value);
        var tokens = settings.GetLaneKeyTokens().ToList();
        while (tokens.Count < VisualizerSettings.LaneKeyCount) tokens.Add("");
        tokens[path.Lane] = value as string ?? "";
        return settings with { LaneKeys = string.Join(' ', tokens) };
    }

    /// <summary>
    /// Why the setting cannot be set now, or null. These are the inputs the settings window locks: a locked value is kept but
    /// has no effect until the setting that locks it changes.
    /// </summary>
    public static string? LockedBy(SettingPath path, VisualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsOpen(path.Section, settings)) return T("ExperimentalClosed");
        return path.Property switch
        {
            nameof(VisualizerSettings.EightKeyBoundaryMs) => Threshold(8),
            nameof(VisualizerSettings.SixKeyBoundaryMs) => Threshold(6),
            nameof(VisualizerSettings.FourKeyBoundaryMs) => Threshold(4),
            nameof(VisualizerSettings.TwoKeyBoundaryMs) => Threshold(2),
            nameof(VisualizerSettings.DownshiftNoteCount) => settings.ImmediateDownshift ? null : T("LockedInstantDown"),
            nameof(VisualizerSettings.MagicCircleNoteCount) or nameof(VisualizerSettings.MagicCircleMaxIntervalMs)
                or nameof(VisualizerSettings.MagicCircleFadeInMs) or nameof(VisualizerSettings.MagicCircleLeadMs)
                or nameof(VisualizerSettings.MagicCircleFadeOutMs) or nameof(VisualizerSettings.MagicCircleFixedTiles)
                or nameof(VisualizerSettings.MagicCircleChords) =>
                settings.MagicCircleJudgmentLine ? null : T("LockedMagicLine"),
            nameof(VisualizerSettings.MagicCircleTileCount) => settings.UseMagicCircleTiles ? null : T("LockedMagicFixed"),
            nameof(VisualizerSettings.ExtraLanes) => settings.CanUseExtraLanes ? null : T("LockedSideLanes"),
            nameof(VisualizerSettings.EightKeyOrder) or nameof(VisualizerSettings.SixKeyOrder)
                or nameof(VisualizerSettings.FourKeyOrder) or nameof(VisualizerSettings.TwoKeyOrder) =>
                settings.EnableTilePreference ? null : T("LockedTileOrder"),
            _ => null
        };

        string? Threshold(int mode) => settings.IsThresholdEnabled(mode) ? null
            : T("LockedBoundary");
    }

    /// <summary>The stored form of a list value: enum names for the song list, "None" for no chord side, numbers as int.</summary>
    private static object Store(SettingPath path, string choice) => path.Property switch
    {
        nameof(VisualizerSettings.SongSortKey) => Enum.GetNames<SongSortField>().First(name => Kebab(name) == choice.ToLowerInvariant()),
        nameof(VisualizerSettings.SongGroup) => Enum.GetNames<SongGroupField>().First(name => Kebab(name) == choice.ToLowerInvariant()),
        nameof(VisualizerSettings.ChordLanePreference) or nameof(VisualizerSettings.TrillLanePreference) =>
            choice.Equals("none", StringComparison.OrdinalIgnoreCase) ? "None" : choice,
        nameof(VisualizerSettings.JudgmentBarMode) => choice.ToLowerInvariant() switch
            { "off" => "Off", "always" => "Always", _ => "MagicCircle" },
        nameof(VisualizerSettings.JudgmentBarDifficulty) => choice.ToLowerInvariant() switch
            { "lenient" => "Lenient", "strict" => "Strict", _ => "Normal" },
        nameof(VisualizerSettings.MaximumTileCount) or nameof(VisualizerSettings.MinimumTileCount)
            or nameof(VisualizerSettings.MagicCircleTileCount) or nameof(VisualizerSettings.InitialTile) =>
            int.Parse(choice, CultureInfo.InvariantCulture),
        nameof(VisualizerSettings.DisplayLaneCount) => choice.Equals("max", StringComparison.OrdinalIgnoreCase)
            ? 0 : int.Parse(choice, CultureInfo.InvariantCulture),
        _ => choice
    };

    private static string Show(SettingPath path, object? value) => path.Property switch
    {
        nameof(VisualizerSettings.SongSortKey) or nameof(VisualizerSettings.SongGroup) => Kebab(value as string ?? ""),
        nameof(VisualizerSettings.ChordLanePreference) or nameof(VisualizerSettings.TrillLanePreference) =>
            (value as string ?? "None").Trim() is var side && side.Equals("None", StringComparison.OrdinalIgnoreCase) ? "none" : side,
        // 0 means the lanes follow the maximum tile count, which reads as the word rather than a number.
        nameof(VisualizerSettings.DisplayLaneCount) => value is 0 ? "max" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    /// <summary>"NoteCount" → "note-count".</summary>
    private static string Kebab(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static string[] Numbers(IEnumerable<int> values) => values.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray();
}
