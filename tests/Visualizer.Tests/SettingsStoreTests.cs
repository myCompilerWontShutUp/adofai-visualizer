using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Visualizer.App.Views;
using Visualizer.Application.Maps;
using Visualizer.Core.Localization;
using Visualizer.Core.Settings;
using Visualizer.Infrastructure.Cache;
using Visualizer.Infrastructure.Storage;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

/// <summary>The app-owned settings store: default location, round trip, custom root and its rejections.</summary>
internal static class SettingsStoreTests
{
    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    /// <summary>
    /// Settings that cannot hold a value of their own here: eight lanes take the one eight-tile pattern there is, and the
    /// old magic circle switch follows the judgment bar's choice in the settings window.
    /// </summary>
    private static readonly string[] Unchangeable = [nameof(VisualizerSettings.EightKeyPattern), nameof(VisualizerSettings.MagicCircleJudgmentLine)];

    /// <summary>One snapshot with a non-default, valid value for every setting but <see cref="Unchangeable"/>.</summary>
    private static VisualizerSettings EverySettingChanged()
    {
        var settings = new VisualizerSettings
        {
            Language = "english", Theme = "dark",
            MapRootDirectory = @"D:\Maps", DataRootDirectory = @"D:\Store",
            SongGroup = "All", SongSortKey = "Title", SongSortHighFirst = false, SongListFields = "Notes Path",
            ShowGuideButtons = false, ShowSongInfo = false, AutoSaveSettings = false,
            ResetKey = "Back", StartTriggerKey = "N", VisualizerToggleKey = "F9",
            PreviousCheckpointKey = "[", NextCheckpointKey = "]", SettingsHotkey = "F10",
            LaneKeys = "Q W E R U I O P",
            OnlyReactWhenAdofaiForeground = false, AdofaiProcessName = "Test Game",
            MaximumTileCount = 8, MinimumTileCount = 2, DisplayLaneCount = 8, DynamicTiming = false,
            EightKeyBoundaryMs = 15, SixKeyBoundaryMs = 25, FourKeyBoundaryMs = 45, TwoKeyBoundaryMs = 450,
            NextIntervalTiles = true, BothIntervalTiles = true,
            SimultaneousWindowMs = 30, ImmediateDownshift = false, DownshiftNoteCount = 5,
            TrillWindowMs = 30, TrillCenterPair = "Never", TrillLanePreference = "3-4", TrillAsOneNote = false,
            ChordsOnlyAboveFourKeys = true,
            StartDelayMs = 200, PlaybackSpeedPercent = 80, NoteTravelTimeMs = 700, CalibrationNoteCount = 3,
            AlignNextNoteOnPause = false,
            NoteThickness = 1.5, NoteWidth = 0.8, HoldNoteWidth = 0.5, GlobalDisplayOffsetMs = 12,
            SixKeyPattern = "1 2 3 4 5 6", FourKeyPattern = "2 3 4 5", TwoKeyPattern = "3 6",
            ExtraLanes = true, SlowNotesPastJudgmentLine = true,
            ExperimentalFeatures = true, ChordSectionLanes = true, ChordLanePreference = "3-4", NoChordCenterPair = false, KeepSpeedOnLoad = true,
            ChordAvoidBeforeLanes = true, RestartPatternOnRun = true, EnhancedChordRules = true, OneHandFourChords = true,
            EnableTilePreference = true, TwoKeyOrder = "1 2", FourKeyOrder = "1 2 3 4",
            SixKeyOrder = "1 2 3 4 5 6", EightKeyOrder = "1 2 3 4 5 6 7 8", InitialTile = 3,
            AutoCheckpointAdvance = false, AutoCheckpointOnLoad = false, JudgmentLineHeightDip = 100, KeyPressWindowMs = 120,
            ShowInputEffect = false, ShowMissEffect = false,
            JudgmentBarMode = "Always", JudgmentBarDifficulty = "Strict", JudgmentAverageCount = 5, ShowJudgmentAverage = false,
            MagicCircleJudgmentLine = true, MagicCircleNoteCount = 30, MagicCircleMaxIntervalMs = 150,
            MagicCircleFadeInMs = 300, MagicCircleLeadMs = 400, MagicCircleFadeOutMs = 200,
            MagicCircleFixedTiles = false, MagicCircleTileCount = 2, MagicCircleChords = false,
            LaneEffectColor = "10, 20, 30", NoteColor = "11, 21, 31", ChordColor = "12, 22, 32",
            CheckpointColor = "13, 23, 33", JudgmentLineColor = "14, 24, 34", MissEffectColor = "15, 25, 35",
            StartNoteColor = "16, 26, 36", EndNoteColor = "17, 27, 37", TrillColor = "18, 28, 38",
            PressedNoteTransparencyPercent = 50,
            LaneBackgroundColors = "#111111 #222222 #333333 #444444 #555555 #666666 #777777 #888888"
        };
        settings.EnsureValid();
        return settings;
    }

