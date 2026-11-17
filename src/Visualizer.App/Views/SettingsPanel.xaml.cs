using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using Visualizer.App.Input;
using Visualizer.App.Themes;
using Visualizer.Core.Localization;
using Visualizer.Application.Library;
using Visualizer.Core.Settings;

namespace Visualizer.App.Views;

/// <summary>
/// The settings dialog. Row builders live in SettingsPanel.Rows.cs; reading the form, change marks (★ and section markers)
/// and previews in SettingsPanel.Form.cs; the advanced section with JSON export, import and reset in SettingsPanel.Json.cs.
/// </summary>
public partial class SettingsPanel : UserControl
{
    private const double LabelWidth = 170;
    private static readonly VisualizerSettings Defaults = new();

    /// <summary>A collapsible settings card. Its marker is sky blue when nothing changed since Apply, orange when changed, red when invalid.</summary>
    private sealed class Section(string key, Ellipse marker, Border card)
    {
        public string Key { get; } = key;
        public Ellipse Marker { get; } = marker;
        public Border Card { get; } = card;
        public List<string> Fields { get; } = [];
        public string State { get; set; } = "default";
    }

    private readonly Dictionary<string, TextBox> _textFields = [];
    private readonly Dictionary<TextBlock, string> _labels = [];
    private readonly Dictionary<FrameworkElement, string> _helpMarks = [];
    /// <summary>Row → setting, so the note beside Apply can follow the pointer or the focused input.</summary>
    private readonly Dictionary<FrameworkElement, string> _rowFields = [];
    private string? _hoveredField;
    private string? _focusedField;
    /// <summary>Setting property name → label key, to name problem settings in messages.</summary>
    private readonly Dictionary<string, string> _fieldLabels = [];
    /// <summary>Setting property name → the ★ beside its label, shown while the value differs from the applied one.</summary>
    private readonly Dictionary<string, TextBlock> _fieldStars = [];
    private readonly List<Section> _sections = [];
    private Section? _experimentalSection;
    private readonly Dictionary<string, TextBox> _orderBoxes = [];
    /// <summary>The muted line shown inside an empty box → its language key.</summary>
    private readonly Dictionary<TextBlock, string> _placeholders = [];
    /// <summary>Setting → its toggle and the small word beside it saying what the toggle is doing now.</summary>
    private readonly Dictionary<string, (CheckBox Toggle, TextBlock Text)> _stateTexts = [];
    private readonly ISettingsLayoutStore _layout;
    private readonly ComboBox _language = new() { ItemsSource = Languages.All.Select(language => language.Name).ToArray() };
    private readonly KeyCaptureBox _reset;
    private readonly KeyCaptureBox _start;
    private readonly KeyCaptureBox _toggle;
    private readonly KeyCaptureBox _previous;
    private readonly KeyCaptureBox _next;
    private readonly KeyCaptureBox _settingsKey;
    private readonly KeyCaptureBox[] _laneBoxes;
    private readonly TextBox[] _laneBackgroundBoxes = LaneColorBoxes();
    private readonly Button _clearSettingsKey = new() { MinWidth = 64 };
    private readonly Button _clearPreviousKey = new() { MinWidth = 64 };
    private readonly Button _clearNextKey = new() { MinWidth = 64 };
    private readonly CheckBox _foreground = new();
    private readonly CheckBox _guide = new();
    private readonly CheckBox _songInfo = new();
    private readonly CheckBox _inputEffect = new();
    private readonly CheckBox _missEffect = new();
    private readonly CheckBox _tilePreference = new();
    private readonly CheckBox _extraLanes = new();
    private readonly CheckBox _judgmentSlowdown = new();
    private readonly CheckBox _immediateDownshift = new();
    private readonly CheckBox _experimental = new();
    private readonly CheckBox _chordLanes = new();
    private readonly ComboBox _pauseAlign = new();
    private readonly CheckBox _keepSpeed = new();
    private readonly ComboBox _chordLane = new();
    private readonly ComboBox _trillCenter = new();
    private readonly CheckBox _trillOne = new();
    private readonly CheckBox _chordsAboveFour = new();
    private readonly ComboBox _trillLane = new();
    private readonly CheckBox _magicFixedTiles = new();
    private readonly CheckBox _magicChords = new();
    private readonly ComboBox _magicTiles = new() { ItemsSource = VisualizerSettings.Modes.ToArray() };
    private readonly Button _browseFolder = new() { MinWidth = 64 };
    private readonly Button _browseData = new() { MinWidth = 64 };
    private readonly ComboBox _songSortField = new();
    private readonly ComboBox _songSortDirection = new();
    private readonly ComboBox _songGroup = new();
    private readonly Button _songListButton = new();
    private readonly Button _editSongButton = new();
    private readonly Button _syncButton = new();
    private readonly Button _resyncButton = new();
    private readonly TextBlock _dataRootPath = new() { FontSize = 9, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -4, 0, 10) };
    private readonly ComboBox _maximum = new() { ItemsSource = VisualizerSettings.Modes.ToArray() };
    private readonly ComboBox _minimum = new() { ItemsSource = VisualizerSettings.Modes.ToArray() };
    private readonly ComboBox _displayLanes = new();
    private readonly CheckBox _dynamic = new();
    private VisualizerSettings _current;
    private readonly CheckBox _autoCheckpointBox = new();
    private readonly CheckBox _autoCheckpointLoad = new();
    private readonly CheckBox _hidePractice = new();
    private readonly CheckBox _autoSave = new();
    private readonly ComboBox _nextInterval = new();
    private readonly CheckBox _chordBefore = new();
    private readonly CheckBox _runRestart = new();
    private readonly CheckBox _enhancedChords = new();
    private readonly CheckBox _enhancedKeys = new();
    private readonly CheckBox _judgmentAverage = new();
    private readonly ComboBox _judgmentMode = new();
    private readonly ComboBox _judgmentDifficulty = new();
    private bool _loading = true;
    private bool _syncingTheme;
    private string _theme;
    private StackPanel? _section;
    private Section? _currentSection;

    public VisualizerSettings? AppliedSettings { get; private set; }
    /// <summary>Where exported JSON goes; the system clipboard by default (replaceable in tests).</summary>
    public Action<string> CopyText { get; set; } = CopyToClipboard;
    /// <summary>Asks (title, message, accept label) and returns whether accepted. Null (the default) shows the themed dialog.</summary>
    public Func<string, string, string, bool>? Confirm { get; set; }
    /// <summary>Receives the effects and colors being edited, so the note field can preview them before Apply.</summary>
    public Action<VisualizerSettings>? Preview { get; set; }
    /// <summary>Asks for a map folder starting from the current text; null when cancelled. The Windows folder picker by default (replaceable in tests).</summary>
    public Func<string, string?> PickFolder { get; set; }
    /// <summary>The store actually in use, shown under the data folder box; set by the main window.</summary>
    public string DataRootInUse
    {
        get => _dataRoot;
        set { _dataRoot = value ?? ""; UpdateDataRootPath(); }
    }
    private string _dataRoot = "";
    /// <summary>
    /// Song list choices being edited. The window only holds them; they are written when the settings are applied.
    /// </summary>
    public IReadOnlyList<Visualizer.Application.Library.SongPreference> SongPreferences { get; set; } = [];
    /// <summary>Song list details being edited; switched in the song list window and applied with the settings.</summary>
    public string SongListFields { get; set; } = "";
    /// <summary>Opens the song list window with the pending choices and returns the confirmed ones; set by the main window.</summary>
    public Func<IReadOnlyList<Visualizer.Application.Library.SongPreference>, SongListResult?>? OpenSongList { get; set; }
    /// <summary>Starts editing the song that is loaded; set by the main window. Null while the edit screen cannot open.</summary>
    public Action? EditSongRequested { get; set; }
    /// <summary>Rebuilds the custom map folder from the map folder; set by the main window.</summary>
    public Func<bool>? ResyncMaps { get; set; }
    /// <summary>Brings the map folder in; set by the main window.</summary>
    public Action? SyncMaps { get; set; }
    /// <summary>Opens the console; set by the main window.</summary>
    public Action? OpenConsole { get; set; }
    /// <summary>Applies settings to the app while this dialog stays open (the console's [Y/n]); set by the main window.</summary>
    public Action<VisualizerSettings>? SettingsApplied { get; set; }
    /// <summary>Whether the form differs from the applied settings: any section changed or invalid.</summary>
    public bool HasUnappliedChanges => _sections.Any(section => section.State != "default");
    private string SelectedLanguage => _language.SelectedIndex >= 0 && _language.SelectedIndex < Languages.All.Count
        ? Languages.All[_language.SelectedIndex].Id : Languages.Default;
    private string T(string key) => Languages.Get(SelectedLanguage, key);
    private Panel CurrentSection => (Panel?)_section ?? Fields;

    public SettingsPanel(VisualizerSettings settings, ISettingsLayoutStore? layout = null)
    {
        InitializeComponent();
        _current = settings;
        _layout = layout ?? MemorySettingsLayoutStore.Shared;
        _language.SelectedIndex = LanguageIndex(settings.Language);
        _theme = settings.Theme;
        ApplyTheme();
        _reset = new KeyCaptureBox(settings.ResetKey);
        _start = new KeyCaptureBox(settings.StartTriggerKey);
        _toggle = new KeyCaptureBox(settings.VisualizerToggleKey);
        _previous = new KeyCaptureBox(settings.PreviousCheckpointKey, allowNone: true);
        _next = new KeyCaptureBox(settings.NextCheckpointKey, allowNone: true);
        _settingsKey = new KeyCaptureBox(settings.SettingsHotkey, allowNone: true);
        var laneTokens = settings.GetLaneKeyTokens();
        _laneBoxes = Enumerable.Range(0, VisualizerSettings.LaneKeyCount)
            .Select(i => new KeyCaptureBox(i < laneTokens.Count ? laneTokens[i] : null)).ToArray();
        foreach (var (button, box) in new[] { (_clearSettingsKey, _settingsKey), (_clearPreviousKey, _previous), (_clearNextKey, _next) })
        {
            button.SetResourceReference(StyleProperty, "ToolButton");
            button.Click += (_, _) => box.ClearKey();
        }
        // The console (Ctrl+T) and the editor (Ctrl+E) open from the settings too, wherever they are shown.
        PreviewKeyDown += (_, e) =>
        {
            if (CmdWindow.IsShortcut(e))
            {
                e.Handled = true;
                OpenConsole?.Invoke();
            }
            else if (MainWindow.IsEditShortcut(e))
            {
                e.Handled = true;
                EditSong();
            }
            // Escape leaves, asking first when something is unapplied.
            else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                else Leave();
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && EnterApplies(Keyboard.FocusedElement))
            {
                e.Handled = true;
                ApplyClick(ApplyButton, new RoutedEventArgs());
            }
        };
        CancelButton.Click += (_, _) => Leave();
        SearchBox.TextChanged += (_, _) => Search(SearchBox.Text);
        // The grey prompt stands where the cursor does, not at a margin of its own (user report).
        AlignPrompt(SearchBox, SearchHint);
        PreviewRulesButton.Click += (_, _) => ShowPreview();
        PickFolder = PickFolderWithDialog;
        _browseFolder.SetResourceReference(StyleProperty, "ToolButton");
        _browseFolder.Click += (_, _) => BrowseMapFolder();
        _browseData.SetResourceReference(StyleProperty, "ToolButton");
        _browseData.Click += (_, _) => BrowseDataFolder();
        _dataRootPath.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        ToolTipService.SetShowOnDisabled(_extraLanes, true);

        AddSection("SectionGeneral");
        AddField("Language", _language, nameof(settings.Language));
        AddField("Guide buttons", _guide, nameof(settings.ShowGuideButtons));
        AddField("Song info", _songInfo, nameof(settings.ShowSongInfo));
        AddField("Global input scope", _foreground, nameof(settings.OnlyReactWhenAdofaiForeground), "InputScopeHelp");
        AddField("Auto save", _autoSave, nameof(settings.AutoSaveSettings));

        AddSection("SectionSongs");
        AddField("Song order", SortRow(), nameof(settings.SongSortKey));
        AddField("Song group", _songGroup, nameof(settings.SongGroup));
        AddField("Hide practice maps", _hidePractice, nameof(settings.HidePracticeMaps), "PracticeMapsHelp");
        CurrentSection.Children.Add(SongListButtons());

        // Everything about checkpoints in one card, instead of the toggle living only on the main screen and its keys
        // sitting among the other controls.
        AddSection("SectionCheckpoint");
        AddField("Auto checkpoint", _autoCheckpointBox, nameof(settings.AutoCheckpointAdvance));
        AddField("Auto checkpoint on load", _autoCheckpointLoad, nameof(settings.AutoCheckpointOnLoad));
        AddField("Previous checkpoint key", WithTrailing(_previous, _clearPreviousKey), nameof(settings.PreviousCheckpointKey));
        AddField("Next checkpoint key", WithTrailing(_next, _clearNextKey), nameof(settings.NextCheckpointKey));

        AddSection("SectionControls");
        AddField("Reset Key", _reset, nameof(settings.ResetKey));
        AddField("Start Trigger Key", _start, nameof(settings.StartTriggerKey));
        AddField("Visualizer Toggle", _toggle, nameof(settings.VisualizerToggleKey));
        AddField("Settings hotkey", WithTrailing(_settingsKey, _clearSettingsKey), nameof(settings.SettingsHotkey));
        AddField("Lane keys", LaneKeyGrid(), nameof(settings.LaneKeys), "LaneKeyHelp");

        AddSection("SectionPattern");
        AddField("Maximum Tile Count", _maximum, nameof(settings.MaximumTileCount));
        AddField("Minimum Tile Count", _minimum, nameof(settings.MinimumTileCount), "MinimumTileHelp");
        AddField("Dynamic Timing", _dynamic, nameof(settings.DynamicTiming));
        AddText("8K ↔ 6K (ms)", nameof(settings.EightKeyBoundaryMs));
        AddText("6K ↔ 4K (ms)", nameof(settings.SixKeyBoundaryMs));
        AddText("4K ↔ 2K (ms)", nameof(settings.FourKeyBoundaryMs));
        AddText("2K ↔ 1K (ms)", nameof(settings.TwoKeyBoundaryMs));
        AddField("Immediate downshift", _immediateDownshift, nameof(settings.ImmediateDownshift), "ImmediateDownshiftHelp");
        AddText("Downshift notes", nameof(settings.DownshiftNoteCount), "DownshiftHelp");
        AddPatternHeader();
        AddPattern("8K pattern", nameof(settings.EightKeyPattern), nameof(settings.EightKeyOrder));
        AddPattern("6K pattern", nameof(settings.SixKeyPattern), nameof(settings.SixKeyOrder));
        AddPattern("4K pattern", nameof(settings.FourKeyPattern), nameof(settings.FourKeyOrder));
        AddPattern("2K pattern", nameof(settings.TwoKeyPattern), nameof(settings.TwoKeyOrder));
        AddSingleTile("1K pattern · Initial tile", nameof(settings.InitialTile), "InitialTileHelp");

        AddSection("SectionTiming");
        AddText("Playback speed (%)", nameof(settings.PlaybackSpeedPercent));
        AddText("Simultaneous window (ms)", nameof(settings.SimultaneousWindowMs), "SimultaneousHelp");
        // The chord rules sit under the chord window.
        AddField("Chord lane preference", _chordLane, nameof(settings.ChordLanePreference), "ChordLaneHelp");
        AddField("Above four keys only for chords", _chordsAboveFour, nameof(settings.ChordsOnlyAboveFourKeys), "ChordsAboveFourHelp");
        // Trills are worked out after the chords and have the same two rules of their own (user request).
        AddText("Trill window (ms)", nameof(settings.TrillWindowMs), "TrillHelp");
        AddField("Trill as one note", _trillOne, nameof(settings.TrillAsOneNote), "TrillAsOneHelp");
        AddField("Trill center pair", _trillCenter, nameof(settings.TrillCenterPair), "TrillCenterHelp");
        AddField("Trill lane preference", _trillLane, nameof(settings.TrillLanePreference), "TrillLaneHelp");
        AddText("Start delay (ms)", nameof(settings.StartDelayMs));
        AddText("Calibration notes", nameof(settings.CalibrationNoteCount), "CalibrationHelp");
        AddField("Pause aligns next note", _pauseAlign, nameof(settings.AlignNextNoteOnPause), "PauseAlignHelp");

        AddSection("SectionJudgmentBar", "JudgmentBarHelp");
        AddField("Judgment bar", _judgmentMode, nameof(settings.JudgmentBarMode));
        AddField("Judgment window", _judgmentDifficulty, nameof(settings.JudgmentBarDifficulty), "JudgmentDifficultyHelp");
        AddField("Average arrow", _judgmentAverage, nameof(settings.ShowJudgmentAverage));
        AddText("Average of notes", nameof(settings.JudgmentAverageCount));
        // A slider in steps of 0.1, as the note scales are (user rule).
        AddScale("Judgment ease", nameof(settings.JudgmentEaseScale), "JudgmentEaseHelp", 1, Visualizer.Core.Maps.HitGrading.MaximumEase);

        // The magic circle has its own card; its switch at the top disables every row below it.
        AddSection("SectionMagic", "MagicCircleHelp");
        AddText("Magic circle notes", nameof(settings.MagicCircleNoteCount));
        AddText("Magic circle max interval (ms)", nameof(settings.MagicCircleMaxIntervalMs));
        AddField("Chords in magic circles", _magicChords, nameof(settings.MagicCircleChords), "MagicCircleChordsHelp");
        AddText("Judgment line fade in (ms)", nameof(settings.MagicCircleFadeInMs));
        AddText("Magic circle lead (ms)", nameof(settings.MagicCircleLeadMs));
        AddText("Judgment line fade out (ms)", nameof(settings.MagicCircleFadeOutMs));
        AddField("Fixed tiles", _magicFixedTiles, nameof(settings.MagicCircleFixedTiles), "MagicCircleFixedHelp");
        AddField("Fixed tile count", _magicTiles, nameof(settings.MagicCircleTileCount), "MagicCircleTilesHelp");

        AddSection("SectionDisplay");
        AddField("Display lanes", _displayLanes, nameof(settings.DisplayLaneCount), "DisplayLaneHelp");
        AddText("Note Travel Time (ms)", nameof(settings.NoteTravelTimeMs));
        AddText("Judgment line height (px)", nameof(settings.JudgmentLineHeightDip));
        AddText("Key press window (±ms)", nameof(settings.KeyPressWindowMs));
        AddScale("Note Thickness (×)", nameof(settings.NoteThickness));
        AddScale("Note width (×)", nameof(settings.NoteWidth));
        AddScale("Hold note width (×)", nameof(settings.HoldNoteWidth));
        AddText("Display Offset (±ms)", nameof(settings.GlobalDisplayOffsetMs));
        AddField("Input effect", _inputEffect, nameof(settings.ShowInputEffect));
        AddField("Miss effect", _missEffect, nameof(settings.ShowMissEffect));

        // Design: the theme first (user rule), then the lane light, the notes, the judgment line, how faint a hit note
        // gets, and the miss effect.
        AddSection("SectionColors", "ColorHelp");
        HeaderBar.Children.Remove(ThemeSelector);
        AddField("Theme", ThemeSelector);
        AddColorHeader();
        AddColor("Lane effect color", nameof(settings.LaneEffectColor));
        AddColor("Note color", nameof(settings.NoteColor));
        AddColor("Chord color", nameof(settings.ChordColor));
        AddColor("Trill color", nameof(settings.TrillColor));
        AddColor("Start note color", nameof(settings.StartNoteColor));
        AddColor("End note color", nameof(settings.EndNoteColor));
        AddColor("Checkpoint color", nameof(settings.CheckpointColor));
        AddColor("Judgment line color", nameof(settings.JudgmentLineColor));
        AddColor("Miss effect color", nameof(settings.MissEffectColor));
        AddText("Pressed note transparency (%)", nameof(settings.PressedNoteTransparencyPercent));
        // Lane backgrounds follow the theme and are not shown here; the hidden boxes still carry an
        // imported or reset value so the setting and JSON keep working.

        AddAdvanced();
        // Experiments have their own card, which only takes input while the switch in Advanced is on (user rule).
        AddSection("SectionExperimental", "ExperimentalSectionHelp");
        AddField("Chord keeps the lane before it", _chordBefore, nameof(settings.ChordAvoidBeforeLanes), "ChordBeforeHelp");
        AddField("Restart the pattern each run", _runRestart, nameof(settings.RestartPatternOnRun), "RunRestartHelp");
        AddField("Enhanced chord and trill rules", _enhancedChords, nameof(settings.EnhancedChordRules), "EnhancedChordsHelp");
        AddField("Enhanced 4/8-key rules", _enhancedKeys, nameof(settings.EnhancedFourEightKeys), "EnhancedKeysHelp");
        // Parked here while the field makes room for the slot picker (user rule): the editor is reached from the console
        // as well, so this is a way in, not the only one.
        AddField("Note editor", _editSongButton);
        _experimentalSection = _sections[^1];
        LoadForm(settings);

        foreach (var box in new[] { _reset, _start, _toggle, _previous, _next, _settingsKey }.Concat(_laneBoxes))
            box.TextChanged += (_, _) => FormChanged();
        foreach (var check in new[] { _foreground, _guide, _songInfo, _inputEffect, _missEffect, _extraLanes, _judgmentSlowdown, _keepSpeed, _magicChords })
        {
            check.Checked += (_, _) => FormChanged();
            check.Unchecked += (_, _) => FormChanged();
        }
        foreach (var box in new[] { _magicFixedTiles })
        {
            box.Checked += (_, _) => { UpdateEnabled(); FormChanged(); };
            box.Unchecked += (_, _) => { UpdateEnabled(); FormChanged(); };
        }
        _magicTiles.SelectionChanged += (_, _) => FormChanged();
        _chordLane.SelectionChanged += (_, _) => FormChanged();
        _trillLane.SelectionChanged += (_, _) => FormChanged();
        _trillCenter.SelectionChanged += (_, _) => FormChanged();
        _judgmentMode.SelectionChanged += (_, _) => FormChanged();
        _judgmentDifficulty.SelectionChanged += (_, _) => FormChanged();
        _songSortField.SelectionChanged += (_, _) => { UpdateSongSort(); FormChanged(); };
        _songSortDirection.SelectionChanged += (_, _) => FormChanged();
        _songGroup.SelectionChanged += (_, _) => FormChanged();
        _pauseAlign.SelectionChanged += (_, _) => FormChanged();
        _nextInterval.SelectionChanged += (_, _) => FormChanged();
        foreach (var (button, style) in new[] { (_songListButton, "ToolButton"), (_syncButton, "ToolButton"),
                     (_resyncButton, "DangerButton"), (_editSongButton, "ToolButton") })
        {
            button.SetResourceReference(StyleProperty, style);
            button.Padding = new Thickness(4, 0, 4, 0);
        }
        _songListButton.Click += (_, _) => ShowSongList();
        _editSongButton.Click += (_, _) => EditSong();
        _syncButton.Click += (_, _) => SyncMaps?.Invoke();
        _resyncButton.Click += (_, _) => Resync();
        _judgmentAverage.Checked += (_, _) => { UpdateEnabled(); FormChanged(); };
        _judgmentAverage.Unchecked += (_, _) => { UpdateEnabled(); FormChanged(); };
        _immediateDownshift.Checked += (_, _) => { UpdateEnabled(); FormChanged(); };
        _immediateDownshift.Unchecked += (_, _) => { UpdateEnabled(); FormChanged(); };
        _experimental.Checked += (_, _) => { UpdateEnabled(); FormChanged(); };
        _experimental.Unchecked += (_, _) => { UpdateEnabled(); FormChanged(); };
        foreach (var box in new[] { _chordLanes, _trillOne, _chordsAboveFour, _enhancedKeys })
        {
            box.Checked += (_, _) => FormChanged();
            box.Unchecked += (_, _) => FormChanged();
        }
        _tilePreference.Checked += (_, _) => { UpdateTilePreference(); FormChanged(); };
        _tilePreference.Unchecked += (_, _) => { UpdateTilePreference(); FormChanged(); };
        _dynamic.Checked += (_, _) => { UpdateEnabled(); FormChanged(); };
        _dynamic.Unchecked += (_, _) => { UpdateEnabled(); FormChanged(); };
        _maximum.SelectionChanged += (_, _) => { UpdateEnabled(); FormChanged(); };
        _minimum.SelectionChanged += (_, _) => { UpdateEnabled(); FormChanged(); };
        _displayLanes.SelectionChanged += (_, _) => FormChanged();
        _language.SelectionChanged += (_, _) => { UpdateLanguage(); FormChanged(); };
        Fields.MouseMove += (_, moved) => SetHoveredField(FieldAt(moved.GetPosition(Fields)));
        Fields.MouseLeave += (_, _) => SetHoveredField(null);
        AddHandler(GotFocusEvent, new RoutedEventHandler((_, focus) => SetFocusedField(FieldOf(focus.OriginalSource as DependencyObject))));
        AddHandler(LostFocusEvent, new RoutedEventHandler((_, _) => SetFocusedField(null)));
        UpdateLanguage();
        _loading = false;
        UpdateSectionStates();
    }

    /// <summary>
    /// Whether Enter means Apply: while no setting holds the focus (the note follows the pointer then) or a toggle does,
    /// which Enter would do nothing for. Text, lists, key boxes and plain buttons keep Enter.
    /// </summary>
    public bool EnterApplies(IInputElement? focused) =>
        focused is CheckBox || (_focusedField is null && focused is not (System.Windows.Controls.Primitives.TextBoxBase or ComboBox or Button or KeyCaptureBox));

    /// <summary>Reads, fills blanks with defaults, validates and checks key bindings. Throws <see cref="ArgumentException"/> when invalid.</summary>
    public VisualizerSettings BuildSettings()
    {
        var settings = ReadForm();
        settings.EnsureValid();
        if (!ThemeCatalog.Exists(settings.Theme)) throw new ArgumentException(UnknownThemeMessage(settings.Language));
        _ = ControlBindings.Create(settings);
        return settings;
    }

    /// <summary>The input of a setting's row. Toggles carry no text of their own, so tests look them up here.</summary>
    public FrameworkElement? InputFor(string field)
    {
        foreach (var (row, name) in _rowFields)
        {
            if (name != field || row is not Grid grid) continue;
            foreach (var child in grid.Children.OfType<FrameworkElement>())
                if (Grid.GetColumn(child) == 1)
                    return child is StackPanel { Children: [CheckBox toggle, ..] } ? toggle : child;
        }
        return null;
    }

    public string SectionState(string sectionKey) => _sections.Single(section => section.Key == sectionKey).State;

    /// <summary>The display lane box's item for a setting value: 0 (follow the maximum) is the first one.</summary>
    private static int LaneIndex(int lanes) => lanes <= 0 ? 0 : VisualizerSettings.Modes.ToList().IndexOf(lanes) + 1;

    /// <summary>Whether the ★ beside a setting is shown, by property name (for example "NoteTravelTimeMs").</summary>
    public bool IsMarkedChanged(string field) => _fieldStars.TryGetValue(field, out var star) && star.Visibility == Visibility.Visible;

    private static string UnknownThemeMessage(string language) =>
        Languages.Format(language, "ThemeUnknown", string.Join(", ", ThemeCatalog.All.Select(theme => theme.Id)));

    private static int LanguageIndex(string language)
    {
        for (var i = 0; i < Languages.All.Count; i++)
            if (string.Equals(Languages.All[i].Id, language, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    /// <summary>The stored values of the chord lane preference; only "none" is translated.</summary>
    private static readonly string[] ChordLanes = ["None", "1-2", "3-4", "1-2,3-4"];

    private static int TrillCenterIndex(string value) => VisualizerSettings.TrillCenterChoices.ToList().IndexOf(value.Trim());
    private static readonly string[] JudgmentModes = ["Off", "MagicCircle", "Always"];
    // Named after the game's own difficulties; the window each one means is in VisualizerSettings.JudgmentWindowMs.
    private static readonly string[] JudgmentDifficulties = ["Lenient", "Normal", "Strict"];

    private void UpdateLanguage()
    {
        var field = _songSortField.SelectedIndex >= 0 ? _songSortField.SelectedIndex : (int)SongOrdering.Parse(_current.SongSortKey);
        _songSortField.ItemsSource = Enum.GetNames<SongSortField>().Select(name => T("SongSort." + name)).ToArray();
        _songSortField.SelectedIndex = field;
        var direction = _songSortDirection.SelectedIndex >= 0 ? _songSortDirection.SelectedIndex : (_current.SongSortHighFirst ? 0 : 1);
        _songSortDirection.ItemsSource = new[] { T("SongSortHigh"), T("SongSortLow") };
        _songSortDirection.SelectedIndex = direction;
        // The list gets its items here, after the form was loaded, so an empty list falls back to the settings.
        var group = _songGroup.SelectedIndex >= 0 ? _songGroup.SelectedIndex
            : (int)(Enum.TryParse<SongGroupField>(_current.SongGroup, ignoreCase: true, out var kind) ? kind : SongGroupField.All);
        _songGroup.ItemsSource = Enum.GetNames<SongGroupField>().Select(name => T("SongGroup." + name)).ToArray();
        _songGroup.SelectedIndex = group;
        // Index 0 is the default of each pair: the next note for a pause, the gap before a note for its tile count.
        var paused = _pauseAlign.SelectedIndex >= 0 ? _pauseAlign.SelectedIndex : _current.AlignNextNoteOnPause ? 0 : 1;
        _pauseAlign.ItemsSource = new[] { T("PausedNote.Next"), T("PausedNote.Stay") };
        _pauseAlign.SelectedIndex = paused;
        var gap = _nextInterval.SelectedIndex >= 0 ? _nextInterval.SelectedIndex : _current.BothIntervalTiles ? 2 : _current.NextIntervalTiles ? 1 : 0;
        _nextInterval.ItemsSource = new[] { T("IntervalTile.Previous"), T("IntervalTile.Next"), T("IntervalTile.Both") };
        _nextInterval.SelectedIndex = gap;
        // Index 0 follows the maximum tile count; the rest are the lane counts in order.
        var shown = _displayLanes.SelectedIndex >= 0 ? _displayLanes.SelectedIndex : LaneIndex(_current.DisplayLaneCount);
        _displayLanes.ItemsSource = new[] { T("DisplayLanes.Max") }
            .Concat(VisualizerSettings.Modes.Select(mode => mode.ToString(CultureInfo.InvariantCulture))).ToArray();
        _displayLanes.SelectedIndex = shown;
        _songListButton.Content = T("SongListOpen");
        _editSongButton.Content = T("EditSong");
        _syncButton.Content = T("Sync");
        _resyncButton.Content = T("Resync");
        var lane = _chordLane.SelectedIndex >= 0 ? _chordLane.SelectedIndex : Array.IndexOf(ChordLanes, _current.ChordLanePreference.Trim());
        _chordLane.ItemsSource = ChordLanes.Select(value => value == "None" ? T("ChordLaneNone") : value).ToArray();
        _chordLane.SelectedIndex = Math.Max(0, lane);
        var trill = _trillLane.SelectedIndex >= 0 ? _trillLane.SelectedIndex : Array.IndexOf(ChordLanes, _current.TrillLanePreference.Trim());
        _trillLane.ItemsSource = ChordLanes.Select(value => value == "None" ? T("ChordLaneNone") : value).ToArray();
        _trillLane.SelectedIndex = Math.Max(0, trill);
        var center = _trillCenter.SelectedIndex >= 0 ? _trillCenter.SelectedIndex : TrillCenterIndex(_current.TrillCenterPair);
        _trillCenter.ItemsSource = VisualizerSettings.TrillCenterChoices.Select(value => T("TrillCenter." + value)).ToArray();
        _trillCenter.SelectedIndex = Math.Max(0, center);
        // The items are filled when the language is applied, so the picked one is kept by value, not by index.
        var mode = _judgmentMode.SelectedIndex >= 0 ? _judgmentMode.SelectedIndex : Array.IndexOf(JudgmentModes, _current.JudgmentBarMode.Trim());
        _judgmentMode.ItemsSource = JudgmentModes.Select(value => T("JudgmentMode." + value)).ToArray();
        _judgmentMode.SelectedIndex = Math.Max(0, mode);
        var hard = _judgmentDifficulty.SelectedIndex >= 0 ? _judgmentDifficulty.SelectedIndex
            : Array.IndexOf(JudgmentDifficulties, _current.JudgmentBarDifficulty.Trim());
        _judgmentDifficulty.ItemsSource = JudgmentDifficulties.Select(value => T("JudgmentDifficulty." + value)).ToArray();
        _judgmentDifficulty.SelectedIndex = Math.Max(0, hard);
        foreach (var (label, key) in _labels) label.Text = T(key);
        foreach (var (hint, key) in _placeholders) hint.Text = T(key);
        foreach (var (mark, key) in _helpMarks)
            mark.ToolTip = new ToolTip { Content = new TextBlock { Text = T(key), TextWrapping = TextWrapping.Wrap, MaxWidth = 320 } };
        foreach (var star in _fieldStars.Values.Distinct()) star.ToolTip = T("ChangedMark");
        foreach (var box in new[] { _settingsKey, _previous, _next }) box.NoneText = T("Unassigned");
        foreach (var button in new[] { _clearSettingsKey, _clearPreviousKey, _clearNextKey }) button.Content = T("Clear");
        _browseFolder.Content = T("Browse");
        _browseData.Content = T("Browse");
        UpdateStates();
        UpdateDataRootPath();
        UpdateAdvancedLanguage();
        UpdateHint();
        PreviewRulesButton.Content = T("Preview");
        SearchHint.Text = T("SettingsSearch");
        SizeControls(Fields);
        ApplyButton.Content = T("Apply");
        CancelButton.Content = T("Cancel");
        HeaderText.Text = T("setting");

        ThemeSelector.ToolTip = T("ThemePick");
        RefreshThemePicker();
        UpdateEnabled();
        Errors.Text = "";
    }

    private void ApplyTheme()
    {
        // Previews immediately in this dialog; the main window follows only after Apply.
        ThemeCatalog.Apply(Resources, _theme);
        SetResourceReference(BackgroundProperty, "DialogBackdropBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        // Blank lane background boxes show the chosen theme's lane colors.
        foreach (var update in _swatchUpdates) update();
    }

    /// <summary>Fills the theme list with names in the selected language and selects the current theme.</summary>
    private void RefreshThemePicker()
    {
        _syncingTheme = true;
        try
        {
            var themes = ThemeCatalog.All;
            ThemePicker.ItemsSource = themes.Select(theme => theme.DisplayName(SelectedLanguage)).ToArray();
            var index = 0;
            for (var i = 0; i < themes.Count; i++)
                if (string.Equals(themes[i].Id, _theme, StringComparison.OrdinalIgnoreCase)) index = i;
            ThemePicker.SelectedIndex = index;
        }
        finally { _syncingTheme = false; }
    }

    private void ThemePickerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTheme || ThemePicker.SelectedIndex < 0) return;
        _theme = ThemeCatalog.All[ThemePicker.SelectedIndex].Id;
        ApplyTheme();
        RefreshThemePicker();
    }

    /// <summary>The Windows folder picker. Choosing a folder only fills the box; nothing in it is opened or written.</summary>
    private string? PickFolderWithDialog(string current)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = T("MapFolderPick"), Multiselect = false };
        if (System.IO.Directory.Exists(current)) dialog.InitialDirectory = current;
        return (Window.GetWindow(this) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FolderName : null;
    }

    private void BrowseMapFolder()
    {
        var box = _textFields[nameof(VisualizerSettings.MapRootDirectory)];
        if (PickFolder(box.Text.Trim()) is { Length: > 0 } folder) box.Text = folder;
    }

    /// <summary>Rebuilds the custom map folder. It deletes copies, so it asks first, like resetting the settings.</summary>
    private void Resync()
    {
        if (ResyncMaps is null || !Ask(T("ResyncTitle"), T("ResyncMessage"), T("Confirm"), risky: true)) return;
        ResyncMaps();
    }

    /// <summary>
    /// Opens the song list. Dragging songs there switches the order to Manual, and the choices stay pending
    /// until Apply.
    /// </summary>
    private void ShowSongList()
    {
        if (OpenSongList?.Invoke(SongPreferences) is not { } result) return;
        SongPreferences = result.Preferences;
        SongListFields = result.Fields;
        if (result.Reordered) _songSortField.SelectedIndex = (int)SongSortField.Manual;
        FormChanged();
    }

    /// <summary>The direction list means nothing for a hand-made order, so it locks (keeping its value) on Manual.</summary>
    private void UpdateSongSort() => _songSortDirection.IsEnabled = _songSortField.SelectedIndex != (int)SongSortField.Manual;

    /// <summary>Picks the app store. An empty box means the default location, so the picker starts from the store in use.</summary>
    private void BrowseDataFolder()
    {
        var box = _textFields[nameof(VisualizerSettings.DataRootDirectory)];
        var start = box.Text.Trim() is { Length: > 0 } own ? own : _dataRoot;
        if (PickFolder(start) is { Length: > 0 } folder) box.Text = folder;
    }

    private void UpdateDataRootPath() => _dataRootPath.Text = _dataRoot.Length == 0 ? "" : Languages.Format(SelectedLanguage, "DataFolderCurrent", _dataRoot);

    /// <summary>
    /// Applies the form without closing the dialog (the console's [Y/n]); the applied values become the baseline for ★ and
    /// section markers. Returns the reason when the form cannot be applied.
    /// </summary>
    /// <param name="language">The language of the reason; the form's own when null (the console asks for English).</param>
    public string? ApplyInPlace(string? language = null)
    {
        try
        {
            var settings = BuildSettings();
            SettingsApplied?.Invoke(settings);
            _current = settings;
            AppliedSettings = settings;
            Errors.Text = "";
            UpdateSectionStates();
            return null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return language is null ? error.Message : ReasonIn(language) ?? error.Message;
        }
    }

    /// <summary>Why the form cannot be applied, in another language: the same checks on the form with only the language changed.</summary>
    private string? ReasonIn(string language)
    {
        var unreadable = new HashSet<string>();
        var settings = ReadForm(errors: unreadable) with { Language = language };
        if (unreadable.Count > 0)
            return string.Join(Environment.NewLine, unreadable.Select(name => _fieldLabels.TryGetValue(name, out var label)
                ? string.Format(CultureInfo.InvariantCulture, Languages.Get(language, "NumberError"), Languages.Get(language, label))
                : Languages.Get(language, "IntegerError")));
        try
        {
            settings.EnsureValid();
            if (!ThemeCatalog.Exists(settings.Theme)) return UnknownThemeMessage(language);
            _ = ControlBindings.Create(settings);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return error.Message; }
        return null;
    }

    private void ApplyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AppliedSettings = BuildSettings();
            Leaving?.Invoke();
        }
        catch (ArgumentException error) { Errors.Text = error.Message; }
    }

    /// <summary>
    /// Asks the host to put the settings away. The panel never hides itself: whoever showed it decides how it goes, and
    /// <see cref="AppliedSettings"/> says whether this was an Apply or a plain close.
    /// </summary>
    public Action? Leaving { get; set; }

    /// <summary>
    /// Asks the host to play the preview timeline with the settings as they stand here, without applying them (user
    /// rule): the panel steps aside for it and comes back with every change still unapplied.
    /// </summary>
    public Action<VisualizerSettings>? Previewing { get; set; }

    /// <summary>The preview button. Settings that do not add up say so here instead, as Apply does.</summary>
    public void ShowPreview()
    {
        if (Previewing is not { } preview) return;
        try { preview(BuildSettings()); }
        catch (ArgumentException error) { Errors.Text = error.Message; }
    }

    /// <summary>
    /// The cancel button and Escape: leave, but only after asking when the form differs from the applied settings (as
    /// closing the old dialog did).
    /// </summary>
    public void Leave()
    {
        if (!TryLeave()) return;
        Leaving?.Invoke();
    }

    /// <summary>
    /// The editor, from the experimental card it is parked in (user rule). The settings go away first — asking about
    /// unapplied changes exactly as cancel does — because the editor works on the settings as they are applied.
    /// </summary>
    private void EditSong()
    {
        if (EditSongRequested is not { } start || !TryLeave()) return;
        Leaving?.Invoke();
        start();
    }

    /// <summary>Whether the settings may be put away now; asks about unapplied changes and false means stay.</summary>
    public bool TryLeave()
    {
        if (!HasUnappliedChanges) return true;
        return Ask(T("CancelTitle"), T("CancelMessage"), T("Close"), risky: true);
    }

    /// <summary>
    /// Puts the settings away for the console or the editor (user rule). With <paramref name="applyLanguage"/> the
    /// unapplied changes are applied first and a problem comes back in that language (the panel then stays); without
    /// it they are dropped. Null when it closed.
    /// </summary>
    public string? CloseFromOutside(string? applyLanguage)
    {
        if (applyLanguage is not null && HasUnappliedChanges && ApplyInPlace(applyLanguage) is { } error) return error;
        Leaving?.Invoke();
        return null;
    }

    /// <summary>
    /// Asks through <see cref="Confirm"/> when a test set it, otherwise with the themed dialog. A risky question (its yes
    /// throws changes away) gets the orange outline accept button.
    /// </summary>
    private bool Ask(string title, string message, string accept, bool risky = false) => Confirm is { } confirm
        ? confirm(title, message, accept)
        : Window.GetWindow(this) is { IsVisible: true } owner
            // With no window to ask over (tests, captures) the answer is no, which never throws anything away.
            && ConfirmDialog.Ask(owner, _current with { Theme = _theme, Language = SelectedLanguage }, title, message, accept, T("Cancel"), risky);
}
