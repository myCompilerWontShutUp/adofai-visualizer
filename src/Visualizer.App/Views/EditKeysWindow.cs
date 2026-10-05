using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Visualizer.App.Localization;
using Visualizer.App.Themes;
using Visualizer.Core.Settings;

namespace Visualizer.App.Views;

/// <summary>
/// The editor's key manual, opened from the "?" at its top right. It replaces the one-line help
/// under the sheet. Rows come from the language file's EditKeys: "key|what it does"; a line without "|" heads a group,
/// or is a note when it is a sentence.
/// </summary>
public sealed class EditKeysWindow : Window
{
    private readonly Grid _rows = new() { Margin = new Thickness(18, 14, 18, 18) };

    public EditKeysWindow(VisualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var text = new UiText(settings.Language);
        Title = text["EditKeysTitle"];
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Resources.MergedDictionaries.Add(new Theme());
        ThemeCatalog.Apply(Resources, settings.Theme);
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        _rows.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var line in text["EditKeys"].Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0))
        {
            var row = _rows.RowDefinitions.Count;
            _rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var split = line.IndexOf('|', StringComparison.Ordinal);
            if (split < 0)
            {
                // A full sentence is a note under the list; anything else heads a group of keys.
                var note = line.EndsWith('.');
                var heading = new TextBlock
                {
                    Text = line, TextWrapping = TextWrapping.Wrap, FontWeight = note ? FontWeights.Normal : FontWeights.SemiBold,
                    Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 4)
                };
                heading.SetResourceReference(TextBlock.ForegroundProperty, note ? "MutedTextBrush" : "AccentBrush");
                Grid.SetRow(heading, row);
                Grid.SetColumnSpan(heading, 2);
                _rows.Children.Add(heading);
                continue;
            }
            var key = new TextBlock { Text = line[..split], FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI"), Margin = new Thickness(0, 3, 12, 3) };
            var what = new TextBlock { Text = line[(split + 1)..], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
            what.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            Grid.SetRow(key, row);
            Grid.SetRow(what, row);
            Grid.SetColumn(what, 1);
            _rows.Children.Add(key);
            _rows.Children.Add(what);
        }
        Content = _rows;
        // Esc closes it, so the manual never keeps the editor's keys away for long.
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    /// <summary>Every key the manual lists, in order.</summary>
    public IReadOnlyList<string> Keys => _rows.Children.OfType<TextBlock>().Where(block => Grid.GetColumn(block) == 0 && Grid.GetColumnSpan(block) == 1)
        .Select(block => block.Text).ToArray();
}
