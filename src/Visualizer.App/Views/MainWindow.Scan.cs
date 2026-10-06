using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Visualizer.App.Commands;
using Visualizer.App.Themes;
using Visualizer.Application.Library;
using Visualizer.Application.Maps;
using Visualizer.Core.Localization;
using Visualizer.Core.Settings;
using Visualizer.Infrastructure.Cache;
using Visualizer.Infrastructure.Maps;
using Visualizer.Infrastructure.Parsing;
using Visualizer.Infrastructure.Storage;
using Visualizer.Infrastructure.Sync;

namespace Visualizer.App.Views;

/// <summary>
/// scannow and install -reinstall (user request). A scan only reads and reports what is wrong; putting a part back is
/// install -reinstall, which hands the repository's parts to update.ps1 like update does. Every reason is English, as
/// the console is.
/// </summary>
public partial class MainWindow
{
    void ICmdHost.ScanNow(IReadOnlyList<string> areas, IProgress<double> progress, Action<IReadOnlyList<ScanArea>> done) =>
        _ = ScanAsync(areas, progress, done);

    private async Task ScanAsync(IReadOnlyList<string> areas, IProgress<double> progress, Action<IReadOnlyList<ScanArea>> done)
    {
        var results = new List<ScanArea>();
        var share = 1d / Math.Max(1, areas.Count);
        for (var i = 0; i < areas.Count; i++)
        {
            var from = i * share;
            // Made here, on the UI thread, so a part that works elsewhere still reports back onto it.
            var part = new Progress<double>(fraction => progress.Report(from + Math.Clamp(fraction, 0, 1) * share));
            ScanArea area;
            try
            {
                area = areas[i] switch
                {
                    "app" => ScanApp(),
                    "source" => await ScanSourceAsync(part),
                    "linker" => ScanLinker(),
                    "maps" => await ScanMapsAsync(part),
                    "edits" => await Task.Run(ScanEdits),
                    "data" => ScanData(),
                    var other => new ScanArea(other, [$"'{other}' is not an area."])
                };
            }
            catch (Exception error) when (IsExpectedError(error)) { area = new ScanArea(areas[i], ["It could not be checked: " + error.Message]); }
            results.Add(area);
            progress.Report((i + 1) * share);
        }
        done(results);
    }

    /// <summary>The build against its source, and the language and theme files it carries.</summary>
    private ScanArea ScanApp()
    {
        var problems = new List<string>();
        if (Repository is { } repository)
        {
            var source = Regex.Match(File.ReadAllText(Path.Combine(repository, "Directory.Build.props")), "<Version>([^<]+)</Version>").Groups[1].Value;
            if (source != AppInfo.Version) problems.Add($"this build is {AppInfo.Version}, but its source is {source}");
            // A source file newer than the build means the app running is not what the files say.
            var built = File.GetLastWriteTimeUtc(typeof(MainWindow).Assembly.Location);
            var root = Path.Combine(repository, "src");
            var newer = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Count(file => File.GetLastWriteTimeUtc(file) > built);
            if (newer > 0) problems.Add($"{newer} source file(s) changed after this build was made");
        }
        var reference = Languages.Keys(Languages.Default);
        foreach (var language in Languages.All)
        {
            var keys = Languages.Keys(language.Id);
            var missing = reference.Except(keys).Count();
            var unknown = keys.Except(reference).Count();
            if (missing + unknown > 0) problems.Add($"language {language.Id}: {missing} word(s) missing, {unknown} unknown");
        }
        foreach (var theme in ThemeCatalog.All)
        {
            var missing = ThemeCatalog.BrushKeys.Except(ThemeCatalog.DeclaredKeys(theme.Id)).ToArray();
            if (missing.Length > 0) problems.Add($"theme {theme.Id}: {missing.Length} color(s) missing, as {missing[0]}");
        }
        if (new VisualizerSettings().Validate() is { Count: > 0 } wrong) problems.Add("the initial settings are not valid: " + wrong[0]);
        return new("app", problems, $"{AppInfo.Version}, {Languages.All.Count} languages, {ThemeCatalog.All.Count} themes");
    }

