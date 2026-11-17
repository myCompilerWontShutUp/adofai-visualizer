using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Visualizer.App.Input;
using Visualizer.App.Localization;
using Visualizer.App.Themes;
using Visualizer.Application.Library;
using Visualizer.Core.Settings;

namespace Visualizer.App.Views;

/// <summary>
/// Form values: loading, reading (blank = default), change marks against the applied settings, and previews.
/// A control disabled by another setting keeps its value, so undoing that setting brings the value back.
/// </summary>
public partial class SettingsPanel
{
    /// <summary>The gap-after setting under "both gaps", which the list cannot show, as the form was last loaded with it.</summary>
    private bool _keptNextInterval;

    private void LoadForm(VisualizerSettings settings)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            _language.SelectedIndex = LanguageIndex(settings.Language);
            _theme = settings.Theme;
            ApplyTheme();
            RefreshThemePicker();
            _reset.SetToken(settings.ResetKey);
            _start.SetToken(settings.StartTriggerKey);
            _toggle.SetToken(settings.VisualizerToggleKey);
            _previous.SetToken(settings.PreviousCheckpointKey);
            _next.SetToken(settings.NextCheckpointKey);
            _settingsKey.SetToken(settings.SettingsHotkey);
            var laneTokens = settings.GetLaneKeyTokens();
            for (var i = 0; i < _laneBoxes.Length; i++) _laneBoxes[i].SetToken(i < laneTokens.Count ? laneTokens[i] : null);
            _foreground.IsChecked = settings.OnlyReactWhenAdofaiForeground;
            _guide.IsChecked = settings.ShowGuideButtons;
            _songInfo.IsChecked = settings.ShowSongInfo;
            _inputEffect.IsChecked = settings.ShowInputEffect;
            _missEffect.IsChecked = settings.ShowMissEffect;
            _maximum.SelectedItem = settings.MaximumTileCount;
            _minimum.SelectedItem = settings.MinimumTileCount;
            _displayLanes.SelectedIndex = LaneIndex(settings.DisplayLaneCount);
            _dynamic.IsChecked = settings.DynamicTiming;
            _tilePreference.IsChecked = settings.EnableTilePreference;
            _extraLanes.IsChecked = settings.ExtraLanes;
            _judgmentSlowdown.IsChecked = settings.SlowNotesPastJudgmentLine;
            _immediateDownshift.IsChecked = settings.ImmediateDownshift;
            _experimental.IsChecked = settings.ExperimentalFeatures;
            _chordLanes.IsChecked = settings.ChordSectionLanes;
            _chordCenter.IsChecked = settings.NoChordCenterPair;
            _pauseAlign.SelectedIndex = settings.AlignNextNoteOnPause ? 0 : 1;
            _keepSpeed.IsChecked = settings.KeepSpeedOnLoad;
            _chordLane.SelectedIndex = Math.Max(0, Array.IndexOf(ChordLanes, settings.ChordLanePreference.Trim()));
            _trillCenter.SelectedIndex = Math.Max(0, TrillCenterIndex(settings.TrillCenterPair));
            _trillOne.IsChecked = settings.TrillAsOneNote;
            _chordsAboveFour.IsChecked = settings.ChordsOnlyAboveFourKeys;
            _trillLane.SelectedIndex = Math.Max(0, Array.IndexOf(ChordLanes, settings.TrillLanePreference.Trim()));
            _judgmentMode.SelectedIndex = Math.Max(0, Array.IndexOf(JudgmentModes, settings.JudgmentBarMode.Trim()));
            _judgmentDifficulty.SelectedIndex = Math.Max(0, Array.IndexOf(JudgmentDifficulties, settings.JudgmentBarDifficulty.Trim()));
            _judgmentAverage.IsChecked = settings.ShowJudgmentAverage;
            // Not a row of its own: the song list window edits it, so the dialog only carries the value through Apply.
            SongListFields = string.Join(' ', settings.GetSongListFields());
            _songGroup.SelectedIndex = Enum.TryParse<SongGroupField>(settings.SongGroup, ignoreCase: true, out var group) ? (int)group : (int)SongGroupField.All;
            _songSortField.SelectedIndex = (int)SongOrdering.Parse(settings.SongSortKey);
            _songSortDirection.SelectedIndex = settings.SongSortHighFirst ? 0 : 1;
            UpdateSongSort();
            _hidePractice.IsChecked = settings.HidePracticeMaps;
            _magicFixedTiles.IsChecked = settings.MagicCircleFixedTiles;
            _magicChords.IsChecked = settings.MagicCircleChords;
            _magicTiles.SelectedItem = settings.MagicCircleTileCount;
            _autoCheckpointBox.IsChecked = settings.AutoCheckpointAdvance;
            _autoCheckpointLoad.IsChecked = settings.AutoCheckpointOnLoad;
            _autoSave.IsChecked = settings.AutoSaveSettings;
            _nextInterval.SelectedIndex = settings.BothIntervalTiles ? 2 : settings.NextIntervalTiles ? 1 : 0;
            _keptNextInterval = settings.NextIntervalTiles;
            _chordBefore.IsChecked = settings.ChordAvoidBeforeLanes;
            _runRestart.IsChecked = settings.RestartPatternOnRun;
            _enhancedChords.IsChecked = settings.EnhancedChordRules;
            _oneHandChords.IsChecked = settings.OneHandFourChords;
            foreach (var (name, box) in _textFields) box.Text = ValueText(settings, name);
            foreach (var (name, box) in _orderBoxes)
            {
                // While the preference is off the boxes show the fixed default order and keep the own order aside.
                box.Tag = settings.EnableTilePreference ? null : ValueText(settings, name);
                box.Text = settings.EnableTilePreference ? ValueText(settings, name) : ValueText(Defaults, name);
                box.IsEnabled = settings.EnableTilePreference;
            }
            FillLaneColors(_laneBackgroundBoxes, settings.LaneBackgroundColors);
            UpdateEnabled();
        }
        finally { _loading = wasLoading; }
        FormChanged();
    }

    private static void FillLaneColors(TextBox[] boxes, string colors)
    {
        var tokens = ColorText.SplitList(colors);
        for (var i = 0; i < boxes.Length; i++) boxes[i].Text = i < tokens.Count ? tokens[i] : "";
    }

    private void UpdateTilePreference()
    {
        var enabled = _tilePreference.IsChecked == true;
        foreach (var (name, box) in _orderBoxes)
        {
            if (box.IsEnabled == enabled) continue;
            if (enabled)
            {
                if (box.Tag is string own) box.Text = own;
                box.Tag = null;
            }
            else
            {
                box.Tag = box.Text;
                box.Text = ValueText(Defaults, name);
            }
            box.IsEnabled = enabled;
        }
    }

    private void UpdateEnabled()
    {
        var policy = new VisualizerSettings
        {
            DynamicTiming = _dynamic.IsChecked == true,
            MaximumTileCount = _maximum.SelectedItem is int maximum ? maximum : 1,
            MinimumTileCount = _minimum.SelectedItem is int minimum ? minimum : 1
        };
        _textFields[nameof(VisualizerSettings.EightKeyBoundaryMs)].IsEnabled = policy.IsThresholdEnabled(8);
        _textFields[nameof(VisualizerSettings.SixKeyBoundaryMs)].IsEnabled = policy.IsThresholdEnabled(6);
        _textFields[nameof(VisualizerSettings.FourKeyBoundaryMs)].IsEnabled = policy.IsThresholdEnabled(4);
        _textFields[nameof(VisualizerSettings.TwoKeyBoundaryMs)].IsEnabled = policy.IsThresholdEnabled(2);
        // Extra lanes apply to 2- and 4-lane views only; the switch keeps its value while locked.
        _extraLanes.IsEnabled = policy.CanUseExtraLanes;
        _extraLanes.ToolTip = policy.CanUseExtraLanes ? null : T("ExtraLanesUnavailable");
        // The wait matters only while immediate switching is on; the box keeps its value while locked.
        _textFields[nameof(VisualizerSettings.DownshiftNoteCount)].IsEnabled = _immediateDownshift.IsChecked == true;
        // The magic circle rows matter only while the bar can appear in one, which its own card now decides.
        var magic = _judgmentMode.SelectedIndex != 0;
        foreach (var field in new[] { nameof(VisualizerSettings.MagicCircleNoteCount), nameof(VisualizerSettings.MagicCircleMaxIntervalMs),
                     nameof(VisualizerSettings.MagicCircleFadeInMs), nameof(VisualizerSettings.MagicCircleLeadMs),
                     nameof(VisualizerSettings.MagicCircleFadeOutMs) })
            _textFields[field].IsEnabled = magic;
        _magicFixedTiles.IsEnabled = magic;
        _magicChords.IsEnabled = magic;
        // The count matters only while the arrow is drawn; the box keeps its value while locked.
        _textFields[nameof(VisualizerSettings.JudgmentAverageCount)].IsEnabled = _judgmentAverage.IsChecked == true;
        _magicTiles.IsEnabled = magic && _magicFixedTiles.IsChecked == true;
        // Only tile counts the maximum allows can be held.
        _magicTiles.ItemsSource = VisualizerSettings.Modes.Where(mode => mode <= policy.MaximumTileCount).ToArray();
        // The experimental card stays on screen while the switch in Advanced is off and only stops taking input (user
        // rule): hiding it took everything in the card away with it, the editor button included. The header stays live so
        // the card can still be opened and read, and the values are kept either way.
        if (_experimentalSection is { } experimental && experimental.Card.Child is Expander card
            && card.Content is UIElement rows)
            rows.IsEnabled = _experimental.IsChecked == true;
    }

    /// <summary>
    /// The short explanation beside Apply. A focused input wins over the pointer, which picks the row it is level with;
    /// with neither, the note tells what settings do in general.
    /// </summary>
    private void UpdateHint()
    {
        var field = _focusedField ?? _hoveredField;
        if (field is null || !_fieldLabels.TryGetValue(field, out var label))
        {
            SettingsNote.Text = T("SettingsNote");
            return;
        }
        // A missing hint returns its own key, in which case the label already says what the row is.
        var key = "Hint." + field;
        var hint = UiText.Get(SelectedLanguage, key);
        SettingsNote.Text = hint == key ? T(label) : hint;
    }

    private void SetHoveredField(string? field)
    {
        if (field == _hoveredField) return;
        _hoveredField = field;
        UpdateHint();
    }

    private void SetFocusedField(string? field)
    {
        if (field == _focusedField) return;
        _focusedField = field;
        UpdateHint();
    }

    /// <summary>The setting of the visible row the pointer is level with, ignoring how far left or right it sits.</summary>
    private string? FieldAt(Point point)
    {
        foreach (var (row, field) in _rowFields)
        {
            if (!row.IsVisible) continue;
            double top;
            try { top = row.TransformToAncestor(Fields).Transform(new Point(0, 0)).Y; }
            catch (InvalidOperationException) { continue; }
            if (point.Y >= top && point.Y <= top + row.ActualHeight) return field;
        }
        return null;
    }

    private string? FieldOf(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement element && _rowFields.TryGetValue(element, out var field)) return field;
        return null;
    }

    private void FormChanged()
    {
        if (_loading) return;
        UpdateSectionStates();
        RaisePreview();
    }

    /// <summary>
    /// Marks every setting that differs from the applied settings with ★, and every section: an invalid value (red) wins over
    /// a change (orange) over no change (sky blue). After Apply the dialog closes, so reopening shows everything unmarked.
    /// </summary>
    private void UpdateSectionStates()
    {
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var settings = ReadForm(fillBlanks: false, errors);
        foreach (var error in settings.ValidateFields()) errors.Add(error.Field);
        if (errors.Count == 0)
        {
            try { _ = ControlBindings.Create(settings); }
            catch (ControlKeyException error) { errors.Add(error.Field); }
            catch (ArgumentException) { }
        }
        var changed = _fieldLabels.Keys.Where(field => errors.Contains(field) || !SameValue(settings, _current, field)).ToHashSet(StringComparer.Ordinal);
        foreach (var star in _fieldStars.Values.Distinct())
            star.Visibility = _fieldStars.Any(pair => ReferenceEquals(pair.Value, star) && changed.Contains(pair.Key)) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var section in _sections)
        {
            section.State = section.Fields.Any(errors.Contains) ? "error" : section.Fields.Any(changed.Contains) ? "changed" : "default";
            section.Marker.SetResourceReference(Shape.FillProperty, section.State switch
            {
                "error" => "ErrorBrush",
                "changed" => "FireBrush",
                _ => "AccentBrush"
            });
            section.Card.SetResourceReference(Border.BorderBrushProperty, section.State == "error" ? "ErrorBrush" : "LineBrush");
        }
        // Apply looks like any other button until something is actually waiting to be applied.
        ApplyButton.SetResourceReference(StyleProperty, HasUnappliedChanges ? "PrimaryButton" : "ToolButton");
    }

    /// <summary>Compares one setting as the user sees it: tile sets in any order, colors in any accepted form, language ids in any case.</summary>
    private static bool SameValue(VisualizerSettings form, VisualizerSettings applied, string field)
    {
        var a = ValueText(form, field);
        var b = ValueText(applied, field);
        return field switch
        {
            nameof(VisualizerSettings.EightKeyPattern) or nameof(VisualizerSettings.FourKeyPattern) or nameof(VisualizerSettings.TwoKeyPattern)
                => VisualizerSettings.SortTiles(a) == VisualizerSettings.SortTiles(b),
            nameof(VisualizerSettings.Language) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase),
            nameof(VisualizerSettings.LaneBackgroundColors) => ColorList(a) == ColorList(b),
            _ when field.EndsWith("Color", StringComparison.Ordinal) => (ColorText.Normalize(a) ?? a) == (ColorText.Normalize(b) ?? b),
            _ => a == b
        };
    }

    private static string ColorList(string text) => string.Join(" ", ColorText.SplitList(text).Select(token => ColorText.Normalize(token) ?? token));

    /// <summary>Sends the edited effects and colors; an unreadable color keeps the applied one.</summary>
    private void RaisePreview()
    {
        if (Preview is not { } preview) return;
        string Color(string name)
        {
            var text = _textFields[name].Text;
            return ColorText.NormalizeRgb(text) ?? (text.Trim().Length == 0 ? ValueText(Defaults, name) : ValueText(_current, name));
        }
        double Scale(string name)
        {
            var text = _textFields[name].Text.Trim();
            if (text.Length == 0) return (double)typeof(VisualizerSettings).GetProperty(name)!.GetValue(Defaults)!;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && value >= VisualizerSettings.MinimumNoteScale && value <= VisualizerSettings.MaximumNoteScale
                ? value : (double)typeof(VisualizerSettings).GetProperty(name)!.GetValue(_current)!;
        }
        var width = Scale(nameof(VisualizerSettings.NoteWidth));
        preview(_current with
        {
            ShowInputEffect = _inputEffect.IsChecked == true,
            ShowMissEffect = _missEffect.IsChecked == true,
            NoteColor = Color(nameof(VisualizerSettings.NoteColor)),
            ChordColor = Color(nameof(VisualizerSettings.ChordColor)),
            TrillColor = Color(nameof(VisualizerSettings.TrillColor)),
            JudgmentLineColor = Color(nameof(VisualizerSettings.JudgmentLineColor)),
            MissEffectColor = Color(nameof(VisualizerSettings.MissEffectColor)),
            CheckpointColor = Color(nameof(VisualizerSettings.CheckpointColor)),
            StartNoteColor = Color(nameof(VisualizerSettings.StartNoteColor)),
            EndNoteColor = Color(nameof(VisualizerSettings.EndNoteColor)),
            PressedNoteTransparencyPercent = Percent(nameof(VisualizerSettings.PressedNoteTransparencyPercent)),
            LaneBackgroundColors = LaneBackgroundText(fillBlanks: false),
            LaneEffectColor = Color(nameof(VisualizerSettings.LaneEffectColor)),
            NoteThickness = Scale(nameof(VisualizerSettings.NoteThickness)),
            NoteWidth = width,
            // A long note is never wider than a note, so the preview keeps it inside the width being edited.
            HoldNoteWidth = Math.Min(width, Scale(nameof(VisualizerSettings.HoldNoteWidth)))
        });

        double Percent(string name)
        {
            var text = _textFields[name].Text.Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 100
                ? value : _current.PressedNoteTransparencyPercent;
        }
    }

    /// <summary>
    /// The lane background setting from its boxes: empty when every box is empty (follow the theme); otherwise eight colors, with an
    /// empty box taking the theme's color for that lane. An unreadable entry stays one invalid token for validation.
    /// </summary>
    private string LaneBackgroundText(bool fillBlanks)
    {
        var boxes = _laneBackgroundBoxes;
        if (boxes.All(box => box.Text.Trim().Length == 0)) return "";
        var theme = ThemeCatalog.LaneBackgrounds(_theme);
        return string.Join(" ", boxes.Select((box, lane) =>
        {
            var text = box.Text.Trim();
            if (text.Length > 0) return ColorText.Normalize(text) ?? text.Replace(' ', '_');
            var color = theme[lane];
            var own = ColorText.ToHex(((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B);
            if (fillBlanks) box.Text = own;
            return own;
        }));
    }

    private static string ValueText(VisualizerSettings settings, string name) =>
        Convert.ToString(typeof(VisualizerSettings).GetProperty(name)!.GetValue(settings), CultureInfo.InvariantCulture) ?? "";

    /// <summary>The trimmed field text; a blank field means its default, which is written back into the box when asked.</summary>
    private string TextOf(string name, bool fillBlanks)
    {
        var box = _textFields[name];
        var text = box.Text.Trim();
        if (text.Length > 0) return text;
        var fallback = ValueText(Defaults, name);
        if (fillBlanks) box.Text = fallback;
        return fallback;
    }

    private VisualizerSettings? TryReadForm()
    {
        try
        {
            var settings = ReadForm(fillBlanks: false);
            return settings.Validate().Count == 0 ? settings : null;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Builds settings from the form. Without <paramref name="errors"/> an unreadable number throws; with it the setting is
    /// recorded there and its default used, so section markers can be computed while typing.
    /// </summary>
    private VisualizerSettings ReadForm(bool fillBlanks = true, ISet<string>? errors = null)
    {
        string Text(string name) => TextOf(name, fillBlanks);
        double Number(string name)
        {
            if (double.TryParse(Text(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return value;
            if (errors is null) throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, T("NumberError"), T(_fieldLabels[name])));
            errors.Add(name);
            return (double)typeof(VisualizerSettings).GetProperty(name)!.GetValue(Defaults)!;
        }
        string Color(string name)
        {
            var text = Text(name);
            return ColorText.NormalizeRgb(text) ?? text;
        }
        int Whole(string name)
        {
            if (int.TryParse(Text(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
            if (errors is null) throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, T("NumberError"), T(_fieldLabels[name])));
            errors.Add(name);
            return (int)typeof(VisualizerSettings).GetProperty(name)!.GetValue(Defaults)!;
        }
        // Pattern boxes hold tile sets: stored ascending, and shown that way once the form is built for Apply.
        string Tiles(string name)
        {
            var sorted = VisualizerSettings.SortTiles(Text(name));
            if (fillBlanks && _textFields[name].Text != sorted) _textFields[name].Text = sorted;
            return sorted;
        }
        if (!int.TryParse(Text(nameof(VisualizerSettings.InitialTile)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var initial))
        {
            if (errors is null) throw new ArgumentException(T("IntegerError"));
            errors.Add(nameof(VisualizerSettings.InitialTile));
            initial = Defaults.InitialTile;
        }
        var preference = _tilePreference.IsChecked == true;
        // A locked order box keeps the user's own order aside (in its Tag), so turning the preference back on restores it.
        string Order(string name)
        {
            if (preference) return Text(name);
            var own = (_orderBoxes[name].Tag as string)?.Trim();
            return string.IsNullOrEmpty(own) ? ValueText(Defaults, name) : own;
        }
        return new VisualizerSettings
        {
            Language = SelectedLanguage,
            Theme = _theme,
            MapRootDirectory = Text(nameof(VisualizerSettings.MapRootDirectory)),
            DataRootDirectory = Text(nameof(VisualizerSettings.DataRootDirectory)).Trim(),
            SongGroup = Enum.GetName((SongGroupField)Math.Clamp(_songGroup.SelectedIndex, 0, Enum.GetValues<SongGroupField>().Length - 1))!,
            SongSortKey = Enum.GetName((SongSortField)Math.Clamp(_songSortField.SelectedIndex, 0, Enum.GetValues<SongSortField>().Length - 1))!,
            SongSortHighFirst = _songSortDirection.SelectedIndex != 1,
            SongListFields = SongListFields,
            HidePracticeMaps = _hidePractice.IsChecked == true,
            ShowGuideButtons = _guide.IsChecked == true,
            ShowSongInfo = _songInfo.IsChecked == true,
            ResetKey = _reset.Token ?? "",
            StartTriggerKey = _start.Token ?? "",
            VisualizerToggleKey = _toggle.Token ?? "",
            PreviousCheckpointKey = _previous.Token ?? "",
            NextCheckpointKey = _next.Token ?? "",
            SettingsHotkey = _settingsKey.Token,
            LaneKeys = string.Join(" ", _laneBoxes.Select(box => box.Token ?? "?")),
            OnlyReactWhenAdofaiForeground = _foreground.IsChecked == true,
            AdofaiProcessName = Text(nameof(VisualizerSettings.AdofaiProcessName)),
            AutoCheckpointAdvance = _autoCheckpointBox.IsChecked == true,
            AutoCheckpointOnLoad = _autoCheckpointLoad.IsChecked == true,
            AutoSaveSettings = _autoSave.IsChecked == true,
            // Both gaps sit over the other choice, which keeps the value last loaded (an import too) for when the box goes back to it.
            NextIntervalTiles = _nextInterval.SelectedIndex == 1 || (_nextInterval.SelectedIndex == 2 && _keptNextInterval),
            BothIntervalTiles = _nextInterval.SelectedIndex == 2,
            ChordAvoidBeforeLanes = _chordBefore.IsChecked == true,
            RestartPatternOnRun = _runRestart.IsChecked == true,
            EnhancedChordRules = _enhancedChords.IsChecked == true,
            OneHandFourChords = _oneHandChords.IsChecked == true,
            MaximumTileCount = _maximum.SelectedItem is int maximum ? maximum : 0,
            MinimumTileCount = _minimum.SelectedItem is int minimum ? minimum : 0,
            DisplayLaneCount = _displayLanes.SelectedIndex <= 0 ? 0 : VisualizerSettings.Modes[_displayLanes.SelectedIndex - 1],
            DynamicTiming = _dynamic.IsChecked == true,
            SimultaneousWindowMs = Number(nameof(VisualizerSettings.SimultaneousWindowMs)),
            TrillWindowMs = Number(nameof(VisualizerSettings.TrillWindowMs)),
            ImmediateDownshift = _immediateDownshift.IsChecked == true,
            DownshiftNoteCount = Whole(nameof(VisualizerSettings.DownshiftNoteCount)),
            StartDelayMs = Number(nameof(VisualizerSettings.StartDelayMs)),
            PlaybackSpeedPercent = Number(nameof(VisualizerSettings.PlaybackSpeedPercent)),
            EightKeyBoundaryMs = Number(nameof(VisualizerSettings.EightKeyBoundaryMs)),
            SixKeyBoundaryMs = Number(nameof(VisualizerSettings.SixKeyBoundaryMs)),
            FourKeyBoundaryMs = Number(nameof(VisualizerSettings.FourKeyBoundaryMs)),
            TwoKeyBoundaryMs = Number(nameof(VisualizerSettings.TwoKeyBoundaryMs)),
            NoteTravelTimeMs = Number(nameof(VisualizerSettings.NoteTravelTimeMs)),
            JudgmentLineHeightDip = Number(nameof(VisualizerSettings.JudgmentLineHeightDip)),
            KeyPressWindowMs = Number(nameof(VisualizerSettings.KeyPressWindowMs)),
            CalibrationNoteCount = Whole(nameof(VisualizerSettings.CalibrationNoteCount)),
            // Kept so an older settings file still reads; the bar is switched by JudgmentBarMode now.
            MagicCircleJudgmentLine = _judgmentMode.SelectedIndex != 0,
            ExperimentalFeatures = _experimental.IsChecked == true,
            ChordSectionLanes = _chordLanes.IsChecked == true,
            NoChordCenterPair = _chordCenter.IsChecked == true,
            KeepSpeedOnLoad = _keepSpeed.IsChecked == true,
            AlignNextNoteOnPause = _pauseAlign.SelectedIndex != 1,
            ChordLanePreference = ChordLanes[Math.Clamp(_chordLane.SelectedIndex, 0, ChordLanes.Length - 1)],
            TrillCenterPair = VisualizerSettings.TrillCenterChoices[Math.Clamp(_trillCenter.SelectedIndex, 0, VisualizerSettings.TrillCenterChoices.Count - 1)],
            TrillAsOneNote = _trillOne.IsChecked == true,
            ChordsOnlyAboveFourKeys = _chordsAboveFour.IsChecked == true,
            TrillLanePreference = ChordLanes[Math.Clamp(_trillLane.SelectedIndex, 0, ChordLanes.Length - 1)],
            JudgmentBarMode = JudgmentModes[Math.Clamp(_judgmentMode.SelectedIndex, 0, JudgmentModes.Length - 1)],
            JudgmentBarDifficulty = JudgmentDifficulties[Math.Clamp(_judgmentDifficulty.SelectedIndex, 0, JudgmentDifficulties.Length - 1)],
            ShowJudgmentAverage = _judgmentAverage.IsChecked == true,
            JudgmentAverageCount = Whole(nameof(VisualizerSettings.JudgmentAverageCount)),
            JudgmentEaseScale = Number(nameof(VisualizerSettings.JudgmentEaseScale)),
            MagicCircleFixedTiles = _magicFixedTiles.IsChecked == true,
            MagicCircleChords = _magicChords.IsChecked == true,
            MagicCircleTileCount = _magicTiles.SelectedItem is int magicTiles ? magicTiles : Defaults.MagicCircleTileCount,
            MagicCircleNoteCount = Whole(nameof(VisualizerSettings.MagicCircleNoteCount)),
            MagicCircleMaxIntervalMs = Number(nameof(VisualizerSettings.MagicCircleMaxIntervalMs)),
            MagicCircleFadeInMs = Number(nameof(VisualizerSettings.MagicCircleFadeInMs)),
            MagicCircleLeadMs = Number(nameof(VisualizerSettings.MagicCircleLeadMs)),
            MagicCircleFadeOutMs = Number(nameof(VisualizerSettings.MagicCircleFadeOutMs)),
            NoteThickness = Number(nameof(VisualizerSettings.NoteThickness)),
            NoteWidth = Number(nameof(VisualizerSettings.NoteWidth)),
            HoldNoteWidth = Number(nameof(VisualizerSettings.HoldNoteWidth)),
            PressedNoteTransparencyPercent = Number(nameof(VisualizerSettings.PressedNoteTransparencyPercent)),
            GlobalDisplayOffsetMs = Number(nameof(VisualizerSettings.GlobalDisplayOffsetMs)),
            EightKeyPattern = Tiles(nameof(VisualizerSettings.EightKeyPattern)),
            SixKeyPattern = Tiles(nameof(VisualizerSettings.SixKeyPattern)),
            FourKeyPattern = Tiles(nameof(VisualizerSettings.FourKeyPattern)),
            TwoKeyPattern = Tiles(nameof(VisualizerSettings.TwoKeyPattern)),
            ExtraLanes = _extraLanes.IsChecked == true,
            SlowNotesPastJudgmentLine = _judgmentSlowdown.IsChecked == true,
            EnableTilePreference = preference,
            TwoKeyOrder = Order(nameof(VisualizerSettings.TwoKeyOrder)),
            FourKeyOrder = Order(nameof(VisualizerSettings.FourKeyOrder)),
            SixKeyOrder = Order(nameof(VisualizerSettings.SixKeyOrder)),
            EightKeyOrder = Order(nameof(VisualizerSettings.EightKeyOrder)),
            InitialTile = initial,
            ShowInputEffect = _inputEffect.IsChecked == true,
            ShowMissEffect = _missEffect.IsChecked == true,
            NoteColor = Color(nameof(VisualizerSettings.NoteColor)),
            ChordColor = Color(nameof(VisualizerSettings.ChordColor)),
            TrillColor = Color(nameof(VisualizerSettings.TrillColor)),
            JudgmentLineColor = Color(nameof(VisualizerSettings.JudgmentLineColor)),
            MissEffectColor = Color(nameof(VisualizerSettings.MissEffectColor)),
            CheckpointColor = Color(nameof(VisualizerSettings.CheckpointColor)),
            StartNoteColor = Color(nameof(VisualizerSettings.StartNoteColor)),
            EndNoteColor = Color(nameof(VisualizerSettings.EndNoteColor)),
            LaneBackgroundColors = LaneBackgroundText(fillBlanks),
            LaneEffectColor = Color(nameof(VisualizerSettings.LaneEffectColor))
        };
    }
}
