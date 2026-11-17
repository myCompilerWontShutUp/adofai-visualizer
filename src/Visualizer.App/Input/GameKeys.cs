using System.Windows.Input;
using Visualizer.Core.Settings;

namespace Visualizer.App.Input;

/// <summary>
/// The keys the game's key limiter takes while this app runs (user request): the lane keys of the tiles the maximum tile
/// count shows, four on four lanes and eight on eight (with the auto tile count, of the lanes the loaded song uses), and
/// the reset key, written as the Unity key names the linker reads. Empty when a lane key has no Unity name, so the game
/// keeps its own list rather than lose a lane key.
/// </summary>
public static class GameKeys
{
    public static IReadOnlyList<string> For(VisualizerSettings settings, IEnumerable<int>? usedTiles = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var tokens = settings.GetLaneKeyTokens();
        var names = new List<string>();
        foreach (var tile in settings.GetPlayedTiles(usedTiles ?? []))
        {
            if (tile < 1 || tile > tokens.Count || !KeyNames.TryParse(tokens[tile - 1], out var key) || UnityName(key) is not { } name)
                return [];
            names.Add(name);
        }
        // The reset key counts too (user rule: Enter starts a song again); without a Unity name it is left out alone.
        if (KeyNames.TryParse(settings.ResetKey, out var reset) && UnityName(reset) is { } resetName) names.Add(resetName);
        return names.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Unity's KeyCode name for a key, or null for one it has no name for.</summary>
    public static string? UnityName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => "Alpha" + (key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => "Keypad" + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F15 => key.ToString(),
        Key.OemSemicolon => "Semicolon",
        Key.OemComma => "Comma",
        Key.OemPeriod => "Period",
        Key.OemQuestion => "Slash",
        Key.OemQuotes => "Quote",
        Key.OemOpenBrackets => "LeftBracket",
        Key.OemCloseBrackets => "RightBracket",
        Key.OemMinus => "Minus",
        Key.OemPlus => "Equals",
        Key.OemTilde => "BackQuote",
        Key.OemPipe => "Backslash",
        Key.Enter => "Return",
        Key.Space => "Space",
        Key.Back => "Backspace",
        Key.Tab => "Tab",
        Key.Escape => "Escape",
        Key.Left => "LeftArrow",
        Key.Right => "RightArrow",
        Key.Up => "UpArrow",
        Key.Down => "DownArrow",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.CapsLock => "CapsLock",
        Key.Multiply => "KeypadMultiply",
        Key.Add => "KeypadPlus",
        Key.Subtract => "KeypadMinus",
        Key.Divide => "KeypadDivide",
        Key.Decimal => "KeypadPeriod",
        Key.LeftShift => "LeftShift",
        Key.RightShift => "RightShift",
        Key.LeftCtrl => "LeftControl",
        Key.RightCtrl => "RightControl",
        Key.LeftAlt => "LeftAlt",
        Key.RightAlt => "RightAlt",
        _ => null
    };
}
