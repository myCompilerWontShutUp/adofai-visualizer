using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Visualizer.App;
using Visualizer.App.Commands;
using Visualizer.Application.Input;
using Visualizer.App.Localization;
using Visualizer.App.ViewModels;
using Visualizer.App.Views;
using Visualizer.Application.Cache;
using Visualizer.Application.Library;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Core.Localization;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

/// <summary>
/// The console: option rules, help, songs, song files, settings by name, rollback, the settings
/// window closing first, and the terminal window with Tab completion.
/// </summary>
internal static class CmdConsoleTests
{
    /// <summary>A stand-in for the app: records what the console asked it to do.</summary>
    private sealed class Host : ICmdHost
    {
        public ParsedTimeline? Timeline { get; set; }
        public ParsedTimeline? Loadable { get; set; }
        public string? SongToLoad { get; set; }
        public string? LoadedSongName { get; set; }
        public VisualizerSettings Settings { get; set; } = new();
        /// <summary>The language the console speaks; English here whatever the settings say, since these checks read its words.</summary>
        public string Language { get; set; } = Languages.English;
        public VisualizerSettings? Pending { get; set; }
        public string? ApplyError { get; set; }
        public string Copied { get; private set; } = "";
        public int Loads { get; private set; }
        public int Applies { get; private set; }
        public bool SettingsOpen { get; set; }
        public bool EditorOpen { get; set; }
        /// <summary>What the open editor holds, as the window hands it to note.</summary>
        public CmdNoteSource? Editing { get; set; }
        /// <summary>The saved edit each song plays with, by id.</summary>
        public Dictionary<string, NoteEdit> Edits { get; } = [];
        public CmdNoteSource? NowNotes => Editing ?? (Timeline is { } timeline ? new(timeline, null) : null);
        public NoteEdit? EditOf(CmdSong song) => Edits.TryGetValue(song.Id, out var edit) ? edit : null;
        public bool HasUnappliedSettings => Pending is not null;
        public List<string> Calls { get; } = [];
        public List<CmdSong> SongList { get; } = [];
        public IReadOnlyList<CmdSong> Songs => SongList;
        public string? LoadedId { get; set; }
        /// <summary>The id of the song the game is playing, as the window finds it; null while the link is off.</summary>
        public string? GameSong { get; set; }
        public string? GameSongId(out string? problem)
        {
            problem = GameSong is null ? "The link is not on. link start first." : null;
            return GameSong;
        }
        public string? Held { get; set; }
        public string? HeldSong => Held;
        public string? EditError { get; set; }
        public string? Folder { get; set; }
        public List<SettingsChange> History { get; } = [];
        public SettingsChange? LastChange => History.Count > 0 ? History[^1] : null;

        /// <summary>Where the song is prepared from, as the app counts it: 1 is the beginning.</summary>
        public int Checkpoint { get; set; } = 1;
        /// <summary>What checkpoint and go asked for, and the reason to hand back.</summary>
        public List<int> Moves { get; } = [];
        public List<int> Tiles { get; } = [];
        public string? MoveError { get; set; }
        public string? TileError { get; set; }

        public string? MoveCheckpoint(int checkpoint)
        {
            Moves.Add(checkpoint);
            if (MoveError is { } reason) return reason;
            Checkpoint = checkpoint;
            return null;
        }

        public string? StartAtTile(int tile)
        {
            Tiles.Add(tile);
            return TileError;
        }

        /// <summary>Pins by song family, kept in memory as the app keeps them in pins.json.</summary>
        public Dictionary<string, IReadOnlyList<int>> PinBook { get; } = [];
        public IReadOnlyList<int> PinsOf(string family) => PinBook.TryGetValue(family, out var tiles) ? tiles : [];
        public string? SetPins(string family, IReadOnlyList<int> tiles)
        {
            PinBook[family] = tiles.ToArray();
            return null;
        }

        /// <summary>The main window's list for list main and playlist.</summary>
        public List<string> Current { get; } = [];
        public IReadOnlyList<string> CurrentIds => Current;
        /// <summary>What load asked for: the id and the speed choice.</summary>
        public List<(string Id, bool? KeepSpeed)> Loaded { get; } = [];
        public string? LoadError { get; set; }

        public string? LoadSongById(string id, bool? keepSpeed)
        {
            Loaded.Add((id, keepSpeed));
            if (LoadError is { } error) return error;
            LoadedId = id;
            LoadedSongName = SongList.Single(song => song.Id == id).Title;
            // As the app does: the practice speed goes back to 100% unless it is kept.
            if (!(keepSpeed ?? Settings.KeepSpeedOnLoad) && Settings.PlaybackSpeedPercent != 100)
                Settings = Settings with { PlaybackSpeedPercent = 100 };
            return null;
        }

        /// <summary>What load -force asked for; <see cref="ForceError"/> makes it fail as a song no format reads.</summary>
        public List<string> Forced { get; } = [];
        public string? ForceError { get; set; }

        public string? ForceLoadSongById(string id, bool? keepSpeed)
        {
            Forced.Add(id);
            if (ForceError is { } error) return error;
            var index = SongList.FindIndex(song => song.Id == id);
            SongList[index] = SongList[index] with { Entry = SongList[index].Entry with { Status = MapParseStatus.Supported, ForcedFormatVersion = 2 } };
            return LoadSongById(id, keepSpeed);
        }

        /// <summary>What fps asked for; the app measures the frames itself.</summary>
        public List<bool> FrameAsks { get; } = [];

        public IReadOnlyList<string> FrameStats(bool restart)
        {
            FrameAsks.Add(restart);
            return ["Frames 240 over 2.0 s · 120.0 fps at the median", "Render tier 2 (hardware)"];
        }

        /// <summary>What sync asked for, as (start, stop, restart); the app itself owns the pipe.</summary>
        public List<(bool Start, bool Stop, bool Restart)> SyncAsks { get; } = [];

        public IReadOnlyList<string> GameSync(bool start, bool stop, bool restart)
        {
            SyncAsks.Add((start, stop, restart));
            return ["Not listening. Use link start to open the pipe."];
        }

        /// <summary>How many times link tiles asked for the tile comparison.</summary>
        public int TileAsks { get; private set; }

        public IReadOnlyList<string> GameSyncTiles()
        {
            TileAsks++;
            return ["Tiles    412 compared"];
        }

        /// <summary>What link follow and unfollow asked for.</summary>
        public List<bool> FollowAsks { get; } = [];

        public IReadOnlyList<string> GameSyncFollow(bool on)
        {
            FollowAsks.Add(on);
            return [on ? "Following the game." : "Not following the game any more."];
        }

        /// <summary>What link auto and link manual asked for.</summary>
        public List<bool> AutoAsks { get; } = [];

        public IReadOnlyList<string> GameSyncAuto(bool on)
        {
            AutoAsks.Add(on);
            return [on ? "Matching by itself from now on." : "The tiles are no longer matched by themselves."];
        }

        /// <summary>What install linker would find; the test sets it, as there is no game folder here.</summary>
        public LinkerPlace Place { get; set; } = new(null, null, null, null, "");
        /// <summary>The places install linker was told to write into, so a test can prove nothing was written on a no.</summary>
        public List<string> Installed { get; } = [];
        /// <summary>Why installing failed, when a test wants the refusal path.</summary>
        public string? InstallProblem { get; set; }

        public LinkerPlace FindLinker() => Place;

        public string? InstallLinker(LinkerPlace place, IProgress<double> progress)
        {
            progress.Report(1);
            if (InstallProblem is { } problem) return problem;
            Installed.Add(place.Target);
            return null;
        }

        public bool LoadSong(out string message)
        {
            Loads++;
            Timeline = Loadable;
            message = Loadable is null ? "The map is Unsupported." : "";
            return Timeline is not null;
        }

        public string? ApplyPendingSettings()
        {
            Applies++;
            if (ApplyError is { } error) return error;
            Settings = Pending!;
            Pending = null;
            return null;
        }

        public string? CloseSettings(bool apply)
        {
            if (apply && ApplyPendingSettings() is { } error) return error;
            Calls.Add(apply ? "close and apply" : "close");
            Pending = null;
            SettingsOpen = false;
            return null;
        }

        public void CopyText(string text) => Copied = text;

        public string? SetHidden(string id, bool hidden)
        {
            var index = SongList.FindIndex(song => song.Id == id);
            SongList[index] = SongList[index] with { Hidden = hidden };
            Calls.Add((hidden ? "hide " : "show ") + id);
            return null;
        }

        public bool OpenPlaylist()
        {
            Calls.Add("playlist");
            return true;
        }

        /// <summary>
        /// A long job finishes at once unless a test wants to watch it run: with <see cref="HoldJobs"/> the callbacks are
        /// kept here instead, so the test can report progress and finish it by hand.
        /// </summary>
        public bool HoldJobs { get; set; }
        public IProgress<double>? JobProgress { get; private set; }
        public Action<int>? JobDone { get; private set; }

        public void Resync(IProgress<double> progress, Action<int> done) => Job("resync", progress, done);

        public void SyncMaps(IProgress<double> progress, Action<int> done) => Job("sync", progress, done);

        /// <summary>What scannow finds in each area (whole unless set), and every part put back.</summary>
        public Dictionary<string, ScanArea> Scans { get; } = [];
        public List<string> Reinstalled { get; } = [];

        public void ScanNow(IReadOnlyList<string> areas, IProgress<double> progress, Action<IReadOnlyList<ScanArea>> done)
        {
            Calls.Add("scan " + string.Join(",", areas));
            done(areas.Select(area => Scans.TryGetValue(area, out var found) ? found : new ScanArea(area, [])).ToArray());
        }

        public void ReinstallLinker(IProgress<double> progress, Action<IReadOnlyList<string>> done)
        {
            Reinstalled.Add("linker");
            done(["The linker is built from this version and written into the game again."]);
        }

        /// <summary>The log the window keeps, as its lines.</summary>
        public List<string> Logged { get; } = [];
        public IReadOnlyList<string> LogLines() => Logged;
        public void ClearLog() => Logged.Clear();

        /// <summary>Why the app running is older than the copy's files, as the window would say; null when it is not.</summary>
        public string? Stale { get; set; }
        public string? StaleBuild() => Stale;

        public string? ReinstallAndRestart(IReadOnlyList<string> parts)
        {
            Reinstalled.Add(string.Join(",", parts));
            return null;
        }

        /// <summary>What update was asked to upload, and the lines it answers with.</summary>
        public List<string> Uploads { get; } = [];
        public List<string> UploadLines { get; set; } = ["Uploaded to origin/main."];
        public IProgress<double>? UploadProgress { get; private set; }
        public Action<IReadOnlyList<string>>? UploadDone { get; private set; }

        /// <summary>What update found, which version it asked about, and every update that was taken.</summary>
        public UpdateCheck Check { get; set; } = new("1.0.0", "1.0.0", true, 0, true, 0, 0, []);
        public string? CheckedVersion { get; private set; }
        public List<(bool Force, string? Version)> Updates { get; } = [];
        public int Applied => Updates.Count;
        public string? UpdateError { get; set; }

        public void CheckUpdate(string? version, IProgress<double> progress, Action<UpdateCheck> done)
        {
            Calls.Add("update");
            CheckedVersion = version;
            UploadProgress = progress;
            if (!HoldJobs) done(Check);
            else UpdateDone = done;
        }

        public Action<UpdateCheck>? UpdateDone { get; private set; }

        public string? ApplyUpdate(bool force, string? version)
        {
            Updates.Add((force, version));
            return UpdateError;
        }

        private void Job(string name, IProgress<double> progress, Action<int> done)
        {
            Calls.Add(name);
            JobProgress = progress;
            JobDone = done;
            if (!HoldJobs) done(SongList.Count);
        }

