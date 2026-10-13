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

/// <summary>What the console reads and changes in the app. Every reason handed back is English (<see cref="CmdSession.Language"/>).</summary>
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
    IReadOnlyList<string> GameSyncInspect(bool on) => ["link inspect is not available here."];
    /// <summary>The lines of the last <paramref name="count"/> inspect runs (every run for 0), or null when none is kept.</summary>
    IReadOnlyList<string>? InspectLines(int count) => null;

    /// <summary>Where the link mod would come from and go to, so the console can say what it will write before it writes.</summary>
    LinkerPlace FindLinker();

    /// <summary>
    /// Writes the mod's own files into the game's Mods folder, or says why it could not. Only ever called after a yes, and
    /// it reports 0..1 so the console can show a bar while it writes.
    /// </summary>
    string? InstallLinker(LinkerPlace place, IProgress<double> progress);
    /// <summary>The applied settings.</summary>
    VisualizerSettings Settings { get; }
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
    string? OverrideNotes(string json) => "The note editor is not open.";
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
    string? MoveCheckpoint(int checkpoint) => "No song is loaded.";
    /// <summary>
    /// Prepares the song from a practice tile, as the section box does. Null when it moved, otherwise why not; the tile is
    /// checked here so the reason is in English rather than the timeline's own.
    /// </summary>
    string? StartAtTile(int tile) => "No song is loaded.";
    /// <summary>The tiles pinned on a song family (an original and its edits share them), in order.</summary>
    IReadOnlyList<int> PinsOf(string family) => [];
    /// <summary>Keeps a family's pins; null when kept, otherwise why not.</summary>
    string? SetPins(string family, IReadOnlyList<int> tiles) => "Pins are not kept here.";

    /// <summary>Every synced song, hidden ones included.</summary>
    IReadOnlyList<CmdSong> Songs { get; }
    /// <summary>The ids of the main window's list in its order: its group, hidden songs left out, sorted as it shows them.</summary>
    IReadOnlyList<string> CurrentIds { get; }
    /// <summary>The id of the loaded song; null when none is loaded or it is not in the list (the example, the preview).</summary>
    string? LoadedId { get; }
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
    IReadOnlyList<string>? ReadReport(string title, out string? problem) { problem = "Notes are not kept here."; return null; }
    /// <summary>Writes a note; null when written, otherwise why not.</summary>
    string? WriteReport(string title, IReadOnlyList<string> lines) => "Notes are not kept here.";
    /// <summary>Removes a note; true when one was there. <paramref name="problem"/> says why it could not be removed.</summary>
    bool DeleteReport(string title, out string? problem) { problem = "Notes are not kept here."; return false; }
    /// <summary>
    /// Writes where a note stands: "done" with this version, "asked" with what is still needed, or "open" to clear it.
    /// The reason is written in the language the settings are set to, since the user is who reads it. Null when it went through.
    /// </summary>
    string? MarkReport(string title, string state, string? why) => "Notes are not kept here.";
    /// <summary>Gives a note another title; null when it went through, otherwise why not.</summary>
    string? RenameReport(string title, string name) => "Notes are not kept here.";
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
        done(areas.Select(area => new ScanArea(area, ["Checking is not set up here."])).ToArray());

    /// <summary>install -reinstall linker: writes this version's linker into the game again, with the app still open.</summary>
    void ReinstallLinker(IProgress<double> progress, Action<IReadOnlyList<string>> done) => done(["Reinstalling is not set up here."]);

    /// <summary>
    /// install -reinstall source and app: the app closes, update.ps1 puts the parts back and builds them, and opens it again
    /// (a running app holds its own files). Null when it is on its way, otherwise why not.
    /// </summary>
    string? ReinstallAndRestart(IReadOnlyList<string> parts) => "Reinstalling is not set up here.";

    /// <summary>
    /// update and version: asks the repository about the latest version, or <paramref name="version"/>, by running its own
    /// update.ps1 -Check. Nothing is changed.
    /// </summary>
    void CheckUpdate(string? version, IProgress<double> progress, Action<UpdateCheck> done) =>
        done(UpdateCheck.Failed("Updating is not set up here."));

    /// <summary>
    /// Takes it: the app closes and the script puts the version in place, builds it and starts it again, since a running
    /// app holds its own files. An installed linker is built and written with it (user rule). <paramref name="force"/>
    /// throws this copy's own changes away. Null when the update is on its way, otherwise why it could not start.
    /// </summary>
    string? ApplyUpdate(bool force, string? version) => "Updating is not set up here.";

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
/// A cmd-like console. Every line it prints is English whatever the app language (user rule), so it has no language file
/// entries; song titles are data and show as they are.
/// </summary>
public sealed class CmdSession
{
    public const string PromptText = "Visualizer>";
    /// <summary>How wide a progress bar is drawn, in characters.</summary>
    private const int BarWidth = 24;
    /// <summary>The language of reasons that come from the app's own messages (placement, settings, editor).</summary>
    public const string Language = Languages.English;
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
        if (!Log.Started) Log.Start([$"Visualizer Console [Version {AppInfo.Version}]", "Type 'cmd' to see the commands.", ""]);
    }

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
        if (Busy) { Write("Another command is still running."); return; }
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
                Write("Cancelled.");
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
            Write($"The note editor is open, so {line.Command.Name} cannot run. Save or close the editor first.");
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
                // This computer's clock, printed like the rest of the console: English and invariant.
                if (NoArguments(line))
                    Show([DateTime.Now.ToString("yyyy-MM-dd dddd HH:mm:ss", CultureInfo.InvariantCulture)], copy);
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
                else Version(copy);
                break;
            case "fps":
                // Measured by the app on itself; nothing is read from the game or the system beyond its own frames.
                if (NoArguments(line)) Show(_host.FrameStats(line.Has("-r")), copy);
                break;
            case "link":
                // One way: the app only listens for the mod, and never sends anything into the game.
                Link(line, copy);
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
                    Write($"sync takes rebuild or nothing, not '{rebuild}'.");
                    break;
                }
                if (rebuild.Length == 0)
                {
                    RunJob("Map sync", (progress, done) =>
                        _host.SyncMaps(progress, count => done([$"Map sync finished. {count} songs in the library."])));
                    break;
                }
                Confirm(yes, "Rebuild the custom maps from the map folder? Songs no longer there leave the list.", answer =>
                {
                    if (!answer) { Write("Cancelled."); return; }
                    RunJob("Map rebuild", (progress, done) =>
                        _host.Resync(progress, count => done([$"Map rebuild finished. {count} songs in the library."])));
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
            lines.Add(group);
            lines.AddRange(CmdCatalog.Visible.Where(command => command.Group == group).Select(command => $"  {command.Usage,-34}{command.Summary}"));
        }
        lines.Add("");
        // Short lines, so they fit the console's default width without wrapping.
        lines.Add("Options go before or after the words: playlist -id base.");
        lines.Add("<id>: an 8-character id or now.   <cmd>: a command, list or last.");
        lines.Add("<string>: " + string.Join(", ", CmdCatalog.SongGroups.SkipLast(1)) + " or all.");
        lines.Add("<title>: any part of a title, origin or edited.");
        lines.Add("help <cmd> shows a command's options and keywords.");
        lines.Add("Tab completes commands, words and settings.");
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
            yield return "  Options: " + command.OptionsText;
            foreach (var line in OptionLines(command)) yield return "  " + line;
        }
        if (command.KeywordNames.Count > 0)
        {
            // The default one stands in brackets, so it is seen rather than read about (user rule).
            yield return "  Keywords: " + string.Join(", ", command.KeywordNames.Select(command.KeywordText));
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
        if (keywords) lines.AddRange(["", "Keywords:", .. KeywordTable()]);
        lines.AddRange(["", keywords ? "To see a command's options and keywords, type option -keywords <cmd>, as in option -keywords playlist."
            : "To see which options a command takes, type option <cmd>, as in option playlist."]);
        return lines;
    }

    /// <summary>Every keyword with the commands that take it; with -o, every option after them.</summary>
    private static IReadOnlyList<string> AllKeywords(bool options)
    {
        var lines = KeywordTable().ToList();
        if (options) lines.AddRange(["", "Options:", .. CmdCatalog.OptionHelp.Select(option => $"  {option.Key,-11}{option.Value}")]);
        lines.AddRange(["", options ? "To see a command's keywords and options, type keyword -options <cmd>, as in keyword -options playlist."
            : "To see which keywords a command takes, type keyword <cmd>, as in keyword playlist."]);
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
        if (command.OptionGroups.Count == 0) lines.Add($"{command.Name} has no options.");
        else lines.AddRange([$"{command.Name}: {command.OptionsText}", .. OptionLines(command)]);
        if (keywords) lines.AddRange(KeywordPart(command));
        return lines;
    }

    /// <summary>keyword &lt;cmd&gt;: the command's keywords, then with -o its options.</summary>
    private static IEnumerable<string> KeywordsOf(CmdCommand command, bool options)
    {
        var lines = KeywordPart(command).ToList();
        if (!options) return lines;
        if (command.OptionGroups.Count == 0) lines.Add($"{command.Name} has no options.");
        else lines.AddRange([$"{command.Name} options: {command.OptionsText}", .. OptionLines(command)]);
        return lines;
    }

    /// <summary>One keyword on its own: what it means and the commands that take it, with a line each where they differ.</summary>
    private static IReadOnlyList<string> WordLines(string keyword, (string Meaning, IReadOnlyList<string> Commands) known)
    {
        var lines = new List<string> { $"{keyword} is a keyword of {string.Join(", ", known.Commands)}." };
        if (known.Meaning.Length > 0) lines.Add("  " + known.Meaning);
        foreach (var name in known.Commands)
        {
            if (CmdCatalog.Find(name) is not { } command) continue;
            var own = CmdCatalog.KeywordMeaning(command, keyword);
            if (own != known.Meaning) lines.Add($"  {name}: {own}");
        }
        return lines;
    }

    private static IEnumerable<string> KeywordPart(CmdCommand command) => command.KeywordNames.Count == 0 ? [$"{command.Name} has no keywords."]
        : [$"{command.Name} keywords: {string.Join(", ", command.KeywordNames.Select(command.KeywordText))}", .. KeywordLines(command)];

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
                Write("There is no command before this one.");
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
        Write($"'{word}' is not a command. Type cmd to see them.");
        return null;
    }

    // ---- songs -----------------------------------------------------------------------------------------------------

    private enum SongColumn { All, Id, Title, IdTitle, TitleId }

    private CmdSong? LoadedSong()
    {
        if (_host.LoadedId is { } id && Song(id) is { } song) return song;
        Write(_host.LoadedSongName is { Length: > 0 } name ? $"{name} is not in the song list, so it has no id." : "No song is loaded.");
        return null;
    }

    /// <summary>The song with this id; letters may be typed in either case.</summary>
    private CmdSong? Song(string id) => _host.Songs.FirstOrDefault(song => string.Equals(song.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The title without the slot the song list puts in front of an edit's ("2*Plum - R" → "Plum - R").</summary>
    private static string BaseTitle(CmdSong song) =>
        song.Slot is { } slot && song.Title.StartsWith(slot.ToString(CultureInfo.InvariantCulture) + "*", StringComparison.Ordinal)
            ? song.Title[(slot.ToString(CultureInfo.InvariantCulture).Length + 1)..] : song.Title;

    /// <summary>What id prints: the id, and an edit's slot.</summary>
    private static string IdText(CmdSong song) => song.Slot is { } slot ? $"{song.Id} (slot {slot})" : song.Id;

    /// <summary>What title prints: the title, and an edit's slot.</summary>
    private static string TitleText(CmdSong song) => song.Slot is { } slot ? $"{BaseTitle(song)} (slot {slot})" : song.Title;

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
            Write($"An id is needed: an 8-character id or now. Usage: {line.Command.Usage}");
            return null;
        }
        if (line.Arguments.Count > 1) return Syntax<CmdSong>(line);
        var id = line.Arguments[0];
        if (id.Equals("now", StringComparison.OrdinalIgnoreCase)) return LoadedSong();
        if (Song(id) is { } song) return song;
        Write($"No song has the id {id}. playlist shows the ids.");
        return null;
    }

    private static IReadOnlyList<string> Details(CmdSong song)
    {
        var entry = song.Entry;
        var timeline = entry.Timeline;
        var rows = new List<(string Name, string Value)>
        {
            ("id", song.Id),
            ("title", song.Title),
            ("artist", entry.Info?.Artist is { Length: > 0 } artist ? artist : "-"),
            ("author", entry.Info?.Author is { Length: > 0 } author ? author : "-"),
            ("kind", song.Kind),
            ("length", timeline is { HitTimesMs.Count: > 0 } timed ? Length(timed.HitTimesMs[^1]) : "-"),
            // Only the notes pressed; long notes and auto tiles have rows of their own, 0 included.
            ("notes", timeline?.CountPressedHits().ToString(CultureInfo.InvariantCulture) ?? "-"),
            ("longnotes", timeline is null ? "-"
                : (timeline.CountHits(HitKind.HoldStart) + timeline.CountHits(HitKind.HoldChain)).ToString(CultureInfo.InvariantCulture)),
            ("autonotes", timeline?.CountHits(HitKind.Auto).ToString(CultureInfo.InvariantCulture) ?? "-"),
            ("difficulty", entry.Info?.Difficulty?.ToString(CultureInfo.InvariantCulture) ?? "-"),
            ("checkpoints", timeline is { CheckpointsVerified: true } marked ? marked.CheckpointCount.ToString(CultureInfo.InvariantCulture) : "-")
        };
        // Every song has the row: an edit's slot, or origin for the song it was made from.
        rows.Add(("slot", song.Slot?.ToString(CultureInfo.InvariantCulture) ?? "origin"));
        rows.Add(("hidden", song.Hidden ? "yes" : "no"));
        rows.Add(("status", entry.Status.ToString().ToLowerInvariant()));
        rows.Add(("path", Path.Combine(entry.RootDirectory, entry.RelativePath)));
        return rows.Select(row => $"{row.Name,-13}{row.Value}").ToArray();

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
            Write("Use " + string.Join(", ", CmdCatalog.SongGroups.SkipLast(1)) + " or all.");
            return null;
        }
        switch (word)
        {
            case "main":
                return _host.CurrentIds.Select(Song).OfType<CmdSong>().ToArray();
            case "now":
                return LoadedSong() is { } loaded ? [loaded] : null;
            case "family":
                return LoadedSong() is { } song ? Family(song) : null;
            case "origin":
                if (LoadedSong() is not { } edit) return null;
                if (OriginOf(edit) is { } origin) return [origin];
                Write($"The original of {edit.Title} is not in the song list.");
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
            Write("No songs match.");
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
    private static string Row(CmdSong song) => $"{song.Id}  {song.Kind,-6}  {song.Title}{(song.Hidden ? "  [hidden]" : "")}";

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
            Write("A title is needed: any part of it, *, ., origin or edited.", $"Usage: {line.Command.Usage}");
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
        // "*" is every song, which is the empty text every title contains.
        if (text == "*") text = "";
        if (line.Has("-id"))
        {
            if (line.Arguments.Count > 1) Write("-id takes one id, all 8 characters of it.");
            else if (Song(text) is { } byId) LoadOne(byId, keepSpeed, force);
            else Write($"No song has the id {text}. An id must be given in full, as playlist -id shows it.");
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
            Write($"No {(editsOnly ? "edit" : "song")} title has '{text}'{(id ? $", and no song has the id {text}" : "")}.");
            return;
        }
        if (matches.Count == 1)
        {
            LoadOne(matches[0], keepSpeed, force);
            return;
        }
        if (matches.Count > CmdCatalog.MaximumPicks)
        {
            Write($"{matches.Count} songs match. Type more of the title.");
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
                Write("-sl goes with edited: origin loads the original.");
                return;
            }
            if (loaded.Slot is null)
            {
                // Pointing the other way helps only when there is an edit to go to (user rule).
                Write($"{loaded.Title} is already the original." + (edits.Count > 0 ? " load edited loads its edits." : ""));
                return;
            }
            if (OriginOf(loaded) is { } origin) LoadOne(origin, keepSpeed, force);
            else Write($"The original of {loaded.Title} is not in the song list.");
            return;
        }
        if (line.Has("-og"))
        {
            Write("-og goes with origin: edited loads an edit.");
            return;
        }
        if (loaded.Slot is not null)
        {
            Write($"{loaded.Title} is an edit. load origin loads its original.");
            return;
        }
        if (edits.Count == 0)
        {
            Write($"{loaded.Title} has no edits.");
            return;
        }
        if (slot is not null)
        {
            if (edits.FirstOrDefault(edit => edit.Slot == slot) is { } inSlot) LoadOne(inSlot, keepSpeed, force);
            else Write($"{loaded.Title} has no edit in slot {slot}.");
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
            Confirm(false, $"{song.Title} is unsupported. Force it, reading it as an older verified format version whose timing may differ from the game?",
                answer =>
                {
                    if (answer) LoadWith(song, () => _host.ForceLoadSongById(song.Id, keepSpeed), forced: true);
                    else Write("Cancelled.");
                });
            return;
        }
        if (force) Write($"-force is only for unsupported songs, and {song.Title} is not one, so it loads as it is.");
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
            Write($"Loaded {song.Id} {song.Title}.");
            if (forced && _host.Songs.FirstOrDefault(listed => listed.Id == song.Id)?.Entry.ForcedFormatVersion is { } format)
                Write($"Forced: read as format version {format}. Its timing may differ from the game; the song list marks it forced.");
            if (SettingPaths.Format(speed, _host.Settings) is var after && after != before)
                Write($"{speed.Name}: {before} → {after}. rollback undoes it; load -speed keeps the speed.");
        });
    }

    /// <summary>Numbers the lines 01, 02, ... and waits for one of them (user rule).</summary>
    private void AskChoice(IReadOnlyList<string> lines, Action<int> take)
    {
        Log.Write(lines.Select((text, index) => (index + 1).ToString("00", CultureInfo.InvariantCulture) + "  " + text).ToArray());
        var prompt = $"Pick 1-{lines.Count}:";
        _choice = new Choice(prompt, lines.Count, take, false);
        Log.Write([prompt]);
    }

    /// <summary>
    /// checkpoint: where the loaded song starts. The numbers are the ones the main window shows ("checkpoint 3"), while the
    /// timeline counts the song's beginning as 1, so a typed number is one more inside.
    /// </summary>
    private void Checkpoint(CmdLine line, bool copy)
    {
        if (_host.Timeline is not { } timeline) { Write("No song is loaded."); return; }
        var song = _host.LoadedSongName ?? "The loaded song";
        var marks = timeline.CheckpointsVerified ? timeline.Checkpoints : [];
        if (marks.Count <= 1) { Write($"{song} has no checkpoints."); return; }
        string Name(int number) => number <= 1 ? "the beginning"
            : marks.FirstOrDefault(mark => mark.Number == number) is { SourceFloor: int floor }
                ? $"checkpoint {number - 1} (tile {floor})" : $"checkpoint {number - 1}";
        var word = line.Rest.Trim();
        // Listing is all everywhere in this console; -list says the same thing (user rule: two ways, one meaning).
        if (word.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            Show([.. marks.Select(mark => (mark.Number == _host.Checkpoint ? "> " : "  ") + Name(mark.Number))], copy);
            return;
        }
        if (word.Length == 0) { Show([$"{song} starts at {Name(_host.Checkpoint)}, of {marks.Count - 1}."], copy); return; }
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
            Write($"'{word}' is not a checkpoint. Use a number from 1 to {marks.Count - 1}, or first, next, previous or last.");
            return;
        }
        if (wanted > marks.Count) { Write($"{song} has {marks.Count - 1} checkpoints."); return; }
        WithSettingsClosed(false, () =>
        {
            if (_host.MoveCheckpoint(wanted) is { } reason) Write(reason);
            else Show([$"{song} starts at {Name(wanted)}."], copy);
        });
    }

    /// <summary>
    /// Why a tile cannot be started from, or null when it can: the checks ParsedTimeline.GetStartIndexForTile makes, said in
    /// English the console's way. go and pin both use it, so a pin is always a tile go can take.
    /// </summary>
    public static string? TileProblem(ParsedTimeline timeline, int tile, string song)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.HitFloors is not { Count: > 0 } floors) return "The song has no tile numbers. Sync the maps and load it again.";
        if (tile >= floors[^1]) return $"{song} has tiles 1 to {floors[^1] - 1}.";
        var index = floors.ToList().BinarySearch(tile);
        if (index < 0) return $"Tile {tile} is a midspin tile, so starting there is not verified.";
        // Play begins at the hit after that tile; if that one only releases a long note, there is nothing to press first.
        if (index + 1 < floors.Count && ParsedTimeline.Closes(timeline.KindOf(index + 1)))
            return $"Tile {tile} is inside a long note, so starting there is not verified.";
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
            Write(add ? "pin takes a tile, or all to list the pins, as in pin 120." : "unpin takes a pinned tile, as in unpin 120; Tab offers them.",
                $"Usage: {line.Command.Usage}");
            return;
        }
        var named = line.Arguments.Count == 2 ? line.Arguments[0] : "now";
        CmdSong? song;
        if (named.Equals("now", StringComparison.OrdinalIgnoreCase)) song = LoadedSong();
        else if ((song = Song(named)) is null) Write($"No song has the id {named}. playlist shows the ids.");
        if (song is null) return;
        var title = BaseTitle(song);
        var pins = _host.PinsOf(song.Family);
        var word = line.Arguments[^1];
        if (add && word.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (pins.Count == 0) Write($"{title} has no pins. pin <tile> adds one.");
            else Show([.. pins.Select((tile, at) => $"pin {at + 1} · tile {tile}")], copy);
            return;
        }
        if (!int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var tile) || tile < 1)
        {
            Write($"'{word}' is not a tile. A tile is a whole number of 1 or more.");
            return;
        }
        if (!add)
        {
            if (!pins.Contains(tile)) { Write($"Tile {tile} of {title} is not pinned. pin all lists the pins."); return; }
            if (_host.SetPins(song.Family, pins.Where(each => each != tile).ToArray()) is { } failed) { Write(failed); return; }
            Write($"Unpinned tile {tile} of {title} and its edits.");
            return;
        }
        if (pins.Contains(tile)) { Write($"Tile {tile} of {title} is already pinned."); return; }
        if (song.Entry.Status != MapParseStatus.Supported || song.Entry.Timeline is not { } timeline)
        {
            Write($"{song.Title} cannot be read, so it has no tiles to pin.");
            return;
        }
        if (TileProblem(timeline, tile, title) is { } problem) { Write(problem); return; }
        var next = pins.Append(tile).Order().ToArray();
        if (_host.SetPins(song.Family, next) is { } reason) { Write(reason); return; }
        Write($"Pinned tile {tile} of {title} and its edits: pin {Array.IndexOf(next, tile) + 1} of {next.Length}.");
    }

    /// <summary>The pins of the song a line names by id, or of the loaded song, as Tab offers them.</summary>
    private IEnumerable<string> PinWords(string? id)
    {
        var song = id is null || id.Equals("now", StringComparison.OrdinalIgnoreCase)
            ? _host.LoadedId is { } loaded ? Song(loaded) : null
            : Song(id);
        return song is null ? [] : _host.PinsOf(song.Family).Select(tile => tile.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>go: the practice start, by tile number. Whole tiles only, and only ones this song presses.</summary>
    private void Go(CmdLine line)
    {
        var word = line.Rest.Trim();
        if (word.Length == 0) { Write("go takes a tile number, as in go 120; Tab offers the song's pins."); return; }
        if (!int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var tile) || tile < 1)
        {
            Write($"'{word}' is not a tile. A tile is a whole number of 1 or more.");
            return;
        }
        WithSettingsClosed(false, () =>
            Write(_host.StartAtTile(tile) is { } reason ? reason : $"Starting from tile {tile}."));
    }

    /// <summary>
    /// update: looks first and says what it found, and only then asks. Taking it closes the app, so nothing may happen
    /// behind the user's back (user rule for anything that reaches out).
    /// </summary>
    /// <summary>version: this build, and whether it is the latest one (user request), which means asking the repository.</summary>
    private void Version(bool copy)
    {
        IReadOnlyList<string> about = [$"Visualizer {AppInfo.Version}", $"Applied {AppInfo.VersionDate}", $"Built   {AppInfo.BuildTime}"];
        RunJob("Version", (progress, done) => _host.CheckUpdate(null, progress, check =>
        {
            var latest = check.Problems.Count > 0 ? "Latest  could not be checked: " + check.Problems[0]
                : check.Behind == 0 ? "Latest  yes, this is the latest version"
                : $"Latest  no, {check.Target} is waiting; update takes it";
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
            Write($"Going to a chosen version needs -force, as in update -force -version:{asked}.");
            return;
        }
        var linker = _host.FindLinker().Installed is not null;
        var along = linker ? " The linker in the game is updated with it." : "";
        RunJob("Update", (progress, done) => _host.CheckUpdate(asked == "latest" ? null : asked, progress, check =>
        {
            if (check.Problems.Count > 0) { done(check.Problems); return; }
            if (!check.Found) { done([$"There is no version {asked} on the repository."]); return; }
            if (!force)
            {
                if (check.Behind == 0) { done([$"This copy is already the latest version ({check.Mine})."]); return; }
                if (check.Changed > 0 || check.Unpublished > 0)
                {
                    done([$"Version {check.Target} is waiting, but this copy has its own {Own(check)}, so it was not updated.",
                        "update -force takes it and throws them away."]);
                    return;
                }
                done([$"Version {check.Target} is waiting; this copy is {check.Mine}."]);
                Confirm(yes, $"Update to {check.Target}? The app closes and opens again on the new version.{along}", answer =>
                {
                    if (!answer) { Write("Cancelled. This copy stays as it is."); return; }
                    Write(_host.ApplyUpdate(false, null) is { } reason ? reason : "Closing to update. The app comes back on the new version.");
                });
                return;
            }
            if (check.Same && check.Changed == 0)
            {
                done([$"This copy is already {check.Target}."]);
                return;
            }
            done([]);
            // -force always asks (user rule): the parser keeps -proceed away from it, so this question is never skipped.
            var lost = check.Changed > 0 || check.Unpublished > 0 ? $" This copy's {Own(check)} are thrown away." : "";
            Confirm(false, $"Go to {check.Target} by force? The risk is yours: an older version may not read what a newer one saved.{lost} "
                + $"The app closes and opens again on {check.Target}.{along}", answer =>
            {
                if (!answer) { Write("Cancelled. This copy stays as it is."); return; }
                Write(_host.ApplyUpdate(true, asked == "latest" ? null : asked) is { } reason ? reason
                    : $"Closing to go to {check.Target}. The app comes back on it.");
            });
        }));

        static string Own(UpdateCheck check) => string.Join(" and ", new[]
        {
            check.Changed > 0 ? $"{check.Changed} changed file(s)" : null,
            check.Unpublished > 0 ? $"{check.Unpublished} commit(s) not on the repository" : null
        }.OfType<string>());
    }

    private void SetHidden(CmdLine line, bool hide)
    {
        if (SongFor(line) is not { } song) return;
        if (song.Hidden == hide)
        {
            Write($"{song.Id} {song.Title} is already {(hide ? "hidden" : "shown")}.");
            return;
        }
        WithSettingsClosed(false, () =>
            Write(_host.SetHidden(song.Id, hide) is { } reason ? reason : $"{(hide ? "Hidden" : "Shown")}: {song.Id} {song.Title}."));
    }

    private void Edit(CmdLine line, bool yes)
    {
        // Nothing named means the loaded song, which is what edit is nearly always for.
        if (SongFor(line, fallback: "now") is not { } song) return;
        WithSettingsClosed(yes, () =>
        {
            Write($"Opening the editor for {song.Title}.");
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
            Write("A name is needed.", $"Usage: {line.Command.Usage}");
            return;
        }
        if (!wanted.Any(char.IsLetterOrDigit))
        {
            Write("A name needs a letter or a digit in it.");
            return;
        }
        // The slot prefix is how the list marks an edit, so it is kept whatever is typed.
        var prefix = song.Slot is { } slot ? slot + "*" : "";
        if (wanted.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > 0) wanted = wanted[prefix.Length..];
        var next = prefix + wanted;
        if (string.Equals(next, song.Title, StringComparison.Ordinal))
        {
            Write($"{song.Title} is already its name.");
            return;
        }
        var family = song.Slot is null ? "" : " Its original and every edit under it are renamed with it.";
        Confirm(yes, $"Rename \"{song.Title}\" to \"{next}\"?" + family, answer =>
        {
            if (!answer) { Write("Cancelled."); return; }
            if (_host.RenameSong(song.Id, wanted) is { } reason) { Write(reason); return; }
            Write($"{song.Id} is now \"{next}\".");
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
            Write("The note editor is not open, so there is nothing to override.", "Open it with edit first.");
            return;
        }
        var json = line.Rest.Trim();
        if (json.Length == 0)
        {
            Write("JSON is needed.", $"Usage: {line.Command.Usage}", "note -json prints it in the shape override reads.");
            return;
        }
        Confirm(yes, "Put these lanes into the editor? The editor's own undo takes it back.", answer =>
        {
            if (!answer) { Write("Cancelled."); return; }
            Write(_host.OverrideNotes(json) is { } reason ? "Nothing was changed. " + reason : "The lanes are in the editor.");
        });
    }

    private void Remove(CmdLine line, bool yes)
    {
        if (SongFor(line) is not { } song) return;
        if (song.Kind != "edited")
        {
            Write("Only edited songs can be removed. delete removes any song for good.");
            return;
        }
        var note = _host.HeldSong is { } held ? $" restore will bring this one back instead of {held}." : " restore can bring it back.";
        Confirm(yes, $"Remove {song.Title}?{note}", answer =>
        {
            if (!answer) { Write("Cancelled."); return; }
            Write(_host.RemoveEdit(song.Id) is { } reason ? reason : $"Removed {song.Id} {song.Title}.");
        });
    }

    private void Restore(bool yes)
    {
        if (_host.HeldSong is not { } held)
        {
            Write("There is nothing to restore.");
            return;
        }
        Confirm(yes, $"Restore {held}?", answer =>
        {
            if (!answer) { Write("Cancelled."); return; }
            Write(_host.Restore(out var slot) is { } reason ? reason : $"Restored {held} into slot {slot}.");
        });
    }

    private void Delete(CmdLine line, bool yes)
    {
        if (SongFor(line) is not { } song) return;
        var after = song.Kind == "custom" ? " restore cannot bring it back; the next sync copies it again." : " restore cannot bring it back.";
        Confirm(yes, $"Delete {song.Title} for good?{after}", answer =>
        {
            if (!answer) { Write("Cancelled."); return; }
            Write(_host.DeleteSong(song.Id) is { } reason ? reason : $"Deleted {song.Id} {song.Title}.");
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
                var other = notes[i + 1];
                steps.Add((noteHits[i] + 1, noteHits[i + 1] + 1, $"{Math.Min(note.Tile, other.Tile)}-{Math.Max(note.Tile, other.Tile)}{Hold(note, other)}", note.HitTimeMs));
                i++;
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
                Write($"A range is needed after the song, as in note {line.Arguments[0]} all.");
            else Write("The syntax of the command is incorrect.", NoteUsage);
            return;
        }
        var range = line.Arguments[^1];
        var json = line.Has("-json");
        if (line.Arguments.Count == 2 && !line.Arguments[0].Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            if (Song(line.Arguments[0]) is not { } song)
            {
                Write($"No song has the id {line.Arguments[0]}. playlist shows the ids.");
                return;
            }
            if (song.Entry.Status != MapParseStatus.Supported || song.Entry.Timeline is not { } timeline)
            {
                Write($"{song.Title} cannot be read, so it has no notes.");
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
            Write("No song is loaded. Name one, as in note <id> all, or pick one on the main window first.");
            return;
        }
        Confirm(yes, $"The song is not loaded. Load \"{toLoad}\" first?", answer =>
        {
            if (!answer)
            {
                Write("Canceled.");
                return;
            }
            if (!_host.LoadSong(out var message) || _host.Timeline is null)
            {
                Write(("The song could not be loaded. " + message).TrimEnd());
                return;
            }
            Write($"Loaded \"{toLoad}\".");
            CheckSettings(() => _host.NowNotes, range, json, yes, copy);
        });
    }

    private const string NoteUsage = "Usage: note [-proceed] [-json] [-copy] [<id>] <all | a..b | a.. | ..b | n | -n>";

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
        Confirm(yes, "There are settings that are not applied yet. Apply the settings?", answer =>
        {
            if (answer)
            {
                if (_host.ApplyPendingSettings() is { } error)
                {
                    Write("The settings could not be applied. " + error);
                    return;
                }
                Write("Settings applied.");
            }
            else Write("Using the applied settings.");
            PrintNotes(source(), range, json, copy);
        });
    }

    /// <summary>How long a range may be read as before the notes are counted; only the spelling is checked there.</summary>
    private const int NoteCountGuess = 1_000_000;

    private void PrintNotes(CmdNoteSource? source, string range, bool json, bool copy)
    {
        if (source is not { Timeline: var timeline })
        {
            Write("No song is loaded.");
            return;
        }
        IReadOnlyList<VisualizerNote> notes;
        IReadOnlyList<int> noteHits;
        // Only the language differs, so a placement problem reads in English like the rest of the console.
        var settings = _host.Settings with { Language = Language };
        try
        {
            notes = PlaybackSession.GenerateSongNotes(timeline, settings, out noteHits);
            // An edit decides the lanes and chords, as when it plays; printed as it stands, whatever lanes the screen shows.
            if (source.Layout is { } layout) notes = NoteEditLayout.Apply(notes, noteHits, layout, settings, out noteHits, anyTile: true);
        }
        catch (Exception error) when (error is NotePlacementException or ArgumentException)
        {
            Write("The notes could not be placed. " + error.Message);
            return;
        }
        // Numbers are source hits; auto hits and long note ends have no note of their own.
        var hits = timeline.HitTimesMs.Count;
        if (!TryParseRange(range, hits, out var first, out var last))
        {
            Write("The syntax of the command is incorrect.", NoteUsage);
            return;
        }
        if (first > hits)
        {
            Write($"There is no note {first}. The song has {hits} notes.");
            return;
        }
        var until = Math.Min(last ?? hits, hits);
        var lines = json ? FormatNotesJson(notes, noteHits, timeline, first, until) : FormatNotes(notes, noteHits, first, until);
        if (lines.Count == 0)
        {
            Write("There are no notes to press in that range.");
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
            if (notes[i].StartsChord && i + 1 < notes.Count) group = ++chord;
            else if (i > 0 && notes[i - 1].StartsChord) group = chord;
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
            Write(line.Arguments.Count == 0 ? $"A setting is needed, as in {line.Command.Name} general.language. property lists them."
                : "The syntax of the command is incorrect.", $"Usage: {line.Command.Usage}");
            return null;
        }
        var name = line.Arguments[0];
        if (SettingPaths.Find(name) is not { } path)
        {
            var section = SettingPaths.Sections.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Write(section.Name is null || !SettingPaths.IsOpen(section.Name, _host.Settings) ? $"'{name}' is not a setting. property lists the sections."
                : SettingPaths.In(section.Name).FirstOrDefault() is { } first
                    ? $"{section.Name} is a section. Add a setting, as in {first.Name}; property {section.Name} lists them."
                    : section.About);
            return null;
        }
        if (!SettingPaths.IsOpen(path.Section, _host.Settings))
        {
            Write("Experimental settings open while advanced.experimental is true.");
            return null;
        }
        return path;
    }

    private void Set(CmdLine line)
    {
        if (line.Arguments.Count == 1 && SettingPaths.Find(line.Arguments[0]) is { } bare && SettingPaths.IsOpen(bare.Section, _host.Settings))
        {
            Write($"A value is needed. property {bare.Name} shows what it takes.");
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
        if (what == "inspect")
        {
            // It presses keys into the game, so it always asks (user rule), even though every rule is checked first.
            Confirm(line.Has("-proceed"), "link inspect presses the lane key of every note the visualizer draws into the game, with No Fail on. Go ahead?",
                answer => Show(answer ? _host.GameSyncInspect(true) : ["Nothing was pressed."], copy));
            return;
        }
        Show(what switch
        {
            "uninspect" => _host.GameSyncInspect(false),
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
            Write("all stands alone, since it already names every area.");
            return null;
        }
        if (items.FirstOrDefault(item => !CmdCatalog.Areas.Contains(item)) is { } unknown)
        {
            Write($"'{unknown}' is not an area. {line.Command.Name} takes {string.Join(", ", line.Command.KeywordNames)}.");
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
            Write("The linker is not installed, so there is nothing to check. Install it first: install linker.");
            return;
        }
        RunJob("Scan", (progress, done) => _host.ScanNow(areas, progress, results =>
        {
            var lines = new List<string>();
            foreach (var area in results)
            {
                if (area.NotInstalled)
                {
                    lines.Add($"{area.Name,-8}not installed; install linker puts it in");
                    continue;
                }
                var found = area.Problems.Count == 0 ? "ok" : area.Problems.Count == 1 ? "1 problem" : $"{area.Problems.Count} problems";
                lines.Add($"{area.Name,-8}{found}{(area.Checked is { } what ? " · " + what : "")}");
                lines.AddRange(area.Problems.Take(MaximumProblems).Select(problem => "  " + problem));
                if (area.Problems.Count > MaximumProblems) lines.Add($"  … and {area.Problems.Count - MaximumProblems} more");
            }
            var broken = results.Where(area => area.Problems.Count > 0).Select(area => area.Name).ToArray();
            var redo = broken.Where(CmdCatalog.Reinstallable.Contains).ToArray();
            // Advice only (user rule): scannow never puts anything right itself.
            if (redo.Length > 0) lines.Add($"Run install -reinstall {string.Join(", ", redo)} to put {(redo.Length == 1 ? "it" : "them")} right.");
            if (broken.Contains("edits")) lines.Add("An edit is your own work, so it is not reinstalled: remove takes a broken one away.");
            if (broken.Contains("data")) lines.Add("Your data is not reinstalled: reset puts the settings back.");
            if (broken.Length == 0) lines.Add("Everything checked is whole.");
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
            Write($"install needs one of: {string.Join(", ", line.Command.KeywordNames)}.", $"Usage: {line.Command.Usage}");
            return;
        }
        var every = named.Count == CmdCatalog.Areas.Count;
        if (!line.Has("-reinstall"))
        {
            if (named is ["linker"]) InstallLinker(yes);
            else Write("Only the linker is installed on its own; the rest come with this copy. install -reinstall puts a part back.");
            return;
        }
        var linker = _host.FindLinker().Installed is not null;
        // all is this version in full: what came with the copy, and the linker when it is there.
        var parts = every ? CmdCatalog.Reinstallable.Where(part => part is "source" or "app" || (part == "linker" && linker)).ToArray() : named;
        if (parts.FirstOrDefault(part => !CmdCatalog.Reinstallable.Contains(part)) is { } own)
        {
            Write(own == "edits" ? "Edits are your own work, so there is nothing to reinstall. remove takes a broken one away."
                : "Your data is your own, so there is nothing to reinstall. reset puts the settings back.", "Nothing was done.");
            return;
        }
        if (parts.Contains("linker") && !linker)
        {
            Write("The linker is not installed. Install it first: install linker.");
            return;
        }
        if (parts.Contains("source") && line.Has("-proceed"))
        {
            // Agreed with the user: putting the files back always asks, since this copy's own changes go with it.
            Write("Putting the source back always asks first, since this copy's own changes go; -proceed cannot answer for it.");
            return;
        }
        var restart = parts.Contains("source") || parts.Contains("app");
        var question = $"Reinstall {string.Join(", ", parts)}?"
            + (parts.Contains("maps") ? " The custom maps are built again from the map folder, so songs no longer in it leave the list." : "")
            + (restart ? " The app closes and opens again." : "");
        if (!parts.Contains("source"))
        {
            Confirm(yes, question, Go);
            return;
        }
        // The files that will go back are shown before the question (user rule), so nothing is lost by surprise.
        RunJob("Scan", (progress, done) => _host.ScanNow(["source"], progress, results =>
        {
            var changes = results.SelectMany(area => area.Problems).ToArray();
            done(changes.Length == 0 ? ["Every file of the source is as this version has it."]
                : ["These go back to this version:", .. changes.Take(MaximumProblems).Select(change => "  " + change),
                    .. changes.Length > MaximumProblems ? new[] { $"  … and {changes.Length - MaximumProblems} more" } : []]);
            Confirm(false, question, Go);
        }));

        void Go(bool answer)
        {
            if (!answer) { Write("Cancelled. Nothing was reinstalled."); return; }
            if (parts.Contains("maps"))
                RunJob("Map rebuild", (progress, done) => _host.Resync(progress, count =>
                {
                    done([$"The custom maps are built again. {count} songs in the library."]);
                    Then();
                }));
            else Then();
        }

        void Then()
        {
            var rest = parts.Where(part => part != "maps").ToArray();
            if (rest.Length == 0) return;
            if (restart)
                Write(_host.ReinstallAndRestart(rest) is { } reason ? reason : "Closing to reinstall. The app comes back when it is done.");
            else RunJob("Linker", (progress, done) => _host.ReinstallLinker(progress, done));
        }
    }

    /// <summary>install linker: the one write into the game's folder, and only after a yes (AGENTS.md rule 2).</summary>
    private void InstallLinker(bool yes)
    {
        var place = _host.FindLinker();
        if (place.GameDirectory is null)
        {
            Write("The game folder was not found, so nothing was written.",
                "A folder counts only with Unity Mod Manager in it; the map folder in the settings is what points at the Steam library.");
            return;
        }
        if (place.Source is null)
        {
            Write("The linker itself was not found, so nothing was written.",
                @"It ships with the app in plugins\linker beside it; build the app again (build.ps1) or update to bring it back.");
            return;
        }
        var version = place.SourceVersion is { Length: > 0 } made ? " " + made : "";
        if (place.Installed is { } installed)
        {
            if (string.Equals(installed, place.SourceVersion, StringComparison.Ordinal))
            {
                Write($"The linker{version} is already installed in {place.Target}.", "Nothing was written.");
                return;
            }
            var now = installed.Length == 0 ? "of an unknown version" : installed;
            Confirm(yes, $"The linker in {place.Target} is {now}. Replace it with{version}?", answer => Run(answer, place));
            return;
        }
        Confirm(yes, $"Install the linker{version} into {place.Target}? Only its own two files are written; other mods are left alone.",
            answer => Run(answer, place));

        void Run(bool answer, LinkerPlace ready)
        {
            if (!answer) { Write("Cancelled."); return; }
            RunJob("Linker install", (progress, done) => done(_host.InstallLinker(ready, progress) is { } reason
                ? [reason]
                : [$"Installed into {ready.Target}.",
                    "Restart the game, switch the mod on in Unity Mod Manager (Ctrl+F10), then link start here."]));
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
                Write($"{path.Name} is already {SettingPaths.Format(path, current)}.");
                return;
            }
            if (Problem(next) is { } problem)
            {
                Write("Not changed. " + problem);
                return;
            }
            Apply(current, next);
        });
    }

    /// <summary>Why a settings snapshot cannot be applied, in English, or null: the same checks the settings panel makes.</summary>
    private static string? Problem(VisualizerSettings settings)
    {
        var worded = settings with { Language = Language };
        var errors = worded.ValidateFields();
        if (errors.Count > 0) return string.Join(" ", errors.Select(error => error.Message).Distinct());
        if (!Themes.ThemeCatalog.Exists(settings.Theme)) return $"There is no theme '{settings.Theme}'.";
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
            Write("Not changed. " + reason);
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
        static string Shown(string value) => value.Length == 0 ? "(empty)" : value;
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
                Write($"'{name}' is not a section. property lists them.");
                return;
            }
            if (!SettingPaths.IsOpen(section, settings))
            {
                Write("Experimental settings open while advanced.experimental is true.");
                return;
            }
            // A section without settings (experimental, for now) says what it holds instead.
            if (!SettingPaths.In(section).Any())
            {
                Write(SettingPaths.Sections.First(s => s.Name == section).About);
                return;
            }
            Show(SettingPaths.In(section).Select(path => $"{path.Name,-30}{SettingPaths.Format(path, settings)}"
                + (SettingPaths.LockedBy(path, settings) is null ? "" : "  (locked)")).ToArray(), copy);
            return;
        }
        if (SettingFor(line, exact: true) is not { } setting) return;
        var lines = new List<string> { $"{setting.Name} is {SettingPaths.Format(setting, settings)} now." };
        var choices = SettingPaths.Choices(setting, settings);
        if (setting.Kind is SettingKind.Toggle or SettingKind.Choice) lines.Add("Values: " + string.Join(", ", choices) + ".");
        else lines.Add("Takes " + SettingPaths.Describe(setting) + ".");
        if (SettingPaths.LockedBy(setting, settings) is { } locked) lines.Add(locked);
        Show(lines, copy);
    }

    private void Import(CmdLine line, bool yes)
    {
        var json = line.Rest.Trim();
        if (json.Length == 0)
        {
            Write("Paste the settings JSON after import, as in: import { \"NoteTravelTimeMs\": 700 }");
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
                Write("The JSON could not be used. " + error.Message.Replace(Environment.NewLine, " ", StringComparison.Ordinal));
                return;
            }
            if (Problem(imported) is { } problem)
            {
                Write("The JSON could not be used. " + problem);
                return;
            }
            if (keptPaths) Write("The map and data folders keep this PC's values.");
            var changes = Changes(current, imported);
            if (changes.Count == 0)
            {
                Write("Nothing changes.");
                return;
            }
            Log.Write(changes);
            Confirm(yes, "Apply these changes?", answer =>
            {
                if (!answer) { Write("Cancelled."); return; }
                Write(_host.ApplySettings(imported) is { } reason ? "Not changed. " + reason : "Applied. rollback undoes it.");
            });
        });
    }

    private void RollBack(bool yes)
    {
        WithSettingsClosed(yes, () =>
        {
            if (_host.LastChange is not { } change)
            {
                Write("There is nothing to roll back.");
                return;
            }
            // What each touched setting is now, and what it goes back to.
            var lines = Changes(change.After, change.Before);
            Write("The last change goes back:");
            Log.Write(lines.Count > 0 ? lines : ["(no visible setting)"]);
            Confirm(yes, "Roll back?", answer =>
            {
                if (!answer) { Write("Cancelled."); return; }
                Write(_host.RollBack() is { } reason ? reason : "Rolled back.");
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
                Write("The settings are already the defaults.");
                return;
            }
            Confirm(yes, $"Reset every setting to its default ({count} {(count == 1 ? "change" : "changes")})? The map and data folders stay; rollback undoes it.", answer =>
            {
                if (!answer) { Write("Cancelled."); return; }
                Write(_host.ApplySettings(defaults) is { } reason ? "Not changed. " + reason : "The settings are reset.");
            });
        });
    }

    private void Do(CmdLine line)
    {
        if (line.Arguments.Count != 1 || !CmdCatalog.ActionNames.Contains(line.Arguments[0].ToLowerInvariant()))
        {
            Write(line.Arguments.Count == 0 ? "An action is needed." : $"'{string.Join(' ', line.Arguments)}' is not an action.",
                "Actions: " + string.Join(", ", CmdCatalog.ActionNames) + ".");
            return;
        }
        var action = line.Arguments[0].ToLowerInvariant();
        if (action == "view-playlist-details")
        {
            // The song list details window.
            WithSettingsClosed(false, () => Write(_host.OpenPlaylist() ? "The song list changes are applied." : "The song list closed without changes."));
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
            Write($"{path.Name} is already none.");
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
                    Write("No folder was picked.");
                    return;
                }
                value = folder;
            }
            var next = SettingPaths.With(current, path, value);
            if (next == current)
            {
                Write($"{path.Name} is already {SettingPaths.Format(path, current)}.");
                return;
            }
            if (Problem(next) is { } problem)
            {
                Write("Not changed. " + problem);
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
            Write("The settings are closed.");
            then();
            return;
        }
        Confirm(yes, "The settings have changes that are not applied. Apply them before they close?", answer =>
        {
            if (_host.CloseSettings(apply: answer) is { } error)
            {
                Write("The settings could not be applied, so the window stays open. " + error);
                return;
            }
            Write(answer ? "The changes are applied and the settings are closed." : "The changes are dropped and the settings are closed.");
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
            Write("There is nothing to copy yet.");
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
            Write("There is nothing to copy there.");
            return;
        }
        if (showing) Write([.. lines]);
        if (!copying) return;
        try
        {
            _host.CopyText(string.Join(Environment.NewLine, lines));
            Write(picked.Length == 1 ? "Copied 1 message." : $"Copied {picked.Length} messages.");
        }
        catch (ExternalException error) { Write("Could not copy to clipboard. " + error.Message); }
    }

    /// <summary>
    /// A blank in a stored prompt. <see cref="Question"/> is what the console asks for it (English, like the rest of the
    /// console; the answer is the user's own words and may be any language). <see cref="Placeholder"/> is left in its
    /// place when the blank is skipped, and tells a language model to ask rather than invent.
    /// </summary>
    private sealed record PromptBlank(string Name, string Question, string Placeholder);

    /// <summary>A stored prompt: its lines, where <c>{name}</c> marks a blank, and what those blanks are.</summary>
    private sealed record StoredPrompt(IReadOnlyList<string> Lines, IReadOnlyList<PromptBlank> Blanks);

    /// <summary>
    /// Prompts kept for pasting into a language model. Nothing here is sent anywhere and nothing runs: the console only
    /// fills the blanks in and hands the text over (user rule).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, StoredPrompt> Prompts =
        new Dictionary<string, StoredPrompt>(StringComparer.OrdinalIgnoreCase)
        {
            ["note-lanes"] = new(
            [
                "You rewrite the lanes of a rhythm chart. The user gives you JSON printed by a practice visualizer and says",
                "in their own words how they want the notes laid out. Return the same JSON with only the lanes changed.",
                "",
                "The JSON is { \"notes\": [ { \"n\": 1, \"tile\": 3, \"ms\": 0 }, ... ] }, one object per note the player presses:",
                "  n     the note number. Never change it, never add one, never leave one out.",
                "  tile  the lane the note is on, 1 to 8 from the left. This is the only value you may change.",
                "  ms    when the note is hit. Never change it; it is there so the numbers can be checked.",
                "  chord two notes that share this number are played together. Keep them together or remove both.",
                "",
                "Rules you must not break:",
                "  1. Keep every note. The output has exactly the same n values, in the same order, as the input.",
                "  2. Lanes are 1 to 8. Do not use a lane the user has not asked for.",
                "  3. The two notes of a chord must sit on lanes next to each other (such as 3 and 4).",
                "  4. Two notes less than 50 ms apart must not share a lane; the same hand cannot hit it twice that fast.",
                "  5. If the user asks for something that breaks a rule, say so in one sentence and change nothing.",
                "",
                "Answer with the JSON alone, no explanation and no code fence, unless rule 5 applies.",
                "",
                "How I want it:",
                "{request}",
                "",
                "{notes}"
            ],
            [
                new("request", "How do you want the lanes laid out?",
                    ">>> WRITE THE REQUEST HERE. If this line is still here, ask for it instead of guessing. <<<"),
                new("notes", "Which notes? (/paste, /note -json 1..64, or paste the JSON itself)",
                    ">>> PUT THE note -json OUTPUT HERE. If this line is still here, ask for it instead of guessing. <<<")
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
        { "paste", "note", "id", "title", "info", "song", "playlist", "get", "property", "export", "version", "date", "fps", "link",
          "cmd", "help", "option", "keyword", "whoami" };

    /// <summary>prompt: shows one stored prompt, or lists their titles; one with blanks is filled in first.</summary>
    private void ShowPrompt(CmdLine line, bool copy)
    {
        var wanted = line.Rest.Trim();
        if (wanted.Length == 0 || wanted.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            Show(["Prompts: " + string.Join(", ", Prompts.Keys), "Type prompt <title> to see one."], copy);
            return;
        }
        if (!Prompts.TryGetValue(wanted, out var prompt))
        {
            Write($"There is no prompt called {wanted}.", "Prompts: " + string.Join(", ", Prompts.Keys));
            return;
        }
        if (prompt.Blanks.Count == 0)
        {
            Show(prompt.Lines, copy);
            return;
        }
        _filling = new Filling(wanted, prompt, new string?[prompt.Blanks.Count], 0, copy);
        Write($"{wanted}: {prompt.Blanks.Count} blanks to fill in.",
            "Type the text, or /paste and /note -json to put an output in. /skip leaves a blank, /back /again /cancel move around.");
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
                    Write($"{filling.Title} is cancelled.");
                    Leave(filling, printed: true);
                    return;
                case "back":
                    if (filling.At == 0)
                    {
                        Write("This is the first blank.");
                        AskBlank();
                        return;
                    }
                    _filling = filling with { At = filling.At - 1 };
                    AskBlank();
                    return;
                case "again":
                    _filling = filling with { Values = new string?[filling.Blanks().Count], At = 0 };
                    Write("Starting over.");
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
                Write(error ?? "That is not a command.");
                AskBlank();
                return;
            }
            if (!BlankCommands.Contains(parsed.Command.Name))
            {
                Write($"{parsed.Command.Name} cannot fill a blank: only commands that print and stop can.");
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
                Write($"{parsed.Command.Name} asks a question, so it cannot fill a blank.");
                AskBlank();
                return;
            }
            while (caught.Count > 0 && caught[^1].Length == 0) caught.RemoveAt(caught.Count - 1);
            if (caught.Count == 0)
            {
                Write($"{parsed.Command.Name} printed nothing.");
                AskBlank();
                return;
            }
            Write($"Took {caught.Count} line{(caught.Count == 1 ? "" : "s")} from {parsed.Command.Name}.");
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
            lines.Add($"    … {own.Length - (ShortLines - 1)} more lines …");
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
        Confirm(false, "Copy this?", answer =>
        {
            if (!answer)
            {
                Write($"{filling.Title} is cancelled.");
                Leave(filling, printed: true);
                return;
            }
            var text = string.Join(Environment.NewLine, Filled(filling, whole: true));
            try
            {
                _host.CopyText(text);
                Write($"Copied the filled prompt ({Filled(filling, whole: true).Count} lines).");
                _filling = null;
            }
            catch (ExternalException error)
            {
                Write("Could not copy to clipboard. " + error.Message);
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
        Write("What was filled in, so it is not lost:");
        foreach (var (value, index) in written)
        {
            var own = value!.Split(Environment.NewLine);
            Write($"  {filling.Blanks()[index].Name}: " + (own.Length == 1 ? own[0] : $"{own[0]} … ({own.Length} lines)"));
        }
    }


    // ---- the notes for the next version ---------------------------------------------------------------------------

    /// <summary>
    /// report: hands the note to the console window, which opens it in its own editor (user rule). The session only
    /// checks the title and reads what is there; nothing about the note is edited here.
    /// </summary>
    /// <summary>What the editor's command line takes, for report -col.</summary>
    private static readonly (string Command, string Means)[] EditorCommands =
    [
        (":w  :write", "Write the note and stay in it."),
        (":q  :quit", "Leave it. Changes that were not written are asked about first."),
        (":wq  :x", "Write it and leave."),
        (":copy", "Copy what is selected, or the whole note when nothing is."),
        (":d  :delete", "Remove the note, after asking.")
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
            Write("-force always asks first, so -proceed cannot answer for it.");
            return;
        }

        if (verb.Length == 0 || !line.Command.KeywordNames.Contains(verb, StringComparer.OrdinalIgnoreCase))
        {
            Write(verb.Length == 0
                ? $"report needs one of: {string.Join(", ", line.Command.KeywordNames)}."
                : $"'{verb}' is not one of report's words. It takes {string.Join(", ", line.Command.KeywordNames)}.");
            return;
        }
        switch (verb.ToLowerInvariant())
        {
            case "create":
            case "write":
                if (line.Options.TryGetValue("-inspect", out var which) || after.TryGetValue("-inspect", out which))
                    InspectReport(title, verb.Equals("create", StringComparison.OrdinalIgnoreCase), which, Has("-force"), Has("-proceed"));
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
            Write($"report {verb} <title> needs a title.");
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
                Write(found is null ? $"{title} was never written." : StateLine(Standing(title)));
                return;
            case "ask":
            {
                // report ask <title> > <reason>, split the way rename is: a title may hold spaces but never ">".
                var cut = title.IndexOf('>');
                if (cut < 0) { Write("Say what is needed: report ask <title> > <reason>"); return; }
                var asked = title[..cut].Trim();
                var why = title[(cut + 1)..].Trim();
                if (why.Length == 0) { Write("Say what is needed: report ask <title> > <reason>"); return; }
                if (_host.MarkReport(asked, "asked", why) is { } problem2) Write(problem2);
                else Write($"\"{asked}\" is waiting on an answer.");
                return;
            }
            case "done":
            case "undone":
            {
                if (found is null) { Write($"{title} was never written."); return; }
                var wanted = verb.Equals("done", StringComparison.OrdinalIgnoreCase) ? "done" : "open";
                if (_host.MarkReport(title, wanted, null) is { } trouble) Write(trouble);
                else Write(wanted == "open" ? $"\"{title}\" is open again." : $"\"{title}\" is done in {AppInfo.Version}.");
                return;
            }
            case "erase":
                if (found is null) { Write($"{title} was never written."); return; }
                Confirm(Has("-proceed"), $"Delete \"{title}\" ({found.Count} lines)?", answer =>
                {
                    if (!answer) { Write("Nothing was deleted."); return; }
                    if (DeleteReport(title) is { } trouble) Write(trouble);
                    else Write($"\"{title}\" deleted.");
                });
                return;
            case "print":
                if (found is null) { Write($"{title} was never written."); return; }
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
        if (title.Length == 0) { Write($"report {word} <title> needs a title."); return; }
        var found = _host.ReadReport(title, out var problem);
        if (problem is { } reason) { Write(reason); return; }
        if (!creating)
        {
            if (found is null) { Write($"There is no note called \"{title}\". report create {title} starts one."); return; }
            Edit(title, found);
            return;
        }
        if (found is null) { Edit(title, null); return; }
        if (!force)
        {
            Write($"\"{title}\" is already there. report write {title} adds to it, or report create -force {title} replaces it.");
            return;
        }
        // Replacing is the only way a note loses what is in it, so it asks (user rule for anything that throws work away).
        Confirm(yes, $"Replace \"{title}\" ({found.Count} lines)? The editor opens empty and what is there goes when it is saved.",
            answer =>
            {
                if (!answer) { Write("Nothing was replaced."); return; }
                Edit(title, []);
            });
    }

    /// <summary>
    /// report -inspect[:n|*] create / write <title>: writes what link inspect found into a note (user rule). create starts the note with
    /// it, write adds it below what the note holds; n is how many of the latest runs (1, the last one, when nothing follows),
    /// * every run kept since the app opened.
    /// </summary>
    private void InspectReport(string title, bool creating, string? which, bool force, bool yes)
    {
        var word = creating ? "create" : "write";
        if (title.Length == 0) { Write($"report {word} <title> -inspect needs a title."); return; }
        var count = which is null or "" ? 1 : which == "*" ? 0 : int.Parse(which, System.Globalization.CultureInfo.InvariantCulture);
        if (_host.InspectLines(count) is not { } found) { Write("No inspect run is kept. link inspect makes one."); return; }
        var lines = found.ToArray();
        var existing = _host.ReadReport(title, out var problem);
        if (problem is { } reason) { Write(reason); return; }
        void Save(IReadOnlyList<string> text, string done)
        {
            if (_host.WriteReport(title, text) is { } failed) { Write(failed); return; }
            Write(done);
        }
        if (!creating)
        {
            if (existing is null) { Write($"There is no note called \"{title}\". report -inspect create {title} starts one."); return; }
            Save([.. existing, "", .. lines], $"Added {lines.Length} lines of inspect results below \"{title}\".");
            return;
        }
        if (existing is null) { Save(lines, $"Wrote {lines.Length} lines of inspect results to \"{title}\"."); return; }
        if (!force)
        {
            Write($"\"{title}\" is already there. report -inspect write {title} adds to it, or report -inspect create -force {title} replaces it.");
            return;
        }
        Confirm(yes, $"Replace \"{title}\" ({existing.Count} lines) with the inspect results?",
            answer =>
            {
                if (!answer) { Write("Nothing was replaced."); return; }
                Save(lines, $"Replaced \"{title}\" with {lines.Length} lines of inspect results.");
            });
    }

    /// <summary>Opens the note in the console's own editor, with the lines it is to hold.</summary>
    private void Edit(string title, IReadOnlyList<string>? lines)
    {
        // The window may not be listening (a test, or the console closing): then the note simply is not opened.
        if (ReportRequested is null)
        {
            Write("The console cannot open a note right now.");
            return;
        }
        var state = _host.ReportNotes()
            .FirstOrDefault(note => note.Title.Equals(title, StringComparison.OrdinalIgnoreCase))?.Done ?? "";
        Log.Report = new ReportDraft(title, lines?.ToArray() ?? [], Changed: false, Done: state);
        // The keys are on the editor's own bottom line, so the console does not repeat them (user rule).
        Write($"{title}: {(lines is null ? "a new note" : $"{lines.Count} lines")}.");
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
            Write(word.Length > 0 ? $"No note holds \"{word}\"." : "No notes are written yet.");
            return;
        }
        var width = Math.Max(8, notes.Max(note => note.Title.Length));
        var open = notes.Count(note => note.State != "done");
        var lines = new List<string>();
        foreach (var note in notes)
        {
            lines.Add($"  {note.Title.PadRight(width)}  {(note.Version.Length > 0 ? note.Version : "-"),-7}"
                + (note.State == "done" ? $"done {note.Version}" : note.State).PadRight(12) + $"{note.Lines} lines");
            // The reason is a sentence in the language the settings are set to, so it goes under its row, not in a column.
            if (note.State == "asked" && note.Why.Length > 0) lines.Add($"  {new string(' ', width)}  {note.Why}");
        }
        lines.Add($"  {open} of {notes.Count} still open.");
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
        if (done.Length == 0) { Write("No note is marked done."); return; }
        Confirm(yes, $"Delete {done.Length} note(s) already done ({string.Join(", ", done)})?", answer =>
        {
            if (!answer) { Write("Nothing was deleted."); return; }
            var gone = _host.ClearDoneReports();
            Write(gone.Count == 0 ? "Nothing was deleted." : $"Deleted {gone.Count} note(s): {string.Join(", ", gone)}.");
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
            Write("Say which name goes where: report rename <old title> > <new title>");
            return;
        }
        var title = rest[..cut].Trim();
        var name = rest[(cut + 1)..].Trim();
        if (title.Length == 0 || name.Length == 0)
        {
            Write("Say which name goes where: report rename <old title> > <new title>");
            return;
        }
        // The same rule the store keeps, said here so the console does not name a storage type (ICmdHost stays its own).
        if (name.Length > 64 || !name.All(letter => char.IsLetterOrDigit(letter) || letter is ' ' or '-' or '_'))
        {
            Write("A note title may only be letters, digits, spaces, dashes and underscores.");
            return;
        }
        Confirm(yes, $"Rename \"{title}\" to \"{name}\"?", answer =>
        {
            if (!answer) { Write("The name stays."); return; }
            if (_host.RenameReport(title, name) is { } trouble) Write(trouble);
            else Write($"\"{title}\" is now \"{name}\".");
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
        _host.DeleteReport(title, out var problem) ? null : problem ?? $"{title} was never written.";

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
            Write("Copied to clipboard.");
        }
        catch (ExternalException error) { Write("Could not copy to clipboard. " + error.Message); }
    }

    private bool NoArguments(CmdLine line)
    {
        if (line.Arguments.Count == 0) return true;
        Syntax<object>(line);
        return false;
    }

    private T? Syntax<T>(CmdLine line) where T : class
    {
        Write("The syntax of the command is incorrect.", $"Usage: {line.Command.Usage}");
        return null;
    }

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
            else if (command.Argument == CmdArgument.Title)
            {
                // A title may have spaces, so everything after the options is the title, matched anywhere in it (user rule).
                start = options + 1 < words.Count ? words[options + 1].Start : line.Length;
                var title = line[start..];
                if (words.Skip(1).Take(options).Any(word => word.Word.Equals("-id", StringComparison.OrdinalIgnoreCase)))
                    return _host.Songs.Select(song => song.Id).Order(StringComparer.Ordinal)
                        .Where(id => id.StartsWith(title, StringComparison.OrdinalIgnoreCase)).ToArray();
                return new[] { "origin", "edited" }.Where(keyword => keyword.StartsWith(title, StringComparison.OrdinalIgnoreCase))
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
