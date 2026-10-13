using System.Windows.Input;

namespace Visualizer.App.Input;

/// <summary>Setting tokens for keys. Punctuation keeps its printed character so ";" stays readable.</summary>
public static class KeyNames
{
    private static readonly (Key Key, string Token)[] Aliases =
    [
        (Key.OemSemicolon, ";"), (Key.OemComma, ","), (Key.OemPeriod, "."), (Key.OemQuestion, "/"),
        (Key.OemQuotes, "'"), (Key.OemOpenBrackets, "["), (Key.OemCloseBrackets, "]"), (Key.OemMinus, "-"),
        (Key.OemPlus, "+"), (Key.OemPlus, "="), (Key.OemTilde, "`"), (Key.OemPipe, "\\"), (Key.Enter, "Enter"),
        (Key.PageUp, "PageUp"), (Key.PageDown, "PageDown"), (Key.CapsLock, "CapsLock"), (Key.PrintScreen, "PrintScreen")
    ];

    public static bool TryParse(string? token, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var text = token.Trim();
        foreach (var (candidate, alias) in Aliases)
        {
            if (!string.Equals(alias, text, StringComparison.OrdinalIgnoreCase)) continue;
            key = candidate;
            return true;
        }
        if (text.Length == 1 && char.IsAsciiDigit(text[0])) text = "D" + text;
        // Enum.TryParse would also accept raw numbers and comma-combined names.
        if (text.All(char.IsAsciiDigit) || text.Contains(',')) return false;
        return Enum.TryParse(text, true, out key) && Enum.IsDefined(key) && key != Key.None;
    }

    public static string Token(Key key)
    {
        foreach (var (candidate, alias) in Aliases)
            if (candidate == key) return alias;
        return key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : key.ToString();
    }

    /// <summary>
    /// The key that was pressed. With the Korean input method in Hangul mode a letter arrives as ImeProcessed, and with
    /// Alt as System; a shortcut read from Key alone would then never match.
    /// </summary>
    public static Key Real(KeyEventArgs e) => e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };

    public static bool IsSupported(Key key) =>
        key is not (Key.None or Key.Tab or Key.Escape or Key.System or Key.ImeProcessed or Key.DeadCharProcessed
            or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.Apps)
        && KeyInterop.VirtualKeyFromKey(key) != 0;
}
