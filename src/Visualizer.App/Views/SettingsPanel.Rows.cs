using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Visualizer.App.Themes;
using Visualizer.Core.Settings;

namespace Visualizer.App.Views;

/// <summary>Builders for section cards and rows: labels with a ★ change mark and a "?" help badge, color boxes and key grids.</summary>
public partial class SettingsPanel
{
    /// <summary>Starts a collapsible card. Open by default except where stated; the open state comes from the layout store.</summary>
    private void AddSection(string key, string? help = null, bool expandedByDefault = true)
    {
        var marker = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        marker.SetResourceReference(Shape.FillProperty, "AccentBrush");
        // Larger than a setting's name, smaller than the panel's own heading (user rule).
        var title = new TextBlock { FontSize = SectionFontSize, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _labels[title] = key;
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(marker);
        header.Children.Add(title);
        if (help is not null) header.Children.Add(HelpMark(help, "TextBrush"));
        _section = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var expander = new Expander { Header = header, Content = _section, IsExpanded = _layout.IsExpanded(key) ?? expandedByDefault };
        expander.SetResourceReference(ForegroundProperty, "MutedTextBrush");
        // A card the search opened is not a card the user opened, so its own state is left as it was (user rule).
        expander.Expanded += (_, _) => { if (!_searching) _layout.SetExpanded(key, true); };
        expander.Collapsed += (_, _) => { if (!_searching) _layout.SetExpanded(key, false); };
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 6),
            Margin = new Thickness(0, 0, 0, 10),
            Child = expander
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        Fields.Children.Add(card);
        _currentSection = new Section(key, marker, card);
        _sections.Add(_currentSection);
    }

    /// <summary>How tall a button, box or dropdown in a settings row is: the settings write smaller than the rest of the app (user rule).</summary>
    public const double RowControlHeight = 26;
    private const double RowFontSize = 11;
    /// <summary>The name of a setting, which reads a little larger than its value (user request).</summary>
    public const double LabelFontSize = 12.5;
    /// <summary>A section's heading: above the names of the settings in it, below the panel's own heading (17).</summary>
    public const double SectionFontSize = 14.5;
    private const double RowGap = 6;

