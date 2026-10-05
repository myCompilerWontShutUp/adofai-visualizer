using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Visualizer.App.Localization;
using Visualizer.App.Themes;
using Visualizer.Application.Cache;
using Visualizer.Application.Library;
using Visualizer.Application.Maps;
using Visualizer.Core.Settings;

namespace Visualizer.App.Views;

/// <summary>
/// What the song list window gives back: the choices, whether the user dragged songs into their own order, and which
/// details the rows show.
/// </summary>
public sealed record SongListResult(IReadOnlyList<SongPreference> Preferences, bool Reordered, string Fields);

/// <summary>
/// The song list opened from setting → song list. It shows every synced song, including hidden ones, and lets each be
/// hidden from the main list, renamed for display only, or dragged into place. Nothing is saved until this window is
/// confirmed and the settings are applied; map files are never touched.
/// </summary>
public sealed class SongListWindow : Window
{
    private sealed class Row
    {
        public required MapCacheEntry Entry { get; set; }
        public required string OriginalName { get; init; }
        /// <summary>Hidden right now: the user's choice when there is one, otherwise the automatic default.</summary>
        public bool Hidden { get; set; }
        /// <summary>What the user decided, or null while the automatic default applies.</summary>
        public bool? Choice { get; set; }
        public bool HiddenByDefault { get; init; }
        public string? DisplayName { get; set; }
        /// <summary>An edit kept right under its original: it moves with the original and is not dragged on its own.</summary>
        public bool Follows { get; init; }
        public Border Panel { get; set; } = new();
    }

    private readonly List<Row> _rows = [];
    private readonly StackPanel _list = new();
    private readonly WrapPanel _fieldBar = new() { Margin = new Thickness(12, 12, 12, 2) };
    private readonly Dictionary<string, ToggleButton> _fieldToggles = [];
    /// <summary>The line drawn between two songs while a row is held, showing where it would land.</summary>
    private readonly Border _dropLine = new() { Height = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(2, 1, 2, 5), IsHitTestVisible = false };
    private int _dropIndex = -1;
    /// <summary>Fixed row height: the list does not jump when details are switched or a name is edited.</summary>
    private const double RowHeight = 62;
    /// <summary>How close to an edge the pointer has to be, and how far each step scrolls, while carrying a row.</summary>
    private const double ScrollEdge = 40, ScrollStep = 14;
    /// <summary>Narrows the list by title. Searching parks dragging: the order is kept in full, so it cannot be changed from a part of it.</summary>
    private readonly TextBox _search = new() { Height = 28, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _empty = new() { FontSize = 12, Margin = new Thickness(4, 4, 4, 0) };
    /// <summary>What was typed into the search box, trimmed; empty means every song is listed.</summary>
    private string _filter = "";
    /// <summary>Why the last removal did not happen, shown beside the buttons until the next change.</summary>
    private readonly TextBlock _notice = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 };
    private readonly Button _confirm = new() { MinWidth = 96, Height = 30 };
    private readonly Button _cancel = new() { MinWidth = 96, Height = 30, Margin = new Thickness(8, 0, 0, 0) };
    private readonly UiText _text;
    private readonly VisualizerSettings _settings;
    private readonly IReadOnlyList<SongPreference> _initial;
    private readonly string _initialFields;
    private bool _reordered;
    /// <summary>The row that was just dropped, marked until the next change so the move is easy to follow.</summary>
    private Row? _moved;

    public SongListResult? Result { get; private set; }
    /// <summary>Asks (title, message) before a copy is deleted; a themed dialog by default (replaceable in tests).</summary>
    public Func<string, string, bool> Confirm { get; set; }
    /// <summary>
    /// Deletes the app's own copy of a song, or says why it could not be. Null leaves the button out, so a caller that cannot
    /// delete (and a test) is never offered it. Unlike hiding and renaming, this happens at once and is not pending.
    /// </summary>
    public Func<MapCacheEntry, string?>? RemoveSong
    {
        get => _remove;
        // Callers set this after the constructor has already drawn the rows, so they are drawn again with the button.
        set { _remove = value; Build(); }
    }
    private Func<MapCacheEntry, string?>? _remove;

