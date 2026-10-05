using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Visualizer.App.Themes;
using Visualizer.Core.Settings;

namespace Visualizer.App.Views;

/// <summary>
/// Themed dialog. The accept button carries its own label and comes first, then cancel (for example "Allow change" · "Cancel").
/// Without a cancel label it is a notice with one button. A risky question (one whose yes throws something away: closing
/// without saving, resetting, overwriting) shows its accept button as the orange outline the reset buttons use, so it never
/// looks like the blue "go ahead". <paramref name="details"/> adds a button that folds more out, as the slot picker does.
/// </summary>
public sealed class ConfirmDialog : Window
{
    private readonly ToggleButton? _more;
    private readonly TextBlock? _detailText;

    public ConfirmDialog(VisualizerSettings settings, string title, string message, string accept, string? cancel, bool risky = false,
        string? details = null, string? detailsLabel = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Resources.MergedDictionaries.Add(new Theme());
        ThemeCatalog.Apply(Resources, settings.Theme);
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 0, 16) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var acceptButton = new Button { Content = accept, MinWidth = 110, IsDefault = true, IsCancel = cancel is null };
        acceptButton.SetResourceReference(StyleProperty, risky ? "DangerButton" : "PrimaryButton");
        AcceptStyle = risky ? "DangerButton" : "PrimaryButton";
        acceptButton.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(acceptButton);
        if (cancel is not null)
        {
            var cancelButton = new Button { Content = cancel, MinWidth = 88, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            cancelButton.SetResourceReference(StyleProperty, "ToolButton");
            buttons.Children.Add(cancelButton);
        }
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(text);
        if (details is not null)
        {
            _more = new ToggleButton { HorizontalAlignment = HorizontalAlignment.Left, Height = 26, Margin = new Thickness(0, 0, 0, 8) };
            _more.SetResourceReference(StyleProperty, "ToolButton");
            _detailText = new TextBlock
            {
                Text = details,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 0, 0, 12),
                Visibility = Visibility.Collapsed
            };
            _detailText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            _more.Checked += (_, _) => UpdateDetails(detailsLabel);
            _more.Unchecked += (_, _) => UpdateDetails(detailsLabel);
            UpdateDetails(detailsLabel);
            root.Children.Add(_more);
            root.Children.Add(_detailText);
        }
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>Whether the folded details are open; null when the question has none.</summary>
    public bool? DetailsOpen
    {
        get => _more?.IsChecked == true;
        set { if (_more is not null) _more.IsChecked = value == true; }
    }

    /// <summary>The folded text, for tests.</summary>
    public string? DetailsText => _detailText?.Text;

    private void UpdateDetails(string? label)
    {
        if (_more is null || _detailText is null) return;
        var open = _more.IsChecked == true;
        // Chevron from the icon font: right while folded, down while open, as the slot picker shows it.
        var chevron = new TextBlock { Text = char.ConvertFromUtf32(open ? 0xE70D : 0xE76C), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) };
        chevron.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(chevron);
        content.Children.Add(new TextBlock { Text = label ?? "", VerticalAlignment = VerticalAlignment.Center });
        _more.Content = content;
        _detailText.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The style key of the accept button, for tests.</summary>
    public string AcceptStyle { get; }

    public static bool Ask(Window owner, VisualizerSettings settings, string title, string message, string accept, string? cancel, bool risky = false,
        string? details = null, string? detailsLabel = null) =>
        new ConfirmDialog(settings, title, message, accept, cancel, risky, details, detailsLabel) { Owner = owner }.ShowDialog() == true;
}