        /// <summary>The notes report writes, kept in memory here instead of the app store.</summary>
        public Dictionary<string, List<string>> Reports { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>What link inspect found, as inspect result asks for it, and how many runs it last asked for.</summary>
        public IReadOnlyList<string>? Inspected { get; set; }
        /// <summary>Whether inspect -flow start begins a run here, and what the window was handed for it.</summary>
        public bool FlowRuns { get; set; }
        public IProgress<double>? FlowProgress { get; private set; }
        public Action? FlowEnded { get; private set; }
        public bool Inspecting { get; private set; }
        public IReadOnlyList<string> GameSyncInspect(bool on, bool flow = false, IProgress<double>? progress = null, Action? ended = null)
        {
            if (!on) return ["link inspect is not available here."];
            if (Inspecting) return ["Already inspecting."];
            Inspecting = FlowRuns;
            if (FlowRuns) { FlowProgress = progress; FlowEnded = () => { Inspecting = false; ended?.Invoke(); }; }
            return [FlowRuns ? "Inspecting." : "Cannot inspect: the link is not on."];
        }
        public int InspectAsked { get; private set; } = -1;
        public IReadOnlyList<string>? InspectLines(int count)
        {
            InspectAsked = count;
            return Inspected;
        }

        public IReadOnlyList<string> ReportTitles() => Reports.Keys.OrderBy(title => title, StringComparer.OrdinalIgnoreCase).ToArray();

        public IReadOnlyList<string>? ReadReport(string title, out string? problem)
        {
            problem = null;
            return Reports.TryGetValue(title, out var lines) ? lines.ToArray() : null;
        }

        public string? WriteReport(string title, IReadOnlyList<string> lines)
        {
            Reports[title] = lines.ToList();
            // Writing opens a note again (user rule): new words are new work.
            ReportDone.Remove(title);
            ReportAsked.Remove(title);
            return null;
        }

        public string? MarkReport(string title, string state, string? why)
        {
            if (!Reports.ContainsKey(title)) return $"{title} was never written.";
            ReportDone.Remove(title);
            ReportAsked.Remove(title);
            if (state == "done") ReportDone[title] = AppInfo.Version;
            else if (state == "asked") ReportAsked[title] = why ?? "";
            return null;
        }

        public string? RenameReport(string title, string name)
        {
            if (!Reports.Remove(title, out var text)) return $"{title} was never written.";
            Reports[name] = text;
            if (ReportDone.Remove(title, out var done)) ReportDone[name] = done;
            if (ReportAsked.Remove(title, out var why)) ReportAsked[name] = why;
            if (ReportVersions.Remove(title, out var version)) ReportVersions[name] = version;
            return null;
        }

        public IReadOnlyList<string> ClearDoneReports()
        {
            var gone = ReportDone.Keys.Where(Reports.ContainsKey).ToArray();
            foreach (var title in gone) { Reports.Remove(title); ReportDone.Remove(title); }
            return gone;
        }

        public bool DeleteReport(string title, out string? problem)
        {
            problem = null;
            return Reports.Remove(title);
        }

        /// <summary>The version each note was written for, and the one it was done in: both kept beside the notes.</summary>
        public Dictionary<string, string> ReportVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ReportDone { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ReportAsked { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<CmdNote> ReportNotes() =>
            [.. ReportTitles().Select(title =>
            {
                var done = ReportDone.TryGetValue(title, out var at) ? at : "";
                var state = done.Length > 0 ? "done" : ReportAsked.ContainsKey(title) ? "asked" : "open";
                return new CmdNote(title,
                    done.Length > 0 ? done : ReportVersions.TryGetValue(title, out var version) ? version : "0.9.1",
                    done, Reports[title].Count, Reports[title], state,
                    ReportAsked.TryGetValue(title, out var why) ? why : "");
            })];

        public string? RenameSong(string id, string name)
        {
            var index = SongList.FindIndex(song => song.Id == id);
            if (index < 0) return $"No song has the id {id}.";
            var slot = SongList[index].Slot is { } n ? n + "*" : "";
            SongList[index] = SongList[index] with { Title = slot + name };
            Calls.Add("retitle " + id);
            return null;
        }

        public string? RemoveEdit(string id)
        {
            var song = SongList.Single(each => each.Id == id);
            SongList.Remove(song);
            Held = song.Title;
            Calls.Add("remove " + id);
            return null;
        }

        public string? Restore(out int slot)
        {
            slot = 2;
            Held = null;
            Calls.Add("restore");
            return null;
        }

        public string? DeleteSong(string id)
        {
            SongList.RemoveAll(song => song.Id == id);
            Calls.Add("delete " + id);
            return null;
        }

        public string? EditSong(string id)
        {
            Calls.Add("edit " + id);
            return EditError;
        }

        public string? ApplySettings(VisualizerSettings settings)
        {
            History.Add(new SettingsChange(Settings, settings));
            Settings = settings;
            Calls.Add("apply");
            return null;
        }

        public string? RollBack()
        {
            var change = History[^1];
            History.RemoveAt(History.Count - 1);
            Settings = SettingsFields.Restore(Settings, change.Before, change.After);
            Calls.Add("rollback");
            return null;
        }

        public string? PickFolder(string start)
        {
            Calls.Add("pick " + start);
            return Folder;
        }
    }

    private static CmdSong Song(string id, string title, string kind, string family, bool hidden = false, int? slot = null) =>
        new(id, title, kind, hidden, slot, family, new MapCacheEntry("cache-" + id, @"C:\store\maps\" + kind, family + @"\main.adofai", title,
            MapParseStatus.Supported, new ParsedTimeline(new[] { 0d, 60_000, 125_500 }), [], "", "test", new MapSongInfo("Artist", title, "Author", 7)));

    /// <summary>Plum - R in custom with two edits, a base song and a hidden custom song.</summary>
    private static Host Library()
    {
        var host = new Host();
        host.SongList.AddRange(
        [
            Song("00000001", "Built in", "base", "1-1/main.adofai"),
            Song("10000002", "Plum - R", "custom", "1234/main.adofai"),
            Song("10000003", "Hidden song", "custom", "5678/main.adofai", hidden: true),
            Song("20000004", "2*Plum - R", "edited", "1234/main.adofai", slot: 2),
            Song("20000005", "5*Plum - R", "edited", "1234/main.adofai", slot: 5)
        ]);
        return host;
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static bool HasHangul(string text) => text.Any(c => c is >= '\uAC00' and <= '\uD7A3');

    /// <summary>Presses a key on a shown element; true when the element kept it from doing anything by itself.</summary>
    private static bool Press(UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
        return args.Handled;
    }

    /// <summary>The non-blank lines printed after the last echoed prompt.</summary>
    private static List<string> LastOutput(CmdLog log)
    {
        var start = -1;
        for (var i = log.Lines.Count - 1; i >= 0; i--)
            if (IsPrompt(log.Lines[i])) { start = i; break; }
        return log.Lines.Skip(start + 1).Where(line => line.Length > 0).ToList();
    }

    /// <summary>A line the user typed: after the console's prompt, or after a note's own prompt in the report editor.</summary>
    private static bool IsPrompt(string line) =>
        line.StartsWith(CmdSession.PromptText, StringComparison.Ordinal)
        || line.StartsWith(CmdSession.FillPrompt, StringComparison.Ordinal);

    private static string Output(CmdLog log) => string.Join(Environment.NewLine, LastOutput(log));

    /// <summary>Types text into the note the way a keyboard does: letters through the editor, line breaks as Enter.</summary>
    private static void Write(ReportEditor editor, string text)
    {
        foreach (var part in text.Replace("\r\n", "\n").Split('\n').Select((line, index) => (line, index)))
        {
            if (part.index > 0) Insert(editor, Environment.NewLine);
            if (part.line.Length > 0) Insert(editor, part.line);
        }
    }

    /// <summary>What the text box does with a typed letter while the note is being written.</summary>
    private static void Insert(ReportEditor editor, string text)
    {
        var box = FindAll<TextBox>(editor).First();
        var at = box.CaretIndex;
        box.Text = box.Text.Insert(at, text);
        box.CaretIndex = at + text.Length;
    }

    /// <summary>Types a : command into the status line and runs it.</summary>
    private static void Command(ReportEditor editor, string typed)
    {
        foreach (var letter in typed) editor.Typed(letter.ToString());
        editor.Press(Key.Enter, ModifierKeys.None);
    }

    /// <summary>Answers the editor's question the way a keyboard does: the letter, then Enter (user rule).</summary>
    private static void Answer(ReportEditor editor, string letter)
    {
        editor.Typed(letter);
        editor.Press(Key.Enter, ModifierKeys.None);
    }

    private static void Lines(IEnumerable<string> actual, params string[] expected)
    {
        var lines = actual.ToList();
        Assert(lines.SequenceEqual(expected), $"Expected [{string.Join(" | ", expected)}], got [{string.Join(" | ", lines)}]");
    }

    private static bool IsNoteLine(string line) => line.Length > 1 && line[0] == 'n' && char.IsDigit(line[1]);

    public static IEnumerable<(string Name, Action Run)> Create(string artifacts)
    {
        yield return ("Console options come right after the command, one per comma group, groups split by / together", () =>
        {
            string? Error(string line)
            {
                _ = CmdCatalog.Parse(line, out var error);
                return error;
            }
            // The user's rule with list's options: -id, -title / -hidden, -shown / -copy (-a -b -c / -d in the request).
            // The message names them the way help does, however they were typed.
            Assert(Error("playlist -id -tl") == "-id and -title cannot be used together.", "-a -b: " + Error("playlist -id -tl"));
            Assert(Error("playlist -id") is null, "-a");
            Assert(Error("playlist -id -c") is null && Error("playlist -c -id") is null, "-a -d in any order");
            Assert(Error("playlist -c") is null, "-d");
            Assert(Error("playlist -c -tl") is null, "-d -c");
            Assert(Error("playlist -id -c -tl") is not null, "-a -d -b");
            Assert(Error("playlist -h -id -c base") is null, "One from each group");
            Assert(Error("playlist -h -s") is not null, "Hidden and shown exclude each other");
            Equal(Error("playlist -c -c"), "-copy is given twice.");
            // An option may stand after the words as well as before them (user rule), read the same either way.
            Assert(Error("playlist base -id") is null && Error("playlist base -id -copy") is null, "Late options");
            var late = CmdCatalog.Parse("playlist base -id", out _)!;
            var early = CmdCatalog.Parse("playlist -id base", out _)!;
            Assert(late.Has("-id") && late.Arguments.SequenceEqual(["base"]) && late.Rest == "base"
                && early.Has("-id") && early.Arguments.SequenceEqual(late.Arguments), "Either side reads the same");
            Equal(Error("playlist base -id -id"), "-id is given twice.");
            Equal(Error("playlist base -id -title"), "-id and -title cannot be used together.");
            Assert(Error("playlist base -force")!.StartsWith("-force is not an option of playlist", StringComparison.Ordinal), "A late option is checked too");
            // -1 is a number, not an option, so the last note keeps working.
            Assert(CmdCatalog.Parse("note -1 -json", out _) is { } lastNote && lastNote.Arguments.SequenceEqual(["-1"]) && lastNote.Has("-json"), "note -1");
            // Options are split by spaces only; help writes each group in brackets.
            Equal(CmdCatalog.Find("playlist")!.OptionsText, "[-id | -title] [-hidden | -shown] [-copy]");
            Assert(Error("playlist -x")!.Contains("[-id | -title] [-hidden | -shown] [-copy]", StringComparison.Ordinal), "Unknown option names the right ones");
            // A value an option does not take says what it does take, and that the name alone has a meaning (user rule).
            Assert(Error("load -speed:fast")!.Contains("It takes keep or reset, or -speed on its own for keep", StringComparison.Ordinal),
                "A wrong value names the right ones: " + Error("load -speed:fast"));
            // The old short spellings and the long ones say the same thing (user rule: two ways to say it is fine).
            var slot = CmdCatalog.Parse("load -sl3 -sm Plum", out _)!;
            Assert(slot.Number == 3 && slot.Has("-sm") && slot.Has("-speed:keep") && slot.Rest == "Plum", "load -sl3 -sm");
            var colon = CmdCatalog.Parse("load -slot:3 -speed:keep Plum", out _)!;
            Assert(colon.Number == 3 && colon.Has("-sm") && colon.Rest == "Plum", "load -slot:3 -speed:keep");
            Equal(CmdCatalog.Parse("load -sl01 Plum", out _)!.Number, 1);
            Equal(CmdCatalog.Parse("load -slot=3 Plum", out _)!.Number, 3);
            // An option that takes a value always has a default, so the name on its own means something (user rule).
            Assert(CmdCatalog.Parse("load -speed Plum", out _)!.Has("-sm"), "-speed alone keeps the speed");
            Assert(CmdCatalog.Parse("load -slot Plum", out _)!.Has("-sl"), "-slot alone is every edit");
            Assert(CmdCatalog.Parse("copy -commands", out _)!.Has("-vs"), "-commands alone brings them with");
            Assert(CmdCatalog.Parse("copy -answers", out _)!.Has("-ep"), "-answers alone leaves them out");
            Assert(CmdCatalog.Parse("copy -count", out _)!.Has("-en"), "-count alone counts the others");
            Assert(Error("load -sl0 Plum")!.Contains("Slots are 1 to 9", StringComparison.Ordinal) && Error("load -sl10 Plum") is not null, "Slots 1 to 9");
            Equal(Error("load -og -sl Plum"), "-origin and -slot cannot be used together.");
            Equal(Error("load -sl2 -og Plum"), "-slot and -origin cannot be used together.");
            Equal(Error("load -sm -sr Plum"), "-speed is given twice, as keep and reset.");
            Equal(Error("load -sl2 -sl3 Plum"), "-slot is given twice, as 2 and 3.");
            Assert(Error("load -id -sr 1abcdefg") is null, "-id with a speed option");
            Assert(Error("load Song -c") is null && CmdCatalog.Parse("load Song -c", out _)!.Rest == "Song -c", "A title is free text");
            Equal(Error("clear -c"), "clear has no options.");
            Equal(Error("hide 10000002 -c"), "hide has no options.");
            Assert(Error("PLAYLIST -ID") is null, "Letter case does not matter");
            Assert(Error("play")!.StartsWith("'play' is not recognized as a command.", StringComparison.Ordinal), "Unknown command");
            Assert(Error("clr") is not null, "clr became clear (app 0.5.1)");

            // Values are free text: a minus key, a negative number and pasted JSON are not options.
            var minus = CmdCatalog.Parse("set keys.next -", out _)!;
            Equal(minus.Rest, "keys.next -");
            var negative = CmdCatalog.Parse("set display.offset -12", out _)!;
            Assert(negative.Options.Count == 0 && negative.Rest == "display.offset -12", "Negative value");
            var json = CmdCatalog.Parse("import -p { \"NoteTravelTimeMs\": 700, \"x\": \"-c\" }", out _)!;
            Assert(json.Has("-p") && json.Rest.StartsWith('{'), "JSON after the options");

            // Every command belongs to a group that cmd lists, and the hidden one to none of them.
            Assert(CmdCatalog.Visible.All(command => CmdCatalog.Groups.Contains(command.Group)), "Groups");
            // whoami and date are ordinary commands now: cmd lists them like the rest (user rule).
            Assert(!CmdCatalog.All.Any(command => command.Hidden), "Nothing is hidden from cmd");
            Assert(CmdCatalog.All.SelectMany(command => command.OptionNames).All(CmdCatalog.OptionHelp.ContainsKey), "Every option is explained");
            // Every keyword is explained too: help looks the meaning up and would throw on a word nobody described.
            foreach (var command in CmdCatalog.Visible)
            foreach (var keyword in command.KeywordNames)
                Assert(CmdCatalog.KeywordMeaning(command, keyword).Length > 0, $"{command.Name} {keyword} has no meaning");
        });

        yield return ("Ctrl+T reaches the console with the game or the visualizer in front", () =>
        {
            // User rule: the game or the visualizer, and anywhere when the global input scope is off. The observed key is the
            // one that acts, so the Korean input method or a window without a handler of its own cannot swallow it.
            var watching = new VisualizerSettings { OnlyReactWhenAdofaiForeground = true };
            var anywhere = watching with { OnlyReactWhenAdofaiForeground = false };
            Assert(InputPolicy.ShouldOpenConsole(watching, appForeground: true, gameForeground: false), "The visualizer may open it");
            Assert(InputPolicy.ShouldOpenConsole(anywhere, appForeground: true, gameForeground: false), "With any scope too");
            Assert(InputPolicy.ShouldOpenConsole(watching, appForeground: false, gameForeground: true), "The game may open it");
            Assert(!InputPolicy.ShouldOpenConsole(watching, appForeground: false, gameForeground: false), "Another app may not");
            Assert(InputPolicy.ShouldOpenConsole(anywhere, appForeground: false, gameForeground: false), "Unless the scope is any app");
            Throws<ArgumentNullException>(() => InputPolicy.ShouldOpenConsole(null!, false, false));
        });

        yield return ("Console link takes a word for what to do, not an option", () =>
        {
            // Options are shared across every command, so the sync actions read as words after it, the way other
            // terminals spell their subcommands (user rule).
            var log = new CmdLog();
            var host = new Host();
            var session = new CmdSession(host, log);
            session.Submit("link");
            Assert(LastOutput(log)[0].Contains("Not listening", StringComparison.Ordinal), "The host's answer is printed");
            session.Submit("link start");
            session.Submit("link stop");
            session.Submit("link again");
            Assert(host.SyncAsks.SequenceEqual([(false, false, false), (true, false, false), (false, true, false), (false, false, true)]),
                "Asked for: " + string.Join(" ", host.SyncAsks));
            session.Submit("link follow");
            session.Submit("link unfollow");
            Assert(host.FollowAsks.SequenceEqual([true, false]), "Following is asked for and stopped");
            session.Submit("link tiles");
            Equal(host.TileAsks, 1);
            Equal(host.SyncAsks.Count, 4);
            Assert(LastOutput(log)[0].Contains("412 compared", StringComparison.Ordinal), "The comparison is printed");
            // A word it does not know, or two of them, is refused rather than shown as a status line.
            session.Submit("link sideways");
            Equal(host.SyncAsks.Count, 4);
            session.Submit("link start stop");
            Equal(host.SyncAsks.Count, 4);
            // The old options are gone, so nothing else can collide with them.
            Assert(!CmdCatalog.All.Single(c => c.Name == "sync").OptionNames.Any(o => o is "-on" or "-off" or "-t" or "-f" or "-n"),
                "sync keeps no options of its own but -c");
        });

        yield return ("log shows what the app did lately, every line on asking, and report -log keeps it in a note", () =>
        {
            // User request: when the notes go missing in play, what the link, the game and following did can be read
            // afterwards. The window keeps the lines, stamped to the millisecond and the newest few thousand only.
            var stamped = new EventLog(() => new DateTime(2026, 10, 13, 17, 30, 1, 250));
            stamped.Add("link: the pipe is open");
            Equal(stamped.Lines()[0], "17:30:01.250  link: the pipe is open");
            for (var i = 0; i < EventLog.Capacity + 10; i++) stamped.Add("line " + i);
            Assert(stamped.Count == EventLog.Capacity && stamped.Lines()[0].EndsWith("  line 10", StringComparison.Ordinal), "The oldest go first");
            stamped.Clear();
            Equal(stamped.Count, 0);

            var host = new Host();
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            session.Submit("log");
            Equal(Output(log), "Nothing is in the log yet.");
            host.Logged.AddRange(Enumerable.Range(1, 50).Select(i => $"17:30:{i:00}.000  event {i}"));
            session.Submit("log");
            var latest = LastOutput(log);
            Assert(latest.Count == 42 && latest[0] == "The last 40 of 50 lines; log all shows every one."
                && latest[1].EndsWith("event 11", StringComparison.Ordinal) && latest[^2].EndsWith("event 50", StringComparison.Ordinal)
                && latest[^1] == "50 lines kept; report -log create <title> writes them into a note.", string.Join(" | ", latest));
            session.Submit("log all");
            Assert(LastOutput(log).Count == 51 && LastOutput(log)[0].EndsWith("event 1", StringComparison.Ordinal), "log all: every line");
            session.Submit("log nothing");
            Assert(Output(log).StartsWith("The syntax of the command is incorrect.", StringComparison.Ordinal), Output(log));

            // report -log writes it into a note, with create and write: create starts one, write adds below.
            session.Submit("report -log create run");
            Assert(host.Reports.TryGetValue("run", out var note) && note.Count == 50 && Output(log) == "Wrote 50 lines of the log to \"run\".", Output(log));
            session.Submit("report write -log run");
            Assert(host.Reports["run"].Count == 101 && host.Reports["run"][50] == "", "Added below, after a blank line");
            session.Submit("report -log create run");
            Assert(Output(log).StartsWith("\"run\" is already there.", StringComparison.Ordinal), Output(log));

            session.Submit("log erase");
            Assert(host.Logged.Count == 0 && Output(log) == "The log is empty now.", "erase empties it");
            session.Submit("report -log create empty");
            Equal(Output(log), "Nothing is in the log yet.");
        });

        yield return ("The console speaks the app's language: Korean from the banner on, the typed words as they are", () =>
        {
            // User rule: the console follows the app's language. Command, option and keyword names, ids, titles and setting
            // paths are what is typed, so they stay as they are in every language.
            var host = Library();
            host.Language = Languages.Korean;
            var log = new CmdLog();
            try
            {
                var session = new CmdSession(host, log);
                Equal(log.Lines[0], $"Visualizer 콘솔 [버전 {AppInfo.Version}]");
                Equal(log.Lines[1], "'cmd'를 치면 명령 목록이 나옵니다.");
                session.Submit("version");
                Lines(LastOutput(log), $"Visualizer {AppInfo.Version}");
                session.Submit("cmd");
                var listed = LastOutput(log);
                Assert(listed[0] == "도움말" && listed.Contains("곡") && listed.Any(line => line.TrimStart().StartsWith("load <title>", StringComparison.Ordinal)
                    && line.EndsWith("곡을 불러옵니다.", StringComparison.Ordinal)), "Groups and summaries in Korean: " + string.Join(" | ", listed));
                session.Submit("help version");
                var help = LastOutput(log);
                Assert(help[0] == "version" && help[1].Contains("-check는", StringComparison.Ordinal)
                    && help.Contains("  옵션: [-number | -check] [-copy]") && help.Any(line => line.Contains("-check", StringComparison.Ordinal)
                        && line.Contains("저장소", StringComparison.Ordinal)), "help in Korean: " + string.Join(" | ", help));
                session.Submit("blah");
                Equal(Output(log), "'blah'은(는) 명령이 아닙니다. 'cmd'를 치면 명령 목록이 나옵니다.");
                session.Submit("version -x");
                Equal(Output(log), "-x은(는) version의 옵션이 아닙니다. 옵션: [-number | -check] [-copy].");
                session.Submit("info 10000002");
                var info = LastOutput(log);
                Assert(info.Any(line => line.StartsWith("제목", StringComparison.Ordinal) && line.EndsWith("Plum - R", StringComparison.Ordinal))
                    && info.Any(line => line.StartsWith("숨김", StringComparison.Ordinal) && line.EndsWith("아니요", StringComparison.Ordinal)),
                    "Rows in Korean, titles as they are: " + string.Join(" | ", info));
                // The labels are Hangul, twice as wide as a letter, and the values still start in one column.
                var column = info.Select(line => CmdSession.Wide(line[..line.IndexOf("  ", StringComparison.Ordinal)]) + line[line.IndexOf("  ", StringComparison.Ordinal)..].TakeWhile(c => c == ' ').Count()).Distinct().ToArray();
                Assert(column.Length == 1, "One value column: " + string.Join(", ", column));
                session.Submit("id now");
                Equal(Output(log), "불러온 곡이 없습니다.");
                session.Submit("load");
                Lines(LastOutput(log), "제목이 필요합니다: 제목의 어느 부분, *, ., origin, edited 또는 link.", "사용법: load <title>");
                session.Submit("playlist base");
                Lines(LastOutput(log), "00000001  base    Built in");
                session.Submit("date");
                Assert(System.Text.RegularExpressions.Regex.IsMatch(Output(log), @"^\d{4}-\d{2}-\d{2} [월화수목금토일]요일 \d{2}:\d{2}:\d{2}$"), "A Korean weekday: " + Output(log));
                session.Submit("set timing.speed fast");
                Assert(Output(log).StartsWith("퍼센트 단위의 숫자를 씁니다.", StringComparison.Ordinal), "Setting errors in Korean: " + Output(log));
                Assert(!HasHangul(string.Join(" ", CmdCatalog.All.Select(command => command.Usage + command.Options + command.Keywords))), "Names stay as typed");

                // Back in English the next line is English again.
                host.Language = Languages.English;
                session.Submit("id now");
                Equal(Output(log), "No song is loaded.");
            }
            finally { ConsoleText.Language = Languages.English; }
        });

        yield return ("Console help: cmd by group, help and option with all and last, version, whoami hidden", () =>
        {
            var log = new CmdLog();
            var host = new Host();
            var session = new CmdSession(host, log);
            Assert(log.Lines[0].StartsWith("Visualizer Console [Version ", StringComparison.Ordinal) && log.Lines.Any(l => l.Contains("'cmd'")), "Banner");
            session.Submit("cmd");
            var list = LastOutput(log);
            Equal(list[0], "Help");
            Assert(list[1].TrimStart().StartsWith("cmd", StringComparison.Ordinal), "cmd first");
            var groups = CmdCatalog.Groups.Select(group => list.IndexOf(group)).ToArray();
            Assert(groups.All(index => index >= 0) && groups.SequenceEqual(groups.Order()), "Groups in order: " + string.Join(",", groups));
            foreach (var name in new[] { "help", "option", "keyword", "song", "info", "id", "title", "load", "playlist", "hide", "show", "edit",
                         "note", "override", "retitle", "sync", "remove", "restore", "delete", "get", "set", "speed", "property", "export", "import", "rollback", "reset", "do",
                         "clear", "exit" })
                Assert(list.Any(line => line.TrimStart().StartsWith(name, StringComparison.Ordinal)), name + " is listed");
            Assert(list.Any(line => line.TrimStart().StartsWith("whoami", StringComparison.Ordinal))
                && list.Any(line => line.TrimStart().StartsWith("date", StringComparison.Ordinal))
                && !list.Any(line => line.Contains("clr", StringComparison.Ordinal)), "whoami and date are listed, clr is gone");
            session.Submit("cmd -c");
            Assert(LastOutput(log).Contains("Copied to clipboard.") && host.Copied.Contains("playlist", StringComparison.Ordinal)
                && LastOutput(log).Count > 5, "-c shows the output and copies it");

            session.Submit("whoami");
            Lines(LastOutput(log), CmdSession.Whoami.ToArray());
            Assert(CmdSession.Whoami[0].StartsWith("██╗", StringComparison.Ordinal) && CmdSession.Whoami.Count == 6, "The art (app 0.5.5)");
            session.Submit("help whoami");
            Assert(LastOutput(log)[0].Contains("whoami", StringComparison.Ordinal), "whoami is explained like the rest");
            session.Submit("date");
            Assert(LastOutput(log)[0].Length >= 19 && LastOutput(log)[0].StartsWith("20", StringComparison.Ordinal),
                "date prints this computer's clock: " + LastOutput(log)[0]);
            // help lists a command's options and keywords, one a line, without being asked.
            session.Submit("help playlist");
            var help = LastOutput(log);
            // The word a bare command means is shown in brackets, not said in a sentence (user rule).
            Assert(help[0] == "playlist [<string>]" && help.Contains("  Options: [-id | -title] [-hidden | -shown] [-copy]")
                && help.Contains("    -id      Show only the ids.") && help.Contains("  Keywords: base, custom, edited, main, now, link, family, origin, [all]")
                && help.Contains("    main    The main window's list, in its order."), "help playlist: " + string.Join(" | ", help));
            session.Submit("help clear");
            Lines(LastOutput(log), "clear", "  Clear the screen.");
            session.Submit("note all");
            session.Submit("help last");
            Assert(LastOutput(log)[0].StartsWith("note [<id>] <range>", StringComparison.Ordinal), "last is the command before: " + Output(log));
            session.Submit("help");
            var every = LastOutput(log);
            Assert(CmdCatalog.Visible.All(command => every.Contains(command.Usage)), "help with nothing is help list");
            // With nothing or list: every option and what it does, then how to see a command's own.
            session.Submit("option");
            var options = LastOutput(log);
            Assert(options.Count == CmdCatalog.OptionHelp.Count + 1 && options[0].TrimStart().StartsWith("-c", StringComparison.Ordinal)
                && options[^1].Contains("option <cmd>", StringComparison.Ordinal), "Options only: " + string.Join(" | ", options));
            Assert(!options.Any(line => line.StartsWith("list:", StringComparison.Ordinal)), "No per-command lists");
            session.Submit("option list");
            Lines(LastOutput(log), options.ToArray());
            // all means "every one of them" and list "show them in a list"; these commands only list (user rule).
            session.Submit("option all");
            Lines(LastOutput(log), "'all' is not a command. Type cmd to see them.");
            // Uninstalling is PowerShell only (user rule): the console has no such command and says where it is done.
            Assert(CmdCatalog.Find("uninstall") is null, "No uninstall command");
            foreach (var line in new[] { "uninstall", "help uninstall" })
            {
                session.Submit(line);
                Lines(LastOutput(log), @"Uninstalling is not done from inside the visualizer: close it and run .\uninstall.ps1 in PowerShell, in its folder.");
            }
            session.Submit("option exit");
            Lines(LastOutput(log), "exit: [-keep]", "  -keep  Keep the screen for the next time the console opens.");
            session.Submit("option clear");
            Lines(LastOutput(log), "clear has no options.");
            // option -k adds the keywords; keyword -o is the same the other way round.
            session.Submit("option -k");
            var withKeywords = LastOutput(log);
            Assert(withKeywords.Contains("Keywords:") && withKeywords.Any(line => line.TrimStart().StartsWith("origin", StringComparison.Ordinal)
                && line.Contains("id, title, load, playlist", StringComparison.Ordinal)), "option -k lists every keyword: " + string.Join(" | ", withKeywords));
            session.Submit("keyword");
            var keywords = LastOutput(log);
            Assert(keywords.Count == CmdCatalog.AllKeywords.Count + 1 && keywords[^1].Contains("keyword <cmd>", StringComparison.Ordinal)
                && keywords[0].TrimStart().StartsWith("list", StringComparison.Ordinal), "keyword: " + string.Join(" | ", keywords));
            Assert(CmdCatalog.NameClash() is null, $"A command and a keyword share the name '{CmdCatalog.NameClash()}'");
            session.Submit("keyword playlist");
            // The word a bare playlist means stands in brackets, so it is seen and not read (user rule).
            Lines(LastOutput(log), ["playlist keywords: base, custom, edited, main, now, link, family, origin, [all]", "  base    Base songs.",
                "  custom  Custom songs.", "  edited  Edited songs.", "  main    The main window's list, in its order.", "  now     The loaded song.",
                "  link    The song the game is playing; the link must be on and the linker connected.",
                "  family  The loaded song's original with all its edits.", "  origin  The loaded song's original.", "  [all]   Every song."]);
            session.Submit("keyword -o playlist");
            var keywordFirst = LastOutput(log);
            session.Submit("option -k playlist");
            var optionFirst = LastOutput(log);
            Assert(keywordFirst[0].StartsWith("playlist keywords:", StringComparison.Ordinal) && optionFirst[0].StartsWith("playlist:", StringComparison.Ordinal)
                && keywordFirst.Select(line => line.Replace("playlist options:", "playlist:", StringComparison.Ordinal)).Order().SequenceEqual(optionFirst.Order()),
                "keyword -o and option -k differ only in order");
            session.Submit("keyword clear");
            Lines(LastOutput(log), "clear has no keywords.");
            session.Submit("keyword do");
            Assert(LastOutput(log).Any(line => line.Contains("clear-previous-checkpoint-key  Clear the previous checkpoint key.", StringComparison.Ordinal))
                && LastOutput(log).Any(line => line.Contains("view-playlist-details", StringComparison.Ordinal)), "do's actions are its keywords");
            session.Submit("keyword note");
            Lines(LastOutput(log), "note keywords: [now], all",
                "  [now]  What is on screen: the lanes being edited while the editor is open, else the loaded song.", "  all    Every note.");
            session.Submit("option nothing");
            Assert(LastOutput(log)[0].Contains("not a command", StringComparison.Ordinal), "Unknown command");
            // version is the version at once (user rule); -check is what it used to do, asking the repository as well.
            session.Submit("version");
            Lines(LastOutput(log), $"Visualizer {AppInfo.Version}");
            session.Submit("version -check");
            Lines(LastOutput(log), $"Visualizer {AppInfo.Version}", $"Applied {AppInfo.VersionDate}", $"Built   {AppInfo.BuildTime}",
                "Latest  yes, this is the latest version");
            session.Submit("version -n");
            Lines(LastOutput(log), AppInfo.Version);
            session.Submit("version -c -check");
            Equal(host.Copied, $"Visualizer {AppInfo.Version}{Environment.NewLine}Applied {AppInfo.VersionDate}{Environment.NewLine}Built   {AppInfo.BuildTime}"
                + $"{Environment.NewLine}Latest  yes, this is the latest version");
            session.Submit("version -number -check");
            Equal(Output(log), "-number and -check cannot be used together.");
            Assert(System.Text.RegularExpressions.Regex.IsMatch(AppInfo.VersionDate, @"^\d{4}-\d{2}-\d{2}$"), "Version date");
            // The build's own time, which is the file's: the build is deterministic, so the header's stamp is not one.
            Assert(System.Text.RegularExpressions.Regex.IsMatch(AppInfo.BuildTime, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$"), "Build time: " + AppInfo.BuildTime);

            var exited = 0;
            session.ExitRequested += () => exited++;
            session.Submit("clear");
            Equal(log.Lines.Count, 0);
            session.Submit("version");
            session.Submit("exit -m");
            Assert(exited == 1 && log.Lines.Any(line => line.StartsWith("Visualizer ", StringComparison.Ordinal)), "exit -m keeps the screen");
            session.Submit("exit");
            Assert(exited == 2 && log.Lines.Count == 0, "exit clears");
            Lines(new CmdSession(host, log).Log.Lines, $"Visualizer Console [Version {AppInfo.Version}]", "Type 'cmd' to see the commands.", "");
            Assert(log.History.Count > 0, "Earlier commands can still be recalled");
        });

        yield return ("Console note: tile and ms to the next note, chords, ranges, -c shows and copies, -p answers Y", () =>
        {
            // The user's example format: the last line has no ms.
            Lines(CmdSession.FormatNotes([new(0, 5, 1), new(100, 4, 1), new(200, 5, 1)], 1, 3), "n1 5 100", "n2 4 100", "n3 5");
            var chords = new VisualizerNote[] { new(0, 5, 2), new(333.5, 3, 4, true), new(333.5, 4, 4), new(333.5, 6, 4), new(500.4, 5, 4) };
            Lines(CmdSession.FormatNotes(chords, 1, 5), "n1 5 334", "n2-3 3-4 0", "n4 6 167", "n5 5");
            Lines(CmdSession.FormatNotes(chords, 3, 3), "n2-3 3-4");
            // all took the place of *.
            foreach (var (text, first, last) in new (string, int, int?)[] { ("all", 1, null), ("ALL", 1, null), ("1..3", 1, 3), ("100..", 100, null), ("..50", 1, 50), ("7", 7, 7) })
            {
                Assert(CmdSession.TryParseRange(text, out var a, out var b), "Parses " + text);
                Equal(a, first);
                Equal(b, last);
            }
            foreach (var bad in new[] { "0", "..", "5..2", "a..b", "1...3", "", "..0", "*" })
                Assert(!CmdSession.TryParseRange(bad, out _, out _), "Rejects " + bad);

            var log = new CmdLog();
            var times = new[] { 0d, 600, 1200, 1800, 1800, 2400 };
            var host = new Host { Timeline = new ParsedTimeline(times) };
            var session = new CmdSession(host, log);
            var notes = PlaybackSession.GenerateSongNotes(times, host.Settings);
            session.Submit("note 1..3");
            Lines(LastOutput(log), CmdSession.FormatNotes(notes, 1, 3).ToArray());
            session.Submit("NOTE all");
            Equal(LastOutput(log).Count, 5);
            session.Submit("note -c 2..");
            var shown = LastOutput(log);
            Assert(shown[^1] == "Copied to clipboard." && shown.Count(IsNoteLine) == 4, "-c shows the notes too (app 0.5.1)");
            Equal(host.Copied, string.Join(Environment.NewLine, CmdSession.FormatNotes(notes, 2, 6)));
            session.Submit("note 9");
            Assert(LastOutput(log).Single().Contains("6 notes"), "Past the last note");
            session.Submit("note 3..1");
            Equal(LastOutput(log)[0], "The syntax of the command is incorrect.");

            // [Y/n]: load the picked song first, then apply unapplied settings; -p answers both with Y.
            var timeline = new ParsedTimeline(new[] { 0d, 600, 1200 });
            var asking = new Host { SongToLoad = "Song A", Loadable = timeline, Pending = new VisualizerSettings { PlaybackSpeedPercent = 50 } };
            var questions = new CmdSession(asking, new CmdLog());
            questions.Submit("note all");
            Assert(questions.IsAsking && questions.Log.Lines[^1] == "The song is not loaded. Load \"Song A\" first? [Y/n]", "Asks to load");
            questions.Submit("");
            Assert(questions.IsAsking && asking.Loads == 0, "A blank answer asks again");
            questions.Submit("Y");
            Assert(questions.Log.Lines.Contains("The song is not loaded. Load \"Song A\" first? [Y/n] Y"), "The answer stays on the question line");
            Assert(questions.IsAsking && questions.Log.Lines[^1] == "There are settings that are not applied yet. Apply the settings? [Y/n]", "Then asks to apply");
            questions.Submit("y");
            Equal(asking.Settings.PlaybackSpeedPercent, 50d);
            var automatic = new Host { SongToLoad = "Song B", Loadable = timeline, Pending = new VisualizerSettings { PlaybackSpeedPercent = 200 } };
            var quick = new CmdSession(automatic, new CmdLog());
            quick.Submit("note -p all");
            Assert(!quick.IsAsking && automatic.Loads == 1 && automatic.Settings.PlaybackSpeedPercent == 200
                && quick.Log.Lines.Any(line => line.EndsWith("[Y/n] Y (-proceed)", StringComparison.Ordinal)), "-p answers Y and says so");

            // A placement problem reads in the console's language, which is the app's (user rule): English in English, and
            // Korean through and through in Korean.
            var english = new CmdSession(new Host { Timeline = new ParsedTimeline(new[] { 0d, 1, 2, 3, 4 }) }, new CmdLog());
            english.Submit("note all");
            Assert(Output(english.Log).Contains("The notes could not be placed.", StringComparison.Ordinal) && !HasHangul(Output(english.Log)), "English reason");
            var korean = new CmdSession(new Host { Timeline = new ParsedTimeline(new[] { 0d, 1, 2, 3, 4 }), Language = Languages.Korean,
                Settings = new VisualizerSettings { Language = Languages.Korean } }, new CmdLog());
            korean.Submit("note all");
            Assert(Output(korean.Log).StartsWith("노트를 배치하지 못했습니다.", StringComparison.Ordinal), "Korean reason: " + Output(korean.Log));
            ConsoleText.Language = Languages.English;
        });

        yield return ("Console songs: song, info, list with its filters, id, hide, show and playlist", () =>
        {
            var host = Library();
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            session.Submit("song");
            Lines(LastOutput(log), "No song is loaded.");
            host.LoadedSongName = "Plum - R (example)";
            session.Submit("song");
            Assert(Output(log).Contains("not in the song list", StringComparison.Ordinal), "The example has no id");
            host.LoadedId = "20000004";
            // now -id and now -tl print what id and title print.
            session.Submit("song -id");
            Lines(LastOutput(log), "20000004 (slot 2)");
            session.Submit("song -tl -c");
            Lines(LastOutput(log), "Plum - R (slot 2)", "Copied to clipboard.");
            session.Submit("song");
            var details = LastOutput(log);
            Assert(details.Contains("id           20000004") && details.Contains("length       2:05") && details.Contains("slot         2")
                && details.Contains("difficulty   7") && details.Any(line => line.StartsWith("path", StringComparison.Ordinal)), "Details: " + string.Join(" | ", details));
            session.Submit("info now");
            Lines(LastOutput(log), details.ToArray());
            session.Submit("info 10000002 -tl");
            Lines(LastOutput(log), "Plum - R");
            session.Submit("info -tl 10000002");
            Lines(LastOutput(log), "Plum - R");
            // The slot row is always there; an original says origin.
            session.Submit("info 10000002");
            var original = LastOutput(log);
            Assert(original.Contains("slot         origin") && original.Count(row => row.StartsWith("slot", StringComparison.Ordinal)) == 1
                && original.ToList().FindIndex(row => row.StartsWith("slot", StringComparison.Ordinal))
                    == original.ToList().FindIndex(row => row.StartsWith("checkpoints", StringComparison.Ordinal)) + 1,
                "Original's slot: " + string.Join(" | ", original));
            session.Submit("info");
            Assert(Output(log).StartsWith("An id is needed", StringComparison.Ordinal), "No default id");
            session.Submit("info 99999999");
            Assert(Output(log).StartsWith("No song has the id 99999999", StringComparison.Ordinal), "Unknown id");

            // Checkpoint 1 is the start of the song, not a checkpoint of the map.
            var plain = Song("10000009", "Plain", "custom", "9999/main.adofai");
            host.SongList.Add(plain with { Entry = plain.Entry with { Timeline = new ParsedTimeline(new[] { 0d, 500, 1000, 1500 },
                [new TimelineCheckpoint(1, 0)], true) } });
            var marked = Song("10000010", "Marked", "custom", "9998/main.adofai");
            host.SongList.Add(marked with { Entry = marked.Entry with { Timeline = new ParsedTimeline(new[] { 0d, 500, 1000, 1500 },
                [new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 1), new TimelineCheckpoint(3, 3)], true) } });
            session.Submit("info 10000009");
            Assert(LastOutput(log).Contains("checkpoints  0"), "Only the start: " + string.Join(" | ", LastOutput(log)));
            session.Submit("info 10000010");
            Assert(LastOutput(log).Contains("checkpoints  2"), "Two checkpoints: " + string.Join(" | ", LastOutput(log)));
            session.Submit("info 10000002");
            Assert(LastOutput(log).Contains("checkpoints  -"), "Unverified: " + string.Join(" | ", LastOutput(log)));

            // notes counts what is pressed; long notes and auto tiles get rows of their own, 0 included.
            var kinds = Song("10000011", "Kinds", "custom", "9997/main.adofai");
            host.SongList.Add(kinds with { Entry = kinds.Entry with { Timeline = new ParsedTimeline(new[] { 0d, 500, 1000, 1500, 2000, 2500 },
                hitKinds: new[] { HitKind.Tap, HitKind.HoldStart, HitKind.HoldEnd, HitKind.Auto, HitKind.Auto, HitKind.Tap }) } });
            session.Submit("info 10000011");
            var counts = LastOutput(log).ToList();
            Assert(counts.Contains("notes        3") && counts.Contains("longnotes    1") && counts.Contains("autonotes    2")
                && counts.FindIndex(row => row.StartsWith("length", StringComparison.Ordinal)) + 1 == counts.IndexOf("notes        3")
                && counts.IndexOf("notes        3") + 1 == counts.IndexOf("longnotes    1")
                && counts.IndexOf("longnotes    1") + 1 == counts.IndexOf("autonotes    2")
                && counts.IndexOf("autonotes    2") + 1 == counts.FindIndex(row => row.StartsWith("difficulty", StringComparison.Ordinal)),
                "Note counts: " + string.Join(" | ", counts));
            session.Submit("info 10000002");
            var taps = LastOutput(log);
            Assert(taps.Contains("notes        3") && taps.Contains("longnotes    0") && taps.Contains("autonotes    0"), "Only taps: " + string.Join(" | ", taps));
            host.SongList.RemoveAll(song => song.Id is "10000009" or "10000010" or "10000011");

            session.Submit("playlist");
            Equal(LastOutput(log).Count, 5);
            Assert(LastOutput(log).Contains("10000003  custom  Hidden song  [hidden]"), "Hidden songs are marked");
            session.Submit("playlist -id custom");
            Lines(LastOutput(log), "10000002", "10000003");
            session.Submit("playlist -tl -s custom");
            Lines(LastOutput(log), "Plum - R");
            session.Submit("playlist -h");
            Lines(LastOutput(log), "10000003  custom  Hidden song  [hidden]");
            // now is the loaded song alone; family its original with every edit; origin the original.
            session.Submit("playlist now");
            Lines(LastOutput(log), "20000004  edited  2*Plum - R");
            session.Submit("playlist -id family");
            Lines(LastOutput(log), "10000002", "20000004 (slot 2)", "20000005 (slot 5)");
            session.Submit("playlist origin");
            Lines(LastOutput(log), "10000002  custom  Plum - R");
            session.Submit("id");
            Lines(LastOutput(log), "20000004 (slot 2)");
            session.Submit("id now");
            Lines(LastOutput(log), "20000004 (slot 2)");
            session.Submit("id family");
            Lines(LastOutput(log), "10000002", "20000004 (slot 2)", "20000005 (slot 5)");
            session.Submit("id origin");
            Lines(LastOutput(log), "10000002");
            session.Submit("id -tl");
            Lines(LastOutput(log), "20000004  Plum - R (slot 2)");
            session.Submit("title");
            Lines(LastOutput(log), "Plum - R (slot 2)");
            session.Submit("title -id");
            Lines(LastOutput(log), "Plum - R (slot 2)  20000004");
            session.Submit("title family");
            Lines(LastOutput(log), "Plum - R", "Plum - R (slot 2)", "Plum - R (slot 5)");
            session.Submit("title -id base");
            Lines(LastOutput(log), "Built in  00000001");
            // An original's origin is itself, and its id has no slot.
            host.LoadedId = "10000002";
            session.Submit("id origin");
            Lines(LastOutput(log), "10000002");
            session.Submit("playlist -tl origin");
            Lines(LastOutput(log), "Plum - R");
            host.LoadedId = "20000004";
            session.Submit("playlist nothing");
            Lines(LastOutput(log), "Use base, custom, edited, main, now, link, family, origin or all.");
            // link is the song the game is playing (user rule), only while the link is on: the window says why not otherwise.
            session.Submit("id link");
            Lines(LastOutput(log), "The link is not on. link start first.");
            session.Submit("load link");
            Assert(host.LoadedId == "20000004" && LastOutput(log)[^1] == "The link is not on. link start first.", "Nothing loads without the link");
            host.GameSong = "10000002";
            session.Submit("id link");
            Lines(LastOutput(log), "10000002");
            session.Submit("title link");
            Lines(LastOutput(log), "Plum - R");
            session.Submit("info link");
            Assert(LastOutput(log).Any(line => line.Contains("10000002", StringComparison.Ordinal)), "info link: " + Output(log));
            // load link takes the original the game plays; -slot picks one of its edits, as load . does (the title comes last).
            session.Submit("load link");
            Equal(host.LoadedId, "10000002");
            session.Submit("load -slot:5 link");
            Assert(host.LoadedId == "20000005", "load -slot:5 link: " + Output(log));
            host.GameSong = null;
            host.LoadedId = "20000004";
            // main and playlist: the main window's list in its order.
            host.Current.AddRange(["20000005", "10000002", "00000001"]);
            session.Submit("playlist -id main");
            Lines(LastOutput(log), "20000005 (slot 5)", "10000002", "00000001");
            // playlist main is what playlist printed on its own, before the two commands became one.
            session.Submit("playlist main");
            Lines(LastOutput(log), "20000005  edited  5*Plum - R", "10000002  custom  Plum - R", "00000001  base    Built in");
            session.Submit("playlist -c main");
            Assert(host.Copied.StartsWith("20000005  edited  5*Plum - R", StringComparison.Ordinal), "playlist -c copies");
            // A bare playlist is every song: all is the word it stands for.
            session.Submit("playlist");
            Equal(LastOutput(log).Count, 5);
            session.Submit("playlist base custom");
            Assert(Output(log).StartsWith("The syntax of the command is incorrect.", StringComparison.Ordinal), "One word at a time");
            // Without a loaded song, id and title have nothing to show.
            host.LoadedId = null;
            host.LoadedSongName = null;
            session.Submit("id");
            Lines(LastOutput(log), "No song is loaded.");
            session.Submit("title family");
            Lines(LastOutput(log), "No song is loaded.");
            session.Submit("id all");
            Lines(LastOutput(log), "00000001", "10000002", "10000003", "20000004 (slot 2)", "20000005 (slot 5)");
            host.LoadedId = "20000004";

            session.Submit("hide 10000002");
            Assert(host.Calls.Contains("hide 10000002") && Output(log) == "Hidden: 10000002 Plum - R.", "hide");
            session.Submit("hide 10000002");
            Assert(Output(log).Contains("already hidden", StringComparison.Ordinal), "Hidden twice");
            session.Submit("show 10000003");
            Assert(host.Calls.Contains("show 10000003"), "show");
            // The song list window moved from playlist to do view-playlist-details.
            session.Submit("do view-playlist-details");
            Assert(host.Calls.Contains("playlist") && Output(log) == "The song list changes are applied.", "do view-playlist-details");
        });

        yield return ("Console load -force: asks first, forces only unsupported songs and says the format it read", () =>
        {
            var host = Library();
            var index = host.SongList.FindIndex(song => song.Id == "00000001");
            host.SongList[index] = host.SongList[index] with { Entry = host.SongList[index].Entry with { Status = MapParseStatus.Unsupported, Timeline = null } };
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            // Risky, so it always asks; no is no.
            session.Submit("load -force Built in");
            Assert(session.IsAsking && log.Lines[^1].StartsWith("Built in is unsupported. Force it,", StringComparison.Ordinal), log.Lines[^1]);
            session.Submit("n");
            Assert(host.Forced.Count == 0 && host.LoadedId is null && LastOutput(log)[^1] == "Cancelled.", "Declined");
            session.Submit("load -force -sm Built in");
            session.Submit("y");
            Assert(host.Forced.SequenceEqual(["00000001"]) && host.Loaded[^1] == ("00000001", true), "Forced with the speed choice");
            Lines(LastOutput(log).Skip(1), "Loaded 00000001 Built in.",
                "Forced: read as format version 2. Its timing may differ from the game; the song list marks it forced.");
            // Any other song loads as it is, with a word that -force did nothing.
            session.Submit("load -force -og Plum");
            Assert(host.Forced.Count == 1 && host.LoadedId == "10000002", "A supported song is not forced");
            Lines(LastOutput(log), "-force is only for unsupported songs, and Plum - R is not one, so it loads as it is.", "Loaded 10000002 Plum - R.");
            // A song no format reads stays unsupported, with the reason from the app.
            host.SongList[index] = host.SongList[index] with { Entry = host.SongList[index].Entry with { Status = MapParseStatus.Unsupported, ForcedFormatVersion = null } };
            host.ForceError = "No verified format version reads it.";
            session.Submit("load -force -id 00000001");
            session.Submit("y");
            Equal(LastOutput(log)[^1], "00000001 Built in: No verified format version reads it.");
            Assert(CmdCatalog.Find("load")!.OptionsText.EndsWith("[-force]", StringComparison.Ordinal), "-force is a group of its own");
        });

        yield return ("Console load: any part of a title, edits to pick from, origin and edited, ids, slots and the practice speed", () =>
        {
            // The user's example list: title (id), two edits in slot 1, one of them of an original that is not there.
            var host = new Host();
            host.SongList.AddRange(
            [
                Song("12345678", "abcd", "custom", "a/main.adofai"),
                Song("12333333", "Raimukun - C†hulhu", "custom", "r/main.adofai"),
                Song("14698323", "2hello", "custom", "h/main.adofai"),
                Song("13532223", "2hi", "custom", "i/main.adofai"),
                Song("18569403", "22222222", "custom", "t/main.adofai"),
                Song("28534333", "1*22222222", "edited", "t/main.adofai", slot: 1),
                Song("22222222", "1*2bye", "edited", "b/main.adofai", slot: 1)
            ]);
            var log = new CmdLog();
            var session = new CmdSession(host, log);

            // load 2: every original with a 2 in its title, each with its edits under it (2bye is an edit), to pick from.
            session.Submit("load 2");
            Assert(session.IsAsking && log.Lines[^1] == "Pick 1-4:", "Asks for a number: " + log.Lines[^1]);
            Lines(LastOutput(log), "01  18569403  custom  22222222", "02  28534333  edited  1*22222222", "03  14698323  custom  2hello",
                "04  13532223  custom  2hi", "Pick 1-4:");
            session.Submit("03");
            Assert(!session.IsAsking && host.LoadedId == "14698323" && log.Lines.Contains("Loaded 14698323 2hello."), "01 and 1 read the same; 03 is 2hello");

            // load -sl1 2: the slot-1 edits with a 2 in their title, in one list, the one of a missing original too.
            session.Submit("load -sl1 2");
            Lines(LastOutput(log), "01  28534333  edited  1*22222222", "02  22222222  edited  1*2bye", "Pick 1-2:");
            session.Submit("2");
            Equal(host.LoadedId, "22222222");

            // load hu, then Tab: the only title with hu in it, anywhere; Enter loads it at once, as it has no edits.
            var window = new CmdWindow(session, "white") { Left = -20000, Top = -20000, ShowActivated = false };
            window.Show();
            // The window holds the transcript and, over it, the note editor; the transcript is the first.
            var screen = FindAll<TextBox>(window).First();
            screen.Select(screen.Text.Length, 0);
            screen.SelectedText = "load hu";
            window.Complete();
            Equal(window.InputText, "load Raimukun - C†hulhu");
            window.Run(window.InputText);
            Equal(host.LoadedId, "12333333");
            window.Close();

            // load 12345678: no title has it, so the whole id is tried next.
            session.Submit("load 12345678");
            Assert(host.LoadedId == "12345678" && Output(log) == "Loaded 12345678 abcd.", "The id after the titles: " + Output(log));
            // load -id 22222222: by id at once; -id 2: an id is given whole.
            session.Submit("load -id 22222222");
            Equal(host.LoadedId, "22222222");
            session.Submit("load -id 2");
            Assert(Output(log).StartsWith("No song has the id 2.", StringComparison.Ordinal), "-id needs the whole id");
            session.Submit("load -id 22222222 more");
            Equal(Output(log), "-id takes one id, all 8 characters of it.");
            // A title that is also a number: 22222222 is a title first (and has an edit, so it asks).
            session.Submit("load 22222222");
            Lines(LastOutput(log), "01  18569403  custom  22222222", "02  28534333  edited  1*22222222", "Pick 1-2:");
            session.Submit("1");
            Equal(host.LoadedId, "18569403");
            // Nothing by title or id.
            session.Submit("load 1zzzzzzz");
            Equal(Output(log), "No song title has '1zzzzzzz', and no song has the id 1zzzzzzz.");
            session.Submit("load nothing");
            Equal(Output(log), "No song title has 'nothing'.");
            session.Submit("load");
            Assert(Output(log).StartsWith("A title is needed", StringComparison.Ordinal), "A title is needed");

            // An edit by its full title; letter case does not matter, and an id's letters neither.
            session.Submit("load 1*22222222");
            Equal(host.LoadedId, "28534333");
            session.Submit("LOAD 1*2BYE");
            Equal(host.LoadedId, "22222222");
            // -og keeps to originals, -sl to edits, -sl<n> to one slot.
            session.Submit("load -og 2222");
            Equal(host.LoadedId, "18569403");
            session.Submit("load -og 2h");
            Lines(LastOutput(log), "01  14698323  custom  2hello", "02  13532223  custom  2hi", "Pick 1-2:");
            session.Submit("2");
            Equal(host.LoadedId, "13532223");
            session.Submit("load -sl bye");
            Equal(host.LoadedId, "22222222");
            session.Submit("load -sl3 2");
            Equal(Output(log), "No edit title has '2'.");
            session.Submit("load abc");
            Equal(host.LoadedId, "12345678");

            // A slip is asked once more; the second ends it (user rule).
            session.Submit("load 2h");
            session.Submit("x");
            Assert(session.IsAsking && log.Lines[^1] == "Pick 1-2:" && log.Lines[^2] == "Pick 1-2: x", "Asked again after a slip");
            session.Submit("7");
            Assert(!session.IsAsking && Output(log).EndsWith("Cancelled.", StringComparison.Ordinal) && host.LoadedId == "12345678", "Cancelled the second time");
            session.Submit("load 2h");
            session.Submit("");
            session.Submit("02");
            Equal(host.LoadedId, "13532223");

            // origin and edited from the loaded song.
            session.Submit("load 1*22222222");
            session.Submit("load edited");
            Equal(Output(log), "1*22222222 is an edit. load origin loads its original.");
            session.Submit("load -sl origin");
            Equal(Output(log), "-sl goes with edited: origin loads the original.");
            session.Submit("load origin");
            Equal(host.LoadedId, "18569403");
            session.Submit("load origin");
            Equal(Output(log), "22222222 is already the original. load edited loads its edits.");
            session.Submit("load -og edited");
            Equal(Output(log), "-og goes with origin: edited loads an edit.");
            session.Submit("load -sl2 edited");
            Equal(Output(log), "22222222 has no edit in slot 2.");
            session.Submit("load edited");
            Equal(host.LoadedId, "28534333");
            session.Submit("load 1*2bye");
            session.Submit("load origin");
            Equal(Output(log), "The original of 1*2bye is not in the song list.");
            session.Submit("load abcd");
            session.Submit("load origin");
            Equal(Output(log), "abcd is already the original.");
            session.Submit("load edited");
            Equal(Output(log), "abcd has no edits.");
            host.SongList.Add(Song("28534334", "2*22222222", "edited", "t/main.adofai", slot: 2));
            session.Submit("load 22222222");
            session.Submit("1");
            session.Submit("load edited");
            Lines(LastOutput(log), "01  28534333  edited  1*22222222", "02  28534334  edited  2*22222222", "Pick 1-2:");
            session.Submit("2");
            Equal(host.LoadedId, "28534334");
            session.Submit("load origin");
            session.Submit("load -sl1 edited");
            Equal(host.LoadedId, "28534333");
            host.LoadedId = null;
            host.LoadedSongName = null;
            session.Submit("load origin");
            Equal(Output(log), "No song is loaded.");

            // The practice speed goes back to 100% unless it is kept; -sm keeps it and -sr resets it either way.
            host.Settings = host.Settings with { PlaybackSpeedPercent = 80 };
            session.Submit("load -sm abcd");
            Assert(host.Settings.PlaybackSpeedPercent == 80 && host.Loaded[^1] == ("12345678", true), "-sm keeps the speed");
            session.Submit("load abcd");
            Lines(LastOutput(log), "Loaded 12345678 abcd.", "timing.speed: 80 → 100. rollback undoes it; load -speed keeps the speed.");
            Assert(host.Loaded[^1] == ("12345678", null), "Without an option the setting decides");
            host.Settings = host.Settings with { PlaybackSpeedPercent = 80, KeepSpeedOnLoad = true };
            session.Submit("load abcd");
            Equal(host.Settings.PlaybackSpeedPercent, 80d);
            session.Submit("load -sr abcd");
            Assert(host.Settings.PlaybackSpeedPercent == 100 && host.Loaded[^1] == ("12345678", false), "-sr resets it anyway");

            // A song that cannot play says why; the settings window closes first.
            host.LoadError = "The map is Unsupported.";
            session.Submit("load hello");
            Equal(Output(log), "14698323 2hello: The map is Unsupported.");
            host.LoadError = null;
            host.SettingsOpen = true;
            session.Submit("load hello");
            Assert(!host.SettingsOpen && host.LoadedId == "14698323", "The settings close first");

            // More than 99 to pick from asks for more of the title; 99 are listed.
            for (var i = 0; i < 97; i++) host.SongList.Add(Song("1" + i.ToString("0000000", CultureInfo.InvariantCulture), $"Many {i:00}", "custom", $"m{i}/main.adofai"));
            host.SongList.Add(Song("1many098", "Many 97", "custom", "m97/main.adofai"));
            host.SongList.Add(Song("1many099", "Many 98", "custom", "m98/main.adofai"));
            session.Submit("load Many");
            Assert(session.IsAsking && log.Lines[^1] == "Pick 1-99:" && log.Lines[^2].StartsWith("99  ", StringComparison.Ordinal), "99 are listed");
            session.Submit("99");
            Equal(host.LoadedId, "1many099");
            host.SongList.Add(Song("1many100", "Many 99", "custom", "m99/main.adofai"));
            session.Submit("load Many");
            Assert(!session.IsAsking && Output(log) == "100 songs match. Type more of the title.", "100 ask for more");
        });

        yield return ("A prompt with blanks asks for each one, takes a command's output, and copies once it is confirmed", () =>
        {
            // User request: the blanks are asked for one at a time. Plain text is the value, /paste and /note -json put an
            // output in, /skip leaves the blank, /back /again /cancel move around, and the end shows it before copying.
            var host = new Host { Timeline = new ParsedTimeline(new[] { 0d, 600, 1200, 1800 }) };
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            session.Submit("prompt note-lanes");
            Assert(session.IsFilling && Output(log).StartsWith("note-lanes: 2 blanks", StringComparison.Ordinal), Output(log));
            Assert(session.Prompt == CmdSession.FillPrompt + "note-lanes (1/2)>", session.Prompt);

            // A blank line is no answer, as everywhere else in the console.
            session.Submit("");
            Assert(session.Prompt == CmdSession.FillPrompt + "note-lanes (1/2)>", "Enter asks the same blank again");
            session.Submit("lanes 3 and 4 only");
            Assert(session.Prompt == CmdSession.FillPrompt + "note-lanes (2/2)>", "The text was taken and the next blank asked");

            // Only commands that print and stop may fill a blank.
            session.Submit("/load abcd");
            Assert(Output(log).Contains("cannot fill a blank", StringComparison.Ordinal) && session.Prompt == CmdSession.FillPrompt + "note-lanes (2/2)>",
                Output(log));
            session.Submit("/nonsense");
            Assert(Output(log).Contains("is not recognized as a command", StringComparison.Ordinal), Output(log));
            session.Submit("/note -json all");
            Assert(Output(log).StartsWith("Took ", StringComparison.Ordinal), Output(log));

            // The filled prompt is shown short, with the long answer folded, and detail shows all of it.
            var shown = LastOutput(log);
            Assert(shown.Any(line => line.Contains("more lines", StringComparison.Ordinal)), "The JSON is folded: " + string.Join(" | ", shown));
            Assert(shown[^1].EndsWith("[Y/n/detail]", StringComparison.Ordinal), shown[^1]);
            // Answering a question prints no prompt line, so what detail added is read from where the screen was.
            var mark = log.Lines.Count;
            session.Submit("d");
            var whole = log.Lines.Skip(mark).Where(line => line.Length > 0).ToList();
            Assert(!whole.Any(line => line.Contains("more lines", StringComparison.Ordinal)) && whole[^1].EndsWith("[Y/n]", StringComparison.Ordinal),
                "detail shows everything and asks again: " + string.Join(" | ", whole.TakeLast(3)));
            session.Submit("y");
            Assert(!session.IsFilling && host.Copied.Contains("lanes 3 and 4 only", StringComparison.Ordinal)
                && host.Copied.Contains("\"tile\":", StringComparison.Ordinal), "The filled prompt is on the clipboard");
            Assert(!host.Copied.Contains("{request}", StringComparison.Ordinal), "The blanks are gone from what was copied");

            // /skip leaves the line that tells a model to ask instead of guessing.
            session.Submit("prompt note-lanes");
            session.Submit("/skip");
            session.Submit("/skip");
            session.Submit("y");
            Assert(host.Copied.Contains("WRITE THE REQUEST HERE", StringComparison.Ordinal)
                && host.Copied.Contains("PUT THE note -json OUTPUT HERE", StringComparison.Ordinal), "Skipped blanks keep their marker");

            // /back, /again and /cancel; cancelling prints what was typed so it is not lost.
            session.Submit("prompt note-lanes");
            session.Submit("first answer");
            session.Submit("/back");
            Assert(session.Prompt == CmdSession.FillPrompt + "note-lanes (1/2)>", "/back returns to the blank before");
            session.Submit("second answer");
            session.Submit("/again");
            Assert(session.Prompt == CmdSession.FillPrompt + "note-lanes (1/2)>" && Output(log).StartsWith("Starting over", StringComparison.Ordinal), Output(log));
            session.Submit("kept text");
            session.Submit("/cancel");
            Assert(!session.IsFilling && Output(log).Contains("kept text", StringComparison.Ordinal), "Cancelling shows what was filled in");

            // Answering n at the end cancels, but what was filled in is printed once so a long request is not lost.
            session.Submit("prompt note-lanes");
            session.Submit("lanes 5 and 6 please");
            session.Submit("/skip");
            session.Submit("n");
            Assert(!session.IsFilling, "n ends it");
            var afterNo = Output(log);
            Assert(afterNo.Contains("cancelled", StringComparison.Ordinal) && afterNo.Contains("lanes 5 and 6 please", StringComparison.Ordinal),
                "What was typed is printed on the way out: " + afterNo);

            // A slash can still be a value, and the whole line after /string is taken as it is.
            session.Submit("prompt note-lanes");
            session.Submit("/string /text with a slash");
            session.Submit("/skip");
            session.Submit("y");
            Assert(host.Copied.Contains("/text with a slash", StringComparison.Ordinal), "/string keeps the slash");
        });

        yield return ("Tab offers what each command is about, and report lists its notes and the editor's commands", () =>
        {
            // User request: report and prompt complete their own names, not song titles, and every command offers its own
            // keywords. The words come from the same places the commands read.
            var host = Library();
            host.Reports["speed box"] = ["the box keeps the %", "and the button is gone"];
            host.ReportDone["speed box"] = "0.9.1";
            host.Reports["lane idea"] = ["chords on 3 and 4"];
            host.ReportVersions["lane idea"] = "0.9.0";
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            IReadOnlyList<string> Complete(string line) => session.Complete(line, out _);

            // Its word first, since report always needs one; then the notes that are there, for the words that name one.
            Lines(Complete("report "), "list", "create", "write", "print", "done", "undone", "ask", "state", "erase", "sweep",
                "rename", "colon");
            Lines(Complete("report s"), "state", "sweep");
            Lines(Complete("report write "), "lane idea", "speed box");
            Lines(Complete("report print s"), "speed box");
            Lines(Complete("report -copy print -proceed l"), "lane idea");
            Assert(!Complete("report create ").Any() && !Complete("report sweep ").Any(), "A new title or no title has nothing to offer");
            Assert(!Complete("report lane ").Any(), "A title where the word goes is not offered on");
            // Options are offered on either side of the words, as they are read on either side (user rule).
            Lines(Complete("report print -"), "-force", "-proceed", "-copy", "-log");
            Lines(Complete("playlist base -i"), "-id");
            Lines(Complete("playlist -c base -"), "-id", "-title", "-hidden", "-shown");
            Lines(Complete("prompt "), "list", "note-lanes");
            // help, option and keyword list with list; all is not one of their words.
            Assert(Complete("help ").Contains("list") && !Complete("help ").Contains("all") && Complete("help ").Contains("last"), "help's words");
            Assert(!Complete("retitle ").Any(), "A new name has nothing to offer");
            // Keywords belong to their command, whatever else the argument takes.
            Lines(Complete("link "), "start", "stop", "tiles", "follow", "unfollow", "again", "auto", "manual");
            Lines(Complete("install "), "all", "app", "source", "linker", "maps", "edits", "data");
            // A list takes its words at every place, less those already in it, after a comma with or without a space.
            Lines(Complete("scannow maps, "), "all", "app", "source", "linker", "edits", "data");
            Lines(Complete("scannow maps,li"), "linker");
            Lines(Complete("scannow maps, linker, d"), "data");
            Lines(Complete("autosave "), "on", "off");
            Assert(Complete("load ").Contains("origin"), "load still offers its own words");

            // -ls: every note with the version it was written for and whether it is done.
            session.Submit("report list");
            var listed = LastOutput(log);
            // Two notes and the count of what is still waiting, which is what the list is for (user rule).
            Assert(listed.Count == 3 && listed[0].Contains("lane idea", StringComparison.Ordinal) && listed[0].Contains("0.9.0", StringComparison.Ordinal)
                && listed[0].Contains("open", StringComparison.Ordinal), string.Join(" | ", listed));
            Assert(listed[1].Contains("speed box", StringComparison.Ordinal) && listed[1].Contains("done 0.9.1", StringComparison.Ordinal),
                string.Join(" | ", listed));
            Assert(listed[2].Contains("1 of 2 still open", StringComparison.Ordinal), listed[2]);
            // A word after it keeps only the notes holding it, in their title or their text.
            session.Submit("report list chords");
            Assert(LastOutput(log).Count == 2 && LastOutput(log)[0].Contains("lane idea", StringComparison.Ordinal),
                "Only the note holding it: " + Output(log));
            session.Submit("report list nothing at all");
            Assert(Output(log).StartsWith("No note holds", StringComparison.Ordinal), Output(log));

            // -col: what can be typed after a colon in the editor.
            session.Submit("report colon");
            var colons = LastOutput(log);
            Assert(colons.Count == 5 && colons[0].TrimStart().StartsWith(":w", StringComparison.Ordinal)
                && colons.Any(line => line.Contains(":delete", StringComparison.Ordinal)), string.Join(" | ", colons));
        });

        yield return ("report opens a note in the console's own editor: modes, the status line and its commands", () =>
        {
            // User request: a note opens over the transcript in the same window, i writes and Escape stops, Enter is a
            // line break, and :wq writes it and goes back. Keys go through Press, as the note editor's do.
            var host = new Host();
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            var window = new CmdWindow(session, "space") { Left = -20000, Top = -20000, ShowActivated = false };
            window.Show();
            session.Submit("report list");
            Assert(Output(log).StartsWith("No notes are written yet.", StringComparison.Ordinal), "Nothing yet");

            session.Submit("report create speed box");
            var editor = window.Editor;
            Assert(window.IsWriting && !editor.Insert, "The note is open and not being written yet");
            Assert(editor.Status.Contains("\"speed box\"", StringComparison.Ordinal) && editor.Status.Contains("1 lines", StringComparison.Ordinal),
                editor.Status);

            // Reading: a letter is not text. Writing: it is, and Enter makes a line.
            Assert(editor.Typed("x") && editor.Lines[0].Length == 0, "Letters do not go in while reading");
            Assert(editor.Press(Key.I, ModifierKeys.None) && editor.Insert && editor.Status.EndsWith("-- INSERT --", StringComparison.Ordinal),
                editor.Status);
            Write(editor, "the box keeps the %" + Environment.NewLine + "and the button is gone");
            Equal(editor.Lines.Count, 2);
            Assert(editor.Changed && editor.Status.Contains("[+]", StringComparison.Ordinal), "Unwritten changes are marked: " + editor.Status);
            Assert(editor.Press(Key.Escape, ModifierKeys.None) && !editor.Insert, "Escape stops writing");
            Assert(host.Reports.Count == 0, "Nothing is written until it is asked for");

            // A key that stands for a letter is never swallowed: ":" is Shift and a key that differs by keyboard, and it
            // only reaches the editor as a text event after the key itself goes through.
            Assert(!editor.Press(Key.OemSemicolon, ModifierKeys.Shift), "The key that types \":\" is left alone");
            Assert(!editor.Press(Key.Y, ModifierKeys.None), "And so is the one that answers a question");
            // The command line is typed into the status line.
            Command(editor, ":w");
            Assert(host.Reports["speed box"].Count == 2 && !editor.Changed && editor.Status.Contains("written", StringComparison.Ordinal),
                editor.Status);
            Command(editor, ":nope");
            Assert(editor.Status.StartsWith("Not an editor command", StringComparison.Ordinal), editor.Status);

            // :q with changes asks in the status line; n stays, y leaves.
            editor.Press(Key.I, ModifierKeys.None);
            Write(editor, " more");
            editor.Press(Key.Escape, ModifierKeys.None);
            Command(editor, ":q");
            Assert(editor.Status.EndsWith("[Y/n]", StringComparison.Ordinal), editor.Status);
            editor.Typed("n");
            Assert(window.IsWriting && editor.Status.EndsWith("[Y/n] n", StringComparison.Ordinal), "The letter alone does nothing: " + editor.Status);
            editor.Press(Key.Enter, ModifierKeys.None);
            Assert(window.IsWriting, "n keeps the note open");
            Command(editor, ":wq");
            Assert(!window.IsWriting && host.Reports["speed box"][^1].EndsWith(" more", StringComparison.Ordinal), "Written and closed");

            // Closing the console keeps what is open, and opening it again comes back to it (user rule).
            session.Submit("report write speed box");
            editor.Press(Key.I, ModifierKeys.None);
            Write(editor, " and one more");
            window.Close();
            Assert(log.Report is { Changed: true }, "The note is kept as it was");
            var again = new CmdWindow(new CmdSession(host, log), "space") { Left = -20000, Top = -20000, ShowActivated = false };
            again.Show();
            // Reopening puts the cursor at the top, as a terminal editor does, so what was typed is at the start of line 1.
            Assert(again.IsWriting && again.Editor.Lines[0].StartsWith(" and one more", StringComparison.Ordinal), "It is open again");

            // The keys are on the bottom line now, since the console line that used to list them is gone (user rule).
            Assert(again.Editor.Status.EndsWith(ReportEditor.Keys, StringComparison.Ordinal), again.Editor.Status);
            Assert(!Output(log).Contains("i write", StringComparison.OrdinalIgnoreCase), "The console does not repeat them: " + Output(log));

            // :copy takes the note, or the selection when there is one; :q! is gone.
            Command(again.Editor, ":copy");
            Assert(host.Copied.StartsWith(" and one more", StringComparison.Ordinal) && again.Editor.Status.Contains("copied", StringComparison.Ordinal),
                $"The whole note: {host.Copied} / {again.Editor.Status}");
            Command(again.Editor, ":q!");
            Assert(again.IsWriting && again.Editor.Status.StartsWith("Not an editor command", StringComparison.Ordinal), again.Editor.Status);

            // :d is :delete, and both ask before the note goes.
            Command(again.Editor, ":d");
            Assert(again.Editor.Status.StartsWith("Delete", StringComparison.Ordinal), again.Editor.Status);
            Answer(again.Editor, "y");
            Assert(!again.IsWriting && host.Reports.Count == 0 && log.Report is null, "Deleted and closed");
            again.Close();
        });

        yield return ("inspect starts, stops and shows its results; -flow keeps the console until the run ends", () =>
        {
            // User rule: inspect is a command of its own. start always asks first; result shows the last run, -runs:n the
            // last n and -runs:* every run kept; -flow goes with start and -runs with result only.
            var host = new Host();
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            session.Submit("inspect");
            Equal(Output(log), "inspect needs one of: start, stop, result.");
            session.Submit("inspect nothing");
            Assert(Output(log).StartsWith("'nothing' is not one of inspect's words.", StringComparison.Ordinal), Output(log));
            session.Submit("inspect result");
            Equal(Output(log), "No inspect run is kept. inspect start makes one.");
            host.Inspected = ["link inspect #1", "problems: none"];
            session.Submit("inspect result");
            Assert(host.InspectAsked == 1 && LastOutput(log).SequenceEqual(new[] { "link inspect #1", "problems: none" }), Output(log));
            session.Submit("inspect -runs:* result");
            Equal(host.InspectAsked, 0);
            session.Submit("inspect -runs:3 result");
            Equal(host.InspectAsked, 3);
            session.Submit("inspect -runs:0 result");
            Assert(Output(log).Contains("is not a count", StringComparison.Ordinal), Output(log));
            session.Submit("inspect -flow stop");
            Equal(Output(log), "-flow goes with start: inspect -flow start.");
            session.Submit("inspect -runs:2 start");
            Equal(Output(log), "-runs goes with result: inspect -runs:3 result.");
            session.Submit("inspect start");
            Assert(session.IsAsking && log.Lines[^1].Contains("presses the lane key", StringComparison.Ordinal), Output(log));
            session.Submit("n");
            Equal(LastOutput(log)[^1], "Nothing was pressed.");
            session.Submit("inspect stop");
            Equal(LastOutput(log)[^1], "link inspect is not available here.");
            // -inspect is no option any more (user rule).
            session.Submit("link -inspect start");
            Assert(Output(log).StartsWith("-inspect is not an option of link.", StringComparison.Ordinal), Output(log));

            // -flow: the console belongs to the run, no prompt and nothing typed taken, until the window says it ended.
            host.FlowRuns = true;
            session.Submit("inspect -f -proceed start");
            Assert(session.Busy && host.FlowEnded is not null && session.JobText().StartsWith("Inspect", StringComparison.Ordinal), session.JobText());
            var lines = log.Lines.Count;
            session.Submit("version");
            Equal(log.Lines.Count, lines);
            host.FlowProgress!.Report(0.5);
            Assert(session.JobText().Contains(" 50%", StringComparison.Ordinal), session.JobText());
            host.FlowEnded!();
            Assert(!session.Busy && session.Prompt == CmdSession.PromptText, "The prompt is back when the run ends");
            // A run that does not start gives the prompt straight back.
            host.FlowRuns = false;
            session.Submit("inspect -flow -proceed start");
            Assert(!session.Busy, "Refused: no bar");

            // A normal run already owns the measurement; asking for -flow must not leave a job without its callback.
            host.FlowRuns = true;
            session.Submit("inspect -proceed start");
            Assert(host.Inspecting && !session.Busy, "The normal run starts without owning the console");
            session.Submit("inspect -flow -proceed start");
            Assert(!session.Busy && LastOutput(log)[^1] == "Already inspecting.", "A duplicate start returns the prompt");
            host.FlowEnded!();
        });

        yield return ("inspect -flow hides the terminal prompt and discards pasted text until it ends", () =>
        {
            var host = new Host { FlowRuns = true };
            var session = new CmdSession(host, new CmdLog());
            var window = new CmdWindow(session, "space") { Left = -20000, Top = -20000, ShowActivated = false };
            try
            {
                window.Show();
                window.Run("inspect -flow -proceed start");
                Assert(session.Busy && window.OutputText.TrimEnd().EndsWith("0%", StringComparison.Ordinal), "The bar replaces the prompt");
                var screen = FindAll<TextBox>(window).First(box => box.Text == window.OutputText);
                screen.AppendText("version");
                Equal(window.InputText, "");
                Assert(window.OutputText.TrimEnd().EndsWith("0%", StringComparison.Ordinal), "Pasted input is discarded immediately");
                host.FlowEnded!();
                Assert(!session.Busy && window.OutputText.TrimEnd().EndsWith(CmdSession.PromptText, StringComparison.Ordinal),
                    "Ending inspect restores the prompt");
            }
            finally { window.Close(); }
        });

        yield return ("report create starts a note and write opens one, each refusing what the other is for", () =>
        {
            var host = new Host();
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            ReportDraft? opened = null;
            session.ReportRequested += draft => opened = draft;

            // write needs a note that is there; create needs one that is not (user rule).
            session.Submit("report write magic circle");
            Assert(opened is null && Output(log).Contains("There is no note called \"magic circle\"", StringComparison.Ordinal), Output(log));
            session.Submit("report create magic circle");
            Assert(opened is { Title: "magic circle", Lines.Count: 0 }, "create opens it at once: " + Output(log));
            host.Reports["magic circle"] = ["two lanes"];

            opened = null;
            session.Submit("report create magic circle");
            Assert(opened is null && Output(log).Contains("is already there", StringComparison.Ordinal)
                && Output(log).Contains("report write magic circle", StringComparison.Ordinal), Output(log));
            session.Submit("report write magic circle");
            Assert(opened is { Lines.Count: 1 } && opened.Lines[0] == "two lanes", "write opens what is there");

            // -force replaces, after asking; a no leaves the note alone.
            opened = null;
            session.Submit("report create -force magic circle");
            Assert(session.IsAsking && log.Lines[^1].Contains("Replace \"magic circle\"", StringComparison.Ordinal), Output(log));
            session.Submit("n");
            Assert(opened is null && LastOutput(log)[^1] == "Nothing was replaced." && host.Reports["magic circle"].Count == 1, Output(log));
            // -force always asks (user rule): -proceed cannot answer for it, on either side of the word.
            session.Submit("report create -force -p magic circle");
            Assert(opened is null && LastOutput(log)[^1] == "-force always asks first, so -proceed cannot answer for it.", Output(log));
            session.Submit("report -proceed -force create magic circle");
            Assert(opened is null && LastOutput(log)[^1] == "-force always asks first, so -proceed cannot answer for it.", Output(log));
            session.Submit("report create -force magic circle");
            session.Submit("y");
            Assert(opened is { Lines.Count: 0 }, "The editor opens empty, and saving is what replaces it");

            // A title needs saying, and a note really called create is still reachable in quotes.
            session.Submit("report create");
            Assert(Output(log).Contains("needs a title", StringComparison.Ordinal), Output(log));
            opened = null;
            session.Submit("report create \"create\"");
            Assert(opened is { Title: "create" }, "Quotes keep the word as a title: " + Output(log));
        });

        yield return ("report's words say the same as its options, and checkpoint lists with list", () =>
        {
            var host = new Host();
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            host.Reports["magic circle"] = ["two lanes"];

            // Each word does what the option of that name does (user rule: two ways to say a thing is fine).
            session.Submit("report print magic circle");
            Lines(LastOutput(log), "two lanes");
            session.Submit("report done magic circle");
            Assert(Output(log).Contains("is done in", StringComparison.Ordinal), Output(log));
            session.Submit("report state magic circle");
            Equal(Output(log), "done " + AppInfo.Version);
            session.Submit("report undone magic circle");
            Assert(Output(log).Contains("is open again", StringComparison.Ordinal), Output(log));
            session.Submit("report ask magic circle > 어느 곡인지 알려 주세요");
            Assert(Output(log).Contains("waiting on an answer", StringComparison.Ordinal), Output(log));
            session.Submit("report colon");
            Assert(LastOutput(log).Count > 3 && LastOutput(log)[0].TrimStart().StartsWith(':'), Output(log));

            // checkpoint lists with list, as every other command does; all is not one of its words.
            host.LoadedSongName = "Plum - R";
            host.Timeline = new ParsedTimeline([0, 500, 1000],
                [new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 1, 500, 40)], true);
            session.Submit("checkpoint list");
            var listed = LastOutput(log);
            Assert(listed.Count == 2 && listed[0].Contains("the beginning", StringComparison.Ordinal), string.Join(" | ", listed));
            session.Submit("checkpoint all");
            Assert(LastOutput(log)[0].StartsWith("'all' is not a checkpoint", StringComparison.Ordinal), Output(log));
        });

        yield return ("A note says whether it is done, keeps saying so, and writing in it opens it again", () =>
        {
            // User rule: these notes are what an agent reads when the user could not reach one, so a note is kept until
            // someone removes it and whether it was taken care of is kept beside it, out of reach of the writing.
            var host = new Host();
            host.Reports["magic circle"] = ["the underline shows with no change"];
            var log = new CmdLog();
            var session = new CmdSession(host, log);

            session.Submit("report state magic circle");
            Equal(Output(log), "open");
            session.Submit("report done magic circle");
            Assert(Output(log).Contains("done", StringComparison.Ordinal), Output(log));
            session.Submit("report state magic circle");
            Equal(Output(log), "done " + AppInfo.Version);
            // The mark is not in the note, so the words are exactly what was written.
            Lines(host.Reports["magic circle"], "the underline shows with no change");

            // Writing it again is new work for the next version, so the mark goes by itself (user rule).
            Equal(session.SaveReport("magic circle", ["and again after the fix"]), null);
            session.Submit("report state magic circle");
            Equal(Output(log), "open");
            session.Submit("report done magic circle");
            session.Submit("report undone magic circle");
            session.Submit("report state magic circle");
            Equal(Output(log), "open");

            // A third word for the case in between: looked at, not settled, and the reason says what is needed. It is
            // written in the language the settings are set to, since the user is who reads it (user rule).
            session.Submit("report ask magic circle > 어느 곡·슬롯인지 알려 주셔야 재현됩니다.");
            session.Submit("report state magic circle");
            Equal(Output(log), "asked: 어느 곡·슬롯인지 알려 주셔야 재현됩니다.");
            session.Submit("report list");
            var waiting = LastOutput(log);
            Assert(waiting.Any(row => row.Contains("asked", StringComparison.Ordinal))
                && waiting.Any(row => row.Contains("재현됩니다", StringComparison.Ordinal)), string.Join(" | ", waiting));
            // It is not done, so -clear leaves it alone.
            Assert(waiting[^1].Contains("1 of 1 still open", StringComparison.Ordinal), waiting[^1]);
            session.Submit("report ask magic circle");
            Assert(Output(log).Contains("report ask <title> > <reason>", StringComparison.Ordinal), "It says how: " + Output(log));

            // -mv splits the two names on ">", since a title may hold spaces but never that.
            session.Submit("report done magic circle");
            session.Submit("report rename magic circle > magic line");
            Assert(session.IsAsking, "It asks first");
            session.Submit("y");
            Assert(host.Reports.ContainsKey("magic line") && !host.Reports.ContainsKey("magic circle"), "Renamed");
            session.Submit("report state magic line");
            Equal(Output(log), "done " + AppInfo.Version);
            session.Submit("report rename magic line");
            Assert(Output(log).Contains("report rename", StringComparison.Ordinal), "It says how: " + Output(log));

            // -clear takes away the done ones and leaves the rest, after asking.
            host.Reports["still open"] = ["not yet"];
            session.Submit("report sweep");
            Assert(session.IsAsking && LastOutput(log)[^1].Contains("magic line", StringComparison.Ordinal), Output(log));
            session.Submit("n");
            Assert(host.Reports.Count == 2, "n keeps them");
            session.Submit("report sweep -p");
            Assert(host.Reports.Count == 1 && host.Reports.ContainsKey("still open"), "Only the done one went");
        });

        yield return ("report print prints a note without opening it, and erase removes it after asking", () =>
        {
            // User request: reading and removing a note from the console line itself, since opening the editor for either
            // is more than the job needs.
            var host = new Host();
            host.Reports["lane idea"] = ["chords on 3 and 4", "and 5 and 6"];
            var log = new CmdLog();
            var session = new CmdSession(host, log);

            session.Submit("report print lane idea");
            Lines(LastOutput(log), "chords on 3 and 4", "and 5 and 6");
            Assert(host.Reports.Count == 1, "Reading does not touch it");
            session.Submit("report print nothing here");
            Assert(Output(log).Contains("never written", StringComparison.Ordinal), Output(log));

            // -cat -c copies it as well, the same as the editor's :copy.
            session.Submit("report print -c lane idea");
            Assert(host.Copied.Contains("chords on 3 and 4", StringComparison.Ordinal), host.Copied);

            // -rm asks first, and n keeps it.
            session.Submit("report erase lane idea");
            Assert(session.IsAsking && LastOutput(log)[^1].Contains("Delete \"lane idea\" (2 lines)?", StringComparison.Ordinal), Output(log));
            session.Submit("n");
            Assert(host.Reports.Count == 1 && Output(log).Contains("Nothing was deleted.", StringComparison.Ordinal), Output(log));
            session.Submit("report erase lane idea");
            session.Submit("y");
            Assert(host.Reports.Count == 0 && Output(log).Contains("deleted", StringComparison.Ordinal), Output(log));

            // -p answers for itself, which is what it means everywhere else.
            host.Reports["lane idea"] = ["again"];
            session.Submit("report erase -p lane idea");
            Assert(host.Reports.Count == 0 && !session.IsAsking, "-p goes ahead without asking");
        });

        yield return ("copy takes the messages back up the screen, and its options pick what goes on the clipboard", () =>
        {
            // User request: copy <count>, where the count goes back up the screen, * is all of it and -1 the first message.
            var host = Library();
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            session.Submit("copy");
            Equal(Output(log), "There is nothing to copy yet.");

            session.Submit("version");
            var version = Output(log);
            session.Submit("copy");
            Assert(host.Copied == version && Output(log) == "Copied 1 message.", $"The message above: {host.Copied}");
            // The typed line comes too with -vs, and alone with -vo.
            session.Submit("copy -vs 2");
            Assert(host.Copied.Contains("Visualizer> version", StringComparison.Ordinal) && host.Copied.Contains(version, StringComparison.Ordinal),
                $"-vs keeps the command: {host.Copied}");
            session.Submit("copy -vo 2");
            Assert(host.Copied.Split(Environment.NewLine).All(line => line.StartsWith("Visualizer>", StringComparison.Ordinal)),
                $"-vo copies only the commands: {host.Copied}");
            // A count past the end is simply the end.
            session.Submit("copy 999");
            Assert(host.Copied.Length > 0 && Output(log).StartsWith("Copied ", StringComparison.Ordinal), "Too far back is the whole screen");
            // -en leaves the copying itself out of the counting, so 1 is the message above the last copy.
            session.Submit("id");
            var ids = Output(log);
            session.Submit("copy");
            session.Submit("copy -en 1");
            Equal(host.Copied, ids);
        });

        yield return ("The stored prompts, renaming the loaded song and the JSON note print", () =>
        {
            var host = Library();
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            // prompt <title>: text to fill in and copy, nothing sent anywhere.
            session.Submit("prompt");
            Assert(Output(log).StartsWith("Prompts: note-lanes", StringComparison.Ordinal), "The titles are listed");
            session.Submit("prompt nothing");
            Assert(Output(log).StartsWith("There is no prompt called nothing.", StringComparison.Ordinal), "An unknown title says so");

            // rename: both names, then a question. An edit keeps its slot in front.
            host.LoadedId = "10000002";
            session.Submit("retitle Plum remix");
            Assert(session.IsAsking && log.Lines[^1].Contains("\"Plum - R\" to \"Plum remix\"", StringComparison.Ordinal), log.Lines[^1]);
            session.Submit("y");
            Assert(host.Calls[^1] == "retitle 10000002" && host.SongList.Single(song => song.Id == "10000002").Title == "Plum remix", "Renamed");
            host.LoadedId = "20000004";
            session.Submit("retitle -p 2*Kept slot");
            Equal(host.SongList.Single(song => song.Id == "20000004").Title, "2*Kept slot");
            session.Submit("retitle ...");
            Equal(Output(log), "A name needs a letter or a digit in it.");

            // note -json prints what override reads back.
            var times = new[] { 0d, 600, 1200, 1215 };
            var notes = new Host { Timeline = new ParsedTimeline(times) };
            var jsonLog = new CmdLog();
            var jsonSession = new CmdSession(notes, jsonLog);
            jsonSession.Submit("note -json all");
            var json = string.Join("\n", LastOutput(jsonLog));
            Assert(json.StartsWith("{", StringComparison.Ordinal) && json.Contains("\"n\": 1", StringComparison.Ordinal)
                && json.Contains("\"tile\":", StringComparison.Ordinal) && json.Contains("\"chord\":", StringComparison.Ordinal),
                "The JSON has the numbers, the lanes and the chord: " + json);
            // -1 is the last note, as a range end or on its own.
            jsonSession.Submit("note -1");
            Equal(LastOutput(jsonLog).Count, 1);
            jsonSession.Submit("note 1..-1");
            Assert(LastOutput(jsonLog).Count >= 3, "A range can end at the last note");
        });

        yield return ("note reads a song by id as it plays, an edit with its own lanes, and the editor's work while it is open", () =>
        {
            // User report: slot 2 printed the original's lanes; note -json has to work in the middle of an edit too.
            var host = Library();
            host.SongList.Add(Song("2abcdefg", "3*Plum - R", "edited", "1234/main.adofai", slot: 3));
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            // Tile 7 is off a four-lane screen: an edit is printed as it stands.
            host.Edits["20000004"] = new NoteEdit("", new Dictionary<int, int> { [0] = 2, [1] = 7, [2] = 3 }, []);
            session.Submit("note -json 20000004 *");
            var edited = LastOutput(log);
            Assert(edited.Contains("  { \"n\": 1, \"tile\": 2, \"ms\": 0 },") && edited.Contains("  { \"n\": 2, \"tile\": 7, \"ms\": 60000 },")
                && edited.Contains("  { \"n\": 3, \"tile\": 3, \"ms\": 125500 }"), "The edit's own lanes: " + string.Join(" | ", edited));
            session.Submit("note -json 10000002 *");
            Assert(!LastOutput(log).Contains("  { \"n\": 2, \"tile\": 7, \"ms\": 60000 },"), "The original keeps the rules' lanes");
            // Options stand on either side, and the copy is what was shown.
            session.Submit("note 20000004 all -json -copy");
            Equal(host.Copied, string.Join(Environment.NewLine, edited));

            // now is the default: with nothing loaded and nothing to load, it says how to name a song.
            session.Submit("note all");
            Assert(Output(log).StartsWith("No song is loaded. Name one, as in note <id> all", StringComparison.Ordinal), Output(log));
            session.Submit("note 2abcdefg");
            Equal(Output(log), "A range is needed after the song, as in note 2abcdefg all.");
            session.Submit("note 2zzzzzzz all");
            Equal(Output(log), "No song has the id 2zzzzzzz. playlist shows the ids.");

            // The loaded song reads as it plays, and while the editor is open now is the editor's work, saved or not.
            host.Timeline = new ParsedTimeline(new[] { 0d, 60_000, 125_500 });
            session.Submit("note -json now all");
            var loaded = LastOutput(log);
            host.EditorOpen = true;
            host.Editing = new CmdNoteSource(host.Timeline, new NoteEdit("", new Dictionary<int, int> { [0] = 8, [1] = 1, [2] = 4 }, []));
            session.Submit("note -json all");
            var editing = LastOutput(log);
            Assert(!editing.SequenceEqual(loaded) && editing.Contains("  { \"n\": 1, \"tile\": 8, \"ms\": 0 },")
                && editing.Contains("  { \"n\": 3, \"tile\": 4, \"ms\": 125500 }"), "The lanes being edited: " + string.Join(" | ", editing));
            session.Submit("note now 1");
            Assert(Output(log).StartsWith("n1 8", StringComparison.Ordinal), "now names the same: " + Output(log));
        });

        yield return ("Commands that would work behind the note editor are refused while it is open", () =>
        {
            // User request: the terminal stays usable while the editor is open, but the commands that would move the
            // ground under it say so instead of running.
            var host = Library();
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            host.EditorOpen = true;
            var travel = host.Settings.NoteTravelTimeMs;
            session.Submit("set display.travel 800");
            Equal(Output(log), "The note editor is open, so set cannot run. Save or close the editor first.");
            Equal(host.Settings.NoteTravelTimeMs, travel);
            session.Submit("load 10000002");
            Equal(Output(log), "The note editor is open, so load cannot run. Save or close the editor first.");
            Assert(host.LoadedId is null && !session.IsAsking, "Nothing was loaded and nothing is being asked");
            session.Submit("sync -p rebuild");
            Equal(Output(log), "The note editor is open, so sync cannot run. Save or close the editor first.");
            // retitle is the command once called rename: the guard follows the name it has now.
            session.Submit("retitle -p Another name");
            Equal(Output(log), "The note editor is open, so retitle cannot run. Save or close the editor first.");
            Assert(!host.Calls.Any(call => call.StartsWith("retitle", StringComparison.Ordinal)), "Nothing was renamed");

            // Reading the app is fine, and so is edit itself: it brings the open window forward.
            session.Submit("get display.travel");
            Equal(Output(log), travel.ToString("0.###", CultureInfo.InvariantCulture));
            session.Submit("playlist");
            Assert(Output(log).Contains("Plum - R", StringComparison.Ordinal), "The song list still prints");

            // With the editor closed they run as before.
            host.EditorOpen = false;
            session.Submit("set display.travel 800");
            Equal(host.Settings.NoteTravelTimeMs, 800d);
        });

        yield return ("Options and keywords keep to their rules, so a new command cannot break them", () =>
        {
            // The rules are written in docs/console.md; this group is what makes them stick.
            foreach (var option in CmdCatalog.Options)
            {
                // 1. A long name, so reading it tells what it does. Two letters was the old way.
                // A word, not an abbreviation: id is a word, tl and sm are not (the short spellings carry those).
                Assert(option.Name.StartsWith('-') && option.Name.Length >= 3, $"{option.Name} is too short to read as a word");
                Assert(option.Name.All(c => c == '-' || char.IsAsciiLetterLower(c)), $"{option.Name} takes letters only");
                // 2. No word separators in a name: a name with parts is one name and a value (user rule, -speed:keep).
                Assert(!option.Name.TrimStart('-').Contains('-', StringComparison.Ordinal), $"{option.Name} should be a name and a value");
                // 3. An option that takes a value always has a default, so the name alone means something (user rule).
                Assert(!option.TakesValue || option.Default.Length > 0 || option.Values == CmdCatalog.NumberMark,
                    $"{option.Name} takes a value but has no default");
                // A count with words beside it (-runs:3, -runs:*) defaults to a count.
                Assert(!option.TakesValue || option.Values == CmdCatalog.NumberMark || option.ValueNames.Contains(option.Default, StringComparer.Ordinal)
                    || (option.ValueNames.Contains(CmdCatalog.NumberMark, StringComparer.Ordinal) && int.TryParse(option.Default, out var count) && count >= 1),
                    $"{option.Name}'s default is not one of its values"); 
                // 4. It is explained, and the short spelling is only a spelling.
                Assert(option.Help.Length >= 8 && option.Help.EndsWith('.'), $"{option.Name} is not explained");
                Assert(option.Short.Length == 0 || option.Short.StartsWith('-'), $"{option.Name}'s short spelling");
                // 5. Every option someone types resolves to that option.
                Assert(CmdCatalog.Option(option.Name)?.Option == option, $"{option.Name} does not read back");
                if (option.Short.Length > 0) Assert(CmdCatalog.Option(option.Short)?.Option == option, $"{option.Short} does not read back");
            }
            // 6. One name, one meaning: no two options share a name or a short spelling.
            var names = CmdCatalog.Options.Select(option => option.Name).ToArray();
            Assert(names.Distinct(StringComparer.Ordinal).Count() == names.Length, "Two options share a name");
            var shorts = CmdCatalog.Options.Where(option => option.Short.Length > 0).Select(option => option.Short).ToArray();
            Assert(shorts.Distinct(StringComparer.Ordinal).Count() == shorts.Length, "Two options share a short spelling");

            foreach (var command in CmdCatalog.Visible)
            {
                // 7. A command may only name options that are in the table, by their long name.
                foreach (var name in command.OptionNames)
                    Assert(CmdCatalog.Options.Any(option => option.Name == name), $"{command.Name} names {name}, which is not an option");
                // 8. Keywords are words, not options, and every one of them is explained.
                foreach (var keyword in command.KeywordNames)
                {
                    Assert(!keyword.StartsWith('-'), $"{command.Name}'s keyword {keyword} looks like an option");
                    Assert(CmdCatalog.KeywordMeaning(command, keyword).Length > 0, $"{command.Name} {keyword} has no meaning");
                }
            }
            // 9. A command and a keyword never share a word (user rule), which is what song is named for.
            Assert(CmdCatalog.NameClash() is null, $"A command and a keyword share the name '{CmdCatalog.NameClash()}'");

            // 10. One default keyword at most, and it is one of the command's own words; help shows it in brackets.
            foreach (var command in CmdCatalog.Visible)
            {
                Assert(command.Keywords.Count(mark => mark == '*') <= 1, $"{command.Name} marks more than one default keyword");
                Assert(command.DefaultKeyword is null || command.KeywordNames.Contains(command.DefaultKeyword, StringComparer.Ordinal),
                    $"{command.Name}'s default keyword is not one of its words");
                if (command.DefaultKeyword is { } standsFor)
                    Assert(command.KeywordText(standsFor) == $"[{standsFor}]" && command.KeywordNames.Where(word => word != standsFor)
                        .All(word => command.KeywordText(word) == word), $"{command.Name} does not show its default in brackets");
            }
            // 11. Where a word has to be typed, options alone may not run the command (user rule). What would let them
            // is an option spelled like one of its own words, so there is none. A word has to be typed when the usage
            // asks for one without brackets: report <what>, do <action> or the word itself, as in install linker.
            var mandatory = CmdCatalog.Visible.Where(command =>
            {
                if (command.KeywordNames.Count == 0 || command.DefaultKeyword is not null) return false;
                var rest = command.Usage[command.Name.Length..].TrimStart();
                var asked = System.Text.RegularExpressions.Regex.Match(rest, "^<?([a-z]+)").Groups[1].Value;
                return asked is "what" or "action" || command.KeywordNames.Contains(asked, StringComparer.Ordinal);
            }).ToArray();
            Assert(new[] { "report", "install", "do" }.All(name => mandatory.Any(command => command.Name == name)),
                "report, install and do need their word: " + string.Join(", ", mandatory.Select(command => command.Name)));

            // 14. A list is the last thing a command takes (user rule): nothing may follow it, so its usage ends with it.
            foreach (var command in CmdCatalog.Visible.Where(command => command.Variadic))
                Assert(command.Usage.EndsWith("…]", StringComparison.Ordinal) || command.Usage.EndsWith("…", StringComparison.Ordinal),
                    $"{command.Name}: something follows its list in {command.Usage}");
            Assert(CmdCatalog.Visible.Count(command => command.Variadic) >= 2, "scannow and install take lists");
            foreach (var command in mandatory)
                foreach (var keyword in command.KeywordNames)
                    Assert(!command.OptionNames.Contains("-" + keyword, StringComparer.Ordinal),
                        $"{command.Name} -{keyword} would do what the word {keyword} does, so the word is not needed");

            // 13. -force always asks (user rule): a command that has both keeps them in one group, so they cannot meet.
            foreach (var command in CmdCatalog.Visible.Where(command => command.OptionNames.Contains("-force")
                && command.OptionNames.Contains("-proceed")))
                Assert(command.OptionGroups.Any(group => group.Contains("-force") && group.Contains("-proceed")),
                    $"{command.Name} lets -proceed answer for -force");

            // 12. Help names an option by its word, never by a short spelling: those only keep old typing working.
            var texts = CmdCatalog.Visible.SelectMany(command => new[] { command.Summary, command.Help }
                    .Concat(command.KeywordNames.Select(keyword => CmdCatalog.KeywordMeaning(command, keyword))))
                .Concat(CmdCatalog.Options.Select(option => option.Help));
            foreach (var spelling in CmdCatalog.ShortSpellings)
            {
                var word = new System.Text.RegularExpressions.Regex($@"(?<![\w-]){System.Text.RegularExpressions.Regex.Escape(spelling)}(?![\w:=])");
                var found = texts.FirstOrDefault(text => word.IsMatch(text));
                Assert(found is null, $"Help writes {spelling} where it means {CmdCatalog.Option(spelling)?.Option.Name}: {found}");
            }

            // And it refuses to run on options alone, naming the words it takes instead.
            var log = new CmdLog();
            var session = new CmdSession(new Host(), log);
            session.Submit("report -copy");
            Assert(LastOutput(log)[0].StartsWith("report needs one of:", StringComparison.Ordinal)
                && LastOutput(log)[0].Contains("create", StringComparison.Ordinal), Output(log));
        });

        yield return ("update says when this is the latest, asks before taking a newer one, and -force goes anywhere after asking", () =>
        {
            var host = new Host();
            var log = new CmdLog();
            var session = new CmdSession(host, log);

            // On the latest version it says so and asks nothing (user rule).
            session.Submit("update");
            Lines(host.Calls, "update");
            Assert(!session.IsAsking && Output(log) == "This copy is already the latest version (1.0.0).", Output(log));
            Equal(host.Applied, 0);

            // Nothing newer on the repository, but the app running is an older build of this copy (user report: update said
            // the latest while the old build stayed): update says so and offers to build it again, as install -reinstall app does.
            host.Stale = "this build is from 2026-10-13 12:49, and 12 file(s) of this copy changed after it";
            session.Submit("update");
            Assert(session.IsAsking && log.Lines.Contains("This copy is the latest version (1.0.0), but the app running is an older build of it: "
                + host.Stale + ".") && log.Lines[^1].StartsWith("Build it again?", StringComparison.Ordinal), string.Join(" | ", LastOutput(log)));
            session.Submit("n");
            Assert(LastOutput(log)[^1] == "Cancelled. This copy stays as it is." && host.Reinstalled.Count == 0, "A no builds nothing");
            session.Submit("version -check");
            Equal(LastOutput(log)[^1], "Latest  yes, but the app running is an older build of it; update builds it again");
            // Its own changes go into the build too, and an installed linker older than this copy's goes with it.
            host.Check = new("1.0.0", "1.0.0", true, 0, true, 3, 0, []);
            host.Place = new(@"C:\Game", @"C:\copy\plugins\linker", "1.0.5", "1.0.4", @"C:\Game\Mods\AdofaiVisualizerSync");
            session.Submit("update -p");
            Assert(log.Lines.Any(line => line.Contains("This copy's own 3 changed file(s) go into it too.", StringComparison.Ordinal)
                && line.Contains("The linker in the game is updated with it.", StringComparison.Ordinal)), string.Join(" | ", LastOutput(log)));
            Assert(host.Reinstalled is ["app,linker"] && LastOutput(log)[^1] == "Closing to build it again. The app comes back when it is done.",
                string.Join(" | ", host.Reinstalled));
            host.Reinstalled.Clear();
            host.Stale = null;
            host.Place = new(null, null, null, null, "");
            host.Check = new("1.0.0", "1.0.0", true, 0, true, 0, 0, []);

            // A newer one: what it is, then the question, which says the app closes and opens again; a no changes nothing.
            host.Check = new("1.0.0", "1.0.1", true, 2, false, 0, 0, []);
            session.Submit("update");
            Assert(session.IsAsking && log.Lines.Contains("Version 1.0.1 is waiting; this copy is 1.0.0.")
                && log.Lines[^1].StartsWith("Update to 1.0.1? The app closes and opens again on the new version.", StringComparison.Ordinal), Output(log));
            session.Submit("n");
            Assert(host.Applied == 0 && LastOutput(log)[^1].Contains("stays as it is", StringComparison.Ordinal), Output(log));
            session.Submit("update");
            session.Submit("y");
            Assert(host.Updates.Single() == (false, null) && LastOutput(log)[^1].Contains("Closing to update", StringComparison.Ordinal), Output(log));
            // -proceed answers for itself, and a reason from the app is printed as it is.
            host.UpdateError = "The update could not start. Access is denied";
            session.Submit("update -p");
            Assert(host.Applied == 2 && LastOutput(log)[^1] == host.UpdateError, Output(log));
            host.UpdateError = null;
            // An installed linker goes along (user rule), and the question says so.
            host.Place = host.Place with { Installed = "1.0.0" };
            session.Submit("update");
            Assert(log.Lines[^1].Contains("The linker in the game is updated with it.", StringComparison.Ordinal), log.Lines[^1]);
            session.Submit("n");
            host.Place = host.Place with { Installed = null };

            // This copy's own changes stand in the way: refused, naming -force.
            host.Check = host.Check with { Changed = 3, Unpublished = 1 };
            session.Submit("update");
            Assert(!session.IsAsking && Output(log).Contains("this copy has its own 3 changed file(s) and 1 commit(s) not on the repository, so it was not updated", StringComparison.Ordinal)
                && Output(log).Contains("update -force takes it", StringComparison.Ordinal), Output(log));
            // -force always asks, and -proceed cannot answer for it (user rule).
            session.Submit("update -force -p");
            Equal(Output(log), "-force always asks first, so -proceed cannot answer for it.");
            session.Submit("update -force");
            Assert(session.IsAsking && log.Lines[^1].StartsWith("Go to 1.0.1 by force? The risk is yours", StringComparison.Ordinal)
                && log.Lines[^1].Contains("3 changed file(s) and 1 commit(s) not on the repository are thrown away", StringComparison.Ordinal), log.Lines[^1]);
            session.Submit("y");
            Assert(host.Updates[^1] == (true, null), "The latest, by force");

            // A chosen version needs -force; -v1.0.0 is read the way -sl3 is.
            session.Submit("update -version:1.0.0");
            Equal(Output(log), "Going to a chosen version needs -force, as in update -force -version:1.0.0.");
            host.Check = new("1.0.1", "1.0.0", true, 0, false, 0, 0, []);
            session.Submit("update -force -v1.0.0");
            Equal(host.CheckedVersion, "1.0.0");
            Assert(session.IsAsking && log.Lines[^1].Contains("an older version may not read what a newer one saved", StringComparison.Ordinal), log.Lines[^1]);
            session.Submit("y");
            Assert(host.Updates[^1] == (true, "1.0.0") && LastOutput(log)[^1] == "Closing to go to 1.0.0. The app comes back on it.", Output(log));
            // A version the repository does not have is only refused; no list is offered (user rule).
            host.Check = new("1.0.1", "1.0.7", false, 0, false, 0, 0, []);
            session.Submit("update -force -version:1.0.7");
            Equal(Output(log), "There is no version 1.0.7 on the repository.");
            session.Submit("update -force -version:1.2");
            Assert(Output(log).StartsWith("-version:1.2 is not a version.", StringComparison.Ordinal), Output(log));
            host.Check = new("1.0.0", "1.0.0", true, 0, true, 0, 0, []);
            session.Submit("update -force -version:1.0.0");
            Equal(Output(log), "This copy is already 1.0.0.");
            // The script's own words when it could not look are printed as they are.
            host.Check = UpdateCheck.Failed("The repository could not be reached. Check the network or the sign-in.");
            session.Submit("update");
            Equal(Output(log), "The repository could not be reached. Check the network or the sign-in.");

            // version -check says whether this is the latest (user request); version alone and -number ask nothing.
            host.Check = new("1.0.0", "1.0.0", true, 0, true, 0, 0, []);
            session.Submit("version -check");
            Lines(LastOutput(log), $"Visualizer {AppInfo.Version}", $"Applied {AppInfo.VersionDate}", $"Built   {AppInfo.BuildTime}",
                "Latest  yes, this is the latest version");
            host.Check = new("1.0.0", "1.0.1", true, 2, false, 0, 0, []);
            session.Submit("version -check");
            Equal(LastOutput(log)[^1], "Latest  no, 1.0.1 is waiting; update takes it");
            var asked = host.Calls.Count;
            session.Submit("version -number");
            Assert(host.Calls.Count == asked && Output(log) == AppInfo.Version, "-number asks nothing");
            session.Submit("version");
            Assert(host.Calls.Count == asked && Output(log) == $"Visualizer {AppInfo.Version}", "version alone asks nothing (user rule)");

            // The script's lines are read into what it found; any other line is a problem to show.
            var read = UpdateCheck.Read(["state mine 1.0.0 abc1234", "state target 1.0.2 def5678", "state behind 4", "state same 0",
                "state changed 2", "state unpublished 0"]);
            Assert(read is { Mine: "1.0.0", Target: "1.0.2", Found: true, Behind: 4, Same: false, Changed: 2, Unpublished: 0, Problems.Count: 0 }, read.ToString());
            Assert(UpdateCheck.Read(["state mine 1.0.0 abc", "state missing 1.0.7"]) is { Found: false, Target: "1.0.7" }, "Missing");
            Assert(UpdateCheck.Read(["git was not found on this computer."]).Problems.Single() == "git was not found on this computer.", "Problems");

            // While it looks, the bar stands in for the prompt and the console takes nothing.
            host.HoldJobs = true;
            session.Submit("update");
            Assert(session.Busy && session.JobText().StartsWith("Update", StringComparison.Ordinal), session.JobText());
            host.UploadProgress!.Report(0.4);
            Assert(session.JobText().Contains(" 40%", StringComparison.Ordinal), session.JobText());
            var before = log.Lines.Count;
            session.Submit("version");
            Equal(log.Lines.Count, before);
            host.UpdateDone!(new("1.0.0", "1.0.0", true, 0, true, 0, 0, []));
            Assert(!session.Busy && !session.IsAsking, "The job is over and nothing is asked");
            host.HoldJobs = false;
        });

        yield return ("scannow says what is wrong and what puts it right, and install -reinstall puts parts back after asking", () =>
        {
            var host = new Host();
            var log = new CmdLog();
            var session = new CmdSession(host, log);

            // Every area by default, in its own order; whole, it says so.
            session.Submit("scannow");
            Equal(host.Calls[^1], "scan app,source,linker,maps,edits,data");
            Assert(LastOutput(log).Contains("app     ok") && LastOutput(log)[^1] == "Everything checked is whole.", Output(log));
            // A linker that is not installed is a fact in the list, and named alone it is refused (user rule).
            host.Scans["linker"] = new ScanArea("linker", [], NotInstalled: true);
            session.Submit("scannow");
            Assert(LastOutput(log).Contains("linker  not installed; install linker puts it in"), Output(log));
            var asked = host.Calls.Count;
            session.Submit("scannow linker");
            Assert(host.Calls.Count == asked && Output(log) == "The linker is not installed, so there is nothing to check. Install it first: install linker.", Output(log));

            // A list: commas with or without spaces, any order, options on either side; nothing else may join it.
            session.Submit("scannow maps,app");
            Equal(host.Calls[^1], "scan app,maps");
            session.Submit("scannow -copy maps , app");
            Equal(host.Calls[^1], "scan app,maps");
            session.Submit("scannow maps, app -copy");
            Assert(host.Copied.Contains("maps    ok", StringComparison.Ordinal), host.Copied);
            session.Submit("scannow maps,");
            Equal(Output(log), "A place in the list is empty. Write it as scannow maps, linker.");
            session.Submit("scannow maps app");
            Equal(Output(log), "'maps app' needs a comma between its words, as in scannow maps, linker.");
            session.Submit("scannow all, maps");
            Equal(Output(log), "all stands alone, since it already names every area.");
            session.Submit("scannow disk");
            Equal(Output(log), "'disk' is not an area. scannow takes all, app, source, linker, maps, edits, data.");
            session.Submit("scannow *");
            Equal(host.Calls[^1], "scan app,source,linker,maps,edits,data");

            // Problems come with what puts them right; your own areas are pointed elsewhere, never reinstalled.
            host.Scans["maps"] = new ScanArea("maps", ["Plum - R changed after it was synced"], "57 maps");
            host.Scans["edits"] = new ScanArea("edits", ["1234/2: edit.json cannot be read"]);
            session.Submit("scannow maps, edits");
            Lines(LastOutput(log), "maps    1 problem · 57 maps", "  Plum - R changed after it was synced", "edits   1 problem",
                "  1234/2: edit.json cannot be read", "Run install -reinstall maps to put it right.",
                "An edit is your own work, so it is not reinstalled: remove takes a broken one away.");

            // install: the linker goes in on its own; everything else is put back with -reinstall.
            session.Submit("install");
            Assert(Output(log).StartsWith("install needs one of: all, app, source, linker, maps, edits, data.", StringComparison.Ordinal), Output(log));
            session.Submit("install -reinstall");
            Assert(Output(log).StartsWith("install needs one of:", StringComparison.Ordinal), "Options alone do not run it");
            session.Submit("install app");
            Equal(Output(log), "Only the linker is installed on its own; the rest come with this copy. install -reinstall puts a part back.");
            session.Submit("install -reinstall edits");
            Lines(LastOutput(log), "Edits are your own work, so there is nothing to reinstall. remove takes a broken one away.", "Nothing was done.");
            session.Submit("install -reinstall maps, data");
            Lines(LastOutput(log), "Your data is your own, so there is nothing to reinstall. reset puts the settings back.", "Nothing was done.");
            session.Submit("install -reinstall linker");
            Equal(Output(log), "The linker is not installed. Install it first: install linker.");

            // maps are built again in the app, after a yes that says what it means.
            session.Submit("install -reinstall maps");
            Assert(session.IsAsking && log.Lines[^1].StartsWith("Reinstall maps? The custom maps are built again", StringComparison.Ordinal), log.Lines[^1]);
            session.Submit("y");
            Assert(host.Calls.Contains("resync") && LastOutput(log)[^1].StartsWith("The custom maps are built again.", StringComparison.Ordinal), Output(log));

            // The source always asks, after showing what goes back (user rule); the app closes and opens again.
            host.Scans["source"] = new ScanArea("source", ["changed src/a.cs", "extra   notes.txt"]);
            session.Submit("install -reinstall source -p");
            Equal(Output(log), "Putting the source back always asks first, since this copy's own changes go; -proceed cannot answer for it.");
            session.Submit("install -reinstall source, app");
            Assert(log.Lines.Contains("These go back to this version:") && log.Lines.Contains("  changed src/a.cs") && session.IsAsking
                && log.Lines[^1].StartsWith("Reinstall app, source? The app closes and opens again.", StringComparison.Ordinal), Output(log));
            session.Submit("y");
            Assert(host.Reinstalled[^1] == "app,source" && LastOutput(log)[^1] == "Closing to reinstall. The app comes back when it is done.", Output(log));

            // all is this version in full, with the linker when it is installed; the linker alone keeps the app open.
            host.Place = host.Place with { Installed = "1.0.0", GameDirectory = "G:" };
            session.Submit("install -reinstall *");
            Assert(log.Lines[^1].StartsWith("Reinstall app, source, linker?", StringComparison.Ordinal), log.Lines[^1]);
            session.Submit("y");
            Equal(host.Reinstalled[^1], "app,source,linker");
            session.Submit("install -p -reinstall linker");
            Assert(host.Reinstalled[^1] == "linker" && LastOutput(log)[^1].StartsWith("The linker is built", StringComparison.Ordinal), Output(log));
        });

        yield return ("checkpoint moves by name or number and go starts from a tile, with the loaded song's own limits", () =>
        {
            var host = new Host();
            var log = new CmdLog();
            var session = new CmdSession(host, log);

            // Both are listed with the songs and explained with their keywords, as cmd and help show them.
            session.Submit("cmd");
            var listed0 = LastOutput(log);
            Assert(listed0.Any(l => l.Contains("checkpoint", StringComparison.Ordinal) && l.Contains("Move to a checkpoint", StringComparison.Ordinal))
                && listed0.Any(l => l.Contains("go <tile>", StringComparison.Ordinal) && l.Contains("Start from a tile", StringComparison.Ordinal)),
                "cmd lists them");
            session.Submit("help checkpoint");
            var explained = LastOutput(log);
            Assert(explained.Any(l => l.Contains("[-copy]", StringComparison.Ordinal))
                && explained.Any(l => l.TrimStart().StartsWith("first", StringComparison.Ordinal))
                && explained.Any(l => l.TrimStart().StartsWith("last", StringComparison.Ordinal)),
                "help checkpoint: " + string.Join(" | ", explained));

            // Nothing loaded, and a song without checkpoints: both say so and ask the app for nothing.
            session.Submit("checkpoint 2");
            Assert(LastOutput(log)[0] == "No song is loaded.", Output(log));
            host.LoadedSongName = "Plum - R";
            host.Timeline = new ParsedTimeline([0, 500, 1000]);
            session.Submit("checkpoint");
            Assert(LastOutput(log)[0] == "Plum - R has no checkpoints.", Output(log));
            session.Submit("checkpoint 2");
            Assert(LastOutput(log)[0] == "Plum - R has no checkpoints.", Output(log));
            Assert(host.Moves.Count == 0, "Nothing was moved");

            // Three entries: the beginning and two checkpoints, which the console numbers 1 and 2 as the window labels them.
            host.Timeline = new ParsedTimeline([0, 500, 1000, 1500],
                [new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 1, 500, 40), new TimelineCheckpoint(3, 2, 1000, 90)], true);
            session.Submit("checkpoint");
            Assert(LastOutput(log)[0] == "Plum - R starts at the beginning, of 2.", Output(log));
            session.Submit("checkpoint 2");
            Sequence(host.Moves, 3); // Checkpoint 2 on screen is the third entry of the timeline.
            Assert(LastOutput(log)[0] == "Plum - R starts at checkpoint 2 (tile 90).", Output(log));
            session.Submit("checkpoint previous");
            Sequence(host.Moves, 3, 2);
            session.Submit("checkpoint first");
            Sequence(host.Moves, 3, 2, 1);
            session.Submit("checkpoint last");
            Sequence(host.Moves, 3, 2, 1, 3);
            // The ends wrap around, as the arrows on the main window do (user rule).
            session.Submit("checkpoint next");
            Sequence(host.Moves, 3, 2, 1, 3, 1);

            // A number outside the song, and a word that is neither, are refused without asking the app.
            var asked = host.Moves.Count;
            session.Submit("checkpoint 3");
            Assert(LastOutput(log)[0] == "Plum - R has 2 checkpoints.", Output(log));
            session.Submit("checkpoint 0");
            Assert(LastOutput(log)[0].StartsWith("'0' is not a checkpoint", StringComparison.Ordinal), Output(log));
            session.Submit("checkpoint half");
            Assert(LastOutput(log)[0].Contains("first, next, previous or last", StringComparison.Ordinal), Output(log));
            Equal(host.Moves.Count, asked);
            // The reason the app gives is printed as it is.
            host.MoveError = "The notes could not be prepared there; the main window says why.";
            session.Submit("checkpoint 1");
            Assert(LastOutput(log)[0] == host.MoveError, Output(log));
            host.MoveError = null;

            // list shows every checkpoint and marks the one in use.
            host.Checkpoint = 2;
            session.Submit("checkpoint list");
            var listed = LastOutput(log);
            Assert(listed.Count == 3 && listed[0].Contains("the beginning", StringComparison.Ordinal)
                && listed[1].StartsWith("> ", StringComparison.Ordinal) && listed[2].Contains("tile 90", StringComparison.Ordinal),
                string.Join(" | ", listed));

            // go takes whole tiles of 1 or more and hands them over; the app's own reason comes back.
            session.Submit("go 120");
            Sequence(host.Tiles, 120);
            Assert(LastOutput(log)[0] == "Starting from tile 120.", Output(log));
            session.Submit("go 0");
            session.Submit("go 2.5");
            session.Submit("go -3");
            session.Submit("go");
            Sequence(host.Tiles, 120); // None of those reached the app.
            Assert(LastOutput(log)[0].StartsWith("go takes a tile number", StringComparison.Ordinal), Output(log));
            host.TileError = "Tile 7 is a midspin tile, so starting there is not verified.";
            session.Submit("go 7");
            Assert(LastOutput(log)[0] == host.TileError, Output(log));

            // Both are refused while the note editor is open, as the other commands that prepare notes are.
            host.EditorOpen = true;
            session.Submit("checkpoint first");
            Assert(LastOutput(log)[0].Contains("note editor is open", StringComparison.Ordinal), Output(log));
            session.Submit("go 5");
            Assert(LastOutput(log)[0].Contains("note editor is open", StringComparison.Ordinal), Output(log));
            Sequence(host.Tiles, 120, 7);
        });

        yield return ("pin keeps tiles for go on a song and all its edits, in order; unpin takes them off and Tab offers them", () =>
        {
            // User request: a pin is a tile marked for go, shared by the original and every edit of it (the family), kept in
            // tile order and numbered as checkpoints are; go and unpin offer the pins on Tab. locate is gone: go took its place.
            var host = Library();
            var timeline = new ParsedTimeline(new[] { 0d, 500, 1000, 1500, 2000, 2500 }, null, false, new[] { 1, 2, 3, 4, 5, 6 });
            for (var i = 0; i < host.SongList.Count; i++)
                host.SongList[i] = host.SongList[i] with { Entry = host.SongList[i].Entry with { Timeline = timeline } };
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            Assert(CmdCatalog.Find("locate") is null && CmdCatalog.Find("go") is not null, "go replaced locate");

            session.Submit("pin 3");
            Equal(LastOutput(log)[0], "No song is loaded.");
            host.LoadedId = "20000004"; // the edit in slot 2 of Plum - R
            host.LoadedSongName = "2*Plum - R";
            session.Submit("pin 3");
            Equal(LastOutput(log)[0], "Pinned tile 3 of Plum - R and its edits: pin 1 of 1.");
            session.Submit("pin 10000002 1"); // on the original, by id
            session.Submit("pin 3");
            Equal(LastOutput(log)[0], "Tile 3 of Plum - R is already pinned.");
            // Shared by the family, in tile order; another song has none.
            session.Submit("pin 10000002 all");
            Lines(LastOutput(log), "pin 1 · tile 1", "pin 2 · tile 3");
            session.Submit("pin all");
            Lines(LastOutput(log), "pin 1 · tile 1", "pin 2 · tile 3");
            session.Submit("pin 00000001 all");
            Equal(LastOutput(log)[0], "Built in has no pins. pin <tile> adds one.");
            // Only tiles go can take.
            session.Submit("pin 6");
            Equal(LastOutput(log)[0], "Plum - R has tiles 1 to 5.");
            session.Submit("pin x");
            Equal(LastOutput(log)[0], "'x' is not a tile. A tile is a whole number of 1 or more.");
            session.Submit("pin 99999999 2");
            Equal(LastOutput(log)[0], "No song has the id 99999999. playlist shows the ids.");

            // Tab offers the pins after go and unpin, and the ids too where a song may be named.
            Lines(session.Complete("go ", out _), "1", "3");
            Assert(session.Complete("unpin ", out _).Take(3).SequenceEqual(["now", "1", "3"]), "unpin offers its pins first");
            Lines(session.Complete("unpin 10000002 ", out _), "1", "3");

            session.Submit("unpin 4");
            Equal(LastOutput(log)[0], "Tile 4 of Plum - R is not pinned. pin all lists the pins.");
            session.Submit("unpin 10000002 1");
            Equal(LastOutput(log)[0], "Unpinned tile 1 of Plum - R and its edits.");
            Sequence(host.PinsOf("1234/main.adofai"), 3);
            // Pins do not prepare notes, so they work while the note editor is open; go does not.
            host.EditorOpen = true;
            session.Submit("pin 5");
            Equal(LastOutput(log)[0], "Pinned tile 5 of Plum - R and its edits: pin 2 of 2.");
            session.Submit("go 5");
            Assert(LastOutput(log)[0].Contains("note editor is open", StringComparison.Ordinal), Output(log));
        });

        yield return ("Console song files: remove only edits and restore the last one, delete any song, sync rebuild with -p", () =>
        {
            var host = Library();
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            session.Submit("remove 10000002");
            Assert(Output(log).StartsWith("Only edited songs can be removed", StringComparison.Ordinal) && host.Calls.Count == 0, "Not an edit");
            session.Submit("remove 20000004");
            Assert(session.IsAsking && log.Lines[^1].Contains("restore can bring it back", StringComparison.Ordinal), "Asks first");
            session.Submit("n");
            Assert(!host.Calls.Contains("remove 20000004"), "n keeps it");
            session.Submit("remove 20000004");
            session.Submit("y");
            Assert(host.Calls.Contains("remove 20000004") && Output(log).EndsWith("Removed 20000004 2*Plum - R.", StringComparison.Ordinal), "Removed");
            session.Submit("remove -p 20000005");
            Assert(log.Lines.Any(line => line.Contains("instead of 2*Plum - R", StringComparison.Ordinal)), "Only the last one is kept, and it says so");
            session.Submit("restore -p");
            Assert(host.Calls.Contains("restore") && Output(log).EndsWith("Restored 5*Plum - R into slot 2.", StringComparison.Ordinal), "Restore");
            session.Submit("restore");
            Lines(LastOutput(log), "There is nothing to restore.");
            session.Submit("delete -p 10000002");
            Assert(host.Calls.Contains("delete 10000002") && log.Lines.Any(line => line.Contains("the next sync copies it again", StringComparison.Ordinal)), "Custom delete");
            session.Submit("delete now");
            Lines(LastOutput(log), "No song is loaded.");
            session.Submit("sync rebuild");
            Assert(session.IsAsking, "the rebuild asks");
            session.Submit("n");
            Assert(!host.Calls.Contains("resync"), "n does nothing");
            session.Submit("sync -p rebuild");
            Assert(host.Calls.Contains("resync"), "-p goes ahead");
            host.EditError = "The editor could not open.";
            session.Submit("edit 00000001");
            Assert(host.Calls.Contains("edit 00000001") && Output(log).EndsWith("The editor could not open.", StringComparison.Ordinal), "edit reports why");
            session.Submit("edit");
            Assert(host.Calls[^1] == "edit 00000001", "edit with nothing takes the loaded song");
        });

        yield return ("Console settings: every setting has a name, get and set round trip, locks, experiments and property", () =>
        {
            // Names: every setting of the settings window has one, none repeats, and no section is named like a command.
            var named = SettingPaths.All.Select(path => path.Property).ToHashSet();
            var missing = SettingsFields.Names.Where(name => !named.Contains(name) && !SettingPaths.NotInWindow.Contains(name)).ToArray();
            Assert(missing.Length == 0, "Settings without a name: " + string.Join(", ", missing));
            Equal(SettingPaths.All.Select(path => path.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(), SettingPaths.All.Count);
            Assert(SettingPaths.Sections.All(section => CmdCatalog.Find(section.Name) is null), "Sections never share a command's name");
            Assert(SettingPaths.All.All(path => path.Name == path.Name.ToLowerInvariant() && !path.Name.Contains(' ')), "Short lower-case names");

            // Every open setting takes back what get shows (set says it is already so), with locks lifted.
            var open = new VisualizerSettings
            {
                ExperimentalFeatures = true, ImmediateDownshift = true, MagicCircleJudgmentLine = true, MagicCircleFixedTiles = true,
                EnableTilePreference = true, DynamicTiming = true, MaximumTileCount = 8, MinimumTileCount = 1, MapRootDirectory = @"C:\Maps"
            };
            var host = new Host { Settings = open };
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            foreach (var path in SettingPaths.All.Where(path => SettingPaths.LockedBy(path, open) is null))
            {
                session.Submit("get " + path.Name);
                Lines(LastOutput(log), SettingPaths.Format(path, open));
                session.Submit($"set {path.Name} {SettingPaths.Format(path, open)}");
                Assert(Output(log).Contains("is already", StringComparison.Ordinal), $"{path.Name}: {Output(log)}");
            }
            Equal(host.Calls.Count, 0);

            // The user's example: hide the guide buttons, then roll it back. Locks are set here, not left to the defaults.
            host = new Host { Settings = new VisualizerSettings { ImmediateDownshift = false, EnableTilePreference = false, ExperimentalFeatures = false } };
            session = new CmdSession(host, log);
            session.Submit("get general.information-buttons");
            Lines(LastOutput(log), "true");
            session.Submit("set general.information-buttons false");
            Lines(LastOutput(log), "general.information-buttons: true → false");
            Equal(host.Settings.ShowGuideButtons, false);
            session.Submit("set tiles.max 6");
            session.Submit("set tiles.pattern-6k 7 6 5 4 3 2");
            Equal(host.Settings.SixKeyPattern, "2 3 4 5 6 7");
            session.Submit("set keys.lane-1 q");
            Equal(host.Settings.GetLaneKeyTokens()[0], "Q");
            session.Submit("set keys.previous none");
            Equal(host.Settings.PreviousCheckpointKey, null);
            session.Submit("set colors.note #102030");
            Equal(host.Settings.NoteColor, "16, 32, 48");
            session.Submit("set songs.sort note-count");
            Equal(host.Settings.SongSortKey, "NoteCount");
            session.Submit("get songs.sort");
            Lines(LastOutput(log), "note-count");
            session.Submit("set advanced.data-folder auto");
            Assert(Output(log).Contains("is already auto", StringComparison.Ordinal), "auto is the empty data folder");

            // Refusals read in English and change nothing.
            var before = host.Settings;
            foreach (var (line, expected) in new[]
            {
                ("set timing.speed fast", "Use a number"),
                ("set timing.speed -5", "Not changed."),
                ("set general.information-buttons maybe", "Use true or false (or on/off, 1/0, able/disable)."),
                ("set tiles.max 5", "Use one of: 1, 2, 4, 6, 8."),
                ("set keys.reset S", "Not changed."),
                ("set keys.reset NoSuchKey", "is not a key"),
                ("set tiles.down-notes 3", "Locked while tiles.instant-down is false."),
                ("set tiles.order-4k 1 2 3 4", "Locked while advanced.tile-order is false."),
                ("set tiles", "tiles is a section."),
                ("set tiles.nothing 1", "'tiles.nothing' is not a setting."),
                ("set timing.speed", "A value is needed."),
                ("get experimental.center-pair", "'experimental.center-pair' is not a setting."),
                ("get", "A setting is needed")
            })
            {
                session.Submit(line);
                Assert(Output(log).Contains(expected, StringComparison.Ordinal) && !HasHangul(Output(log)), $"{line}: {Output(log)}");
            }
            Equal(host.Settings, before);
            // A toggle takes on/off, 1/0 and able/disable too; get still shows true or false.
            foreach (var (word, value) in new[] { ("off", false), ("on", true), ("0", false), ("1", true), ("disable", false), ("able", true),
                         ("FALSE", false), ("enable", true), ("Off", false) })
            {
                session.Submit("set general.song-info " + word);
                Equal(host.Settings.ShowSongInfo, value);
                session.Submit("get general.song-info");
                Lines(LastOutput(log), value ? "true" : "false");
            }
            session.Submit("set general.song-info true");
            before = host.Settings;
            // speed is set timing.speed without the name; alone it shows the speed.
            host.Settings = host.Settings with { PlaybackSpeedPercent = 100 };
            var speedBefore = host.Settings;
            session.Submit("speed");
            Lines(LastOutput(log), "100");
            session.Submit("speed fast");
            Assert(Output(log).StartsWith("Use a number", StringComparison.Ordinal) && host.Settings == speedBefore, "A word is refused");
            session.Submit("speed 80");
            Lines(LastOutput(log), "timing.speed: 100 → 80");
            Equal(host.Settings.PlaybackSpeedPercent, 80d);
            session.Submit("speed 80");
            Lines(LastOutput(log), "timing.speed is already 80.");
            host.RollBack();
            Equal(host.Settings.PlaybackSpeedPercent, 100d);
            // -ar is the other speed: the approach rate, which is the travel time and not the song's speed (user request).
            var travel = host.Settings.NoteTravelTimeMs;
            session.Submit("speed -ar");
            Lines(LastOutput(log), travel.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            session.Submit("speed -ar 800");
            Equal(host.Settings.NoteTravelTimeMs, 800d);
            Equal(host.Settings.PlaybackSpeedPercent, 100d); // The song's speed is left alone.
            session.Submit("speed -ar fast");
            Assert(Output(log).StartsWith("Use a number", StringComparison.Ordinal) && host.Settings.NoteTravelTimeMs == 800d, "A word is refused");
            host.RollBack();
            Equal(host.Settings.NoteTravelTimeMs, travel);
            // The chord rules moved to timing and advanced; the experimental section has nothing in it.
            session.Submit("set timing.chord-side 1-2");
            Equal(host.Settings.ChordLanePreference, "1-2");
            session.Submit("set timing.no-center-pair false");
            Equal(host.Settings.NoChordCenterPair, false);
            session.Submit("set advanced.chord-lanes true");
            Equal(host.Settings.ChordSectionLanes, true);
            session.Submit("set advanced.experimental true");
            session.Submit("property experimental");
            Assert(LastOutput(log).Any(line => line.Contains("chord-before", StringComparison.Ordinal))
                && LastOutput(log).Any(line => line.Contains("run-restart", StringComparison.Ordinal)),
                "The experiments are listed: " + string.Join(" / ", LastOutput(log)));
            session.Submit("get experimental");
            Assert(LastOutput(log).Any(line => line.Contains("chord-before", StringComparison.Ordinal)), "And read back");

            session.Submit("property");
            Assert(LastOutput(log)[0].StartsWith("general", StringComparison.Ordinal) && LastOutput(log).Any(line => line.StartsWith("experimental", StringComparison.Ordinal)), "Sections");
            session.Submit("property tiles");
            Assert(LastOutput(log).Any(line => line.StartsWith("tiles.max", StringComparison.Ordinal) && line.EndsWith("6", StringComparison.Ordinal))
                && LastOutput(log).Any(line => line.StartsWith("tiles.down-notes", StringComparison.Ordinal) && line.EndsWith("(locked)", StringComparison.Ordinal)), "Settings of a section");
            session.Submit("property tiles.max");
            Lines(LastOutput(log), "tiles.max is 6 now.", "Values: 1, 2, 4, 6, 8.");
            session.Submit("property keys.reset");
            Assert(Output(log).Contains("Takes a key name", StringComparison.Ordinal), "Free values are described");
            session.Submit("property -c general");
            Assert(host.Copied.Contains("general.language", StringComparison.Ordinal), "property -c copies");
            host.Settings = host.Settings with { ExperimentalFeatures = false };
            session.Submit("property");
            Assert(!LastOutput(log).Any(line => line.StartsWith("experimental", StringComparison.Ordinal)), "Closed experiments are not listed");
        });

        yield return ("Console export, import, rollback, reset and do", () =>
        {
            var host = new Host { Settings = new VisualizerSettings { MapRootDirectory = @"D:\Maps", DataRootDirectory = @"D:\Store" } };
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            session.Submit("export -c");
            var json = host.Copied;
            Assert(json.Contains("\"NoteTravelTimeMs\"", StringComparison.Ordinal) && !json.Contains("MapRootDirectory", StringComparison.Ordinal)
                && LastOutput(log)[0] == "{", "export shows and copies the shared JSON");

            // import: only the named settings change; the changes are shown before [Y/n].
            session.Submit("import { \"NoteTravelTimeMs\": 700, \"ShowGuideButtons\": false }");
            Assert(session.IsAsking && log.Lines.Contains("display.travel: 600 → 700") && log.Lines.Contains("general.information-buttons: true → false"), "Changes first");
            session.Submit("y");
            Assert(host.Settings.NoteTravelTimeMs == 700 && !host.Settings.ShowGuideButtons && host.Settings.MapRootDirectory == @"D:\Maps", "Applied");
            session.Submit("import -p { \"MapRootDirectory\": \"E:\\\\Other\", \"StartDelayMs\": 300 }");
            Assert(host.Settings.StartDelayMs == 300 && host.Settings.MapRootDirectory == @"D:\Maps"
                && log.Lines.Contains("The map and data folders keep this PC's values."), "Folders stay");
            session.Submit("import { \"NoteTravelTimeMs\": -1 }");
            Assert(Output(log).StartsWith("The JSON could not be used.", StringComparison.Ordinal) && !HasHangul(Output(log)), "Bad values: " + Output(log));
            session.Submit("import { oops");
            Assert(Output(log).StartsWith("The JSON could not be used.", StringComparison.Ordinal), "Broken JSON");
            session.Submit("import");
            Assert(Output(log).StartsWith("Paste the settings JSON", StringComparison.Ordinal), "Nothing pasted");
            session.Submit("import { \"StartDelayMs\": 300 }");
            Lines(LastOutput(log), "Nothing changes.");

            // rollback: steps back one change at a time, showing what goes back and asking first.
            session.Submit("rollback");
            Assert(session.IsAsking && log.Lines.Any(line => line.StartsWith("timing.start-delay: 300 → ", StringComparison.Ordinal)), "Shows the way back");
            session.Submit("y");
            Equal(host.Settings.StartDelayMs, new VisualizerSettings().StartDelayMs);
            session.Submit("rollback -p");
            Assert(host.Settings.NoteTravelTimeMs == 600 && host.Settings.ShowGuideButtons, "Second step back");
            session.Submit("rollback");
            Lines(LastOutput(log), "There is nothing to roll back.");

            // reset keeps this PC's folders.
            host.Settings = host.Settings with { NoteTravelTimeMs = 900 };
            session.Submit("reset -p");
            Assert(host.Settings == new VisualizerSettings { MapRootDirectory = @"D:\Maps", DataRootDirectory = @"D:\Store" }, "Defaults but the folders");
            session.Submit("reset");
            Lines(LastOutput(log), "The settings are already the defaults.");

            // do: the settings window's actions.
            // The checkpoint keys say so in their action names.
            session.Submit("do clear-previous-checkpoint-key");
            Lines(LastOutput(log), "keys.previous: - → none");
            session.Submit("do clear-previous-checkpoint-key");
            Lines(LastOutput(log), "keys.previous is already none.");
            session.Submit("do clear-next-checkpoint-key");
            Lines(LastOutput(log), "keys.next: + → none");
            session.Submit("do clear-previous-key");
            Assert(Output(log).Contains("is not an action", StringComparison.Ordinal), "The old name is gone");
            host.Folder = @"F:\Picked";
            session.Submit("do map-folder");
            Assert(host.Calls.Contains(@"pick D:\Maps") && host.Settings.MapRootDirectory == @"F:\Picked", "map-folder picks and applies");
            host.Folder = null;
            session.Submit("do data-folder");
            Lines(LastOutput(log), "No folder was picked.");
            session.Submit("do reset");
            Assert(Output(log).Contains("is not an action", StringComparison.Ordinal) && Output(log).Contains("clear-next-checkpoint-key", StringComparison.Ordinal), "Unknown action");
        });

        yield return ("Console closes the settings window first, applying or dropping its changes as answered", () =>
        {
            var host = new Host { SettingsOpen = true, Pending = new VisualizerSettings { NoteTravelTimeMs = 800 } };
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            // Reading needs no closing.
            session.Submit("get display.travel");
            Assert(host.SettingsOpen && !session.IsAsking, "get leaves the window alone");
            session.Submit("set general.song-info false");
            Assert(session.IsAsking && log.Lines[^1].StartsWith("The settings have changes that are not applied.", StringComparison.Ordinal), "Asks about the changes");
            session.Submit("y");
            Assert(!host.SettingsOpen && host.Calls[0] == "close and apply", "Applied and closed");
            // The set is built on the settings after the window's changes, so both stay.
            Assert(host.Settings.NoteTravelTimeMs == 800 && !host.Settings.ShowSongInfo, "Both changes kept");

            host.SettingsOpen = true;
            host.Pending = new VisualizerSettings { NoteTravelTimeMs = 100 };
            session.Submit("edit -p 00000001");
            Assert(host.SettingsOpen, "An unknown id stops before the window");
            host.SongList.AddRange(Library().SongList);
            session.Submit("edit -p 00000001");
            Assert(!host.SettingsOpen && host.Calls[^2] == "close and apply" && host.Calls[^1] == "edit 00000001", "-p applies and edits");

            host.SettingsOpen = true;
            host.Pending = new VisualizerSettings { NoteTravelTimeMs = 333 };
            session.Submit("reset");
            session.Submit("n");
            Assert(!host.SettingsOpen && host.Calls.Contains("close") && host.Settings.NoteTravelTimeMs != 333, "n drops them");
            // Then reset asks its own question.
            Assert(session.IsAsking && session.Log.Lines[^1].StartsWith("Reset every setting", StringComparison.Ordinal), "Reset asks next");
            session.Submit("n");

            host.SettingsOpen = true;
            session.Submit("do view-playlist-details");
            Assert(!host.SettingsOpen && host.Calls[^1] == "playlist", "Nothing unapplied: the window just closes");
        });

        yield return ("Settings: the map sync sits with the song list, and reset keeps the folders", () =>
        {
            // The map sync moved here from the main window (user rule); the editor's button is parked in the experimental
            // card while the field makes room for the slot picker, and leaves the settings before it opens.
            var settings = new VisualizerSettings { MapRootDirectory = @"D:\Maps", DataRootDirectory = @"D:\Store" };
            var window = new SettingsPanel(settings, new MemorySettingsLayoutStore());
            var synced = 0;
            window.SyncMaps = () => synced++;
            var sync = FindAll<Button>(window).Single(button => Equals(button.Content, UiText.Get(settings.Language, "Sync")));
            sync.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal(synced, 1);
            var edits = 0;
            var left = 0;
            window.EditSongRequested = () => edits++;
            window.Leaving = () => left++;
            var edit = FindAll<Button>(window).Single(button => Equals(button.Content, UiText.Get(settings.Language, "EditSong")));
            edit.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(edits == 1 && left == 1, "The editor button puts the settings away and then opens the editor");

            // Reset keeps this PC's folders (user rule).
            var reset = new SettingsPanel(settings with { NoteTravelTimeMs = 900 }, new MemorySettingsLayoutStore()) { Confirm = (_, _, _) => true };
            FindAll<Button>(reset).Single(button => Equals(button.Content, UiText.Get(settings.Language, "ResetSettings"))).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var built = reset.BuildSettings();
            Assert(built.MapRootDirectory == @"D:\Maps" && built.DataRootDirectory == @"D:\Store" && built.NoteTravelTimeMs == new VisualizerSettings().NoteTravelTimeMs, "Folders stay");
        });

        yield return ("Rollback history: every applied change, one step at a time, field by field", () =>
        {
            var model = new MainViewModel();
            model.ApplySettings(new VisualizerSettings { NoteTravelTimeMs = 700 });
            model.ApplySettings(model.Settings with { ShowGuideButtons = false });
            model.ApplySettings(model.Settings);
            Equal(model.SettingsHistory.Count, 2); // Applying the same settings is not a change.
            var committed = 0;
            model.SettingsCommitted += () => committed++;
            // Only what the last change touched goes back: the travel time from the step before stays.
            model.RollBack();
            Assert(model.Settings.ShowGuideButtons && model.Settings.NoteTravelTimeMs == 700 && committed == 1, "One step");
            model.RollBack();
            Equal(model.Settings.NoteTravelTimeMs, new VisualizerSettings().NoteTravelTimeMs);
            Assert(model.RollBack() is null, "Nothing left");
            model.ApplySettings(model.Settings with { StartDelayMs = 100 });
            model.ForgetSettingsHistory();
            Equal(model.SettingsHistory.Count, 0);

            // The real app hands the same history to the console.
            var main = English(new MainWindow());
            ICmdHost app = main;
            Assert(app.LastChange is null && app.Songs.Count == 0 && app.LoadedId is null && !app.SettingsOpen, "Fresh app host");
            Assert(app.ApplySettings(app.Settings with { NoteTravelTimeMs = 750 }) is null && app.LastChange is not null, "Applied through the host");
            Assert(app.RollBack() is null && app.Settings.NoteTravelTimeMs == new VisualizerSettings().NoteTravelTimeMs, "Rolled back through the host");
            Equal(app.EditSong("99999999"), "No song has the id 99999999.");
            // checkpoint and go on the real app: with no song, then on one whose tiles and checkpoints are known.
            Equal(app.MoveCheckpoint(2), "The loaded song has no checkpoints.");
            Equal(app.StartAtTile(3), "No song is loaded.");
            var appModel = (MainViewModel)main.DataContext;
            // Hits on tiles 1, 2, 4, 5 (tile 3 is a midspin), with a checkpoint at the third hit.
            appModel.SelectedMap = new MapCacheEntry("cp", "unused", "map.adofai", "Checkpoints", MapParseStatus.Supported,
                new ParsedTimeline([0, 500, 1000, 1500], [new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 2, 1000, 4)], true,
                    [1, 2, 4, 5]), [], "test", "test");
            appModel.LoadSelectedSong();
            Equal(app.Checkpoint, 1);
            Assert(app.MoveCheckpoint(2) is null && app.Checkpoint == 2, "Moved to the song's own checkpoint");
            Equal(app.MoveCheckpoint(9), "That checkpoint is not in this song.");
            Assert(app.StartAtTile(2) is null, "A tile the song presses starts there");
            // A tile start leaves the section box filled; going back to a checkpoint clears it, as the help says.
            Assert(appModel.SectionText.Length > 0 && appModel.Playback.StartTile is not null, "The section is set: " + appModel.SectionText);
            Assert(app.MoveCheckpoint(1) is null && appModel.SectionText.Length == 0 && appModel.Playback.StartTile is null,
                "checkpoint first comes back to the song's beginning");
            Equal(app.StartAtTile(3), "Tile 3 is a midspin tile, so starting there is not verified.");
            Equal(app.StartAtTile(5), "Checkpoints has tiles 1 to 4.");
            main.Close();
        });

        yield return ("A long command shows a progress bar instead of the prompt and takes no input until it is done", () =>
        {
            var host = new Host();
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            Assert(!session.Busy && session.JobText().Length == 0, "Nothing is running yet");

            host.HoldJobs = true;
            session.Submit("sync");
            Lines(host.Calls, "sync");
            // The bar stands where the prompt would be, so the screen takes nothing until the job reports it is done.
            Assert(session.Busy, "The job is running");
            Assert(session.JobText().StartsWith("Map sync", StringComparison.Ordinal) && session.JobText().Contains("0%", StringComparison.Ordinal),
                "The bar starts empty: " + session.JobText());
            Assert(!log.Lines[^1].StartsWith(CmdSession.PromptText, StringComparison.Ordinal), "No prompt while it runs");
            // A command that gets here meanwhile is dropped, not queued, and leaves no line behind.
            var before = log.Lines.Count;
            session.Submit("version");
            Lines(host.Calls, "sync");
            Equal(log.Lines.Count, before);

            host.JobProgress!.Report(0.5);
            Assert(session.JobText().Contains(" 50%", StringComparison.Ordinal) && session.JobText().Contains("############", StringComparison.Ordinal),
                "Half way: " + session.JobText());
            host.JobDone!(57);
            Assert(!session.Busy && session.JobText().Length == 0, "The job is over");
            Assert(LastOutput(log)[^1] == "Map sync finished. 57 songs in the library.", string.Join(" | ", LastOutput(log)));
            // And the console works again.
            session.Submit("date");
            Assert(LastOutput(log).Count > 0, "The next command runs");

            // What the screen itself shows while a job runs, since the bar stands where the prompt would be.
            var window = new CmdWindow(session, "space") { Left = -20000, Top = -20000, ShowActivated = false };
            window.Run("sync");
            host.JobProgress!.Report(0.48);
            Assert(window.OutputText.Contains(" 48%", StringComparison.Ordinal)
                && !window.OutputText.TrimEnd().EndsWith(CmdSession.PromptText.TrimEnd(), StringComparison.Ordinal),
                "The screen shows the bar and no prompt: " + window.OutputText[^60..]);
            Capture(window, Path.Combine(artifacts, "console-progress.png"), 680, 440);
            host.JobDone!(57);
            Assert(window.OutputText.TrimEnd().EndsWith(CmdSession.PromptText.TrimEnd(), StringComparison.Ordinal),
                "The prompt is back once the job is done: " + window.OutputText[^60..]);
            window.Close();
        });

        yield return ("install linker says what it will write, asks first, and leaves a copy of the same version alone", () =>
        {
            var host = new Host();
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            var target = Path.Combine("G:", "game", "Mods", "AdofaiVisualizerSync");

            // Nothing found: the two exceptions the user asked for, and nothing is written in either.
            session.Submit("install linker");
            Assert(LastOutput(log)[0].Contains("game folder was not found", StringComparison.Ordinal), Output(log));
            host.Place = new LinkerPlace(Path.Combine("G:", "game"), null, null, null, target);
            session.Submit("install linker");
            Assert(LastOutput(log)[0].Contains("linker itself was not found", StringComparison.Ordinal), Output(log));
            Assert(host.Installed.Count == 0 && !session.IsAsking, "Nothing was written and nothing was asked");

            // Ready to install: it names the folder it will write into and asks, and a no writes nothing.
            host.Place = new LinkerPlace(Path.Combine("G:", "game"), Path.Combine("G:", "built"), "0.9.2", null, target);
            session.Submit("install linker");
            Assert(session.IsAsking && log.Lines[^1].Contains(target, StringComparison.Ordinal)
                && log.Lines[^1].Contains("[Y/n]", StringComparison.Ordinal), log.Lines[^1]);
            session.Submit("n");
            Assert(host.Installed.Count == 0 && LastOutput(log)[^1] == "Cancelled.", Output(log));
            session.Submit("install linker");
            session.Submit("y");
            Lines(host.Installed, target);
            Assert(LastOutput(log).Any(line => line.Contains("Unity Mod Manager", StringComparison.Ordinal)), "It says what to do next");

            // The same version already there is left alone; an older one is offered a replacement.
            host.Place = host.Place with { Installed = "0.9.2" };
            session.Submit("install linker");
            Assert(!session.IsAsking && host.Installed.Count == 1 && LastOutput(log)[0].Contains("already installed", StringComparison.Ordinal), Output(log));
            host.Place = host.Place with { Installed = "0.9.1" };
            session.Submit("install -p linker");
            Lines(host.Installed, target, target);

            // A refusal from the app is passed on as it is.
            host.InstallProblem = "The linker was not installed. 'Mods' is a link, so nothing was written.";
            session.Submit("install -p linker");
            Assert(LastOutput(log).Contains(host.InstallProblem) && host.Installed.Count == 2, Output(log));
        });

        yield return ("link auto and link manual switch matching the tiles by itself, and it is not a setting", () =>
        {
            var host = new Host();
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            session.Submit("link auto");
            Lines(host.AutoAsks.Select(on => on ? "on" : "off"), "on");
            session.Submit("link manual");
            Lines(host.AutoAsks.Select(on => on ? "on" : "off"), "on", "off");
            // Following by hand is a different thing, and there is no UI or setting for this one (user rule).
            Assert(host.FollowAsks.Count == 0, "It is not the same as link follow");
            Assert(SettingPaths.Find("link.auto") is null, "There is no setting for it");
        });

        yield return ("Ctrl+T opens the console, then minimizes it, then shows it again; the main window stays as it is", () =>
        {
            // User request. The key itself cannot be sent from a test (Ctrl is read from the real keyboard), so the
            // same calls the key makes are used.
            var main = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false };
            main.Show();
            var console = main.ToggleConsole();
            Assert(console.IsVisible && console.WindowState == WindowState.Normal && ReferenceEquals(console.Owner, main), "Opens");
            Assert(ReferenceEquals(main.ToggleConsole(), console) && console.WindowState == WindowState.Minimized, "Then minimizes");
            Equal(main.WindowState, WindowState.Normal);
            main.ToggleConsole();
            Assert(console.WindowState == WindowState.Normal && console.IsVisible, "Then shows it again");
            // Ctrl+T in the console itself minimizes it, and the next one in the app brings it back.
            console.ToggleMinimized();
            Equal(console.WindowState, WindowState.Minimized);
            main.ToggleConsole();
            Equal(console.WindowState, WindowState.Normal);
            // Minimized with its own button, it comes back the same way.
            console.WindowState = WindowState.Minimized;
            main.ToggleConsole();
            Equal(console.WindowState, WindowState.Normal);
            Assert(Visualizer.Core.Localization.Languages.Get("korean", "ConsoleShortcut").Contains("최소화", StringComparison.Ordinal), "The manual says so");
            // Closed, the next Ctrl+T opens a new console.
            console.Close();
            var again = main.ToggleConsole();
            Assert(!ReferenceEquals(again, console) && again.IsVisible && again.WindowState == WindowState.Normal, "A new console");
            again.Close();
            main.Close();
        });

        yield return ("Console fps: the frame report, and -r starts a new measurement", () =>
        {
            var host = Library();
            var log = new CmdLog();
            var session = new CmdSession(host, log);
            session.Submit("fps");
            Lines(LastOutput(log), "Frames 240 over 2.0 s · 120.0 fps at the median", "Render tier 2 (hardware)");
            Assert(host.FrameAsks.SequenceEqual([false]), "Printing alone keeps the window");
            session.Submit("fps -r");
            Assert(host.FrameAsks.SequenceEqual([false, true]), "-r starts a new measurement");
            session.Submit("fps -c");
            Assert(host.Copied.StartsWith("Frames 240", StringComparison.Ordinal), "-c copies the report");
            session.Submit("fps now");
            Assert(Output(log).StartsWith("The syntax of the command is incorrect.", StringComparison.Ordinal), "fps takes no word");
        });

        yield return ("Console window: one terminal screen, Tab completion, history and the kept transcript", () =>
        {
            var log = new CmdLog();
            var host = Library();
            host.Timeline = new ParsedTimeline(new[] { 0d, 600 });
            var window = new CmdWindow(new CmdSession(host, log), "white") { Left = -20000, Top = -20000, ShowActivated = false };
            Color Resource(string key) => ((SolidColorBrush)window.FindResource(key)).Color;
            var white = Resource("WindowBrush");
            Equal(((SolidColorBrush)window.Background).Color, white);
            window.ApplyTheme("dark");
            Assert(Resource("WindowBrush") != white && ((SolidColorBrush)window.Background).Color == Resource("WindowBrush"), "Follows the theme");
            window.Show();
            // The window holds the transcript and, over it, the note editor; the transcript is the first.
            var screen = FindAll<TextBox>(window).First();
            Assert(!FindAll<Button>(window).Any(), "No Run button");
            Assert(screen.Text.EndsWith(CmdSession.PromptText + " ", StringComparison.Ordinal), "The screen ends with the prompt");
            screen.Select(screen.Text.Length, 0);
            screen.SelectedText = "cmd";
            Press(screen, Key.Enter);
            Assert(window.OutputText.Contains("Visualizer> cmd") && window.OutputText.Contains("note [<id>] <range>"), "Enter runs the line");
            Equal(window.InputText, "");

            // Only the typed part can change.
            var before = screen.Text;
            screen.Select(screen.Text.Length, 0);
            Assert(Press(screen, Key.Back), "Backspace at the prompt does nothing");
            screen.Text = "broken";
            Equal(screen.Text, before);

            // Tab: cmd first on an empty line, then commands, options, keywords, settings by section and values.
            Assert(Press(screen, Key.Tab), "Tab stays in the console");
            Equal(window.InputText, "cmd");
            window.Complete();
            Equal(window.InputText, "help");
            window.Complete(backward: true);
            Equal(window.InputText, "cmd");
            Press(screen, Key.Escape);
            foreach (var (typed, first) in new[]
            {
                ("pla", "playlist"), ("playlist -", "playlist -id"), ("playlist -id ", "playlist -id base"), ("help la", "help last"),
                ("info ", "info now"), ("info 1000", "info 10000002"), ("do cl", "do clear-previous-checkpoint-key"),
                ("load lum", "load Plum - R"), ("load or", "load origin"), ("load -og bui", "load -og Built in"), ("load -id 0000", "load -id 00000001"),
                ("load -s", "load -slot"), ("note ", "note now"), ("title f", "title family"),
                ("set ti", "set tiles"), ("set tiles.m", "set tiles.max"), ("set general.information-buttons ", "set general.information-buttons true"),
                ("property col", "property colors")
            })
            {
                screen.Select(screen.Text.Length, 0);
                screen.SelectedText = typed;
                window.Complete();
                Equal(window.InputText, first);
                Press(screen, Key.Escape);
            }
            screen.Select(screen.Text.Length, 0);
            screen.SelectedText = "set tiles.m";
            window.Complete();
            window.Complete();
            Equal(window.InputText, "set tiles.min");
            Press(screen, Key.Escape);
            screen.Select(screen.Text.Length, 0);
            screen.SelectedText = "who";
            window.Complete();
            Equal(window.InputText, "whoami"); // Listed like the rest now, so Tab offers it.
            Press(screen, Key.Escape);

            // whoami's art: the console's font has every character (no stand-in glyph of another width) and every line fits
            // the narrowest console.
            // The first of the console's fonts that is installed is the one that draws the text.
            var installed = Fonts.SystemFontFamilies.Select(family => family.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var used = CmdWindow.Mono.Source.Split(',').Select(name => name.Trim()).First(installed.Contains);
            Assert(new Typeface(new FontFamily(used), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var font), "Font " + used);
            var missing = CmdSession.Whoami.SelectMany(line => line).Distinct().Where(c => !font.CharacterToGlyphMap.ContainsKey(c)).ToArray();
            Assert(missing.Length == 0, $"{font.FamilyNames.Values.First()} lacks: {string.Join(" ", missing.Select(c => ((int)c).ToString("X4")))}");
            var widths = CmdSession.Whoami.SelectMany(line => line).Distinct().Select(c => font.AdvanceWidths[font.CharacterToGlyphMap[c]]).Distinct().ToArray();
            Assert(widths.Length == 1, "Every character of the art is as wide as the others");
            var artWidth = CmdSession.Whoami.Max(line => line.Length) * widths[0] * 13;
            Assert(artWidth + 12 * 2 + SystemParameters.VerticalScrollBarWidth + 20 <= window.MinWidth, $"The art ({artWidth:0}px) fits the narrowest console ({window.MinWidth}px)");
            window.Run("whoami");
            Assert(window.Width >= 760 && window.Height >= 480, "The console opens roomy enough for the art");
            // Layout rounding pulls the size to whole device pixels, so 990 comes back as 990.4 at 125% scaling.
            var pixel = 1 / VisualTreeHelper.GetDpi(window).DpiScaleX;
            Assert(Math.Abs(window.Width - 990) <= pixel + 0.01 && Math.Abs(window.Height - 624) <= pixel + 0.01,
                $"The console opens about 30% roomier than 760x480 (app 0.5.8), not {window.Width:0.##}x{window.Height:0.##}");
            Capture(window, Path.Combine(artifacts, "console-whoami.png"), (int)window.Width, (int)window.Height);

            window.Run("note all");
            Assert(window.OutputText.Contains("n1 ") && window.OutputText.Contains("n2 "), "note from the window");
            window.RecallHistory(-1);
            Equal(window.InputText, "note all");
            Capture(window, Path.Combine(artifacts, "console-window.png"), 680, 440);
            window.RecallHistory(1);
            Equal(window.InputText, "");

            // A question is answered on its own line, right after [Y/n].
            window.Run("sync rebuild");
            Assert(screen.Text.EndsWith("[Y/n] ", StringComparison.Ordinal), "The answer goes after the question");
            window.Run("n");

            // exit -m and closing the window keep the transcript; exit clears it.
            window.Run("exit -m");
            var again = new CmdWindow(new CmdSession(host, log), "dark");
            Assert(again.OutputText.Contains("note [<id>] <range>"), "exit -m keeps the screen");
            again.Show();
            again.Close();
            var third = new CmdWindow(new CmdSession(host, log), "dark");
            Assert(third.OutputText.Contains("note [<id>] <range>"), "Closing the window keeps the screen");
            var closed = false;
            third.Closed += (_, _) => closed = true;
            third.Show();
            third.Run("exit");
            Assert(closed, "exit closes the window");
            var fresh = new CmdWindow(new CmdSession(host, log), "dark");
            Equal(fresh.OutputText, string.Join(Environment.NewLine, $"Visualizer Console [Version {AppInfo.Version}]", "Type 'cmd' to see the commands.", "")
                + Environment.NewLine + CmdSession.PromptText + " ");
            fresh.Close();
            Assert(CmdWindow.IsShortcut(Key.T, ModifierKeys.Control) && !CmdWindow.IsShortcut(Key.T, ModifierKeys.Control | ModifierKeys.Shift), "Ctrl+T only");
        });
    }
}
