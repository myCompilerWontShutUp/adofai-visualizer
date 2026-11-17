using System.Globalization;
using Visualizer.Core.Localization;
using static Visualizer.Core.Localization.ConsoleText;

namespace Visualizer.App.Commands;

/// <summary>What a command takes after its options; it also decides what Tab offers there.</summary>
public enum CmdArgument { None, Id, Command, Songs, Title, Range, Setting, SettingValue, SettingOrSection, Action, Json, Note, Prompt, Text, Areas, Tile, Pin, Redirect }

/// <summary>
/// One console command. <see cref="Options"/> lists option groups split by "/"; the options of one group, split by commas,
/// cannot be used together (user rule). <see cref="Keywords"/> are the fixed words its argument takes.
/// </summary>
public sealed record CmdCommand(string Name, string Group, string Usage, string Options,
    CmdArgument Argument, string Keywords = "", bool Hidden = false)
{
    /// <summary>One line on what it does, in the console's language (the language files' console part).</summary>
    public string Summary => T("Summary." + Name);

    /// <summary>What help says about it, in the console's language.</summary>
    public string Help => T("Help." + Name);

    public IReadOnlyList<IReadOnlyList<string>> OptionGroups { get; } = Options.Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(group => (IReadOnlyList<string>)group.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .Where(group => group.Count > 0).ToArray();

    public IEnumerable<string> OptionNames => OptionGroups.SelectMany(group => group);

    /// <summary>
    /// The words the argument takes. One of them may be written with a "*" in <see cref="Keywords"/>: that is the one a
    /// bare command means, and help shows it in brackets rather than saying so in a sentence (user rule).
    /// </summary>
    /// <summary>
    /// Whether the last argument is a list, written "&lt;area&gt;, …" in the usage: words split by commas, spaces or not,
    /// with nothing after them (user rule).
    /// </summary>
    public bool Variadic => Usage.Contains('…');

    public IReadOnlyList<string> KeywordNames { get; } = Keywords
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(word => word.TrimEnd('*')).ToArray();

    /// <summary>The keyword a bare command means, or null when a keyword has to be typed.</summary>
    public string? DefaultKeyword { get; } = Keywords
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(word => word.EndsWith('*'))?.TrimEnd('*');

    /// <summary>How help writes one keyword: the default one in brackets, so it is seen and not read (user rule).</summary>
    public string KeywordText(string keyword) =>
        string.Equals(keyword, DefaultKeyword, StringComparison.Ordinal) ? $"[{keyword}]" : keyword;

    /// <summary>The options as help shows them: one pair of brackets per group, as in [-id | -title] [-speed[:keep|reset]].</summary>
    public string OptionsText => string.Join(' ', OptionGroups.Select(group =>
        "[" + string.Join(" | ", group.Select(name => CmdCatalog.Option(name) is { } known ? known.Option.Display : name)) + "]"));
}

/// <summary>
/// A parsed line: the command, its options with the value each carries, the words after them, and the raw text after them
/// (set values, JSON, titles). <see cref="Number"/> is the n of an option written with a number, such as load -slot:3.
/// </summary>
public sealed record CmdLine(CmdCommand Command, IReadOnlyDictionary<string, string> Options, IReadOnlyList<string> Arguments,
    string Rest, int? Number = null)
{
    /// <summary>
    /// Whether the line carries that option, written any way it may be written: -speed:keep, -speed (its default) and the
    /// old -sm all ask the same thing.
    /// </summary>
    public bool Has(string option) => CmdCatalog.Option(option) is { } asked
        && Options.TryGetValue(asked.Option.Name, out var value)
        && string.Equals(value, asked.Value, StringComparison.Ordinal);
}

/// <summary>Every console command in the order cmd lists them, grouped by what they do, and the parser for a typed line.</summary>
public static class CmdCatalog
{
    /// <summary>How an option that takes a number is written in the option lists: -slot:&lt;n&gt; stands for -slot:1 to -slot:9.</summary>
    public const string NumberMark = "<n>";
    /// <summary>A version as the app numbers them, major.minor.patch: -version:1.0.0.</summary>
    public const string VersionMark = "<version>";
    /// <summary>
    /// The answer to "uninstall", typed or asked about. Taking the visualizer off the computer is done in PowerShell only,
    /// never from inside it (user rule), so there is no such command; the word just says where it is done.
    /// </summary>
    public static string UninstallNote => T("UninstallNote");

    /// <summary>Whether <paramref name="text"/> is a version written as major.minor.patch.</summary>
    public static bool IsVersion(string text)
    {
        var parts = text.Split('.');
        return parts.Length == 3 && parts.All(part => part.Length is > 0 and <= 5 && part.All(char.IsAsciiDigit));
    }
    /// <summary>The most lines load lists to pick from; more asks for more of the title (user rule).</summary>
    public const int MaximumPicks = 99;

    public static IReadOnlyList<string> Groups { get; } = ["Help", "Songs", "Editing", "Song files", "Settings", "Console"];

    /// <summary>The words a &lt;string&gt; takes; all is the default of playlist, now the default of id and title.</summary>
    public static IReadOnlyList<string> SongGroups { get; } = ["base", "custom", "edited", "main", "now", "link", "family", "origin", "all"];

    /// <summary>The song words, with the one a bare command means marked so help can show it (user rule).</summary>
    private static string SongWords(string standsFor) =>
        string.Join(", ", SongGroups.Select(word => word == standsFor ? word + "*" : word));

    public static IReadOnlyList<CmdCommand> All { get; } =
    [
        new("cmd", "Help", "cmd", "-copy", CmdArgument.None),
        new("help", "Help", "help [<cmd>]", "-copy", CmdArgument.Command, "list*, last"),
        new("option", "Help", "option [<cmd>]", "-keywords / -copy", CmdArgument.Command, "list*, last"),
        new("keyword", "Help", "keyword [<cmd>]", "-options / -copy", CmdArgument.Command, "list*, last"),
        new("version", "Help", "version", "-number, -check / -copy", CmdArgument.None),
        new("fps", "Help", "fps", "-restart / -copy", CmdArgument.None),
        new("link", "Help", "link [<what>]",
            "-proceed / -copy", CmdArgument.None, "start, stop, tiles, follow, unfollow, again, auto, manual"),
        new("inspect", "Help", "inspect <what>", "-proceed / -flow / -runs / -copy", CmdArgument.None, "start, stop, result"),
        new("log", "Help", "log [<what>]", "-copy", CmdArgument.None, "list*, all, erase"),
        new("install", "Help", "install <what>[, …]",
            "-reinstall / -proceed", CmdArgument.Areas, "all, app, source, linker, maps, edits, data"),
        new("scannow", "Help", "scannow [<area>, …]", "-copy", CmdArgument.Areas, "all*, app, source, linker, maps, edits, data"),

        new("song", "Songs", "song", "-id, -title / -copy", CmdArgument.None),
        new("info", "Songs", "info <id>", "-title / -copy", CmdArgument.Id, "now, link"),
        new("id", "Songs", "id [<string>]", "-title / -copy", CmdArgument.Songs, SongWords("now")),
        new("title", "Songs", "title [<string>]", "-id / -copy", CmdArgument.Songs, SongWords("now")),
        new("load", "Songs", "load <title>", "-id, -origin, -slot / -speed / -hidden / -force", CmdArgument.Title, "origin, edited, link"),
        new("playlist", "Songs", "playlist [<string>]", "-id, -title / -hidden, -shown / -copy", CmdArgument.Songs, SongWords("all")),
        new("checkpoint", "Songs", "checkpoint [<number>]", "-copy", CmdArgument.None, "list, first, next, previous, last"),
        new("go", "Songs", "go <tile>", "", CmdArgument.Tile),
        new("pin", "Songs", "pin [<id>] <tile>", "-copy", CmdArgument.Pin, "now*, all"),
        new("unpin", "Songs", "unpin [<id>] <tile>", "", CmdArgument.Pin, "now*"),
        new("redirect", "Songs", "redirect [<id>] [<id>]", "-proceed / -copy", CmdArgument.Redirect, "list*, go, erase, all"),
        new("hide", "Songs", "hide <id>", "", CmdArgument.Id, "now"),
        new("show", "Songs", "show <id>", "", CmdArgument.Id, "now"),

        new("edit", "Editing", "edit [<id>]", "-proceed", CmdArgument.Id, "now*"),
        new("note", "Editing", "note [<id>] <range>", "-proceed / -json / -copy", CmdArgument.Range, "now*, all"),
        new("override", "Editing", "override <json>", "-proceed", CmdArgument.Json),
        new("retitle", "Editing", "retitle <title>", "-proceed", CmdArgument.Text),

        new("sync", "Song files", "sync [rebuild]", "-proceed", CmdArgument.None, "rebuild"),
        new("remove", "Song files", "remove <id>", "-proceed", CmdArgument.Id, "now"),
        new("restore", "Song files", "restore", "-proceed", CmdArgument.None),
        new("delete", "Song files", "delete <id>", "-proceed", CmdArgument.Id, "now"),

        new("get", "Settings", "get <setting>", "", CmdArgument.Setting),
        new("set", "Settings", "set <setting> <value>", "", CmdArgument.SettingValue),
        // A shortcut for the setting changed most often.
        new("speed", "Settings", "speed [<percent>]", "-rate", CmdArgument.None),
        new("autosave", "Settings", "autosave [on | off]", "", CmdArgument.None, "on, off"),
        new("property", "Settings", "property [<section>[.<setting>]]", "-copy", CmdArgument.SettingOrSection),
        new("export", "Settings", "export", "-copy", CmdArgument.None),
        new("import", "Settings", "import <json>", "-proceed", CmdArgument.Json),
        new("rollback", "Settings", "rollback", "-proceed", CmdArgument.None),
        new("reset", "Settings", "reset", "-proceed", CmdArgument.None),
        new("do", "Settings", "do <action>", "", CmdArgument.Action, string.Join(", ", ActionNames)),

        new("copy", "Console", "copy [<count>]", "-commands / -answers / -count / -paste", CmdArgument.Range),
        new("paste", "Console", "paste [<count>]", "-commands / -answers / -count / -copy", CmdArgument.Range),
        new("prompt", "Console", "prompt [<title>]", "-copy", CmdArgument.Prompt, "list*"),
        new("report", "Console", "report <what> [<title>]",
            "-force, -proceed / -copy / -log", CmdArgument.Note, "list, create, write, print, done, undone, ask, state, erase, sweep, rename, colon"),
        new("update", "Console", "update", "-force, -proceed / -version", CmdArgument.None),
        new("restart", "Console", "restart <what>", "-proceed / -copy", CmdArgument.None, "linker, app"),
        new("clear", "Console", "clear", "", CmdArgument.None),
        new("exit", "Console", "exit", "-keep", CmdArgument.None),

        new("date", "Console", "date", "-copy", CmdArgument.None),
        new("whoami", "Console", "whoami", "", CmdArgument.None)
    ];

    /// <summary>
    /// One option. <see cref="Name"/> is what help and Tab show; <see cref="Short"/> is the older short spelling, which
    /// keeps working (user rule: two ways to say a thing is fine, one word with two meanings is not). An option that takes
    /// a value lists its <see cref="Values"/> and must have a <see cref="Default"/>, so writing the name alone always means
    /// something (user rule).
    /// </summary>
    public sealed record CmdOption(string Name, string Short = "", string Values = "", string Default = "")
    {
        /// <summary>What it means, in the console's language.</summary>
        public string Help => T("Option." + Name);

        public IReadOnlyList<string> ValueNames { get; } = Values.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Whether a value may follow the name, as in -slot:3.</summary>
        public bool TakesValue => ValueNames.Count > 0;

        /// <summary>How help writes it: -speed[:keep|reset], -slot[:&lt;n&gt;], or just -copy.</summary>
        public string Display => TakesValue ? $"{Name}[:{string.Join('|', ValueNames)}]" : Name;
    }

    /// <summary>What every option means, whichever command has it. The name is the long one; the short spelling is an alias.</summary>
    public static IReadOnlyList<CmdOption> Options { get; } =
    [
        new("-copy", "-c"),
        new("-proceed", "-p"),
        new("-id"),
        new("-title", "-tl"),
        new("-hidden", "-h"),
        new("-shown", "-s"),
        new("-origin", "-og"),
        new("-slot", "-sl", NumberMark, ""),
        new("-speed", "", "keep,reset", "keep"),
        new("-force"),
        new("-rate", "-ar"),
        new("-keywords", "-k"),
        new("-options", "-o"),
        new("-number", "-n"),
        new("-check"),
        new("-restart", "-r"),
        new("-keep", "-m"),
        new("-json"),
        new("-paste", "-pa"),
        new("-commands", "", "with,only", "with"),
        new("-answers", "", "with,skip", "skip"),
        new("-count", "", "all,others", "others"),
        new("-version", "-v", "latest," + VersionMark, "latest"),
        new("-log"),
        new("-reinstall"),
        new("-flow", "-f"),
        new("-runs", "", NumberMark + ",*", "1")
    ];

    /// <summary>The areas scannow checks and install -reinstall names, in the order both work through them.</summary>
    public static IReadOnlyList<string> Areas { get; } = ["app", "source", "linker", "maps", "edits", "data"];

    /// <summary>The areas install -reinstall can put back; edits and data are the user's own (user rule).</summary>
    public static IReadOnlyList<string> Reinstallable { get; } = ["app", "source", "linker", "maps"];

    /// <summary>
    /// A list argument: words split by commas, with or without spaces (user rule), so "maps,linker" and "maps , linker"
    /// read the same. Null with <paramref name="error"/> when a place in it is empty.
    /// </summary>
    public static IReadOnlyList<string>? ListOf(CmdLine line, out string? error)
    {
        error = null;
        var text = string.Join(' ', line.Arguments);
        if (text.Trim().Length == 0) return [];
        var items = text.Split(',').Select(item => item.Trim().ToLowerInvariant()).ToArray();
        if (items.Any(item => item.Length == 0))
        {
            error = T("ListEmptyPlace", line.Command.Name);
            return null;
        }
        if (items.FirstOrDefault(item => item.Contains(' ')) is { } joined)
        {
            error = T("ListNeedsComma", joined, line.Command.Name);
            return null;
        }
        return items.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Every option by its name, and the short spellings that stand for a name and a value.</summary>
    private static readonly Dictionary<string, CmdOption> ByName =
        Options.ToDictionary(option => option.Name, StringComparer.Ordinal);

    /// <summary>The old short spellings: -sm is -speed:keep, -vo is -commands:only, -c is -copy.</summary>
    private static readonly Dictionary<string, (string Name, string Value)> Aliases = new(StringComparer.Ordinal)
    {
        ["-sm"] = ("-speed", "keep"),
        ["-sr"] = ("-speed", "reset"),
        ["-vs"] = ("-commands", "with"),
        ["-vo"] = ("-commands", "only"),
        ["-ep"] = ("-answers", "skip"),
        ["-en"] = ("-count", "others")
    };

    private static readonly Dictionary<string, string> ShortNames = Options
        .Where(option => option.Short.Length > 0)
        .ToDictionary(option => option.Short, option => option.Name, StringComparer.Ordinal);

    /// <summary>Every short spelling that still reads, which help never writes: it names options by their words (user rule).</summary>
    public static IEnumerable<string> ShortSpellings => ShortNames.Keys.Concat(Aliases.Keys);

    /// <summary>What every option means, by name, for the places that list them all.</summary>
    public static IReadOnlyDictionary<string, string> OptionHelp =>
        Options.ToDictionary(option => option.Name, option => option.Help, StringComparer.Ordinal);

    /// <summary>
    /// Reads a typed option into the name it is filed under and the value it carries: -sm becomes -speed keep, -slot:3
    /// becomes -slot 3, and a name on its own takes the option's default. Null when no option goes by that spelling.
    /// </summary>
    public static (CmdOption Option, string Value)? Option(string typed)
    {
        var text = (typed ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0) return null;
        var cut = text.IndexOfAny([':', '=']);
        var (head, value) = cut < 0 ? (text, "") : (text[..cut], text[(cut + 1)..]);
        if (Aliases.TryGetValue(head, out var alias) && value.Length == 0)
            return ByName.TryGetValue(alias.Name, out var named) ? (named, alias.Value) : null;
        if (ShortNames.TryGetValue(head, out var full)) head = full;
        // -sl3 and -slot3, the way slots were written before the colon; -v1.0.0 the same way.
        if (!ByName.ContainsKey(head) && value.Length == 0)
        {
            var digits = head.Length;
            while (digits > 1 && (char.IsAsciiDigit(head[digits - 1]) || head[digits - 1] == '.')) digits--;
            if (digits < head.Length)
            {
                value = head[digits..];
                head = head[..digits];
                if (ShortNames.TryGetValue(head, out var shortened)) head = shortened;
            }
        }
        if (!ByName.TryGetValue(head, out var option)) return null;
        if (!option.TakesValue) return value.Length == 0 ? (option, "") : null;
        if (value.Length == 0) return (option, option.Default);
        if (option.Values == NumberMark)
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 9
                ? (option, value) : null;
        // A count with words beside it (-runs:3, -runs:*): any whole number from 1, or one of the words.
        if (option.ValueNames.Contains(NumberMark, StringComparer.Ordinal))
            return option.ValueNames.Contains(value, StringComparer.Ordinal)
                || (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= 1) ? (option, value) : null;
        if (option.ValueNames.Contains(VersionMark, StringComparer.Ordinal))
            return value == "latest" || IsVersion(value) ? (option, value) : null;
        return option.ValueNames.Contains(value, StringComparer.Ordinal) ? (option, value) : null;
    }

    private static readonly HashSet<string> OwnOptionHelp =
    [
        "song -id",
        "song -title",
        "info -title",
        "playlist -id",
        "playlist -title",
        "id -title",
        "title -id",
        "load -id"
    ];

    /// <summary>What a keyword means; "command keyword" first, for the words that mean something else there.</summary>
    private static readonly HashSet<string> KeywordText =
    [
        "list",
        "all",
        "on",
        "off",
        "start",
        "stop",
        "tiles",
        "follow",
        "unfollow",
        "again",
        "auto",
        "manual",
        "linker",
        "app",
        "source",
        "maps",
        "edits",
        "data",
        "scannow all",
        "install all",
        "first",
        "next",
        "previous",
        "create",
        "write",
        "print",
        "done",
        "undone",
        "ask",
        "state",
        "colon",
        "rebuild",
        "last",
        "now",
        "link",
        "base",
        "custom",
        "edited",
        "main",
        "family",
        "origin",
        "playlist all",
        "id all",
        "title all",
        "note all",
        "note now",
        "checkpoint list",
        "pin all",
        "redirect list",
        "redirect go",
        "redirect erase",
        "redirect all",
        "report erase",
        "report sweep",
        "report rename",
        "report create",
        "report write",
        "report print",
        "report done",
        "report undone",
        "report ask",
        "report state",
        "report colon",
        "report list",
        "restart linker",
        "restart app",
        "inspect start",
        "inspect stop",
        "inspect result",
        "log list",
        "log all",
        "log erase",
        "load origin",
        "checkpoint last",
        "load edited",
        "clear-previous-checkpoint-key",
        "clear-next-checkpoint-key",
        "clear-settings-key",
        "map-folder",
        "data-folder",
        "view-playlist-details"
    ];

    /// <summary>The settings actions do runs; editing, resync and reset have commands of their own.</summary>
    public static IReadOnlyList<string> ActionNames =>
        ["clear-previous-checkpoint-key", "clear-next-checkpoint-key", "clear-settings-key", "map-folder", "data-folder", "view-playlist-details"];

    public static IEnumerable<CmdCommand> Visible => All.Where(command => !command.Hidden);

    /// <summary>Every keyword once, in the order commands list them, with the commands that take it.</summary>
    public static IReadOnlyList<(string Keyword, IReadOnlyList<string> Commands)> AllKeywords =>
        Visible.SelectMany(command => command.KeywordNames.Select(keyword => (keyword, command.Name)))
            .GroupBy(pair => pair.keyword, StringComparer.Ordinal)
            .Select(group => (group.Key, (IReadOnlyList<string>)group.Select(pair => pair.Name).ToArray())).ToArray();

    public static string OptionMeaning(CmdCommand command, string option) =>
        OwnOptionHelp.Contains(command.Name + " " + option) ? T("Option." + command.Name + " " + option) : OptionHelp[option];

    /// <summary>What a keyword means for that command; a word no command explains has no meaning, and asking for it throws.</summary>
    public static string KeywordMeaning(CmdCommand command, string keyword) =>
        KeywordText.Contains(command.Name + " " + keyword) ? T("Keyword." + command.Name + " " + keyword)
            : KeywordText.Contains(keyword) ? T("Keyword." + keyword) : throw new KeyNotFoundException($"{keyword} has no meaning.");

    /// <summary>
    /// What a keyword means on its own, for the commands that take it, or null when no command has that word. A word whose
    /// meaning differs per command is described per command by <see cref="KeywordMeaning"/>.
    /// </summary>
    public static (string Meaning, IReadOnlyList<string> Commands)? Keyword(string? word)
    {
        var wanted = (word ?? "").Trim().ToLowerInvariant();
        if (wanted.Length == 0) return null;
        var commands = Visible.Where(command => command.KeywordNames.Contains(wanted, StringComparer.Ordinal))
            .Select(command => command.Name).ToArray();
        if (commands.Length == 0) return null;
        return (KeywordText.Contains(wanted) ? T("Keyword." + wanted) : "", commands);
    }

    /// <summary>A command whose name is also a keyword, or null. The console keeps the two sets apart (user rule).</summary>
    public static string? NameClash() =>
        Visible.Select(command => command.Name).Where(name => !SharedNames.Contains(name, StringComparer.Ordinal))
            .FirstOrDefault(name => Visible.Any(command => command.KeywordNames.Contains(name, StringComparer.Ordinal)));

    /// <summary>
    /// The words a keyword may share with a command (user rule): load link, title link and the like name the song the link
    /// is playing, which is what the link command is about; redirect go goes to the redirected song as go goes to a tile
    /// (user decision).
    /// </summary>
    private static readonly string[] SharedNames = ["link", "go"];

    public static CmdCommand? Find(string? name) =>
        All.FirstOrDefault(command => string.Equals(command.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a word is written like an option: a hyphen, letters, and at most a number after them, as in -sl3. "-12" and "-"
    /// stay values.
    /// </summary>
    public static bool IsOption(string word)
    {
        if (word.Length < 2 || word[0] != '-' || !char.IsAsciiLetter(word[1])) return false;
        var letters = 1;
        while (letters < word.Length && char.IsAsciiLetter(word[letters])) letters++;
        var rest = word[letters..];
        // -slot3 as it used to be written, or -slot:3, -speed:keep and -runs:* as help writes them now.
        if (rest.Length > 0 && rest[0] is ':' or '=') return rest.Length > 1 && rest[1..].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '*');
        return rest.All(c => char.IsAsciiDigit(c) || c == '.');
    }

    /// <summary>
    /// Reads a typed line. Options stand after the command, split by spaces in any order, before or after its words
    /// (only before free text); each must be one of the command's, at most one per group. Null with <paramref name="error"/> set when the line cannot run; null without an
    /// error for a blank line.
    /// </summary>
    public static CmdLine? Parse(string? line, out string? error)
    {
        error = null;
        var text = (line ?? "").Trim();
        var words = Words(text);
        if (words.Count == 0) return null;
        if (Find(words[0].Word) is not { } command)
        {
            error = words[0].Word == "uninstall" ? UninstallNote
                : T("NotACommand", words[0].Word);
            return null;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        int? number = null;
        var next = 1;
        for (; next < words.Count && IsOption(words[next].Word); next++)
            if (Take(words[next].Word) is { } wrong)
            {
                error = wrong;
                return null;
            }
        var rest = next < words.Count ? text[words[next].Start..] : "";
        // A set value, pasted JSON or a title is free text, so what follows stays as it was typed. Anywhere else the
        // words are fixed ones, and an option may stand after them as well as before them (user rule).
        if (command.Argument is CmdArgument.SettingValue or CmdArgument.Json or CmdArgument.Title
            or CmdArgument.Note or CmdArgument.Prompt or CmdArgument.Text)
            return new CmdLine(command, options, words.Skip(next).Select(word => word.Word).ToArray(), rest, number);
        var arguments = new List<string>();
        var late = false;
        foreach (var (word, _) in words.Skip(next))
        {
            if (!IsOption(word) || Option(word) is null)
            {
                arguments.Add(word);
                continue;
            }
            if (Take(word) is { } wrong)
            {
                error = wrong;
                return null;
            }
            late = true;
        }
        // The raw text keeps its spacing unless an option had to be taken out from among the words.
        return new CmdLine(command, options, arguments, late ? string.Join(' ', arguments) : rest, number);

        // Reads one option into the line, or says what is wrong with it.
        string? Take(string word)
        {
            var typed = word.ToLowerInvariant();
            if (Option(typed) is not { } read)
            {
                // A value the option does not take is its own mistake; anything else is read as an unknown option.
                return Known(typed) is not { TakesValue: true } option
                    ? command.OptionGroups.Count == 0 ? T("NoOptions", command.Name)
                        : T("NotAnOption", typed, command.Name, command.OptionsText)
                    : option.Values == NumberMark ? T("NotASlot", typed, option.Name)
                    : option.ValueNames.Contains(VersionMark, StringComparer.Ordinal) ? T("NotAVersion", typed, option.Name)
                    : option.ValueNames.Contains(NumberMark, StringComparer.Ordinal) ? T("NotACount", typed, option.Name, option.Default)
                    : T("NotAValue", typed, option.Name, string.Join(T("Or"), option.ValueNames), option.Default);
            }
            if (!command.OptionNames.Contains(read.Option.Name))
                return command.OptionGroups.Count == 0 ? T("NoOptions", command.Name) : T("NotAnOption", typed, command.Name, command.OptionsText);
            if (options.TryGetValue(read.Option.Name, out var already))
                return already == read.Value ? T("GivenTwice", read.Option.Name) : T("GivenTwiceAs", read.Option.Name, already, read.Value);
            options[read.Option.Name] = read.Value;
            if (read.Option.Values == NumberMark && read.Value.Length > 0)
                number = int.Parse(read.Value, NumberStyles.None, CultureInfo.InvariantCulture);
            var group = command.OptionGroups.First(names => names.Contains(read.Option.Name));
            if (options.Keys.FirstOrDefault(other => other != read.Option.Name && group.Contains(other)) is not { } clash) return null;
            // -force does what would be refused, so it always asks; nothing may answer for it (user rule).
            return clash is "-force" or "-proceed" && read.Option.Name is "-force" or "-proceed"
                ? T("ForceAlwaysAsks") : T("CannotTogether", clash, read.Option.Name);
        }

        // The option a spelling names, whatever value follows it, for telling a wrong value from an unknown name.
        static CmdOption? Known(string typed)
        {
            var cut = typed.IndexOfAny([':', '=']);
            var head = cut < 0 ? typed : typed[..cut];
            // -sl0 names -slot with a value, the way -sl3 does, so a bad slot is told apart from an unknown name.
            if (cut < 0 && !ByName.ContainsKey(head) && !ShortNames.ContainsKey(head) && !Aliases.ContainsKey(head))
            {
                var digits = head.Length;
                while (digits > 1 && (char.IsAsciiDigit(head[digits - 1]) || head[digits - 1] == '.')) digits--;
                head = head[..digits];
            }
            if (Aliases.TryGetValue(head, out var alias)) head = alias.Name;
            else if (ShortNames.TryGetValue(head, out var full)) head = full;
            return ByName.TryGetValue(head, out var option) ? option : null;
        }
    }

    /// <summary>The words of a line with where each starts.</summary>
    public static IReadOnlyList<(string Word, int Start)> Words(string text)
    {
        var words = new List<(string, int)>();
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            words.Add((text[start..i], start));
        }
        return words;
    }
}
