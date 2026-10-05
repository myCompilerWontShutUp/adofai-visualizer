using System.Text.Json;

namespace Visualizer.Core.Settings;

/// <summary>
/// The initial value of every setting, read from Settings/DefaultSettings.jsonc embedded at build. <see cref="VisualizerSettings"/>
/// takes its property defaults from here, so that file is the one place to change them.
/// </summary>
public static class DefaultSettings
{
    private const string Resource = "Visualizer.Settings.DefaultSettings.jsonc";
    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> Values = new(Load);

    /// <summary>The setting names listed in the file.</summary>
    public static IReadOnlyCollection<string> Keys => Values.Value.Keys.ToArray();

    public static string Text(string name) => Read(name, value => value.GetString())
        ?? throw new InvalidDataException($"DefaultSettings.jsonc: {name} must be a string.");

    public static string? OptionalText(string name) => Read(name, value => value.ValueKind == JsonValueKind.Null ? null : value.GetString());

    public static double Number(string name) => Read(name, value => value.GetDouble());

    public static int Integer(string name) => Read(name, value => value.GetInt32());

    public static bool Flag(string name) => Read(name, value => value.GetBoolean());

    private static T Read<T>(string name, Func<JsonElement, T> read)
    {
        if (!Values.Value.TryGetValue(name, out var value))
            throw new InvalidDataException($"DefaultSettings.jsonc has no value for {name}.");
        try { return read(value); }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"DefaultSettings.jsonc: {name} has the wrong type ({value.ValueKind}).", error);
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> Load()
    {
        using var stream = typeof(DefaultSettings).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidDataException("DefaultSettings.jsonc is not embedded.");
        using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in json.RootElement.EnumerateObject())
        {
            if (!values.TryAdd(entry.Name, entry.Value.Clone()))
                throw new InvalidDataException($"DefaultSettings.jsonc lists {entry.Name} twice.");
        }
        return values;
    }
}