    /// <summary>
    /// Gives every button, box and dropdown in the panel that height and that text size. Done in one pass once the rows
    /// are built, since they take both from the shared styles, which the main window uses at its own larger size — a
    /// smaller MinHeight alone changed nothing, because the style's own MinHeight and FontSize still decided (user report:
    /// only the letters had shrunk).
    /// </summary>
    private void SizeControls(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is FrameworkElement element and (ComboBox or TextBox or (ButtonBase and not CheckBox)))
            {
                // A box meant to hold several lines (the JSON one) keeps the height it was given.
                var lines = element is TextBox { AcceptsReturn: true };
                if (!lines && element.ReadLocalValue(HeightProperty) == DependencyProperty.UnsetValue)
                {
                    element.Height = RowControlHeight;
                    element.MinHeight = RowControlHeight;
                }
                if (element is Control control)
                {
                    control.FontSize = RowFontSize;
                    // The sides are what the style left room for (the dropdown arrow); only top and bottom go.
                    if (!lines) control.Padding = new Thickness(control.Padding.Left, 0, control.Padding.Right, 0);
                }
            }
            SizeControls(child);
        }
    }

    /// <summary>Whether the cards are being opened by the search, so their own open state is not written over.</summary>
    private bool _searching;

    /// <summary>
    /// Shows only the rows whose words contain what was typed (user rule). A card with nothing left is hidden, the rest
    /// open while the search lasts, and clearing the box puts every card back the way it was left.
    /// </summary>
    public void Search(string? text)
    {
        var wanted = (text ?? "").Trim();
        SearchNone.Visibility = Visibility.Collapsed;
        SearchHint.Visibility = wanted.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searching = true;
        try
        {
            var found = 0;
            foreach (var section in _sections)
            {
                var shown = 0;
                foreach (var row in Rows(section))
                {
                    var matches = wanted.Length == 0 || Words(row).Contains(wanted, StringComparison.CurrentCultureIgnoreCase);
                    row.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                    if (matches) shown++;
                }
                // Experimental rows are only worth finding while the experiments are on (user rule); with the box empty
                // the card follows the switch in Advanced, as it always has.
                var hiddenExperiment = section.Key == "SectionExperimental" && _experimental.IsChecked != true;
                if (hiddenExperiment) shown = 0;
                section.Card.Visibility = hiddenExperiment || (wanted.Length > 0 && shown == 0) ? Visibility.Collapsed : Visibility.Visible;
                found += shown;
                if (section.Card.Child is not Expander expander) continue;
                expander.IsExpanded = wanted.Length > 0 ? shown > 0 : _layout.IsExpanded(section.Key) ?? true;
            }
            if (wanted.Length > 0 && found == 0)
            {
                SearchNone.Text = T("SettingsSearchNone");
                SearchNone.Visibility = Visibility.Visible;
            }
        }
        finally { _searching = false; }
    }

    /// <summary>The cards in the order they were built, for the tests that look at one of them.</summary>
    public IReadOnlyList<string> SectionKeys => _sections.Select(section => section.Key).ToArray();

    /// <summary>The rows of a card, which are the children its fields were added to.</summary>
    private static IEnumerable<FrameworkElement> Rows(Section section) =>
        section.Card.Child is Expander { Content: Panel panel } ? panel.Children.OfType<FrameworkElement>() : [];

    /// <summary>Everything written on a row: its label, the state word beside a toggle, and what its "?" explains.</summary>
    private static string Words(DependencyObject row)
    {
        var words = new System.Text.StringBuilder();
        foreach (var child in LogicalTreeHelper.GetChildren(row).OfType<DependencyObject>())
        {
            if (child is TextBlock text) words.Append(text.Text).Append(' ');
            if (child is FrameworkElement { ToolTip: string tip }) words.Append(tip).Append(' ');
            if (child is ContentControl { Content: string content }) words.Append(content).Append(' ');
            words.Append(Words(child));
        }
        return words.ToString();
    }

    private void AddField(string label, FrameworkElement input, string? field = null, string? help = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, RowGap) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var labelBlock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            // The name of a setting reads a little larger than the value beside it (user request).
            FontSize = LabelFontSize,
            MaxWidth = LabelWidth - (help is null ? 12 : 33) - 10
        };
        labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _labels[labelBlock] = label;
        // A small star at the top right of the label marks a value that differs from the applied one.
        var star = new TextBlock
        {
            Text = "★",
            FontSize = 8,
            Margin = new Thickness(1, -3, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed
        };
        star.SetResourceReference(TextBlock.ForegroundProperty, "ChangedBrush");
        var labelCell = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        labelCell.Children.Add(labelBlock);
        labelCell.Children.Add(star);
        if (help is not null) labelCell.Children.Add(HelpMark(help, "MutedTextBrush"));
        row.Children.Add(labelCell);
        input.MinHeight = RowControlHeight;
        input.VerticalAlignment = VerticalAlignment.Center;
        // A toggle says what it is doing right now in a small, muted word beside it.
        var cell = input is CheckBox toggle && field is not null ? WithState(toggle, field) : input;
        Grid.SetColumn(cell, 1);
        row.Children.Add(cell);
        CurrentSection.Children.Add(row);
        if (field is null) return;
        _rowFields[row] = field;
        _fieldLabels[field] = label;
        _fieldStars[field] = star;
        _currentSection?.Fields.Add(field);
    }

    /// <summary>
    /// The toggle with its state word beside it. The word never repeats the setting name and says, in a word or two, what
    /// the state does (plain on/off tells too little).
    /// </summary>
    private FrameworkElement WithState(CheckBox toggle, string field)
    {
        var state = new TextBlock
        {
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        state.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _stateTexts[field] = (toggle, state);
        toggle.Checked += (_, _) => UpdateState(field);
        toggle.Unchecked += (_, _) => UpdateState(field);
        var cell = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        cell.Children.Add(toggle);
        cell.Children.Add(state);
        return cell;
    }

    /// <summary>Setting → the pair of words for its two states. A toggle not listed falls back to plain on/off.</summary>
    private static readonly Dictionary<string, (string On, string Off)> StateWords = new(StringComparer.Ordinal)
    {
        [nameof(VisualizerSettings.ShowGuideButtons)] = ("StateShown", "StateHidden"),
        [nameof(VisualizerSettings.ShowSongInfo)] = ("StateShown", "StateHidden"),
        [nameof(VisualizerSettings.OnlyReactWhenAdofaiForeground)] = ("StateScopeGame", "StateScopeAny"),
        [nameof(VisualizerSettings.EnableTilePreference)] = ("StateOrderOwn", "StateOrderDefault"),
        [nameof(VisualizerSettings.ImmediateDownshift)] = ("StateDownshiftNow", "StateDownshiftCycle"),
        [nameof(VisualizerSettings.DynamicTiming)] = ("StateDynamicOn", "StateDynamicOff"),
        [nameof(VisualizerSettings.AutoCheckpointAdvance)] = ("StateAutoCheckpointOn", "StateAutoCheckpointOff"),
        [nameof(VisualizerSettings.AutoCheckpointOnLoad)] = ("StateAutoOnLoadOn", "StateAutoOnLoadOff"),
        [nameof(VisualizerSettings.AutoSaveSettings)] = ("StateAutoSaveOn", "StateAutoSaveOff"),
        [nameof(VisualizerSettings.ShowJudgmentAverage)] = ("StateAverageOn", "StateAverageOff"),
        [nameof(VisualizerSettings.ChordAvoidBeforeLanes)] = ("StateChordBeforeOn", "StateChordBeforeOff"),
        [nameof(VisualizerSettings.RestartPatternOnRun)] = ("StateRunRestartOn", "StateRunRestartOff"),
        [nameof(VisualizerSettings.EnhancedChordRules)] = ("StateEnhancedChordsOn", "StateEnhancedChordsOff"),
        [nameof(VisualizerSettings.MagicCircleFixedTiles)] = ("StateFixedTilesOn", "StateFixedTilesOff"),
        [nameof(VisualizerSettings.MagicCircleChords)] = ("StateMagicChordsOn", "StateMagicChordsOff"),
        [nameof(VisualizerSettings.ShowInputEffect)] = ("StateEffectOn", "StateEffectOff"),
        [nameof(VisualizerSettings.ShowMissEffect)] = ("StateEffectOn", "StateEffectOff"),
        [nameof(VisualizerSettings.ExtraLanes)] = ("StateExtraLanesOn", "StateExtraLanesOff"),
        [nameof(VisualizerSettings.SlowNotesPastJudgmentLine)] = ("StateSlowdownOn", "StateSlowdownOff"),
        [nameof(VisualizerSettings.ExperimentalFeatures)] = ("StateApplied", "StateNotApplied"),
        [nameof(VisualizerSettings.ChordSectionLanes)] = ("StateChordLanesOn", "StateChordLanesOff"),
        [nameof(VisualizerSettings.NoChordCenterPair)] = ("StateCenterPairOff", "StateCenterPairOn"),
        [nameof(VisualizerSettings.NoTrillCenterPair)] = ("StateTrillCenterLast", "StateCenterPairOn"),
        [nameof(VisualizerSettings.TrillAsOneNote)] = ("StateTrillOneNote", "StateTrillEachNote"),
        [nameof(VisualizerSettings.KeepSpeedOnLoad)] = ("StateKeepSpeedOn", "StateKeepSpeedOff")
    };

    private void UpdateState(string field)
    {
        if (!_stateTexts.TryGetValue(field, out var pair)) return;
        var words = StateWords.TryGetValue(field, out var own) ? own : (On: "StateOn", Off: "StateOff");
        pair.Text.Text = T(pair.Toggle.IsChecked == true ? words.On : words.Off);
    }

    private void UpdateStates()
    {
        foreach (var field in _stateTexts.Keys) UpdateState(field);
    }

    /// <summary>A round "?" in the color of the text beside it; hovering shows the explanation.</summary>
    private Border HelpMark(string key, string brushKey)
    {
        var mark = new TextBlock
        {
            Text = "?",
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        mark.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        var badge = new Border
        {
            Width = 15,
            Height = 15,
            CornerRadius = new CornerRadius(7.5),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Cursor = Cursors.Help,
            Child = mark
        };
        badge.SetResourceReference(Border.BorderBrushProperty, brushKey);
        ToolTipService.SetInitialShowDelay(badge, 100);
        ToolTipService.SetShowDuration(badge, 60000);
        _helpMarks[badge] = key;
        return badge;
    }

    private void AddText(string label, string name, string? help = null) => AddField(label, NewTextBox(name), name, help);

    private TextBox NewTextBox(string name)
    {
        var input = new TextBox();
        input.TextChanged += (_, _) => FormChanged();
        _textFields.Add(name, input);
        return input;
    }

    /// <summary>
    /// A design color row: one box per channel (R, G, B) with a preview chip. The setting itself stays one "r, g, b" text,
    /// kept in a hidden box so JSON import, reset and the change marks keep working unchanged.
    /// </summary>
    private void AddColor(string label, string name, string? help = null)
    {
        var box = NewTextBox(name);
        box.Visibility = Visibility.Collapsed;
        var channels = Enumerable.Range(0, 3)
            .Select(_ => new TextBox { TextAlignment = TextAlignment.Center, MaxLength = 3, MinHeight = 30 }).ToArray();
        _colorChannels[name] = channels;
        var updating = false;
        void ToBox()
        {
            if (updating) return;
            updating = true;
            box.Text = string.Join(", ", channels.Select(channel => channel.Text.Trim()));
            updating = false;
        }
        void FromBox()
        {
            if (updating || !ColorText.TryParse(box.Text, out var argb)) return;
            updating = true;
            for (var i = 0; i < 3; i++) channels[i].Text = ((argb >> (16 - 8 * i)) & 0xFF).ToString(CultureInfo.InvariantCulture);
            updating = false;
        }
        box.TextChanged += (_, _) => FromBox();
        foreach (var channel in channels) channel.TextChanged += (_, _) => ToBox();
        FromBox();
        var swatch = Swatch(box);
        swatch.Width = 32;
        swatch.Margin = new Thickness(8, 0, 0, 0);
        var grid = ColorGrid();
        for (var i = 0; i < 3; i++)
        {
            Grid.SetColumn(channels[i], i * 2);
            grid.Children.Add(channels[i]);
        }
        Grid.SetColumn(swatch, 6);
        grid.Children.Add(swatch);
        grid.Children.Add(box);
        AddField(label, grid, name, help);
    }

    /// <summary>Three equal channel boxes and the preview chip, lined up like the pattern rows.</summary>
    private static Grid ColorGrid()
    {
        var grid = new Grid();
        for (var i = 0; i < 3; i++)
        {
            if (i > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    /// <summary>R, G and B written once above the color rows, aligned with the boxes below.</summary>
    private void AddColorHeader()
    {
        var grid = ColorGrid();
        grid.Margin = new Thickness(LabelWidth, 4, 0, 4);
        for (var i = 0; i < 3; i++)
        {
            var caption = new TextBlock
            {
                Text = "RGB"[i].ToString(),
                FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "FaintTextBrush");
            Grid.SetColumn(caption, i * 2);
            grid.Children.Add(caption);
        }
        CurrentSection.Children.Add(grid);
    }

    /// <summary>A scale row: a slider that drags the value, with the number beside it. Both write the same setting.</summary>
    private void AddScale(string label, string name, string? help = null)
    {
        var box = NewTextBox(name);
        box.Width = 64;
        box.TextAlignment = TextAlignment.Center;
        // Steps of 0.1 so the value always reads with a single decimal.
        var slider = new Slider
        {
            Minimum = VisualizerSettings.MinimumNoteScale,
            Maximum = VisualizerSettings.MaximumNoteScale,
            TickFrequency = 0.1,
            IsSnapToTickEnabled = true,
            SmallChange = 0.1,
            LargeChange = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var updating = false;
        slider.ValueChanged += (_, _) =>
        {
            if (updating) return;
            updating = true;
            box.Text = slider.Value.ToString("0.0", CultureInfo.InvariantCulture);
            updating = false;
        };
        box.TextChanged += (_, _) =>
        {
            if (updating || !double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return;
            updating = true;
            slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
            updating = false;
        };
        _scaleSliders[name] = slider;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(box, 1);
        grid.Children.Add(slider);
        grid.Children.Add(box);
        AddField(label, grid, name, help);
    }

    private readonly List<Action> _swatchUpdates = [];

    /// <summary>
    /// A preview chip for a color box: the color, the <paramref name="blank"/> color while the box is empty, or an error outline
    /// when the text is not a color.
    /// </summary>
    private Border Swatch(TextBox box, Func<Color>? blank = null)
    {
        var swatch = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        void Update()
        {
            var valid = ColorText.TryParse(box.Text, out var argb);
            var empty = box.Text.Trim().Length == 0;
            swatch.Background = valid
                ? new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb))
                : empty && blank is not null ? new SolidColorBrush(blank()) : Brushes.Transparent;
            swatch.SetResourceReference(Border.BorderBrushProperty, valid || empty ? "LineBrush" : "ErrorBrush");
        }
        box.TextChanged += (_, _) => Update();
        _swatchUpdates.Add(Update);
        Update();
        return swatch;
    }

    private readonly Dictionary<string, TextBox[]> _colorChannels = [];
    private readonly Dictionary<string, Slider> _scaleSliders = [];

    private static TextBox[] LaneColorBoxes() => Enumerable.Range(0, VisualizerSettings.LaneKeyCount)
        .Select(_ => new TextBox { FontSize = 9, Padding = new Thickness(4, 0, 4, 0), MinHeight = 26 }).ToArray();

    /// <summary>Lanes 1–4 over lanes 5–8, each box with its number and a color strip. With <paramref name="followTheme"/>, empty boxes show the theme's lane color.</summary>
    private UniformGrid LaneColorGrid(TextBox[] boxes, bool followTheme = false)
    {
        var grid = new UniformGrid { Columns = 4, Rows = 2 };
        for (var i = 0; i < boxes.Length; i++)
        {
            var lane = i;
            boxes[i].TextChanged += (_, _) => FormChanged();
            var cell = new DockPanel { Margin = new Thickness(i % 4 == 0 ? 0 : 4, i < 4 ? 0 : 4, 0, 0) };
            var number = new TextBlock
            {
                Text = (i + 1).ToString(CultureInfo.InvariantCulture),
                FontSize = 9,
                Width = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            number.SetResourceReference(TextBlock.ForegroundProperty, "FaintTextBrush");
            DockPanel.SetDock(number, Dock.Left);
            var strip = Swatch(boxes[i], followTheme ? () => ThemeCatalog.LaneBackgrounds(_theme)[lane] : null);
            strip.Height = 4;
            strip.Margin = new Thickness(0, 2, 0, 0);
            DockPanel.SetDock(strip, Dock.Bottom);
            cell.Children.Add(number);
            cell.Children.Add(strip);
            cell.Children.Add(boxes[i]);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private static Grid PatternGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        return grid;
    }

    /// <summary>Captions over the two pattern columns, each with its "?" help: the tiles used, and their order (preference).</summary>
    private void AddPatternHeader()
    {
        var grid = PatternGrid();
        grid.Margin = new Thickness(LabelWidth, 4, 0, 4);
        foreach (var (key, help, column) in new[] { ("PatternTiles", "PatternTilesHelp", 0), ("PatternOrder", "TilePreferenceHelp", 2) })
        {
            var caption = new TextBlock { FontSize = 9, VerticalAlignment = VerticalAlignment.Center };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "FaintTextBrush");
            _labels[caption] = key;
            var cell = new StackPanel { Orientation = Orientation.Horizontal };
            cell.Children.Add(caption);
            cell.Children.Add(HelpMark(help, "FaintTextBrush"));
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
        CurrentSection.Children.Add(grid);
    }

    /// <summary>One row per mode: the pattern tiles and, in their own box, the tile preference. Both share the row's ★.</summary>
    private void AddPattern(string label, string patternName, string orderName)
    {
        var tiles = NewTextBox(patternName);
        var order = NewTextBox(orderName);
        _orderBoxes[orderName] = order;
        var grid = PatternGrid();
        Grid.SetColumn(order, 2);
        grid.Children.Add(tiles);
        grid.Children.Add(order);
        AddField(label, grid, patternName);
        _fieldLabels[orderName] = orderName;
        _fieldStars[orderName] = _fieldStars[patternName];
        _currentSection?.Fields.Add(orderName);
    }

    /// <summary>A one-box row aligned with the "tiles" column of the pattern rows; the preference column stays empty.</summary>
    private void AddSingleTile(string label, string name, string help)
    {
        var grid = PatternGrid();
        grid.Children.Add(NewTextBox(name));
        AddField(label, grid, name, help);
    }

    private UniformGrid LaneKeyGrid()
    {
        var grid = new UniformGrid { Rows = 1, Columns = _laneBoxes.Length };
        for (var i = 0; i < _laneBoxes.Length; i++)
        {
            var cell = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0) };
            var number = new TextBlock
            {
                Text = (i + 1).ToString(CultureInfo.InvariantCulture),
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2)
            };
            number.SetResourceReference(TextBlock.ForegroundProperty, "FaintTextBrush");
            _laneBoxes[i].Padding = new Thickness(0);
            cell.Children.Add(number);
            cell.Children.Add(_laneBoxes[i]);
            grid.Children.Add(cell);
        }
        return grid;
    }

    /// <summary>The song order row: what to sort by, then which end comes first, side by side.</summary>
    private Grid SortRow()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        _songSortDirection.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(_songSortDirection, 1);
        grid.Children.Add(_songSortField);
        grid.Children.Add(_songSortDirection);
        return grid;
    }

    /// <summary>
    /// The song list row: details take half, editing and the rebuild a quarter each. The gaps are columns of
    /// their own, so the two quarter buttons come out exactly as wide (a margin on either one would make it narrower).
    /// </summary>
    private Grid SongListButtons()
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_syncButton, 2);
        Grid.SetColumn(_resyncButton, 4);
        grid.Children.Add(_songListButton);
        grid.Children.Add(_syncButton);
        grid.Children.Add(_resyncButton);
        return grid;
    }

    /// <summary>
    /// Puts a box's grey prompt exactly where the cursor stands. A text box insets its text by its own padding twice —
    /// once itself and once through the shared template's content host — and then by a little more that follows the
    /// screen scale, so no constant margin lines up everywhere (user report: the cursor sat inside the prompt's first
    /// letter). Reading the cursor back is the only measure that holds on every machine.
    /// </summary>
    internal static void AlignPrompt(TextBox box, FrameworkElement prompt)
    {
        void Align()
        {
            var at = box.GetRectFromCharacterIndex(0);
            if (double.IsFinite(at.X) && at.X >= 0)
                prompt.Margin = new Thickness(at.X, prompt.Margin.Top, prompt.Margin.Right, prompt.Margin.Bottom);
        }
        box.Loaded += (_, _) => Align();
        box.SizeChanged += (_, _) => Align();
        Align();
    }

    /// <summary>
    /// A box with a muted line inside it while it is empty, so a blank value reads as "decided for you" instead of
    /// "switched off".
    /// </summary>
    private Grid WithPlaceholder(TextBox box, string key)
    {
        var hint = new TextBlock
        {
            Margin = new Thickness(11, 0, 24, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            IsHitTestVisible = false,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _placeholders[hint] = key;
        void Show() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Show();
        Show();
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(hint);
        AlignPrompt(box, hint);
        return grid;
    }

    private static Grid WithTrailing(FrameworkElement main, FrameworkElement trailing)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        trailing.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(trailing, 1);
        grid.Children.Add(main);
        grid.Children.Add(trailing);
        return grid;
    }
}
