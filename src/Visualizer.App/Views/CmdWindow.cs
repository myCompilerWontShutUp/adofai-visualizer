using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Visualizer.App.Commands;
using Visualizer.App.Input;
using Visualizer.App.Themes;
using static Visualizer.Core.Localization.ConsoleText;

namespace Visualizer.App.Views;

/// <summary>
/// The console opened with Ctrl+T. Like cmd it is one screen: the transcript ends with the prompt line and the command is
/// typed right after it. Only the typed part can change; the transcript and command history
/// live in <see cref="CmdLog"/>, so closing and reopening the window keeps them until the app exits.
/// </summary>
public sealed class CmdWindow : Window
{
    /// <summary>The console's font: a terminal font with box-drawing and block characters as wide as letters.</summary>
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");
    private readonly CmdSession _session;
    private readonly ReportEditor _editor = new();
    private readonly TextBox _screen = new()
    {
        Style = new Style(typeof(TextBox)), // Not the rounded input style: a plain terminal surface.
        AcceptsReturn = true,
        IsUndoEnabled = false,
        // Long lines wrap like a narrow console window, so no (unthemed) horizontal scroll bar appears.
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Padding = new Thickness(12, 10, 12, 10),
        FontFamily = Mono,
        FontSize = 13,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    /// <summary>Everything on screen before the typed command: the transcript and the prompt (or the open question).</summary>
    private string _fixed = "";
    private string _draft = "";
    private bool _rendering;
    private int _historyIndex = -1;
    // Tab steps through the words that fit; any other key ends the round.
    private IReadOnlyList<string> _choices = [];
    private string _choiceBase = "";
    private int _choice = -1;
    /// <summary>How the console looked before it was minimized (normal or maximized), for Ctrl+T to bring it back that way.</summary>
    private WindowState _shownState = WindowState.Normal;

    /// <summary>Whether a note is open over the transcript.</summary>
    public bool IsWriting => _editor.Visibility == Visibility.Visible;

    /// <summary>The note editor, for the tests that press its keys.</summary>
    public ReportEditor Editor => _editor;

    private void OpenReport(ReportDraft draft)
    {
        _editor.Open(draft.Title, draft.Lines, draft.Changed, draft.Done);
        _editor.Visibility = Visibility.Visible;
        _screen.Visibility = Visibility.Collapsed;
        _editor.Start();
    }

    private void CloseReport()
    {
        _session.Log.Report = null;
        _editor.Visibility = Visibility.Collapsed;
        _screen.Visibility = Visibility.Visible;
        Render();
        _screen.Focus();
    }

    /// <summary>Keeps what is being written for the next time the console opens.</summary>
    private void KeepReport() => _session.Log.Report = new ReportDraft(_editor.Title, _editor.Lines, _editor.Changed, _editor.Done);

    /// <summary>
    /// The fixed shortcut that opens this window while the app has the focus: Ctrl+T. It is not a setting and cannot be
    /// rebound, so the key boxes never see it.
    /// </summary>
    public static bool IsShortcut(Key key, ModifierKeys modifiers) => key == Key.T && modifiers == ModifierKeys.Control;

    /// <summary>Whether a key event is Ctrl+T, whatever the input method made of the T.</summary>
    public static bool IsShortcut(KeyEventArgs e) => IsShortcut(KeyNames.Real(e), Keyboard.Modifiers);

    /// <summary>
    /// What Ctrl+T in this window does. The app sets it so one press toggles the console once, wherever it was seen; on
    /// its own the window minimizes itself.
    /// </summary>
    public Action? Shortcut { get; set; }

    public CmdWindow(CmdSession session, string theme)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Title = T("ConsoleTitle");
        // Roomy enough to show whoami's art (72 columns) in full when it opens, with room to spare.
        Width = 990;
        Height = 624;
        // Never so narrow that the art's lines wrap.
        MinWidth = 640;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        Resources.MergedDictionaries.Add(new Theme());
        ApplyTheme(theme);

        _screen.SetResourceReference(ForegroundProperty, "TextBrush");
        _screen.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentBrush");
        _screen.SetResourceReference(TextBoxBase.CaretBrushProperty, "AccentBrush");
        _screen.PreviewKeyDown += ScreenKeyDown;
        _screen.PreviewTextInput += (_, e) => { if (_session.Busy) e.Handled = true; else ToPromptLine(); };
        _screen.TextChanged += ScreenChanged;
        DataObject.AddPastingHandler(_screen, (_, e) =>
        {
            if (_session.Busy) e.CancelCommand();
            else ToPromptLine();
        });
        // Cut over the transcript copies instead, since the transcript cannot change.
        CommandManager.AddPreviewExecutedHandler(_screen, (_, e) =>
        {
            if (e.Command != ApplicationCommands.Cut || _screen.SelectionStart >= _fixed.Length) return;
            _screen.Copy();
            e.Handled = true;
        });
        // Two layers in one window (user rule): the transcript, and the note editor over it when one is open.
        _editor.Visibility = Visibility.Collapsed;
        _editor.Save = lines => _session.SaveReport(_editor.Title, lines);
        _editor.Delete = () => _session.DeleteReport(_editor.Title);
        _editor.Leave = CloseReport;
        _editor.Copy = _session.CopyOut;
        _editor.ToggleConsole = () => (Shortcut ?? ToggleMinimized)();
        var layers = new Grid();
        layers.Children.Add(_screen);
        layers.Children.Add(_editor);
        Content = layers;

        _session.Log.Changed += Render;
        _session.ExitRequested += Close;
        _session.ReportRequested += OpenReport;
        Closed += (_, _) =>
        {
            _session.Log.Changed -= Render;
            _session.ExitRequested -= Close;
            _session.ReportRequested -= OpenReport;
            // The note stays as it is, so opening the console again comes back to it (user rule).
            if (_editor.Visibility == Visibility.Visible) KeepReport();
        };
        // A note left open when the console was closed is there again when it opens.
        if (_session.Log.Report is { } open) OpenReport(open);
        Activated += (_, _) => { if (_editor.Visibility == Visibility.Visible) _editor.Start(); else _screen.Focus(); };
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _shownState = WindowState;
        };
        Render();
    }

    /// <summary>
    /// Ctrl+T once the console is open: minimizes it, or shows a minimized one as it was and gives
    /// it the focus. A console shown without the focus (tests) comes back without it too.
    /// </summary>
    public void ToggleMinimized()
    {
        if (WindowState != WindowState.Minimized)
        {
            WindowState = WindowState.Minimized;
            return;
        }
        WindowState = _shownState;
        if (ShowActivated) Activate();
    }

    /// <summary>The whole screen, prompt line included.</summary>
    public string OutputText => _screen.Text;

    /// <summary>What is typed after the prompt.</summary>
    public string InputText => _draft;

    /// <summary>Runs a line as if it were typed and entered.</summary>
    public void Run(string text)
    {
        _draft = text ?? "";
        Submit();
    }

    public void ApplyTheme(string theme)
    {
        ThemeCatalog.Apply(Resources, theme);
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
    }

    /// <summary>↑ (-1) and ↓ (+1) through earlier commands, as in cmd; past the newest one the line is empty again.</summary>
    public void RecallHistory(int direction)
    {
        var history = _session.Log.History;
        if (history.Count == 0 || _session.IsAsking) return;
        if (direction < 0) _historyIndex = _historyIndex < 0 ? history.Count - 1 : Math.Max(0, _historyIndex - 1);
        else if (_historyIndex < 0) return;
        else if (++_historyIndex >= history.Count) _historyIndex = -1;
        _draft = _historyIndex < 0 ? "" : history[_historyIndex];
        Render();
    }

    /// <summary>
    /// Tab: puts the first word that fits in place of the one being typed; pressed again it steps through the rest, and
    /// Shift+Tab steps back. On an empty line the first word is cmd.
    /// </summary>
    public void Complete(bool backward = false)
    {
        if (_choice >= 0 && _choices.Count > 0)
            _choice = (_choice + (backward ? _choices.Count - 1 : 1)) % _choices.Count;
        else
        {
            var choices = _session.Complete(_draft, out var start);
            if (choices.Count == 0) return;
            _choices = choices;
            _choiceBase = _draft[..start];
            _choice = backward ? choices.Count - 1 : 0;
        }
        _draft = _choiceBase + _choices[_choice];
        Render();
    }

    private void ScreenKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+T in the console minimizes it; pressed again in the app, it comes back.
        if (IsShortcut(e))
        {
            e.Handled = true;
            (Shortcut ?? ToggleMinimized)();
            return;
        }
        // A long command owns the screen while it runs: only Ctrl+T (above) and copying get through, as in a terminal.
        if (_session.Busy)
        {
            var copying = (Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key is Key.C or Key.A or Key.Insert;
            if (!copying) e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Tab or Key.LeftShift or Key.RightShift)) _choice = -1;
        var start = _screen.SelectionStart;
        var end = start + _screen.SelectionLength;
        var limit = _fixed.Length;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Enter:
                Submit();
                e.Handled = true;
                break;
            case Key.Tab:
                Complete(backward: shift);
                e.Handled = true;
                break;
            case Key.Up:
                RecallHistory(-1);
                e.Handled = true;
                break;
            case Key.Down:
                RecallHistory(1);
                e.Handled = true;
                break;
            case Key.Escape:
                // As in cmd, Esc clears the typed line.
                _draft = "";
                Render();
                e.Handled = true;
                break;
            case Key.Back or Key.Delete:
                // Nothing typed would be erased, so the transcript is left alone; a selection reaching into it loses only
                // its typed part.
                if (end < limit || (end == limit && (e.Key == Key.Back || start < end))) e.Handled = true;
                else if (start < limit) _screen.Select(limit, end - limit);
                break;
            case Key.Home when (Keyboard.Modifiers & ModifierKeys.Control) == 0 && start >= limit:
                _screen.Select(limit, shift ? end - limit : 0);
                e.Handled = true;
                break;
            case Key.Left when !shift && start == limit && end == limit:
                e.Handled = true;
                break;
        }
    }

    /// <summary>Typing or pasting always lands on the prompt line, as in a terminal, even after clicking the transcript.</summary>
    private void ToPromptLine()
    {
        if (_screen.SelectionStart < _fixed.Length) _screen.Select(_screen.Text.Length, 0);
    }

    private void ScreenChanged(object sender, TextChangedEventArgs e)
    {
        if (_rendering) return;
        if (_session.Busy) { Render(); return; }
        _choice = -1;
        var text = _screen.Text;
        if (text.Length >= _fixed.Length && string.CompareOrdinal(text, 0, _fixed, 0, _fixed.Length) == 0)
        {
            var typed = text[_fixed.Length..];
            if (typed.IndexOfAny(['\r', '\n']) < 0)
            {
                _draft = typed;
                return;
            }
            // One command per line: a pasted line break becomes a space.
            _draft = typed.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        }
        // Anything that reached the transcript is put back.
        Render();
    }

    private void Submit()
    {
        var text = _draft;
        _draft = "";
        _historyIndex = -1;
        _session.Submit(text);
        Render();
    }

    private void Render()
    {
        var lines = _session.Log.Lines;
        var transcript = string.Join(Environment.NewLine, lines);
        // While a long command runs its bar stands where the prompt would be, so nothing can be typed until it is done.
        // An open question is answered on its own line after "[Y/n]"; otherwise a prompt line follows the transcript.
        _fixed = _session.Busy ? (lines.Count > 0 ? transcript + Environment.NewLine : "") + _session.JobText()
            : _session.IsAsking ? transcript + " "
            : (lines.Count > 0 ? transcript + Environment.NewLine : "") + CmdSession.PromptText + " ";
        if (_session.Busy) _draft = "";
        _rendering = true;
        try
        {
            _screen.Text = _fixed + _draft;
            _screen.Select(_screen.Text.Length, 0);
        }
        finally { _rendering = false; }
        _screen.ScrollToEnd();
    }
}
