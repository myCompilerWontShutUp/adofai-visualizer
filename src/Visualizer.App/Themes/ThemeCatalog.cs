using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Visualizer.Core.Localization;

namespace Visualizer.App.Themes;

/// <summary>A theme file: its id (the file name), list position and display names by language id.</summary>
public sealed record ThemeInfo(string Id, int Order, IReadOnlyDictionary<string, string> Names)
{
    public string DisplayName(string? language) =>
        language is not null && Names.TryGetValue(language, out var name) ? name
        : Names.TryGetValue(Languages.English, out var english) ? english : Id;
}

/// <summary>
/// Window palettes read from the theme files embedded from Themes/Palettes/*.json. Styles and windows reference the brush keys
/// with DynamicResource, so applying another palette re-themes an open window. Adding a file adds a theme after a rebuild.
/// The note field keeps its own colors.
/// </summary>
public static class ThemeCatalog
{
    public const string Universe = "universe";
    public const string Dark = "dark";
    public const string White = "white";
    private const string ResourcePrefix = "Visualizer.Themes.";

    /// <summary>Brush keys every palette provides. A file missing one falls back to the universe theme's color.</summary>
    public static IReadOnlyList<string> BrushKeys { get; } =
    [
        "WindowBrush", "SurfaceBrush", "SurfaceRaisedBrush", "SurfaceHoverBrush", "PopupBrush", "HighlightBrush", "LineBrush", "LineHoverBrush",
        "TextBrush", "MutedTextBrush", "FaintTextBrush", "AccentBrush", "AccentSoftBrush", "OnAccentBrush", "FireBrush", "ChangedBrush", "FocusBrush",
        "HoverOverlayBrush", "PressedOverlayBrush", "PrimaryPressedOverlayBrush", "ErrorBrush", "ThumbBrush", "OrbitBrush",
        "PillBrush", "PillWaitingBrush", "PillWaitingTextBrush", "PillPlayingBrush", "PillPlayingTextBrush", "PillPausedBrush", "PillPausedTextBrush"
    ];

    private sealed class PaletteDictionary : ResourceDictionary { }

    private sealed record ThemeFile(ThemeInfo Info, IReadOnlyDictionary<string, Color> Colors, Color[] Primary, bool Stars, Color[] Lanes);

    private static readonly Lazy<IReadOnlyDictionary<string, ThemeFile>> Files = new(Load);
    private static readonly Lazy<IReadOnlyList<ThemeInfo>> Ordered = new(() =>
        Files.Value.Values.Select(file => file.Info).OrderBy(info => info.Order).ThenBy(info => info.Id, StringComparer.Ordinal).ToArray());

    public static IReadOnlyList<ThemeInfo> All => Ordered.Value;

    public static bool Exists(string? id) => id is not null && Files.Value.ContainsKey(id);

    /// <summary>The colors a theme file declares itself, for checking that files stay complete.</summary>
    public static IReadOnlyCollection<string> DeclaredKeys(string id) =>
        Files.Value.TryGetValue(id, out var file) ? file.Colors.Keys.ToArray() : Array.Empty<string>();

    /// <summary>
    /// The theme's note field lane backgrounds for screen lanes 1..8. A file lists one or more colors ("laneBackgrounds") that repeat
    /// across the lanes, so neighbouring lanes differ slightly. An unknown id or a file without them uses the universe theme's.
    /// </summary>
    public static IReadOnlyList<Color> LaneBackgrounds(string? theme)
    {
        var file = theme is not null && Files.Value.TryGetValue(theme, out var own) && own.Lanes.Length > 0 ? own : Files.Value[Universe];
        return Enumerable.Range(0, 8).Select(lane => file.Lanes[lane % file.Lanes.Length]).ToArray();
    }

