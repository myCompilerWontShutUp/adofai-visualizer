using static Visualizer.Core.Localization.ConsoleText;

namespace Visualizer.Infrastructure.Storage;

/// <summary>
/// App-owned store for the settings file and the map cache. Map and game folders are never written, and a custom root is
/// rejected when it is the map folder, contains it, sits inside it, or passes through a reparse point.
/// </summary>
public sealed class AppStorage
{
    /// <summary>The store's folder under %LOCALAPPDATA%, shared by every copy of the app on the PC.</summary>
    public const string FolderName = "AdofaiVisualizer";
    /// <summary>Written in the default root only while a custom root is in use; it is how the next launch finds the settings.</summary>
    private const string PointerName = "data-root.txt";

    /// <summary>Store in use: the custom root when one is set and usable, otherwise <see cref="DefaultRoot"/>.</summary>
    public string Root { get; private set; }
    public string DefaultRoot { get; }
    /// <summary>Reason the custom root was ignored, for the status line. Null when nothing was wrong.</summary>
    public string? Warning { get; private set; }
    /// <summary>The repository folder whose store was just moved here, once, for the status line.</summary>
    public string? MovedFrom { get; private set; }
    /// <summary>A repository store that was not moved because the shared one already held files.</summary>
    public string? LeftBehind { get; private set; }

    public string SettingsPath => Path.Combine(Root, "settings.json");

    /// <summary>
    /// Where the game's linker reads the judgment ease from (user request): always the shared home, beside the pointer file,
    /// so the mod finds it without knowing a chosen folder. One number; 1 is the game's own judgment.
    /// </summary>
    public string JudgmentEasePath => Path.Combine(DefaultRoot, "judgment-ease.txt");

