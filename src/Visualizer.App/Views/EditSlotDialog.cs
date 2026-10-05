using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Visualizer.App.Localization;
using Visualizer.App.Themes;
using Visualizer.Application.Library;
using Visualizer.Core.Settings;

namespace Visualizer.App.Views;

/// <summary>
/// Asks which of the song's nine slots an edit goes into. It opens on every save: the slot being
/// edited is marked, a used slot warns that its edit is replaced, and what that slot holds (when it was made and more)
/// waits folded under Details.
/// </summary>
public sealed class EditSlotDialog : Window
{
    public const int SlotCount = 9;
    private readonly UiText _text;
    private readonly int? _current;
    private readonly string _sourceHash;
    private readonly Dictionary<int, EditSlotInfo> _used;
    private readonly ToggleButton[] _cells = new ToggleButton[SlotCount];
    private readonly TextBlock _warning = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
    private readonly ToggleButton _more = new() { HorizontalAlignment = HorizontalAlignment.Left, Height = 26, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Grid _details = new() { Margin = new Thickness(6, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _save = new() { MinWidth = 110, IsDefault = true };
    private int? _selected;

    public EditSlotDialog(VisualizerSettings settings, string songName, int? currentSlot, IReadOnlyList<EditSlotInfo> used, string sourceHash)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(used);
        _text = new UiText(settings.Language);
        _current = currentSlot;
        _sourceHash = sourceHash ?? "";
        _used = used.Where(info => info.Slot is >= 1 and <= SlotCount).ToDictionary(info => info.Slot);

        Title = _text["EditSlotTitle"];
        Width = 420;
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

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = songName, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        root.Children.Add(Muted(_text["EditSlotPrompt"], new Thickness(0, 4, 0, 0)));
        if (currentSlot is { } editing)
        {
            var now = new TextBlock { Text = _text.Format("EditSlotCurrent", editing), Margin = new Thickness(0, 2, 0, 0) };
            now.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            root.Children.Add(now);
        }

        var grid = new UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(-3, 12, -3, 0) };
        for (var slot = 1; slot <= SlotCount; slot++)
        {
            var number = slot;
            var cell = new ToggleButton { Height = 54, Margin = new Thickness(3), Content = CellContent(slot) };
            cell.SetResourceReference(StyleProperty, "PillToggle");
            cell.Click += (_, _) => Select(number);
            _cells[slot - 1] = cell;
            grid.Children.Add(cell);
        }
        root.Children.Add(grid);

        _warning.SetResourceReference(TextBlock.ForegroundProperty, "FireBrush");
        root.Children.Add(_warning);
        _more.SetResourceReference(StyleProperty, "PillToggle");
        _more.Checked += (_, _) => { _details.Visibility = Visibility.Visible; UpdateMore(); };
        _more.Unchecked += (_, _) => { _details.Visibility = Visibility.Collapsed; UpdateMore(); };
        root.Children.Add(_more);
        _details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _details.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(_details);

        _save.SetResourceReference(StyleProperty, "PrimaryButton");
        _save.Click += (_, _) => { if (_selected is not null) DialogResult = true; };
        var cancel = new Button { Content = _text["Cancel"], MinWidth = 88, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        cancel.SetResourceReference(StyleProperty, "ToolButton");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(_save);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        // 1..9 pick a slot from the keyboard, as the editor's number keys pick lanes.
        PreviewKeyDown += (_, e) =>
        {
            var slot = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D0 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad0 : 0;
            if (slot == 0) return;
            Select(slot);
            e.Handled = true;
        };

        // The slot being edited comes first, then the first empty one; with every slot used and no current one, nothing is
        // picked until the user chooses what to replace.
        var start = currentSlot ?? Enumerable.Range(1, SlotCount).Cast<int?>().FirstOrDefault(slot => !_used.ContainsKey(slot!.Value));
        if (start is { } first) Select(first);
        else Refresh();
    }

    /// <summary>The slot picked now; null while none is.</summary>
    public int? Selected => _selected;

    /// <summary>Whether saving would replace an edit already in the picked slot.</summary>
    public bool Overwrites => _selected is { } slot && _used.ContainsKey(slot);

    /// <summary>The line under the slots that says what is replaced; empty for an empty slot.</summary>
    public string Warning => _warning.Text;

    /// <summary>Whether the folded details are open.</summary>
    public bool DetailsOpen
    {
        get => _more.IsChecked == true;
        set => _more.IsChecked = value;
    }

    /// <summary>The detail rows of the picked slot as "label: value".</summary>
    public IReadOnlyList<string> DetailLines => DetailRows().Select(row => $"{row.Label}: {row.Value}").ToArray();

    public string SaveLabel => (string)_save.Content;

    public void Select(int slot)
    {
        if (slot is < 1 or > SlotCount) return;
        _selected = slot;
        Refresh();
    }

    /// <summary>Shows the dialog over <paramref name="owner"/>; the picked slot, or null when cancelled.</summary>
    public static int? Pick(Window owner, VisualizerSettings settings, string songName, int? currentSlot,
        IReadOnlyList<EditSlotInfo> used, string sourceHash)
    {
        var dialog = new EditSlotDialog(settings, songName, currentSlot, used, sourceHash) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Selected : null;
    }

    private object CellContent(int slot)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = slot.ToString(CultureInfo.InvariantCulture), FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        var state = slot == _current ? _text["EditSlotEditing"] : _used.ContainsKey(slot) ? _text["EditSlotUsed"] : _text["EditSlotEmpty"];
        // The state follows the cell's own color (accent when picked), only a little quieter.
        panel.Children.Add(new TextBlock { Text = state, FontSize = 11, Opacity = 0.8, HorizontalAlignment = HorizontalAlignment.Center });
        return panel;
    }

    private void Refresh()
    {
        for (var slot = 1; slot <= SlotCount; slot++) _cells[slot - 1].IsChecked = slot == _selected;
        var overwrite = Overwrites;
        _warning.Text = overwrite ? _text.Format("EditSlotOverwrite", _selected!.Value) : "";
        _warning.Visibility = overwrite ? Visibility.Visible : Visibility.Collapsed;
        _more.Visibility = overwrite ? Visibility.Visible : Visibility.Collapsed;
        _details.Visibility = overwrite && DetailsOpen ? Visibility.Visible : Visibility.Collapsed;
        _save.Content = overwrite ? _text["EditSlotReplace"] : _text["Save"];
        // Replacing an edit throws the old one away: the orange outline, never the blue "go ahead".
        _save.SetResourceReference(StyleProperty, overwrite ? "DangerButton" : "PrimaryButton");
        _save.IsEnabled = _selected is not null;
        UpdateMore();

        _details.Children.Clear();
        _details.RowDefinitions.Clear();
        foreach (var (label, value) in DetailRows())
        {
            var row = _details.RowDefinitions.Count;
            _details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = Muted(label, new Thickness(0, 2, 16, 2));
            var text = new TextBlock { Text = value, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            _details.Children.Add(name);
            _details.Children.Add(text);
        }
    }

    private void UpdateMore()
    {
        // Chevron from the icon font: right while folded, down while open.
        var chevron = new TextBlock { Text = char.ConvertFromUtf32(DetailsOpen ? 0xE70D : 0xE76C), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) };
        chevron.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(chevron);
        content.Children.Add(new TextBlock { Text = _text["EditSlotDetails"], VerticalAlignment = VerticalAlignment.Center });
        _more.Content = content;
    }

    private IEnumerable<(string Label, string Value)> DetailRows()
    {
        if (_selected is not { } slot || !_used.TryGetValue(slot, out var info)) yield break;
        string When(DateTimeOffset? time) => time is { } value
            ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : _text["EditSlotUnknown"];
        yield return (_text["EditSlotCreated"], When(info.CreatedAt));
        yield return (_text["EditSlotSaved"], When(info.SavedAt));
        yield return (_text["EditSlotContent"], _text.Format("EditSlotContentValue", info.Notes, info.Chords));
        yield return (_text["EditSlotTiles"], info.MaximumTileCount?.ToString(CultureInfo.InvariantCulture) ?? _text["EditSlotUnknown"]);
        yield return (_text["EditSlotSource"], info.SourceHash.Length == 0 || _sourceHash.Length == 0 ? _text["EditSlotUnknown"]
            : info.SourceHash == _sourceHash ? _text["EditSlotSourceSame"] : _text["EditSlotSourceChanged"]);
    }

    private static TextBlock Muted(string text, Thickness margin)
    {
        var block = new TextBlock { Text = text, Margin = margin, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return block;
    }
}
