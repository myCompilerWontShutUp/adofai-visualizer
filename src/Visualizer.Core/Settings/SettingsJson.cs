using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Visualizer.Core.Localization;

namespace Visualizer.Core.Settings;

/// <summary>A rejected settings JSON: the reason, the settings it concerns, and for syntax errors the 1-based position.</summary>
public sealed class SettingsJsonException(string message, IReadOnlyList<string>? fields = null, long? line = null, long? column = null)
    : ArgumentException(message)
{
    public IReadOnlyList<string> Fields { get; } = fields ?? Array.Empty<string>();
    public long? Line { get; } = line;
    public long? Column { get; } = column;
}

/// <summary>
/// Settings as JSON text for copying between sessions. Import is strict: the text must be one JSON object whose
/// keys are setting names (any letter case); keys left out keep the current value. Every failure is a
/// <see cref="SettingsJsonException"/> with a readable reason, and nothing is applied unless the whole result is valid.
/// </summary>
public static class SettingsJson
{
    public const int MaximumLength = 64 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Values worked out from other settings (UseChordCenterPair and the like). Files from app 0.4.6 and earlier carry some of
    /// them, so they are accepted when they agree with the settings they come from; a value that disagrees was typed in by
    /// hand and is refused, since it could not change anything.
    /// </summary>
    private static readonly Dictionary<string, System.Reflection.PropertyInfo> Derived = typeof(VisualizerSettings).GetProperties()
        .Where(property => !property.CanWrite && property.GetIndexParameters().Length == 0)
        .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Settings that name folders on this PC. The app's own settings file keeps them; the copy made for sharing leaves them
    /// out, and importing one keeps this PC's folders, so settings from another PC never move the store.
    /// </summary>
    public static IReadOnlyList<string> MachinePaths { get; } =
        [nameof(VisualizerSettings.MapRootDirectory), nameof(VisualizerSettings.DataRootDirectory)];

    /// <summary>
    /// The name <see cref="VisualizerSettings.NoChordCenterPair"/> had before app 0.6.1, with the opposite meaning. A file that
    /// carries it was written while the chord rules were experiments, which did nothing unless experiments were on.
    /// </summary>
    private const string LegacyCenterPair = "ChordCenterPair";

    /// <summary>
    /// Settings that are gone. A file that still names one is read as if it did not (the app's own settings file keeps
    /// every setting, so refusing the name would throw all the others away with it).
    /// </summary>
    private static readonly HashSet<string> Retired = new(StringComparer.OrdinalIgnoreCase)
    {
        "SongSearch", // typing in the song box is always on (user rule)
    };

    /// <summary>Every setting, for the app's own settings file.</summary>
    public static string Export(VisualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(settings, Options);
    }

    /// <summary>Every setting except this PC's folders, for "Export settings".</summary>
    public static string ExportPortable(VisualizerSettings settings)
    {
        var json = (JsonObject)JsonNode.Parse(Export(settings))!;
        foreach (var name in MachinePaths) json.Remove(name);
        return json.ToJsonString(Options);
    }

    /// <summary>
    /// "Import settings": like <see cref="Import"/>, but this PC's folders stay as <paramref name="current"/> has them.
    /// <paramref name="keptPaths"/> tells whether the text named one (an export from app 0.4.6 or earlier does). Reasons
    /// are in <paramref name="messageLanguage"/> when given (the console asks for English), otherwise the current language.
    /// </summary>
    public static VisualizerSettings ImportPortable(string? text, VisualizerSettings current, out bool keptPaths, string? messageLanguage = null) =>
        Import(text, current, portable: true, messageLanguage, out keptPaths);

    /// <summary>The app's own settings file: every setting the text names, folders included.</summary>
    public static VisualizerSettings Import(string? text, VisualizerSettings current) => Import(text, current, portable: false, null, out _);