    public SongListWindow(IReadOnlyList<MapCacheEntry> songs, IReadOnlyList<SongPreference> preferences, VisualizerSettings settings,
        Func<MapCacheEntry, (DateTime? Created, DateTime? Modified)>? fileTimes = null,
        Func<MapCacheEntry, bool>? isHiddenByDefault = null,
        Func<MapCacheEntry, (string Family, int? Slot)>? families = null)
    {
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(settings);
        _text = new UiText(settings.Language);
        _settings = settings;
        // Saying yes throws a copy away, so the accepting button is drawn as a risky one (user rule).
        Confirm = (title, message) => ConfirmDialog.Ask(this, _settings, title, message, _text["Confirm"], _text["Cancel"], risky: true);
        _initial = preferences;
        _initialFields = string.Join(' ', settings.GetSongListFields());
        var byId = preferences.ToDictionary(song => song.Id, StringComparer.Ordinal);

        Title = _text["SongListTitle"];
        Width = 860;
        Height = 560;
        MinWidth = 560;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Resources.MergedDictionaries.Add(new Theme());
        ThemeCatalog.Apply(Resources, settings.Theme);
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        // Listed in the order the main screen uses, so what is dragged here matches what is seen there. Edits stay under their
        // original and move with it.
        var ordered = SongOrdering.Sort(songs, SongOrdering.Parse(settings.SongSortKey), settings.SongSortHighFirst, byId, fileTimes, families);
        string? block = null;
        foreach (var entry in ordered)
        {
            byId.TryGetValue(entry.Id, out var pref);
            var automatic = isHiddenByDefault?.Invoke(entry) == true;
            var place = families?.Invoke(entry);
            var follows = place is { Slot: not null } edit && string.Equals(edit.Family, block, StringComparison.OrdinalIgnoreCase);
            if (!follows) block = place?.Family;
            _rows.Add(new Row
            {
                Entry = entry,
                OriginalName = entry.SongName,
                HiddenByDefault = automatic,
                Choice = pref?.Hidden,
                Hidden = pref?.Hidden ?? automatic,
                DisplayName = pref?.DisplayName,
                Follows = follows
            });
        }

        // One switch per detail, in the order the rows draw them; all on unless the settings say otherwise.
        var shown = settings.GetSongListFields();
        foreach (var field in VisualizerSettings.SongListFieldNames)
        {
            var toggle = new ToggleButton
            {
                Content = _text["SongField." + field],
                IsChecked = shown.Contains(field, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 0, 6, 6),
                MinHeight = 26
            };
            toggle.SetResourceReference(StyleProperty, "PillToggle");
            toggle.Checked += (_, _) => { Build(); UpdateConfirm(); };
            toggle.Unchecked += (_, _) => { Build(); UpdateConfirm(); };
            _fieldToggles[field] = toggle;
            _fieldBar.Children.Add(toggle);
        }

        _dropLine.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        _confirm.Click += (_, _) => { Result = Collect(); DialogResult = true; };
        _cancel.Content = _text["Cancel"];
        _cancel.Click += (_, _) => { DialogResult = false; };
        _cancel.SetResourceReference(StyleProperty, "ToolButton");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12, 6, 12, 8) };
        scroll.Content = _list;
        // Only the rows took the drop before, so letting go in the gap between two of them, or below the last one, did
        // nothing at all. The whole list is the target now, and dragging near an edge scrolls it.
        _list.AllowDrop = true;
        scroll.AllowDrop = true;
        _list.DragOver += (_, e) => { MoveDropLine(GapAt(e.GetPosition(_list).Y)); Held(e); };
        _list.Drop += (_, e) => DropHeld(e);
        scroll.DragOver += (_, e) => { AutoScroll(scroll, e.GetPosition(scroll).Y); Held(e); };
        scroll.Drop += (_, e) => DropHeld(e);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 8, 12, 12) };
        _notice.SetResourceReference(TextBlock.ForegroundProperty, "FireBrush");
        actions.Children.Add(_notice);
        actions.Children.Add(_confirm);
        actions.Children.Add(_cancel);

        // The search sits above the detail switches, where the main screen keeps its own search box.
        _empty.Text = _text["SongListSearchNone"];
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _search.TextChanged += (_, _) =>
        {
            _filter = _search.Text.Trim();
            _moved = null;
            Build();
        };
        var caption = new TextBlock { Text = _text["SongListSearch"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var searchRow = new DockPanel { Margin = new Thickness(12, 12, 12, 0) };
        DockPanel.SetDock(caption, Dock.Left);
        searchRow.Children.Add(caption);
        searchRow.Children.Add(_search);

        var root = new DockPanel();
        DockPanel.SetDock(searchRow, Dock.Top);
        DockPanel.SetDock(_fieldBar, Dock.Top);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(searchRow);
        root.Children.Add(_fieldBar);
        root.Children.Add(actions);
        root.Children.Add(scroll);
        Content = root;

        Build();
        UpdateConfirm();
    }

    /// <summary>Rebuilds the rows after a change so the order, names, details and buttons stay in step with the list.</summary>
    private void Build()
    {
        _list.Children.Clear();
        var shown = 0;
        // An edit is named after its original (user rule), so it is listed with whatever the original matched.
        var leader = true;
        foreach (var row in _rows)
        {
            if (!row.Follows) leader = Matches(row);
            // Every row is still built: the panels carry the drag targets and a name being edited keeps its box.
            row.Panel = BuildRow(row);
            if (!leader && !Matches(row)) continue;
            _list.Children.Add(row.Panel);
            shown++;
        }
        if (shown == 0 && _rows.Count > 0) _list.Children.Add(_empty);
    }

    /// <summary>Whether a song is listed with the search as it stands. Both names count: the shown one and the original.</summary>
    private bool Matches(Row row) => _filter.Length == 0
        || (row.DisplayName ?? row.OriginalName).Contains(_filter, StringComparison.CurrentCultureIgnoreCase)
        || row.OriginalName.Contains(_filter, StringComparison.CurrentCultureIgnoreCase);

    private Border BuildRow(Row row)
    {
        var moved = ReferenceEquals(row, _moved);
        var panel = new Border { Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(6), AllowDrop = true, Height = RowHeight };
        panel.SetResourceReference(Border.BackgroundProperty, moved ? "AccentSoftBrush" : "SurfaceBrush");
        panel.SetResourceReference(Border.BorderBrushProperty, moved ? "AccentBrush" : "BorderBrush");
        panel.BorderThickness = new Thickness(1);
        // The row that was just dropped fades in where it landed, so the move is visible without hunting for it.
        if (moved) panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(260)));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = row.DisplayName ?? row.OriginalName, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 14 };
        if (row.Hidden)
        {
            name.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            name.Opacity = 0.55;
        }
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(name);
        if (row.Hidden && row.Choice is null && row.HiddenByDefault)
        {
            var reason = new TextBlock { Text = _text["SongListBesideMain"], FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            reason.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            head.Children.Add(reason);
        }
        texts.Children.Add(head);
        if (row.DisplayName is not null)
        {
            var renamed = new TextBlock { Text = _text.Format("SongListRenamed", row.OriginalName), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            renamed.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            texts.Children.Add(renamed);
        }
        if (Describe(row.Entry) is { Length: > 0 } details)
        {
            var info = new TextBlock { Text = details, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            info.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            texts.Children.Add(info);
        }

        // Equal widths for every button except the drag handle.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
        if (_remove is not null)
        {
            var remove = new Button { Content = _text["SongListRemove"], Width = 92, Height = 26, Margin = new Thickness(0, 0, 6, 0) };
            remove.SetResourceReference(StyleProperty, "DangerButton");
            remove.Click += (_, _) => AskRemove(row);
            buttons.Children.Add(remove);
        }
        // Both ways look the same; the dimmed title is what tells a hidden song apart.
        var hide = new Button { Content = _text[row.Hidden ? "SongListShow" : "SongListHide"], Width = 92, Height = 26 };
        hide.SetResourceReference(StyleProperty, "ToolButton");
        hide.Click += (_, _) => { _moved = null; row.Hidden = !row.Hidden; row.Choice = row.Hidden; Build(); UpdateConfirm(); };
        // An edit is named after its original (user rule), so only the original offers the rename; the edits follow it.
        var rename = new Button
        {
            Content = _text["SongListRename"],
            Width = 92,
            Height = 26,
            Margin = new Thickness(6, 0, 0, 0),
            IsEnabled = !row.Follows,
            ToolTip = row.Follows ? _text["SongListFollows"] : null
        };
        rename.SetResourceReference(StyleProperty, "ToolButton");
        ToolTipService.SetShowOnDisabled(rename, true);
        rename.Click += (_, _) => { _moved = null; StartRename(row); };
        // A part of the list cannot be put in order, so the handle waits until the search is cleared.
        var searching = _filter.Length > 0;
        var handle = new Button
        {
            Content = "≡",
            Width = 32,
            Height = 26,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = _text[row.Follows ? "SongListFollows" : searching ? "SongListSearchDrag" : "SongListDrag"],
            IsEnabled = !row.Follows && !searching
        };
        ToolTipService.SetShowOnDisabled(handle, true);
        handle.SetResourceReference(StyleProperty, "ToolButton");
        handle.PreviewMouseLeftButtonDown += (_, _) => Drag(panel, row);
        buttons.Children.Add(hide);
        buttons.Children.Add(rename);
        buttons.Children.Add(handle);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(texts);
        grid.Children.Add(buttons);
        panel.Child = grid;

        // The line follows the pointer: above this song while over its top half, below it otherwise.
        panel.DragOver += (_, e) =>
        {
            MoveDropLine(_rows.IndexOf(row) + (e.GetPosition(panel).Y > panel.ActualHeight / 2 ? 1 : 0));
            Held(e);
        };
        panel.Drop += (_, e) => DropHeld(e);
        return panel;
    }

    /// <summary>
    /// Asks once more and then deletes the app's own copy. This is not pending like the other choices: the file is gone once
    /// the question is accepted, so cancelling the window afterwards does not bring it back.
    /// </summary>
    private void AskRemove(Row row)
    {
        _moved = null;
        _notice.Text = "";
        if (RemoveSong is not { } remove) return;
        if (!Confirm(_text["SongListRemove"], _text.Format("SongListRemoveAsk", row.DisplayName ?? row.OriginalName))) return;
        if (remove(row.Entry) is { Length: > 0 } problem)
        {
            _notice.Text = problem;
            return;
        }
        _rows.Remove(row);
        Build();
        UpdateConfirm();
    }

    /// <summary>Takes a song out of the list after its copy was deleted. Public for tests, which cannot press a dialog.</summary>
    public bool RemoveRow(int index)
    {
        if (index < 0 || index >= _rows.Count) return false;
        AskRemove(_rows[index]);
        return true;
    }

    /// <summary>Holds the row: it dims while it is carried and comes back when the drag ends, dropped or not.</summary>
    private void Drag(Border panel, Row row)
    {
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(120)));
        DragDrop.DoDragDrop(panel, row, DragDropEffects.Move);
        ClearDropLine();
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(160)));
    }

    /// <summary>
    /// The gap the pointer is over, counted in songs: 0 above the first, <c>Count</c> below the last. Public for tests,
    /// since a real drag cannot be raised from one.
    /// </summary>
    public int GapAt(double y)
    {
        var top = 0.0;
        for (var index = 0; index < _rows.Count; index++)
        {
            var height = _rows[index].Panel.ActualHeight > 0 ? _rows[index].Panel.ActualHeight : RowHeight;
            // The margin under each row belongs to the gap after it, so letting go between two rows still lands.
            var step = height + _rows[index].Panel.Margin.Bottom;
            if (y < top + step / 2) return index;
            top += step;
        }
        return _rows.Count;
    }

    /// <summary>Says the drag is a move, so the pointer shows it and the drop is accepted.</summary>
    private static void Held(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(Row)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Scrolls while a row is carried near the top or bottom, so a long list can be crossed.</summary>
    private static void AutoScroll(ScrollViewer scroll, double y)
    {
        if (y < ScrollEdge) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - ScrollStep);
        else if (y > scroll.ActualHeight - ScrollEdge) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + ScrollStep);
    }

    /// <summary>Puts the line in the gap the held row would go into. Index 0 is above the first song.</summary>
    private void MoveDropLine(int index)
    {
        index = Snap(Math.Clamp(index, 0, _rows.Count));
        if (_dropIndex == index && _list.Children.Contains(_dropLine)) return;
        _list.Children.Remove(_dropLine);
        _list.Children.Insert(index, _dropLine);
        _dropIndex = index;
    }

    private void ClearDropLine()
    {
        _list.Children.Remove(_dropLine);
        _dropIndex = -1;
    }

    /// <summary>Moves the held row into the gap the line marks. Dropping it back where it was changes nothing.</summary>
    private void DropHeld(DragEventArgs e)
    {
        var gap = _dropIndex;
        ClearDropLine();
        if (e.Data.GetData(typeof(Row)) is not Row dropped || gap < 0 || !MoveRow(_rows.IndexOf(dropped), gap)) Build();
    }

    /// <summary>
    /// Moves the song at <paramref name="from"/> into the gap before song <paramref name="gap"/> (gap 0 is above the first
    /// song, <c>Count</c> below the last). Returns whether the order changed. Public for tests, since a real drag cannot be
    /// raised from one.
    /// </summary>
    public bool MoveRow(int from, int gap)
    {
        // An edit goes where its original goes; the original takes its edits along.
        if (from < 0 || from >= _rows.Count || _rows[from].Follows) return false;
        var size = 1;
        while (from + size < _rows.Count && _rows[from + size].Follows) size++;
        gap = Snap(Math.Clamp(gap, 0, _rows.Count));
        if (gap >= from && gap <= from + size) return false;
        var block = _rows.GetRange(from, size);
        _rows.RemoveRange(from, size);
        // Taking the rows out shifts every gap below them up.
        _rows.InsertRange(gap > from ? gap - size : gap, block);
        _reordered = true;
        _moved = block[0];
        Build();
        UpdateConfirm();
        return true;
    }

    /// <summary>A gap among a song's edits is taken as the gap after them, so nothing lands between an original and its edits.</summary>
    private int Snap(int gap)
    {
        while (gap > 0 && gap < _rows.Count && _rows[gap].Follows) gap++;
        return gap;
    }

    /// <summary>Renaming happens in place: confirming an empty box puts the original name back (no separate reset button).</summary>
    private void StartRename(Row row)
    {
        var box = new TextBox { Text = row.DisplayName ?? "", MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
        var accept = new Button { Content = _text["Confirm"], Width = 92, Height = 26, Margin = new Thickness(6, 0, 0, 0) };
        accept.SetResourceReference(StyleProperty, "PrimaryButton");
        var hint = new TextBlock
        {
            Text = _text.Format("SongListRenameHint", row.OriginalName),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var edit = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        edit.Children.Add(box);
        edit.Children.Add(accept);
        edit.Children.Add(hint);
        row.Panel.Child = edit;
        box.Focus();
        box.SelectAll();

        void Accept()
        {
            var typed = box.Text.Trim();
            // A name of punctuation alone cannot be typed at the console, where "." already means the loaded song.
            if (typed.Length > 0 && !HasLetterOrDigit(typed))
            {
                hint.Text = _text["SongListNameNeedsLetter"];
                hint.SetResourceReference(TextBlock.ForegroundProperty, "FireBrush");
                box.Focus();
                return;
            }
            row.DisplayName = typed.Length == 0 || string.Equals(typed, row.OriginalName, StringComparison.Ordinal) ? null : typed;
            Build();
            UpdateConfirm();
        }
        accept.Click += (_, _) => Accept();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Accept(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Build(); }
        };
    }

    /// <summary>Whether a shown name has something to type at the console: at least one letter or digit.</summary>
    public static bool HasLetterOrDigit(string name) => name is not null && name.Any(char.IsLetterOrDigit);

    /// <summary>The details line, in the fixed order, holding only the switched-on ones. An unsupported song always says so.</summary>
    private string Describe(MapCacheEntry entry)
    {
        var parts = new List<string>();
        if (entry.Status != MapParseStatus.Supported) parts.Add(entry.Status.ToString());
        parts.AddRange(Chosen().Select(field => SongDetails.Field(entry, field, _text)));
        return string.Join(" · ", parts);
    }

    private IEnumerable<string> Chosen() =>
        VisualizerSettings.SongListFieldNames.Where(field => _fieldToggles[field].IsChecked == true);

    /// <summary>The choices as they stand, with the dragged order when there was one. Public for tests.</summary>
    public SongListResult Collect() => new(Gather(), _reordered, string.Join(' ', Chosen()));

    private IReadOnlyList<SongPreference> Gather() => _rows
        .Select((row, index) => new SongPreference(row.Entry.Id, row.Choice, row.DisplayName, _reordered ? index : Place(row)))
        .ToArray();

    private int Place(Row row) => _initial.FirstOrDefault(song => song.Id == row.Entry.Id)?.Order ?? -1;

    private void UpdateConfirm()
    {
        var changed = _reordered || string.Join(' ', Chosen()) != _initialFields || _rows.Any(row =>
        {
            var before = _initial.FirstOrDefault(song => song.Id == row.Entry.Id);
            return row.Choice != before?.Hidden || row.DisplayName != before?.DisplayName;
        });
        _confirm.Content = _text["Confirm"];
        _confirm.SetResourceReference(StyleProperty, changed ? "PrimaryButton" : "ToolButton");
    }
}
