using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Visualizer.App.Localization;
using Visualizer.App.Rendering;
using Visualizer.App.Themes;
using Visualizer.Application.Library;
using Visualizer.Core.Settings;

namespace Visualizer.App.Views;

/// <summary>What the edit window gives back when it is saved: the layout and the slot it goes into.</summary>
public sealed record NoteEditResult(NoteEdit Layout, int Slot);

/// <summary>
/// The note editor: lanes and chord grouping only. Times, the notes that exist and the judgment are never touched, and
/// there is no playback. Only one of these windows exists at a time (the main window keeps it).
/// </summary>
public sealed class NoteEditWindow : Window
{
    private readonly NoteEditField _field = new() { ClipToBounds = true, Focusable = true };
    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Vertical, Width = 16, SmallChange = 200 };
    private readonly Button _save = new() { MinWidth = 96, Height = 30 };
    private readonly Button _cancel = new() { MinWidth = 96, Height = 30, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ToggleButton _options = new() { Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0) };
    private readonly CheckBox _ghost = new();
    private readonly Border _panel = new() { Width = PanelWidth, HorizontalAlignment = HorizontalAlignment.Right, BorderThickness = new Thickness(1, 0, 0, 0) };
    private readonly TranslateTransform _slide = new();
    private readonly ISettingsLayoutStore _layout;
    private bool _panelOpen;
    private readonly Button _keys = new() { Content = "?", Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0), FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _picked = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 12, 0) };
    private EditKeysWindow? _manual;
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _note = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly UiText _text;
    private readonly VisualizerSettings _settings;
    private readonly double _songMs;
    private readonly string _songName;
    private Point? _dragFrom;
    private int _anchor = -1;

    public NoteEditSession Session { get; }
    /// <summary>The slot being edited when the song is itself an edit; null for an original song.</summary>
    public int? CurrentSlot { get; }
    public NoteEditResult? Result { get; private set; }
    /// <summary>Asks (title, message, accept) before throwing edits away. Null (the default) shows the themed dialog.</summary>
    public Func<string, string, string, bool>? Confirm { get; set; }
    /// <summary>What the song's used slots hold, for the slot picker.</summary>
    public IReadOnlyList<EditSlotInfo> Slots { get; set; } = [];
    /// <summary>Picks the slot to save into, or null to go back; the slot dialog, shown on every save.</summary>
    public Func<int?> PickSlot { get; set; }

    /// <summary>
    /// <paramref name="laneWidth"/> is the width of one lane on the main screen, so the sheet looks the same; all eight
    /// lanes are shown whatever the key count.
    /// </summary>
    public NoteEditWindow(NoteEditSession session, VisualizerSettings settings, string songName, int? currentSlot,
        double laneWidth = 60, ISettingsLayoutStore? layout = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _layout = layout ?? MemorySettingsLayoutStore.Shared;
        _text = new UiText(settings.Language);
        _songName = songName ?? "";
        CurrentSlot = currentSlot;
        _songMs = session.Notes.Count == 0 ? 1000 : session.Notes[^1].EndMs;

        Title = _text["EditTitle"];
        // The sheet is as wide as eight lanes of the main screen; the window fits around it.
        _field.Width = Math.Clamp(laneWidth, 30, 160) * session.Tiles;
        SizeToContent = SizeToContent.Width;
        Height = 900;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Resources.MergedDictionaries.Add(new Theme());
        ThemeCatalog.Apply(Resources, settings.Theme);
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        PickSlot = () => EditSlotDialog.Pick(this, _settings, _songName, CurrentSlot, Slots, Session.SourceHash);

        _field.Session = session;
        _field.Settings = settings;
        _field.TopMs = _songMs > 0 ? Math.Min(_songMs, 8000) : 1000;
        _title.Inlines.Add(new System.Windows.Documents.Run(songName) { FontWeight = FontWeights.SemiBold });
        if (currentSlot is { } editing)
        {
            var slotText = new System.Windows.Documents.Run("   " + _text.Format("EditSlot", editing)) { FontSize = 12 };
            slotText.SetResourceReference(TextElement.ForegroundProperty, "MutedTextBrush");
            _title.Inlines.Add(slotText);
        }
        // The line under the sheet only speaks when a key could not do its job; the keys are in the manual.
        _note.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _picked.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        // Chords the rules no longer allow were split on opening; say so once, since saving keeps it.
        if (session.Loosened > 0) _note.Text = _text.Format("EditLoosened", session.Loosened);
        _save.Click += (_, _) => Save();
        _cancel.Content = _text["Cancel"];
        _cancel.Click += (_, _) => Close();
        _cancel.SetResourceReference(StyleProperty, "ToolButton");
        _keys.SetResourceReference(StyleProperty, "ToolButton");
        _keys.ToolTip = _text["EditKeysTitle"];
        _keys.Click += (_, _) => ToggleManual();
        BuildPanel();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_picked);
        actions.Children.Add(_save);
        actions.Children.Add(_cancel);
        actions.Children.Add(_options);
        actions.Children.Add(_keys);
        var top = new Grid { Margin = new Thickness(12, 10, 12, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(actions, 1);
        top.Children.Add(_title);
        top.Children.Add(actions);

        // The scroll bar sits right against the sheet, and the pair stays centred if the window is wider.
        var sheet = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        sheet.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sheet.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_scroll, 1);
        sheet.Children.Add(_field);
        sheet.Children.Add(_scroll);

        // The options sit over the sheet, not over the whole window, so the buttons that open them stay in reach.
        var middle = new Grid { ClipToBounds = true };
        middle.Children.Add(sheet);
        middle.Children.Add(_panel);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        var bottom = new Border { Padding = new Thickness(12, 6, 12, 10), Child = _note };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(middle);
        Content = root;

        _scroll.Minimum = 0;
        _scroll.Maximum = _songMs;
        _scroll.Value = ToScroll(_field.TopMs);
        _scroll.ValueChanged += ScrollChanged;
        _field.SizeChanged += (_, _) => UpdateScroll();
        _field.MouseLeftButtonDown += FieldPressed;
        _field.MouseMove += FieldMoved;
        _field.MouseLeftButtonUp += FieldReleased;
        _field.MouseWheel += FieldWheel;
        PreviewKeyDown += WindowKey;
        // Alt alone would put the window in menu mode, and the editor's keys would stop working until Alt or Esc was
        // pressed again; nothing here uses Alt, so it is kept away from the window.
        PreviewKeyUp += (_, e) => { if (IsAlt(e)) e.Handled = true; };
        Loaded += (_, _) => { UpdateScroll(); _field.Focus(); };
        // The editor takes the focus when it opens and whenever it comes back to the front (user rule).
        ContentRendered += (_, _) => { Activate(); _field.Focus(); };
        Activated += (_, _) => _field.Focus();
        Closed += (_, _) => _manual?.Close();
        UpdateSave();
    }

    /// <summary>How far the options move when they slide out.</summary>
    private const double PanelWidth = 264;
    private const string PanelKey = "EditPanel";
    private const string GhostKey = "EditGhostNotes";

    /// <summary>
    /// The editor's own options, slid out from the right instead of opened as another window: there are few of them and
    /// they only change what the sheet shows, so they belong beside it. They are UI state, kept for the run and never
    /// written into the settings JSON (<see cref="ISettingsLayoutStore"/>).
    /// </summary>
    private void BuildPanel()
    {
        _options.SetResourceReference(StyleProperty, "IconButton");
        _options.Content = char.ConvertFromUtf32(0xE713);
        _options.ToolTip = _text["EditOptions"];
        _options.Checked += (_, _) => ShowPanel(true);
        _options.Unchecked += (_, _) => ShowPanel(false);

        var title = new TextBlock { Text = _text["EditOptions"], FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        _ghost.Content = _text["EditGhostNotes"];
        _ghost.IsChecked = _layout.IsExpanded(GhostKey) ?? true;
        _ghost.Checked += (_, _) => Ghost(true);
        _ghost.Unchecked += (_, _) => Ghost(false);
        var hint = new TextBlock { Text = _text["EditGhostNotesHint"], FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var items = new StackPanel();
        items.Children.Add(title);
        items.Children.Add(_ghost);
        items.Children.Add(hint);
        _panel.Child = items;
        _panel.Padding = new Thickness(16);
        _panel.RenderTransform = _slide;
        _panel.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
        _panel.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        _field.ShowRuleNotes = _ghost.IsChecked == true;
        ShowPanel(_layout.IsExpanded(PanelKey) ?? false, animate: false);
    }

    private void Ghost(bool on)
    {
        _field.ShowRuleNotes = on;
        _layout.SetExpanded(GhostKey, on);
    }

    /// <summary>Whether the options are out.</summary>
    public bool PanelOpen => _panelOpen;

    /// <summary>Slides the options in or out; the gear button and the tests come through here.</summary>
    public void ShowPanel(bool open, bool animate = true)
    {
        _panelOpen = open;
        if (_options.IsChecked != open) _options.IsChecked = open;
        _panel.IsHitTestVisible = open;
        _layout.SetExpanded(PanelKey, open);
        var to = open ? 0 : PanelWidth;
        if (!animate)
        {
            // An animation holds the property at its last value, so it has to come off before the value is set.
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = to;
            return;
        }
        _slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(170)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Where the rules would put this song's notes with the settings as they are; drawn behind, never editable.</summary>
    public IReadOnlyList<EditNote> RuleNotes
    {
        get => _field.RuleNotes;
        set => _field.RuleNotes = value;
    }

    /// <summary>Whether that layer is drawn. The gear panel holds the toggle.</summary>
    public bool GhostNotes
    {
        get => _field.ShowRuleNotes;
        set => _ghost.IsChecked = value;
    }

    /// <summary>Opens the console, so the terminal is reachable from here as it is from the main window (Ctrl+T).</summary>
    public Action? ToggleConsole { get; set; }

    /// <summary>Draws the sheet again after something outside the window changed the notes (the console's override).</summary>
    public void Redrawn() => Redraw();

    /// <summary>The key manual beside the editor's top right; the "?" opens it and closes it again.</summary>
    public EditKeysWindow ToggleManual()
    {
        if (_manual is { IsVisible: true } open)
        {
            open.Close();
            return open;
        }
        var manual = new EditKeysWindow(_settings);
        manual.Closed += (_, _) => { if (ReferenceEquals(_manual, manual)) _manual = null; };
        _manual = manual;
        // An unshown editor (tests, captures) only builds it.
        if (!IsVisible) return manual;
        manual.Owner = this;
        manual.WindowStartupLocation = WindowStartupLocation.Manual;
        manual.Left = Left + ActualWidth - manual.Width - 16;
        manual.Top = Top + 64;
        manual.Show();
        return manual;
    }

    /// <summary>What the line beside the buttons says: how many notes are picked, once more than one is.</summary>
    public string PickedText => _picked.Text;

    private void UpdateScroll()
    {
        _scroll.ViewportSize = _field.WindowMs;
        _scroll.LargeChange = _field.WindowMs * 0.9;
        _scroll.Maximum = Math.Max(_field.WindowMs, _songMs);
    }

    // ---- mouse -----------------------------------------------------------

    private void FieldPressed(object sender, MouseButtonEventArgs e)
    {
        _field.Focus();
        var point = e.GetPosition(_field);
        var index = _field.NoteAt(point);
        if (index < 0)
        {
            // An empty spot starts a rubber band; without a modifier it also clears what was picked.
            if (Keyboard.Modifiers == ModifierKeys.None) Session.ClearSelection();
            _dragFrom = point;
            _field.CaptureMouse();
            Redraw();
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Session.Toggle(index);
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor >= 0) Session.SelectRange(_anchor, index);
        else Session.Select([index]);
        _anchor = index;
        Redraw();
    }

    private void FieldMoved(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(_field);
        _field.Band = new Rect(from, point);
    }

    private void FieldReleased(object sender, MouseButtonEventArgs e)
    {
        if (_dragFrom is null) return;
        if (_field.Band is { } band && band.Width + band.Height > 4)
            Session.Select(_field.NotesIn(band), add: Keyboard.Modifiers != ModifierKeys.None);
        _dragFrom = null;
        _field.Band = null;
        _field.ReleaseMouseCapture();
        Redraw();
    }

    private void FieldWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        // Nothing acts while another window has the focus (user rule).
        if (!IsActive) return;
        Wheel(e.Delta, Keyboard.Modifiers, e.GetPosition(_field));
    }

    /// <summary>
    /// The wheel scrolls, Shift+wheel ten times as far, and Ctrl+wheel zooms around the pointer.
    /// </summary>
    public void Wheel(int delta, ModifierKeys modifiers, Point pointer)
    {
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            var under = _field.TimeAt(pointer.Y);
            _field.Zoom *= delta > 0 ? 1.15 : 1 / 1.15;
            // Keep the moment under the pointer where it was, so zooming feels anchored.
            _field.TopMs = under + (pointer.Y - NoteEditField.HeaderHeight) / _field.Zoom;
            UpdateScroll();
            Sync();
            return;
        }
        var speed = modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        Scroll(delta / 120.0 * _field.WindowMs * 0.15 * speed);
    }

    private void Scroll(double deltaMs)
    {
        _field.TopMs = Math.Clamp(_field.TopMs + deltaMs, _field.WindowMs * 0.2, _songMs + _field.WindowMs * 0.2);
        Sync();
    }

    private void Sync()
    {
        _scroll.ValueChanged -= ScrollChanged;
        _scroll.Value = ToScroll(_field.TopMs);
        _scroll.ValueChanged += ScrollChanged;
    }

    private void ScrollChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => _field.TopMs = FromScroll(e.NewValue);

    // The sheet runs upward, so the top of the bar is the end of the song.
    private double ToScroll(double topMs) => Math.Clamp(_scroll.Maximum - topMs, _scroll.Minimum, _scroll.Maximum);
    private double FromScroll(double value) => _scroll.Maximum - value;

    // ---- keys ------------------------------------------------------------

    private static bool IsAlt(KeyEventArgs e) => e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt;

    private void WindowKey(object sender, KeyEventArgs e)
    {
        if (IsAlt(e))
        {
            e.Handled = true;
            return;
        }
        // Fixed shortcut, the same one the main and settings windows take.
        if (CmdWindow.IsShortcut(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            ToggleConsole?.Invoke();
            return;
        }
        // Keys act only while the editor itself has the focus (user rule).
        if (IsActive && Press(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>
    /// One key of the editor; true when it did something. The keys and what they do are listed in the manual (the "?" at
    /// the top right): a/d and 1-8 move, w/s step, Ctrl+F flips, Ctrl+R switches between chord and single notes, Ctrl+M
    /// merges, Ctrl+U splits, Ctrl+Z/Ctrl+Shift+Z undo and redo, Ctrl+A picks all, Ctrl+S saves into a picked slot,
    /// Ctrl+Shift+S over the slot being edited, and Ctrl+X closes.
    /// </summary>
    public bool Press(Key key, ModifierKeys modifiers)
    {
        var control = modifiers.HasFlag(ModifierKeys.Control);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        if (control)
        {
            switch (key)
            {
                case Key.Z:
                    Say((shift ? Session.Redo() : Session.Undo()) ? EditRefusal.None : EditRefusal.Blocked);
                    Redraw();
                    return true;
                case Key.A:
                    Session.SelectAll();
                    Redraw();
                    return true;
                case Key.F: Apply(Session.Flip()); return true;
                case Key.R: Apply(Session.SwitchChords(_settings.SimultaneousWindowMs)); return true;
                case Key.M: Apply(Session.MergeAll(_settings.SimultaneousWindowMs)); return true;
                case Key.U: Apply(Session.Split()); return true;
                case Key.S:
                    if (shift) SaveHere();
                    else Save();
                    return true;
                case Key.X:
                    Close();
                    return true;
                default: return false;
            }
        }
        switch (key)
        {
            case Key.A: Apply(Session.Nudge(-1)); break;
            case Key.D: Apply(Session.Nudge(1)); break;
            case Key.W: Move(1); break;
            case Key.S: Move(-1); break;
            case Key.Escape: Session.ClearSelection(); Redraw(); break;
            case >= Key.D1 and <= Key.D8: Apply(Session.ToTile(key - Key.D1 + 1)); break;
            case >= Key.NumPad1 and <= Key.NumPad8: Apply(Session.ToTile(key - Key.NumPad1 + 1)); break;
            default: return false;
        }
        return true;
    }

    private void Move(int direction)
    {
        var next = Session.Step(direction);
        if (next < 0) return;
        Session.Select([next]);
        _anchor = next;
        _field.Reveal(next);
        Sync();
        Redraw();
    }

    private void Apply(EditRefusal refusal)
    {
        Say(refusal);
        Redraw();
    }

    /// <summary>A wall or a lane out of sight says nothing; the rest put one line under the sheet.</summary>
    private void Say(EditRefusal refusal)
    {
        _note.Text = refusal switch
        {
            EditRefusal.LaneTaken => _text["EditLaneTaken"],
            EditRefusal.HoldInTheWay => _text["EditHoldInTheWay"],
            EditRefusal.BadMerge => _text["EditBadMerge"],
            EditRefusal.BadSplit => _text["EditBadSplit"],
            EditRefusal.MixedChords => _text["EditMixedChords"],
            EditRefusal.NoteBetween => _text["EditNoteBetween"],
            EditRefusal.LanesApart => _text["EditLanesApart"],
            _ => ""
        };
    }

    private void Redraw()
    {
        _field.InvalidateVisual();
        UpdateSave();
        var count = Session.Selection.Count;
        _picked.Text = count > 1 ? _text.Format("EditSelected", count) : "";
    }

    private void UpdateSave()
    {
        _save.Content = _text["Save"];
        _save.SetResourceReference(StyleProperty, Session.Changed ? "PrimaryButton" : "ToolButton");
    }

    // ---- saving ----------------------------------------------------------

    /// <summary>Always asks for the slot first; the picker itself warns before replacing a used one.</summary>
    public void Save()
    {
        if (PickSlot() is not { } slot || slot is < 1 or > EditSlotDialog.SlotCount) return;
        Finish(slot);
    }

    /// <summary>
    /// Ctrl+Shift+S: over the slot being edited after one question, without the picker. An
    /// original song has no slot yet, so the picker opens instead.
    /// </summary>
    public void SaveHere()
    {
        if (CurrentSlot is not { } slot)
        {
            Save();
            return;
        }
        if (!Ask(_text["EditTitle"], _text.Format("EditSaveHere", slot), _text["EditSlotReplace"], risky: true)) return;
        Finish(slot);
    }

    private void Finish(int slot)
    {
        Result = new NoteEditResult(Session.ToLayout(), slot);
        // Shown without modality (the console has to stay usable), so there is no dialog result to set and closing is what
        // ends it; a window that was never shown at all (tests, captures) has neither.
        try { DialogResult = true; }
        catch (InvalidOperationException) { if (IsVisible) Close(); }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (DialogResult == true || Result is not null || !Session.Changed) return;
        if (!Ask(_text["EditTitle"], _text["EditDiscard"], _text["Close"], risky: true)) e.Cancel = true;
    }

    /// <summary>Asks through <see cref="Confirm"/> when a test set it, otherwise with the themed dialog.</summary>
    private bool Ask(string title, string message, string accept, bool risky = false) => Confirm is { } confirm
        ? confirm(title, message, accept)
        : ConfirmDialog.Ask(this, _settings, title, message, accept, _text["Cancel"], risky);
}
