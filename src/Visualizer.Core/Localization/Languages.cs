using System.Globalization;
using System.Text.Json;

namespace Visualizer.Core.Localization;

/// <summary>One language file: its id (the file name), the name shown in the language list, and its list position.</summary>
public sealed record LanguageInfo(string Id, string Name, int Order);

/// <summary>
/// User-visible text, read from the language files embedded from Localization/Languages/*.json. Adding a file there adds a
/// language after a rebuild; no code lists languages. A key missing from a language falls back to English, then to the key.
/// </summary>
public static class Languages
{
    public const string Korean = "korean";
    public const string English = "english";
    public const string Default = Korean;
    private const string Fallback = English;
    private const string ResourcePrefix = "Visualizer.Languages.";

    /// <param name="Console">The console's words, kept apart from the windows' in the same file (user rule).</param>
    private sealed record LanguageFile(LanguageInfo Info, IReadOnlyDictionary<string, string> Strings, IReadOnlyDictionary<string, string> Console);

    private static readonly Lazy<IReadOnlyDictionary<string, LanguageFile>> Files = new(Load);
    private static readonly Lazy<IReadOnlyList<LanguageInfo>> Ordered = new(() =>
        Files.Value.Values.Select(file => file.Info).OrderBy(info => info.Order).ThenBy(info => info.Id, StringComparer.Ordinal).ToArray());

    /// <summary>Every language file, in list order.</summary>
    public static IReadOnlyList<LanguageInfo> All => Ordered.Value;

    public static bool Exists(string? id) => id is not null && Files.Value.ContainsKey(id);

    /// <summary>The file id matching <paramref name="id"/> in any letter case, or null.</summary>
    public static string? Resolve(string? id) => id is not null && Files.Value.TryGetValue(id, out var file) ? file.Info.Id : null;

    /// <summary>Keys of one language file, for checking that files stay complete.</summary>
    public static IReadOnlyCollection<string> Keys(string id) => Files.Value.TryGetValue(id, out var file)
        ? file.Strings.Keys.ToArray() : Array.Empty<string>();

    /// <summary>Keys of one language file's console part, for checking that files stay complete.</summary>
    public static IReadOnlyCollection<string> ConsoleKeys(string id) => Files.Value.TryGetValue(id, out var file)
        ? file.Console.Keys.ToArray() : Array.Empty<string>();

    public static string Get(string? language, string key) => Find(language, key, file => file.Strings);

    /// <summary>The text with {0}-style placeholders filled. Without arguments the text is returned as written, so literal braces stay.</summary>
    public static string Format(string? language, string key, params object?[] args) =>
        args.Length == 0 ? Get(language, key) : string.Format(CultureInfo.InvariantCulture, Get(language, key), args);

    /// <summary>A console text; like <see cref="Get"/>, a missing key falls back to English, then to the key.</summary>
    public static string ConsoleGet(string? language, string key) => Find(language, key, file => file.Console);

    /// <summary>A console text with its placeholders filled, the way <see cref="Format"/> fills a window's.</summary>
    public static string ConsoleFormat(string? language, string key, params object?[] args) =>
        args.Length == 0 ? ConsoleGet(language, key) : string.Format(CultureInfo.InvariantCulture, ConsoleGet(language, key), args);

    private static string Find(string? language, string key, Func<LanguageFile, IReadOnlyDictionary<string, string>> part)
    {
        if (language is not null && Files.Value.TryGetValue(language, out var file) && part(file).TryGetValue(key, out var text)) return text;
        return Files.Value.TryGetValue(Fallback, out var fallback) && part(fallback).TryGetValue(key, out var english) ? english : key;
    }

    private static IReadOnlyDictionary<string, LanguageFile> Load()
    {
        var assembly = typeof(Languages).Assembly;
        var files = new Dictionary<string, LanguageFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".json", StringComparison.Ordinal)) continue;
            var id = resource[ResourcePrefix.Length..^".json".Length];
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidDataException($"Language file {id} could not be read.");
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = json.RootElement;
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in root.GetProperty("strings").EnumerateObject())
                strings[entry.Name] = entry.Value.GetString() ?? "";
            var console = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("console", out var consolePart))
                foreach (var entry in consolePart.EnumerateObject())
                    console[entry.Name] = entry.Value.GetString() ?? "";
            var name = root.TryGetProperty("name", out var named) ? named.GetString() ?? id : id;
            var order = root.TryGetProperty("order", out var ordered) && ordered.TryGetInt32(out var value) ? value : int.MaxValue;
            files[id] = new LanguageFile(new LanguageInfo(id, name, order), strings, console);
        }
        return files;
    }
}
