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
            Assert(link.Add(Reading(0, 0, speed: 0), 2) is not null, "A speed of zero");
            Assert(link.Add(Reading(0, 0, speed: 99), 3) is not null, "A speed far out of range");
            Assert(link.Add(Reading(0, 0, floor: -2), 4) is not null, "A tile below -1");
            Assert(link.Add(Reading(0, 0, state: " "), 5) is not null, "An empty state");
            Assert(link.Add(Reading(0, 0, path: ""), 6) is not null, "An empty level path");
            Assert(link.Add(Reading(-1, 0), 7) is not null, "A mod clock before zero");
            var report = link.Report()!;
            Equal(report.Accepted, 0);
            Equal(report.Rejected, 8);
            Assert(report.FirstRejection!.Contains("message shape", StringComparison.Ordinal), "The first reason is kept");

            // A mod clock that goes backwards is a different run, not a later reading.
            var second = new GameSyncLink();
            Equal(second.Add(Reading(100, 0), 0), null);
            Assert(second.Add(Reading(90, 0), 1)!.Contains("backwards", StringComparison.Ordinal), "Going backwards is refused");
            Equal(second.Report()!.Accepted, 1);
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

            // A paused game and a stale reading both leave the playhead alone.
            var paused = new GameSyncMessage(GameSyncLink.Protocol, 0, 2180, "PlayerControl", true, 5, 1, "L");
            var held = session.TimelineTimeMs;
            Assert(!follower.Follow(paused, 12_000, 12_000, session), "A paused game does not move it");
            Assert(!follower.Follow(drifted, 12_000, 12_000 + GameSyncFollower.StaleMs + 1, session), "Nor a stale reading");
            Assert(!follower.Follow(null, 12_000, 12_000, session), "Nor nothing at all");
            Equal(session.TimelineTimeMs, held);
            Assert(follower.Lines()[0].Contains("waiting for the game", StringComparison.Ordinal), "And it says so");

            // Only the clock moved: the notes and their times are exactly as prepared.
            Sequence(session.Notes.Select(note => (int)note.HitTimeMs), 0, 300, 600, 900);
            follower.Stop();
            Assert(!follower.Follow(drifted, 12_000, 12_000, session), "Stopped means stopped");
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
            // The first two hits are a chord, which is what a single lane cannot place.
            session.LoadParsedTimeline(new ParsedTimeline([0, 10, 300], hitFloors: [1, 2, 3]), settings);
            session.Toggle();
            session.Reset(settings);
            var first = session.Prepared;
            Assert(first > 0, "Preparing is counted");
            // The speed is why link auto refuses most often, and putting it back prepares the very same notes: counting
            // notes instead of preparations would leave the automatic follow waiting for the game to reload the level.
            var slow = settings with { PlaybackSpeedPercent = 50 };
            session.ApplySettings(slow);
            Equal(session.Notes.Count, 3);
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

        yield return ("The window opens and closes the sync pipe on its own", () =>
        {
            // Its own pipe name: the real one may be held by an app the user is running, and tests never fight it.
            var name = "adofai-visualizer-sync-test-" + Guid.NewGuid().ToString("N");
            MainWindow.GameSyncPipeName = name;
            // The real host, not a fake one: the pipe must stay shut until asked, and must not outlive the window.
            var window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false };
            var host = (ICmdHost)window;
            Assert(host.GameSync(false, false, false)[0].Contains("Not listening", StringComparison.Ordinal), "Shut to begin with");
            Assert(host.GameSync(true, false, false)[0].Contains("Listening", StringComparison.Ordinal), "Opened by -on");
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.Out))
                client.Connect(5000);
            Assert(host.GameSync(false, false, true)[^1] == "Measuring again from now.", "A reset says so");
            Assert(host.GameSync(false, true, false)[0].Contains("Not listening", StringComparison.Ordinal), "Closed by -off");
            window.Close();
            // Closing while it listens must not leave the pipe or its thread behind.
            var second = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false };
            ((ICmdHost)second).GameSync(true, false, false);
            second.Close();
            using var after = new NamedPipeClientStream(".", name, PipeDirection.Out);
            Throws<TimeoutException>(() => after.Connect(500));
            MainWindow.GameSyncPipeName = NamedPipeGameSyncServer.PipeName;
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
