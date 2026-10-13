namespace Visualizer.Core.Localization;

/// <summary>
/// The console's words, from the "console" part of the language files. The console speaks the app's language (user
/// rule): the console session sets it before it handles a line, and the main window whenever the language setting is
/// applied, so a reason kept for later (a link note, an inspect stop) is in the language it was written in. Command, option
/// and keyword names, setting paths, titles and paths are never translated; only what is said around them.
/// </summary>
public static class ConsoleText
{
    /// <summary>The language the console speaks now; English until the app says otherwise.</summary>
    public static string Language { get; set; } = Languages.English;

    /// <summary>The console text under <paramref name="key"/>, as written.</summary>
    public static string T(string key) => Languages.ConsoleGet(Language, key);

    /// <summary>The console text under <paramref name="key"/> with its {0}-style placeholders filled.</summary>
    public static string T(string key, params object?[] args) => Languages.ConsoleFormat(Language, key, args);
}
