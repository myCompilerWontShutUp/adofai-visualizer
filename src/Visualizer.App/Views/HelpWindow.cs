using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Visualizer.App.Localization;
using Visualizer.App.Themes;
using Visualizer.Core.Settings;

namespace Visualizer.App.Views;

/// <summary>In-app guide. Key names come from the current settings, so the text matches what the user configured.</summary>
public sealed class HelpWindow : Window
{
    public HelpWindow(VisualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Title = UiText.Get(settings.Language, "Help");
        Width = 520;
        Height = 720;
        MinWidth = 400;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Resources.MergedDictionaries.Add(new Theme());
        ThemeCatalog.Apply(Resources, settings.Theme);
        SetResourceReference(BackgroundProperty, "DialogBackdropBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var body = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var text = string.Format(CultureInfo.InvariantCulture, UiText.Get(settings.Language, "HelpBody"),
            settings.ResetKey, settings.StartTriggerKey, settings.VisualizerToggleKey, settings.LaneKeys,
            settings.PreviousCheckpointKey ?? UiText.Get(settings.Language, "Unassigned"),
            settings.NextCheckpointKey ?? UiText.Get(settings.Language, "Unassigned"));
        foreach (var block in text.Split("\n\n"))
        {
            var lines = block.Split('\n', 2);
            var heading = new TextBlock { Text = lines[0], FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            body.Children.Add(heading);
            if (lines.Length > 1)
            {
                var paragraph = new TextBlock { Text = lines[1], TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 0, 16) };
                paragraph.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                body.Children.Add(paragraph);
            }
        }
        // The fixed shortcut goes last, quietly: it is not a setting and cannot be changed.
        var shortcut = new TextBlock
        {
            Text = UiText.Get(settings.Language, "ConsoleShortcut"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0)
        };
        shortcut.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        body.Children.Add(shortcut);
        var close = new Button { Content = UiText.Get(settings.Language, "Close"), MinWidth = 96, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.SetResourceReference(StyleProperty, "PrimaryButton");
        close.Click += (_, _) => Close();
        var root = new DockPanel { Margin = new Thickness(20, 16, 12, 16) };
        DockPanel.SetDock(close, Dock.Bottom);
        close.Margin = new Thickness(0, 12, 8, 0);
        root.Children.Add(close);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
        Content = root;
    }
}