    /// <summary>The repository's files against the commit this copy is on, as update.ps1 -Scan reads them.</summary>
    private async Task<ScanArea> ScanSourceAsync(IProgress<double> progress)
    {
        if (UpdateScript("-Scan", "-Progress") is not { } start)
            return new("source", [], "not a copy of the repository, so there is nothing to compare");
        var finished = new TaskCompletionSource<IReadOnlyList<string>>();
        _ = RunScriptAsync(start, progress, finished.SetResult);
        var lines = await finished.Task;
        var problems = new List<string>();
        string? version = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("state mine ", StringComparison.Ordinal)) version = line["state mine ".Length..].Trim();
            else if (line.StartsWith("scan ", StringComparison.Ordinal) && line.Split(' ', 3) is [_, var kind, var path])
                problems.Add($"{kind,-8}{path}");
            else problems.Add(line);
        }
        return new("source", problems, version is { Length: > 0 } ? $"against {version}" : null);
    }

    /// <summary>The installed linker against this version's build. Not there at all is not a problem, only a fact.</summary>
    private ScanArea ScanLinker()
    {
        var place = ((ICmdHost)this).FindLinker();
        if (place.Installed is null || place.GameDirectory is null) return new("linker", [], NotInstalled: true);
        var problems = new List<string>();
        if (place.Installed != AppInfo.Version) problems.Add($"the linker is {(place.Installed.Length == 0 ? "of an unknown version" : place.Installed)}, but this app is {AppInfo.Version}");
        foreach (var name in LinkerInstaller.FileNames)
            if (!File.Exists(Path.Combine(place.Target, name))) problems.Add($"{name} is missing from {place.Target}");
        if (place.Source is null) problems.Add("this version's linker is not beside the app, so the installed one cannot be compared");
        else
        {
            var dll = LinkerInstaller.FileNames[0];
            var installed = Path.Combine(place.Target, dll);
            if (File.Exists(installed) && !File.ReadAllBytes(installed).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(place.Source, dll))))
                problems.Add($"the installed {dll} is not this version's build");
        }
        return new("linker", problems, $"{place.Installed} in {place.Target}");
    }

    /// <summary>The map copies against the song list: every copy listed, every listed song still there and as it was synced.</summary>
    private async Task<ScanArea> ScanMapsAsync(IProgress<double> progress)
    {
        _cache ??= new WorkspaceMapCache(Storage);
        var entries = await _cache.ReadAsync();
        var maps = Maps;
        var mapRoot = _model.Settings.MapRootDirectory;
        return await Task.Run(async () =>
        {
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(mapRoot) || !Directory.Exists(mapRoot)) problems.Add($"the map folder {mapRoot} is not there, so sync has nothing to read");
            var roots = new[] { ("base", maps.BaseRoot), ("custom", maps.CustomRoot), ("edited", maps.EditedRoot) };
            var levels = new List<(string Kind, string Root, string Path)>();
            foreach (var (kind, root) in roots)
            {
                if (!Directory.Exists(root)) continue;
                var discovery = await new ReadOnlyDirectoryMapSource(root).DiscoverAsync();
                levels.AddRange(MapFileRules.SelectLevels(discovery.RelativePaths).Select(path => (kind, root, path)));
            }
            var listed = entries.ToDictionary(entry => (Path.TrimEndingDirectorySeparator(entry.RootDirectory).ToUpperInvariant(),
                entry.RelativePath.ToUpperInvariant()));
            var seen = new HashSet<(string, string)>();
            var older = 0;
            for (var i = 0; i < levels.Count; i++)
            {
                var (kind, root, path) = levels[i];
                var key = (Path.TrimEndingDirectorySeparator(root).ToUpperInvariant(), path.ToUpperInvariant());
                seen.Add(key);
                if (!listed.TryGetValue(key, out var entry)) problems.Add($"{kind}/{path} is not in the song list");
                else
                {
                    var text = (await new ReadOnlyDirectoryMapSource(root).ReadAsync(path)).Text;
                    if (MapSyncService.HashOf(text) != entry.SourceHash) problems.Add($"{entry.SongName} changed after it was synced");
                    if (entry.ParserVersion != AdofaiTimelineParser.CurrentVersion) older++;
                }
                progress.Report((i + 1d) / Math.Max(1, levels.Count));
            }
            if (older > 0) problems.Add($"{older} song(s) were read by an older version of the reader");
            var ours = roots.Select(root => Path.TrimEndingDirectorySeparator(root.Item2).ToUpperInvariant()).ToHashSet();
            foreach (var (key, entry) in listed)
                if (ours.Contains(key.Item1) && !seen.Contains(key)) problems.Add($"{entry.SongName} is in the song list, but its file is gone");
            var unread = entries.Count(entry => entry.Status != MapParseStatus.Supported);
            return new ScanArea("maps", problems, $"{levels.Count} maps, {entries.Count} in the song list"
                + (unread > 0 ? $", {unread} the app cannot read" : ""));
        });
    }

    /// <summary>Every edited song: its copy of the original, its lanes, and that the copy is the one the lanes were made on.</summary>
    private ScanArea ScanEdits()
    {
        var problems = new List<string>();
        var root = Maps.EditedRoot;
        var count = 0;
        if (Directory.Exists(root))
        {
            foreach (var song in Directory.EnumerateDirectories(root))
                foreach (var slot in Directory.EnumerateDirectories(song))
                {
                    count++;
                    var name = $"{Path.GetFileName(song)}/{Path.GetFileName(slot)}";
                    if (!int.TryParse(Path.GetFileName(slot), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 9)
                        problems.Add($"{name} is not a slot (1 to 9)");
                    var copies = Directory.EnumerateFiles(slot, "*.adofai").Where(file => !MapFileRules.IsEditorBackupName(Path.GetFileName(file))).ToArray();
                    var edit = Path.Combine(slot, "edit.json");
                    if (copies.Length != 1) problems.Add($"{name} should hold one copy of its original, and holds {copies.Length}");
                    if (!File.Exists(edit)) { problems.Add($"{name} has no edit.json"); continue; }
                    try
                    {
                        using var json = JsonDocument.Parse(File.ReadAllText(edit));
                        var data = json.RootElement;
                        if (!data.TryGetProperty("SchemaVersion", out var schema) || schema.GetInt32() != NoteEdit.SchemaVersion)
                            problems.Add($"{name}: edit.json is of another form");
                        if (data.TryGetProperty("Lanes", out var lanes) && lanes.EnumerateObject().Any(lane => lane.Value.GetInt32() is < 1 or > 8))
                            problems.Add($"{name}: a lane is outside 1 to 8");
                        if (copies.Length == 1 && data.TryGetProperty("SourceHash", out var hash) && hash.GetString() is { Length: > 0 } made
                            && MapSyncService.HashOf(File.ReadAllText(copies[0])) != made)
                            problems.Add($"{name}: its copy is not the map the lanes were made on");
                    }
                    catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
                    {
                        problems.Add($"{name}: edit.json cannot be read ({error.Message})");
                    }
                }
        }
        return new("edits", problems, count == 1 ? "1 edit" : $"{count} edits");
    }

    /// <summary>The settings and the files that keep the song list, the ids, the marks and the notes.</summary>
    private ScanArea ScanData()
    {
        var problems = new List<string>();
        if (Storage.Warning is { } warning) problems.Add("the data folder chosen in the settings cannot be used: " + warning);
        void Check(string what, Action read)
        {
            try { read(); }
            catch (Exception error) when (IsExpectedError(error) || error is SettingsJsonException) { problems.Add($"{what} cannot be read: {error.Message}"); }
        }
        Check("settings.json", () => { if (Storage.ReadSettings() is { } json) SettingsJson.Import(json, new VisualizerSettings()); });
        Check("songs.json", () => SongStore.Read());
        Check("song-marks.json", () => MarkBook.Read());
        Check("song-ids.json", () =>
        {
            var ids = IdBook.Read();
            foreach (var shared in ids.GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
                problems.Add($"song-ids.json gives {shared.Count()} songs the same id {shared.Key}");
        });
        Check("the notes", () => new ReportBook(Storage).Notes());
        Check("pins.json", () => new PinBook(Storage).Read());
        return new("data", problems, Storage.Root);
    }

    /// <summary>
    /// install -reinstall linker: this version's linker, which ships beside the app, is written into the game again. Nothing
    /// is built and the app stays open.
    /// </summary>
    void ICmdHost.ReinstallLinker(IProgress<double> progress, Action<IReadOnlyList<string>> done)
    {
        var place = ((ICmdHost)this).FindLinker();
        if (place.GameDirectory is null) { done(["The game folder was not found."]); return; }
        if (place.Source is null) { done(["This version's linker is not beside the app, so there is nothing to reinstall from."]); return; }
        done([((ICmdHost)this).InstallLinker(place, progress)
            ?? $"The linker{(place.SourceVersion is { Length: > 0 } version ? " " + version : "")} is written into {place.Target} again. Restart the game to load it."]);
    }

    /// <summary>
    /// install -reinstall source and app: the app closes, since a running app holds its own files, and update.ps1 puts the
    /// parts back, builds them and opens it again. Nothing is read back, because this app will not be here to read it.
    /// </summary>
    string? ICmdHost.ReinstallAndRestart(IReadOnlyList<string> parts)
    {
        var arguments = new List<string> { "-Reinstall", string.Join(",", parts), "-WaitForProcessId",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "-Restart" };
        if (parts.Contains("linker") && ((ICmdHost)this).FindLinker() is { GameDirectory: { } game }) arguments.AddRange(["-LinkerGame", game]);
        if (UpdateScript([.. arguments]) is not { } start)
            return "This build is not running from the repository, so there is nothing to reinstall from.";
        start.RedirectStandardOutput = false;
        start.RedirectStandardError = false;
        try { Process.Start(start); }
        catch (Exception error) when (IsExpectedError(error) || error is System.ComponentModel.Win32Exception)
        {
            return "The reinstall could not start. " + error.Message;
        }
        Dispatcher.BeginInvoke(new Action(() => System.Windows.Application.Current.Shutdown()),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        return null;
    }
}
