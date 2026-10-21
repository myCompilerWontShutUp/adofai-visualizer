using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using Visualizer.App;
using Visualizer.App.Commands;
using Visualizer.App.Views;
using Visualizer.Application.Sync;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using Visualizer.Infrastructure.Cache;
using Visualizer.Infrastructure.Sync;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

/// <summary>
/// The read-only game sync link (docs/game-sync-plan.md). Nothing here needs the game or the mod: the readings are made up,
/// and the pipe test plays the mod's part itself, so the whole path is checked without anything running in the game.
/// </summary>
internal static class GameSyncTests
{
    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }

    private static GameSyncMessage Reading(double clock, double position, string state = "PlayerControl",
        bool paused = false, int floor = 3, double speed = 1, string path = @"C:\maps\main.adofai", int protocol = GameSyncLink.Protocol) =>
        new(protocol, clock, position, state, paused, floor, speed, path);

    /// <summary>A window whose console speaks English, which these checks read; the app's own default is Korean.</summary>
    private static MainWindow EnglishWindow() => English(new MainWindow { Left = -20000, Top = -20000, ShowActivated = false });

    public static IEnumerable<(string Name, Action Run)> Create(string artifactRoot)
    {
        yield return ("The linker is installed into the game's own mod folder and nothing else there is touched", () =>
        {
            // No game and no mod are needed: a folder shaped like the game's is enough, and the installer only ever
            // copies two files. Everything here is under the test artifacts folder (AGENTS.md rule 5).
            var root = Path.Combine(artifactRoot, "linker");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            var game = Path.Combine(root, "steamapps", "common", "A Dance of Fire and Ice");
            var maps = Path.Combine(root, "steamapps", "workshop", "content", "977950");
            Directory.CreateDirectory(maps);
            Directory.CreateDirectory(Path.Combine(game, "A Dance of Fire and Ice_Data", "Managed"));
            var source = Path.Combine(root, "built");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "AdofaiVisualizerSync.dll"), "mod");
            File.WriteAllText(Path.Combine(source, "Info.json"), "{\"Id\":\"AdofaiVisualizerSync\",\"Version\":\"0.9.2\"}");
            Equal(LinkerInstaller.VersionAt(source), "0.9.2");

            // Without Unity Mod Manager nothing is written at all: the mod would never be loaded.
            Assert(!LinkerInstaller.HasModManager(game), "No mod manager yet");
            // Not null, since this PC may have the game at its usual path; what matters is that this folder is not offered.
            Assert(LinkerInstaller.FindGame(maps) != game, "A folder without the mod manager is not offered");
            Throws<InvalidOperationException>(() => LinkerInstaller.Install(game, source));
            Assert(!Directory.Exists(Path.Combine(game, "Mods")), "Not even the folder was made");

            var manager = Path.Combine(game, "A Dance of Fire and Ice_Data", "Managed", "UnityModManager");
            Directory.CreateDirectory(manager);
            File.WriteAllText(Path.Combine(manager, "UnityModManager.dll"), "umm");
            Assert(LinkerInstaller.HasModManager(game), "Now it is there");
            // The map folder and the game sit in one Steam library, which is how the game folder is found.
            Equal(LinkerInstaller.FindGame(maps), game);
            Assert(LinkerInstaller.InstalledVersion(game) is null, "Nothing is installed yet");

            // Another mod's folder and a file of the game's own must come out of this untouched.
            var other = Path.Combine(game, "Mods", "SomeOtherMod");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "Info.json"), "someone else");
            File.WriteAllText(Path.Combine(game, "A Dance of Fire and Ice.exe"), "game");

            var target = LinkerInstaller.Install(game, source);
            Equal(target, Path.Combine(game, "Mods", "AdofaiVisualizerSync"));
            Equal(string.Join(" ", Directory.GetFiles(target).Select(Path.GetFileName).Order(StringComparer.Ordinal)),
                "AdofaiVisualizerSync.dll Info.json");
            Equal(LinkerInstaller.InstalledVersion(game), "0.9.2");
            Equal(File.ReadAllText(Path.Combine(other, "Info.json")), "someone else");
            Equal(File.ReadAllText(Path.Combine(game, "A Dance of Fire and Ice.exe")), "game");
            Equal(string.Join(" ", Directory.GetDirectories(Path.Combine(game, "Mods")).Select(Path.GetFileName).Order(StringComparer.Ordinal)),
                "AdofaiVisualizerSync SomeOtherMod");
        });

        yield return ("The linker ships built with this version, in the repository and beside the app", () =>
        {
            // User rule: another PC installs the linker with install linker alone and never builds it (that needs the game's
            // assemblies and the .NET Framework tools). build-mod.ps1 writes the release build into plugins\linker, which is
            // committed, and the app build carries it beside the exe. After a version change, build-mod.ps1 must run again.
            var plugin = Path.Combine(WorkspaceMapCache.FindRepositoryRoot(AppContext.BaseDirectory), LinkerInstaller.PluginFolder);
            Equal(LinkerInstaller.VersionAt(plugin), AppInfo.Version);
            var dll = Path.Combine(plugin, LinkerInstaller.FileNames[0]);
            Equal(AssemblyName.GetAssemblyName(dll).Version?.ToString(3), AppInfo.Version);
            // The first place the console looks is beside the app, and the copy there is the committed one.
            var beside = Path.Combine(AppContext.BaseDirectory, LinkerInstaller.PluginFolder);
            Equal(LinkerInstaller.FindSource(AppContext.BaseDirectory, null), beside);
            Assert(File.ReadAllBytes(Path.Combine(beside, LinkerInstaller.FileNames[0])).AsSpan().SequenceEqual(File.ReadAllBytes(dll)),
                "The app carries the committed linker");
        });

        yield return ("The uninstaller takes a plugin folder whole, only the app's files from a chosen folder, and never follows a link", () =>
        {
            // User rule: uninstalling is done in PowerShell only (uninstall.ps1) and the plugins go cleanly, the files Unity
            // Mod Manager adds beside a mod included. The script is dot-sourced so only its functions run, on folders under
            // the test artifacts: nothing of this PC's game, data or copy is looked at (AGENTS.md rule 5).
            var root = Path.Combine(artifactRoot, "uninstall");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true); // .NET removes a junction without entering it
            string Make(string path, string text = "x")
            {
                var full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
                return full;
            }
            var game = Path.Combine(root, "game");
            Make(@"game\Mods\AdofaiVisualizerSync\AdofaiVisualizerSync.dll");
            Make(@"game\Mods\AdofaiVisualizerSync\AdofaiVisualizerSync.dll.39107.cache");
            Make(@"game\Mods\AdofaiVisualizerSync\Info.json", "{\"Id\":\"AdofaiVisualizerSync\",\"Version\":\"1.0.1\"}");
            var otherMod = Make(@"game\Mods\OtherMod\Info.json", "{\"Id\":\"OtherMod\"}");
            var umm = Make(@"game\A Dance of Fire and Ice_Data\Managed\UnityModManager\Params.xml", "<Mod Id=\"AdofaiVisualizerSync\" />");
            // A folder of the same name that belongs to someone else (its Info.json says so) is not ours.
            var game2 = Path.Combine(root, "game2");
            var stranger = Make(@"game2\Mods\AdofaiVisualizerSync\Info.json", "{\"Id\":\"SomeoneElse\"}");
            var outside = Make(@"outside\keep.txt", "kept");
            var store = Path.Combine(root, "store");
            Make(@"store\settings.json");
            Make(@"store\maps\custom\1\main.adofai");
            var chosen = Path.Combine(root, "chosen");
            foreach (var mine in new[] { "settings.json", "songs.json", @"maps\edited\1\edit.json", @"reports\note.txt", "songs-1a2b.tmp" })
                Make(Path.Combine("chosen", mine));
            var theirs = new[] { Make(@"chosen\photo.png"), Make(@"chosen\letters\a.txt") };
            var copy = Path.Combine(root, "copy");
            Make(@"copy\AGENTS.md");
            Make(@"copy\Visualizer.sln");
            var packed = Make(@"copy\.git\objects\pack\a.pack");
            File.SetAttributes(packed, FileAttributes.ReadOnly); // git's own files are read-only
            var app = Make(@"copy\src\app.exe");
            // Stand-ins for the Start menu and the desktop: Install.cmd's folder with its shortcut, and a desktop with one
            // shortcut into the copy and one to something else.
            var menu = Path.Combine(root, "menu", "ADOFAI Visualizer");
            var desk = Path.Combine(root, "desk");
            Directory.CreateDirectory(menu);
            Directory.CreateDirectory(desk);

            var script = Path.Combine(WorkspaceMapCache.FindRepositoryRoot(AppContext.BaseDirectory), "uninstall.ps1");
            var steps = Path.Combine(root, "steps.ps1");
            File.WriteAllText(steps, $$"""
                $ErrorActionPreference = 'Stop'
                . '{{script}}' -Plan
                $ErrorActionPreference = 'Stop'
                New-Item -ItemType Junction -Path '{{store}}\link' -Target '{{Path.GetDirectoryName(outside)}}' | Out-Null
                New-Item -ItemType Junction -Path '{{copy}}\link' -Target '{{Path.GetDirectoryName(outside)}}' | Out-Null
                $failed = New-Object System.Collections.Generic.List[string]
                $shell = New-Object -ComObject WScript.Shell
                foreach ($pair in @(@('{{menu}}\ADOFAI Visualizer.lnk', '{{app}}'), @('{{desk}}\ADOFAI Visualizer.lnk', '{{app}}'), @('{{desk}}\Other.lnk', '{{outside}}'))) {
                    $link = $shell.CreateShortcut($pair[0]); $link.TargetPath = $pair[1]; $link.Save()
                }
                $root = '{{copy}}' # the copy being removed, as $PSScriptRoot is when the script runs from its own folder
                $links = @(OwnShortcuts @('{{menu}}', '{{desk}}', '{{root}}\nowhere'))
                'links ' + (($links | ForEach-Object { Split-Path (Split-Path $_ -Parent) -Leaf }) -join ',')
                Remove-Shortcuts $links $failed
                $found = @(InstalledPlugins @('{{game}}', '{{game2}}') @(PluginIds))
                'found ' + (($found | ForEach-Object { $_.Folder + $_.Version }) -join '|')
                foreach ($plugin in $found) { Remove-Tree $plugin.Folder $failed }
                Remove-Tree '{{store}}' $failed
                'mine ' + ((StoreFiles '{{chosen}}') -join ',')
                Remove-StoreFiles '{{chosen}}' $failed
                Remove-Tree '{{copy}}' $failed
                'failed ' + ($failed -join '|')
                """);
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", steps }) start.ArgumentList.Add(argument);
            using var run = System.Diagnostics.Process.Start(start)!;
            run.StandardInput.Close(); // a question that slipped through would read no answer, which keeps everything
            var output = run.StandardOutput.ReadToEnd();
            var errors = run.StandardError.ReadToEnd();
            Assert(run.WaitForExit(60000) && run.ExitCode == 0, $"The steps ran: {output} {errors}");
            var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Assert(!lines.Any(line => line.StartsWith("The visualizer on this computer", StringComparison.Ordinal)), "Dot-sourcing runs nothing: " + output);
            Equal(string.Join(" / ", lines), string.Join(" / ", "links ADOFAI Visualizer,desk", $@"found {game}\Mods\AdofaiVisualizerSync 1.0.1",
                "mine settings.json,songs.json,maps,reports,songs-1a2b.tmp", "failed"));
            Assert(!Directory.Exists(menu), "The Start menu folder goes once its shortcut has");
            Equal(string.Join(",", Directory.EnumerateFiles(desk).Select(Path.GetFileName)), "Other.lnk"); // only shortcuts into the copy go

            Assert(!Directory.Exists(Path.Combine(game, "Mods", "AdofaiVisualizerSync")), "The linker folder is gone, the mod manager's copy with it");
            Assert(File.Exists(otherMod) && File.Exists(stranger), "Another mod, and a folder of the same name that is not ours, stay");
            Equal(File.ReadAllText(umm), "<Mod Id=\"AdofaiVisualizerSync\" />"); // the mod manager's own file is not touched
            Assert(!Directory.Exists(store), "A store of ours goes whole");
            Assert(!Directory.Exists(copy), "The copy goes, read-only files of git included");
            Equal(File.ReadAllText(outside), "kept"); // the links were removed, not followed
            Assert(theirs.All(File.Exists) && Directory.Exists(chosen), "In a chosen folder, files that are not the app's stay");
            Equal(string.Join(",", Directory.EnumerateFileSystemEntries(chosen).Select(Path.GetFileName).Order(StringComparer.Ordinal)), "letters,photo.png");
        });

        yield return ("Install.cmd and Uninstall.cmd are double-click files that cmd and PowerShell both read right", () =>
        {
            // User choice A: installing and removing without typing PowerShell. Install.cmd is one file read twice (cmd runs
            // its first lines, which hand the file to PowerShell, and PowerShell skips them as a comment); Uninstall.cmd
            // removes the folder it sits in, so its work is one line that ends cmd before it would read on.
            var repository = WorkspaceMapCache.FindRepositoryRoot(AppContext.BaseDirectory);
            foreach (var name in new[] { "Install.cmd", "Uninstall.cmd" })
            {
                var bytes = File.ReadAllBytes(Path.Combine(repository, name));
                Assert(bytes.All(b => b < 128), name + " is plain ASCII, which cmd reads the same under every code page");
                var text = System.Text.Encoding.ASCII.GetString(bytes);
                Assert(text.Replace("\r\n", "").IndexOf('\n') < 0, name + " has CRLF line ends");
            }
            var install = File.ReadAllText(Path.Combine(repository, "Install.cmd"));
            Assert(install.StartsWith("<# :\r\n@echo off", StringComparison.Ordinal), "cmd sees a label, PowerShell a comment");
            var uninstall = File.ReadAllLines(Path.Combine(repository, "Uninstall.cmd")).Where(line => !line.StartsWith("@rem", StringComparison.Ordinal)).ToArray();
            Assert(uninstall.Length == 1 && uninstall[0].StartsWith("@cd /d \"%~dp0..\" & ", StringComparison.Ordinal)
                && uninstall[0].Contains("uninstall.ps1", StringComparison.Ordinal) && uninstall[0].EndsWith("& exit /b", StringComparison.Ordinal),
                "One line, started outside the folder, that exits before cmd reads on: " + string.Join(" | ", uninstall));
            // PowerShell parses the whole installer, batch lines included, and so do both scripts.
            var check = Path.Combine(artifactRoot, "parse.ps1");
            File.WriteAllText(check, $$"""
                foreach ($file in @('{{repository}}\Install.cmd', '{{repository}}\uninstall.ps1', '{{repository}}\update.ps1')) {
                    $errors = $null
                    [Management.Automation.Language.Parser]::ParseInput([IO.File]::ReadAllText($file), [ref]$null, [ref]$errors) | Out-Null
                    '{0} {1}' -f (Split-Path $file -Leaf), $errors.Count
                }
                """);
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", check }) start.ArgumentList.Add(argument);
            using var run = System.Diagnostics.Process.Start(start)!;
            var output = run.StandardOutput.ReadToEnd();
            Assert(run.WaitForExit(60000), "The parse ran");
            Equal(string.Join(" / ", output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
                "Install.cmd 0 / uninstall.ps1 0 / update.ps1 0");
        });

        yield return ("Sync link takes good readings and measures the link", () =>
        {
            var link = new GameSyncLink();
            Assert(link.Report() is null, "Nothing to report before anything arrives");
            Assert(!link.HasReadings, "And no readings");
            // Arrival every 10 ms with the position keeping up: the mod clock runs 5 ms behind, which is a constant delay.
            for (var i = 0; i < 20; i++)
                Equal(link.Add(Reading(i * 10, 1000 + i * 10), i * 10 + 5), null);
            var report = link.Report()!;
            Equal(report.Accepted, 20);
            Equal(report.Rejected, 0);
            Equal(report.IntervalMedianMs, 10);
            // The two clocks start apart, so only the variation counts: a constant 5 ms offset shows as no variation.
            Equal(report.LatencyP95Ms, 0);
            Equal(report.LatencyMaxMs, 0);
            Equal(report.PositionRate, 1);
            Equal(report.Last!.Floor, 3);
            Assert(link.HasReadings, "The link has readings");
        });

        yield return ("Sync link refuses readings it cannot trust", () =>
        {
            var link = new GameSyncLink();
            // A different message shape is refused rather than guessed at.
            Assert(link.Add(Reading(0, 0, protocol: GameSyncLink.Protocol + 1), 0)!.Contains("message shape", StringComparison.Ordinal),
                "A newer shape is named");
            Assert(link.Add(Reading(0, double.NaN), 1) is not null, "A position that is not a number");
            Assert(link.Add(Reading(0, 0, floor: -2), 4) is not null, "A tile below -1");
            Assert(link.Add(Reading(0, 0, state: " "), 5) is not null, "An empty state");
            Assert(link.Add(Reading(0, 0, path: ""), 6) is not null, "An empty level path");
            Assert(link.Add(Reading(-1, 0), 7) is not null, "A mod clock before zero");
            var report = link.Report()!;
            Equal(report.Accepted, 0);
            Equal(report.Rejected, 6);
            Assert(report.FirstRejection!.Contains("message shape", StringComparison.Ordinal), "The first reason is kept");
            // The practice speed is only shown, so an odd one never costs the position.
            var shown = new GameSyncLink();
            Equal(shown.Add(Reading(0, 0, speed: 0), 0), null);
            Equal(shown.Add(Reading(1, 0, speed: 99), 1), null);

            // A mod clock that goes backwards is a different run, not a later reading.
            var second = new GameSyncLink();
            Equal(second.Add(Reading(100, 0), 0), null);
            Assert(second.Add(Reading(90, 0), 1)!.Contains("backwards", StringComparison.Ordinal), "Going backwards is refused");
            Equal(second.Report()!.Accepted, 1);
            // Unless the mod connected again: a restarted game counts from zero, and what the last connection said goes.
            second.SetMod("1.0.1");
            second.SetLevel(new GameSyncLevel("L", [0, 100]));
            second.BeginConnection();
            Assert(second.ModVersion is null && second.Level is null && second.Latest.Reading is null, "The last connection's word is dropped");
            Equal(second.Add(Reading(5, 0), 2), null);
            Assert(second.Report()!.Changes[^1].Contains("connected again", StringComparison.Ordinal), "And the reconnection is noted");
        });

        yield return ("Sync link notes state changes and a slowed song", () =>
        {
            var link = new GameSyncLink();
            link.Add(Reading(0, -3000, "Countdown"), 0);
            link.Add(Reading(10, -2000, "Countdown"), 10);
            link.Add(Reading(20, 0, "PlayerControl"), 20);
            link.Add(Reading(30, 500, "PlayerControl", paused: true), 30);
            link.Add(Reading(40, 500, "PlayerControl"), 40);
            var changes = link.Report()!.Changes;
            Equal(changes.Count, 3);
            Assert(changes[0].Contains("Countdown -> PlayerControl", StringComparison.Ordinal), "The countdown ending is noted");
            Assert(changes[1].Contains("paused", StringComparison.Ordinal), "Pausing is noted");
            Assert(changes[2].Contains("resumed", StringComparison.Ordinal), "Resuming is noted");

            // Half speed: the song position moves half as fast as real time, which the report states as it is.
            var slow = new GameSyncLink();
            for (var i = 0; i < 10; i++) slow.Add(Reading(i * 10, i * 5, speed: 0.5), i * 10);
            Equal(slow.Report()!.PositionRate, 0.5);
        });

        yield return ("Sync link follows the tiles the game passes and starts over on a restart", () =>
        {
            var link = new GameSyncLink();
            // Tile 0 is the start tile, so the first crossing is the first tile the player actually hits.
            link.Add(Reading(0, 0, floor: 0), 0);
            link.Add(Reading(10, 300, floor: 1), 10);
            link.Add(Reading(20, 300, floor: 1), 20);
            link.Add(Reading(30, 600, floor: 2), 30);
            link.Add(Reading(40, 900, floor: 4), 40);
            Sequence(link.Crossings.Select(c => c.Floor), 1, 2, 4);
            Equal(link.Crossings[2].PositionMs, 900d);
            Equal(link.Speed, 1d);

            // The song put back to the start is a new run, so the tiles of the old one go.
            link.Add(Reading(50, -2000, "Countdown", floor: 0), 50);
            Equal(link.Crossings.Count, 0);
            link.Add(Reading(60, 300, floor: 1), 60);
            Sequence(link.Crossings.Select(c => c.Floor), 1);
            // So is another level.
            link.Add(Reading(70, 300, floor: 1, path: "D:"), 70);
            Equal(link.Crossings.Count, 0);
            Equal(link.LevelPath, "D:");
        });

        yield return ("The follower puts the playhead on the game's clock and only moves the clock", () =>
        {
            var settings = new VisualizerSettings { StartDelayMs = 0, GlobalDisplayOffsetMs = 0 };
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            // Hits on tiles 1, 2, 4, 5; the game counts its zero 1200 ms before ours.
            var timeline = new ParsedTimeline([0, 300, 600, 900], hitFloors: [1, 2, 4, 5]);
            session.LoadParsedTimeline(timeline, settings);
            session.Toggle();
            session.Reset(settings);
            var follower = new GameSyncFollower();

            // A song the game does not have, and one whose tiles do not line up, are both refused rather than guessed at.
            Assert(follower.Prepare(new GameSyncLevel("L", [0, 5, 5, 5, 5, 5]), session) is not null, "Tiles that disagree are refused");
            Equal(follower.State, GameSyncFollowState.WrongSong);
            Assert(follower.OffsetMs is null, "And no offset is kept");

            double[] game = [1050, 1200, 1500, 1700, 1800, 2100];
            // Prepared from a practice tile, the app has no notes before it: the game playing from the start would leave
            // the screen empty for as long as that section is into the song, which is refused instead of shown.
            session.ResetAtTile(settings, 4);
            Assert(session.StartHitIndex > 0, "Prepared from a later hit");
            var refused = follower.Prepare(new GameSyncLevel("L", game), session);
            Assert(refused is not null && refused.Contains("not the start of the song", StringComparison.Ordinal),
                "A later start is refused: " + refused);
            session.Reset(settings);

            Equal(follower.Prepare(new GameSyncLevel("L", game), session), null);
            Equal(follower.OffsetMs, 1200d);

            // The game is at its tile 2 (our hit 1, 300 ms): the playhead goes there without a start key.
            clock.NowMs = 10_000;
            var reading = new GameSyncMessage(GameSyncLink.Protocol, 0, 1500, "PlayerControl", false, 2, 1, "L");
            // One reading alone does not show a running clock: the game may be sitting on its start screen.
            Assert(!follower.Follow(reading with { PositionMs = 1490 }, 9_990, 10_000, session), "One reading starts nothing");
            Equal(session.State, PlaybackState.Waiting);
            Assert(follower.Follow(reading, 10_000, 10_000, session), "It starts following");
            Equal(session.State, PlaybackState.Playing);
            Assert(Math.Abs(session.TimelineTimeMs - 300) < 0.001, "At our hit 1: " + session.TimelineTimeMs);
            Equal(follower.State, GameSyncFollowState.Following);

            // A reading is as old as the time since it arrived, so the position is carried forward by that much.
            clock.NowMs = 10_050;
            follower.Follow(reading, 10_000, 10_050, session);
            Assert(Math.Abs(session.TimelineTimeMs - 350) < 0.5, "Carried forward 50 ms: " + session.TimelineTimeMs);

            // A small difference is pulled back a little at a time, never snapped, so the game's frame rate is not judder.
            clock.NowMs = 11_000;
            session.MoveClock(1000);
            var drifted = new GameSyncMessage(GameSyncLink.Protocol, 0, 2180, "PlayerControl", false, 5, 1, "L");
            follower.Follow(drifted, 11_000, 11_000, session);
            var afterPull = session.TimelineTimeMs;
            // The game says 980; the playhead moves part of the way there, not all of it.
            Assert(afterPull < 1000 && afterPull > 980, $"Pulled part of the way from 1000 towards 980: {afterPull}");
            Equal(follower.Snaps, 1);
            Equal(follower.Pulls, 2);

            // A big difference is a restart, not drift: it goes there at once, and whatever the last attempt marked
            // ahead of the new point goes with it — the app sees lane keys while the game has the focus.
            clock.NowMs = 12_000;
            session.MoveClock(5000);
            var judge = new NotePressJudge();
            session.Presses = judge;
            var lane = session.Notes.First(note => note.Tile != session.Notes[0].Tile).Tile;
            var marked = judge.Press(session, lane, 1e9);
            Assert(marked is > 0, "A later note is marked while the last attempt ran");
            Assert(follower.Follow(drifted, 12_000, 12_000, session), "A jump happens");
            Assert(Math.Abs(session.TimelineTimeMs - session.TimelineTimeOf(980)) < 1, "Straight to the game: " + session.TimelineTimeMs);

            // The game restarting throws the playhead back before those notes, and the marks must not survive it.
            session.MoveClock(5000);
            Assert(judge.IsPressed(session.Notes, marked!.Value), "The mark is there before the restart");
            var restarted = new GameSyncMessage(GameSyncLink.Protocol, 0, 1200, "Countdown", false, 0, 1, "L");
            Assert(follower.Follow(restarted, 12_000, 12_000, session), "The restart moves it back");
            Assert(!Enumerable.Range(0, session.Notes.Count).Any(i =>
                    judge.IsPressed(session.Notes, i) && session.Notes[i].HitTimeMs + session.Offsets.TotalMs >= session.TimelineTimeMs),
                "Nothing ahead of the playhead stays marked");
            // Those game presses did not feed the one-time display calibration, so the notes never shifted under them.
            Equal(session.CalibrationOffsetMs, 0d);
            Equal(session.Offsets.TotalMs, 0d);
            // And a calibration made before following is only suspended: switching following off puts it straight back.
            session.ClockFollowsGame = false;
            session.AddCalibrationSample(40);
            Equal(session.CalibrationOffsetMs, 40d);
            Equal(session.Offsets.TotalMs, 40d);
            session.ClockFollowsGame = true;
            Equal(session.Offsets.TotalMs, 0d);
            Equal(session.CalibrationOffsetMs, 40d);
            session.ClockFollowsGame = false;
            Equal(session.Offsets.TotalMs, 40d);
            session.ClockFollowsGame = true;
            Equal(follower.Snaps, 3);

            // A game over keeps the song running in the game, but the notes stand still where the run ended.
            clock.NowMs = 11_500;
            session.MoveClock(session.TimelineTimeOf(600));
            var stoppedAt = session.TimelineTimeMs;
            var over = new GameSyncMessage(GameSyncLink.Protocol, 0, 3000, "Fail", false, 5, 1, "L", Dead: true);
            Assert(follower.Follow(over, 11_500, 11_500, session), "A game over is taken");
            Equal(follower.State, GameSyncFollowState.GameOver);
            Equal(session.TimelineTimeMs, stoppedAt);
            // The song runs on in the game; the notes must not run on with it.
            clock.NowMs = 11_900;
            var later = new GameSyncMessage(GameSyncLink.Protocol, 0, 3400, "Fail2", false, 5, 1, "L", Dead: true);
            follower.Follow(later, 11_900, 11_900, session);
            Equal(session.TimelineTimeMs, stoppedAt);
            Assert(follower.Lines()[0].Contains("game is over", StringComparison.Ordinal), "And it says so");
            // Starting again puts them back where the game is.
            clock.NowMs = 12_000;
            var again = new GameSyncMessage(GameSyncLink.Protocol, 0, 1200, "Countdown", false, 0, 1, "L");
            Assert(follower.Follow(again, 12_000, 12_000, session), "The restart moves them back");
            Assert(Math.Abs(session.TimelineTimeMs - session.TimelineTimeOf(0)) < 1, "To the start: " + session.TimelineTimeMs);

            // Escape is the player taking the playhead back: following must not resume it from under them.
            session.Pause();
            var stopped = session.TimelineTimeMs;
            Assert(!follower.Follow(drifted, 12_000, 12_100, session), "A pause here is left alone");
            Equal(follower.State, GameSyncFollowState.HeldHere);
            Equal(session.State, PlaybackState.Paused);
            Equal(session.TimelineTimeMs, stopped);
            Assert(follower.Lines()[0].Contains("Resume in the app", StringComparison.Ordinal), "And it says how to go back");
            // Resuming in the app hands it back to the game.
            session.Start();
            Assert(follower.Follow(drifted, 12_200, 12_200, session), "Resuming follows the game again");
            Equal(follower.State, GameSyncFollowState.Following);

            // Anything that re-prepares the notes or moves the song on its own ends following instead of fighting it.
            session.Start();
            follower.Follow(drifted, 12_300, 12_300, session);
            session.ResetAtTile(settings, 4);
            Assert(!follower.Follow(drifted, 12_400, 12_400, session), "A section start ends following");
            Equal(follower.State, GameSyncFollowState.Dropped);
            Assert(follower.OffsetMs is null, "And the offset goes with it");
            // Ending it hands the session back: nothing stays suspended or hidden.
            Assert(!session.ClockFollowsGame, "The session is the app's again");
            Assert(double.IsNegativeInfinity(session.HideNotesBeforeMs), "And no tile stays hidden");
            Assert(follower.Lines()[0].Contains("Use link follow again", StringComparison.Ordinal), "It says how to go back");

            session.Reset(settings);
            Equal(follower.Prepare(new GameSyncLevel("L", game), session), null);
            // The app's own playback speed is refused: the game sets the pace while it is followed.
            session.ApplySettings(settings with { PlaybackSpeedPercent = 50 });
            Assert(follower.Prepare(new GameSyncLevel("L", game), session) is { } paced
                   && paced.Contains("playback speed", StringComparison.Ordinal), "A different pace is refused");
            session.ApplySettings(settings);
            Equal(follower.Prepare(new GameSyncLevel("L", game), session), null);
            session.Toggle();
            session.Toggle();
            session.Start();
            follower.Follow(reading, 12_500, 12_500, session);

            // A paused game holds the notes exactly where it is, however long it stays paused, instead of letting them run
            // on and jump back when it resumes.
            var paused = new GameSyncMessage(GameSyncLink.Protocol, 0, 2180, "PlayerControl", true, 5, 1, "L");
            Assert(follower.Follow(paused, 12_000, 12_000, session), "A paused game puts the playhead where it is");
            Assert(Math.Abs(session.TimelineTimeMs - session.TimelineTimeOf(980)) < 0.001, "On the paused spot: " + session.TimelineTimeMs);
            clock.NowMs = 12_400;
            follower.Follow(paused, 12_000, 12_400, session);
            Assert(Math.Abs(session.TimelineTimeMs - session.TimelineTimeOf(980)) < 0.001, "And it stays there: " + session.TimelineTimeMs);
            Assert(follower.Lines()[0].Contains("paused", StringComparison.Ordinal), "And it says so");
            // A game gone quiet, or never heard from, holds them too: running on alone would only be a guess.
            clock.NowMs = 13_000;
            follower.Follow(drifted, 12_000, 13_000, session);
            var held = session.TimelineTimeMs;
            clock.NowMs = 13_400;
            follower.Follow(drifted, 12_000, 13_400, session);
            Equal(session.TimelineTimeMs, held);
            Assert(follower.Lines()[0].Contains("nothing from the game", StringComparison.Ordinal), "And it says so");
            clock.NowMs = 13_500;
            follower.Follow(null, 0, 13_500, session);
            Equal(session.TimelineTimeMs, held);

            // Only the clock moved: the notes and their times are exactly as prepared.
            Sequence(session.Notes.Select(note => (int)note.HitTimeMs), 0, 300, 600, 900);
            follower.Stop();
            Assert(!follower.Follow(drifted, 12_000, 12_000, session), "Stopped means stopped");
        });

        yield return ("Following holds the notes while the game stands, plays another level or another rate", () =>
        {
            var settings = new VisualizerSettings { StartDelayMs = 0, GlobalDisplayOffsetMs = 0 };
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            session.LoadParsedTimeline(new ParsedTimeline([0, 300, 600, 900], hitFloors: [1, 2, 4, 5]), settings);
            session.Toggle();
            session.Reset(settings);
            var follower = new GameSyncFollower();
            double[] game = [1050, 1200, 1500, 1700, 1800, 2100];
            Equal(follower.Prepare(new GameSyncLevel("L", game), session), null);
            GameSyncMessage At(double position, string path = "L", double pitch = 1, bool dead = false) =>
                new(GameSyncLink.Protocol, 0, position, dead ? "Fail" : "PlayerControl", false, 2, 1, path, pitch, Dead: dead);
            // Each reading arrives as its frame starts, so nothing is carried forward.
            void Frame(double nowMs, GameSyncMessage reading)
            {
                clock.NowMs = nowMs;
                follower.Follow(reading, nowMs, nowMs, session);
            }
            bool OnGame(double position) => Math.Abs(session.TimelineTimeMs - session.TimelineTimeOf(position - 1200)) < 0.001;

            // A game on its start screen sends one position over and over; the notes wait for it to move.
            for (var t = 1000; t <= 1400; t += 50) Frame(t, At(-1000));
            Equal(session.State, PlaybackState.Waiting);
            // The level waiting for its first key reports a position that runs on in real time: that starts nothing.
            GameSyncMessage Waiting(double position) => At(position) with { State = "Start" };
            for (var t = 1410; t <= 1440; t += 10) Frame(t, Waiting(120_000 + t));
            Equal(session.State, PlaybackState.Waiting);
            // A game over starts nothing either.
            Frame(1450, At(-900, dead: true));
            Frame(1500, At(-800, dead: true));
            Equal(session.State, PlaybackState.Waiting);
            Frame(1550, At(-1000));
            Equal(session.State, PlaybackState.Waiting);
            // Once its clock runs, the notes run with it.
            Frame(1560, At(-990));
            Equal(session.State, PlaybackState.Playing);
            Assert(OnGame(-990), "On the game's clock: " + session.TimelineTimeMs);

            // The game goes on to another level: these notes are not its notes, so they stand where they are.
            clock.NowMs = 1600;
            var at = session.TimelineTimeMs;
            Frame(1600, At(-950, path: "M"));
            Frame(1900, At(-650, path: "M"));
            Equal(session.TimelineTimeMs, at);
            // Back on this level, they follow again.
            Frame(2000, At(-500));
            Frame(2016, At(-484));
            Assert(OnGame(-484), "Following again: " + session.TimelineTimeMs);

            // Played faster or slower, the game would have the notes chase it; they wait for 100% instead.
            clock.NowMs = 2100;
            at = session.TimelineTimeMs;
            Frame(2100, At(-400, pitch: 1.2));
            Frame(2400, At(-40, pitch: 1.2));
            Equal(session.TimelineTimeMs, at);
            Frame(2500, At(100));
            Frame(2516, At(116));
            Assert(OnGame(116), "Following again at 100%: " + session.TimelineTimeMs);
            Equal(follower.State, GameSyncFollowState.Following);
            clock.NowMs = 2600;
            follower.Follow(At(200, pitch: 0.5), 2600, 2600, session);
            Assert(follower.Lines()[0].Contains("50%", StringComparison.Ordinal), "The rate is named: " + follower.Lines()[0]);

            // Every new connection sends the level again: the same tiles change nothing.
            clock.NowMs = 2700;
            follower.Follow(At(300), 2700, 2700, session, new GameSyncLevel("L", game.ToArray()));
            follower.Follow(At(316), 2716, 2716, session, new GameSyncLevel("L", game.ToArray()));
            Equal(follower.State, GameSyncFollowState.Following);
            Equal(follower.OffsetMs, 1200d);
            // Other tiles under the same name no longer line up, so following ends instead of keeping the old offset.
            clock.NowMs = 2732;
            Assert(!follower.Follow(At(332), 2732, 2732, session, new GameSyncLevel("L", [0, 5, 5, 5, 5, 5])), "Other tiles end it");
            Equal(follower.State, GameSyncFollowState.LevelChanged);
            Assert(follower.OffsetMs is null && !session.ClockFollowsGame, "And the session is the app's again");
            Assert(follower.Lines()[0].Contains("Use link follow again", StringComparison.Ordinal), "It says how to go back");
        });

        yield return ("After a game over the countdown shows the notes from the checkpoint, and the hidden ones never miss", () =>
        {
            // User report: back at a checkpoint after a game over, the notes to play only showed when the countdown ended, and
            // the hidden ones before the checkpoint went by with red miss marks. The game goes on naming the tile the run died
            // on (5) through the countdown; the checkpoint respawning at tile 3 is where it carries on.
            var settings = new VisualizerSettings { StartDelayMs = 0, GlobalDisplayOffsetMs = 0 };
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            session.LoadParsedTimeline(new ParsedTimeline([0, 300, 600, 900],
                [new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 2, SourceFloor: 3, RespawnFloor: 3)], true, [1, 2, 4, 5]), settings);
            session.Toggle();
            session.Reset(settings);
            var follower = new GameSyncFollower();
            Equal(follower.Prepare(new GameSyncLevel("L", [1050, 1200, 1500, 1700, 1800, 2100]), session), null);
            var judge = new NotePressJudge();
            var misses = new List<int>();
            GameSyncMessage At(double position, string state = "PlayerControl", int floor = 5, bool dead = false) =>
                new(GameSyncLink.Protocol, 0, position, dead ? "Fail" : state, false, floor, 1, "L", Dead: dead);
            void Frame(double nowMs, GameSyncMessage reading)
            {
                clock.NowMs = nowMs;
                follower.Follow(reading, nowMs, nowMs, session);
                judge.CollectMisses(session, 100, misses);
            }
            for (var t = 1000; t <= 2100; t += 16) Frame(t, At(1300 + (t - 1000), floor: t < 1900 ? 4 : 5));
            Frame(2116, At(2216, dead: true));
            misses.Clear();
            // The countdown: the song well before the checkpoint, the tile still the one the run died on.
            for (var t = 3000; t <= 3400; t += 16) Frame(t, At(1300 + (t - 3000), "Countdown"));
            Assert(Math.Abs(session.HideNotesBeforeMs - session.NoteTimeOf(1700 - 1200)) < 0.001,
                $"Hidden only up to the respawn tile: {session.HideNotesBeforeMs}");
            Assert(session.Notes.Any(note => note.HitTimeMs >= session.HideNotesBeforeMs && note.HitTimeMs < session.NoteTimeOf(900)),
                "The note after the checkpoint falls during the countdown");
            Assert(misses.Count == 0, "The note hidden before the checkpoint went by without a miss: " + string.Join(",", misses));
        });

        yield return ("A game over holds the notes until the game starts again, through the reload in between", () =>
        {
            // User report: after dying with the game followed, the notes ran on until the level started again. The song keeps
            // playing in the game through a game over, the fail screen may count as paused, and restarting reloads the level,
            // which leaves the app with nothing to read for a while: none of that may move the notes.
            var settings = new VisualizerSettings { StartDelayMs = 0, GlobalDisplayOffsetMs = 0 };
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            session.LoadParsedTimeline(new ParsedTimeline([0, 300, 600, 900], hitFloors: [1, 2, 4, 5]), settings);
            session.Toggle();
            session.Reset(settings);
            var follower = new GameSyncFollower();
            Equal(follower.Prepare(new GameSyncLevel("L", [1050, 1200, 1500, 1700, 1800, 2100]), session), null);
            GameSyncMessage At(double position, bool dead = false, bool paused = false, int floor = 2) =>
                new(GameSyncLink.Protocol, 0, position, dead ? "Fail" : "PlayerControl", paused, floor, 1, "L", Dead: dead);
            void Frame(double nowMs, GameSyncMessage? reading, double arrivedMs)
            {
                clock.NowMs = nowMs;
                follower.Follow(reading, arrivedMs, nowMs, session);
            }
            for (var t = 1000; t <= 1100; t += 16) Frame(t, At(1300 + (t - 1000)), t);
            Equal(session.State, PlaybackState.Playing);

            // Dead: the notes stop where the run ended, while the game's song plays on under the fail screen.
            Frame(1116, At(1416, dead: true), 1116);
            var ended = session.TimelineTimeMs;
            for (var t = 1132; t <= 2000; t += 16) Frame(t, At(1300 + (t - 1000), dead: true), t);
            Equal(session.TimelineTimeMs, ended);
            // The fail screen counting as paused changes nothing.
            Frame(2100, At(2400, dead: true, paused: true), 2100);
            Equal(session.TimelineTimeMs, ended);
            // Restarting reloads the level: nothing arrives, the last word goes stale, and still nothing moves.
            Frame(2700, At(2400, dead: true, paused: true), 2100);
            Frame(3500, At(2400, dead: true, paused: true), 2100);
            Frame(3600, null, double.NaN);
            Equal(session.TimelineTimeMs, ended);
            // Then the level waits for its first key with its song position running on (measured in the game): still nothing.
            for (var t = 3700; t <= 3900; t += 16) Frame(t, At(120_000 + t) with { State = "Start" }, t);
            Equal(session.TimelineTimeMs, ended);

            // The level starts again from its countdown: the notes go back there and follow it. The first reading may still
            // name the tile the run ended on, which would hide every note before it for the whole attempt; the tile the
            // restart really is on shows them again.
            Frame(4000, At(-1000, floor: 5), 4000);
            Assert(session.HideNotesBeforeMs > session.Notes[0].HitTimeMs, "The stale tile hides notes for a moment");
            Frame(4016, At(-984, floor: 0), 4016);
            Assert(Math.Abs(session.TimelineTimeMs - session.TimelineTimeOf(-984 - 1200)) < 0.001, "Back on the game's clock: " + session.TimelineTimeMs);
            Equal(session.State, PlaybackState.Playing);
            Assert(session.Notes.All(note => note.HitTimeMs >= session.HideNotesBeforeMs), "Every note of the new attempt is drawn");

            // A tile that never comes back (user report, Wont Let You Go after a game error: the game kept naming the tile the
            // last run died on, seconds ahead of the song). The countdown keeps it hidden, but once the player has control a
            // tile that far ahead is not believed, and the notes from where the song is are drawn.
            Frame(4100, At(-700, dead: true, floor: 5), 4100);
            for (var t = 4200; t <= 4296; t += 16) Frame(t, At(-1000 + (t - 4200), floor: 5) with { State = "Countdown" }, t);
            var staleCut = session.NoteTimeOf(2100 - 1200);
            Assert(Math.Abs(session.HideNotesBeforeMs - staleCut) < 0.001, "The countdown trusts the tile: " + session.HideNotesBeforeMs);
            for (var t = 4312; t <= 4408; t += 16) Frame(t, At(-1000 + (t - 4200), floor: 5), t);
            Assert(session.HideNotesBeforeMs <= session.NoteTimeOf(-1000 + 208 - 1200) + 0.001,
                $"Control shows the notes from the song's place on: {session.HideNotesBeforeMs} (the tile said {staleCut})");
        });

        yield return ("link auto finds an edit by its original, and with it on only the linker moves the playhead", () =>
        {
            // User report: an edit of the song the game plays was never picked up. It lives in a slot folder of its own
            // (maps/edited/<song>/<slot>/), but it plays the original's map.
            var game = @"C:\Program Files (x86)\Steam\steamapps\workshop\content\977950\3011005213\main.adofai";
            Assert(MainWindow.GamePlays(game, @"3011005213\main.adofai"), "The original matches");
            Assert(!MainWindow.GamePlays(game, @"3011005213\1\main.adofai"), "An edit's own path does not");
            Assert(MainWindow.GamePlays(game, @"3011005213\1\main.adofai", @"3011005213\main.adofai"), "Its original's does");
            Assert(MainWindow.GamePlays(game.Replace('\\', '/'), "3011005213/main.adofai"), "Either separator");
            Assert(!MainWindow.GamePlays(game, @"011005213\main.adofai"), "Only whole folder names");
            Assert(!MainWindow.GamePlays(game, null, ""), "Nothing to match");
            // User rule: with link auto on, Enter in the game must not bring the start of the song back; the keys that put
            // the playhead anywhere are left to the linker, while switching the app on or opening settings still work.
            foreach (var action in new[] { Visualizer.Application.Input.InputAction.Reset, Visualizer.Application.Input.InputAction.Start,
                         Visualizer.Application.Input.InputAction.Pause, Visualizer.Application.Input.InputAction.PreviousCheckpoint,
                         Visualizer.Application.Input.InputAction.NextCheckpoint })
                Assert(Visualizer.Application.Input.InputPolicy.MovesPlayhead(action), action + " moves the playhead");
            foreach (var action in new[] { Visualizer.Application.Input.InputAction.Toggle, Visualizer.Application.Input.InputAction.OpenSettings,
                         Visualizer.Application.Input.InputAction.LaneKey })
                Assert(!Visualizer.Application.Input.InputPolicy.MovesPlayhead(action), action + " does not");
        });

        yield return ("link auto loads the song the game moves to, once, and a song picked by hand stays", () =>
        {
            // User rule: with link auto on, the song the game moves to is loaded here. An edit plays its original's map; the
            // original is the one loaded, and an edit only when the list has no original.
            Visualizer.Application.Cache.MapCacheEntry Song(string id, string path) => new(id, @"C:\maps", path, id,
                Visualizer.Application.Maps.MapParseStatus.Supported, new ParsedTimeline(new[] { 0d, 500, 1000 }), Array.Empty<string>(), "", "test");
            var original = Song("a", @"3011005213\main.adofai");
            var edit = Song("e", @"3011005213\1\main.adofai");
            var other = Song("b", @"2924697157\main.adofai");
            // Read by an older parser: its times may not be the game's until a sync.
            var stale = Song("s", @"555\main.adofai") with { ParserVersion = "adofai-v1-old" };
            var game = @"C:\Steam\workshop\content\977950\3011005213\main.adofai";
            string? OriginalOf(Visualizer.Application.Cache.MapCacheEntry song) => song.Id == "e" ? original.RelativePath : null;
            Equal(MainWindow.SongFor(game, [edit, original, other], OriginalOf)?.Id, "a");
            Equal(MainWindow.SongFor(game, [edit, other], OriginalOf)?.Id, "e");
            Equal(MainWindow.SongFor(game, [other], OriginalOf), null);

            // The real window on a pipe of its own, driven by its own frames as the app is.
            var name = "adofai-visualizer-sync-test-" + Guid.NewGuid().ToString("N");
            MainWindow.GameSyncPipeName = name;
            var window = EnglishWindow();
            try
            {
                var model = (Visualizer.App.ViewModels.MainViewModel)window.DataContext;
                var host = (ICmdHost)window;
                window.Show();
                Pump();
                model.SetLibrary([original, other, stale]);
                model.LoadChosen(other, null);
                host.GameSync(true, false, false);
                host.GameSyncAuto(true);
                using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
                client.Connect(5000);
                void Send(string line)
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    client.Write(bytes, 0, bytes.Length);
                    client.Flush();
                }
                void Level(string folder) =>
                    Send("{\"v\":2,\"lvl\":\"C:\\\\Steam\\\\" + folder + "\\\\main.adofai\",\"times\":[0,500,1000,1500]}");
                bool Frames(Func<bool> until, int ms = 5000)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (watch.ElapsedMilliseconds < ms)
                    {
                        Pump();
                        if (until()) return true;
                        Thread.Sleep(10);
                    }
                    return false;
                }
                string Status() => string.Join(" ", host.GameSync(false, false, false));

                Level("3011005213");
                Assert(Frames(() => model.LoadedEntry?.Id == "a"), "The game's song is loaded: " + model.LoadedEntry?.Id + " " + Status());
                // Its tiles cannot match (these songs carry no tile numbers), and with link auto on the keys that move the
                // notes are off: the main window says so instead of looking dead (user report).
                Assert(Frames(() => model.Message == model.Text["GameAutoRefused"]), "The refusal shows: " + model.Message);
                // Picked by hand while the game stays on that song: it stays.
                model.LoadChosen(other, null);
                Frames(() => false, 300);
                Equal(model.LoadedEntry?.Id, "b");
                // A song the list does not have changes nothing here, and says why.
                Level("999");
                Assert(Frames(() => Status().Contains("not in the song list", StringComparison.Ordinal)), "It says so: " + Status());
                Equal(model.LoadedEntry?.Id, "b");
                // Back to a song the list has: loaded again, as it is another move.
                Level("3011005213");
                Assert(Frames(() => model.LoadedEntry?.Id == "a"), "Moving back loads it again: " + model.LoadedEntry?.Id);
                // A song read by an older parser says what to do about it.
                Level("555");
                Assert(Frames(() => model.LoadedEntry?.Id == "s" && model.Message == model.Text["GameAutoStaleCache"]),
                    "A stale cache says to sync: " + model.Message);
                // Switched off, the game moving on loads nothing.
                host.GameSyncAuto(false);
                Level("2924697157");
                Frames(() => false, 300);
                Equal(model.LoadedEntry?.Id, "s");
                // The log kept what happened, frame by frame (user request), and no key is in it.
                var kept = host.LogLines();
                Assert(kept.Any(line => line.EndsWith("link: the pipe is open", StringComparison.Ordinal))
                    && kept.Any(line => line.Contains("linker: connected from game process", StringComparison.Ordinal))
                    && kept.Any(line => line.EndsWith(@"level: C:\Steam\3011005213\main.adofai, 4 tiles", StringComparison.Ordinal))
                    && kept.Any(line => line.EndsWith("song: a", StringComparison.Ordinal))
                    && kept.Any(line => line.Contains("auto: ", StringComparison.Ordinal))
                    && kept.All(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d{2}:\d{2}:\d{2}\.\d{3}  ")),
                    "The log: " + string.Join(" | ", kept.TakeLast(12)));
                // load link still names the song the game is playing with link auto off (user report: it did not work then),
                // by the console's eight-character id, not the cache's own key; the level alone is enough.
                Equal(host.GameSongId(out var noId), null);
                Assert(noId is not null && noId.Contains("no id", StringComparison.Ordinal), "A song without an id says so: " + noId);
                model.SongIdMap = new Dictionary<string, string> { ["a"] = "10000001", ["b"] = "10000002", ["s"] = "10000003" };
                Equal(host.GameSongId(out var problem), "10000002");
                Equal(problem, null);

                // Closing the link ends link auto too (user rule): with nothing to follow it goes back to manual.
                host.GameSyncAuto(true);
                // It also puts the song back as loading leaves it (user rule): the notes used to run on alone from wherever the
                // game left them, with the tiles a restart hid still hidden.
                if (model.Playback.State != PlaybackState.Playing) model.Playback.Start();
                Equal(model.Playback.State, PlaybackState.Playing);
                model.Playback.HideNotesBeforeMs = 1234;
                var stopped = host.GameSync(false, true, false);
                Assert(stopped.Contains("link auto is off as well: the link is closed, so link manual is back.")
                    && stopped.Any(line => line.StartsWith("Auto     off", StringComparison.Ordinal)), string.Join(" | ", stopped));
                Equal(model.Playback.State, PlaybackState.Waiting);
                Assert(double.IsNegativeInfinity(model.Playback.HideNotesBeforeMs) && model.Playback.TimelineTimeMs <= 0
                    && stopped.Contains("The song is back as it was loaded: from its beginning, stopped."), string.Join(" | ", stopped));
                var reopened = host.GameSync(true, false, false);
                Assert(reopened.Any(line => line.StartsWith("Auto     off", StringComparison.Ordinal)), "Opening the pipe again does not switch it back on");
                Assert(Frames(() => host.LogLines().Any(line => line.EndsWith("link: the pipe is closed", StringComparison.Ordinal))),
                    "The log has the pipe closing");
            }
            finally
            {
                window.Close();
                MainWindow.GameSyncPipeName = NamedPipeGameSyncServer.PipeName;
            }
        });

        yield return ("link auto shows a refused cache on the main screen once, and clears it when matching succeeds", () =>
        {
            foreach (var language in new[] { "korean", "english" })
            {
                var oldClock = MainWindow.PlaybackClock;
                var oldPipe = MainWindow.GameSyncPipeName;
                var clock = new Clock { NowMs = 1000 };
                MainWindow.PlaybackClock = clock;
                MainWindow.GameSyncPipeName = "adofai-visualizer-sync-test-" + Guid.NewGuid().ToString("N");
                var window = EnglishWindow();
                try
                {
                    // No app store and no real game: only synthetic schedules and this window's private listener.
                    var model = (Visualizer.App.ViewModels.MainViewModel)window.DataContext;
                    var host = (ICmdHost)window;
                    var settings = new VisualizerSettings { Language = language, StartDelayMs = 0 };
                    model.ApplySettings(settings);
                    var stale = new Visualizer.Application.Cache.MapCacheEntry("cache", @"C:\maps", @"3011005213\main.adofai", "Synthetic",
                        Visualizer.Application.Maps.MapParseStatus.Supported, new ParsedTimeline([0, 500, 1200], hitFloors: [1, 2, 3]),
                        Array.Empty<string>(), "", "adofai-v24-pause-correction");
                    model.SetLibrary([stale]);
                    model.LoadChosen(stale, null);
                    host.GameSync(true, false, false);
                    var sync = (GameSyncLink)typeof(MainWindow).GetProperty("Sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    var frame = typeof(MainWindow).GetMethod("AutoFollowGame", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    void Frame() => frame.Invoke(window, null);
                    sync.SetLevel(new GameSyncLevel(@"C:\Steam\3011005213\main.adofai", [0, 1200, 1700, 2200]));
                    host.GameSyncAuto(true);
                    model.SetMessage("A previous message");
                    Frame();
                    Assert(!model.FollowingGame && !model.Playback.ClockFollowsGame, "Other tile times must still be refused");
                    Equal(model.Message, model.Text["GameAutoStaleCache"]);
                    Assert(model.Message.Contains("sync", StringComparison.Ordinal) && model.Message.Contains("link manual", StringComparison.Ordinal),
                        "The main screen explains how to update the cache or start by hand");
                    // The console speaks the app's language, so the fix is checked by what both languages name: sync and the old cache.
                    static bool PointsToSync(string line) => line.Contains("sync", StringComparison.Ordinal)
                        && line.Contains("adofai-v24-pause-correction", StringComparison.Ordinal);
                    Assert(host.GameSync(false, false, false).Any(PointsToSync), "The console's automatic refusal also points to the cache fix");
                    Assert(PointsToSync(host.GameSyncFollow(true)[0]), "A manual refusal explains the same fix");

                    // Retrying the same refusal must not overwrite newer messages or refresh its age every two seconds.
                    model.SetMessage("A newer message");
                    var messages = 0;
                    model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.Message)) messages++; };
                    clock.NowMs += 1000;
                    Frame();
                    clock.NowMs += 1100;
                    Frame();
                    clock.NowMs += 2100;
                    Frame();
                    Equal(messages, 0);
                    Equal(model.Message, "A newer message");
                    // Re-preparing is a new attempt, so a refusal must be shown again then.
                    model.Playback.Reset(settings);
                    Frame();
                    Equal(messages, 1);
                    Equal(model.Message, model.Text["GameAutoStaleCache"]);

                    // A corrected cache is loaded through the ordinary path. It matches and replaces the refusal.
                    var fresh = stale with { ParserVersion = Visualizer.Infrastructure.Parsing.AdofaiTimelineParser.CurrentVersion,
                        Timeline = new ParsedTimeline([0, 500, 1000], hitFloors: [1, 2, 3]) };
                    model.SetLibrary([fresh]);
                    model.LoadChosen(fresh, null);
                    Frame();
                    Assert(model.FollowingGame && model.Playback.ClockFollowsGame, "The corrected cache follows without toggling auto off");
                    Equal(model.Message, model.Text["GameAutoFollowing"]);
                    messages = 0;
                    Frame();
                    Equal(messages, 0);

                    // A mismatch from a current cache gets the general reason guide, without an older-cache claim.
                    host.GameSyncFollow(false);
                    var mismatch = stale with { ParserVersion = fresh.ParserVersion };
                    model.SetLibrary([mismatch]);
                    model.LoadChosen(mismatch, null);
                    Frame();
                    Equal(model.Message, model.Text["GameAutoRefused"]);
                    Assert(!model.FollowingGame, "A current cache with wrong times is still refused");
                    Assert(!host.GameSyncFollow(true)[0].Contains("older timing cache", StringComparison.Ordinal), "Its reason does not blame the cache version");
                    host.GameSyncAuto(false);
                    model.SetMessage("Manual mode");
                    clock.NowMs += 3000;
                    Frame();
                    Equal(model.Message, "Manual mode");
                }
                finally
                {
                    window.Close();
                    MainWindow.PlaybackClock = oldClock;
                    MainWindow.GameSyncPipeName = oldPipe;
                }
            }
        });

        yield return ("Following works with the start wait, the display offset and a restart's misses", () =>
        {
            // The settings that shift the notes must shift the mapping by exactly as much, or the game and the app drift.
            var settings = new VisualizerSettings { StartDelayMs = 2000, GlobalDisplayOffsetMs = 30 };
            var clock = new Clock();
            var session = new PlaybackSession(clock) { Presses = new NotePressJudge() };
            var timeline = new ParsedTimeline([0, 300, 600, 900], hitFloors: [1, 2, 4, 5]);
            session.LoadParsedTimeline(timeline, settings);
            session.Toggle();
            session.Reset(settings);
            var follower = new GameSyncFollower();
            double[] game = [1050, 1200, 1500, 1700, 1800, 2100];
            Equal(follower.Prepare(new GameSyncLevel("L", game), session), null);

            // The game is on its tile 4, which is our hit 2: that note must be exactly on the judgment line.
            clock.NowMs = 20_000;
            var onTile = new GameSyncMessage(GameSyncLink.Protocol, 0, 1800, "PlayerControl", false, 4, 1, "L");
            follower.Follow(onTile with { PositionMs = 1790 }, 19_990, 20_000, session);
            Assert(follower.Follow(onTile, 20_000, 20_000, session), "It follows");
            var note = session.Notes[2];
            Assert(Math.Abs(session.TimelineTimeMs - (note.HitTimeMs + session.Offsets.TotalMs)) < 0.001,
                $"The tile sits on the line: {session.TimelineTimeMs} against {note.HitTimeMs + session.Offsets.TotalMs}");
            Equal(session.Offsets.TotalMs, 30d);

            // Notes the playhead passed count as missed; after a restart the same notes must be able to miss again.
            var missed = new List<int>();
            session.Presses!.CollectMisses(session, settings.KeyPressWindowMs, missed);
            Equal(missed.Count, 2);
            var back = new GameSyncMessage(GameSyncLink.Protocol, 0, 1200, "Countdown", false, 1, 1, "L");
            Assert(follower.Follow(back, 20_100, 20_100, session), "The restart moves it back");
            clock.NowMs = 30_000;
            // Forward to our hit 3 (game position 2100 is source 900 once the offset is taken off).
            session.MoveClock(session.TimelineTimeOf(900));
            missed.Clear();
            session.Presses.CollectMisses(session, settings.KeyPressWindowMs, missed);
            Assert(missed.Count == 3, $"All three can miss again after the restart, not {missed.Count}");
        });

        yield return ("Every preparation is counted, so link auto can try the tiles again after a refusal", () =>
        {
            var settings = new VisualizerSettings { StartDelayMs = 0 };
            var session = new PlaybackSession(new Clock());
            // A long note, and the note after it never takes its lane, which is what a single lane cannot place.
            session.LoadParsedTimeline(new ParsedTimeline([0, 10, 300], hitFloors: [1, 2, 3],
                hitKinds: [HitKind.HoldStart, HitKind.HoldEnd, HitKind.Tap]), settings);
            session.Toggle();
            session.Reset(settings);
            var first = session.Prepared;
            Assert(first > 0, "Preparing is counted");
            // The speed is why link auto refuses most often, and putting it back prepares the very same notes: counting
            // notes instead of preparations would leave the automatic follow waiting for the game to reload the level.
            var slow = settings with { PlaybackSpeedPercent = 50 };
            session.ApplySettings(slow);
            Equal(session.Notes.Count, 2);
            Assert(session.Prepared > first, $"A speed change is a new preparation: {first} -> {session.Prepared}");
            var second = session.Prepared;
            session.ApplySettings(settings);
            Assert(session.Prepared > second, "And so is putting it back");
            // A preparation that cannot place its notes counts too, or a refusal there would never be retried.
            var failed = session.Prepared;
            Throws<NotePlacementException>(() => session.ApplySettings(settings with { MaximumTileCount = 1, DynamicTiming = false }));
            Assert(session.Prepared > failed, "A failed preparation is still a preparation");
        });

        yield return ("Tile check lines the game's own schedule up with ours and names the tile that is out", () =>
        {
            // Our hits are on tiles 1, 2, 4, 5 (tile 3 is a mid-spin, which the game has but we never press).
            var timeline = new ParsedTimeline([0, 300, 600, 900], hitFloors: [1, 2, 4, 5]);
            // The game's own times for tiles 0..5. It counts its zero 1200 ms before ours, and nothing else differs.
            double[] game = [1050, 1200, 1500, 1700, 1800, 2100];
            var report = GameTileCheck.Compare(game, timeline);
            Equal(report.Matched, 4);
            Equal(report.Unmatched, 0);
            Equal(report.MinOffsetMs, 1200d);
            Equal(report.MaxOffsetMs, 1200d);
            var lines = GameTileCheck.Lines(report);
            Assert(lines[^1].Contains("exactly as the game does", StringComparison.Ordinal), "Agreement is stated: " + lines[^1]);
            Assert(lines.Any(line => line.StartsWith("Offset", StringComparison.Ordinal)), "The offset is printed");

            // One tile read differently is what a missed rule looks like; it must be named, not averaged away.
            double[] bent = [1050, 1200, 1500, 1700, 1800, 2204];
            var wrong = GameTileCheck.Compare(bent, timeline);
            Equal(wrong.Worst!.Floor, 5);
            var told = GameTileCheck.Lines(wrong, "the game has something else.");
            Assert(told[0].StartsWith("Careful", StringComparison.Ordinal), "A different song is called out");
            Assert(told.Any(line => line.StartsWith("Worst", StringComparison.Ordinal) && line.Contains("tile 5", StringComparison.Ordinal)),
                "The worst tile is named");
            Assert(told[^1].Contains("do not all have the same offset", StringComparison.Ordinal), "And the verdict says so");

            // A game that has fewer tiles than we found hits for is counted, never guessed at.
            var short_ = GameTileCheck.Compare([1050, 1200, 1500], timeline);
            Equal(short_.Matched, 2);
            Equal(short_.Unmatched, 2);
            Assert(GameTileCheck.Lines(short_).Any(l => l.Contains("not tiles the game has", StringComparison.Ordinal)), "Said plainly");

            // A timeline from an older cache has no tile numbers, so nothing can be lined up.
            var noFloors = GameTileCheck.Compare(game, new ParsedTimeline([0, 300]));
            Equal(noFloors.Matched, 0);
            Assert(GameTileCheck.Lines(noFloors)[0].Contains("Is it the same song?", StringComparison.Ordinal), "And it says so");
        });

        yield return ("Sync link text says what is happening, and a reset clears it", () =>
        {
            Equal(GameSyncLink.Lines(null, false, false, null)[0], "Not listening. Use link start to open the pipe.");
            var waiting = GameSyncLink.Lines(null, true, false, null);
            Assert(waiting[0].Contains("no mod connected", StringComparison.Ordinal), "Waiting is named");
            Assert(waiting[^1].Contains("Nothing received yet", StringComparison.Ordinal), "And so is the empty window");
            Assert(GameSyncLink.Lines(null, true, false, "pipe is busy")[1].Contains("pipe is busy", StringComparison.Ordinal),
                "A problem is printed");

            var link = new GameSyncLink();
            for (var i = 0; i < 5; i++) link.Add(Reading(i * 10, i * 10, "Countdown"), i * 10);
            var lines = GameSyncLink.Lines(link.Report(), true, true, null, "0.7.3");
            Assert(lines[0].Contains("mod is connected", StringComparison.Ordinal), "Connected is named");
            Assert(lines.Any(l => l.Contains("never said its version", StringComparison.Ordinal)), "A silent mod is named as old");
            Assert(lines.Any(line => line.StartsWith("Game", StringComparison.Ordinal) && line.Contains("Countdown", StringComparison.Ordinal)),
                "The game state is printed");
            Assert(lines.Any(line => line.Contains(@"C:\maps\main.adofai", StringComparison.Ordinal)), "And the level");
            link.SetLevel(new GameSyncLevel("L", [0, 100]));
            link.Reset();
            Assert(link.Report() is null, "A reset empties the window");
            Assert(!link.HasReadings, "And its readings");
            // The tile times are the game's own schedule, not a measurement, so measuring again keeps them.
            Assert(link.Level is { Path: "L" }, "A reset keeps the level's tile times");
        });

        yield return ("link inspect presses each visualizer note on time, only into the game, and stops when it cannot see", () =>
        {
            // User rule: for measuring only, it presses the lane key of every note the visualizer draws at the moment the note
            // reaches the judgment line. Here a clock and a key sender of the test's own; nothing reaches a real keyboard.
            var clock = new Clock();
            var sent = new List<(int Key, bool Down, double At)>();
            var front = true;
            var keys = new Dictionary<int, int> { [3] = 0x44, [4] = 0x46, [5] = 0x4A, [6] = 0x4B };
            using var inspection = new Visualizer.App.Input.LinkInspection(clock, keys, (key, down) => sent.Add((key, down, clock.NowMs)), () => front);
            // A tap on tile 3, a chord on 4 and 5, a long note on 6. The timeline stands at 0 when the clock does.
            VisualizerNote[] notes = [new(1000, 3, 4), new(1200, 4, 4, StartsChord: true), new(1200, 5, 4), new(1400, 6, 4, HoldEndMs: 1700)];
            void At(double now) { clock.NowMs = now; inspection.Plan(notes, now, 0, now); Assert(inspection.Step(now), "Still running at " + now); }
            At(0);
            At(999);
            Equal(sent.Count, 0);
            At(1000);
            Assert(sent is [(0x44, true, 1000)], "Tile 3 goes down on its moment");
            At(1025);
            Assert(sent[^1] == (0x44, false, 1025), "A tap comes up shortly after");
            At(1200);
            Assert(sent.Skip(2).Take(2).Select(press => press.Key).Order().SequenceEqual([0x46, 0x4A]), "Both keys of the chord go down together");
            At(1400);
            At(1500);
            Assert(sent.Count(press => press.Key == 0x4B) == 1, "A long note stays down");
            At(1700);
            Assert(sent[^1] == (0x4B, false, 1700), "and comes up at its end");
            At(1800);
            Equal(sent.Count(press => press.Down), 4);
            Assert(inspection.Finished(notes.Length), "Every note was pressed once");
            Assert(inspection.Presses.All(press => press.LateMs == 0), "Each on its moment");

            // A run that starts late skips what already passed. Until the game first comes to the front it waits: a note due
            // then goes without a key and is counted (user report: the run ended at the first note while the console was in
            // front, so no key ever went). A key never goes while the game is not in front.
            sent.Clear();
            front = false;
            using var late = new Visualizer.App.Input.LinkInspection(clock, keys, (key, down) => sent.Add((key, down, clock.NowMs)), () => front,
                () => "in front: Console");
            clock.NowMs = 1100;
            late.Plan(notes, 1100, 0, 1100);
            clock.NowMs = 1200;
            late.Plan(notes, 1200, 0, 1200);
            Assert(late.Step(1200) && late.StopReason is null && late.WaitingForGame && late.Skipped == 2,
                $"Not in front yet: the chord goes unpressed and the run waits ({late.StopReason}, {late.Skipped})");
            Equal(sent.Count, 0);
            front = true;
            clock.NowMs = 1400;
            late.Plan(notes, 1400, 0, 1400);
            Assert(late.Step(1400) && !late.WaitingForGame && sent is [(0x4B, true, 1400)], "In front: the long note goes down");
            // Once it has been in front, leaving the front ends the run at once and says what came instead; its key comes up.
            front = false;
            clock.NowMs = 1500;
            late.Plan(notes, 1500, 0, 1500);
            Assert(!late.Step(1500) && late.StopReason == "the game window went out of front (in front: Console)", late.StopReason ?? "running");
            Assert(sent is [(0x4B, true, 1400), (0x4B, false, 1500)], "Only the one key went, and it came up");
            front = true;
            // Without a fresh plan the visualizer is not drawing, and it stops on its own.
            using var stale = new Visualizer.App.Input.LinkInspection(clock, keys, (key, down) => sent.Add((key, down, clock.NowMs)), () => front);
            stale.Plan(notes, 0, 0, 0);
            Assert(!stale.Step(Visualizer.App.Input.LinkInspection.StaleMs + 1) && stale.StopReason!.Contains("stopped drawing", StringComparison.Ordinal), "Stale plan stops");

            // What the game judged is kept with its tile: a Miss and an Overload in the run show in the report lines.
            var run = new Visualizer.App.Input.InspectRun(1, new DateTime(2026, 10, 13), "Song", @"C:\maps\main.adofai", "1.0.4");
            GameSyncMessage Reading(int floor, params int[] counts) =>
                new(GameSyncLink.Protocol, 0, floor * 100, "PlayerControl", false, floor, 1, "L", NoFail: true, Judgments: counts);
            run.Observe(Reading(1, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0, 0, 0), null);
            run.Observe(Reading(7, 0, 0, 0, 9, 0, 0, 0, 0, 1, 1, 0, 0), new Visualizer.App.Input.InspectPress(5, 4, 700, 700.5));
            run.Finish("the song ended", [], skipped: 2);
            var lines = run.Lines();
            Assert(lines.Any(line => line.StartsWith("waiting: 2 notes", StringComparison.Ordinal)), "Notes let go while waiting are told");
            Assert(lines.Any(line => line.Contains("FailMiss observed at game tile 7", StringComparison.Ordinal))
                && lines.Any(line => line.Contains("FailOverload observed at game tile 7", StringComparison.Ordinal))
                && lines.Any(line => line.Contains("Perfect +4", StringComparison.Ordinal)), string.Join(" / ", lines));
        });

        yield return ("inspect keeps the first tile when its first plan arrives at the judgment line", () =>
        {
            var clock = new Clock();
            var sent = new List<(int Key, bool Down)>();
            using var inspection = new Visualizer.App.Input.LinkInspection(clock,
                new Dictionary<int, int> { [1] = 0x44 }, (key, down) => sent.Add((key, down)), () => true);
            VisualizerNote[] notes = [new(0, 1, 1), new(500, 1, 1)];
            inspection.Plan(notes, 0, 0, 0, advancing: false);
            inspection.Step(0);
            Equal(sent.Count, 0);
            inspection.Plan(notes, 0, 0, 0);
            inspection.Step(0);
            Assert(sent is [(0x44, true)], "The tile on the judgment line is pressed, not skipped as already past");
        });

        yield return ("restarted inspect waits beyond the old song end and restores notes when the game rewinds", () =>
        {
            var clock = new Clock();
            var sent = new List<(int Key, bool Down)>();
            VisualizerNote[] notes = [new(0, 1, 1), new(500, 1, 1)];
            using (var previous = new Visualizer.App.Input.LinkInspection(clock,
                new Dictionary<int, int> { [1] = 0x44 }, (key, down) => sent.Add((key, down)), () => true))
            {
                previous.Plan(notes, -100, 0, 0);
                previous.Plan(notes, 0, 0, 100);
                previous.Step(100);
            }
            sent.Clear();
            using var restarted = new Visualizer.App.Input.LinkInspection(clock,
                new Dictionary<int, int> { [1] = 0x44 }, (key, down) => sent.Add((key, down)), () => true);
            restarted.Plan(notes, 600, 0, 700);
            restarted.Step(700);
            Assert(!restarted.Finished(notes.Length), "The old clock must not end a new inspect before the game restarts");
            restarted.Plan(notes, -100, 0, 800);
            restarted.Step(800);
            Equal(sent.Count, 0);
            restarted.Plan(notes, 0, 0, 900);
            restarted.Step(900);
            Assert(sent is [(0x44, true)], "The first note is restored after rewind, even if the old clock consumed it");
            Assert(restarted.Presses is [{ Note: 0, LateMs: 0 }], "The first tile is sent on time");
        });

        yield return ("inspect replay releases the previous hold and does not duplicate a note during small clock corrections", () =>
        {
            var clock = new Clock();
            var sent = new List<(int Key, bool Down)>();
            using var inspection = new Visualizer.App.Input.LinkInspection(clock,
                new Dictionary<int, int> { [1] = 0x44 }, (key, down) => sent.Add((key, down)), () => true);
            VisualizerNote[] notes = [new(0, 1, 1, HoldEndMs: 500), new(1000, 1, 1)];
            inspection.Plan(notes, -100, 0, 0);
            inspection.Plan(notes, 0, 0, 100);
            inspection.Step(100);
            inspection.Plan(notes, -1, 0, 101);
            inspection.Step(101);
            Equal(sent.Count, 1);
            inspection.Plan(notes, -200, 0, 200);
            inspection.Step(200);
            Assert(sent is [(0x44, true), (0x44, false)], "Rewinding releases the old hold immediately");
            inspection.Plan(notes, 0, 0, 400);
            inspection.Step(400);
            Assert(sent is [(0x44, true), (0x44, false), (0x44, true)], "A replay presses the same first tile again");
        });

        yield return ("inspect warms every key during countdown and cancels warm-up before notes or a pause", () =>
        {
            var clock = new Clock();
            var sent = new List<(int Key, bool Down)>();
            int[] keys = [0x41, 0x53, 0x44, 0x46, 0x4A, 0x4B, 0x4C, 0xBA, 0x20, 0x0D];
            using var inspection = new Visualizer.App.Input.LinkInspection(clock,
                new Dictionary<int, int> { [1] = 0x41 }, (key, down) => sent.Add((key, down)), () => true);
            VisualizerNote[] notes = [new(1000, 1, 1)];
            inspection.Plan(notes, 0, 0, 0);
            inspection.WarmUp([.. keys, 0x41], 0, 960);
            for (var now = 0; now <= 400; now += 5)
            {
                clock.NowMs = now;
                inspection.Plan(notes, now, 0, now);
                inspection.Step(now);
            }
            Assert(inspection.Warmed.SequenceEqual(keys), "Eight lane keys, Space and Enter each go once, in order");
            Equal(inspection.Presses.Count, 0);
            foreach (var key in keys)
                Assert(sent.Count(p => p == (key, true)) == 1 && sent.Count(p => p == (key, false)) == 1,
                    "Every warm-up key is released before tile 1");
            inspection.Plan(notes, 1000, 0, 1000);
            inspection.Step(1000);
            Assert(inspection.Presses is [{ Note: 0, LateMs: 0 }], "Warm-up does not consume the first note");
            Equal(inspection.Presses[0].PreviousKeyGapMs, 1000d);

            sent.Clear();
            using var shortCountdown = new Visualizer.App.Input.LinkInspection(clock,
                new Dictionary<int, int> { [1] = 0x41 }, (key, down) => sent.Add((key, down)), () => true);
            shortCountdown.Plan(notes, 0, 0, 0);
            shortCountdown.WarmUp(keys, 0, 100);
            shortCountdown.Step(0);
            shortCountdown.CancelWarmUp();
            shortCountdown.Step(40);
            Assert(sent is [(0x41, true), (0x41, false)], "Leaving countdown releases its key and cancels every queued tap");
            shortCountdown.WarmUp(keys, 40, 100);
            shortCountdown.Step(110);
            Equal(sent.Count, 2);
            Assert(shortCountdown.Warmed.Count == 1, "A late scheduler cannot send a warm-up key after the cutoff");

            using var checkpoint = new Visualizer.App.Input.LinkInspection(clock,
                new Dictionary<int, int> { [1] = 0x41 }, (_, _) => { }, () => true);
            VisualizerNote[] checkpointNotes = [new(0, 1, 1), new(500, 1, 1), new(1000, 1, 1)];
            checkpoint.Plan(checkpointNotes, 400, 0, 0, hideBeforeMs: 1000);
            Equal(checkpoint.NextNoteDueMs(), 600d);
            checkpoint.Plan(checkpointNotes, 420, 0, 20, hideBeforeMs: 500);
            Equal(checkpoint.NextNoteDueMs(), 100d);
            Assert(checkpointNotes[0].HitTimeMs == 0 && checkpointNotes[1].HitTimeMs == 500,
                "The checkpoint's hidden prefix only affects scheduling, never source hit times");
        });

        yield return ("inspection maps hold-end-filtered notes to their original floors without changing timing", () =>
        {
            var clock = new Clock();
            var settings = new VisualizerSettings { MaximumTileCount = 4, StartDelayMs = 0 };
            var timeline = new ParsedTimeline([0, 200, 500], hitFloors: [11, 12, 14],
                hitKinds: [HitKind.HoldStart, HitKind.HoldEnd, HitKind.Tap]);
            var playback = new PlaybackSession(clock);
            playback.LoadParsedTimeline(timeline, settings);
            playback.Toggle();
            playback.Reset(settings);
            Equal(playback.Notes.Count, 2);
            Equal(playback.SourceFloorOfNote(0), 11);
            Equal(playback.SourceFloorOfNote(1), 14);
            Equal(playback.SourceFloorOfNote(-1), null);
            Equal(playback.SourceFloorOfNote(2), null);
            using var inspection = new Visualizer.App.Input.LinkInspection(clock,
                new Dictionary<int, int> { [settings.InitialTile] = 0x41 }, (_, _) => { }, () => true,
                sourceFloors: [playback.SourceFloorOfNote(0), playback.SourceFloorOfNote(1)]);
            inspection.Plan(playback.Notes, -100, 0, 0);
            inspection.Plan(playback.Notes, 0, 0, 100);
            inspection.Step(100);
            Assert(inspection.Presses is [{ SourceFloor: 11, Note: 0, LateMs: 0 }], "The first press records its original floor");
            Equal(timeline.HitTimesMs[1], 200d);
        });

        yield return ("inspect results keep measurement settings, observed sample gaps and positive counts across a restart", () =>
        {
            var settings = new VisualizerSettings { MaximumTileCount = 4 };
            var run = new Visualizer.App.Input.InspectRun(1, new DateTime(2026, 10, 13), "Song", "L", "1.0.7", settings);
            GameSyncMessage Sample(double stamp, int perfect, int miss) => new(GameSyncLink.Protocol, stamp, stamp,
                "PlayerControl", false, 20, 1, "L", NoFail: true, Judgments: [0, 0, 0, perfect, 0, 0, 0, 0, miss, 0, 0, 0]);
            run.Observe(Sample(0, 10, 2), null);
            run.Observe(Sample(16, 11, 2), null);
            run.Observe(Sample(32, 0, 0), null);
            var press = new Visualizer.App.Input.InspectPress(0, 3, 60, 60, SourceFloor: 14, PreviousKeyGapMs: 40);
            run.Observe(Sample(64, 3, 1), press);
            // A zero reading can be omitted; the first observed reading of a new attempt already has judgments.
            run.Observe(Sample(80, 1, 1), press);
            run.Observe(Sample(96, 2, 2), press);
            run.Finish("stopped", [press]);
            var lines = run.Lines();
            Assert(lines.Any(line => line.Contains("Perfect +6, FailMiss +3", StringComparison.Ordinal)), string.Join(" | ", lines));
            Assert(lines.Any(line => line.StartsWith("settings: max 4K", StringComparison.Ordinal)), "The run retains its original settings");
            Assert(lines.Any(line => line.Contains("largest 32.0 ms; 1 above", StringComparison.Ordinal)), "Sample gaps are reported separately from app frames");
            Assert(lines.Any(line => line.Contains("observed at game tile 20", StringComparison.Ordinal)
                && line.Contains("targeted map tile 14", StringComparison.Ordinal)), "Observed game floor and targeted source floor stay distinct");
            Assert(lines.Any(line => line.Contains("same key after 40.0 ms", StringComparison.Ordinal)), "The last input's actual repeat gap helps identify chatter filtering");
        });

        yield return ("inspect countdown warm-up waits for the game foreground and keeps each tap separate", () =>
        {
            var clock = new Clock();
            var sent = new List<(int Key, bool Down, double At)>();
            var front = false;
            using var inspection = new Visualizer.App.Input.LinkInspection(clock,
                new Dictionary<int, int> { [1] = 0x41 }, (key, down) => sent.Add((key, down, clock.NowMs)), () => front);
            VisualizerNote[] notes = [new(1200, 1, 1)];
            int[] warm = [0x41, 0x53, 0x44, 0x46, 0x4A, 0x4B, 0x4C, 0xBA, 0x20, 0x0D];
            inspection.Plan(notes, 0, 0, 0);
            inspection.WarmUp(warm, 0, 1200 - Visualizer.App.Input.LinkInspection.WarmQuietMs);
            for (var now = 0; now <= 480; now += 40)
            {
                clock.NowMs = now;
                inspection.Plan(notes, now, 0, now);
                inspection.Step(now);
            }
            Equal(sent.Count, 0);
            front = true;
            clock.NowMs = 500;
            inspection.Plan(notes, 500, 0, 500);
            inspection.Step(500);
            Assert(sent is [(0x41, true, 500)], "Warm-up must survive the console foreground, and start with one key");
            for (var now = 505; now <= 900; now += 5)
            {
                clock.NowMs = now;
                inspection.Plan(notes, now, 0, now);
                inspection.Step(now);
            }
            var downs = sent.Where(p => p.Down).ToArray();
            Assert(downs.Select(p => p.Key).SequenceEqual(warm), "Every queued key goes once after the game comes forward");
            Assert(downs.Zip(downs.Skip(1)).All(p => p.Second.At - p.First.At >= Visualizer.App.Input.LinkInspection.WarmGapMs),
                "Delayed warm-up taps never burst together");
            Assert(sent.Count(p => !p.Down) == warm.Length, "Every warm-up key is released before the first note");
        });

        yield return ("link inspect starts only while all its rules hold, in the order the user set them", () =>
        {
            var name = "adofai-visualizer-sync-test-" + Guid.NewGuid().ToString("N");
            MainWindow.GameSyncPipeName = name;
            var pressed = new List<int>();
            MainWindow.KeySender = (key, down) => pressed.Add(key);
            var window = EnglishWindow();
            try
            {
                var host = (ICmdHost)window;
                string Try() => host.GameSyncInspect(true)[0];
                Assert(Try().Contains("the link is not on", StringComparison.Ordinal), Try());
                host.GameSync(true, false, false);
                Assert(Try().Contains("not connected", StringComparison.Ordinal), Try());
                using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
                client.Connect(5000);
                void Send(string line)
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    client.Write(bytes, 0, bytes.Length);
                    client.Flush();
                }
                var sync = (GameSyncLink)typeof(MainWindow).GetProperty("Sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Send("""{"v":2,"t":1,"pos":-500,"st":"Countdown","paused":false,"floor":0,"speed":1,"path":"C:\\maps\\a\\main.adofai","nf":false}""");
                Wait(() => sync.Latest.Reading is not null, "a reading");
                host.GameSyncAuto(true);
                Assert(Try().Contains("link auto is on", StringComparison.Ordinal), Try());
                host.GameSyncAuto(false);
                Assert(Try().Contains("no level yet", StringComparison.Ordinal), Try());
                Send("""{"v":2,"lvl":"C:\\maps\\a\\main.adofai","times":[0,1000,1500]}""");
                Wait(() => sync.Level is not null, "the level");
                Assert(Try().Contains("not the one open in the game", StringComparison.Ordinal), Try());
                var model = (Visualizer.App.ViewModels.MainViewModel)window.DataContext;
                var song = new Visualizer.Application.Cache.MapCacheEntry("a", @"C:\maps", @"a\main.adofai", "A",
                    Visualizer.Application.Maps.MapParseStatus.Supported, new ParsedTimeline([0, 500], hitFloors: [1, 2]), Array.Empty<string>(), "", "test");
                model.SetLibrary([song]);
                model.LoadChosen(song, null);
                Assert(Try().Contains("No Fail is off", StringComparison.Ordinal), Try());
                Send("""{"v":2,"t":2,"pos":-480,"st":"Countdown","paused":false,"floor":0,"speed":1,"path":"C:\\maps\\a\\main.adofai"}""");
                Wait(() => sync.Latest.Reading?.PositionMs == -480, "a reading without No Fail");
                Assert(Try().Contains("older one", StringComparison.Ordinal), Try());
                Send("""{"v":2,"t":3,"pos":-460,"st":"Countdown","paused":false,"floor":0,"speed":1,"path":"C:\\maps\\a\\main.adofai","nf":true,"hm":[0,0,0,0,0,0,0,0,0,0,0,0]}""");
                Wait(() => sync.Latest.Reading?.NoFail == true, "No Fail");
                Assert(sync.Latest.Reading!.Judgments is { Count: 12 }, "The judgment counts come through");
                // Every rule holds: it starts (following first), and nothing is pressed before the game has been in front.
                var ended = 0;
                var flowStart = CmdLog.Shared.Lines.Count;
                var started = host.GameSyncInspect(true, flow: true, ended: () => ended++);
                Assert(started[0].StartsWith("Inspecting", StringComparison.Ordinal), string.Join(" / ", started));
                Assert(host.GameSync(false, false, false).Any(line => line.StartsWith("Inspect  running", StringComparison.Ordinal)), "link shows it");
                // The top of the lanes says a run is on (user rule) while it is, and nothing once it stops. It sits over the
                // song's band in the middle, which used to cover it (user report).
                var banner = (System.Windows.FrameworkElement)window.FindName("InspectBanner");
                Assert(banner.Visibility == System.Windows.Visibility.Visible
                    && ((System.Windows.Controls.TextBlock)window.FindName("InspectBannerText")).Text.StartsWith("inspect is", StringComparison.Ordinal), "The banner shows");
                var lanes = (System.Windows.Controls.Grid)((System.Windows.FrameworkElement)window.FindName("Field")).Parent;
                var top = (System.Windows.FrameworkElement)banner.Parent;
                var band = (System.Windows.FrameworkElement)window.FindName("OverlayArea");
                Assert(top.VerticalAlignment == System.Windows.VerticalAlignment.Top
                    && lanes.Children.IndexOf((System.Windows.UIElement)top) > lanes.Children.IndexOf(band)
                    && lanes.Children.IndexOf((System.Windows.UIElement)top) == lanes.Children.Count - 1,
                    "At the top of the lanes and over the song's band");
                // The last reading arrives between the last frame and stop: -flow must still print its judgment.
                Send("""{"v":2,"t":4,"pos":-440,"st":"Countdown","paused":false,"floor":0,"speed":1,"path":"C:\\maps\\a\\main.adofai","nf":true,"hm":[0,0,0,0,0,0,0,0,1,0,0,0]}""");
                Wait(() => sync.Latest.Reading?.Judgments?[8] == 1, "a last-frame miss");
                Assert(host.GameSyncInspect(false)[0].StartsWith("Stopped inspecting", StringComparison.Ordinal), "inspect stop stops it");
                Equal(ended, 1);
                Assert(!host.Inspecting && CmdLog.Shared.Lines.Skip(flowStart).Contains("game: FailMiss observed · current tile 0"),
                    "The final judgment is printed before the flow ends");
                Assert(banner.Visibility == System.Windows.Visibility.Collapsed, "and the banner goes");
                Equal(pressed.Count, 0);
                Assert(host.InspectLines(1) is { Count: > 0 } kept && kept[0].StartsWith("link inspect #1", StringComparison.Ordinal), "The run is kept");
                model.Playback.Start();
                Assert(model.Playback.Pause() && model.FollowingGame, "The previous follow can remain locally paused after Esc");
                Assert(host.GameSyncInspect(true)[0].StartsWith("Inspecting", StringComparison.Ordinal), "Inspection starts again");
                Assert(model.Playback.State == PlaybackState.Playing,
                    "Restarting inspection hands a locally paused clock back to the game instead of leaving it held");
                host.GameSyncInspect(false);
            }
            finally
            {
                window.Close();
                MainWindow.KeySender = null;
                MainWindow.GameSyncPipeName = NamedPipeGameSyncServer.PipeName;
            }
        });

        yield return ("The window opens and closes the sync pipe on its own", () =>
        {
            // Its own pipe name: the real one may be held by an app the user is running, and tests never fight it.
            var name = "adofai-visualizer-sync-test-" + Guid.NewGuid().ToString("N");
            MainWindow.GameSyncPipeName = name;
            // The real host, not a fake one: the pipe must stay shut until asked, and must not outlive the window.
            var window = EnglishWindow();
            var host = (ICmdHost)window;
            Assert(host.GameSync(false, false, false)[0].Contains("Not listening", StringComparison.Ordinal), "Shut to begin with");
            Assert(host.GameSync(true, false, false)[0].Contains("Listening", StringComparison.Ordinal), "Opened by -on");
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.Out))
                client.Connect(5000);
            Assert(host.GameSync(false, false, true)[^1] == "Measuring again from now.", "A reset says so");
            // restart linker (user request): the whole link from nothing, listening again at once, link auto as it was.
            host.GameSyncAuto(true);
            var restarted = host.RestartLinker();
            Assert(restarted[0].StartsWith("The link started again", StringComparison.Ordinal)
                && restarted.Any(line => line.StartsWith("Listening", StringComparison.Ordinal))
                && restarted.Any(line => line.StartsWith("Auto     on", StringComparison.Ordinal)), string.Join(" | ", restarted));
            using (var again = new NamedPipeClientStream(".", name, PipeDirection.Out))
                again.Connect(5000);
            Assert(host.LogLines().Any(line => line.EndsWith("link: restarted from nothing", StringComparison.Ordinal)), "The log has it");
            host.GameSyncAuto(false);
            Assert(host.GameSync(false, true, false)[0].Contains("Not listening", StringComparison.Ordinal), "Closed by -off");
            window.Close();
            // Closing while it listens must not leave the pipe or its thread behind.
            var second = EnglishWindow();
            ((ICmdHost)second).GameSync(true, false, false);
            second.Close();
            using var after = new NamedPipeClientStream(".", name, PipeDirection.Out);
            Throws<TimeoutException>(() => after.Connect(500));
            MainWindow.GameSyncPipeName = NamedPipeGameSyncServer.PipeName;
        });

        yield return ("The pipe takes in a second game and follows the one that is playing", () =>
        {
            // User report: ADOFAI sometimes runs twice, and a game in trouble may keep the pipe while it sends nothing. The pipe
            // used to take one game at a time, so the game being played could not even connect then.
            var link = new GameSyncLink();
            var name = "adofai-visualizer-sync-test-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeGameSyncServer(link, name, foreground: () => 0);
            server.Start();
            static void Send(NamedPipeClientStream pipe, string line)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
            }
            static string Reading(int clock, int position, string path) =>
                $$"""{"v":2,"t":{{clock}},"pos":{{position}},"st":"PlayerControl","paused":false,"floor":1,"speed":1,"path":"{{path}}"}""";
            var first = new NamedPipeClientStream(".", name, PipeDirection.Out);
            first.Connect(5000);
            Send(first, """{"v":2,"mod":"1.0.9"}""");
            Send(first, """{"v":2,"lvl":"A","times":[0,500]}""");
            Send(first, Reading(1, 100, "A"));
            Wait(() => link.Latest.Reading?.Path == "A", "the first game to be followed");
            using var second = new NamedPipeClientStream(".", name, PipeDirection.Out);
            second.Connect(5000);
            Send(second, """{"v":2,"mod":"1.0.9"}""");
            Send(second, """{"v":2,"lvl":"B","times":[0,700]}""");
            Send(second, Reading(1, 200, "B"));
            Wait(() => server.Games == 2, "both games to be in");
            // While the game followed still sends, it stays the one followed: a second game does not pull the notes away.
            Send(first, Reading(2, 110, "A"));
            Wait(() => link.Latest.Reading?.PositionMs == 110, "the first game's next reading");
            Equal(link.Level?.Path, "A");
            // The first goes quiet (a game in trouble) while the second plays on: the second is followed, from its own level.
            Thread.Sleep((int)NamedPipeGameSyncServer.QuietMs + 150);
            Send(second, Reading(2, 210, "B"));
            Wait(() => link.Latest.Reading?.Path == "B", "the game still playing to be followed");
            Equal(link.Level?.Path, "B");
            Assert(server.ClientProcessId != 0, "The followed game is named");
            // The quiet one leaving takes nothing with it.
            first.Dispose();
            Wait(() => server.Games == 1, "the quiet game to be gone");
            Send(second, Reading(3, 220, "B"));
            Wait(() => link.Latest.Reading?.PositionMs == 220, "the second game to carry on");
            server.Stop();
            Equal(server.Games, 0);
        });

        yield return ("Sync pipe takes what a mod writes and refuses nonsense", () =>
        {
            var link = new GameSyncLink();
            var name = "adofai-visualizer-sync-test-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeGameSyncServer(link, name);
            Assert(!server.Listening, "Closed until asked");
            server.Start();
            Assert(server.Listening, "Then listening");

            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
            client.Connect(5000);
            void Send(string line)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
            }
            // Exactly what tools/GameSyncMod writes, including the escaped path.
            Send("""{"v":2,"t":12.5,"pos":8317.25,"st":"PlayerControl","paused":false,"floor":128,"speed":1,"path":"C:\\maps\\main.adofai"}""");
            Send("""{"v":2,"t":20,"pos":8325,"st":"PlayerControl","paused":true,"floor":128,"speed":0.75,"pitch":0.9,"ci":100,"cv":-20,"path":"C:\\maps\\main.adofai"}""");
            // Who the mod is, exactly as tools/GameSyncMod writes it on every connection.
            Send("""{"v":2,"mod":"0.7.3"}""");
            // The level's own tile times, exactly as tools/GameSyncMod writes them when a level loads.
            Send("""{"v":2,"lvl":"C:\\maps\\main.adofai","times":[0,1050,1200,1500.5]}""");
            Send("not json at all");
            Send("""{"v":2,"t":30}""");
            Wait(() => link.Report() is { Accepted: 2, Rejected: 2 }, "the pipe to hand over four lines");

            var report = link.Report()!;
            Equal(report.Last!.PositionMs, 8325);
            Equal(report.Last.Floor, 128);
            Equal(report.Last.Speed, 0.75);
            Assert(report.Last.Paused, "The paused flag comes through");
            Equal(report.Last.PitchRate, 0.9);
            Equal(report.Last.InputOffsetMs, 100d);
            Equal(report.Last.VisualOffsetMs, -20d);
            Equal(report.Last.Path, @"C:\maps\main.adofai");
            Assert(report.FirstRejection!.Contains("not a reading", StringComparison.Ordinal), "Nonsense is counted, not parsed");
            Assert(link.Level is { } level && level.Path == @"C:\maps\main.adofai", "The level's tile times come through");
            Sequence(link.Level!.TimesMs.Select(t => (int)t), 0, 1050, 1200, 1500);
            Equal(report.LevelTiles, 4);
            Equal(link.ModVersion, "0.7.3");
            // A mod left over from an older build is named, not left looking like one that sends nothing.
            Assert(GameSyncLink.Lines(report, true, true, null, "0.7.3").Any(l => l == "Mod      0.7.3"), "The mod says who it is");
            Assert(GameSyncLink.Lines(report, true, true, null, "0.8.0").Any(l => l.Contains("this app is 0.8.0", StringComparison.Ordinal)),
                "A mod from another build is called out");

            // Only one listener can hold a name. A second one must say so and wait, not spin on it.
            using (var second = new NamedPipeGameSyncServer(new GameSyncLink(), name))
            {
                second.Start();
                Wait(() => second.Problem is not null, "the second listener to notice the name is taken");
                Assert(second.Problem!.Contains("Another visualizer", StringComparison.Ordinal), "It says who has it: " + second.Problem);
            }
            // A restarted game is a new mod whose clock starts from zero again: it is taken, not refused as going backwards.
            // And a line the link cannot take costs that line, not the connection.
            client.Dispose();
            using (var restarted = new NamedPipeClientStream(".", name, PipeDirection.Out))
            {
                restarted.Connect(5000);
                foreach (var line in new[]
                {
                    """{"v":2,"mod":"   "}""",
                    """{"v":2,"t":1,"pos":100,"st":"Countdown","paused":false,"floor":0,"speed":1,"path":"C:\\maps\\main.adofai"}"""
                })
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    restarted.Write(bytes, 0, bytes.Length);
                }
                restarted.Flush();
                Wait(() => link.Report() is { Accepted: 3 }, "the restarted mod's reading to be taken");
            }
            Equal(link.Report()!.Last!.PositionMs, 100d);

            server.Stop();
            Assert(!server.Listening, "And it closes again");
        });
    }

    /// <summary>Waits for something another thread does, so the pipe test does not depend on timing.</summary>
    private static void Wait(Func<bool> until, string what)
    {
        for (var waited = 0; waited < 5000; waited += 20)
        {
            if (until()) return;
            Thread.Sleep(20);
        }
        throw new InvalidOperationException($"Waited 5 s for {what}.");
    }
}