    /// <summary>Replaces the palette merged into <paramref name="resources"/>; an unknown id uses the universe theme.</summary>
    public static void Apply(ResourceDictionary resources, string? theme)
    {
        foreach (var previous in resources.MergedDictionaries.OfType<PaletteDictionary>().ToArray())
            resources.MergedDictionaries.Remove(previous);
        resources.MergedDictionaries.Insert(0, Create(theme is not null && Files.Value.TryGetValue(theme, out var file) ? file : Files.Value[Universe]));
    }

    private static PaletteDictionary Create(ThemeFile file)
    {
        var fallback = Files.Value[Universe];
        var palette = new PaletteDictionary();
        foreach (var key in BrushKeys)
            palette[key] = Solid(file.Colors.TryGetValue(key, out var color) ? color : fallback.Colors[key]);
        var accent = file.Colors.TryGetValue("AccentBrush", out var ownAccent) ? ownAccent : fallback.Colors["AccentBrush"];
        // Flat colour, never a gradient (user rule): blue says active or yes, grey says off, red says careful. A theme
        // may still name its own blue; only the first colour is used now.
        palette["PrimaryBrush"] = Solid(file.Primary.Length >= 1 ? file.Primary[0] : accent);
        palette["ToggleOnBrush"] = Solid(accent);
        var window = file.Colors.TryGetValue("WindowBrush", out var ownWindow) ? ownWindow : fallback.Colors["WindowBrush"];
        palette["DialogBackdropBrush"] = file.Stars ? SpaceBackdrop.Brush : Solid(window);
        // The same colors as plain Colors, for the few places that take one instead of a brush (an effect's glow). They
        // are derived here rather than named in every palette file, so a theme keeps naming colors only once.
        foreach (var key in BrushKeys)
            palette[key.Replace("Brush", "Color", StringComparison.Ordinal)]
                = file.Colors.TryGetValue(key, out var own) ? own : fallback.Colors[key];
        return palette;
    }

    private static IReadOnlyDictionary<string, ThemeFile> Load()
    {
        var assembly = typeof(ThemeCatalog).Assembly;
        var files = new Dictionary<string, ThemeFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".json", StringComparison.Ordinal)) continue;
            var id = resource[ResourcePrefix.Length..^".json".Length];
            using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException($"Theme file {id} could not be read.");
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = json.RootElement;
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("names", out var named))
                foreach (var entry in named.EnumerateObject()) names[entry.Name] = entry.Value.GetString() ?? id;
            var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
            foreach (var entry in root.GetProperty("colors").EnumerateObject()) colors[entry.Name] = Hex(entry.Value.GetString(), id);
            var primary = root.TryGetProperty("primary", out var primaryColors)
                ? primaryColors.EnumerateArray().Select(value => Hex(value.GetString(), id)).ToArray() : [];
            var order = root.TryGetProperty("order", out var ordered) && ordered.TryGetInt32(out var value) ? value : int.MaxValue;
            var stars = root.TryGetProperty("backdrop", out var backdrop) && backdrop.GetString() == "stars";
            var lanes = root.TryGetProperty("laneBackgrounds", out var laneColors)
                ? laneColors.EnumerateArray().Select(value => Hex(value.GetString(), id)).ToArray() : [];
            files[id] = new ThemeFile(new ThemeInfo(id, order, names), colors, primary, stars, lanes);
        }
        if (!files.TryGetValue(Universe, out var universe) || universe.Lanes.Length == 0)
            throw new InvalidDataException("The universe theme file with lane backgrounds is required.");
        return files;
    }

    private static Color Hex(string? text, string theme) =>
        ColorConverter.ConvertFromString(text ?? "") is Color color ? color : throw new InvalidDataException($"Theme {theme} has an invalid color: {text}");

    private static SolidColorBrush Solid(Color color) => Freeze(new SolidColorBrush(color));

    /// <summary>The color with each channel scaled by <paramref name="factor"/> (below 1 darkens it).</summary>
    private static Color Shade(Color color, double factor) => Color.FromArgb(color.A,
        (byte)Math.Round(color.R * factor), (byte)Math.Round(color.G * factor), (byte)Math.Round(color.B * factor));

    private static T Freeze<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