    /// <summary>Every setting that differs between two snapshots, as "name: actual instead of expected".</summary>
    private static string Differences(VisualizerSettings actual, VisualizerSettings expected) => string.Join(", ", typeof(VisualizerSettings)
        .GetProperties().Where(property => property.GetIndexParameters().Length == 0)
        .Where(property => !Equals(property.GetValue(actual), property.GetValue(expected)))
        .Select(property => $"{property.Name}: {property.GetValue(actual)} instead of {property.GetValue(expected)}"));

    public static IEnumerable<(string Name, Action Run)> Create(string artifactRoot) => new (string, Action)[]
    {
        ("scannow's own checks read a store and say what is wrong in it, changing nothing", () =>
        {
            var home = Path.Combine(artifactRoot, "scan-store");
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
            // A map copy no song list knows, a whole edit and a broken one, and a song list file that cannot be read.
            var custom = Path.Combine(home, "maps", "custom", "111");
            Directory.CreateDirectory(custom);
            File.WriteAllText(Path.Combine(custom, "main.adofai"), "{ }");
            var whole = Path.Combine(home, "maps", "edited", "111", "1");
            Directory.CreateDirectory(whole);
            File.WriteAllText(Path.Combine(whole, "main.adofai"), "{ }");
            File.WriteAllText(Path.Combine(whole, "edit.json"), $$"""{ "SchemaVersion": 1, "SourceHash": "{{MapSyncService.HashOf("{ }")}}", "Lanes": { "0": 5 } }""");
            var broken = Path.Combine(home, "maps", "edited", "111", "2");
            Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, "main.adofai"), "{ }");
            File.WriteAllText(Path.Combine(broken, "edit.json"), """{ "SchemaVersion": 1, "SourceHash": "nope", "Lanes": { "0": 9 } }""");
            File.WriteAllText(Path.Combine(home, "songs.json"), "not json");
            var before = Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories).ToDictionary(file => file, File.ReadAllText);

