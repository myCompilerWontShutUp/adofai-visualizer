using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Visualizer.App.Input;
using Visualizer.App.ViewModels;
using Visualizer.Application.Cache;
using Visualizer.Application.Library;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Core.Localization;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using static Visualizer.Core.Localization.ConsoleText;

namespace Visualizer.App.Commands;

/// <summary>A song as the console shows it.</summary>
/// <param name="Id">The eight-character id (first digit: 0 base, 1 custom, 2 edited).</param>
/// <param name="Title">The name the song list shows, slot included ("2*Plum - R").</param>
/// <param name="Kind">base, custom, edited or external.</param>
/// <param name="Family">The same for a song and every edit of it, so family finds them together.</param>
public sealed record CmdSong(string Id, string Title, string Kind, bool Hidden, int? Slot, string Family, MapCacheEntry Entry);

/// <summary>
/// What update.ps1 -Check found: this copy's version, the target's (the latest, or the one asked for) and what stands in
/// the way. <see cref="Problems"/> holds the script's own words when it could not look, such as no network.
/// </summary>
public sealed record UpdateCheck(string Mine, string? Target, bool Found, int Behind, bool Same, int Changed, int Unpublished,
    IReadOnlyList<string> Problems)
{
    public static UpdateCheck Failed(params string[] problems) => new("?", null, false, 0, false, 0, 0, problems);

    /// <summary>Reads the script's "state" lines; any other line is a problem to show as it is.</summary>
    public static UpdateCheck Read(IReadOnlyList<string> lines)
    {
        string mine = "?", target = "";
        bool found = true, same = false;
        int behind = 0, changed = 0, unpublished = 0;
        var problems = new List<string>();
        foreach (var line in lines)
        {
            if (!line.StartsWith("state ", StringComparison.Ordinal)) { problems.Add(line); continue; }
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int Number() => words.Length > 2 && int.TryParse(words[2], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            var value = words.Length > 2 ? words[2] : "";
            switch (words.Length > 1 ? words[1] : "")
            {
                case "mine": mine = value; break;
                case "target": target = value; break;
                case "missing": target = value; found = false; break;
                case "behind": behind = Number(); break;
                case "same": same = Number() == 1; break;
                case "changed": changed = Number(); break;
                case "unpublished": unpublished = Number(); break;
            }
        }
        return new(mine, target.Length > 0 ? target : null, found, behind, same, changed, unpublished, problems);
    }
}

/// <summary>
/// What scannow found in one area: each problem as a line, a short count of what was checked, and whether the area is
/// not there at all (the linker before it is installed).
/// </summary>
public sealed record ScanArea(string Name, IReadOnlyList<string> Problems, string? Checked = null, bool NotInstalled = false);

/// <summary>What note prints from: a song's timeline and the lane edit laid over the rules' notes, if there is one.</summary>
public sealed record CmdNoteSource(ParsedTimeline Timeline, NoteEdit? Layout);

/// <summary>
/// A note as report -ls lists it: the version it was written for, where it stands (open, done or asked) with the reason
/// in the words it was written in, and its text so a word can be looked for in it.
/// </summary>
public sealed record CmdNote(string Title, string Version, string Done, int Lines, IReadOnlyList<string> Text,
    string State = "open", string Why = "")
{
    public bool Holds(string word) => Text.Any(line => line.Contains(word, StringComparison.CurrentCultureIgnoreCase));
}

/// <summary>
/// What install linker found before anything is written: the game folder, the folder a built mod sits in with its version,
/// the version already installed (null when the mod is not there) and the folder that would be written.
/// </summary>
public sealed record LinkerPlace(string? GameDirectory, string? Source, string? SourceVersion, string? Installed, string Target);

/// <summary>What the console reads and changes in the app. Every reason handed back is in the console's language (<see cref="ConsoleText"/>).</summary>
public interface ICmdHost
{
    /// <summary>The loaded song's timeline; null before a song is loaded.</summary>
    ParsedTimeline? Timeline { get; }
    /// <summary>The song that loading would load now (picked in the song box); null when there is none.</summary>
    string? SongToLoad { get; }
    /// <summary>The name of the song shown on the main window; null before a song is loaded.</summary>
    string? LoadedSongName { get; }
    /// <summary>Loads <see cref="SongToLoad"/>. False, with the reason, when no timeline could be loaded.</summary>
    bool LoadSong(out string message);
    /// <summary>
    /// Loads the song with this id as the main window's Load does. <paramref name="keepSpeed"/>
    /// overrides keep speed on load: true keeps the practice speed, false puts it back to 100%. Null when the song plays,
    /// otherwise the reason.
    /// </summary>
    string? LoadSongById(string id, bool? keepSpeed);
    /// <summary>
    /// load -force (user rule): reads an unsupported song as an older verified format version, keeps that in the cache and
    /// loads it as <see cref="LoadSongById"/> does. Null when the song plays, otherwise the reason.
    /// </summary>
    string? ForceLoadSongById(string id, bool? keepSpeed);
    /// <summary>
    /// How smoothly the app is drawing: frame intervals, its own work per frame, collections and the render tier.
    /// <paramref name="restart"/> starts a new measurement window after the report.
    /// </summary>
    IReadOnlyList<string> FrameStats(bool restart);
    /// <summary>
    /// The read-only game sync link (docs/game-sync-plan.md): what the mod is sending and how steady the link is.
    /// <paramref name="start"/> opens the pipe, <paramref name="stop"/> closes it, <paramref name="restart"/> measures again.
    /// </summary>
    IReadOnlyList<string> GameSync(bool start, bool stop, bool restart);
    /// <summary>
    /// link tiles: lines the tiles the game actually played up with the loaded song's hit times. This is the only check
    /// against the game itself, since the tests and the independent calculator share one reading of the game's rules.
    /// </summary>
    IReadOnlyList<string> GameSyncTiles();
    /// <summary>
    /// link follow / unfollow: starts or stops following the game's clock, so the notes come down with the game instead of with the
    /// start key. Only the playhead moves; the notes and their times are untouched.
    /// </summary>
    IReadOnlyList<string> GameSyncFollow(bool on);

    /// <summary>
    /// Switches following on by itself: whenever the game loads the song that is loaded here, the tiles are matched and the
    /// notes go on the game's clock, with the same checks typing it by hand makes. The loaded song is never changed.
    /// </summary>
    IReadOnlyList<string> GameSyncAuto(bool on);
    /// <summary>
    /// link inspect (measurement only): presses the visualizer's notes into the game while every rule of it holds, or stops.
    /// </summary>
    /// <param name="flow">inspect -flow: every key goes to the console as it is sent.</param>
    /// <param name="progress">How far the run has got, 0..1, for the bar the console shows meanwhile.</param>
    /// <param name="ended">Called once when a run that started ends, by itself, by Esc or by inspect stop.</param>
    IReadOnlyList<string> GameSyncInspect(bool on, bool flow = false, IProgress<double>? progress = null, Action? ended = null) =>
        [T("InspectNotHere")];
    /// <summary>Whether an inspect run is going on.</summary>
    bool Inspecting => false;
    /// <summary>The lines of the last <paramref name="count"/> inspect runs (every run for 0), or null when none is kept.</summary>
    IReadOnlyList<string>? InspectLines(int count) => null;
    /// <summary>The log's lines, oldest first (log, report -log).</summary>
    IReadOnlyList<string> LogLines() => [];
    /// <summary>Empties the log.</summary>
    void ClearLog() { }

    /// <summary>Where the link mod would come from and go to, so the console can say what it will write before it writes.</summary>
    LinkerPlace FindLinker();

    /// <summary>
    /// Writes the mod's own files into the game's Mods folder, or says why it could not. Only ever called after a yes, and
    /// it reports 0..1 so the console can show a bar while it writes.
    /// </summary>
    string? InstallLinker(LinkerPlace place, IProgress<double> progress);
    /// <summary>The applied settings.</summary>
    VisualizerSettings Settings { get; }
    /// <summary>The language the console speaks: the app's own (user rule).</summary>
    string Language => Settings.Language;
    bool SettingsOpen { get; }
    /// <summary>Whether the note editor is open; commands that would work behind it are refused while it is.</summary>
    bool EditorOpen => false;
    /// <summary>
    /// What note reads for now: while the note editor is open, its song with the lanes as they stand, saved or not (user
    /// rule); otherwise the loaded song with the edit it plays. Null when there is neither.
    /// </summary>
    CmdNoteSource? NowNotes => Timeline is { } timeline ? new(timeline, null) : null;
    /// <summary>The saved lane edit a song plays with, so note prints an edited song as it plays; null for an original.</summary>
    NoteEdit? EditOf(CmdSong song) => null;
    /// <summary>
    /// Puts the lanes of the open editor's notes where the JSON says, as note -json prints them. Null when done, otherwise
    /// why not. Nothing is changed unless every line passes.
    /// </summary>
    string? OverrideNotes(string json) => T("EditorNotOpen");
    /// <summary>Whether the open settings hold changes that are not applied.</summary>
    bool HasUnappliedSettings { get; }
    /// <summary>Applies those changes and keeps the window open: null on success, otherwise the reason.</summary>
    string? ApplyPendingSettings();
    /// <summary>Closes the settings, applying their unapplied changes first or dropping them. Null when closed.</summary>
    string? CloseSettings(bool apply);
    void CopyText(string text);

    /// <summary>
    /// The checkpoint the loaded song is prepared from, counted as the timeline counts them: 1 is the song's beginning and
    /// the checkpoint the main window calls n is n + 1 here.
    /// </summary>
    int Checkpoint => 1;
    /// <summary>Prepares the song from that checkpoint, as the main window's list does. Null when it moved, otherwise why not.</summary>
    string? MoveCheckpoint(int checkpoint) => T("NoSongLoaded");
    /// <summary>
    /// Prepares the song from a practice tile, as the section box does. Null when it moved, otherwise why not; the tile is
    /// checked here so the reason is in English rather than the timeline's own.
    /// </summary>
    string? StartAtTile(int tile) => T("NoSongLoaded");
    /// <summary>The tiles pinned on a song family (an original and its edits share them), in order.</summary>
    IReadOnlyList<int> PinsOf(string family) => [];
    /// <summary>Keeps a family's pins; null when kept, otherwise why not.</summary>
    string? SetPins(string family, IReadOnlyList<int> tiles) => T("PinsNotHere");
    /// <summary>
    /// The redirects link auto follows (user request): song family → the id of the song it loads in place of that family's
    /// song when the game plays it.
    /// </summary>
    IReadOnlyDictionary<string, string> Redirects => new Dictionary<string, string>();
    /// <summary>Keeps these redirects in place of every one kept so far; null when kept, otherwise why not.</summary>
    string? SetRedirects(IReadOnlyDictionary<string, string> redirects) => T("RedirectsNotHere");

    /// <summary>Every synced song, hidden ones included.</summary>
    IReadOnlyList<CmdSong> Songs { get; }
    /// <summary>The ids of the main window's list in its order: its group, hidden songs left out, sorted as it shows them.</summary>
    IReadOnlyList<string> CurrentIds { get; }
    /// <summary>The id of the loaded song; null when none is loaded or it is not in the list (the example, the preview).</summary>
    string? LoadedId { get; }
    /// <summary>
    /// The id of the song the game is playing, found in the song list as link auto finds it (its original first). Null with
    /// the reason when the link is off, the linker is not connected or the song is not in the list.
    /// </summary>
    string? GameSongId(out string? problem) { problem = T("LinkNotHere"); return null; }
    string? SetHidden(string id, bool hidden);
    /// <summary>Opens the song list window; true when it was confirmed (its changes are then applied and saved).</summary>
    bool OpenPlaylist();
    /// <summary>Rebuilds the custom map folder from the map folder; the app reports progress on its own.</summary>
    void Resync(IProgress<double> progress, Action<int> done);
    /// <summary>Brings the map folder in and reads the app folders again, as the main window's sync button does.</summary>
    void SyncMaps(IProgress<double> progress, Action<int> done);
    /// <summary>The titles of the notes written for the next version.</summary>
    IReadOnlyList<string> ReportTitles() => [];
    /// <summary>Those notes with the version they were written for and whether they are done (report -ls).</summary>
    IReadOnlyList<CmdNote> ReportNotes() => [];
    /// <summary>The lines of one note, null when there is none; <paramref name="problem"/> says why it could not be read.</summary>
    IReadOnlyList<string>? ReadReport(string title, out string? problem) { problem = T("NotesNotHere"); return null; }
    /// <summary>Writes a note; null when written, otherwise why not.</summary>
    string? WriteReport(string title, IReadOnlyList<string> lines) => T("NotesNotHere");
    /// <summary>Removes a note; true when one was there. <paramref name="problem"/> says why it could not be removed.</summary>
    bool DeleteReport(string title, out string? problem) { problem = T("NotesNotHere"); return false; }
    /// <summary>
    /// Writes where a note stands: "done" with this version, "asked" with what is still needed, or "open" to clear it.
    /// The reason is written in the language the settings are set to, since the user is who reads it. Null when it went through.
    /// </summary>
    string? MarkReport(string title, string state, string? why) => T("NotesNotHere");
    /// <summary>Gives a note another title; null when it went through, otherwise why not.</summary>
    string? RenameReport(string title, string name) => T("NotesNotHere");
    /// <summary>Removes every note already marked done and returns the titles that went.</summary>
    IReadOnlyList<string> ClearDoneReports() => [];
    /// <summary>Gives a song the name the list shows; an edit hands it to its original. Null when done, otherwise why not.</summary>
    string? RenameSong(string id, string name);
    /// <summary>Removes an edited song and keeps it for restore. Null on success, otherwise the reason.</summary>
    string? RemoveEdit(string id);
    /// <summary>The name of the song restore would bring back, or null.</summary>
    string? HeldSong { get; }
    /// <summary>Brings the kept song back. Null on success with <paramref name="slot"/> set, otherwise the reason.</summary>
    string? Restore(out int slot);
    /// <summary>Deletes a song for good. Null on success, otherwise the reason.</summary>
    string? DeleteSong(string id);
    /// <summary>Opens the editor for a song. Null on success, otherwise the reason it cannot start.</summary>
    string? EditSong(string id);
    /// <summary>Applies settings as the settings panel does: saved, and kept for rollback. Null when applied.</summary>
    string? ApplySettings(VisualizerSettings settings);
    /// <summary>The change rollback would undo next, or null.</summary>
    SettingsChange? LastChange { get; }
    /// <summary>Undoes <see cref="LastChange"/>. Null when done, otherwise the reason.</summary>
    string? RollBack();
    /// <summary>scannow: checks each area and says what is wrong in it. Nothing is changed.</summary>
    void ScanNow(IReadOnlyList<string> areas, IProgress<double> progress, Action<IReadOnlyList<ScanArea>> done) =>
        done(areas.Select(area => new ScanArea(area, [T("ScanNotHere")])).ToArray());

    /// <summary>install -reinstall linker: writes this version's linker into the game again, with the app still open.</summary>
    void ReinstallLinker(IProgress<double> progress, Action<IReadOnlyList<string>> done) => done([T("ReinstallNotHere")]);

    /// <summary>
    /// install -reinstall source and app: the app closes, update.ps1 puts the parts back and builds them, and opens it again
    /// (a running app holds its own files). Null when it is on its way, otherwise why not.
    /// </summary>
    string? ReinstallAndRestart(IReadOnlyList<string> parts) => T("ReinstallNotHere");
    /// <summary>restart linker: the link thrown away whole and opened again; the status lines that follow.</summary>
    IReadOnlyList<string> RestartLinker() => [T("RestartNotHere")];
    /// <summary>restart app: closes this app and starts the same build again; null when it is on its way, else why not.</summary>
    string? RestartApp() => T("RestartNotHere");

    /// <summary>
    /// update and version: asks the repository about the latest version, or <paramref name="version"/>, by running its own
    /// update.ps1 -Check. Nothing is changed.
    /// </summary>
    void CheckUpdate(string? version, IProgress<double> progress, Action<UpdateCheck> done) =>
        done(UpdateCheck.Failed(T("UpdateNotHere")));

    /// <summary>
    /// Why the app running is an older build than this copy's own files say, or null when it is not: its source version
    /// differs, or files changed after it was built. update then offers to build it again (user request).
    /// </summary>
    string? StaleBuild() => null;

    /// <summary>
    /// Takes it: the app closes and the script puts the version in place, builds it and starts it again, since a running
    /// app holds its own files. An installed linker is built and written with it (user rule). <paramref name="force"/>
    /// throws this copy's own changes away. Null when the update is on its way, otherwise why it could not start.
    /// </summary>
    string? ApplyUpdate(bool force, string? version) => T("UpdateNotHere");

    /// <summary>Asks for a folder, starting at <paramref name="start"/>; null when none was picked.</summary>
    string? PickFolder(string start);
}

/// <summary>Console transcript and command history, kept in memory while the app runs (never written to disk).</summary>
/// <summary>A note being written: what is in it and whether that differs from what is on disk.</summary>
public sealed record ReportDraft(string Title, IReadOnlyList<string> Lines, bool Changed, string Done = "");

public sealed class CmdLog
{
    private const int MaximumLines = 50_000;
    private const int MaximumHistory = 500;
    private readonly List<string> _lines = [];
    private readonly List<string> _history = [];

    public static CmdLog Shared { get; } = new();
    /// <summary>
    /// The note open in the console's editor, kept here with the transcript so closing and reopening the console comes
    /// back to it (user rule). Null when no note is open.
    /// </summary>
    public ReportDraft? Report { get; set; }
    public IReadOnlyList<string> Lines => _lines;
    public IReadOnlyList<string> History => _history;
    /// <summary>Whether the banner is on the transcript's record: clear keeps it away, exit starts the next console afresh.</summary>
    public bool Started { get; private set; }
    public event Action? Changed;

    public void Start(IEnumerable<string> banner)
    {
        Started = true;
        Write(banner);
    }

    /// <summary>Redraws the screen without adding a line: a progress bar moves this way.</summary>
    public void Refresh() => Changed?.Invoke();

    public void Write(IEnumerable<string> lines)
    {
        _lines.AddRange(lines);
        if (_lines.Count > MaximumLines) _lines.RemoveRange(0, _lines.Count - MaximumLines);
        Changed?.Invoke();
    }

    /// <summary>Adds to the last line, as an answer typed after a [Y/n] question.</summary>
    public void AppendToLast(string text)
    {
        if (_lines.Count == 0) _lines.Add(text);
        else _lines[^1] += text;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _lines.Clear();
        Changed?.Invoke();
    }

    /// <summary>Clears the screen and lets the next console show its banner again, as a new cmd window does.</summary>
    public void Reset()
    {
        Started = false;
        Clear();
    }

    internal void Remember(string command)
    {
        _history.Add(command);
        if (_history.Count > MaximumHistory) _history.RemoveAt(0);
    }
}

/// <summary>
/// A cmd-like console. It speaks the app's language (user rule): every line it prints comes from the console part of the
/// language files, while command, option and keyword names, setting paths, song titles and paths show as they are typed.
/// </summary>
public sealed class CmdSession
{
    public const string PromptText = "Visualizer>";
    /// <summary>How wide a progress bar is drawn, in characters.</summary>
    private const int BarWidth = 24;
    /// <summary>The language of reasons that come from the app's own messages (placement, settings, editor): the console's.</summary>
    public static string Language => ConsoleText.Language;
    private const string AnswerPrompt = "[Y/n]";
    /// <summary>The same question with a third answer that prints everything before deciding (user rule).</summary>
    private const string DetailPrompt = "[Y/n/detail]";

    /// <summary>
    /// What whoami prints. Only box-drawing and block characters, which the console's font draws
    /// as wide as a letter, so the lines stay aligned; the console is never narrower than the widest line.
    /// </summary>
    public static IReadOnlyList<string> Whoami { get; } =
    [
        "██╗   ██╗██╗███████╗██╗   ██╗ █████╗ ██╗     ██╗███████╗███████╗██████╗",
        "██║   ██║██║██╔════╝██║   ██║██╔══██╗██║     ██║╚══███╔╝██╔════╝██╔══██╗",
        "██║   ██║██║███████╗██║   ██║███████║██║     ██║  ███╔╝ █████╗  ██████╔╝",
        "╚██╗ ██╔╝██║╚════██║██║   ██║██╔══██║██║     ██║ ███╔╝  ██╔══╝  ██╔══██╗",
        " ╚████╔╝ ██║███████║╚██████╔╝██║  ██║███████╗██║███████╗███████╗██║  ██║",
        "  ╚═══╝  ╚═╝╚══════╝ ╚═════╝ ╚═╝  ╚═╝╚══════╝╚═╝╚══════╝╚══════╝╚═╝  ╚═╝"
    ];

    private readonly ICmdHost _host;
    private (string Question, Action<bool> Answer, Action? Detail)? _question;
    /// <summary>A numbered list waiting for its number (load); one slip is asked again (user rule).</summary>
    private Choice? _choice;

    private sealed record Choice(string Prompt, int Count, Action<int> Take, bool Retried);

    public CmdSession(ICmdHost host, CmdLog? log = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Log = log ?? CmdLog.Shared;
        Speak();
        if (!Log.Started) Log.Start([T("Banner", AppInfo.Version), T("BannerHint"), ""]);
    }

    /// <summary>
    /// The console speaks the app's language (user rule), read again before every line it handles so a language set a
    /// moment ago is the one the next answer comes in.
    /// </summary>
    private void Speak() => ConsoleText.Language = Languages.Resolve(_host.Language) ?? Languages.English;

    public CmdLog Log { get; }
    public bool IsAsking => _question is not null || _choice is not null;

    /// <summary>
    /// The long command running now and how far it has got (0..1), or null when none is. While one runs the screen shows a
    /// progress bar in place of the prompt and takes no input, the way a terminal does (user request).
    /// </summary>
    public (string Label, double Fraction)? Job { get; private set; }

    /// <summary>Whether a long command is running, so nothing may be typed yet.</summary>
    public bool Busy => Job is not null;

    /// <summary>The progress line drawn in place of the prompt: the label, a bar and the percentage.</summary>
    public string JobText()
    {
        if (Job is not { } job) return "";
        var filled = (int)Math.Round(Math.Clamp(job.Fraction, 0, 1) * BarWidth);
        return $"{job.Label}  [{new string('#', filled)}{new string('.', BarWidth - filled)}] "
            + $"{(int)Math.Round(Math.Clamp(job.Fraction, 0, 1) * 100),3}%";
    }

    /// <summary>A job's progress, passed on as it comes in.</summary>
    private sealed class JobSteps(Action<double> step) : IProgress<double>
    {
        public void Report(double value) => step(value);
    }

    /// <summary>
    /// Runs a long command: the bar replaces the prompt until the job reports it is done, and the finishing line is written
    /// then. A job that never calls back would leave the console waiting, so every host path ends in its done callback.
    /// </summary>
    private void RunJob(string label, Action<IProgress<double>, Action<IReadOnlyList<string>>> start)
    {
        if (Busy) { Write(T("JobRunning")); return; }
        Job = (label, 0);
        Log.Refresh();
        // Reported straight through, not through Progress<T>: the host already steps on the UI thread (its own
        // Progress<T> marshals there), and posting again would make the bar lag behind the job.
        var progress = new JobSteps(fraction =>
        {
            if (Job is { } running) Job = (running.Label, fraction);
            Log.Refresh();
        });
        start(progress, lines =>
        {
            Job = null;
            Write(lines.ToArray());
        });
    }
    /// <summary>
    /// The prompt for a stored prompt being filled in, which also counts the blank it is asking for. It is written in from
    /// the margin so the line never looks like the console's own (user rule).
    /// </summary>
    public const string FillPrompt = "    prompt:";

    /// <summary>"[Y/n]" or "Pick 1-n:" while a question waits for its answer, otherwise the command prompt.</summary>
    public string Prompt => _choice?.Prompt
        ?? (IsAsking ? (_question?.Detail is null ? AnswerPrompt : DetailPrompt)
            : _filling is { } filling ? $"{FillPrompt}{filling.Title} ({filling.At + 1}/{filling.Blanks().Count})>"
            : PromptText);
    public event Action? ExitRequested;

    public void Submit(string? input)
    {
        // Nothing is taken while a long command runs: the screen shows its bar in place of the prompt and types nothing,
        // so an input that gets here anyway is dropped rather than queued (user request).
        if (Busy) return;
        Speak();
        var line = (input ?? "").Trim();
        if (_choice is { } choice)
        {
            if (line.Length > 0) Log.AppendToLast(" " + line);
            // 01 and 1 are the same answer (user rule).
            if (int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= choice.Count)
            {
                _choice = null;
                choice.Take(number);
            }
            else if (!choice.Retried)
            {
                // The first slip may be a typo, so it is asked again; the second ends it.
                _choice = choice with { Retried = true };
                Log.Write([choice.Prompt]);
                return;
            }
            else
            {
                _choice = null;
                Write(T("Cancelled"));
            }
            if (!IsAsking) Log.Write([""]);
            return;
        }
        if (_question is { } question)
        {
            // Only an explicit answer counts: a blank Enter asks again like any other unclear answer (user rule).
            bool? yes = line.ToLowerInvariant() switch { "y" or "yes" => true, "n" or "no" => false, _ => null };
            var detail = question.Detail is not null && line.ToLowerInvariant() is "d" or "detail";
            if (line.Length > 0) Log.AppendToLast(" " + line);
            if (detail)
            {
                // Everything, in full, and then the same question again without the offer.
                question.Detail!();
                _question = (question.Question[..^DetailPrompt.Length] + AnswerPrompt, question.Answer, null);
                Log.Write([_question.Value.Question]);
                return;
            }
            if (yes is not bool answer)
            {
                Log.Write([question.Question]);
                return;
            }
            _question = null;
            question.Answer(answer);
            if (!IsAsking) Log.Write([""]);
            return;
        }

        Log.Write([(Prompt + " " + line).TrimEnd()]);
        // A blank being filled in takes the line as its value, or as one of the slash words.
        if (_filling is not null)
        {
            FillBlank(line);
            if (!IsAsking) Log.Write([""]);
            return;
        }
        if (line.Length == 0) return;
        Log.Remember(line);
        if (CmdCatalog.Parse(line, out var error) is not { } parsed)
        {
            Write(error ?? "");
            Log.Write([""]);
            return;
        }
        if (Run(parsed) && !IsAsking) Log.Write([""]);
    }

    /// <summary>Runs one parsed line; false for the commands that leave no blank line behind (clear, exit).</summary>
    /// <summary>
    /// Commands that change the settings, the loaded song or the map folders. The note editor holds one song's lanes while
    /// it is open, and all of these would move the ground under it, so they say so instead of running (user rule). The
    /// settings are not in this list: those commands close them first, after asking, which has been the rule all along.
    /// </summary>
    private static readonly HashSet<string> NotWhileEditing = new(StringComparer.Ordinal)
        { "load", "set", "speed", "autosave", "import", "reset", "rollback", "do", "sync", "remove", "restore", "delete", "retitle",
          "checkpoint", "go" };

    private bool Run(CmdLine line)
    {
        var copy = line.Has("-c");
        var yes = line.Has("-p");
        if (_host.EditorOpen && NotWhileEditing.Contains(line.Command.Name))
        {
            Write(T("EditorBlocks", line.Command.Name));
            return true;
        }
        switch (line.Command.Name)
        {
            case "copy" or "paste":
                CopyBack(line);
                break;
            case "report":
                OpenReport(line, copy);
                break;
            case "prompt":
                ShowPrompt(line, copy);
                break;
            case "update":
                if (NoArguments(line)) Update(line, yes);
                break;
            case "scannow":
                ScanNow(line, copy);
                break;
            case "clear":
                if (NoArguments(line)) Log.Clear();
                return false;
            case "exit":
                if (!NoArguments(line)) return true;
                ExitRequested?.Invoke();
                // -m keeps the screen for the next Ctrl+T, as closing the window does.
                if (!line.Has("-m")) Log.Reset();
                return false;
            case "whoami":
                Log.Write(Whoami);
                break;
            case "date":
                // This computer's clock, with the weekday in the console's language.
                if (NoArguments(line))
                    Show([DateTime.Now.ToString("yyyy-MM-dd dddd HH:mm:ss", DateCulture())], copy);
                break;
            case "cmd":
                if (NoArguments(line)) Show(CommandList(), copy);
                break;
            case "help":
                if (CommandsFor(line) is { } described)
                    Show(described.SelectMany(command => HelpOf(command).Append("")).SkipLast(1).ToArray(), copy);
                break;
            case "option":
                // With nothing or all, every option once; a command's own options need its name.
                // -k adds the keywords, which makes it keyword -o the other way round.
                if (IsEvery(line)) Show(AllOptions(line.Has("-k")), copy);
                else if (CommandsFor(line) is { } listed) Show(listed.SelectMany(command => OptionsOf(command, line.Has("-k"))).ToArray(), copy);
                break;
            case "keyword":
                // A word that is a keyword is described on its own; anything else is read as a command, as before.
                if (IsEvery(line)) Show(AllKeywords(line.Has("-o")), copy);
                else if (line.Arguments.Count == 1 && CmdCatalog.Keyword(line.Arguments[0]) is { } known)
                    Show(WordLines(line.Arguments[0].ToLowerInvariant(), known), copy);
                else if (CommandsFor(line) is { } named) Show(named.SelectMany(command => KeywordsOf(command, line.Has("-o"))).ToArray(), copy);
                break;
            case "version":
                if (!NoArguments(line)) break;
                if (line.Has("-number")) Show([AppInfo.Version], copy);
                else Version(copy, line.Has("-check"));
                break;
            case "fps":
                // Measured by the app on itself; nothing is read from the game or the system beyond its own frames.
                if (NoArguments(line)) Show(_host.FrameStats(line.Has("-r")), copy);
                break;
            case "link":
                // One way: the app only listens for the mod, and never sends anything into the game.
                Link(line, copy);
                break;
            case "log":
                ShowLog(line, copy);
                break;
            case "inspect":
                Inspect(line, copy);
                break;
            case "restart":
                Restart(line, copy);
                break;
            case "install":
                Install(line, yes);
                break;
            case "song":
                // -id and -tl print what id and title print for the loaded song.
                if (NoArguments(line) && LoadedSong() is { } loaded)
                    Show(line.Has("-id") ? [IdText(loaded)] : line.Has("-tl") ? [TitleText(loaded)] : Details(loaded), copy);
                break;
            case "info":
                if (SongFor(line) is { } song) Show(line.Has("-tl") ? [song.Title] : Details(song), copy);
                break;
            case "id":
                // title -id and id -tl print the same, in the other order; list is the wider one.
                ListSongs(line, "now", line.Has("-tl") ? SongColumn.IdTitle : SongColumn.Id, copy);
                break;
            case "retitle":
                Rename(line, yes);
                break;
            case "override":
                Override(line, yes);
                break;
            case "title":
                ListSongs(line, "now", line.Has("-id") ? SongColumn.TitleId : SongColumn.Title, copy);
                break;
            case "load":
                Load(line);
                break;
            case "playlist":
                // The song list, which used to be two commands: playlist main is what playlist printed on its own.
                ListSongs(line, "all", line.Has("-id") ? SongColumn.Id : line.Has("-title") ? SongColumn.Title : SongColumn.All, copy);
                break;
            case "checkpoint":
                Checkpoint(line, copy);
                break;
            case "go":
                Go(line);
                break;
            case "pin":
                Pin(line, copy, add: true);
                break;
            case "unpin":
                Pin(line, copy, add: false);
                break;
            case "redirect":
                Redirect(line, copy, yes);
                break;
            case "hide" or "show":
                SetHidden(line, line.Command.Name == "hide");
                break;
            case "edit":
                Edit(line, yes);
                break;
            case "note":
                Note(line, yes, copy);
                break;
            case "sync":
                // Adding is not a question; only the rebuild, which drops what the map folder no longer has, asks.
                var rebuild = line.Rest.Trim();
                if (rebuild.Length > 0 && !rebuild.Equals("rebuild", StringComparison.OrdinalIgnoreCase))
                {
                    Write(T("SyncTakes", rebuild));
                    break;
                }
                if (rebuild.Length == 0)
                {
                    RunJob(T("JobMapSync"), (progress, done) =>
                        _host.SyncMaps(progress, count => done([T("MapSyncDone", count)])));
                    break;
                }
                Confirm(yes, T("AskRebuild"), answer =>
                {
                    if (!answer) { Write(T("Cancelled")); return; }
                    RunJob(T("JobMapRebuild"), (progress, done) =>
                        _host.Resync(progress, count => done([T("MapRebuildDone", count)])));
                });
                break;
            case "remove":
                Remove(line, yes);
                break;
            case "restore":
                if (NoArguments(line)) Restore(yes);
                break;
            case "delete":
                Delete(line, yes);
                break;
            case "get":
                if (SettingFor(line, exact: true) is { } read) Write(SettingPaths.Format(read, _host.Settings));
                break;
            case "set":
                Set(line);
                break;
            case "speed":
                // -ar is the other speed a player means: how long the note takes to come down, not how fast the song runs.
                Shortcut(line, line.Has("-ar") ? "display.travel" : "timing.speed");
                break;
            case "autosave":
                Shortcut(line, "general.autosave");
                break;
            case "property":
                Property(line, copy);
                break;
            case "export":
                if (NoArguments(line)) Show(SettingsJson.ExportPortable(_host.Settings).Split('\n').Select(text => text.TrimEnd('\r')).ToArray(), copy);
                break;
            case "import":
                Import(line, yes);
                break;
            case "rollback":
                if (NoArguments(line)) RollBack(yes);
                break;
            case "reset":
                if (NoArguments(line)) Reset(yes);
                break;
            case "do":
                Do(line);
                break;
        }
        return true;
    }

    // ---- help ------------------------------------------------------------------------------------------------------

    private static IReadOnlyList<string> CommandList()
    {
        var lines = new List<string>();
        foreach (var group in CmdCatalog.Groups)
        {
            lines.Add(T("Group." + group));
            lines.AddRange(CmdCatalog.Visible.Where(command => command.Group == group).Select(command => $"  {command.Usage,-34}{command.Summary}"));
        }
        lines.Add("");
        // Short lines, so they fit the console's default width without wrapping.
        lines.Add(T("CmdHintOptions"));
        lines.Add(T("CmdHintId"));
        lines.Add(T("CmdHintString", string.Join(", ", CmdCatalog.SongGroups.SkipLast(1))));
        lines.Add(T("CmdHintTitle"));
        lines.Add(T("CmdHintHelp"));
        lines.Add(T("CmdHintTab"));
        return lines;
    }

    /// <summary>Whether the line asks for the whole listing: nothing typed, or the word that stands for it.</summary>
    private static bool IsEvery(CmdLine line) =>
        line.Arguments.Count == 0 || (line.Arguments.Count == 1 && line.Arguments[0].Equals("list", StringComparison.OrdinalIgnoreCase));

    /// <summary>A command's usage and what it does, then its options and keywords as lists.</summary>
    private static IEnumerable<string> HelpOf(CmdCommand command)
    {
        yield return command.Usage;
        yield return "  " + command.Help;
        if (command.OptionGroups.Count > 0)
        {
            yield return "  " + T("HelpOptions", command.OptionsText);
            foreach (var line in OptionLines(command)) yield return "  " + line;
        }
        if (command.KeywordNames.Count > 0)
        {
            // The default one stands in brackets, so it is seen rather than read about (user rule).
            yield return "  " + T("HelpKeywords", string.Join(", ", command.KeywordNames.Select(command.KeywordText)));
            foreach (var line in KeywordLines(command)) yield return "  " + line;
        }
    }

    private static IEnumerable<string> OptionLines(CmdCommand command)
    {
        // The long names are wider than the old short ones, so the column follows the widest of them.
        var width = command.OptionNames.Max(option => Shown(option).Length) + 2;
        return command.OptionNames.Select(option => "  " + Shown(option).PadRight(width) + CmdCatalog.OptionMeaning(command, option));

        static string Shown(string name) => CmdCatalog.Option(name) is { } known ? known.Option.Display : name;
    }

    private static IEnumerable<string> KeywordLines(CmdCommand command)
    {
        var width = command.KeywordNames.Max(keyword => command.KeywordText(keyword).Length) + 2;
        return command.KeywordNames.Select(keyword =>
            "  " + command.KeywordText(keyword).PadRight(width) + CmdCatalog.KeywordMeaning(command, keyword));
    }

    /// <summary>Every option and what it does; with -k, every keyword after them.</summary>
    private static IReadOnlyList<string> AllOptions(bool keywords)
    {
        var lines = CmdCatalog.OptionHelp.Select(option => $"  {option.Key,-11}{option.Value}").ToList();
        if (keywords) lines.AddRange(["", T("KeywordsHeading"), .. KeywordTable()]);
        lines.AddRange(["", keywords ? T("OptionsHintBoth") : T("OptionsHint")]);
        return lines;
    }

    /// <summary>Every keyword with the commands that take it; with -o, every option after them.</summary>
    private static IReadOnlyList<string> AllKeywords(bool options)
    {
        var lines = KeywordTable().ToList();
        if (options) lines.AddRange(["", T("OptionsHeading"), .. CmdCatalog.OptionHelp.Select(option => $"  {option.Key,-11}{option.Value}")]);
        lines.AddRange(["", options ? T("KeywordsHintBoth") : T("KeywordsHint")]);
        return lines;
    }

    private static IEnumerable<string> KeywordTable()
    {
        var keywords = CmdCatalog.AllKeywords;
        var width = keywords.Max(keyword => keyword.Keyword.Length) + 2;
        return keywords.Select(keyword => "  " + keyword.Keyword.PadRight(width) + string.Join(", ", keyword.Commands));
    }

    /// <summary>option &lt;cmd&gt;: the command's options, then with -k its keywords.</summary>
    private static IEnumerable<string> OptionsOf(CmdCommand command, bool keywords)
    {
        var lines = new List<string>();
        if (command.OptionGroups.Count == 0) lines.Add(T("NoOptions", command.Name));
        else lines.AddRange([$"{command.Name}: {command.OptionsText}", .. OptionLines(command)]);
        if (keywords) lines.AddRange(KeywordPart(command));
        return lines;
    }

    /// <summary>keyword &lt;cmd&gt;: the command's keywords, then with -o its options.</summary>
    private static IEnumerable<string> KeywordsOf(CmdCommand command, bool options)
    {
        var lines = KeywordPart(command).ToList();
        if (!options) return lines;
        if (command.OptionGroups.Count == 0) lines.Add(T("NoOptions", command.Name));
        else lines.AddRange([T("CommandOptions", command.Name, command.OptionsText), .. OptionLines(command)]);
        return lines;
    }

    /// <summary>One keyword on its own: what it means and the commands that take it, with a line each where they differ.</summary>
    private static IReadOnlyList<string> WordLines(string keyword, (string Meaning, IReadOnlyList<string> Commands) known)
    {
        var lines = new List<string> { T("KeywordOf", keyword, string.Join(", ", known.Commands)) };
        if (known.Meaning.Length > 0) lines.Add("  " + known.Meaning);
        foreach (var name in known.Commands)
        {
            if (CmdCatalog.Find(name) is not { } command) continue;
            var own = CmdCatalog.KeywordMeaning(command, keyword);
            if (own != known.Meaning) lines.Add($"  {name}: {own}");
        }
        return lines;
    }

    private static IEnumerable<string> KeywordPart(CmdCommand command) => command.KeywordNames.Count == 0 ? [T("NoKeywords", command.Name)]
        : [T("CommandKeywords", command.Name, string.Join(", ", command.KeywordNames.Select(command.KeywordText))), .. KeywordLines(command)];

    /// <summary>The commands a &lt;cmd&gt; names: all when left out, last for the one run before this.</summary>
    private IReadOnlyList<CmdCommand>? CommandsFor(CmdLine line)
    {
        if (line.Arguments.Count > 1) return Syntax<IReadOnlyList<CmdCommand>>(line);
        // list is the word for a whole listing everywhere in this console, and it is what a bare help means.
        var word = line.Arguments.Count == 0 ? "list" : line.Arguments[0].ToLowerInvariant();
        if (word == "list") return CmdCatalog.Visible.ToArray();
        if (word == "last")
        {
            // The line being run is already in the history; the one before it is what "last" means.
            var history = Log.History;
            if (history.Count < 2)
            {
                Write(T("NoCommandBefore"));
                return null;
            }
            word = CmdCatalog.Words(history[^2]).FirstOrDefault().Word ?? "";
        }
        if (CmdCatalog.Find(word) is { Hidden: false } command) return [command];
        if (word == "uninstall")
        {
            Write(CmdCatalog.UninstallNote);
            return null;
        }
        Write(T("NotACommandHere", word));
        return null;
    }

    // ---- songs -----------------------------------------------------------------------------------------------------

    private enum SongColumn { All, Id, Title, IdTitle, TitleId }

    private CmdSong? LoadedSong()
    {
        if (_host.LoadedId is { } id && Song(id) is { } song) return song;
        Write(_host.LoadedSongName is { Length: > 0 } name ? T("NotInListNoId", name) : T("NoSongLoaded"));
        return null;
    }

    /// <summary>The song the game is playing (user rule: only while the linker is on), or null with the reason written.</summary>
    private CmdSong? GameSong()
    {
        if (_host.GameSongId(out var problem) is { } id && Song(id) is { } song) return song;
        Write(problem ?? T("GameSongNotListed"));
        return null;
    }

    /// <summary>The song with this id; letters may be typed in either case.</summary>
    private CmdSong? Song(string id) => _host.Songs.FirstOrDefault(song => string.Equals(song.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The title without the slot the song list puts in front of an edit's ("2*Plum - R" → "Plum - R").</summary>
    private static string BaseTitle(CmdSong song) =>
        song.Slot is { } slot && song.Title.StartsWith(slot.ToString(CultureInfo.InvariantCulture) + "*", StringComparison.Ordinal)
            ? song.Title[(slot.ToString(CultureInfo.InvariantCulture).Length + 1)..] : song.Title;

    /// <summary>What id prints: the id, and an edit's slot.</summary>
    private static string IdText(CmdSong song) => song.Slot is { } slot ? T("WithSlot", song.Id, slot) : song.Id;

    /// <summary>What title prints: the title, and an edit's slot.</summary>
    private static string TitleText(CmdSong song) => song.Slot is { } slot ? T("WithSlot", BaseTitle(song), slot) : song.Title;

    /// <summary>The loaded song's original and its edits, the original first and the edits by slot.</summary>
    private IReadOnlyList<CmdSong> Family(CmdSong song) => _host.Songs.Where(each => each.Family == song.Family)
        .OrderBy(each => each.Slot ?? 0).ThenBy(each => each.Id, StringComparer.Ordinal).ToArray();

    private IReadOnlyList<CmdSong> EditsOf(CmdSong song) => Family(song).Where(each => each.Slot is not null).ToArray();

    /// <summary>The original a song was edited from; an original is its own.</summary>
    private CmdSong? OriginOf(CmdSong song) => song.Slot is null ? song : _host.Songs.FirstOrDefault(each => each.Family == song.Family && each.Slot is null);

    /// <summary>The song an &lt;id&gt; names: an eight-character id or now. There is no default.</summary>
    private CmdSong? SongFor(CmdLine line, string? fallback = null)
    {
        if (line.Arguments.Count == 0)
        {
            // Some commands take the loaded song when nothing is named; the rest say what they need.
            if (fallback is not null) return LoadedSong();
            Write(T("IdNeeded", line.Command.Usage));
            return null;
        }
        if (line.Arguments.Count > 1) return Syntax<CmdSong>(line);
        var id = line.Arguments[0];
        if (id.Equals("now", StringComparison.OrdinalIgnoreCase)) return LoadedSong();
        if (id.Equals("link", StringComparison.OrdinalIgnoreCase)) return GameSong();
        if (Song(id) is { } song) return song;
        Write(T("NoSongWithId", id));
        return null;
    }

    private static IReadOnlyList<string> Details(CmdSong song)
    {
        var entry = song.Entry;
        var timeline = entry.Timeline;
        var rows = new List<(string Name, string Value)>
        {
            (T("Row.id"), song.Id),
            (T("Row.title"), song.Title),
            (T("Row.artist"), entry.Info?.Artist is { Length: > 0 } artist ? artist : "-"),
            (T("Row.author"), entry.Info?.Author is { Length: > 0 } author ? author : "-"),
            (T("Row.kind"), song.Kind),
            (T("Row.length"), timeline is { HitTimesMs.Count: > 0 } timed ? Length(timed.HitTimesMs[^1]) : "-"),
            // Only the notes pressed; long notes and auto tiles have rows of their own, 0 included.
            (T("Row.notes"), timeline?.CountPressedHits().ToString(CultureInfo.InvariantCulture) ?? "-"),
            (T("Row.longnotes"), timeline is null ? "-"
                : (timeline.CountHits(HitKind.HoldStart) + timeline.CountHits(HitKind.HoldChain)).ToString(CultureInfo.InvariantCulture)),
            (T("Row.autonotes"), timeline?.CountHits(HitKind.Auto).ToString(CultureInfo.InvariantCulture) ?? "-"),
            (T("Row.difficulty"), entry.Info?.Difficulty?.ToString(CultureInfo.InvariantCulture) ?? "-"),
            (T("Row.checkpoints"), timeline is { CheckpointsVerified: true } marked ? marked.CheckpointCount.ToString(CultureInfo.InvariantCulture) : "-")
        };
        // Every song has the row: an edit's slot, or origin for the song it was made from.
        rows.Add((T("Row.slot"), song.Slot?.ToString(CultureInfo.InvariantCulture) ?? "origin"));
        rows.Add((T("Row.hidden"), song.Hidden ? T("Yes") : T("No")));
        rows.Add((T("Row.status"), entry.Status.ToString().ToLowerInvariant()));
        rows.Add((T("Row.path"), Path.Combine(entry.RootDirectory, entry.RelativePath)));
        // Hangul is twice as wide as a letter in the console's font, so the column is counted in letter widths.
        var width = rows.Max(row => Wide(row.Name)) + 2;
        return rows.Select(row => row.Name + new string(' ', width - Wide(row.Name)) + row.Value).ToArray();

        static string Length(double ms) => TimeSpan.FromMilliseconds(ms).ToString(ms >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The songs a &lt;string&gt; names: base, custom, edited or all by folder; main, the main
    /// window's list in its order; now, the loaded song; family, its original with every edit; origin, its original.
    /// </summary>
    private IReadOnlyList<CmdSong>? SongsFor(CmdLine line, string fallback)
    {
        if (line.Arguments.Count > 1) return Syntax<IReadOnlyList<CmdSong>>(line);
        var word = line.Arguments.Count == 0 ? fallback : line.Arguments[0].ToLowerInvariant();
        if (!CmdCatalog.SongGroups.Contains(word))
        {
            Write(T("UseSongWords", string.Join(", ", CmdCatalog.SongGroups.SkipLast(1))));
            return null;
        }
        switch (word)
        {
            case "main":
                return _host.CurrentIds.Select(Song).OfType<CmdSong>().ToArray();
            case "now":
                return LoadedSong() is { } loaded ? [loaded] : null;
            case "link":
                return GameSong() is { } played ? [played] : null;
            case "family":
                return LoadedSong() is { } song ? Family(song) : null;
            case "origin":
                if (LoadedSong() is not { } edit) return null;
                if (OriginOf(edit) is { } origin) return [origin];
                Write(T("OriginalNotListed", edit.Title));
                return null;
        }
        IEnumerable<CmdSong> songs = _host.Songs;
        if (word != "all") songs = songs.Where(each => each.Kind == word);
        return songs.OrderBy(each => each.Id, StringComparer.Ordinal).ToArray();
    }

    private void ListSongs(CmdLine line, string fallback, SongColumn column, bool copy)
    {
        if (SongsFor(line, fallback) is not { } songs) return;
        if (line.Has("-h")) songs = songs.Where(song => song.Hidden).ToArray();
        if (line.Has("-s")) songs = songs.Where(song => !song.Hidden).ToArray();
        if (songs.Count == 0)
        {
            Write(T("NoSongsMatch"));
            return;
        }
        Show(songs.Select(song => column switch
        {
            SongColumn.Id => IdText(song),
            SongColumn.Title => TitleText(song),
            SongColumn.IdTitle => $"{song.Id}  {TitleText(song)}",
            SongColumn.TitleId => $"{TitleText(song)}  {song.Id}",
            _ => Row(song)
        }).ToArray(), copy);
    }

    /// <summary>A song as list shows it: id, folder and title as the song list names it.</summary>
    private static string Row(CmdSong song) => $"{song.Id}  {song.Kind,-6}  {song.Title}{(song.Hidden ? "  " + T("HiddenMark") : "")}";

    // ---- load ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// load. A base or custom song by any part of its title, each with its edits under it, listed to
    /// pick from unless only one song comes up; an edit by its full title (1*Plum - R); origin and edited from the loaded
    /// song; -id by the whole id, which a title that matches nothing also tries. -og keeps to originals, -sl to edits and
    /// -sl&lt;n&gt; to one slot. -sm keeps the practice speed and -sr puts it back to 100%. -force loads an unsupported song anyway.
    /// </summary>
    private void Load(CmdLine line)
    {
        var text = line.Rest.Trim();
        bool? keepSpeed = line.Has("-sm") ? true : line.Has("-sr") ? false : null;
        var force = line.Has("-force");
        if (text.Length == 0)
        {
            Write(T("TitleNeeded"), T("Usage", line.Command.Usage));
            return;
        }
        // "." is the loaded song, so it can be loaded again or taken with an option; a title is never just a dot.
        if (text == ".")
        {
            if (LoadedSong() is not { } current) return;
            if (line.Has("-origin") || line.Has("-slot") || line.Number is not null)
                text = BaseTitle(current);
            else
            {
                LoadOne(current, keepSpeed, force);
                return;
            }
        }
        // "link" is the song the game is playing (user rule), its original as link auto takes it; an option picks from its family.
        if (text.Equals("link", StringComparison.OrdinalIgnoreCase))
        {
            if (GameSong() is not { } played) return;
            if (line.Has("-origin") || line.Has("-slot") || line.Number is not null)
                text = BaseTitle(played);
            else
            {
                LoadOne(played, keepSpeed, force);
                return;
            }
        }
        // "*" is every song, which is the empty text every title contains.
        if (text == "*") text = "";
        if (line.Has("-id"))
        {
            if (line.Arguments.Count > 1) Write(T("IdTakesOne"));
            else if (Song(text) is { } byId) LoadOne(byId, keepSpeed, force);
            else Write(T("NoSongWithFullId", text));
            return;
        }
        var slot = line.Number;
        var editsOnly = line.Has("-sl") || slot is not null;
        if (text.ToLowerInvariant() is "origin" or "edited")
        {
            LoadRelative(text.ToLowerInvariant(), line, slot, keepSpeed, force);
            return;
        }
        // An edit by its full title, slot and all; Tab does not offer these (user rule).
        if (!line.Has("-og") && _host.Songs.FirstOrDefault(song => song.Slot is { } own && (slot is null || own == slot)
                && string.Equals(song.Title, text, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            LoadOne(exact, keepSpeed, force);
            return;
        }
        var withHidden = line.Has("-h");
        IReadOnlyList<CmdSong> matches = editsOnly
            ? _host.Songs.Where(song => song.Slot is { } own && (slot is null || own == slot)
                    && (withHidden || !song.Hidden) && Has(BaseTitle(song), text))
                .OrderBy(BaseTitle, StringComparer.CurrentCultureIgnoreCase).ThenBy(song => song.Slot).ThenBy(song => song.Id, StringComparer.Ordinal).ToArray()
            : Originals(text, withHidden).SelectMany(origin => line.Has("-og") ? new[] { origin }
                : EditsOf(origin).Where(edit => withHidden || !edit.Hidden).Prepend(origin)).ToArray();
        if (matches.Count == 0)
        {
            // Nothing by title: a whole id is tried next (user rule).
            var id = SongIds.LooksLikeId(text);
            if (id && Song(text) is { } byId)
            {
                LoadOne(byId, keepSpeed, force);
                return;
            }
            Write(T(id ? editsOnly ? "NoEditTitleOrId" : "NoSongTitleOrId" : editsOnly ? "NoEditTitle" : "NoSongTitle", text));
            return;
        }
        if (matches.Count == 1)
        {
            LoadOne(matches[0], keepSpeed, force);
            return;
        }
        if (matches.Count > CmdCatalog.MaximumPicks)
        {
            Write(T("TooManyMatches", matches.Count));
            return;
        }
        AskChoice(matches.Select(Row).ToArray(), number => LoadOne(matches[number - 1], keepSpeed, force));
    }

    /// <summary>
    /// Base and custom songs whose title has the text anywhere, by title. Songs hidden from the song list are left out
    /// unless -h asks for them (user rule): load is about playing, and a hidden song was put away on purpose.
    /// </summary>
    private IEnumerable<CmdSong> Originals(string text, bool withHidden) =>
        _host.Songs.Where(song => song.Slot is null && (withHidden || !song.Hidden) && Has(song.Title, text))
            .OrderBy(song => song.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(song => song.Id, StringComparer.Ordinal);

    private static bool Has(string title, string text) => title.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>load origin and load edited: the loaded song's original, or the edits of the loaded original.</summary>
    private void LoadRelative(string keyword, CmdLine line, int? slot, bool? keepSpeed, bool force)
    {
        if (LoadedSong() is not { } loaded) return;
        var edits = EditsOf(loaded);
        if (keyword == "origin")
        {
            if (line.Has("-sl") || slot is not null)
            {
                Write(T("SlotGoesWithEdited"));
                return;
            }
            if (loaded.Slot is null)
            {
                // Pointing the other way helps only when there is an edit to go to (user rule).
                Write(T(edits.Count > 0 ? "AlreadyOriginalWithEdits" : "AlreadyOriginal", loaded.Title));
                return;
            }
            if (OriginOf(loaded) is { } origin) LoadOne(origin, keepSpeed, force);
            else Write(T("OriginalNotListed", loaded.Title));
            return;
        }
        if (line.Has("-og"))
        {
            Write(T("OriginGoesWithOrigin"));
            return;
        }
        if (loaded.Slot is not null)
        {
            Write(T("IsAnEdit", loaded.Title));
            return;
        }
        if (edits.Count == 0)
        {
            Write(T("HasNoEdits", loaded.Title));
            return;
        }
        if (slot is not null)
        {
            if (edits.FirstOrDefault(edit => edit.Slot == slot) is { } inSlot) LoadOne(inSlot, keepSpeed, force);
            else Write(T("NoEditInSlot", loaded.Title, slot));
            return;
        }
        if (edits.Count == 1) LoadOne(edits[0], keepSpeed, force);
        else AskChoice(edits.Select(Row).ToArray(), number => LoadOne(edits[number - 1], keepSpeed, force));
    }

    /// <summary>
    /// Loads one song after the settings are closed, and says when the practice speed went back to 100%. With -force an
    /// unsupported song is read as an older verified format version after a [Y/n], always asked since it is risky (user rule);
    /// any other song loads as it is.
    /// </summary>
    private void LoadOne(CmdSong song, bool? keepSpeed, bool force = false)
    {
        if (force && song.Entry.Status == MapParseStatus.Unsupported)
        {
            Confirm(false, T("AskForce", song.Title),
                answer =>
                {
                    if (answer) LoadWith(song, () => _host.ForceLoadSongById(song.Id, keepSpeed), forced: true);
                    else Write(T("Cancelled"));
                });
            return;
        }
        if (force) Write(T("ForceNotNeeded", song.Title));
        LoadWith(song, () => _host.LoadSongById(song.Id, keepSpeed), forced: false);
    }

    private void LoadWith(CmdSong song, Func<string?> load, bool forced)
    {
        WithSettingsClosed(false, () =>
        {
            var speed = SettingPaths.Find("timing.speed")!;
            var before = SettingPaths.Format(speed, _host.Settings);
            if (load() is { } reason)
            {
                Write($"{song.Id} {song.Title}: {reason}");
                return;
            }
            Write(T("Loaded", song.Id, song.Title));
            if (forced && _host.Songs.FirstOrDefault(listed => listed.Id == song.Id)?.Entry.ForcedFormatVersion is { } format)
                Write(T("ForcedAs", format));
            if (SettingPaths.Format(speed, _host.Settings) is var after && after != before)
                Write(T("SpeedReset", speed.Name, before, after));
        });
    }

    /// <summary>Numbers the lines 01, 02, ... and waits for one of them (user rule).</summary>
    private void AskChoice(IReadOnlyList<string> lines, Action<int> take)
    {
        Log.Write(lines.Select((text, index) => (index + 1).ToString("00", CultureInfo.InvariantCulture) + "  " + text).ToArray());
        var prompt = T("Pick", lines.Count);
        _choice = new Choice(prompt, lines.Count, take, false);
        Log.Write([prompt]);
    }

    /// <summary>
    /// checkpoint: where the loaded song starts. The numbers are the ones the main window shows ("checkpoint 3"), while the
    /// timeline counts the song's beginning as 1, so a typed number is one more inside.
    /// </summary>
    private void Checkpoint(CmdLine line, bool copy)
    {
        if (_host.Timeline is not { } timeline) { Write(T("NoSongLoaded")); return; }
        var song = _host.LoadedSongName ?? T("TheLoadedSong");
        var marks = timeline.CheckpointsVerified ? timeline.Checkpoints : [];
        if (marks.Count <= 1) { Write(T("NoCheckpoints", song)); return; }
        string Name(int number) => number <= 1 ? T("TheBeginning")
            : marks.FirstOrDefault(mark => mark.Number == number) is { SourceFloor: int floor }
                ? T("CheckpointAtTile", number - 1, floor) : T("CheckpointNumber", number - 1);
        var word = line.Rest.Trim();
        // Listing is all everywhere in this console; -list says the same thing (user rule: two ways, one meaning).
        if (word.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            Show([.. marks.Select(mark => (mark.Number == _host.Checkpoint ? "> " : "  ") + Name(mark.Number))], copy);
            return;
        }
        if (word.Length == 0) { Show([T("StartsAt", song, Name(_host.Checkpoint), marks.Count - 1)], copy); return; }
        // The ends wrap around, as the main window's arrows do (user rule).
        var wanted = word.ToLowerInvariant() switch
        {
            "first" => 1,
            "last" => marks.Count,
            "next" => _host.Checkpoint % marks.Count + 1,
            "previous" => (_host.Checkpoint + marks.Count - 2) % marks.Count + 1,
            _ => int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var typed) && typed >= 1 ? typed + 1 : -1
        };
        if (wanted < 0)
        {
            Write(T("NotACheckpoint", word, marks.Count - 1));
            return;
        }
        if (wanted > marks.Count) { Write(T("HasCheckpoints", song, marks.Count - 1)); return; }
        WithSettingsClosed(false, () =>
        {
            if (_host.MoveCheckpoint(wanted) is { } reason) Write(reason);
            else Show([T("NowStartsAt", song, Name(wanted))], copy);
        });
    }

    /// <summary>
    /// Why a tile cannot be started from, or null when it can: the checks ParsedTimeline.GetStartIndexForTile makes, said in
    /// the console's words. go and pin both use it, so a pin is always a tile go can take.
    /// </summary>
    public static string? TileProblem(ParsedTimeline timeline, int tile, string song)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.HitFloors is not { Count: > 0 } floors) return T("NoTileNumbers");
        if (tile >= floors[^1]) return T("TileRange", song, floors[^1] - 1);
        var index = floors.ToList().BinarySearch(tile);
        if (index < 0) return T("MidspinTile", tile);
        // Play begins at the hit after that tile; if that one only releases a long note, there is nothing to press first.
        if (index + 1 < floors.Count && ParsedTimeline.Closes(timeline.KindOf(index + 1)))
            return T("InsideLongNote", tile);
        return null;
    }

    /// <summary>
    /// pin and unpin: tiles marked for go (user request). A pin belongs to the song's family, the original and every edit
    /// of it, so setting it on any of them sets it on all (user rule); the tiles are kept in order, as checkpoints are.
    /// </summary>
    private void Pin(CmdLine line, bool copy, bool add)
    {
        if (line.Arguments.Count is 0 or > 2)
        {
            Write(add ? T("PinTakes") : T("UnpinTakes"), T("Usage", line.Command.Usage));
            return;
        }
        var named = line.Arguments.Count == 2 ? line.Arguments[0] : "now";
        CmdSong? song;
        if (named.Equals("now", StringComparison.OrdinalIgnoreCase)) song = LoadedSong();
        else if ((song = Song(named)) is null) Write(T("NoSongWithId", named));
        if (song is null) return;
        var title = BaseTitle(song);
        var pins = _host.PinsOf(song.Family);
        var word = line.Arguments[^1];
        if (add && word.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (pins.Count == 0) Write(T("NoPins", title));
            else Show([.. pins.Select((tile, at) => T("PinLine", at + 1, tile))], copy);
            return;
        }
        if (!int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var tile) || tile < 1)
        {
            Write(T("NotATile", word));
            return;
        }
        if (!add)
        {
            if (!pins.Contains(tile)) { Write(T("NotPinned", tile, title)); return; }
            if (_host.SetPins(song.Family, pins.Where(each => each != tile).ToArray()) is { } failed) { Write(failed); return; }
            Write(T("Unpinned", tile, title));
            return;
        }
        if (pins.Contains(tile)) { Write(T("AlreadyPinned", tile, title)); return; }
        if (song.Entry.Status != MapParseStatus.Supported || song.Entry.Timeline is not { } timeline)
        {
            Write(T("NoTilesToPin", song.Title));
            return;
        }
        if (TileProblem(timeline, tile, title) is { } problem) { Write(problem); return; }
        var next = pins.Append(tile).Order().ToArray();
        if (_host.SetPins(song.Family, next) is { } reason) { Write(reason); return; }
        Write(T("Pinned", tile, title, Array.IndexOf(next, tile) + 1, next.Length));
    }

    /// <summary>The pins of the song a line names by id, or of the loaded song, as Tab offers them.</summary>
    private IEnumerable<string> PinWords(string? id)
    {
        var song = id is null || id.Equals("now", StringComparison.OrdinalIgnoreCase)
            ? _host.LoadedId is { } loaded ? Song(loaded) : null
            : Song(id);
        return song is null ? [] : _host.PinsOf(song.Family).Select(tile => tile.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// redirect (user request): the song link auto loads in place of the one the game plays. Two ids name the song and the
    /// one put in its place; one id puts it in place of the loaded song (there is no now: ids only). A redirect belongs to
    /// the song's family, as a pin does, since the game plays an original's map for every edit of it (user decision); the
    /// song put in its place is that one song, an edit's slot included. list (the default) lists them all with the loaded
    /// song's marked, go loads the loaded song's, erase takes the loaded song's away (another song's by its id, all of
    /// them with all). Only link auto follows them: nothing else loads differently.
    /// </summary>
    private void Redirect(CmdLine line, bool copy, bool yes)
    {
        var words = line.Arguments;
        var first = words.Count == 0 ? "list" : words[0].ToLowerInvariant();
        if (first is "list" or "go" or "erase" or "all" or "*")
        {
            var most = first == "erase" ? 2 : 1;
            if (first is "all" or "*" || words.Count > most)
            {
                Write(T("RedirectTakes"), T("Usage", line.Command.Usage));
                return;
            }
            if (first == "list") ListRedirects(copy);
            else if (first == "go") GoRedirect();
            else EraseRedirect(words.Count > 1 ? words[1] : null, yes);
            return;
        }
        if (words.Count > 2)
        {
            Write(T("RedirectTakes"), T("Usage", line.Command.Usage));
            return;
        }
        if (words.Any(IsSongWord)) { Write(T("RedirectIdsOnly")); return; }
        CmdSong? from;
        if (words.Count == 1) from = LoadedSong();
        else if ((from = Song(words[0])) is null) Write(T("NoSongWithId", words[0]));
        if (from is null) return;
        if (Song(words[^1]) is not { } to)
        {
            Write(T("NoSongWithId", words[^1]));
            return;
        }
        var title = FamilyTitle(from.Family);
        // Its own original is what link auto loads for it anyway; an edit of it is a song of its own to put in its place.
        if (to.Family == from.Family && to.Slot is null) { Write(T("RedirectToItself", title)); return; }
        if (to.Entry.Status != MapParseStatus.Supported) { Write(T("RedirectUnsupported", to.Title)); return; }
        var redirects = new Dictionary<string, string>(_host.Redirects, StringComparer.Ordinal);
        var before = redirects.TryGetValue(from.Family, out var was) ? was : null;
        if (string.Equals(before, to.Id, StringComparison.OrdinalIgnoreCase)) { Write(T("RedirectAlready", title, to.Id, to.Title)); return; }
        redirects[from.Family] = to.Id;
        if (_host.SetRedirects(redirects) is { } reason) { Write(reason); return; }
        Write(T("RedirectSet", title, to.Id, to.Title));
        if (before is not null) Write(T("RedirectReplaced", SongText(before)));
    }

    /// <summary>now and link name songs elsewhere; redirect takes ids only (user rule).</summary>
    private static bool IsSongWord(string word) => word.Equals("now", StringComparison.OrdinalIgnoreCase)
        || word.Equals("link", StringComparison.OrdinalIgnoreCase);

    /// <summary>redirect list: every redirect, the loaded song's marked as checkpoint list marks the one in use.</summary>
    private void ListRedirects(bool copy)
    {
        var redirects = _host.Redirects;
        if (redirects.Count == 0) { Write(T("NoRedirects")); return; }
        var loaded = _host.LoadedId is { } id ? Song(id)?.Family : null;
        Show(redirects.Select(pair => (pair.Key, From: FamilySong(pair.Key), To: pair.Value))
            .OrderBy(each => each.From is { } song ? BaseTitle(song) : each.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(each => (each.Key == loaded ? "> " : "  ")
                + T("RedirectLine", each.From is { } song ? $"{song.Id} {BaseTitle(song)}" : each.Key, SongText(each.To)))
            .ToArray(), copy);
    }

    /// <summary>redirect go: loads the song the loaded song's redirect names, when it has one; otherwise nothing moves.</summary>
    private void GoRedirect()
    {
        // It loads a song, which the open note editor would lose its ground to.
        if (_host.EditorOpen) { Write(T("EditorBlocks", "redirect go")); return; }
        if (LoadedSong() is not { } loaded) return;
        if (!_host.Redirects.TryGetValue(loaded.Family, out var id)) { Write(T("NoRedirect", FamilyTitle(loaded.Family))); return; }
        if (Song(id) is not { } to) { Write(T("RedirectTargetGone", FamilyTitle(loaded.Family), id)); return; }
        if (to.Id.Equals(loaded.Id, StringComparison.OrdinalIgnoreCase)) { Write(T("RedirectLoadedAlready", to.Title)); return; }
        LoadOne(to, null);
    }

    /// <summary>redirect erase [&lt;id&gt; | all]: the loaded song's redirect, another song's by its id, or every one after a [Y/n].</summary>
    private void EraseRedirect(string? named, bool yes)
    {
        var redirects = new Dictionary<string, string>(_host.Redirects, StringComparer.Ordinal);
        if (named is not null && (named.Equals("all", StringComparison.OrdinalIgnoreCase) || named == "*"))
        {
            if (redirects.Count == 0) { Write(T("NoRedirects")); return; }
            var count = redirects.Count;
            Confirm(yes, T("AskEraseRedirects", count), answer =>
            {
                if (!answer) { Write(T("Cancelled")); return; }
                if (_host.SetRedirects(new Dictionary<string, string>(StringComparer.Ordinal)) is { } failed) { Write(failed); return; }
                Write(T("RedirectsErased", count));
            });
            return;
        }
        if (named is not null && IsSongWord(named)) { Write(T("RedirectIdsOnly")); return; }
        CmdSong? song;
        if (named is null) song = LoadedSong();
        else if ((song = Song(named)) is null) Write(T("NoSongWithId", named));
        if (song is null) return;
        var title = FamilyTitle(song.Family);
        if (!redirects.Remove(song.Family)) { Write(T("NoRedirect", title)); return; }
        if (_host.SetRedirects(redirects) is { } reason) { Write(reason); return; }
        Write(T("RedirectErased", title));
    }

    /// <summary>The original of a family, else its first edit; null when the list has none of it any more.</summary>
    private CmdSong? FamilySong(string family) => _host.Songs.Where(song => song.Family == family)
        .OrderBy(song => song.Slot ?? 0).ThenBy(song => song.Id, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>A family by its song's title, as pins name it.</summary>
    private string FamilyTitle(string family) => FamilySong(family) is { } song ? BaseTitle(song) : family;

    /// <summary>A redirected song as id and title, or the id and that the list no longer has it.</summary>
    private string SongText(string id) => Song(id) is { } song ? $"{song.Id} {song.Title}" : T("RedirectGone", id);

    /// <summary>go: the practice start, by tile number. Whole tiles only, and only ones this song presses.</summary>
    private void Go(CmdLine line)
    {
        var word = line.Rest.Trim();
        if (word.Length == 0) { Write(T("GoTakes")); return; }
        if (!int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var tile) || tile < 1)
        {
            Write(T("NotATile", word));
            return;
        }
        WithSettingsClosed(false, () =>
            Write(_host.StartAtTile(tile) is { } reason ? reason : T("StartingFromTile", tile)));
    }

    /// <summary>
    /// update: looks first and says what it found, and only then asks. Taking it closes the app, so nothing may happen
    /// behind the user's back (user rule for anything that reaches out).
    /// </summary>
    /// <summary>
    /// version: the version at once (user rule). -check adds when this copy was applied and built and asks the repository
    /// whether it is the latest, which is what takes the time.
    /// </summary>
    private void Version(bool copy, bool check)
    {
        if (!check)
        {
            Show([T("VersionLine", AppInfo.Version)], copy);
            return;
        }
        IReadOnlyList<string> about = [T("VersionLine", AppInfo.Version), T("VersionApplied", AppInfo.VersionDate), T("VersionBuilt", AppInfo.BuildTime)];
        RunJob(T("JobVersion"), (progress, done) => _host.CheckUpdate(null, progress, check =>
        {
            var latest = check.Problems.Count > 0 ? T("LatestUnknown", check.Problems[0])
                : check.Behind == 0 ? _host.StaleBuild() is null ? T("LatestYes") : T("LatestStale") : T("LatestNo", check.Target);
            done([]);
            Show([.. about, latest], copy);
        }));
    }

    /// <summary>
    /// update: looks first and says what it found. The latest version is taken after a yes (user rule); one this copy's
    /// own changes stand in the way of needs -force, and so does any other version than the latest, since -force is what
    /// does what would be refused. -force always asks and says the risk is the user's.
    /// </summary>
    private void Update(CmdLine line, bool yes)
    {
        var force = line.Has("-force");
        var asked = line.Options.TryGetValue("-version", out var value) ? value : "latest";
        if (!force && asked != "latest")
        {
            Write(T("ChosenNeedsForce", asked));
            return;
        }
        var linker = _host.FindLinker().Installed is not null;
        var along = linker ? T("LinkerAlong") : "";
        RunJob(T("JobUpdate"), (progress, done) => _host.CheckUpdate(asked == "latest" ? null : asked, progress, check =>
        {
            if (check.Problems.Count > 0) { done(check.Problems); return; }
            if (!check.Found) { done([T("NoSuchVersion", asked)]); return; }
            if (!force)
            {
                if (check.Behind == 0)
                {
                    // Nothing newer on the repository, yet the app running may be an older build of this very copy: the copy
                    // the code is written in gets a new build only this way (user request), the way install -reinstall app does.
                    if (_host.StaleBuild() is not { } stale) { done([T("AlreadyLatest", check.Mine)]); return; }
                    done([T("LatestButStale", check.Mine, stale)]);
                    // An installed linker is written again from this copy's plugins\linker, as the plain update does. The copy
                    // found beside the running app belongs to that older build, so comparing with it said "the same version"
                    // and left the game on the old linker (user report: the game kept linker 1.1.2 after the rebuild to 1.1.3).
                    var withLinker = _host.FindLinker().Installed is not null;
                    Confirm(yes, T("AskRebuildApp", check.Changed + check.Unpublished > 0 ? T("RebuildOwnChanges", Own(check)) : "",
                        withLinker ? T("LinkerAlong") : ""), answer =>
                    {
                        if (!answer) { Write(T("CancelledStays")); return; }
                        Write(_host.ReinstallAndRestart(withLinker ? ["app", "linker"] : ["app"]) is { } reason ? reason : T("ClosingToRebuild"));
                    });
                    return;
                }
                if (check.Changed > 0 || check.Unpublished > 0)
                {
                    done([T("WaitingButOwn", check.Target, Own(check)), T("ForceTakesIt")]);
                    return;
                }
                done([T("VersionWaiting", check.Target, check.Mine)]);
                Confirm(yes, T("AskUpdate", check.Target, along), answer =>
                {
                    if (!answer) { Write(T("CancelledStays")); return; }
                    Write(_host.ApplyUpdate(false, null) is { } reason ? reason : T("ClosingToUpdate"));
                });
                return;
            }
            if (check.Same && check.Changed == 0)
            {
                done([T("AlreadyThat", check.Target)]);
                return;
            }
            done([]);
            // -force always asks (user rule): the parser keeps -proceed away from it, so this question is never skipped.
            var lost = check.Changed > 0 || check.Unpublished > 0 ? T("OwnLost", Own(check)) : "";
            Confirm(false, T("AskForceUpdate", check.Target, lost, along), answer =>
            {
                if (!answer) { Write(T("CancelledStays")); return; }
                Write(_host.ApplyUpdate(true, asked == "latest" ? null : asked) is { } reason ? reason : T("ClosingToGo", check.Target));
            });
        }));

        static string Own(UpdateCheck check) => string.Join(T("And"), new[]
        {
            check.Changed > 0 ? T("ChangedFiles", check.Changed) : null,
            check.Unpublished > 0 ? T("UnpublishedCommits", check.Unpublished) : null
        }.OfType<string>());
    }

    private void SetHidden(CmdLine line, bool hide)
    {
        if (SongFor(line) is not { } song) return;
        if (song.Hidden == hide)
        {
            Write(T(hide ? "AlreadyHidden" : "AlreadyShown", song.Id, song.Title));
            return;
        }
        WithSettingsClosed(false, () =>
            Write(_host.SetHidden(song.Id, hide) is { } reason ? reason : T(hide ? "HiddenSong" : "ShownSong", song.Id, song.Title)));
    }

    private void Edit(CmdLine line, bool yes)
    {
        // Nothing named means the loaded song, which is what edit is nearly always for.
        if (SongFor(line, fallback: "now") is not { } song) return;
        WithSettingsClosed(yes, () =>
        {
            Write(T("OpeningEditor", song.Title));
            if (_host.EditSong(song.Id) is { } reason) Write(reason);
        });
    }

    /// <summary>
    /// Renames the loaded song for the list, as the song list window does. An edit keeps its slot prefix, which is not part
    /// of the name, and both names are shown before the question so nothing is replaced by surprise (user rule).
    /// </summary>
    private void Rename(CmdLine line, bool yes)
    {
        if (LoadedSong() is not { } song) return;
        var wanted = line.Rest.Trim();
        if (wanted.Length == 0)
        {
            Write(T("NameNeeded"), T("Usage", line.Command.Usage));
            return;
        }
        if (!wanted.Any(char.IsLetterOrDigit))
        {
            Write(T("NameNeedsLetter"));
            return;
        }
        // The slot prefix is how the list marks an edit, so it is kept whatever is typed.
        var prefix = song.Slot is { } slot ? slot + "*" : "";
        if (wanted.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > 0) wanted = wanted[prefix.Length..];
        var next = prefix + wanted;
        if (string.Equals(next, song.Title, StringComparison.Ordinal))
        {
            Write(T("AlreadyItsName", song.Title));
            return;
        }
        var family = song.Slot is null ? "" : T("RenameFamily");
        Confirm(yes, T("AskRename", song.Title, next) + family, answer =>
        {
            if (!answer) { Write(T("Cancelled")); return; }
            if (_host.RenameSong(song.Id, wanted) is { } reason) { Write(reason); return; }
            Write(T("Renamed", song.Id, next));
        });
    }

    /// <summary>
    /// override: hands the JSON to the open editor. It only reads note numbers and lanes; a time that is there has to
    /// match the note's own, which is how a wrong or shifted print is caught (user rule).
    /// </summary>
    private void Override(CmdLine line, bool yes)
    {
        if (!_host.EditorOpen)
        {
            Write(T("NothingToOverride"), T("OpenWithEdit"));
            return;
        }
        var json = line.Rest.Trim();
        if (json.Length == 0)
        {
            Write(T("JsonNeeded"), T("Usage", line.Command.Usage), T("NoteJsonShape"));
            return;
        }
        Confirm(yes, T("AskOverride"), answer =>
        {
            if (!answer) { Write(T("Cancelled")); return; }
            Write(_host.OverrideNotes(json) is { } reason ? T("NothingChanged", reason) : T("LanesInEditor"));
        });
    }

    private void Remove(CmdLine line, bool yes)
    {
        if (SongFor(line) is not { } song) return;
        if (song.Kind != "edited")
        {
            Write(T("OnlyEditsRemoved"));
            return;
        }
        var note = _host.HeldSong is { } held ? T("RestoreInstead", held) : T("RestoreCan");
        Confirm(yes, T("AskRemove", song.Title, note), answer =>
        {
            if (!answer) { Write(T("Cancelled")); return; }
            Write(_host.RemoveEdit(song.Id) is { } reason ? reason : T("Removed", song.Id, song.Title));
        });
    }

    private void Restore(bool yes)
    {
        if (_host.HeldSong is not { } held)
        {
            Write(T("NothingToRestore"));
            return;
        }
        Confirm(yes, T("AskRestore", held), answer =>
        {
            if (!answer) { Write(T("Cancelled")); return; }
            Write(_host.Restore(out var slot) is { } reason ? reason : T("Restored", held, slot));
        });
    }

    private void Delete(CmdLine line, bool yes)
    {
        if (SongFor(line) is not { } song) return;
        var after = song.Kind == "custom" ? T("DeleteCustom") : T("DeleteForGood");
        Confirm(yes, T("AskDelete", song.Title, after), answer =>
        {
            if (!answer) { Write(T("Cancelled")); return; }
            Write(_host.DeleteSong(song.Id) is { } reason ? reason : T("Deleted", song.Id, song.Title));
        });
    }

    // ---- notes -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Parses "all", "a..b", "a..", "..b" or "n" (1-based, inclusive). <paramref name="last"/> is null for an open end.
    /// </summary>
    /// <summary>
    /// A range read against a known count, so a number written with a minus counts from the end: -1 is the last one, -2 the
    /// one before it. * means all, as it does for load.
    /// </summary>
    public static bool TryParseRange(string text, int count, out int first, out int? last)
    {
        first = 1;
        last = null;
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase) || text == "*") return true;
        var dots = text.IndexOf("..", StringComparison.Ordinal);
        if (dots < 0)
        {
            if (!TrySigned(text, count, out first)) return false;
            last = first;
            return true;
        }
        var left = text[..dots];
        var right = text[(dots + 2)..];
        if (left.Length == 0 && right.Length == 0) return false;
        if (left.Length > 0 && !TrySigned(left, count, out first)) return false;
        if (right.Length == 0) return true;
        if (!TrySigned(right, count, out var end) || end < first) return false;
        last = end;
        return true;
    }

    /// <summary>A number, or one written with a minus for "counting back from the last".</summary>
    private static bool TrySigned(string text, int count, out int value)
    {
        if (text.StartsWith('-') && TryNumber(text[1..], out var back))
        {
            value = count - back + 1;
            return value >= 1;
        }
        return TryNumber(text, out value);
    }

    public static bool TryParseRange(string text, out int first, out int? last)
    {
        first = 1;
        last = null;
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
        var dots = text.IndexOf("..", StringComparison.Ordinal);
        if (dots < 0)
        {
            if (!TryNumber(text, out first)) return false;
            last = first;
            return true;
        }
        var left = text[..dots];
        var right = text[(dots + 2)..];
        if (left.Length == 0 && right.Length == 0) return false;
        if (left.Length > 0 && !TryNumber(left, out first)) return false;
        if (right.Length == 0) return true;
        if (!TryNumber(right, out var end) || end < first) return false;
        last = end;
        return true;
    }

    /// <summary>
    /// One line per shown note: n&lt;hit&gt; &lt;tile&gt; &lt;ms to the next line&gt;, the last line without ms. A chord is one line
    /// (n3-4 3-4) and is listed when either of its hits is in range.
    /// </summary>
    public static IReadOnlyList<string> FormatNotes(IReadOnlyList<VisualizerNote> notes, int first, int last) =>
        FormatNotes(notes, Enumerable.Range(0, notes.Count).ToArray(), first, last);

    /// <summary>
    /// As above with <paramref name="noteHits"/> giving each note's source hit index: numbers skip auto hits and long note ends,
    /// and a long note adds hold=&lt;ms&gt; after its tile.
    /// </summary>
    public static IReadOnlyList<string> FormatNotes(IReadOnlyList<VisualizerNote> notes, IReadOnlyList<int> noteHits, int first, int last)
    {
        static string Hold(params VisualizerNote[] members) => members.Max(m => m.HoldEndMs - m.HitTimeMs) is double length
            ? " hold=" + Math.Round(length, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) : "";
        var steps = new List<(int First, int Last, string Tiles, double TimeMs)>();
        for (var i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            if (note.StartsChord && i + 1 < notes.Count)
            {
                // Every note marked in a row and the one after the last are one chord (two or more, user rule).
                var end = i + 1;
                while (end + 1 < notes.Count && notes[end].StartsChord) end++;
                var members = notes.Skip(i).Take(end - i + 1).ToArray();
                steps.Add((noteHits[i] + 1, noteHits[end] + 1, string.Join("-", members.Select(m => m.Tile).Order()) + Hold(members), note.HitTimeMs));
                i = end;
            }
            else steps.Add((noteHits[i] + 1, noteHits[i] + 1, note.Tile.ToString(CultureInfo.InvariantCulture) + Hold(note), note.HitTimeMs));
        }
        var picked = steps.Where(step => step.Last >= first && step.First <= last).ToList();
        return picked.Select((step, k) =>
        {
            var line = (step.First == step.Last ? $"n{step.First}" : $"n{step.First}-{step.Last}") + " " + step.Tiles;
            if (k + 1 < picked.Count)
                line += " " + Math.Round(picked[k + 1].TimeMs - step.TimeMs, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
            return line;
        }).ToList();
    }

    private static bool TryNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1;

    /// <summary>
    /// note [&lt;id&gt;] &lt;range&gt;: a song's notes as they play. now, the default, is what is on screen: the editor's lanes
    /// as they stand while it is open, saved or not, so note -json works in the middle of an edit (user rule).
    /// </summary>
    private void Note(CmdLine line, bool yes, bool copy)
    {
        // Read once here only to catch a misspelt range; the numbers are worked out again when the note count is known,
        // since -1 means the last note.
        if (line.Arguments.Count is 0 or > 2 || !TryParseRange(line.Arguments[^1], NoteCountGuess, out _, out _))
        {
            if (line.Arguments.Count == 1 && (Song(line.Arguments[0]) is not null || line.Arguments[0].Equals("now", StringComparison.OrdinalIgnoreCase)))
                Write(T("RangeNeeded", line.Arguments[0]));
            else Write(T("SyntaxIncorrect"), NoteUsage);
            return;
        }
        var range = line.Arguments[^1];
        var json = line.Has("-json");
        if (line.Arguments.Count == 2 && !line.Arguments[0].Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            if (Song(line.Arguments[0]) is not { } song)
            {
                Write(T("NoSongWithId", line.Arguments[0]));
                return;
            }
            if (song.Entry.Status != MapParseStatus.Supported || song.Entry.Timeline is not { } timeline)
            {
                Write(T("NoNotesToRead", song.Title));
                return;
            }
            CheckSettings(() => new CmdNoteSource(timeline, _host.EditOf(song)), range, json, yes, copy);
            return;
        }
        if (_host.NowNotes is not null)
        {
            CheckSettings(() => _host.NowNotes, range, json, yes, copy);
            return;
        }
        if (_host.SongToLoad is not { } toLoad)
        {
            Write(T("NoSongNameOne"));
            return;
        }
        Confirm(yes, T("AskLoadFirst", toLoad), answer =>
        {
            if (!answer)
            {
                Write(T("Cancelled"));
                return;
            }
            if (!_host.LoadSong(out var message) || _host.Timeline is null)
            {
                Write(T("CouldNotLoad", message).TrimEnd());
                return;
            }
            Write(T("LoadedQuoted", toLoad));
            CheckSettings(() => _host.NowNotes, range, json, yes, copy);
        });
    }

    private static string NoteUsage => T("Usage", "note [-proceed] [-json] [-copy] [<id>] <all | a..b | a.. | ..b | n | -n>");

    /// <summary>
    /// Unapplied settings would place notes differently, so they are applied or left out first. The source is read after
    /// that, since applying them prepares the loaded song again.
    /// </summary>
    private void CheckSettings(Func<CmdNoteSource?> source, string range, bool json, bool yes, bool copy)
    {
        if (!_host.HasUnappliedSettings)
        {
            PrintNotes(source(), range, json, copy);
            return;
        }
        Confirm(yes, T("AskApplyPending"), answer =>
        {
            if (answer)
            {
                if (_host.ApplyPendingSettings() is { } error)
                {
                    Write(T("CouldNotApply", error));
                    return;
                }
                Write(T("SettingsApplied"));
            }
            else Write(T("UsingApplied"));
            PrintNotes(source(), range, json, copy);
        });
    }

    /// <summary>How long a range may be read as before the notes are counted; only the spelling is checked there.</summary>
    private const int NoteCountGuess = 1_000_000;

    private void PrintNotes(CmdNoteSource? source, string range, bool json, bool copy)
    {
        if (source is not { Timeline: var timeline })
        {
            Write(T("NoSongLoaded"));
            return;
        }
        IReadOnlyList<VisualizerNote> notes;
        IReadOnlyList<int> noteHits;
        // Only the language differs, so a placement problem reads in the console's language like the rest of it.
        var settings = _host.Settings with { Language = Language };
        try
        {
            notes = PlaybackSession.GenerateSongNotes(timeline, settings, out noteHits);
            // An edit decides the lanes and chords, as when it plays; printed as it stands, whatever lanes the screen shows.
            if (source.Layout is { } layout) notes = NoteEditLayout.Apply(notes, noteHits, layout, settings, out noteHits, anyTile: true);
        }
        catch (Exception error) when (error is NotePlacementException or ArgumentException)
        {
            Write(T("CouldNotPlace", error.Message));
            return;
        }
        // Numbers are source hits; auto hits and long note ends have no note of their own.
        var hits = timeline.HitTimesMs.Count;
        if (!TryParseRange(range, hits, out var first, out var last))
        {
            Write(T("SyntaxIncorrect"), NoteUsage);
            return;
        }
        if (first > hits)
        {
            Write(T("NoSuchNote", first, hits));
            return;
        }
        var until = Math.Min(last ?? hits, hits);
        var lines = json ? FormatNotesJson(notes, noteHits, timeline, first, until) : FormatNotes(notes, noteHits, first, until);
        if (lines.Count == 0)
        {
            Write(T("NoNotesInRange"));
            return;
        }
        Show(lines, copy);
    }

    /// <summary>
    /// The same notes as JSON, one object per pressed note: n is the note number, tile the lane, ms the song's own hit time
    /// and chord the group two notes of a chord share. override reads exactly this back (user rule).
    /// </summary>
    public static IReadOnlyList<string> FormatNotesJson(IReadOnlyList<VisualizerNote> notes, IReadOnlyList<int> noteHits,
        ParsedTimeline timeline, int first, int last)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(noteHits);
        ArgumentNullException.ThrowIfNull(timeline);
        var entries = new List<string>();
        var chord = 0;
        for (var i = 0; i < notes.Count; i++)
        {
            var group = 0;
            // A chord's members are marked in a row up to the one before its last, so only the first opens a group.
            if (i > 0 && notes[i - 1].StartsChord) group = chord;
            else if (notes[i].StartsChord && i + 1 < notes.Count) group = ++chord;
            var number = noteHits[i] + 1;
            if (number < first || number > last) continue;
            var time = noteHits[i] < timeline.HitTimesMs.Count ? timeline.HitTimesMs[noteHits[i]] : notes[i].HitTimeMs;
            var line = $"  {{ \"n\": {number}, \"tile\": {notes[i].Tile}, \"ms\": "
                       + Math.Round(time, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
            if (group > 0) line += $", \"chord\": {group}";
            entries.Add(line + " }");
        }
        if (entries.Count == 0) return [];
        return ["{", "  \"notes\": [", .. entries.Select((text, i) => i + 1 < entries.Count ? text + "," : text), "  ]", "}"];
    }

    // ---- settings --------------------------------------------------------------------------------------------------

    /// <summary>The setting a line names. <paramref name="exact"/> is false when more words may follow (set).</summary>
    private SettingPath? SettingFor(CmdLine line, bool exact)
    {
        if (line.Arguments.Count == 0 || (exact && line.Arguments.Count > 1))
        {
            Write(line.Arguments.Count == 0 ? T("SettingNeeded", line.Command.Name) : T("SyntaxIncorrect"), T("Usage", line.Command.Usage));
            return null;
        }
        var name = line.Arguments[0];
        if (SettingPaths.Find(name) is not { } path)
        {
            var section = SettingPaths.Sections.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Write(section.Name is null || !SettingPaths.IsOpen(section.Name, _host.Settings) ? T("NotASetting", name)
                : SettingPaths.In(section.Name).FirstOrDefault() is { } first ? T("IsASection", section.Name, first.Name)
                    : section.About);
            return null;
        }
        if (!SettingPaths.IsOpen(path.Section, _host.Settings))
        {
            Write(T("ExperimentalClosed"));
            return null;
        }
        return path;
    }

    private void Set(CmdLine line)
    {
        if (line.Arguments.Count == 1 && SettingPaths.Find(line.Arguments[0]) is { } bare && SettingPaths.IsOpen(bare.Section, _host.Settings))
        {
            Write(T("ValueNeeded", bare.Name));
            return;
        }
        if (SettingFor(line, exact: false) is not { } path) return;
        SetValue(path, line.Rest[line.Arguments[0].Length..].Trim());
    }

    /// <summary>speed: timing.speed without its name; with nothing, it shows the speed as get does.</summary>
    /// <summary>The game sync link: a word after the command says what to do, and nothing at all shows how it stands.</summary>
    private void Link(CmdLine line, bool copy)
    {
        var what = line.Arguments.Count == 0 ? "" : line.Arguments[0].ToLowerInvariant();
        if (line.Arguments.Count > 1 || (what.Length > 0 && !line.Command.KeywordNames.Contains(what)))
        {
            Syntax<object>(line);
            return;
        }
        Show(what switch
        {
            "tiles" => _host.GameSyncTiles(),
            "follow" => _host.GameSyncFollow(true),
            "unfollow" => _host.GameSyncFollow(false),
            "auto" => _host.GameSyncAuto(true),
            "manual" => _host.GameSyncAuto(false),
            _ => _host.GameSync(what == "start", what == "stop", what == "again")
        }, copy);
    }

    /// <summary>
    /// install linker: puts the read-only link mod into the game's Mods folder. That is the one write outside the app's own
    /// folders (AGENTS.md rule 2), so it names the folder and asks first, and -p is the only way past the question.
    /// </summary>
    /// <summary>The most problem lines scannow prints for one area; the rest are counted.</summary>
    private const int MaximumProblems = 20;

    /// <summary>
    /// The areas a list names (user rule: several at once, split by commas). all and * stand for every area and stand
    /// alone. Empty when nothing is named.
    /// </summary>
    private IReadOnlyList<string>? AreasOf(CmdLine line)
    {
        if (CmdCatalog.ListOf(line, out var error) is not { } items)
        {
            Write(error!);
            return null;
        }
        if (items.Any(item => item is "all" or "*"))
        {
            if (items.Count == 1) return CmdCatalog.Areas;
            Write(T("AllStandsAlone"));
            return null;
        }
        if (items.FirstOrDefault(item => !CmdCatalog.Areas.Contains(item)) is { } unknown)
        {
            Write(T("NotAnArea", unknown, line.Command.Name, string.Join(", ", line.Command.KeywordNames)));
            return null;
        }
        return CmdCatalog.Areas.Where(items.Contains).ToArray();
    }

    /// <summary>scannow [&lt;area&gt;, …]: says what is wrong and what puts it right, and changes nothing (user request).</summary>
    private void ScanNow(CmdLine line, bool copy)
    {
        if (AreasOf(line) is not { } named) return;
        var every = named.Count == 0 || named.Count == CmdCatalog.Areas.Count;
        var areas = named.Count == 0 ? CmdCatalog.Areas : named;
        // Asked for by name, a linker that is not there is a mistake to put right first (user rule).
        if (!every && areas.Contains("linker") && _host.FindLinker().Installed is null)
        {
            Write(T("LinkerNotInstalledScan"));
            return;
        }
        RunJob(T("JobScan"), (progress, done) => _host.ScanNow(areas, progress, results =>
        {
            var lines = new List<string>();
            foreach (var area in results)
            {
                if (area.NotInstalled)
                {
                    lines.Add($"{area.Name,-8}" + T("ScanNotInstalled"));
                    continue;
                }
                var found = area.Problems.Count == 0 ? T("ScanOk") : area.Problems.Count == 1 ? T("OneProblem") : T("Problems", area.Problems.Count);
                lines.Add($"{area.Name,-8}{found}{(area.Checked is { } what ? " · " + what : "")}");
                lines.AddRange(area.Problems.Take(MaximumProblems).Select(problem => "  " + problem));
                if (area.Problems.Count > MaximumProblems) lines.Add("  " + T("AndMore", area.Problems.Count - MaximumProblems));
            }
            var broken = results.Where(area => area.Problems.Count > 0).Select(area => area.Name).ToArray();
            var redo = broken.Where(CmdCatalog.Reinstallable.Contains).ToArray();
            // Advice only (user rule): scannow never puts anything right itself.
            if (redo.Length > 0) lines.Add(T(redo.Length == 1 ? "ReinstallAdviceOne" : "ReinstallAdvice", string.Join(", ", redo)));
            if (broken.Contains("edits")) lines.Add(T("EditsNotReinstalled"));
            if (broken.Contains("data")) lines.Add(T("DataNotReinstalled"));
            if (broken.Length == 0) lines.Add(T("AllWhole"));
            done([]);
            Show(lines, copy);
        }));
    }

    /// <summary>
    /// install &lt;what&gt;[, …]: the linker goes in on its own; with -reinstall, parts of this version go back (user request).
    /// Edits and data are the user's own and are never reinstalled.
    /// </summary>
    private void Install(CmdLine line, bool yes)
    {
        if (AreasOf(line) is not { } named) return;
        if (named.Count == 0)
        {
            Write(T("InstallNeeds", string.Join(", ", line.Command.KeywordNames)), T("Usage", line.Command.Usage));
            return;
        }
        var every = named.Count == CmdCatalog.Areas.Count;
        if (!line.Has("-reinstall"))
        {
            if (named is ["linker"]) InstallLinker(yes);
            else Write(T("OnlyLinkerInstalls"));
            return;
        }
        var linker = _host.FindLinker().Installed is not null;
        // all is this version in full: what came with the copy, and the linker when it is there.
        var parts = every ? CmdCatalog.Reinstallable.Where(part => part is "source" or "app" || (part == "linker" && linker)).ToArray() : named;
        if (parts.FirstOrDefault(part => !CmdCatalog.Reinstallable.Contains(part)) is { } own)
        {
            Write(own == "edits" ? T("EditsOwnWork") : T("DataOwn"), T("NothingDone"));
            return;
        }
        if (parts.Contains("linker") && !linker)
        {
            Write(T("LinkerNotInstalled"));
            return;
        }
        if (parts.Contains("source") && line.Has("-proceed"))
        {
            // Agreed with the user: putting the files back always asks, since this copy's own changes go with it.
            Write(T("SourceAlwaysAsks"));
            return;
        }
        var restart = parts.Contains("source") || parts.Contains("app");
        var question = T("AskReinstall", string.Join(", ", parts))
            + (parts.Contains("maps") ? T("ReinstallMapsNote") : "")
            + (restart ? T("AppReopens") : "");
        if (!parts.Contains("source"))
        {
            Confirm(yes, question, Go);
            return;
        }
        // The files that will go back are shown before the question (user rule), so nothing is lost by surprise.
        RunJob(T("JobScan"), (progress, done) => _host.ScanNow(["source"], progress, results =>
        {
            var changes = results.SelectMany(area => area.Problems).ToArray();
            done(changes.Length == 0 ? [T("SourceWhole")]
                : [T("TheseGoBack"), .. changes.Take(MaximumProblems).Select(change => "  " + change),
                    .. changes.Length > MaximumProblems ? new[] { "  " + T("AndMore", changes.Length - MaximumProblems) } : []]);
            Confirm(false, question, Go);
        }));

        void Go(bool answer)
        {
            if (!answer) { Write(T("CancelledNoReinstall")); return; }
            if (parts.Contains("maps"))
                RunJob(T("JobMapRebuild"), (progress, done) => _host.Resync(progress, count =>
                {
                    done([T("MapsBuiltAgain", count)]);
                    Then();
                }));
            else Then();
        }

        void Then()
        {
            var rest = parts.Where(part => part != "maps").ToArray();
            if (rest.Length == 0) return;
            if (restart)
                Write(_host.ReinstallAndRestart(rest) is { } reason ? reason : T("ClosingToReinstall"));
            else RunJob(T("JobLinker"), (progress, done) => _host.ReinstallLinker(progress, done));
        }
    }

    /// <summary>install linker: the one write into the game's folder, and only after a yes (AGENTS.md rule 2).</summary>
    private void InstallLinker(bool yes)
    {
        var place = _host.FindLinker();
        if (place.GameDirectory is null)
        {
            Write(T("NoGameFolder"), T("NoGameFolderWhy"));
            return;
        }
        if (place.Source is null)
        {
            Write(T("NoLinkerFiles"), T("NoLinkerFilesWhy"));
            return;
        }
        var version = place.SourceVersion is { Length: > 0 } made ? " " + made : "";
        if (place.Installed is { } installed)
        {
            if (string.Equals(installed, place.SourceVersion, StringComparison.Ordinal))
            {
                Write(T("LinkerAlready", version, place.Target), T("NothingWritten"));
                return;
            }
            var now = installed.Length == 0 ? T("UnknownVersion") : installed;
            Confirm(yes, T("AskReplaceLinker", place.Target, now, version), answer => Run(answer, place));
            return;
        }
        Confirm(yes, T("AskInstallLinker", version, place.Target), answer => Run(answer, place));

        void Run(bool answer, LinkerPlace ready)
        {
            if (!answer) { Write(T("Cancelled")); return; }
            RunJob(T("JobLinkerInstall"), (progress, done) => done(_host.InstallLinker(ready, progress) is { } reason
                ? [reason]
                : [T("InstalledInto", ready.Target), T("AfterInstall")]));
        }
    }

    /// <summary>
    /// A command that stands for one setting: with a value it changes it, with nothing it prints what it is now. Every
    /// shortcut behaves the same way, so none of them is a write-only command (user rule).
    /// </summary>
    private void Shortcut(CmdLine line, string setting)
    {
        var path = SettingPaths.Find(setting)!;
        if (line.Arguments.Count == 0) Write(SettingPaths.Format(path, _host.Settings));
        else if (line.Arguments.Count > 1) Syntax<object>(line);
        else SetValue(path, line.Arguments[0]);
    }

    /// <summary>Changes one setting to what the text says, with the same checks and messages for set and speed.</summary>
    private void SetValue(SettingPath path, string text)
    {
        if (SettingPaths.LockedBy(path, _host.Settings) is { } locked)
        {
            Write(locked);
            return;
        }
        if (!SettingPaths.TryParse(path, text, _host.Settings, out var value, out var error))
        {
            Write(error ?? "");
            return;
        }
        WithSettingsClosed(false, () =>
        {
            // Built on the settings as they are once the window is closed, so changes applied there are kept.
            var current = _host.Settings;
            var next = SettingPaths.With(current, path, value);
            if (next == current)
            {
                Write(T("AlreadyValue", path.Name, SettingPaths.Format(path, current)));
                return;
            }
            if (Problem(next) is { } problem)
            {
                Write(T("NotChanged", problem));
                return;
            }
            Apply(current, next);
        });
    }

    /// <summary>Why a settings snapshot cannot be applied, in the console's language, or null: the same checks the settings panel makes.</summary>
    private static string? Problem(VisualizerSettings settings)
    {
        var worded = settings with { Language = Language };
        var errors = worded.ValidateFields();
        if (errors.Count > 0) return string.Join(" ", errors.Select(error => error.Message).Distinct());
        if (!Themes.ThemeCatalog.Exists(settings.Theme)) return T("NoTheme", settings.Theme);
        try { _ = ControlBindings.Create(worded); }
        catch (ArgumentException error) { return error.Message; }
        return null;
    }

    /// <summary>Applies a new snapshot and prints what changed; rollback can undo it.</summary>
    private void Apply(VisualizerSettings before, VisualizerSettings after)
    {
        var changes = Changes(before, after);
        if (_host.ApplySettings(after) is { } reason)
        {
            Write(T("NotChanged", reason));
            return;
        }
        Log.Write(changes);
    }

    /// <summary>One line per changed setting, "name: old → new", named as get and set name them.</summary>
    private static IReadOnlyList<string> Changes(VisualizerSettings from, VisualizerSettings to)
    {
        var lines = SettingPaths.All
            .Where(path => SettingPaths.Format(path, from) != SettingPaths.Format(path, to))
            .Select(path => $"{path.Name}: {Shown(SettingPaths.Format(path, from))} → {Shown(SettingPaths.Format(path, to))}")
            .ToList();
        // Settings the window has no row for are named as they are stored.
        lines.AddRange(SettingPaths.NotInWindow
            .Where(name => !Equals(SettingsFields.Get(from, name), SettingsFields.Get(to, name)))
            .Select(name => $"{name}: {Shown(Text(from, name))} → {Shown(Text(to, name))}"));
        return lines;

        static string Text(VisualizerSettings settings, string name) => Convert.ToString(SettingsFields.Get(settings, name), CultureInfo.InvariantCulture) ?? "";
        static string Shown(string value) => value.Length == 0 ? T("Empty") : value;
    }

    private void Property(CmdLine line, bool copy)
    {
        if (line.Arguments.Count > 1)
        {
            Syntax<object>(line);
            return;
        }
        var settings = _host.Settings;
        if (line.Arguments.Count == 0)
        {
            Show(SettingPaths.Sections.Where(section => SettingPaths.IsOpen(section.Name, settings))
                .Select(section => $"{section.Name,-14}{section.About}").ToArray(), copy);
            return;
        }
        var name = line.Arguments[0];
        if (!name.Contains('.', StringComparison.Ordinal))
        {
            var section = SettingPaths.Sections.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Name;
            if (section is null)
            {
                Write(T("NotASection", name));
                return;
            }
            if (!SettingPaths.IsOpen(section, settings))
            {
                Write(T("ExperimentalClosed"));
                return;
            }
            // A section without settings (experimental, for now) says what it holds instead.
            if (!SettingPaths.In(section).Any())
            {
                Write(SettingPaths.Sections.First(s => s.Name == section).About);
                return;
            }
            Show(SettingPaths.In(section).Select(path => $"{path.Name,-30}{SettingPaths.Format(path, settings)}"
                + (SettingPaths.LockedBy(path, settings) is null ? "" : "  " + T("Locked"))).ToArray(), copy);
            return;
        }
        if (SettingFor(line, exact: true) is not { } setting) return;
        var lines = new List<string> { T("SettingIsNow", setting.Name, SettingPaths.Format(setting, settings)) };
        var choices = SettingPaths.Choices(setting, settings);
        if (setting.Kind is SettingKind.Toggle or SettingKind.Choice) lines.Add(T("SettingValues", string.Join(", ", choices)));
        else lines.Add(T("SettingTakes", SettingPaths.Describe(setting)));
        if (SettingPaths.LockedBy(setting, settings) is { } locked) lines.Add(locked);
        Show(lines, copy);
    }

    private void Import(CmdLine line, bool yes)
    {
        var json = line.Rest.Trim();
        if (json.Length == 0)
        {
            Write(T("PasteJson"));
            return;
        }
        WithSettingsClosed(yes, () =>
        {
            var current = _host.Settings;
            VisualizerSettings imported;
            bool keptPaths;
            try { imported = SettingsJson.ImportPortable(json, current, out keptPaths, Language); }
            catch (SettingsJsonException error)
            {
                Write(T("JsonUnusable", error.Message.Replace(Environment.NewLine, " ", StringComparison.Ordinal)));
                return;
            }
            if (Problem(imported) is { } problem)
            {
                Write(T("JsonUnusable", problem));
                return;
            }
            if (keptPaths) Write(T("FoldersKept"));
            var changes = Changes(current, imported);
            if (changes.Count == 0)
            {
                Write(T("NothingChanges"));
                return;
            }
            Log.Write(changes);
            Confirm(yes, T("AskApplyChanges"), answer =>
            {
                if (!answer) { Write(T("Cancelled")); return; }
                Write(_host.ApplySettings(imported) is { } reason ? T("NotChanged", reason) : T("AppliedRollback"));
            });
        });
    }

    private void RollBack(bool yes)
    {
        WithSettingsClosed(yes, () =>
        {
            if (_host.LastChange is not { } change)
            {
                Write(T("NothingToRollBack"));
                return;
            }
            // What each touched setting is now, and what it goes back to.
            var lines = Changes(change.After, change.Before);
            Write(T("LastChangeGoesBack"));
            Log.Write(lines.Count > 0 ? lines : [T("NoVisibleSetting")]);
            Confirm(yes, T("AskRollBack"), answer =>
            {
                if (!answer) { Write(T("Cancelled")); return; }
                Write(_host.RollBack() is { } reason ? reason : T("RolledBack"));
            });
        });
    }

    private void Reset(bool yes)
    {
        WithSettingsClosed(yes, () =>
        {
            var current = _host.Settings;
            // This PC's folders stay (user rule).
            var defaults = new VisualizerSettings { MapRootDirectory = current.MapRootDirectory, DataRootDirectory = current.DataRootDirectory };
            var count = Changes(current, defaults).Count;
            if (count == 0)
            {
                Write(T("AlreadyDefaults"));
                return;
            }
            Confirm(yes, T(count == 1 ? "AskResetOne" : "AskReset", count), answer =>
            {
                if (!answer) { Write(T("Cancelled")); return; }
                Write(_host.ApplySettings(defaults) is { } reason ? T("NotChanged", reason) : T("SettingsReset"));
            });
        });
    }

    private void Do(CmdLine line)
    {
        if (line.Arguments.Count != 1 || !CmdCatalog.ActionNames.Contains(line.Arguments[0].ToLowerInvariant()))
        {
            Write(line.Arguments.Count == 0 ? T("ActionNeeded") : T("NotAnAction", string.Join(' ', line.Arguments)),
                T("Actions", string.Join(", ", CmdCatalog.ActionNames)));
            return;
        }
        var action = line.Arguments[0].ToLowerInvariant();
        if (action == "view-playlist-details")
        {
            // The song list details window.
            WithSettingsClosed(false, () => Write(_host.OpenPlaylist() ? T("SongListApplied") : T("SongListUnchanged")));
            return;
        }
        var clears = action.StartsWith("clear", StringComparison.Ordinal);
        var path = SettingPaths.Find(action switch
        {
            "clear-previous-checkpoint-key" => "keys.previous",
            "clear-next-checkpoint-key" => "keys.next",
            "clear-settings-key" => "keys.settings",
            "map-folder" => "advanced.map-folder",
            _ => "advanced.data-folder"
        })!;
        if (clears && SettingPaths.Format(path, _host.Settings) == "none")
        {
            Write(T("AlreadyNone", path.Name));
            return;
        }
        WithSettingsClosed(false, () =>
        {
            var current = _host.Settings;
            object? value = null;
            if (!clears)
            {
                if (_host.PickFolder(SettingsFields.Get(current, path.Property) as string ?? "") is not { Length: > 0 } folder)
                {
                    Write(T("NoFolderPicked"));
                    return;
                }
                value = folder;
            }
            var next = SettingPaths.With(current, path, value);
            if (next == current)
            {
                Write(T("AlreadyValue", path.Name, SettingPaths.Format(path, current)));
                return;
            }
            if (Problem(next) is { } problem)
            {
                Write(T("NotChanged", problem));
                return;
            }
            Apply(current, next);
        });
    }

    // ---- questions and the settings ---------------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="then"/> once the settings are closed (user rule). Unapplied changes there are
    /// applied or dropped as answered; -p applies them.
    /// </summary>
    private void WithSettingsClosed(bool yes, Action then)
    {
        if (!_host.SettingsOpen)
        {
            then();
            return;
        }
        if (!_host.HasUnappliedSettings)
        {
            if (_host.CloseSettings(apply: false) is { } error)
            {
                Write(error);
                return;
            }
            Write(T("SettingsClosed"));
            then();
            return;
        }
        Confirm(yes, T("AskApplyBeforeClose"), answer =>
        {
            if (_host.CloseSettings(apply: answer) is { } error)
            {
                Write(T("SettingsStayOpen", error));
                return;
            }
            Write(answer ? T("AppliedAndClosed") : T("DroppedAndClosed"));
            then();
        });
    }

    /// <summary>Asks a [Y/n] question, or with -p answers Y at once and leaves the question on screen with its answer.</summary>
    private void Confirm(bool yes, string question, Action<bool> answer, Action? detail = null)
    {
        var prompt = detail is null ? AnswerPrompt : DetailPrompt;
        if (yes)
        {
            Log.Write([$"{question} {prompt} Y (-proceed)"]);
            answer(true);
            return;
        }
        var text = question + " " + prompt;
        _question = (text, answer, detail);
        Log.Write([text]);
    }

    // ---- output ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Everything the console prints goes through here, so filling a prompt slot with a command's output can catch the
    /// lines instead of putting them on the screen.
    /// </summary>
    private void Write(params string[] lines)
    {
        if (_caught is not null) _caught.AddRange(lines);
        else Log.Write(lines);
    }

    private List<string>? _caught;

    /// <summary>Prints the lines; with -c they are copied to the clipboard as well (user rule).</summary>

    // ---- copying the screen and the stored prompts -------------------------------------------------------------------

    /// <summary>
    /// One message on the screen: the line the user typed and everything printed under it. The blank line between
    /// messages is left out, so what is copied has no gaps at its ends.
    /// </summary>
    private sealed record Message(string Typed, IReadOnlyList<string> Printed)
    {
        /// <summary>Whether this message was a copy of its own, which -en skips when counting.</summary>
        public bool Copying => Typed.StartsWith("copy", StringComparison.OrdinalIgnoreCase)
            || Typed.StartsWith("paste", StringComparison.OrdinalIgnoreCase)
            || CmdCatalog.Words(Typed).Any(word => word.Word.Equals("-c", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a line of the screen is one the user typed, and after which prompt.</summary>
    private static bool IsTyped(string line, out string prompt)
    {
        foreach (var start in new[] { PromptText, FillPrompt })
            if (line.StartsWith(start, StringComparison.Ordinal))
            {
                // A note's or a blank's prompt carries its own name, so the typed part starts after the ">".
                var end = start == PromptText ? start.Length : line.IndexOf('>') + 1;
                prompt = end > 0 && end <= line.Length ? line[..end] : start;
                return true;
            }
        prompt = "";
        return false;
    }

    /// <summary>
    /// The screen split into messages, newest last. The line being typed now is not one of them: the copy running here has
    /// printed nothing yet, so counting starts at the message before it (user rule).
    /// </summary>
    private IReadOnlyList<Message> Messages()
    {
        var found = new List<Message>();
        var printed = new List<string>();
        var typed = "";
        var started = false;
        foreach (var line in Log.Lines)
        {
            if (IsTyped(line, out var mark))
            {
                if (started) found.Add(new Message(typed, printed.ToArray()));
                printed.Clear();
                typed = line[mark.Length..].Trim();
                started = true;
                continue;
            }
            if (started) printed.Add(line);
        }
        // The last message is the copy command itself, which is still running.
        if (started && found.Count > 0) found.Add(new Message(typed, printed.ToArray()));
        return found.Count > 0 ? found.GetRange(0, found.Count - 1) : [];
    }

    /// <summary>
    /// copy: puts the last messages of the screen on the clipboard. The number says how far back to go, * is all of them
    /// and a..b a range; -1 is the first message, the way -1 is the last note for note (user rule).
    /// </summary>
    private void CopyBack(CmdLine line)
    {
        // paste shows the messages again and copies only when asked; copy is the other way round (-pa does both).
        var showing = line.Command.Name == "paste" || line.Has("-pa");
        var copying = line.Command.Name == "copy" || line.Has("-c");
        if (line.Arguments.Count > 1)
        {
            Syntax<object>(line);
            return;
        }
        var messages = Messages();
        if (line.Has("-en")) messages = messages.Where(message => !message.Copying).ToArray();
        if (messages.Count == 0)
        {
            Write(T("NothingToCopy"));
            return;
        }
        // Counted from the newest back, so 1 is the message above this one.
        var newestFirst = messages.Reverse().ToArray();
        var text = line.Arguments.Count == 0 ? "1" : line.Arguments[0];
        if (!TryParseRange(text, newestFirst.Length, out var first, out var last))
        {
            Syntax<object>(line);
            return;
        }
        // Past the end is simply the end (user rule); only an empty screen is an error.
        var from = Math.Max(1, Math.Min(first, newestFirst.Length));
        var until = Math.Max(from, Math.Min(last ?? from, newestFirst.Length));
        var picked = newestFirst[(from - 1)..until].Reverse().ToArray();
        var lines = new List<string>();
        foreach (var message in picked)
        {
            if (line.Has("-vs") || line.Has("-vo")) lines.Add((PromptText + " " + message.Typed).TrimEnd());
            if (line.Has("-vo")) continue;
            lines.AddRange(line.Has("-ep") ? message.Printed.Where(text => !text.Contains(AnswerPrompt, StringComparison.Ordinal)) : message.Printed);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0)
        {
            Write(T("NothingToCopyThere"));
            return;
        }
        if (showing) Write([.. lines]);
        if (!copying) return;
        try
        {
            _host.CopyText(string.Join(Environment.NewLine, lines));
            Write(picked.Length == 1 ? T("CopiedOne") : T("CopiedMessages", picked.Length));
        }
        catch (ExternalException error) { Write(T("CouldNotCopy", error.Message)); }
    }

    /// <summary>
    /// A blank in a stored prompt. <see cref="Question"/> is what the console asks for it, in the console's language (the
    /// answer is the user's own words and may be any language). <see cref="Placeholder"/> is left in its place when the
    /// blank is skipped, and tells a language model to ask rather than invent.
    /// </summary>
    private sealed record PromptBlank(string Name, string Question, string Placeholder);

    /// <summary>A stored prompt: its lines, where <c>{name}</c> marks a blank, and what those blanks are.</summary>
    private sealed record StoredPrompt(IReadOnlyList<string> Lines, IReadOnlyList<PromptBlank> Blanks);

    /// <summary>
    /// Prompts kept for pasting into a language model, in the console's language. Nothing here is sent anywhere and nothing
    /// runs: the console only fills the blanks in and hands the text over (user rule). A line that is only {name} is a blank.
    /// </summary>
    private static IReadOnlyDictionary<string, StoredPrompt> Prompts =>
        new Dictionary<string, StoredPrompt>(StringComparer.OrdinalIgnoreCase)
        {
            ["note-lanes"] = new(T("Prompt.note-lanes").Split('\n'),
            [
                new("request", T("Blank.request"), T("Placeholder.request")),
                new("notes", T("Blank.notes"), T("Placeholder.notes"))
            ])
        };

    /// <summary>
    /// A prompt being filled in: the values so far and which blank is being asked for. Answering is not a command line,
    /// so the typed text is the value unless it starts with a slash.
    /// </summary>
    private sealed record Filling(string Title, StoredPrompt Prompt, string?[] Values, int At, bool Copy)
    {
        public IReadOnlyList<PromptBlank> Blanks() => Prompt.Blanks;
    }

    private Filling? _filling;

    /// <summary>Whether a prompt is being filled in, which takes the typed lines as answers.</summary>
    public bool IsFilling => _filling is not null;

    /// <summary>
    /// The commands whose printed lines may fill a blank. Only ones that print and stop: anything that asks a question or
    /// opens a window would leave two things waiting for the same typed line.
    /// </summary>
    private static readonly HashSet<string> BlankCommands = new(StringComparer.Ordinal)
        { "paste", "note", "id", "title", "info", "song", "playlist", "get", "property", "export", "version", "date", "fps", "link", "log",
          "cmd", "help", "option", "keyword", "whoami" };

    /// <summary>prompt: shows one stored prompt, or lists their titles; one with blanks is filled in first.</summary>
    private void ShowPrompt(CmdLine line, bool copy)
    {
        var wanted = line.Rest.Trim();
        if (wanted.Length == 0 || wanted.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            Show([T("PromptList", string.Join(", ", Prompts.Keys)), T("PromptHint")], copy);
            return;
        }
        if (!Prompts.TryGetValue(wanted, out var prompt))
        {
            Write(T("NoSuchPrompt", wanted), T("PromptList", string.Join(", ", Prompts.Keys)));
            return;
        }
        if (prompt.Blanks.Count == 0)
        {
            Show(prompt.Lines, copy);
            return;
        }
        _filling = new Filling(wanted, prompt, new string?[prompt.Blanks.Count], 0, copy);
        Write(T("BlanksToFill", wanted, prompt.Blanks.Count), T("BlankHint"));
        AskBlank();
    }

    private void AskBlank()
    {
        var filling = _filling!;
        Write($"({filling.At + 1}/{filling.Blanks().Count}) {filling.Blanks()[filling.At].Question}");
    }

    /// <summary>One line typed while a prompt is being filled in.</summary>
    private void FillBlank(string line)
    {
        var filling = _filling!;
        var text = line.Trim();
        // An empty line is no answer at all, as it is for every other question (user rule): ask again.
        if (text.Length == 0)
        {
            AskBlank();
            return;
        }
        if (text.StartsWith('/'))
        {
            var word = text[1..];
            var name = word.Split(' ')[0].ToLowerInvariant();
            switch (name)
            {
                case "skip":
                    Keep(null);
                    return;
                case "cancel":
                    Write(T("PromptCancelled", filling.Title));
                    Leave(filling, printed: true);
                    return;
                case "back":
                    if (filling.At == 0)
                    {
                        Write(T("FirstBlank"));
                        AskBlank();
                        return;
                    }
                    _filling = filling with { At = filling.At - 1 };
                    AskBlank();
                    return;
                case "again":
                    _filling = filling with { Values = new string?[filling.Blanks().Count], At = 0 };
                    Write(T("StartingOver"));
                    AskBlank();
                    return;
                case "string":
                    // Everything after the word is the value, spaces and slashes included.
                    Keep(word.Length > "string".Length ? word["string".Length..].TrimStart(' ') : "");
                    return;
                default:
                    FromCommand(word);
                    return;
            }
        }
        Keep(text);

        void Keep(string? value)
        {
            var values = filling.Values.ToArray();
            values[filling.At] = value;
            var next = filling.At + 1;
            _filling = filling with { Values = values, At = next };
            if (next < filling.Blanks().Count)
            {
                AskBlank();
                return;
            }
            ConfirmFilled();
        }

        void FromCommand(string typed)
        {
            if (CmdCatalog.Parse(typed, out var error) is not { } parsed)
            {
                Write(error ?? T("NotACommandBlank"));
                AskBlank();
                return;
            }
            if (!BlankCommands.Contains(parsed.Command.Name))
            {
                Write(T("CannotFillBlank", parsed.Command.Name));
                AskBlank();
                return;
            }
            var caught = new List<string>();
            _caught = caught;
            try { Run(parsed); }
            finally { _caught = null; }
            // A command that ended up asking something would fight this question for the next line.
            if (IsAsking)
            {
                _question = null;
                _choice = null;
                Write(T("AsksCannotFill", parsed.Command.Name));
                AskBlank();
                return;
            }
            while (caught.Count > 0 && caught[^1].Length == 0) caught.RemoveAt(caught.Count - 1);
            if (caught.Count == 0)
            {
                Write(T("PrintedNothing", parsed.Command.Name));
                AskBlank();
                return;
            }
            Write(T(caught.Count == 1 ? "TookLine" : "TookLines", caught.Count, parsed.Command.Name));
            Keep(string.Join(Environment.NewLine, caught));
        }
    }

    /// <summary>The filled prompt, with long answers shortened so the screen still shows the shape of it.</summary>
    private IReadOnlyList<string> Filled(Filling filling, bool whole)
    {
        var lines = new List<string>();
        foreach (var line in filling.Prompt.Lines)
        {
            var blank = filling.Blanks().Select((each, index) => (each, index))
                .FirstOrDefault(pair => line.Trim() == "{" + pair.each.Name + "}");
            if (blank.each is null)
            {
                lines.Add(line);
                continue;
            }
            var value = filling.Values[blank.index];
            if (value is null)
            {
                lines.Add(blank.each.Placeholder);
                continue;
            }
            var own = value.Split(Environment.NewLine);
            if (whole || own.Length <= ShortLines)
            {
                lines.AddRange(own);
                continue;
            }
            lines.AddRange(own.Take(ShortLines - 1));
            lines.Add("    " + T("MoreLines", own.Length - (ShortLines - 1)));
        }
        return lines;
    }

    /// <summary>How many lines of an answer the shortened view shows before it says how many are left.</summary>
    private const int ShortLines = 3;

    private void ConfirmFilled()
    {
        var filling = _filling!;
        Write("");
        foreach (var line in Filled(filling, whole: false)) Write(line);
        Write("");
        Confirm(false, T("AskCopyThis"), answer =>
        {
            if (!answer)
            {
                Write(T("PromptCancelled", filling.Title));
                Leave(filling, printed: true);
                return;
            }
            var text = string.Join(Environment.NewLine, Filled(filling, whole: true));
            try
            {
                _host.CopyText(text);
                Write(T("CopiedPrompt", Filled(filling, whole: true).Count));
                _filling = null;
            }
            catch (ExternalException error)
            {
                Write(T("CouldNotCopy", error.Message));
                Leave(filling, printed: true);
            }
        }, () => { foreach (var line in Filled(filling, whole: true)) Write(line); });
    }

    /// <summary>
    /// Ends the filling. What was typed is printed once on the way out, so a long request is not lost to a wrong answer
    /// (user rule).
    /// </summary>
    private void Leave(Filling filling, bool printed)
    {
        _filling = null;
        if (!printed) return;
        var written = filling.Values.Select((value, index) => (value, index)).Where(pair => pair.value is not null).ToArray();
        if (written.Length == 0) return;
        Write(T("WhatWasFilled"));
        foreach (var (value, index) in written)
        {
            var own = value!.Split(Environment.NewLine);
            Write($"  {filling.Blanks()[index].Name}: " + (own.Length == 1 ? own[0] : T("FirstOfLines", own[0], own.Length)));
        }
    }


    // ---- the notes for the next version ---------------------------------------------------------------------------

    /// <summary>
    /// report: hands the note to the console window, which opens it in its own editor (user rule). The session only
    /// checks the title and reads what is there; nothing about the note is edited here.
    /// </summary>
    /// <summary>What the editor's command line takes, for report -col.</summary>
    private static (string Command, string Means)[] EditorCommands =>
    [
        (":w  :write", T("Colon.write")),
        (":q  :quit", T("Colon.quit")),
        (":wq  :x", T("Colon.writequit")),
        (":copy", T("Colon.copy")),
        (":d  :delete", T("Colon.delete"))
    ];

    /// <summary>
    /// report always says what it is doing: its words are the only way to drive it, since an option may not stand in for a
    /// keyword a command cannot do without (user rule).
    /// </summary>
    private void OpenReport(CmdLine line, bool copy)
    {
        // The word comes first and the title is the rest of the line. Options may sit on either side of the word, since a
        // title is free text and the parser leaves everything after the command alone.
        var rest = line.Rest.Trim();
        // Each option read here with its value: -inspect:3 says how many runs, not only that it is there.
        var after = new Dictionary<string, string>(StringComparer.Ordinal);
        string Next()
        {
            var space = rest.IndexOf(' ');
            var word = space < 0 ? rest : rest[..space];
            rest = space < 0 ? "" : rest[(space + 1)..].Trim();
            return word;
        }
        bool Option()
        {
            if (!rest.StartsWith('-')) return false;
            var space = rest.IndexOf(' ');
            var word = space < 0 ? rest : rest[..space];
            if (CmdCatalog.Option(word) is not { } read || !line.Command.OptionNames.Contains(read.Option.Name)) return false;
            after[read.Option.Name] = read.Value;
            Next();
            return true;
        }
        while (Option()) { }
        var verb = rest.Length > 0 && !rest.StartsWith('"') ? Next() : "";
        while (Option()) { }
        // Quotes keep a title that is spelled like one of the words; they are never needed otherwise.
        if (rest.Length >= 2 && rest.StartsWith('"') && rest.EndsWith('"')) rest = rest[1..^1];
        bool Has(string option) => line.Has(option)
            || (CmdCatalog.Option(option) is { } known && after.ContainsKey(known.Option.Name));
        // -copy may stand after the word as well, so the printing branches read it from here.
        copy = copy || Has("-copy");
        var title = rest;
        // The parser keeps -force and -proceed apart only in front of the word; behind it they are read here.
        if (Has("-force") && Has("-proceed"))
        {
            Write(T("ForceAlwaysAsks"));
            return;
        }

        if (verb.Length == 0 || !line.Command.KeywordNames.Contains(verb, StringComparer.OrdinalIgnoreCase))
        {
            Write(verb.Length == 0
                ? T("ReportNeeds", string.Join(", ", line.Command.KeywordNames))
                : T("NotReportWord", verb, string.Join(", ", line.Command.KeywordNames)));
            return;
        }
        switch (verb.ToLowerInvariant())
        {
            case "create":
            case "write":
                if (line.Options.ContainsKey("-log") || after.ContainsKey("-log"))
                    LogReport(title, verb.Equals("create", StringComparison.OrdinalIgnoreCase), Has("-force"), Has("-proceed"));
                else NewReport(title, verb.Equals("create", StringComparison.OrdinalIgnoreCase), Has("-force"), Has("-proceed"));
                return;
            case "colon":
                Show([.. EditorCommands.Select(each => $"  {each.Command,-12}{each.Means}")], copy);
                return;
            case "list":
                ListReports(title, copy);
                return;
            case "sweep":
                ClearReports(Has("-proceed"));
                return;
            case "rename":
                RenameReport(title, Has("-proceed"));
                return;
        }
        if (title.Length == 0)
        {
            Write(T("ReportNeedsTitle", verb));
            return;
        }
        var found = _host.ReadReport(title, out var problem);
        if (problem is { } reason)
        {
            Write(reason);
            return;
        }
        // These work on the note where it lies: none of them opens the editor.
        switch (verb.ToLowerInvariant())
        {
            case "state":
                // One line, for an agent to read: it never opens anything and never changes anything.
                Write(found is null ? T("NeverWritten", title) : StateLine(Standing(title)));
                return;
            case "ask":
            {
                // report ask <title> > <reason>, split the way rename is: a title may hold spaces but never ">".
                var cut = title.IndexOf('>');
                if (cut < 0) { Write(T("SayWhatIsNeeded")); return; }
                var asked = title[..cut].Trim();
                var why = title[(cut + 1)..].Trim();
                if (why.Length == 0) { Write(T("SayWhatIsNeeded")); return; }
                if (_host.MarkReport(asked, "asked", why) is { } problem2) Write(problem2);
                else Write(T("WaitingOnAnswer", asked));
                return;
            }
            case "done":
            case "undone":
            {
                if (found is null) { Write(T("NeverWritten", title)); return; }
                var wanted = verb.Equals("done", StringComparison.OrdinalIgnoreCase) ? "done" : "open";
                if (_host.MarkReport(title, wanted, null) is { } trouble) Write(trouble);
                else Write(wanted == "open" ? T("OpenAgain", title) : T("DoneIn", title, AppInfo.Version));
                return;
            }
            case "erase":
                if (found is null) { Write(T("NeverWritten", title)); return; }
                Confirm(Has("-proceed"), T("AskDeleteNote", title, found.Count), answer =>
                {
                    if (!answer) { Write(T("NothingDeleted")); return; }
                    if (DeleteReport(title) is { } trouble) Write(trouble);
                    else Write(T("NoteDeleted", title));
                });
                return;
            case "print":
                if (found is null) { Write(T("NeverWritten", title)); return; }
                Show([.. found], copy);
                return;
        }
    }

    /// <summary>
    /// report create / write: the two ways in, each saying what it expects (user rule). create starts one and refuses a
    /// title that is already there unless -force replaces it; write opens one that is there and refuses a title that is not.
    /// </summary>
    private void NewReport(string title, bool creating, bool force, bool yes)
    {
        var word = creating ? "create" : "write";
        if (title.Length == 0) { Write(T("ReportNeedsTitle", word)); return; }
        var found = _host.ReadReport(title, out var problem);
        if (problem is { } reason) { Write(reason); return; }
        if (!creating)
        {
            if (found is null) { Write(T("NoNoteCalled", title)); return; }
            Edit(title, found);
            return;
        }
        if (found is null) { Edit(title, null); return; }
        if (!force)
        {
            Write(T("NoteAlreadyThere", title));
            return;
        }
        // Replacing is the only way a note loses what is in it, so it asks (user rule for anything that throws work away).
        Confirm(yes, T("AskReplaceNote", title, found.Count),
            answer =>
            {
                if (!answer) { Write(T("NothingReplaced")); return; }
                Edit(title, []);
            });
    }

    /// <summary>
    /// report -log create / write <title>: writes the log into a note (user request):
    /// create starts the note with it, write adds it below what the note holds.
    /// </summary>
    private void LogReport(string title, bool creating, bool force, bool yes)
    {
        var word = creating ? "create" : "write";
        if (title.Length == 0) { Write(T("LogReportNeedsTitle", word)); return; }
        var lines = _host.LogLines().ToArray();
        if (lines.Length == 0) { Write(T("LogEmpty")); return; }
        var existing = _host.ReadReport(title, out var problem);
        if (problem is { } reason) { Write(reason); return; }
        void Save(IReadOnlyList<string> text, string done)
        {
            if (_host.WriteReport(title, text) is { } failed) { Write(failed); return; }
            Write(done);
        }
        if (!creating)
        {
            if (existing is null) { Write(T("NoNoteCalledLog", title)); return; }
            Save([.. existing, "", .. lines], T("LogAdded", lines.Length, title));
            return;
        }
        if (existing is null) { Save(lines, T("LogWrote", lines.Length, title)); return; }
        if (!force)
        {
            Write(T("LogNoteAlreadyThere", title));
            return;
        }
        Confirm(yes, T("AskReplaceLog", title, existing.Count), answer =>
        {
            if (!answer) { Write(T("NothingReplaced")); return; }
            Save(lines, T("LogReplaced", title, lines.Length));
        });
    }

    /// <summary>
    /// inspect start | stop | result (user request; measuring only, AGENTS.md rule 6): start presses the visualizer's notes
    /// into the game after asking, and with -flow prints every key as it goes until the run ends; stop ends it; result shows
    /// what the last runs found, -runs:n the last n and -runs:* every one kept.
    /// </summary>
    /// <summary>
    /// restart linker and restart app (user request: after trouble in the game, such as an error or the game running twice,
    /// the link had no way back short of closing everything). linker throws the link away whole and opens it again, which
    /// asks nothing; app closes this app and starts the same build again, after asking.
    /// </summary>
    private void Restart(CmdLine line, bool copy)
    {
        var words = string.Join(", ", line.Command.KeywordNames);
        var what = line.Arguments.Count == 0 ? "" : line.Arguments[0].ToLowerInvariant();
        if (line.Arguments.Count > 1) { Syntax<object>(line); return; }
        if (what.Length == 0) { Write(T("RestartNeeds", words)); return; }
        if (!line.Command.KeywordNames.Contains(what)) { Write(T("NotRestartWord", what, words)); return; }
        if (what == "linker") { Show(_host.RestartLinker(), copy); return; }
        Confirm(line.Has("-proceed"), T("AskRestartApp"), answer =>
        {
            if (!answer) { Write(T("NothingRestarted")); return; }
            Write(_host.RestartApp() is { } reason ? reason : T("ClosingToRestart"));
        });
    }

    private void Inspect(CmdLine line, bool copy)
    {
        var words = string.Join(", ", line.Command.KeywordNames);
        var what = line.Arguments.Count == 0 ? "" : line.Arguments[0].ToLowerInvariant();
        if (line.Arguments.Count > 1) { Syntax<object>(line); return; }
        if (what.Length == 0) { Write(T("InspectNeeds", words)); return; }
        if (!line.Command.KeywordNames.Contains(what)) { Write(T("NotInspectWord", what, words)); return; }
        if (line.Has("-flow") && what != "start") { Write(T("FlowWithStart")); return; }
        if (line.Options.ContainsKey("-runs") && what != "result") { Write(T("RunsWithResult")); return; }
        switch (what)
        {
            case "start":
                // It presses keys into the game, so it always asks (user rule), even though every rule is checked first.
                var flow = line.Has("-flow");
                Confirm(line.Has("-proceed"), T("AskInspect"), answer =>
                {
                    if (!answer) { Show([T("NothingPressed")], copy); return; }
                    if (!flow || _host.Inspecting) { Show(_host.GameSyncInspect(true), copy); return; }
                    // With -flow the console belongs to the run until it ends (user rule): no prompt and nothing typed is
                    // taken, the keys come up line by line, and the bar says how far it has got. Esc or the end of the song
                    // gives the prompt back.
                    RunJob(T("JobInspect"), (progress, done) =>
                    {
                        var running = false;
                        var said = _host.GameSyncInspect(true, true, progress, () => { if (running) done([]); });
                        Write([.. said]);
                        running = _host.Inspecting;
                        if (!running) done([]);
                    });
                });
                return;
            case "stop":
                Show(_host.GameSyncInspect(false), copy);
                return;
            default:
                var runs = line.Options.TryGetValue("-runs", out var value) ? value : "1";
                var count = runs == "*" ? 0 : int.Parse(runs, CultureInfo.InvariantCulture);
                if (_host.InspectLines(count) is { } lines) Show(lines, copy);
                else Write(T("NoInspectRun"));
                return;
        }
    }

    /// <summary>log: the latest lines of what the app did, every line kept, or none any more (user request).</summary>
    private void ShowLog(CmdLine line, bool copy)
    {
        var what = line.Arguments.Count == 0 ? "list" : line.Arguments[0].ToLowerInvariant();
        if (line.Arguments.Count > 1 || !line.Command.KeywordNames.Contains(what))
        {
            Syntax<object>(line);
            return;
        }
        if (what == "erase")
        {
            _host.ClearLog();
            Write(T("LogErased"));
            return;
        }
        var lines = _host.LogLines();
        if (lines.Count == 0) { Write(T("LogEmpty")); return; }
        if (what == "all" || lines.Count <= LogShown) Show([.. lines, T("LogHint", lines.Count)], copy);
        else Show([T("LogLatest", LogShown, lines.Count), .. lines.Skip(lines.Count - LogShown), T("LogHint", lines.Count)], copy);
    }

    /// <summary>How many of the latest lines a bare log shows; log all shows the rest.</summary>
    private const int LogShown = 40;

    /// <summary>Opens the note in the console's own editor, with the lines it is to hold.</summary>
    private void Edit(string title, IReadOnlyList<string>? lines)
    {
        // The window may not be listening (a test, or the console closing): then the note simply is not opened.
        if (ReportRequested is null)
        {
            Write(T("CannotOpenNote"));
            return;
        }
        var state = _host.ReportNotes()
            .FirstOrDefault(note => note.Title.Equals(title, StringComparison.OrdinalIgnoreCase))?.Done ?? "";
        Log.Report = new ReportDraft(title, lines?.ToArray() ?? [], Changed: false, Done: state);
        // The keys are on the editor's own bottom line, so the console does not repeat them (user rule).
        Write(lines is null ? T("NewNote", title) : T("NoteLines", title, lines.Count));
        ReportRequested(Log.Report);
    }

    /// <summary>
    /// report -ls: every note with the version it was written for and whether it is done, or only the notes holding a
    /// word when one is typed after it.
    /// </summary>
    private void ListReports(string word, bool copy)
    {
        var notes = _host.ReportNotes();
        if (word.Length > 0)
            notes = notes.Where(note => note.Title.Contains(word, StringComparison.CurrentCultureIgnoreCase)
                || note.Holds(word)).ToArray();
        if (notes.Count == 0)
        {
            Write(word.Length > 0 ? T("NoNoteHolds", word) : T("NoNotesYet"));
            return;
        }
        var width = Math.Max(8, notes.Max(note => note.Title.Length));
        var open = notes.Count(note => note.State != "done");
        var lines = new List<string>();
        foreach (var note in notes)
        {
            lines.Add($"  {note.Title.PadRight(width)}  {(note.Version.Length > 0 ? note.Version : "-"),-7}"
                + (note.State == "done" ? $"done {note.Version}" : note.State).PadRight(12) + T("LineCount", note.Lines));
            // The reason is a sentence in the language the settings are set to, so it goes under its row, not in a column.
            if (note.State == "asked" && note.Why.Length > 0) lines.Add($"  {new string(' ', width)}  {note.Why}");
        }
        lines.Add("  " + T("StillOpen", open, notes.Count));
        Show(lines, copy);
    }

    /// <summary>Where a note stands, or null when there is no note by that title.</summary>
    private CmdNote? Standing(string title) =>
        _host.ReportNotes().FirstOrDefault(note => note.Title.Equals(title, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The one line that says where a note stands, which is what -e prints and what -ls puts in its column. A note that
    /// was looked at but not settled says why, in the language the settings are set to (user rule).
    /// </summary>
    private static string StateLine(CmdNote? note) => note is null ? "open"
        : note.State switch
        {
            "done" => $"done {note.Version}",
            "asked" => note.Why.Length > 0 ? $"asked: {note.Why}" : "asked",
            _ => "open"
        };

    /// <summary>report -clear: takes away every note already marked done, after asking (user rule).</summary>
    private void ClearReports(bool yes)
    {
        var done = _host.ReportNotes().Where(note => note.State == "done").Select(note => note.Title).ToArray();
        if (done.Length == 0) { Write(T("NoNoteDone")); return; }
        Confirm(yes, T("AskSweep", done.Length, string.Join(", ", done)), answer =>
        {
            if (!answer) { Write(T("NothingDeleted")); return; }
            var gone = _host.ClearDoneReports();
            Write(gone.Count == 0 ? T("NothingDeleted") : T("DeletedNotes", gone.Count, string.Join(", ", gone)));
        });
    }

    /// <summary>
    /// report -mv &lt;old&gt; &gt; &lt;new&gt;: a title may hold spaces, so the two are split on "&gt;", which a title can
    /// never contain.
    /// </summary>
    private void RenameReport(string rest, bool yes)
    {
        var cut = rest.IndexOf('>');
        if (cut < 0)
        {
            Write(T("SayWhichName"));
            return;
        }
        var title = rest[..cut].Trim();
        var name = rest[(cut + 1)..].Trim();
        if (title.Length == 0 || name.Length == 0)
        {
            Write(T("SayWhichName"));
            return;
        }
        // The same rule the store keeps, said here so the console does not name a storage type (ICmdHost stays its own).
        if (name.Length > 64 || !name.All(letter => char.IsLetterOrDigit(letter) || letter is ' ' or '-' or '_'))
        {
            Write(T("NoteTitleRule"));
            return;
        }
        Confirm(yes, T("AskRename", title, name), answer =>
        {
            if (!answer) { Write(T("NameStays")); return; }
            if (_host.RenameReport(title, name) is { } trouble) Write(trouble);
            else Write(T("NoteRenamed", title, name));
        });
    }

    /// <summary>Asks the console window to open a note in its editor.</summary>
    public event Action<ReportDraft>? ReportRequested;

    /// <summary>Writes a note for the editor; null when written, otherwise why not.</summary>
    public string? SaveReport(string title, IReadOnlyList<string> lines) => _host.WriteReport(title, lines);

    /// <summary>Puts text on the clipboard for the editor's :copy, the same way the -c option does.</summary>
    public void CopyOut(string text) => _host.CopyText(text);

    /// <summary>Removes a note for the editor; null when it went, otherwise why not.</summary>
    public string? DeleteReport(string title) =>
        _host.DeleteReport(title, out var problem) ? null : problem ?? T("NeverWritten", title);

    private void Show(IReadOnlyList<string> lines, bool copy)
    {
        if (_caught is not null)
        {
            _caught.AddRange(lines);
            return;
        }
        Log.Write(lines);
        if (!copy) return;
        try
        {
            _host.CopyText(string.Join(Environment.NewLine, lines));
            Write(T("CopiedToClipboard"));
        }
        catch (ExternalException error) { Write(T("CouldNotCopy", error.Message)); }
    }

    private bool NoArguments(CmdLine line)
    {
        if (line.Arguments.Count == 0) return true;
        Syntax<object>(line);
        return false;
    }

    private T? Syntax<T>(CmdLine line) where T : class
    {
        // ConsoleText by name: T is the type here.
        Write(ConsoleText.T("SyntaxIncorrect"), ConsoleText.T("Usage", line.Command.Usage));
        return null;
    }

    /// <summary>The culture the console's language names weekdays in; invariant when its language file names none.</summary>
    private static CultureInfo DateCulture() =>
        T("DateCulture") is { Length: > 0 } name ? CultureInfo.GetCultureInfo(name) : CultureInfo.InvariantCulture;

    /// <summary>
    /// How many letter widths a text takes in the console's font: Hangul and the other wide scripts take two, so a column
    /// after a translated label still lines up.
    /// </summary>
    public static int Wide(string text) => text.Sum(letter => letter is >= '\u1100' and <= '\u115F' or >= '\u2E80' and <= '\uA4CF'
        or >= '\uAC00' and <= '\uD7A3' or >= '\uF900' and <= '\uFAFF' or >= '\uFF00' and <= '\uFF60' or >= '\uFFE0' and <= '\uFFE6' ? 2 : 1);

    // ---- completion ------------------------------------------------------------------------------------------------

    /// <summary>
    /// What Tab can put in place of the word being typed: commands (cmd first, so Tab on an empty line gives cmd), a
    /// command's options, and its words: ids, commands, song groups, actions, settings by section and the values a setting
    /// takes. <paramref name="start"/> is where that word begins in the line.
    /// </summary>
    public IReadOnlyList<string> Complete(string? line, out int start)
    {
        line ??= "";
        var words = CmdCatalog.Words(line);
        var fresh = words.Count == 0 || char.IsWhiteSpace(line[^1]);
        start = fresh ? line.Length : words[^1].Start;
        if (IsAsking) return [];
        var typed = fresh ? "" : words[^1].Word;
        var index = fresh ? words.Count : words.Count - 1;
        IEnumerable<string> pool;
        if (index == 0) pool = CmdCatalog.Visible.Select(command => command.Name);
        else if (CmdCatalog.Find(words[0].Word) is not { Hidden: false } command) return [];
        else
        {
            var options = 0;
            while (options + 1 < index && CmdCatalog.IsOption(words[options + 1].Word)) options++;
            // Options may stand after the words too, except inside free text, which takes a dash as it is typed.
            var freeText = command.Argument is CmdArgument.SettingValue or CmdArgument.Json or CmdArgument.Title or CmdArgument.Text
                or CmdArgument.Prompt;
            if (typed.StartsWith('-') && (options + 1 == index || !freeText))
                pool = command.OptionNames.Except(words.Skip(1).Take(index - 1)
                    .Select(word => CmdCatalog.Option(word.Word)?.Option.Name ?? ""));
            else if (command.Argument == CmdArgument.Note)
            {
                // report takes its word first, then the title of a note that is there for the words that name one (user rule).
                var at = options + 1;
                if (at == index || !command.KeywordNames.Contains(words[at].Word, StringComparer.OrdinalIgnoreCase))
                    return at < index ? [] : command.KeywordNames.Where(word => word.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToArray();
                var verb = words[at].Word.ToLowerInvariant();
                for (at++; at < index && CmdCatalog.IsOption(words[at].Word); at++) { }
                start = at < words.Count ? words[at].Start : line.Length;
                var name = line[start..];
                return verb is "write" or "print" or "done" or "undone" or "ask" or "state" or "erase" or "rename"
                    ? _host.ReportTitles().Where(each => each.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToArray()
                    : [];
            }
            else if (command.Argument == CmdArgument.Prompt)
            {
                // A prompt is named, not a song: those are what Tab offers here (user rule).
                start = options + 1 < words.Count ? words[options + 1].Start : line.Length;
                var name = line[start..];
                return command.KeywordNames.Concat(Prompts.Keys)
                    .Where(each => each.StartsWith(name, StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
            }
            else if (command.Argument == CmdArgument.Text) return [];
            else if (command.Argument == CmdArgument.Redirect)
            {
                // A word or the ids first; after erase, all or an id; after an id, the id of the song to put in its place.
                var ids = _host.Songs.Select(song => song.Id).Order(StringComparer.Ordinal);
                var first = options + 1 < index ? words[options + 1].Word : "";
                pool = (index - options - 1) switch
                {
                    0 => command.KeywordNames.Where(word => word != "all").Concat(ids),
                    1 when first.Equals("erase", StringComparison.OrdinalIgnoreCase) => new[] { "all" }.Concat(ids),
                    1 when !command.KeywordNames.Contains(first.ToLowerInvariant(), StringComparer.Ordinal) => ids,
                    _ => []
                };
            }
            else if (command.Argument == CmdArgument.Title)
            {
                // A title may have spaces, so everything after the options is the title, matched anywhere in it (user rule).
                start = options + 1 < words.Count ? words[options + 1].Start : line.Length;
                var title = line[start..];
                if (words.Skip(1).Take(options).Any(word => word.Word.Equals("-id", StringComparison.OrdinalIgnoreCase)))
                    return _host.Songs.Select(song => song.Id).Order(StringComparer.Ordinal)
                        .Where(id => id.StartsWith(title, StringComparison.OrdinalIgnoreCase)).ToArray();
                return new[] { "origin", "edited", "link" }.Where(keyword => keyword.StartsWith(title, StringComparison.OrdinalIgnoreCase))
                    .Concat(_host.Songs.Where(song => song.Slot is null && Has(song.Title, title)).Select(song => song.Title)
                        .Order(StringComparer.CurrentCultureIgnoreCase))
                    .Distinct().ToArray();
            }
            else
            {
                var settings = _host.Settings;
                // Whatever else the argument takes, the command's own words belong in its first one; a list takes them
                // at every place, less the ones already in it.
                var named = words.Skip(1).Take(index - 1).SelectMany(word => word.Word.Split(',')).Select(word => word.Trim())
                    .Where(word => word.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (command.Variadic && typed.LastIndexOf(',') is var comma and >= 0) { start += comma + 1; typed = typed[(comma + 1)..]; }
                var keywords = command.Variadic ? command.KeywordNames.Where(word => !named.Contains(word))
                    : index - options - 1 == 0 ? command.KeywordNames : [];
                pool = keywords.Concat((command.Argument, index - options - 1) switch
                {
                    (CmdArgument.Id, 0) => new[] { "now" }.Concat(_host.Songs.Select(song => song.Id).Order(StringComparer.Ordinal)),
                    (CmdArgument.Command, 0) => CmdCatalog.Visible.Select(each => each.Name),
                    (CmdArgument.Songs, 0) => CmdCatalog.SongGroups,
                    (CmdArgument.Range, 0) when command.Name == "note" => _host.Songs.Select(song => song.Id).Order(StringComparer.Ordinal),
                    (CmdArgument.Range, 0) => ["all"],
                    (CmdArgument.Range, 1) when command.Name == "note" => ["all"],
                    (CmdArgument.Tile, 0) => PinWords(null),
                    (CmdArgument.Pin, 0) when command.Name == "unpin" => PinWords(null).Concat(_host.Songs.Select(song => song.Id).Order(StringComparer.Ordinal)),
                    (CmdArgument.Pin, 0) => _host.Songs.Select(song => song.Id).Order(StringComparer.Ordinal),
                    (CmdArgument.Pin, 1) when command.Name == "unpin" => PinWords(words[options + 1].Word),
                    (CmdArgument.Pin, 1) => ["all"],
                    (CmdArgument.Action, 0) => CmdCatalog.ActionNames,
                    (CmdArgument.Setting or CmdArgument.SettingOrSection or CmdArgument.SettingValue, 0) => SettingWords(typed, settings),
                    (CmdArgument.SettingValue, 1) when SettingPaths.Find(words[options + 1].Word) is { } path => SettingPaths.Choices(path, settings),
                    _ => []
                });
            }
        }
        return pool.Where(word => word.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
    }

    /// <summary>Sections until a dot is typed, then that section's settings (user rule: a dot, not a space, moves on).</summary>
    private static IEnumerable<string> SettingWords(string typed, VisualizerSettings settings)
    {
        var dot = typed.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0) return SettingPaths.OpenSections(settings);
        var section = typed[..dot];
        return SettingPaths.IsOpen(section, settings) ? SettingPaths.In(section).Select(path => path.Name) : [];
    }
}
