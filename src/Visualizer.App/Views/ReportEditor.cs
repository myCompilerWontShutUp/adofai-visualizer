using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Visualizer.App.Views;

/// <summary>
/// The note a <c>report</c> opens, edited inside the console window instead of in a window of its own (user rule). It
/// works the way a terminal editor does: the text is there to move around in, <c>i</c> switches to writing and Escape
/// leaves it again, and the line at the bottom says where the cursor is and takes the <c>:</c> commands.
///
/// It never touches a file. Saving and deleting go out through <see cref="Save"/> and <see cref="Delete"/>, which the
/// console window points at the app's own store.
/// </summary>
public sealed class ReportEditor : Grid
{
    /// <summary>What Tab puts in: a terminal editor never moves the focus away with it.</summary>
    private const string TabText = "    ";

    /// <summary>The short reminder on the right of the bottom line while the note is only being read (user rule).</summary>
    public const string Keys = "i write  :w save  :q leave  :d delete";

    private readonly TextBox _text = new()
    {
        Style = new Style(typeof(TextBox)),
        AcceptsReturn = true,
        AcceptsTab = false,
        IsUndoEnabled = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Padding = new Thickness(12, 10, 12, 6),
        FontFamily = CmdWindow.Mono,
        FontSize = 13,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        IsReadOnlyCaretVisible = true,
        // The notes are written in Korean; the console's own prompt line keeps the IME off, this does not.
        IsReadOnly = true
    };

    private readonly TextBlock _status = new()
    {
        FontFamily = CmdWindow.Mono,
        FontSize = 12,
        Padding = new Thickness(12, 4, 12, 6)
    };

    private string? _command;
    private (string Question, Action<bool> Answer)? _asking;
    private string _answer = "";
    private string _saved = "";

