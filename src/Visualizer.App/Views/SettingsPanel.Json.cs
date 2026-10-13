using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Visualizer.App.Input;
using Visualizer.App.Themes;
using Visualizer.Core.Settings;

namespace Visualizer.App.Views;

/// <summary>The advanced section: extra lanes, tile preference, input scope, and JSON export / import / reset of the form.</summary>
public partial class SettingsPanel
{
    private readonly Button _exportButton = new() { Margin = new Thickness(0, 0, 3, 0) };
    private readonly Button _importButton = new() { Margin = new Thickness(3, 0, 3, 0) };
    private readonly Button _resetButton = new() { Margin = new Thickness(3, 0, 0, 0) };
    private readonly StackPanel _importPanel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _jsonInput = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        MinHeight = 150,
        MaxHeight = 280,
        MaxLength = SettingsJson.MaximumLength,
        VerticalContentAlignment = VerticalAlignment.Top,
        Padding = new Thickness(8, 6, 8, 6),
        FontFamily = new FontFamily("Consolas, Segoe UI"),
        FontSize = 10
    };
    private readonly Button _jsonApply = new() { MinWidth = 88 };
    private readonly Button _jsonCancel = new() { MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _jsonStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 10, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };

    /// <summary>
    /// Collapsed card: the experimental switch on top, then extra lanes, judgment line slowdown, tile preference,
    /// chord section lanes and the speed kept on load, then map folder (with the folder picker), data folder, process name,
    /// and the JSON buttons.
    /// </summary>
    private void AddAdvanced()
    {
        AddSection("SectionAdvanced", expandedByDefault: false);
        // Reveals the experimental card below this one; every experiment stays without effect while this is off.
        AddField("Experimental features", _experimental, nameof(VisualizerSettings.ExperimentalFeatures), "ExperimentalHelp");
        AddField("Extra lanes", _extraLanes, nameof(VisualizerSettings.ExtraLanes), "ExtraLanesHelp");
        AddField("Judgment slowdown", _judgmentSlowdown, nameof(VisualizerSettings.SlowNotesPastJudgmentLine), "JudgmentSlowdownHelp");
        AddField("Tile preference toggle", _tilePreference, nameof(VisualizerSettings.EnableTilePreference), "TilePreferenceToggleHelp");
        AddField("Chord section lanes", _chordLanes, nameof(VisualizerSettings.ChordSectionLanes), "ChordLanesHelp");
        AddField("Keep speed on load", _keepSpeed, nameof(VisualizerSettings.KeepSpeedOnLoad), "KeepSpeedHelp");
        AddField("Tiles from the next gap", _nextInterval, nameof(VisualizerSettings.NextIntervalTiles), "NextIntervalHelp");
        AddField("Map folder", WithTrailing(NewTextBox(nameof(VisualizerSettings.MapRootDirectory)), _browseFolder),
            nameof(VisualizerSettings.MapRootDirectory), "MapFolderHelp");
        // Settings and the map cache live here. An empty box keeps the default store; the line below always shows the real one.
        AddField("Data folder", WithTrailing(WithPlaceholder(NewTextBox(nameof(VisualizerSettings.DataRootDirectory)), "DataFolderAuto"), _browseData),
            nameof(VisualizerSettings.DataRootDirectory), "DataFolderHelp");
        CurrentSection.Children.Add(_dataRootPath);
        AddText("ADOFAI process name", nameof(VisualizerSettings.AdofaiProcessName));

        foreach (var (button, style) in new[] { (_exportButton, "ToolButton"), (_importButton, "ToolButton"),
                     (_resetButton, "DangerButton"), (_jsonApply, "PrimaryButton"), (_jsonCancel, "ToolButton") })
            button.SetResourceReference(StyleProperty, style);
        // The buttons share one row, so they carry less side padding than the style gives, or their labels clip.
        foreach (var button in new[] { _exportButton, _importButton, _resetButton })
            button.Padding = new Thickness(4, 0, 4, 0);
        _exportButton.Click += (_, _) => ExportJson();
        _importButton.Click += (_, _) => ShowImport();
        _resetButton.Click += (_, _) => ResetToDefaults();
        _jsonApply.Click += (_, _) => ApplyJson();
        _jsonCancel.Click += (_, _) => CancelJson();
        // Three equal buttons spanning the card, reset last. The console has no button; it opens with Ctrl+T.
        var buttons = new UniformGrid { Columns = 3, Rows = 1, Margin = new Thickness(0, 12, 0, 8) };
        buttons.Children.Add(_exportButton);
        buttons.Children.Add(_importButton);
        buttons.Children.Add(_resetButton);
        CurrentSection.Children.Add(buttons);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(_jsonApply);
        actions.Children.Add(_jsonCancel);
        _importPanel.Children.Add(_jsonInput);
        _importPanel.Children.Add(actions);
        CurrentSection.Children.Add(_importPanel);
        CurrentSection.Children.Add(_jsonStatus);
    }

    private void UpdateAdvancedLanguage()
    {
        _exportButton.Content = T("ExportSettings");
        _importButton.Content = T("ImportSettings");
        _resetButton.Content = T("ResetSettings");
        _jsonApply.Content = T("Apply");
        _jsonCancel.Content = T("Cancel");
    }

    private void ExportJson()
    {
        try
        {
            // This PC's folders stay out of the copy, so it can be pasted on another PC.
            CopyText(SettingsJson.ExportPortable(_current));
            SetJsonStatus(T("ExportDone"), error: false);
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException or ArgumentException)
        {
            SetJsonStatus(string.Format(CultureInfo.InvariantCulture, T("ExportFailed"), error.Message), error: true);
        }
    }

    private void ShowImport()
    {
        _importPanel.Visibility = Visibility.Visible;
        SetJsonStatus(T("ImportHelp"), error: false);
        _jsonInput.Focus();
    }

    /// <summary>Checks the JSON; on success fills the form (applied only with the dialog's Apply) and closes the input.</summary>
    private void ApplyJson()
    {
        VisualizerSettings imported;
        bool keptPaths;
        try
        {
            // Missing entries keep what the form shows now, or the applied settings if the form is not valid; so do this
            // PC's folders, even when the text names them.
            imported = SettingsJson.ImportPortable(_jsonInput.Text, TryReadForm() ?? _current, out keptPaths);
            if (!ThemeCatalog.Exists(imported.Theme))
                throw new SettingsJsonException(UnknownThemeMessage(SelectedLanguage), [nameof(VisualizerSettings.Theme)]);
            _ = ControlBindings.Create(imported);
        }
        catch (SettingsJsonException error)
        {
            ShowJsonError(error.Message, error.Fields, error.Line, error.Column);
            return;
        }
        catch (ControlKeyException error)
        {
            ShowJsonError(error.Message, [error.Field], null, null);
            return;
        }
        catch (ArgumentException error)
        {
            ShowJsonError(error.Message, [], null, null);
            return;
        }
        LoadForm(imported);
        CloseImport();
        SetJsonStatus(T("ImportLoaded") + (keptPaths ? " " + T("ImportKeptPaths") : ""), error: false);
    }

    private void CancelJson()
    {
        if (!string.IsNullOrWhiteSpace(_jsonInput.Text) && !Ask(T("DiscardTitle"), T("DiscardMessage"), T("Close"), risky: true)) return;
        CloseImport();
        SetJsonStatus("", error: false);
    }

    private void CloseImport()
    {
        _importPanel.Visibility = Visibility.Collapsed;
        _jsonInput.Text = "";
    }

    private void ResetToDefaults()
    {
        if (!Ask(T("ResetTitle"), T("ResetMessage"), T("Confirm"), risky: true)) return;
        // This PC's folders stay; everything else goes back to its default (user rule).
        var form = TryReadForm() ?? _current;
        LoadForm(new VisualizerSettings { MapRootDirectory = form.MapRootDirectory, DataRootDirectory = form.DataRootDirectory });
        SetJsonStatus(T("ResetLoaded"), error: false);
    }

    private void ShowJsonError(string message, IReadOnlyList<string> fields, long? line, long? column)
    {
        var text = string.Format(CultureInfo.InvariantCulture, T("ImportFailed"), message);
        if (fields.Count > 0)
            text += Environment.NewLine + string.Format(CultureInfo.InvariantCulture, T("ImportFields"), string.Join(", ", fields.Select(FieldLabel)));
        SetJsonStatus(text, error: true);
        SelectInJson(fields, line, column);
    }

    private string FieldLabel(string field) => _fieldLabels.TryGetValue(field, out var key) ? $"{field} ({T(key)})" : field;

    /// <summary>Selects the first problem setting's key in the JSON, or the syntax error position.</summary>
    private void SelectInJson(IReadOnlyList<string> fields, long? line, long? column)
    {
        var text = _jsonInput.Text;
        foreach (var field in fields)
        {
            var index = text.IndexOf("\"" + field + "\"", StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            _jsonInput.Focus();
            _jsonInput.Select(index, field.Length + 2);
            return;
        }
        if (line is not long row || column is not long col) return;
        var lines = text.Split('\n');
        var offset = 0L;
        for (var i = 0; i < row - 1 && i < lines.Length; i++) offset += lines[i].Length + 1;
        var position = (int)Math.Clamp(offset + col - 1, 0, text.Length);
        _jsonInput.Focus();
        _jsonInput.Select(position, position < text.Length ? 1 : 0);
    }

    /// <summary>The status line takes no room while empty.</summary>
    private void SetJsonStatus(string text, bool error)
    {
        _jsonStatus.Text = text;
        _jsonStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _jsonStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorBrush" : "MutedTextBrush");
    }

    internal static void CopyToClipboard(string text)
    {
        // Another app can hold the clipboard for a moment; retry briefly before reporting.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (ExternalException) when (attempt < 5) { Thread.Sleep(40); }
        }
    }
}
