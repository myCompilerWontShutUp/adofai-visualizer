using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Visualizer.App.Input;

/// <summary>Read-only box in the settings dialog that records the next pressed key.</summary>
public sealed class KeyCaptureBox : TextBox
{
    private readonly bool _allowNone;
    private string _noneText = "";

    public KeyCaptureBox(string? token, bool allowNone = false)
    {
        _allowNone = allowNone;
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        TextAlignment = TextAlignment.Center;
        InputMethod.SetIsInputMethodEnabled(this, false);
        SetResourceReference(StyleProperty, typeof(TextBox));
        if (KeyNames.TryParse(token, out var key)) CapturedKey = key;
        Refresh();
    }

    public Key? CapturedKey { get; private set; }
    public string? Token => CapturedKey is { } key ? KeyNames.Token(key) : null;

    public string NoneText
    {
        get => _noneText;
        set { _noneText = value; Refresh(); }
    }

    /// <summary>Shows <paramref name="token"/>; an empty token clears the box only when a key is optional.</summary>
    public void SetToken(string? token)
    {
        if (KeyNames.TryParse(token, out var key)) CapturedKey = key;
        else if (_allowNone) CapturedKey = null;
        Refresh();
    }

    public void ClearKey()
    {
        if (!_allowNone) return;
        CapturedKey = null;
        Refresh();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = KeyNames.Real(e);
        if (key is Key.Tab or Key.Escape)
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        if (KeyNames.IsSupported(key))
        {
            CapturedKey = key;
            Refresh();
        }
        e.Handled = true; // Handled inside this dialog box only; global input is never consumed.
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e) => e.Handled = true;

    private void Refresh() => Text = CapturedKey is { } key ? KeyNames.Token(key) : _noneText;
}
