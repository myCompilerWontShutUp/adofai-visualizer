using System.Text.Json;

namespace Visualizer.Infrastructure.Sync;

/// <summary>
/// Puts the read-only game link mod into the game's own Mods folder. That one folder is the only place outside this
/// project the app ever writes (AGENTS.md rule 2), so nothing here runs unless the user asks for it by name, and only the
/// mod's own two files are written: the rest of the game folder, other mods included, is left alone.
/// </summary>
public static class LinkerInstaller
{
    /// <summary>The mod's folder under the game's Mods folder, the same one build-mod.ps1 -Install writes.</summary>
    public const string FolderName = "AdofaiVisualizerSync";
    /// <summary>Everything the mod is: the assembly and the manifest Unity Mod Manager reads.</summary>
    public static IReadOnlyList<string> FileNames { get; } = ["AdofaiVisualizerSync.dll", "Info.json"];
    /// <summary>
    /// Where the built mod lives, in the repository and beside the app alike: build-mod.ps1 writes it there and it is
    /// committed with the version, so a copy that cannot build the mod (no game assemblies, no .NET Framework tools) still
    /// installs it.
    /// </summary>
    public static string PluginFolder { get; } = Path.Combine("plugins", "linker");
    private const string GameFolderName = "A Dance of Fire and Ice";

    public static string ManagedOf(string gameDirectory) => Path.Combine(gameDirectory, GameFolderName + "_Data", "Managed");

    /// <summary>
    /// Whether Unity Mod Manager is in that game folder. Without it nothing would ever load the mod, so nothing is
    /// installed; the same check build-mod.ps1 makes before it copies.
    /// </summary>
    public static bool HasModManager(string gameDirectory) =>
        File.Exists(Path.Combine(ManagedOf(gameDirectory), "UnityModManager", "UnityModManager.dll"));

    public static string TargetOf(string gameDirectory) => Path.Combine(gameDirectory, "Mods", FolderName);

    /// <summary>
    /// Where the game is, going by the map folder: the Steam library that holds the workshop maps holds the game too. The
    /// usual install path is tried last. Only a folder with Unity Mod Manager in it counts, since the mod could not be
    /// loaded without it; null is left to the caller to report.
    /// </summary>
    public static string? FindGame(string? mapRoot)
    {
        foreach (var candidate in GameCandidates(mapRoot))
            if (HasModManager(candidate)) return candidate;
        return null;
    }

    private static IEnumerable<string> GameCandidates(string? mapRoot)
    {
        // ...\steamapps\workshop\content\977950 and ...\steamapps\common\A Dance of Fire and Ice share the library folder.
        if (!string.IsNullOrWhiteSpace(mapRoot) && Path.IsPathFullyQualified(mapRoot))
            for (var folder = Path.TrimEndingDirectorySeparator(mapRoot); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
                if (string.Equals(Path.GetFileName(folder), "steamapps", StringComparison.OrdinalIgnoreCase))
                    yield return Path.Combine(folder, "common", GameFolderName);
        yield return Path.Combine(@"C:\Program Files (x86)\Steam\steamapps\common", GameFolderName);
    }

    /// <summary>
    /// The folder a built mod sits in: the plugins folder beside the app first (the build puts it there), then the
    /// repository's own, then whatever build-mod.ps1 last built. Null when none has both files.
    /// </summary>
    public static string? FindSource(string appDirectory, string? repositoryRoot)
    {
        foreach (var folder in SourceCandidates(appDirectory, repositoryRoot))
            if (FileNames.All(name => File.Exists(Path.Combine(folder, name)))) return folder;
        return null;
    }

    private static IEnumerable<string> SourceCandidates(string appDirectory, string? repositoryRoot)
    {
        yield return Path.Combine(appDirectory, PluginFolder);
        yield return Path.Combine(appDirectory, "linker");
        yield return appDirectory;
        if (repositoryRoot is null) yield break;
        yield return Path.Combine(repositoryRoot, PluginFolder);
        // The mod is not in the solution, so its output is wherever build-mod.ps1 last put it.
        foreach (var configuration in new[] { "Release", "Debug" })
            yield return Path.Combine(repositoryRoot, "tools", "GameSyncMod", "bin", configuration);
    }

    /// <summary>The version in a mod folder's Info.json, or null when there is none to read. Used to tell an old copy apart.</summary>
    public static string? VersionAt(string folder)
    {
        var manifest = Path.Combine(folder, "Info.json");
        if (!File.Exists(manifest)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            return document.RootElement.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.String
                ? version.GetString() : null;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The installed mod's version, or null when the mod is not there at all.</summary>
    public static string? InstalledVersion(string gameDirectory) =>
        File.Exists(Path.Combine(TargetOf(gameDirectory), FileNames[0])) ? VersionAt(TargetOf(gameDirectory)) ?? "" : null;

    /// <summary>
    /// Writes the mod's two files into the game's Mods folder, one file at a time rather than copying a folder, so nothing
    /// else there is touched. A link anywhere on the way is refused: following one would write outside the approved folder.
    /// Returns the folder written.
    /// </summary>
    public static string Install(string gameDirectory, string sourceDirectory, IProgress<double>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        if (!HasModManager(gameDirectory))
            throw new InvalidOperationException("Unity Mod Manager is not in that game folder, so nothing was written.");
        var target = TargetOf(gameDirectory);
        foreach (var folder in new[] { Path.GetDirectoryName(target)!, target })
            if (Directory.Exists(folder) && new DirectoryInfo(folder).LinkTarget is not null)
                throw new IOException($"'{folder}' is a link, so nothing was written.");
        Directory.CreateDirectory(target);
        var written = 0;
        foreach (var name in FileNames)
        {
            var from = Path.Combine(sourceDirectory, name);
            if (!File.Exists(from)) throw new FileNotFoundException("The mod is missing one of its files.", from);
            var to = Path.Combine(target, name);
            if (File.Exists(to) && new FileInfo(to).LinkTarget is not null)
                throw new IOException($"'{to}' is a link, so nothing was written.");
            File.WriteAllBytes(to, File.ReadAllBytes(from));
            progress?.Report((double)++written / FileNames.Count);
        }
        return target;
    }
}
