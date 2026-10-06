using System.Globalization;

namespace Visualizer.App.Commands;

/// <summary>What a command takes after its options; it also decides what Tab offers there.</summary>
public enum CmdArgument { None, Id, Command, Songs, Title, Range, Setting, SettingValue, SettingOrSection, Action, Json, Note, Prompt, Text, Areas, Tile, Pin }

/// <summary>
/// One console command. <see cref="Options"/> lists option groups split by "/"; the options of one group, split by commas,
/// cannot be used together (user rule). <see cref="Keywords"/> are the fixed words its argument takes.
/// </summary>
public sealed record CmdCommand(string Name, string Group, string Usage, string Summary, string Help, string Options,
    CmdArgument Argument, string Keywords = "", bool Hidden = false)
{
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
    public const string UninstallNote =
        @"Uninstalling is not done from inside the visualizer: close it and run .\uninstall.ps1 in PowerShell, in its folder.";

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
    public static IReadOnlyList<string> SongGroups { get; } = ["base", "custom", "edited", "main", "now", "family", "origin", "all"];

    /// <summary>The song words, with the one a bare command means marked so help can show it (user rule).</summary>
    private static string SongWords(string standsFor) =>
        string.Join(", ", SongGroups.Select(word => word == standsFor ? word + "*" : word));

    public static IReadOnlyList<CmdCommand> All { get; } =
    [
        new("cmd", "Help", "cmd", "Show the commands.", "Show every command, grouped by what it does.", "-copy", CmdArgument.None),
        new("help", "Help", "help [<cmd>]", "Explain a command.",
            "Explain a command, with its options and keywords. With nothing, every command.", "-copy", CmdArgument.Command, "list*, last"),
        new("option", "Help", "option [<cmd>]", "Show the options.",
            "With nothing, every option and what it does; with a command, that command's options.", "-keywords / -copy", CmdArgument.Command, "list*, last"),
        new("keyword", "Help", "keyword [<cmd>]", "Show the keywords.",
            "With nothing, every keyword and the commands that take it; with a command, that command's keywords.", "-options / -copy",
            CmdArgument.Command, "list*, last"),
        new("version", "Help", "version", "Show the app version.",
            "Show the app version, the date it was applied, when this build was made and whether it is the latest version, which asks "
            + "the repository. -number prints the number alone and asks nothing.", "-number / -copy", CmdArgument.None),
        new("fps", "Help", "fps", "Show how smoothly it draws.",
            "Show the frame intervals of the last few seconds, how long the app itself took in them, the garbage collections meanwhile and the render tier. -restart starts a new measurement after printing.",
            "-restart / -copy", CmdArgument.None),
        new("link", "Help", "link [<what>]", "Show or drive the game link.",
            "With nothing, show what the read-only sync mod is sending: how often readings arrive, how much the delay varies, the game's state, speed and level, and the state changes so far. "
            + "start opens the pipe and stop closes it; tiles compares the game's own tile times with the loaded song; follow puts the notes on the game's clock and unfollow takes them back; again starts a new measurement. "
            + "Nothing is sent to the game and nothing is saved.",
            "-copy", CmdArgument.None, "start, stop, tiles, follow, unfollow, again, auto, manual"),
        new("install", "Help", "install <what>[, …]", "Install the game link mod, or put a part back.",
            "install linker writes the read-only link mod into the game's own Mods folder, which is the one place outside this app's folders it ever writes, and only when Unity Mod Manager is there. "
            + "It asks first, writes the mod's two files and nothing else, and leaves other mods alone. A mod already there with the same version is left as it is. "
            + "-reinstall writes a part again when scannow finds it wrong: source puts this version's files back, app builds it again, linker writes this version's mod again, "
            + "maps builds the custom maps again, and all is this version in full. Your edits and data are your own and are never reinstalled. "
            + "Name several with commas, as in install -reinstall app, linker.",
            "-reinstall / -proceed", CmdArgument.Areas, "all, app, source, linker, maps, edits, data"),
        new("scannow", "Help", "scannow [<area>, …]", "Check the app and its files.",
            "Check that each area is whole and print what is wrong; nothing is changed. app is this build against its source and its language and "
            + "theme files, source the repository's files against this version, linker the installed mod, maps the map copies against the song "
            + "list, edits every edited song, and data the settings and the files that keep the song list. Name several with commas, as in "
            + "scannow maps, linker. What install -reinstall can put right is named at the end.",
            "-copy", CmdArgument.Areas, "all*, app, source, linker, maps, edits, data"),

        new("song", "Songs", "song", "Show the loaded song.", "Show the loaded song's details, the same as info now.", "-id, -title / -copy", CmdArgument.None),
        new("info", "Songs", "info <id>", "Show a song's details.",
            "Show a song's id, title, artist, author, kind, length, notes (pressed ones), long notes, auto notes, difficulty, checkpoints, slot (origin for an original), hidden state and path.",
            "-title / -copy", CmdArgument.Id, "now"),
        new("id", "Songs", "id [<string>]", "Show song ids.", "Show song ids; an edit shows its slot. With nothing, the loaded song's.",
            "-title / -copy", CmdArgument.Songs, SongWords("now")),
        new("title", "Songs", "title [<string>]", "Show song titles.", "Show song titles; an edit shows its slot. With nothing, the loaded song's.",
            "-id / -copy", CmdArgument.Songs, SongWords("now")),
        new("load", "Songs", "load <title>", "Load a song.",
            "Load a base or custom song by any part of its title; a song with edits lists them to pick from, and a single match loads at once. "
            + "* is every song and . is the loaded one, which is how it reloads or takes an option. An edit loads by its full title, as in 1*Plum - R. "
            + "Songs hidden from the song list are left out unless -hidden asks for them. Unless keep speed on load is set, the playback speed goes back to 100%. "
            + "-force loads an unsupported song anyway, after asking.",
            "-id, -origin, -slot / -speed / -hidden / -force", CmdArgument.Title, "origin, edited"),
        new("playlist", "Songs", "playlist [<string>]", "List songs.",
            "List songs with their id, kind and title. With nothing, every song; main is the main window's list in its own order, "
            + "which is what this command used to print on its own.",
            "-id, -title / -hidden, -shown / -copy", CmdArgument.Songs, SongWords("all")),
        new("checkpoint", "Songs", "checkpoint [<number>]", "Move to a checkpoint.",
            "Prepare the loaded song from one of its checkpoints, the way the main window's list does. A number is the checkpoint of that name, "
            + "first is the song's beginning, last is the final one, and next and previous step through the list and wrap around at its ends. "
            + "With nothing, it says where the song stands now, and list shows every checkpoint of the song.",
            "-copy", CmdArgument.None, "list, first, next, previous, last"),
        new("go", "Songs", "go <tile>", "Start from a tile.",
            "Prepare the loaded song from a tile number, as the section box does: play begins at the first note at or after that tile, with the lanes "
            + "of a full run. The tile must be one this song presses, so a midspin tile or one inside a long note is refused. Tab offers the song's pins; "
            + "checkpoint first goes back to the beginning.",
            "", CmdArgument.Tile),
        new("pin", "Songs", "pin [<id>] <tile>", "Pin a tile for go.",
            "Pin a tile of a song so go can find it again: Tab after go offers the pins. A pin belongs to the song and every edit of it alike, "
            + "whichever of them it was set on. now, the default, is the loaded song; all lists the song's pins in tile order, numbered as "
            + "checkpoints are. The tile must be one go can start from.",
            "-copy", CmdArgument.Pin, "now*, all"),
        new("unpin", "Songs", "unpin [<id>] <tile>", "Remove a pin.",
            "Remove a pin from a song and every edit of it. now, the default, is the loaded song, and Tab offers its pins.",
            "", CmdArgument.Pin, "now*"),
        new("hide", "Songs", "hide <id>", "Hide a song from the song list.", "Hide a song from the song list. show brings it back.", "", CmdArgument.Id, "now"),
        new("show", "Songs", "show <id>", "Show a hidden song again.", "Show a hidden song in the song list again.", "", CmdArgument.Id, "now"),

        new("edit", "Editing", "edit [<id>]", "Edit a song's lanes and chords.",
            "Open the editor for a song; with nothing, the loaded one. The settings close first; their unapplied changes are applied or dropped as you answer.",
            "-proceed", CmdArgument.Id, "now*"),
        new("note", "Editing", "note [<id>] <range>", "Show a song's notes.",
            "Show a song's notes as it plays, as n<note> <tile> <ms to next>; a chord shows as n3-4 3-4, and an edit shows its own lanes. "
            + "now, the default, is what is on screen: while the editor is open, the lanes being edited, saved or not. "
            + "Ranges: all, a..b, a.., ..b or n, where -1 is the last note. -json prints them as JSON instead, which override reads back.",
            "-proceed / -json / -copy", CmdArgument.Range, "now*, all"),
        new("override", "Editing", "override <json>", "Set the lanes from JSON.",
            "Replace the open editor's lanes with the JSON pasted after the command, as note -json prints it. Only the note numbers and tiles are read; "
            + "a time that is there must match the note's own. The editor must be open.", "-proceed", CmdArgument.Json),
        new("retitle", "Editing", "retitle <title>", "Rename the loaded song.",
            "Give the loaded song the display name typed after the command, as the song list does. An edit keeps its n* prefix. Shows both names and asks first.",
            "-proceed", CmdArgument.Text),

        new("sync", "Song files", "sync [rebuild]", "Bring the map folder in.",
            "Copy the map folder's songs into the custom maps and read every app folder again, as the main window's button does; it asks nothing. "
            + "rebuild builds the custom maps from nothing instead, which is the only way a song no longer in the map folder leaves the list, and asks first.",
            "-proceed", CmdArgument.None, "rebuild"),
        new("remove", "Song files", "remove <id>", "Remove an edited song.",
            "Remove an edited song. restore brings back the one removed last.", "-proceed", CmdArgument.Id, "now"),
        new("restore", "Song files", "restore", "Bring back the removed song.",
            "Bring back the edited song removed last. Only that one is kept, so a second restore finds nothing.", "-proceed", CmdArgument.None),
        new("delete", "Song files", "delete <id>", "Delete any song for good.",
            "Delete a song for good; restore cannot bring it back. A custom map returns with the next sync.", "-proceed", CmdArgument.Id, "now"),

        new("get", "Settings", "get <setting>", "Show a setting.", "Show a setting's value, as in get general.language.", "", CmdArgument.Setting),
        new("set", "Settings", "set <setting> <value>", "Change a setting.",
            "Change a setting and apply it at once, as in set general.information-buttons false. A toggle also takes on/off, 1/0 and able/disable. rollback undoes it.",
            "", CmdArgument.SettingValue),
        // A shortcut for the setting changed most often.
        new("speed", "Settings", "speed [<percent>]", "Change the playback speed.",
            "Change the speed the notes play at, the same as set timing.speed; with nothing, show it. -rate changes the approach rate instead, "
            + "which is how long a note takes to come down (set display.travel, in ms, so a smaller number is faster). rollback undoes either.",
            "-rate", CmdArgument.None),
        new("autosave", "Settings", "autosave [on | off]", "Keep settings for the next run.",
            "Whether applying a setting also writes it to the store, the same as set general.autosave; with nothing, show it.",
            "", CmdArgument.None, "on, off"),
        new("property", "Settings", "property [<section>[.<setting>]]", "List sections, settings or values.",
            "With nothing, list the sections; with a section, its settings and values; with a setting, the values it takes.",
            "-copy", CmdArgument.SettingOrSection),
        new("export", "Settings", "export", "Show the settings as JSON.",
            "Show the settings as JSON, as Export settings does. The map and data folders are left out.", "-copy", CmdArgument.None),
        new("import", "Settings", "import <json>", "Apply settings from JSON.",
            "Apply the settings named in JSON pasted after the command; the rest stay. Shows what changes and asks first.", "-proceed", CmdArgument.Json),
        new("rollback", "Settings", "rollback", "Undo the last settings change.",
            "Undo the last change of the settings, whatever made it: the settings panel, a command, a load or a forced lane change. Each rollback steps back once more.",
            "-proceed", CmdArgument.None),
        new("reset", "Settings", "reset", "Reset the settings.",
            "Reset every setting to its default. The map and data folders stay, and rollback undoes it.", "-proceed", CmdArgument.None),
        new("do", "Settings", "do <action>", "Run a settings action.", "Run an action of the settings panel.", "", CmdArgument.Action,
            string.Join(", ", ActionNames)),

        new("copy", "Console", "copy [<count>]", "Copy what was printed.",
            "Copy the last lines of the screen to the clipboard; with a number, that many messages back, * for all of them, -1 for the first, or a..b for a range. "
            + "-commands keeps the typed commands with their Visualizer> prompt, -commands:only copies only those, -answers leaves out the answered "
            + "questions, and -count skips the copying itself when counting.",
            "-commands / -answers / -count / -paste", CmdArgument.Range),
        new("paste", "Console", "paste [<count>]", "Show what was printed again.",
            "Print the last messages of the screen again, counted the way copy counts them: a number goes that many back, * is all of them, "
            + "-1 is the first and a..b is a range. -copy copies them as well. In a prompt slot, /paste puts them in.",
            "-commands / -answers / -count / -copy", CmdArgument.Range),
        new("prompt", "Console", "prompt [<title>]", "Fill in a stored prompt.",
            "A prompt kept for pasting into a language model; with nothing, list the titles. A prompt with blanks asks for each one in turn: type the "
            + "text, or /paste and /note -json to put an output in, /skip to leave the blank, /back /again /cancel to move around. The filled prompt "
            + "is shown and copied once you answer Y. Nothing is sent anywhere and nothing about the app changes.",
            "-copy", CmdArgument.Prompt, "list*"),
        new("report", "Console", "report <what> [<title>]", "Write a note for the next version.",
            "A note left for whoever works on this next, kept in the app store until someone removes it. Every use says what it is doing, since "
            + "a word is always needed here: create <title> starts one and opens it, and refuses a title that is already there unless -force "
            + "replaces it after asking; write <title> opens one that is there to add to it, and refuses a title that is not. list shows them all "
            + "with the version they were written for, and a word after it keeps only the notes holding it. print puts a note here instead of "
            + "opening it; done and undone mark whether it is taken care of, a mark that lives beside the note where nobody can type it in or out; "
            + "ask <title> > <why> says it is not settled and what is still needed, in the language the settings are set to; state prints that "
            + "standing alone for an agent to read; erase removes a note and sweep removes every one already done; rename <old> > <new> gives one "
            + "another name; colon lists the editor's : commands.",
            "-force, -proceed / -copy", CmdArgument.Note,
            "list, create, write, print, done, undone, ask, state, erase, sweep, rename, colon"),
        new("update", "Console", "update", "Get the latest version.",
            "Ask the repository whether this copy is the latest one. When it is, it says so; when a newer version is there, it asks, "
            + "then closes the app, builds that version and opens it again, and an installed linker in the game is updated with it. "
            + "-force goes to a version even when this copy's own changes stand in the way, throwing them away, and -version:1.0.0 picks "
            + "the version; it always asks, and the risk is yours. The songs, the settings and the notes are never touched. It runs the "
            + "repository's own update.ps1, so the terminal does the same thing.",
            "-force, -proceed / -version", CmdArgument.None),
        new("clear", "Console", "clear", "Clear the screen.", "Clear the screen.", "", CmdArgument.None),
        new("exit", "Console", "exit", "Close the console.",
            "Clear the screen and close the console. exit -keep keeps the screen for the next time, as closing the window does.", "-keep", CmdArgument.None),

        new("date", "Console", "date", "Show the date and time.",
            "Show this computer's date and time. Nothing is read from the game and nothing is saved.", "-copy", CmdArgument.None),
        new("whoami", "Console", "whoami", "Show who this is.", "Show who this is.", "", CmdArgument.None)
    ];

    /// <summary>
    /// One option. <see cref="Name"/> is what help and Tab show; <see cref="Short"/> is the older short spelling, which
    /// keeps working (user rule: two ways to say a thing is fine, one word with two meanings is not). An option that takes
    /// a value lists its <see cref="Values"/> and must have a <see cref="Default"/>, so writing the name alone always means
    /// something (user rule).
    /// </summary>
    public sealed record CmdOption(string Name, string Help, string Short = "", string Values = "", string Default = "")
    {
        public IReadOnlyList<string> ValueNames { get; } = Values.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Whether a value may follow the name, as in -slot:3.</summary>
        public bool TakesValue => ValueNames.Count > 0;

        /// <summary>How help writes it: -speed[:keep|reset], -slot[:&lt;n&gt;], or just -copy.</summary>
        public string Display => TakesValue ? $"{Name}[:{string.Join('|', ValueNames)}]" : Name;
    }

    /// <summary>What every option means, whichever command has it. The name is the long one; the short spelling is an alias.</summary>
    public static IReadOnlyList<CmdOption> Options { get; } =
    [
        new("-copy", "Show the output and copy it to the clipboard too.", "-c"),
        new("-proceed", "Answer Y to every question and go ahead.", "-p"),
        new("-id", "Show only the id; with title, the id too; with load, find the song by its id."),
        new("-title", "Show only the title; with id, the title too.", "-tl"),
        new("-hidden", "Only hidden songs; with load, the hidden ones as well.", "-h"),
        new("-shown", "Only shown songs.", "-s"),
        new("-origin", "Load the original at once.", "-og"),
        new("-slot", "Only the edits; with a number, load that slot (1 to 9) at once.", "-sl", NumberMark, ""),
        new("-speed", "What the playback speed does on a load.", "", "keep,reset", "keep"),
        new("-force", "Go ahead with what would be refused, after asking: load an unsupported song as an older verified format version, "
            + "which may not time as the game does, start a note over a title report create would keep, or update over this copy's own changes."),
        new("-rate", "The approach rate instead of the playback speed: how long a note takes to reach the judgment line, in ms.", "-ar"),
        new("-keywords", "Show the keywords too.", "-k"),
        new("-options", "Show the options too.", "-o"),
        new("-number", "The number alone, with nothing around it.", "-n"),
        new("-restart", "Start a new measurement after printing.", "-r"),
        new("-keep", "Keep the screen for the next time the console opens.", "-m"),
        new("-json", "Print as JSON instead."),
        new("-paste", "Print the messages as well as copying them.", "-pa"),
        new("-commands", "Whether the typed commands come along, each with its Visualizer> prompt.", "", "with,only", "with"),
        new("-answers", "Whether the answered questions come along.", "", "with,skip", "skip"),
        new("-count", "What is counted when counting messages back.", "", "all,others", "others"),
        new("-version", "Which version to go to: latest, or one written as 1.0.0.", "-v", "latest," + VersionMark, "latest"),
        new("-reinstall", "Write it again even when it is there, as this version has it.")
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
            error = $"A place in the list is empty. Write it as {line.Command.Name} maps, linker.";
            return null;
        }
        if (items.FirstOrDefault(item => item.Contains(' ')) is { } joined)
        {
            error = $"'{joined}' needs a comma between its words, as in {line.Command.Name} maps, linker.";
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
    public static IReadOnlyDictionary<string, string> OptionHelp { get; } =
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
        if (option.ValueNames.Contains(VersionMark, StringComparer.Ordinal))
            return value == "latest" || IsVersion(value) ? (option, value) : null;
        return option.ValueNames.Contains(value, StringComparer.Ordinal) ? (option, value) : null;
    }

    private static readonly Dictionary<string, string> OwnOptionHelp = new(StringComparer.Ordinal)
    {
        ["song -id"] = "Show only the id.",
        ["song -title"] = "Show only the title.",
        ["info -title"] = "Show only the title.",
        ["playlist -id"] = "Show only the ids.",
        ["playlist -title"] = "Show only the titles.",
        ["id -title"] = "Show the title too.",
        ["title -id"] = "Show the id too.",
        ["load -id"] = "Find the song by its id (all 8 characters)."
    };

    /// <summary>What a keyword means; "command keyword" first, for the words that mean something else there.</summary>
    private static readonly Dictionary<string, string> KeywordText = new(StringComparer.Ordinal)
    {
        ["list"] = "Show them all.",
        ["all"] = "Every one of them.",
        ["on"] = "Switch it on.",
        ["off"] = "Switch it off.",
        ["start"] = "Open the pipe the read-only sync mod writes to.",
        ["stop"] = "Close it again.",
        ["tiles"] = "Compare the game's own tile times with the loaded song's hit times.",
        ["follow"] = "Put the notes on the game's clock instead of the start key.",
        ["unfollow"] = "Take the notes back off the game's clock.",
        ["again"] = "Start a new measurement.",
        ["auto"] = "Match the tiles by itself once the game loads the song that is loaded here.",
        ["manual"] = "Stop matching by itself; link follow still does it by hand.",
        ["linker"] = "The read-only game link mod.",
        ["app"] = "This build: the version it was built from, its source's later changes, and its language and theme files.",
        ["source"] = "The repository's files, against the version this copy is on.",
        ["maps"] = "The map copies against the song list: missing, changed or left behind.",
        ["edits"] = "Every edited song: its copy, its lanes and the original it was made from.",
        ["data"] = "The settings, the song list choices, the ids, the marks and the notes.",
        ["scannow all"] = "Every area.",
        ["install all"] = "This version in full with -reinstall: source, app, and the linker when it is installed.",
        ["first"] = "The song's beginning.",
        ["next"] = "The checkpoint after the one chosen now.",
        ["previous"] = "The checkpoint before the one chosen now.",
        ["create"] = "Start it and open it.",
        ["write"] = "Open what is already there and add to it.",
        ["print"] = "Print it here instead of opening it.",
        ["done"] = "Mark it taken care of.",
        ["undone"] = "Open it again.",
        ["ask"] = "Say it is not settled, and why.",
        ["state"] = "Print only where it stands.",
        ["colon"] = "List the commands typed after a colon.",
        ["rebuild"] = "Build the custom maps from nothing, so songs no longer in the map folder leave.",
        ["last"] = "The command run before this one.",
        ["now"] = "The loaded song.",
        ["base"] = "Base songs.",
        ["custom"] = "Custom songs.",
        ["edited"] = "Edited songs.",
        ["main"] = "The main window's list, in its order.",
        ["family"] = "The loaded song's original with all its edits.",
        ["origin"] = "The loaded song's original.",
        ["playlist all"] = "Every song.",
        ["id all"] = "Every song.",
        ["title all"] = "Every song.",
        ["note all"] = "Every note.",
        ["note now"] = "What is on screen: the lanes being edited while the editor is open, else the loaded song.",
        ["checkpoint list"] = "List the song's checkpoints, marking the one it starts from.",
        ["pin all"] = "List the song's pins, in tile order.",
        ["report erase"] = "Remove the note, after asking.",
        ["report sweep"] = "Remove every note already marked done.",
        ["report rename"] = "Give a note another name: report rename <old> > <new>.",
        ["report create"] = "Start a note with this title and open it; a title already there is refused unless -force replaces it.",
        ["report write"] = "Open a note that is already there to add to it.",
        ["report print"] = "Print it here instead of opening it.",
        ["report done"] = "Mark it taken care of, in this version.",
        ["report undone"] = "Open it again.",
        ["report ask"] = "Say it is not settled and why, in the language the settings are set to.",
        ["report state"] = "Print only whether it has been taken care of.",
        ["report colon"] = "List the commands typed after a colon.",
        ["report list"] = "List the notes, with the version each was written for; a word after it keeps only the ones holding it.",
        ["load origin"] = "The original of the loaded edit.",
        ["checkpoint last"] = "The song's final checkpoint.",
        ["load edited"] = "The edits of the loaded original; a single one loads at once.",
        ["clear-previous-checkpoint-key"] = "Clear the previous checkpoint key.",
        ["clear-next-checkpoint-key"] = "Clear the next checkpoint key.",
        ["clear-settings-key"] = "Clear the settings key.",
        ["map-folder"] = "Pick the map folder.",
        ["data-folder"] = "Pick the data folder.",
        ["view-playlist-details"] = "Open the song list details window."
    };

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
        OwnOptionHelp.TryGetValue(command.Name + " " + option, out var own) ? own : OptionHelp[option];

    public static string KeywordMeaning(CmdCommand command, string keyword) =>
        KeywordText.TryGetValue(command.Name + " " + keyword, out var own) ? own : KeywordText[keyword];

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
        return (KeywordText.TryGetValue(wanted, out var text) ? text : "", commands);
    }

    /// <summary>A command whose name is also a keyword, or null. The console keeps the two sets apart (user rule).</summary>
    public static string? NameClash() =>
        Visible.Select(command => command.Name).FirstOrDefault(name => Visible.Any(command => command.KeywordNames.Contains(name, StringComparer.Ordinal)));

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
        // -slot3 as it used to be written, or -slot:3 and -speed:keep as help writes them now.
        if (rest.Length > 0 && rest[0] is ':' or '=') return rest.Length > 1 && rest[1..].All(c => char.IsAsciiLetterOrDigit(c) || c == '.');
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
                : $"'{words[0].Word}' is not recognized as a command. Type 'cmd' to see the commands.";
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
                    ? command.OptionGroups.Count == 0 ? $"{command.Name} has no options."
                        : $"{typed} is not an option of {command.Name}. Its options: {command.OptionsText}."
                    : option.Values == NumberMark
                        ? $"{typed} is not a slot. Slots are 1 to 9, as in {option.Name}:3, or {option.Name} on its own for every edit."
                    : option.ValueNames.Contains(VersionMark, StringComparer.Ordinal)
                        ? $"{typed} is not a version. Write it as 1.0.0, as in {option.Name}:1.0.0, or {option.Name} on its own for the latest."
                        : $"{typed} is not one of {option.Name}'s values. It takes {string.Join(" or ", option.ValueNames)}, or {option.Name} on its own for {option.Default}.";
            }
            if (!command.OptionNames.Contains(read.Option.Name))
                return command.OptionGroups.Count == 0 ? $"{command.Name} has no options."
                    : $"{typed} is not an option of {command.Name}. Its options: {command.OptionsText}.";
            if (options.TryGetValue(read.Option.Name, out var already))
                return already == read.Value ? $"{read.Option.Name} is given twice."
                    : $"{read.Option.Name} is given twice, as {already} and {read.Value}.";
            options[read.Option.Name] = read.Value;
            if (read.Option.Values == NumberMark && read.Value.Length > 0)
                number = int.Parse(read.Value, NumberStyles.None, CultureInfo.InvariantCulture);
            var group = command.OptionGroups.First(names => names.Contains(read.Option.Name));
            if (options.Keys.FirstOrDefault(other => other != read.Option.Name && group.Contains(other)) is not { } clash) return null;
            // -force does what would be refused, so it always asks; nothing may answer for it (user rule).
            return clash is "-force" or "-proceed" && read.Option.Name is "-force" or "-proceed"
                ? "-force always asks first, so -proceed cannot answer for it."
                : $"{clash} and {read.Option.Name} cannot be used together.";
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
