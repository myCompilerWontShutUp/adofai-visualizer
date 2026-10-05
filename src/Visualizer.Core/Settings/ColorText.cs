using System.Globalization;

namespace Visualizer.Core.Settings;

/// <summary>
/// Color text for settings: <c>#RGB</c>, <c>#RRGGBB</c>, <c>#AARRGGBB</c> (the # may be left out for 6 or 8 digits),
/// <c>rgb(r, g, b)</c>, <c>rgba(r, g, b, a)</c> with a from 0 to 1, or <c>r, g, b</c>. Values are ARGB and normalize to HEX.
/// </summary>
public static class ColorText
{
    public static bool TryParse(string? text, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.StartsWith('#')) return TryHex(value[1..], out argb);
        if (value.Length is 6 or 8 && value.All(Uri.IsHexDigit)) return TryHex(value, out argb);
        string body;
        var withAlpha = false;
        if (value.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) && value.EndsWith(')'))
        {
            body = value[5..^1];
            withAlpha = true;
        }
        else if (value.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) && value.EndsWith(')')) body = value[4..^1];
        else body = value;
        var parts = body.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != (withAlpha ? 4 : 3)) return false;
        var channels = new uint[3];
        for (var i = 0; i < 3; i++)
        {
            if (!byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var channel)) return false;
            channels[i] = channel;
        }
        uint alpha = 255;
        if (withAlpha)
        {
            if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction)
                || !double.IsFinite(fraction) || fraction is < 0 or > 1) return false;
            alpha = (uint)Math.Round(fraction * 255);
        }
        argb = (alpha << 24) | (channels[0] << 16) | (channels[1] << 8) | channels[2];
        return true;
    }

    public static string ToHex(uint argb) => (argb >> 24) == 0xFF
        ? "#" + (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture)
        : "#" + argb.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>The design settings' own form: <c>r, g, b</c>, or <c>rgba(r, g, b, a)</c> when the color is not opaque.</summary>
    public static string ToRgb(uint argb)
    {
        var (r, g, b) = ((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);
        var text = string.Create(CultureInfo.InvariantCulture, $"{r}, {g}, {b}");
        return (argb >> 24) == 0xFF ? text
            : string.Create(CultureInfo.InvariantCulture, $"rgba({text}, {((argb >> 24) & 0xFF) / 255d:0.###})");
    }

    /// <summary>The color as HEX, or null when the text is not a color.</summary>
    public static string? Normalize(string? text) => TryParse(text, out var argb) ? ToHex(argb) : null;

    /// <summary>The color as "r, g, b", or null when the text is not a color.</summary>
    public static string? NormalizeRgb(string? text) => TryParse(text, out var argb) ? ToRgb(argb) : null;

    /// <summary>Splits a color list on whitespace outside parentheses, so <c>rgb(1, 2, 3)</c> stays one entry.</summary>
    public static IReadOnlyList<string> SplitList(string? text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var character in text ?? "")
        {
            if (character == '(') depth++;
            else if (character == ')') depth = Math.Max(0, depth - 1);
            if (char.IsWhiteSpace(character) && depth == 0)
            {
                if (current.Length > 0) tokens.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(character);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static bool TryHex(string digits, out uint argb)
    {
        argb = 0;
        if (digits.Length == 0 || !digits.All(Uri.IsHexDigit)) return false;
        if (digits.Length == 3) digits = string.Concat(digits.Select(c => new string(c, 2)));
        if (digits.Length is not (6 or 8)) return false;
        argb = uint.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (digits.Length == 6) argb |= 0xFF000000;
        return true;
    }
}