    public ReportEditor()
    {
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetRow(_status, 1);
        Children.Add(_text);
        Children.Add(_status);
        _text.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _text.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentBrush");
        _text.SetResourceReference(TextBoxBase.CaretBrushProperty, "AccentBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _status.SetResourceReference(TextBlock.BackgroundProperty, "SurfaceBrush");
        _text.TextChanged += (_, _) => ShowStatus();
        _text.SelectionChanged += (_, _) => ShowStatus();
        _text.PreviewKeyDown += (_, e) => { if (Press(e.Key, Keyboard.Modifiers)) e.Handled = true; };
        // While the text is read only the typed letters are the editor's own: ":" opens the command line, and an answer
        // to a question is one letter.
        _text.PreviewTextInput += (_, e) =>
        {
            if (Typed(e.Text)) e.Handled = true;
        };
    }

    /// <summary>The note's title, shown in the line at the bottom.</summary>
    public string Title { get; private set; } = "";

    /// <summary>
    /// The version this note was marked done in, or empty while it is open. It sits on the status line, which is the one
    /// part of the screen the writer cannot type into (user rule), and writing the note clears it in the store.
    /// </summary>
    public string Done { get; private set; } = "";

    /// <summary>The lines as they stand, which is what <see cref="Save"/> is given.</summary>
    public IReadOnlyList<string> Lines => _text.Text.Replace("\r\n", "\n").Split('\n');

    /// <summary>Whether the text differs from what was last written.</summary>
    public bool Changed => _text.Text != _saved;

    /// <summary>Whether typing goes into the note (i) or is read as the editor's own keys (Escape).</summary>
    public bool Insert { get; private set; }

    /// <summary>The line at the bottom, as the tests read it.</summary>
    public string Status => _status.Text;

    /// <summary>Writes the note; null when written, otherwise why not.</summary>
    public Func<IReadOnlyList<string>, string?>? Save { get; set; }

    /// <summary>Removes the note; the text says what happened, null when it went.</summary>
    public Func<string?>? Delete { get; set; }

    /// <summary>Closes the editor and goes back to the console.</summary>
    public Action? Leave { get; set; }

    /// <summary>Puts text on the clipboard, which is what <c>:copy</c> does with the note.</summary>
    public Action<string>? Copy { get; set; }

    /// <summary>Ctrl+T while the note is open, which minimizes the console as it does everywhere else.</summary>
    public Action? ToggleConsole { get; set; }

    /// <summary>Puts a note in. <paramref name="lines"/> is null for a note that has not been written yet.</summary>
    public void Open(string title, IReadOnlyList<string>? lines, bool changed = false, string done = "")
    {
        Title = title ?? "";
        Done = done ?? "";
        _text.Text = lines is null ? "" : string.Join(Environment.NewLine, lines);
        _saved = changed ? _saved + " " : _text.Text;
        _text.CaretIndex = 0;
        _command = null;
        _asking = null;
        SetInsert(false);
        ShowStatus();
    }

    /// <summary>Gives the focus to the text, as opening the editor does.</summary>
    public void Start() => _text.Focus();

    /// <summary>
    /// One key of the editor; true when it did something. Escape leaves writing (and, before that, an open command or
    /// question); i starts writing; Tab puts spaces in. Everything else is the text box's own.
    /// </summary>
    public bool Press(Key key, ModifierKeys modifiers)
    {
        // Whatever was said last is answered by pressing anything at all.
        _said = "";
        if (CmdWindow.IsShortcut(key, modifiers))
        {
            ToggleConsole?.Invoke();
            return true;
        }
        if (key == Key.Escape)
        {
            if (_asking is not null) { _asking = null; _answer = ""; ShowStatus(); return true; }
            if (_command is not null) { _command = null; ShowStatus(); return true; }
            if (!Insert) return true;
            SetInsert(false);
            return true;
        }
        // A key that stands for a letter has to be left alone: swallowing it here would stop the text event that carries
        // the letter, which is where ":" and a y/n answer arrive. The text box is read only meanwhile, so nothing goes in.
        if (_asking is { } question)
        {
            switch (key)
            {
                case Key.Enter:
                    // Typed, then Enter, exactly like the console's own questions (user rule): a stray letter never acts.
                    bool? yes = _answer.Trim().ToLowerInvariant() switch { "y" or "yes" => true, "n" or "no" => false, _ => null };
                    _answer = "";
                    if (yes is not bool answer) { ShowStatus(); return true; }
                    _asking = null;
                    question.Answer(answer);
                    ShowStatus();
                    return true;
                case Key.Back:
                    _answer = _answer.Length == 0 ? "" : _answer[..^1];
                    ShowStatus();
                    return true;
                default: return false;
            }
        }
        if (_command is not null)
        {
            switch (key)
            {
                case Key.Enter:
                    var typed = _command;
                    _command = null;
                    RunCommand(typed);
                    return true;
                case Key.Back:
                    _command = _command.Length <= 1 ? null : _command[..^1];
                    ShowStatus();
                    return true;
                default: return false;
            }
        }
        if (Insert && key == Key.Tab)
        {
            var at = _text.CaretIndex;
            _text.Text = _text.Text.Insert(at, TabText);
            _text.CaretIndex = at + TabText.Length;
            return true;
        }
        if (Insert) return false;
        if (key == Key.I)
        {
            SetInsert(true);
            return true;
        }
        // Everything else while reading is left to the text box, which is read only and so changes nothing, and to the
        // text event after it. ":" only ever reaches the editor that way (it is typed as Shift and a key that varies by
        // keyboard), so this line must not swallow keys.
        return false;
    }

    /// <summary>A typed letter while the note is not being written: the command line, or the answer to a question.</summary>
    public bool Typed(string text)
    {
        _said = "";
        if (_asking is not null)
        {
            // The letters gather in the line until Enter; nothing happens on the key itself.
            _answer += text;
            ShowStatus();
            return true;
        }
        if (_command is not null)
        {
            _command += text;
            ShowStatus();
            return true;
        }
        if (Insert) return false;
        if (text == ":")
        {
            _command = ":";
            ShowStatus();
        }
        return true;
    }

    /// <summary>Runs a typed <c>:</c> line. Anything unknown is named in the status line and nothing happens.</summary>
    private void RunCommand(string typed)
    {
        switch (typed.Trim().ToLowerInvariant())
        {
            case ":w" or ":write":
                Write();
                return;
            case ":wq" or ":x":
                if (Write()) Leave?.Invoke();
                return;
            case ":q" or ":quit":
                if (!Changed) { Leave?.Invoke(); return; }
                Ask("Leave without writing?", yes => { if (yes) Leave?.Invoke(); });
                return;
            case ":copy":
                // What is selected, or the whole note when nothing is: the same thing a copy key does elsewhere.
                var chosen = _text.SelectionLength > 0 ? _text.SelectedText : _text.Text;
                if (Copy is null) { Say("Nothing can be copied here."); return; }
                Copy(chosen);
                Say(_text.SelectionLength > 0 ? "Selection copied." : $"\"{Title}\" copied, {Lines.Count} lines.");
                return;
            case ":d" or ":delete":
                Ask($"Delete \"{Title}\"?", yes =>
                {
                    if (!yes) return;
                    var problem = Delete?.Invoke();
                    if (problem is null) Leave?.Invoke();
                    else Say(problem);
                });
                return;
            default:
                Say($"Not an editor command: {typed}");
                return;
        }
    }

    private bool Write()
    {
        if (Save?.Invoke(Lines) is { } problem)
        {
            Say("Not written. " + problem);
            return false;
        }
        _saved = _text.Text;
        Say($"\"{Title}\" written, {Lines.Count} lines.");
        return true;
    }

    private void Ask(string question, Action<bool> answer)
    {
        _asking = (question, answer);
        _answer = "";
        ShowStatus();
    }

    private string _said = "";

    private void Say(string text)
    {
        _said = text;
        ShowStatus();
    }

    private void SetInsert(bool insert)
    {
        Insert = insert;
        _text.IsReadOnly = !insert;
        // Windows attaches the IME to a text box when it takes the focus, and not to a read-only one. The editor opens
        // read only and never loses the focus, so the IME stayed off for the whole session and Korean could not be typed
        // (ASCII arrives without it, which is why English worked). Turning it back on and handing the focus over again
        // is what makes Windows attach it.
        InputMethod.SetIsInputMethodEnabled(_text, insert);
        if (insert && _text.IsKeyboardFocused) Refocus();
        _said = "";
        ShowStatus();
    }

    /// <summary>Takes the focus away from the text and gives it back, keeping the cursor and selection where they were.</summary>
    private void Refocus()
    {
        var at = _text.SelectionStart;
        var length = _text.SelectionLength;
        Focusable = true;
        Focus();
        _text.Focus();
        Focusable = false;
        _text.Select(at, length);
    }

    /// <summary>The line at the bottom: a question or a typed command while one is open, otherwise where the cursor is.</summary>
    private void ShowStatus()
    {
        if (_asking is { } asking)
        {
            _status.Text = $"{asking.Question} [Y/n]{(_answer.Length > 0 ? " " + _answer : "")}";
            return;
        }
        if (_command is not null)
        {
            _status.Text = _command;
            return;
        }
        var caret = _text.CaretIndex;
        var line = _text.GetLineIndexFromCharacterIndex(caret);
        var column = caret - _text.GetCharacterIndexFromLineIndex(line);
        // Something to say takes the line on its own until the next key, as a terminal editor does.
        if (_said.Length > 0)
        {
            _status.Text = _said;
            return;
        }
        var mark = Changed ? " [+]" : "";
        // Writing opens a note again, so a note being written in is shown as open from the first keystroke.
        var state = Done.Length > 0 && !Changed ? $"  done {Done}" : "  open";
        var where = string.Create(CultureInfo.InvariantCulture, $"{line + 1},{column + 1}");
        // The right of the line says what typing does now: the mode while writing, and otherwise the short list of keys,
        // since nothing else says them once the console line that used to is gone (user rule).
        _status.Text = $"\"{Title}\"{mark}{state}  {Lines.Count} lines  {where}".PadRight(56) + (Insert ? "-- INSERT --" : Keys);
    }
}