    private static VisualizerSettings Import(string? text, VisualizerSettings current, bool portable, string? messageLanguage, out bool keptPaths)
    {
        keptPaths = false;
        ArgumentNullException.ThrowIfNull(current);
        var language = messageLanguage ?? current.Language;
        string T(string key, params object?[] args) => Languages.Format(language, key, args);
        if (string.IsNullOrWhiteSpace(text))
            throw new SettingsJsonException(T("Json.Empty"));
        if (text.Length > MaximumLength)
            throw new SettingsJsonException(T("Json.TooLong", MaximumLength / 1024));

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = 8
            });
        }
        catch (JsonException error)
        {
            var line = (error.LineNumber ?? 0) + 1;
            var column = (error.BytePositionInLine ?? 0) + 1;
            throw new SettingsJsonException(T("Json.Syntax", line, column), line: line, column: column);
        }
        if (root is not JsonObject input)
            throw new SettingsJsonException(T("Json.NotObject"));

        var merged = (JsonObject)JsonNode.Parse(Export(current))!;
        var names = merged.Select(pair => pair.Key).ToArray();
        var unknown = new List<string>();
        var derived = new List<(System.Reflection.PropertyInfo Property, JsonNode? Value)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        JsonNode? legacyCenter = null;
        var hasLegacyCenter = false;
        try
        {
            foreach (var (key, value) in input)
            {
                var name = names.FirstOrDefault(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase));
                if (name is null && string.Equals(key, LegacyCenterPair, StringComparison.OrdinalIgnoreCase))
                {
                    if (hasLegacyCenter) throw new SettingsJsonException(T("Json.Repeated", LegacyCenterPair), [LegacyCenterPair]);
                    (legacyCenter, hasLegacyCenter) = (value, true);
                    continue;
                }
                if (name is null && Derived.TryGetValue(key, out var property))
                {
                    if (!seen.Add(property.Name))
                        throw new SettingsJsonException(T("Json.Repeated", property.Name), [property.Name]);
                    derived.Add((property, value));
                    continue;
                }
                if (name is null && Retired.Contains(key)) continue;
                if (name is null) { unknown.Add(key); continue; }
                if (!seen.Add(name))
                    throw new SettingsJsonException(T("Json.Repeated", name), [name]);
                if (portable && MachinePaths.Contains(name))
                {
                    keptPaths = true;
                    continue;
                }
                merged[name] = value?.DeepClone();
            }
        }
        catch (Exception error) when (error is ArgumentException and not SettingsJsonException or InvalidOperationException)
        {
            // System.Text.Json reports exactly repeated keys while enumerating the object.
            throw new SettingsJsonException(T("Json.RepeatedAny"));
        }
        if (unknown.Count > 0)
            throw new SettingsJsonException(T("Json.Unknown", string.Join(", ", unknown)), unknown);
        if (hasLegacyCenter) ReadLegacyChordRules(input, merged, legacyCenter, current, T);

        VisualizerSettings? result;
        try { result = merged.Deserialize<VisualizerSettings>(Options); }
        catch (JsonException error)
        {
            var path = (error.Path ?? "").TrimStart('$', '.');
            throw new SettingsJsonException(T("Json.WrongType", path), path.Length > 0 ? new[] { path } : null);
        }
        catch (NotSupportedException)
        {
            throw new SettingsJsonException(T("Json.Unsupported"));
        }
        if (result is null)
            throw new SettingsJsonException(T("Json.Blank"));
        var errors = result.ValidateFields();
        if (messageLanguage is not null && errors.Count > 0)
        {
            // Worded in the asked language; a language that does not exist only shows on the real value, so that one stays.
            var worded = (result with { Language = messageLanguage }).ValidateFields();
            errors = worded.Concat(errors.Where(error => error.Field == nameof(VisualizerSettings.Language))).ToArray();
        }
        if (errors.Count > 0)
            throw new SettingsJsonException(string.Join(Environment.NewLine, errors.Select(e => e.Message).Distinct()),
                errors.Select(e => e.Field).Distinct().ToArray());
        var disagreeing = derived
            .Where(item => item.Value?.ToJsonString() != JsonSerializer.SerializeToNode(item.Property.GetValue(result), Options)?.ToJsonString())
            .Select(item => item.Property.Name).ToArray();
        if (disagreeing.Length > 0)
            throw new SettingsJsonException(T("Json.Derived", string.Join(", ", disagreeing)), disagreeing);
        return result;
    }

    /// <summary>
    /// A file from before app 0.6.1. With experiments on, its chord rules were in effect and carry over (the center pair turned
    /// the other way round); with them off, they did nothing, so the rules that were in effect, the defaults, are taken instead.
    /// </summary>
    private static void ReadLegacyChordRules(JsonObject input, JsonObject merged, JsonNode? legacyCenter, VisualizerSettings current,
        Func<string, object?[], string> text)
    {
        bool? ReadFlag(JsonNode? node, string name)
        {
            if (node is null) return null;
            if (node is JsonValue value && value.TryGetValue<bool>(out var flag)) return flag;
            throw new SettingsJsonException(text("Json.WrongType", [name]), [name]);
        }
        var named = input.FirstOrDefault(pair => string.Equals(pair.Key, nameof(VisualizerSettings.ExperimentalFeatures), StringComparison.OrdinalIgnoreCase));
        var experiments = ReadFlag(named.Value, nameof(VisualizerSettings.ExperimentalFeatures)) ?? current.ExperimentalFeatures;
        var allowed = ReadFlag(legacyCenter, LegacyCenterPair) ?? false;
        // What these rules did while the file was written, not today's defaults: back then the chord rules only applied with
        // experiments on, so with them off a file's values did nothing and these are the rules that were in effect.
        merged[nameof(VisualizerSettings.NoChordCenterPair)] = experiments ? !allowed : true;
        if (experiments) return;
        merged[nameof(VisualizerSettings.ChordSectionLanes)] = false;
        merged[nameof(VisualizerSettings.ChordLanePreference)] = "None";
    }
}
