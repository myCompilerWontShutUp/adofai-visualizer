using System.Text;

namespace Visualizer.Infrastructure.Parsing;

/// <summary>
/// In-memory compatibility for the text the game accepts but strict JSON does not: literal control characters inside
/// legacy strings, and separators the game's own reader never needed.
/// <para>
/// The game loads a level with GDMiniJSON (<c>ADOFAI.LevelData.LoadLevel</c> → <c>RDFile.ReadAllText</c> →
/// <c>GDMiniJSON.Json.Deserialize</c>). In that parser <c>ParseObject</c> and <c>ParseArray</c> reach the next member
/// through the default case of a switch on the next token and only skip a COMMA when one is there, and no method in it
/// throws (its single failure is returning null). So a level with a comma missing between two members loads in the game,
/// while System.Text.Json refuses it; the comma is put back here, in memory only. The other way round, a COMMA token is
/// read on its own and the loop simply goes on, so commas with no value before them (two in a row, or one right after the
/// bracket) are skipped there and dropped here. Nothing is ever written back: map files are read-only data (AGENTS.md
/// rule 3).
/// </para>
/// </summary>
internal static class AdofaiJson
{
    public static string NormalizeStrings(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new StringBuilder(source.Length);
        // The containers being read, so a missing separator is only added where the game would read a new member.
        var containers = new Stack<char>();
        // The last token finished a value, so the next value-starting token needs a separator in front of it.
        var afterValue = false;
        // The last token opened a container or was a comma, so a comma now separates nothing.
        var commaIdle = false;
        for (var i = source.Length > 0 && source[0] == '﻿' ? 1 : 0; i < source.Length; i++)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (char.IsWhiteSpace(c))
            {
                result.Append(c);
                continue;
            }
            if (c == '/' && (next == '/' || next == '*'))
            {
                i = CopyComment(source, i, result);
                continue;
            }
            if (c == '"')
            {
                if (afterValue) Separate(result, containers, '"');
                i = CopyString(source, i, result);
                // A string followed by a colon was a name, so the value after it still has no separator due.
                afterValue = NextSignificant(source, i + 1) != ':';
                commaIdle = false;
                continue;
            }
            if (c is '{' or '[')
            {
                if (afterValue) Separate(result, containers, c);
                containers.Push(c);
                result.Append(c);
                afterValue = false;
                commaIdle = true;
                continue;
            }
            if (c is '}' or ']')
            {
                if (containers.Count > 0) containers.Pop();
                result.Append(c);
                // The container itself is now a finished value.
                afterValue = true;
                commaIdle = false;
                continue;
            }
            // GDMiniJSON loops on past a COMMA token in both ParseObject and ParseArray (PLUM MEGAMIX 2: "requiredMods": [],,).
            if (c == ',' && commaIdle) continue;
            if (c is ',' or ':')
            {
                result.Append(c);
                afterValue = false;
                commaIdle = c == ',';
                continue;
            }
            // A number, true, false or null: taken to the next delimiter so the whole literal counts as one value.
            if (afterValue) Separate(result, containers, c);
            while (i < source.Length && !char.IsWhiteSpace(source[i]) && source[i] is not (',' or ':' or '}' or ']' or '{' or '[' or '"'))
                result.Append(source[i++]);
            i--;
            afterValue = true;
            commaIdle = false;
        }
        return result.ToString();
    }

    /// <summary>
    /// Writes the separator the game does not need. Inside an array any value may follow another, but inside an object
    /// only a name may: GDMiniJSON's <c>ParseObject</c> reads the next member with <c>ParseString</c>, which gives up on
    /// anything else, so a level the game could not read is left to fail here too.
    /// </summary>
    private static void Separate(StringBuilder result, Stack<char> containers, char starts)
    {
        if (containers.Count == 0) return;
        if (containers.Peek() == '[' || starts == '"') result.Append(',');
    }

    /// <summary>Copies a string as it stands, except that literal control characters are escaped. Returns its last index.</summary>
    private static int CopyString(string source, int start, StringBuilder result)
    {
        result.Append(source[start]);
        for (var i = start + 1; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\\')
            {
                result.Append(c);
                if (i + 1 < source.Length) result.Append(source[++i]);
                continue;
            }
            if (c == '"')
            {
                result.Append(c);
                return i;
            }
            if (c < ' ')
            {
                result.Append("\\u");
                result.Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }
            result.Append(c);
        }
        return source.Length - 1;
    }

    /// <summary>Copies a comment through, since the reader is told to skip comments rather than fail on them.</summary>
    private static int CopyComment(string source, int start, StringBuilder result)
    {
        var block = source[start + 1] == '*';
        result.Append(source[start]);
        result.Append(source[start + 1]);
        for (var i = start + 2; i < source.Length; i++)
        {
            result.Append(source[i]);
            if (!block && source[i] is '\r' or '\n') return i;
            if (block && source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/')
            {
                result.Append(source[++i]);
                return i;
            }
        }
        return source.Length - 1;
    }

    /// <summary>The next character that is not whitespace or a comment, or '\0' at the end of the text.</summary>
    private static char NextSignificant(string source, int start)
    {
        for (var i = start; i < source.Length; i++)
        {
            if (char.IsWhiteSpace(source[i])) continue;
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] is not ('\r' or '\n')) i++;
                continue;
            }
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i++;
                continue;
            }
            return source[i];
        }
        return '\0';
    }
}