            MainWindow.StoreForTests = AppStorage.At(home);
            try
            {
                var window = English(new MainWindow());
                var model = (Visualizer.App.ViewModels.MainViewModel)window.DataContext;
                model.ApplySettings(model.Settings with { MapRootDirectory = Path.Combine(home, "no-map-folder") });
                IReadOnlyList<Visualizer.App.Commands.ScanArea>? found = null;
                ((Visualizer.App.Commands.ICmdHost)window).ScanNow(["app", "maps", "edits", "data"], new Progress<double>(), results => found = results);
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (found is null && waited.Elapsed < TimeSpan.FromSeconds(20)) Pump();
                Assert(found is not null, "The scan finished");
                var area = found!.ToDictionary(each => each.Name);
                Assert(area["app"].Problems.Count == 0, "This build, its languages and themes: " + string.Join(" | ", area["app"].Problems));
                var maps = area["maps"].Problems;
                Assert(maps.Any(line => line.StartsWith("custom/111", StringComparison.Ordinal) && line.EndsWith("is not in the song list", StringComparison.Ordinal))
                    && maps.Any(line => line.Contains("no-map-folder is not there", StringComparison.Ordinal)), "Maps: " + string.Join(" | ", maps));
                var edits = area["edits"];
                Assert(edits.Checked == "2 edits" && edits.Problems.Count == 2
                    && edits.Problems.Contains("111/2: a lane is outside 1 to 8") && edits.Problems.Contains("111/2: its copy is not the map the lanes were made on"),
                    "Edits: " + string.Join(" | ", edits.Problems));
                Assert(area["data"].Problems.Count == 1 && area["data"].Problems[0].StartsWith("songs.json cannot be read", StringComparison.Ordinal),
                    "Data: " + string.Join(" | ", area["data"].Problems));
                window.Close();
            }
            finally { MainWindow.StoreForTests = null; }
            // A scan only reads (user rule): every file is as it was and none was added.
            var after = Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories).ToDictionary(file => file, File.ReadAllText);
            Assert(after.Count == before.Count && after.All(pair => before.TryGetValue(pair.Key, out var text) && text == pair.Value), "Nothing changed");
        }),

        ("A copy's own store moves to the shared one once, with every song id rewritten for its new place", () =>
        {
            var from = Path.Combine(artifactRoot, "move-from");
            var to = Path.Combine(artifactRoot, "move-to");
            foreach (var folder in new[] { from, to, to + ".moving" }) if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            // A store shaped like the real one: two songs in the custom folder, one edit, and the files that keep ids.
            var custom = Path.Combine(from, "maps", "custom");
            var oldA = MapSyncService.IdOf(custom, Path.Combine("111", "main.adofai"));
            var oldB = MapSyncService.IdOf(custom, Path.Combine("222", "main.adofai"));
            var outside = MapSyncService.IdOf(@"D:\Elsewhere", "x.adofai");
            Directory.CreateDirectory(Path.Combine(from, "map-cache"));
            Directory.CreateDirectory(Path.Combine(from, "maps", "edited", "111", "1"));
            Directory.CreateDirectory(Path.Combine(from, "reports"));
            Directory.CreateDirectory(Path.Combine(custom, "111"));
            File.WriteAllText(Path.Combine(custom, "111", "main.adofai"), "{}");
            string Entry(string id, string root, string relative) =>
                $$"""{ "Id": "{{id}}", "RootDirectory": {{System.Text.Json.JsonSerializer.Serialize(root)}}, "RelativePath": {{System.Text.Json.JsonSerializer.Serialize(relative)}}, "SongName": "곡" }""";
            File.WriteAllText(Path.Combine(from, "map-cache", "library.json"), $$"""
                { "SchemaVersion": 1, "Entries": [ {{Entry(oldA, custom, Path.Combine("111", "main.adofai"))}},
                  {{Entry(oldB, custom, Path.Combine("222", "main.adofai"))}}, {{Entry(outside, @"D:\Elsewhere", "x.adofai")}} ] }
                """);
            File.WriteAllText(Path.Combine(from, "settings.json"), """{ "Language": "korean" }""");
            File.WriteAllText(Path.Combine(from, "songs.json"), $$"""{ "SchemaVersion": 1, "Songs": [ { "Id": "{{oldA}}", "Hidden": true } ] }""");
            File.WriteAllText(Path.Combine(from, "song-ids.json"), $$"""{ "SchemaVersion": 1, "Ids": { "{{oldA}}": "1aaaaaaa", "{{oldB}}": "1bbbbbbb" } }""");
            File.WriteAllText(Path.Combine(from, "song-marks.json"), $$"""{ "SchemaVersion": 1, "New": [ "{{oldB}}" ], "Last": "{{oldA}}" }""");
            File.WriteAllText(Path.Combine(from, "maps", "edited", "111", "1", "edit.json"), $$"""{ "SchemaVersion": 1, "SourceId": "{{oldA}}", "Slot": 1 }""");
            File.WriteAllText(Path.Combine(from, "reports", "lane idea.txt"), "chords on 3 and 4");

            var result = StoreMove.Run(from, to);
            Assert(result.Moved && result.LeftBehind is null, "The store moved");
            var newCustom = Path.Combine(to, "maps", "custom");
            var newA = MapSyncService.IdOf(newCustom, Path.Combine("111", "main.adofai"));
            var newB = MapSyncService.IdOf(newCustom, Path.Combine("222", "main.adofai"));
            Assert(newA != oldA && newB != oldB, "Ids follow the place, so they change");
            var cache = File.ReadAllText(Path.Combine(to, "map-cache", "library.json"));
            Assert(cache.Contains(newA) && cache.Contains(newB) && !cache.Contains(oldA) && cache.Contains(outside), "Cache ids moved; a foreign entry is left alone");
            Assert(cache.Contains(System.Text.Json.JsonSerializer.Serialize(newCustom)[1..^1]), "Cache roots point into the new store");
            Assert(File.ReadAllText(Path.Combine(to, "songs.json")).Contains(newA), "A hidden song stays hidden");
            var book = File.ReadAllText(Path.Combine(to, "song-ids.json"));
            Assert(book.Contains($"\"{newA}\": \"1aaaaaaa\"") && book.Contains($"\"{newB}\": \"1bbbbbbb\""), "Console ids stay the same: " + book);
            var marks = File.ReadAllText(Path.Combine(to, "song-marks.json"));
            Assert(marks.Contains(newB) && marks.Contains(newA) && !marks.Contains(oldA), "New marks and the last song follow");
            Assert(File.ReadAllText(Path.Combine(to, "maps", "edited", "111", "1", "edit.json")).Contains(newA), "An edit knows its original");
            Equal(File.ReadAllText(Path.Combine(to, "reports", "lane idea.txt")), "chords on 3 and 4");
            Equal(File.ReadAllText(Path.Combine(to, "maps", "custom", "111", "main.adofai")), "{}");
            Assert(File.Exists(Path.Combine(from, "songs.json")) && File.ReadAllText(Path.Combine(from, "songs.json")).Contains(oldA), "The old folder is kept as it was");
            Assert(!Directory.Exists(to + ".moving"), "Nothing is left half built");

            // Once is enough: a shared store that holds files is never overwritten, and the app says data was left behind.
            var again = StoreMove.Run(from, to);
            Assert(!again.Moved && again.LeftBehind == Path.GetFullPath(from), "A second move changes nothing");
            Assert(!StoreMove.Run(Path.Combine(artifactRoot, "move-nothing"), Path.Combine(artifactRoot, "move-none")).Moved, "No data, no move");

            // A store that points to a folder the user chose moves only the pointer: the data itself is not moving.
            var pointed = Path.Combine(artifactRoot, "move-pointed");
            var target = Path.Combine(artifactRoot, "move-pointed-to");
            foreach (var folder in new[] { pointed, target }) if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(pointed);
            File.WriteAllText(Path.Combine(pointed, "data-root.txt"), @"D:\Chosen");
            Assert(StoreMove.Run(pointed, target).Moved, "The pointer moves");
            Equal(Directory.GetFiles(target).Select(Path.GetFileName).Single(), "data-root.txt");
        }),

        ("Every copy shares one store under %LOCALAPPDATA% and it keeps settings across runs", () =>
        {
            // User request: a copy fetched for playing and the one being worked on see the same songs, settings and notes.
            var repo = WorkspaceMapCache.FindRepositoryRoot(AppContext.BaseDirectory);
            Equal(AppStorage.SharedRoot(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdofaiVisualizer"));
            Equal(AppStorage.RepositoryStoreFor(AppContext.BaseDirectory), Path.Combine(repo, ".local", "app-data"));
            Equal(AppStorage.RepositoryStoreFor(Path.GetPathRoot(repo)!), null);

            // Every write stays inside the test artifacts folder: the pointer file too, so the real store is untouched.
            var home = Path.Combine(artifactRoot, "store-home");
            var root = Path.Combine(artifactRoot, "store-roundtrip");
            foreach (var folder in new[] { home, root }) if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            var storage = AppStorage.At(home);
            Assert(storage.UsesDefaultRoot, "A fresh store starts in the default location");
            Assert(storage.ReadSettings() is null || storage.ReadSettings()!.Length > 0, "Reading an absent file returns null, never an empty string");

            storage.UseRoot(root, VisualizerSettings.DefaultMapRoot);
            try
            {
                Equal(storage.Root, root);
                Equal(storage.CacheDirectory, Path.Combine(root, "map-cache"));
                Assert(storage.ReadSettings() is null, "A new store starts empty; old files are left where they were");

                var saved = new VisualizerSettings { PlaybackSpeedPercent = 75, NoteTravelTimeMs = 640, Theme = "dark" };
                storage.WriteSettings(SettingsJson.Export(saved));
                Assert(File.Exists(Path.Combine(root, "settings.json")), "The settings file lands in the store");
                Assert(!Directory.EnumerateFiles(root, "*.tmp").Any(), "The temp file is gone after the write");

                // A second run: the pointer sends Open to the same folder and the values come back.
                var next = AppStorage.At(home);
                Equal(next.Root, root);
                var restored = SettingsJson.Import(next.ReadSettings(), new VisualizerSettings());
                Equal(restored.PlaybackSpeedPercent, 75);
                Equal(restored.NoteTravelTimeMs, 640);
                Equal(restored.Theme, "dark");
            }
            finally
            {
                storage.UseRoot("", VisualizerSettings.DefaultMapRoot);
            }
            Assert(storage.UsesDefaultRoot, "An empty path restores the default store");
            Assert(AppStorage.At(home).UsesDefaultRoot, "Clearing the path removes the pointer for the next run");
            Assert(Directory.Exists(root), "Clearing the setting never deletes what was written");
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }),

        ("Every setting survives the settings window and the stored file", () =>
        {
            var settings = EverySettingChanged();
            // Every setting is changed, so one the window does not read back cannot pass unseen (user report: a new trill
            // window went back to 40 ms on Apply, and the snapshot here had never changed it).
            var defaults = new VisualizerSettings();
            var unchanged = typeof(VisualizerSettings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(property => property.CanWrite && !Unchangeable.Contains(property.Name)
                    && Equals(property.GetValue(settings), property.GetValue(defaults)))
                .Select(property => property.Name).ToArray();
            Assert(unchanged.Length == 0, "Settings left at their default here: " + string.Join(", ", unchanged));

            // The file: what Apply writes is what the next launch reads back.
            var stored = SettingsJson.Import(SettingsJson.Export(settings), new VisualizerSettings());
            Assert(Differences(stored, settings).Length == 0, "Lost in the settings file — " + Differences(stored, settings));

            // The dialog: opening it and pressing Apply without touching anything must not change a setting.
            var window = new SettingsPanel(settings, new MemorySettingsLayoutStore());
            var applied = window.BuildSettings();
            Assert(Differences(applied, settings).Length == 0, "Lost in the settings window — " + Differences(applied, settings));
        }),

        ("The trill settings: a typed trill window applies, the center pair is a list of three, and older files carry over", () =>
        {
            // User report: the trill window went back to 40 ms on Apply whatever was typed.
            var window = new SettingsPanel(new VisualizerSettings(), new MemorySettingsLayoutStore());
            ((TextBox)window.InputFor(nameof(VisualizerSettings.TrillWindowMs))!).Text = "30";
            Equal(window.BuildSettings().TrillWindowMs, 30d);

            // User rule: the center pair is a list (allowed, last resort, never) instead of a toggle, last resort by default.
            var center = (ComboBox)window.InputFor(nameof(VisualizerSettings.TrillCenterPair))!;
            Equal(string.Join(" / ", center.Items.Cast<string>()), "가능 / 최후순위 / 불가능");
            Equal(center.SelectedIndex, 1);
            center.SelectedIndex = 2;
            Equal(window.BuildSettings().TrillCenterPair, "Never");
            var labels = FindAll<TextBlock>(window).Select(block => block.Text).ToHashSet();
            Assert(labels.Contains("트릴 중앙 배치") && labels.Contains("트릴을 단일 노트로 판정"), "The renamed labels");

            // A file from before the list holds the toggle: on was the last resort, off used the center like any pair.
            Equal(SettingsJson.Import("""{ "NoTrillCenterPair": true }""", new VisualizerSettings { TrillCenterPair = "Never" }).TrillCenterPair, "LastResort");
            Equal(SettingsJson.Import("""{ "NoTrillCenterPair": false }""", new VisualizerSettings()).TrillCenterPair, "Allowed");
            Equal(SettingsJson.Import("""{ "NoTrillCenterPair": false, "TrillCenterPair": "Never" }""", new VisualizerSettings()).TrillCenterPair, "Never");
            IReadOnlyList<string>? refused = null;
            try { SettingsJson.Import("""{ "NoTrillCenterPair": "yes" }""", new VisualizerSettings()); }
            catch (SettingsJsonException error) { refused = error.Fields; }
            Assert(refused is not null && refused.SequenceEqual(["NoTrillCenterPair"]), "A wrong old value is named: " + string.Join(", ", refused ?? []));
            Throws<SettingsJsonException>(() => SettingsJson.Import("""{ "TrillCenterPair": "Sometimes" }""", new VisualizerSettings()));
            Assert(!SettingsJson.Export(new VisualizerSettings()).Contains("NoTrillCenterPair", StringComparison.Ordinal), "Only the new name is written");
        }),

        ("Importing an export in the settings window brings back every setting but this PC's folders", () =>
        {
            var settings = EverySettingChanged();
            // The app's own file keeps the folders; the copy for sharing leaves them out.
            var export = SettingsJson.Export(settings);
            var portable = SettingsJson.ExportPortable(settings);
            Assert(export.Contains("MapRootDirectory", StringComparison.Ordinal) && export.Contains("DataRootDirectory", StringComparison.Ordinal),
                "The settings file keeps the folders");
            Assert(!portable.Contains("MapRootDirectory", StringComparison.Ordinal) && !portable.Contains("DataRootDirectory", StringComparison.Ordinal),
                "The shared copy leaves the folders out");
            // Only real settings are written: values worked out from others would do nothing if edited, and an exported
            // one that is not edited along with its source makes the import refuse the file. Every computed property is
            // checked, so a new one cannot slip in unmarked.
            foreach (var derived in typeof(VisualizerSettings).GetProperties().Where(property => !property.CanWrite).Select(property => property.Name))
                Assert(!export.Contains($"\"{derived}\"", StringComparison.Ordinal), derived + " is not a setting and is not exported");
            // So an exported file can be edited by hand: changing a setting alone imports.
            var edited = System.Text.Json.Nodes.JsonNode.Parse(SettingsJson.ExportPortable(new VisualizerSettings()))!.AsObject();
            edited[nameof(VisualizerSettings.JudgmentBarDifficulty)] = "Strict";
            edited[nameof(VisualizerSettings.ExperimentalFeatures)] = true;
            edited[nameof(VisualizerSettings.ChordAvoidBeforeLanes)] = true;
            var hand = SettingsJson.Import(edited.ToJsonString(), new VisualizerSettings());
            Assert(hand.JudgmentWindowMs == 40 && hand.UseChordAvoidBefore, "An edited export imports");

            // Import into a window that starts from the defaults, then Apply: nothing but the folders may change on the way.
            var here = new VisualizerSettings { MapRootDirectory = @"C:\Here\Maps", DataRootDirectory = "" };
            var expected = settings with { MapRootDirectory = here.MapRootDirectory, DataRootDirectory = here.DataRootDirectory };
            foreach (var (text, named) in new[] { (portable, false), (export, true) })
            {
                var window = new SettingsPanel(here, new MemorySettingsLayoutStore());
                var json = FindAll<TextBox>(window).Single(box => box.AcceptsReturn);
                var panel = (FrameworkElement)LogicalTreeHelper.GetParent(json);
                FindAll<Button>(window).Single(button => Equals(button.Content, "설정 불러오기")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Equal(panel.Visibility, Visibility.Visible);
                json.Text = text;
                FindAll<Button>(panel).First().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Equal(panel.Visibility, Visibility.Collapsed);
                var imported = window.BuildSettings();
                Assert(Differences(imported, expected).Length == 0, "Lost importing in the window — " + Differences(imported, expected));
                // An older export names the folders; the form keeps this PC's and says so. The status is in the imported language.
                var kept = FindAll<TextBlock>(window).Any(block => block.Text.Contains(Languages.Get("english", "ImportKeptPaths"), StringComparison.Ordinal));
                Assert(kept == named, named ? "Says the folders were kept" : "Nothing to say about folders");
            }

            // A file written by 0.4.6 or earlier still carries the worked-out values: they are fine while they agree...
            var old = System.Text.Json.Nodes.JsonNode.Parse(export)!.AsObject();
            old["UseChordCenterPair"] = settings.UseChordCenterPair;
            old["PressedNoteStrength"] = settings.PressedNoteStrength;
            old["MagicCircleTiles"] = settings.MagicCircleTiles;
            var restored = SettingsJson.Import(old.ToJsonString(), new VisualizerSettings());
            Assert(Differences(restored, settings).Length == 0, "An older file still loads — " + Differences(restored, settings));
            // ...and one typed in by hand, which could not change anything, is refused with the reason.
            old["UseChordCenterPair"] = !settings.UseChordCenterPair;
            IReadOnlyList<string>? refused = null;
            try { SettingsJson.Import(old.ToJsonString(), new VisualizerSettings()); }
            catch (SettingsJsonException error) { refused = error.Fields; }
            Assert(refused is not null && refused.SequenceEqual(["UseChordCenterPair"]), "The refused value is named: " + string.Join(", ", refused ?? []));

            // Older files hold the chord rules as experiments, with the center pair named the other way round. With experiments
            // on, the file's rules carry over; with them off, they did nothing, so the rules that were in effect (the defaults) come in.
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(export)!.AsObject();
            legacy.Remove(nameof(VisualizerSettings.NoChordCenterPair));
            legacy["ChordCenterPair"] = true;
            var carried = SettingsJson.Import(legacy.ToJsonString(), new VisualizerSettings());
            Assert(carried.NoChordCenterPair == false && carried.ChordSectionLanes && carried.ChordLanePreference == "3-4", "Experiments on: the rules carry over");
            legacy["ExperimentalFeatures"] = false;
            var inEffect = SettingsJson.Import(legacy.ToJsonString(), new VisualizerSettings { NoChordCenterPair = false, ChordSectionLanes = true });
            Assert(inEffect.NoChordCenterPair && !inEffect.ChordSectionLanes && inEffect.ChordLanePreference == "None" && !inEffect.ExperimentalFeatures,
                "Experiments off: the rules that were in effect");
            legacy["ChordCenterPair"] = "yes";
            Throws<SettingsJsonException>(() => SettingsJson.Import(legacy.ToJsonString(), new VisualizerSettings()));

            // A partial file changes only what it names, and the rest of the form stays as it was.
            var partial = new SettingsPanel(settings, new MemorySettingsLayoutStore());
            var partialJson = FindAll<TextBox>(partial).Single(box => box.AcceptsReturn);
            FindAll<Button>(partial).Single(button => Equals(button.Content, "Import settings")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            partialJson.Text = "{ \"notetraveltimems\": 900, \"Theme\": \"white\" }";
            FindAll<Button>((FrameworkElement)LogicalTreeHelper.GetParent(partialJson)).First().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var changed = settings with { NoteTravelTimeMs = 900, Theme = "white" };
            Assert(Differences(partial.BuildSettings(), changed).Length == 0, "Partial import — " + Differences(partial.BuildSettings(), changed));
        }),

        ("A data folder must be absolute and clear of the map folder", () =>
        {
            var maps = @"C:\Maps\Workshop";
            string? Reason(string path) => new VisualizerSettings { MapRootDirectory = maps, DataRootDirectory = path }
                .ValidateFields().FirstOrDefault(error => error.Field == nameof(VisualizerSettings.DataRootDirectory))?.Message;

            Assert(Reason("") is null, "Empty keeps the default store");
            Assert(Reason(@"D:\Visualizer") is null, "A folder of its own is fine");
            Assert(Reason(@"data\store") is not null, "A relative path is refused");
            Assert(Reason(maps) is not null, "The map folder itself is refused");
            Assert(Reason(maps + @"\store") is not null, "A folder inside the map folder is refused");
            Assert(Reason(@"C:\Maps") is not null, "A folder containing the map folder is refused");
            Throws<IOException>(() => AppStorage.Validate(maps + @"\store", maps));
            Throws<ArgumentException>(() => AppStorage.Validate(@"data\store", maps));
        }),

        ("The settings window shows the store in use and picks a new one", () =>
        {
            var window = new SettingsPanel(new VisualizerSettings(), new MemorySettingsLayoutStore())
            {
                DataRootInUse = @"C:\Store\app-data"
            };
            // The row holds the box and its picker side by side, so reach through the pair.
            var cell = window.InputFor(nameof(VisualizerSettings.DataRootDirectory));
            var box = cell as TextBox ?? FindAll<TextBox>(cell!).First();
            Assert(box is not null, "The data folder row has a text box");
            Equal(box!.Text, "");
            Equal(window.BuildSettings().DataRootDirectory, "");

            var shown = FindAll<TextBlock>(window).Select(text => text.Text).ToList();
            Assert(shown.Any(text => text.Contains(@"C:\Store\app-data")), "The store in use is shown under the box");

            // The picker starts from the store in use while the box is empty, and only fills the box in.
            string? asked = null;
            window.PickFolder = current => { asked = current; return @"E:\Visualizer"; };
            var picker = FindAll<Button>(window).Where(button => Equals(button.Content, "파일")).ToList()[1];
            picker.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal(asked, @"C:\Store\app-data");
            Equal(box.Text, @"E:\Visualizer");
            Equal(window.BuildSettings().DataRootDirectory, @"E:\Visualizer");
            Assert(window.IsMarkedChanged(nameof(VisualizerSettings.DataRootDirectory)), "Picking a folder is a pending change");
        }),
    };
}