    /// <summary>
    /// Tells the linker the judgment ease to apply. Written whole and then swapped in, so the mod never reads half a
    /// number; anything the mod cannot read as an ease between 1 and 3 counts as 1.
    /// </summary>
    public void WriteJudgmentEase(double scale)
    {
        if (!double.IsFinite(scale) || scale < 1) throw new ArgumentOutOfRangeException(nameof(scale));
        Directory.CreateDirectory(DefaultRoot);
        var temporary = JudgmentEasePath + ".tmp";
        File.WriteAllText(temporary, scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        File.Move(temporary, JudgmentEasePath, overwrite: true);
    }
    public string CacheDirectory => Path.Combine(Root, "map-cache");
    public bool UsesDefaultRoot => Same(Root, DefaultRoot);

    private AppStorage(string defaultRoot)
    {
        DefaultRoot = defaultRoot;
        Root = defaultRoot;
    }

    /// <summary>
    /// The store every copy of the app on this PC shares, <c>%LOCALAPPDATA%\AdofaiVisualizer</c> (user request: a copy
    /// fetched for playing and the one being worked on see the same songs, settings and notes). A copy run from a
    /// repository first moves what it kept in its <c>.local/app-data</c>, once. A pointer file redirects to a custom root.
    /// </summary>
    public static AppStorage Open(string baseDirectory)
    {
        var shared = SharedRoot();
        if (RepositoryStoreFor(baseDirectory) is not { } old) return At(shared);
        StoreMoveResult move;
        try { move = StoreMove.Run(old, shared); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            // Nothing is lost: this run keeps to the old folder and says why; the next one tries again.
            var kept = At(old);
            kept.Warning = error.Message;
            return kept;
        }
        var storage = At(shared);
        storage.MovedFrom = move.Moved ? old : null;
        storage.LeftBehind = move.LeftBehind;
        return storage;
    }

    /// <summary>
    /// The store for a given default root, pointer file included. The app uses <see cref="Open"/>; tests use this to keep
    /// every write inside their own folder.
    /// </summary>
    public static AppStorage At(string defaultRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultRoot);
        var storage = new AppStorage(Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultRoot)));
        var pointer = Path.Combine(storage.DefaultRoot, PointerName);
        string custom;
        try
        {
            if (!File.Exists(pointer)) return storage;
            custom = File.ReadAllText(pointer).Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            storage.Warning = error.Message;
            return storage;
        }
        if (custom.Length == 0) return storage;
        // A root that has become unusable must not stop the app: fall back to the default and say why.
        try { storage.Root = Normalize(custom); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            { storage.Warning = error.Message; }
        return storage;
    }

    /// <summary>The shared store under %LOCALAPPDATA%.</summary>
    public static string SharedRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) throw new IOException(T("NoLocalAppData"));
        return Path.Combine(local, FolderName);
    }

    /// <summary>Where a copy run from a repository kept its store before every copy shared one, or null outside one.</summary>
    public static string? RepositoryStoreFor(string baseDirectory)
    {
        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) && File.Exists(Path.Combine(dir.FullName, "Visualizer.sln")))
                return Path.Combine(dir.FullName, ".local", "app-data");
        return null;
    }

    /// <summary>Checks a root the user typed. Throws with the reason when it cannot be used.</summary>
    public static string Validate(string path, string? mapRootDirectory)
    {
        var root = Normalize(path);
        if (mapRootDirectory is { Length: > 0 } && Path.IsPathFullyQualified(mapRootDirectory))
        {
            var maps = Path.GetFullPath(mapRootDirectory);
            if (Within(root, maps) || Within(maps, root))
                throw new IOException("맵 폴더와 프로그램 저장소는 같거나 서로의 하위 폴더일 수 없습니다.");
        }
        return root;
    }

    /// <summary>
    /// Points the store at <paramref name="path"/> for this run and the next launch; an empty path restores the default and
    /// removes the pointer. Existing files are never moved or deleted, so a new root starts empty.
    /// </summary>
    public void UseRoot(string? path, string? mapRootDirectory)
    {
        var pointer = Path.Combine(DefaultRoot, PointerName);
        var wanted = string.IsNullOrWhiteSpace(path) ? DefaultRoot : Validate(path, mapRootDirectory);
        if (Same(wanted, DefaultRoot))
        {
            if (File.Exists(pointer)) File.Delete(pointer);
            Root = DefaultRoot;
        }
        else
        {
            Directory.CreateDirectory(wanted);
            Directory.CreateDirectory(DefaultRoot);
            File.WriteAllText(pointer, wanted);
            Root = wanted;
        }
        Warning = null;
    }

    /// <summary>Settings text written by an earlier run, or null when the app has never saved any.</summary>
    public string? ReadSettings()
    {
        var path = SettingsPath;
        if (!File.Exists(path)) return null;
        RejectReparseAncestors(path);
        return File.ReadAllText(path);
    }

    /// <summary>Writes the settings through a temp file so a failed write cannot truncate the previous one.</summary>
    public void WriteSettings(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        Directory.CreateDirectory(Root);
        RejectReparseAncestors(SettingsPath);
        var temp = Path.Combine(Root, $"settings-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, json);
        File.Move(temp, SettingsPath, overwrite: true);
    }

    private string SeenVersionPath => Path.Combine(Root, "last-version.txt");

    /// <summary>Whether an app version has run with this store before: its settings or its song cache are there.</summary>
    public bool RanBefore => File.Exists(SettingsPath) || File.Exists(Path.Combine(CacheDirectory, "library.json"));

    /// <summary>The version whose update notes were shown last, or null before any were.</summary>
    public string? ReadSeenVersion()
    {
        if (!File.Exists(SeenVersionPath)) return null;
        RejectReparseAncestors(SeenVersionPath);
        return File.ReadAllText(SeenVersionPath).Trim();
    }

    public void WriteSeenVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Directory.CreateDirectory(Root);
        RejectReparseAncestors(SeenVersionPath);
        File.WriteAllText(SeenVersionPath, version);
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException(T("FullPathRequired"), nameof(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        RejectReparseAncestors(root);
        return root;
    }

    private static bool Same(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

    private static bool Within(string path, string parent)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparseAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(T("StoreReparse"));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
