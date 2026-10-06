using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Visualizer.App.Localization;
using Visualizer.App.ViewModels;
using Visualizer.App.Views;
using Visualizer.Application.Cache;
using Visualizer.Application.Library;
using Visualizer.Application.Maps;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Infrastructure.Cache;
using Visualizer.Infrastructure.Maps;
using Visualizer.Infrastructure.Parsing;
using Visualizer.Infrastructure.Storage;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

/// <summary>The song list: order, hidden songs, display names, its store and the search toggle.</summary>
internal static class SongListTests
{
    /// <summary>Collects progress as it is reported. Progress&lt;T&gt; would post it and the test would read an empty list.</summary>
    private sealed class Steps<T>(Action<T> add) : IProgress<T>
    {
        public void Report(T value) => add(value);
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static MapCacheEntry Song(string id, string name, int? difficulty = null, int notes = 4, string? artist = null) =>
        new(id, @"C:\maps", $"{id}\\main.adofai", name, MapParseStatus.Supported,
            new ParsedTimeline(Enumerable.Range(0, notes).Select(i => i * 500d).ToArray(), Array.Empty<TimelineCheckpoint>(), false),
            Array.Empty<string>(), "", "test", new MapSongInfo(artist, name, null, difficulty));

    public static IEnumerable<(string Name, Action Run)> Create(string artifactRoot) => new (string, Action)[]
    {
        ("The song order follows the chosen field and direction, with blanks last", () =>
        {
            var songs = new[]
            {
                Song("a", "Beta", difficulty: 12, notes: 9),
                Song("b", "alpha", difficulty: 3, notes: 20),
                Song("c", "Gamma", difficulty: null, notes: 2),
            };
            string[] Order(SongSortField field, bool highFirst) =>
                SongOrdering.Sort(songs, field, highFirst).Select(entry => entry.SongName).ToArray();

            // The default: hardest on top, and the song without a difficulty stays last either way.
            Sequence(Order(SongSortField.Difficulty, true), "Beta", "alpha", "Gamma");
            Sequence(Order(SongSortField.Difficulty, false), "alpha", "Beta", "Gamma");
            Sequence(Order(SongSortField.Title, false), "alpha", "Beta", "Gamma");
            Sequence(Order(SongSortField.Title, true), "Gamma", "Beta", "alpha");
            Sequence(Order(SongSortField.NoteCount, true), "alpha", "Beta", "Gamma");
            // Only pressed notes count: 30 hits of which 29 are auto tiles is one note.
            var auto = Song("d", "Delta", notes: 30) with { Timeline = new ParsedTimeline(Enumerable.Range(0, 30).Select(i => i * 500d).ToArray(),
                hitKinds: Enumerable.Range(0, 30).Select(i => i < 29 ? HitKind.Auto : HitKind.Tap).ToArray()) };
            Sequence(SongOrdering.Sort([.. songs, auto], SongSortField.NoteCount, true).Select(entry => entry.SongName).ToArray(),
                "alpha", "Beta", "Gamma", "Delta");
            // The sync order is the order the cache lists them in; newest on top reverses it.
            Sequence(Order(SongSortField.Synced, true), "Gamma", "alpha", "Beta");
            Sequence(Order(SongSortField.Synced, false), "Beta", "alpha", "Gamma");

            // Manual: placed songs first in their own order, the rest behind them as they came.
            var manual = new Dictionary<string, SongPreference> { ["c"] = new("c", Order: 0), ["a"] = new("a", Order: 1) };
            Sequence(SongOrdering.Sort(songs, SongSortField.Manual, true, manual).Select(e => e.SongName).ToArray(),
                "Gamma", "Beta", "alpha");

            static void Sequence(string[] actual, params string[] expected) =>
                Assert(actual.SequenceEqual(expected), "Order: " + string.Join(" / ", actual));
        }),

        ("Hidden songs leave the main list and a display name replaces the title", () =>
        {
            var model = new MainViewModel();
            model.ApplySettings(model.Settings with { SongSortKey = nameof(SongSortField.Title), SongSortHighFirst = false });
            var entries = new[] { Song("a", "Beta"), Song("b", "alpha"), Song("c", "Gamma") };
            model.SetLibrary(entries);
            Equal(model.Library.Count, 3);
            Equal(model.Library[0].SongName, "alpha");

            model.SongPreferences = [new SongPreference("b", Hidden: true), new SongPreference("a", DisplayName: "My song")];
            Equal(model.Library.Count, 2);
            Equal(model.AllSongs.Count, 3); // The song list window still sees the hidden one, under its own name.
            Assert(model.AllSongs.Any(entry => entry.Id == "b"), "A hidden song stays in the full list");
            Assert(model.Library.All(entry => entry.Id != "b"), "A hidden song leaves the main list");
            Equal(model.Library.Single(entry => entry.Id == "a").SongName, "My song");
            // The rename is display only: the arrows and the picker follow it, the cache keeps the original.
            Equal(model.Library[0].SongName, "Gamma");

            model.SongPreferences = [];
            Equal(model.Library.Count, 3);
            Equal(model.Library.Single(entry => entry.Id == "a").SongName, "Beta");
        }),

        ("The song list store keeps hidden, renamed and dragged songs", () =>
        {
            var home = Path.Combine(artifactRoot, "song-store-home");
            var root = Path.Combine(artifactRoot, "song-store");
            foreach (var folder in new[] { home, root }) if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            var storage = AppStorage.At(home);
            storage.UseRoot(root, VisualizerSettings.DefaultMapRoot);
            try
            {
                var store = new WorkspaceSongPreferences(storage);
                Equal(store.Read().Count, 0);
                store.Write([new SongPreference("a", Hidden: true), new SongPreference("b", DisplayName: "Name", Order: 2),
                    new SongPreference("c")]);
                var read = new WorkspaceSongPreferences(storage).Read();
                // A song with no choice at all is not worth keeping.
                Equal(read.Count, 2);
                Assert(read.Single(song => song.Id == "a").Hidden == true, "Hidden is kept");
                Equal(read.Single(song => song.Id == "b").DisplayName, "Name");
                Equal(read.Single(song => song.Id == "b").Order, 2);
                Assert(File.Exists(Path.Combine(root, "songs.json")), "The file lands in the store");
            }
            finally { storage.UseRoot("", VisualizerSettings.DefaultMapRoot); }
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }),

        ("The settings card holds both order lists and the detail button; typing in the song box is not a setting", () =>
        {
            var window = new SettingsPanel(new VisualizerSettings(), new MemorySettingsLayoutStore());
            // User rule: the song box always takes typing, so there is no switch for it, and a settings file that still
            // names the old one is read without it rather than refused.
            Assert(typeof(VisualizerSettings).GetProperty("SongSearch") is null, "No search switch");
            var kept = SettingsJson.Import("{ \"SongSearch\": false, \"SongSortKey\": \"Title\" }", new VisualizerSettings());
            Equal(kept.SongSortKey, "Title");

            var row = window.InputFor(nameof(VisualizerSettings.SongSortKey))!;
            var lists = FindAll<ComboBox>(row).ToList();
            Equal(lists.Count, 2);
            Equal(window.BuildSettings().SongSortKey, nameof(SongSortField.Difficulty));
            Assert(window.BuildSettings().SongSortHighFirst, "Highest first by default");
            lists[1].SelectedIndex = 1;
            Assert(!window.BuildSettings().SongSortHighFirst, "The direction list reaches the settings");
            // A hand-made order has no direction, so that list locks while keeping its value.
            lists[0].SelectedIndex = (int)SongSortField.Manual;
            Assert(!lists[1].IsEnabled, "Manual locks the direction list");
            Equal(window.BuildSettings().SongSortKey, nameof(SongSortField.Manual));
            Assert(!window.BuildSettings().SongSortHighFirst, "The locked direction keeps its value");
            lists[0].SelectedIndex = (int)SongSortField.Title;
            Assert(lists[1].IsEnabled, "Leaving Manual unlocks it again");

            // An unknown order name is refused by name, not silently replaced.
            var bad = new VisualizerSettings { SongSortKey = "Loudness" }.ValidateFields();
            Assert(bad.Any(error => error.Field == nameof(VisualizerSettings.SongSortKey)), "An unknown order is refused");
        }),

        ("The detail switches pick what each row shows, in the fixed order", () =>
        {
            var songs = new[] { Song("a", "Beta", difficulty: 12, notes: 6, artist: "Someone") };
            var window = new SongListWindow(songs, [], new VisualizerSettings());
            Equal(window.Width, 860d);

            // Every detail starts on, and the row lists them in the order the switches are in.
            var toggles = FindAll<ToggleButton>(window).ToList();
            Sequence(toggles.Select(toggle => (string)toggle.Content!), "작곡가", "길이", "노트 수", "난이도", "체크포인트", "날짜", "경로");
            Assert(toggles.All(toggle => toggle.IsChecked == true), "All details start on");
            Equal(window.Collect().Fields, "Artist Length Notes Difficulty Checkpoints Date Path");
            var line = Details(window);
            Assert(line.StartsWith("작곡가 Someone · 길이 ", StringComparison.Ordinal) && line.EndsWith(@"a\main.adofai", StringComparison.Ordinal),
                "Row details: " + line);
            Assert(line.IndexOf("노트 6개", StringComparison.Ordinal) < line.IndexOf("난이도 12", StringComparison.Ordinal)
                && line.IndexOf("난이도 12", StringComparison.Ordinal) < line.IndexOf("체크포인트 0개", StringComparison.Ordinal),
                "Notes, difficulty, checkpoints in that order: " + line);

            // Checkpoint 1 is the start of the song: a map without checkpoints shows 0, one with two shows 2.
            var start = Song("b", "Gamma") with { Timeline = new ParsedTimeline(new[] { 0d, 500, 1000 }, [new TimelineCheckpoint(1, 0)], true,
                hitKinds: new[] { HitKind.Tap, HitKind.HoldStart, HitKind.HoldEnd }) };
            var two = Song("c", "Delta") with { Timeline = new ParsedTimeline(new[] { 0d, 500, 1000 },
                [new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 1), new TimelineCheckpoint(3, 2)], true) };
            var counted = new SongListWindow([start, two], [], new VisualizerSettings());
            var rows = FindAll<TextBlock>(counted).Select(text => text.Text).Where(text => text.Contains(" · ", StringComparison.Ordinal)).ToList();
            Assert(rows.Count == 2 && rows.Count(row => row.Contains("체크포인트 0개")) == 1 && rows.Count(row => row.Contains("체크포인트 2개")) == 1,
                "Checkpoint counts: " + string.Join(" | ", rows));
            // A long note's end is not pressed, so it is not a note.
            Assert(rows.Any(row => row.Contains("노트 2개") && row.Contains("체크포인트 0개")) && rows.Any(row => row.Contains("노트 3개") && row.Contains("체크포인트 2개")),
                "Pressed notes: " + string.Join(" | ", rows));

            // Switching one off drops it from the row and from what the window gives back.
            toggles[0].IsChecked = false;
            toggles[6].IsChecked = false;
            Assert(!Details(window).Contains("작곡가", StringComparison.Ordinal), "The artist is gone");
            Assert(!Details(window).Contains("main.adofai", StringComparison.Ordinal), "The path is gone");
            Equal(window.Collect().Fields, "Length Notes Difficulty Checkpoints Date");
            // A settings snapshot keeps them in the fixed order, whatever order they were written in.
            Sequence(new VisualizerSettings { SongListFields = "Path Notes" }.GetSongListFields(), "Notes", "Path");
            Assert(new VisualizerSettings { SongListFields = "Loudness" }.ValidateFields()
                .Any(error => error.Field == nameof(VisualizerSettings.SongListFields)), "An unknown detail is refused");

            static string Details(SongListWindow window) =>
                FindAll<TextBlock>(window).Select(text => text.Text).FirstOrDefault(text => text.Contains(" · ", StringComparison.Ordinal)) ?? "";
            static void Sequence(IEnumerable<string> actual, params string[] expected) =>
                Assert(actual.SequenceEqual(expected), "Order: " + string.Join(" / ", actual));
        }),

        ("The song list's search box narrows the rows and parks dragging until it is cleared", () =>
        {
            var window = new SongListWindow([Song("a", "Alpha"), Song("b", "Beta"), Song("c", "Gamma")], [], new VisualizerSettings());
            string[] Listed() => FindAll<TextBlock>(window).Where(text => text.FontSize > 13).Select(text => text.Text).Order(StringComparer.Ordinal).ToArray();
            List<Button> Handles() => FindAll<Button>(window).Where(button => Equals(button.Content, "\u2261")).ToList();
            Equal(string.Join(" ", Listed()), "Alpha Beta Gamma");
            Assert(Handles().All(handle => handle.IsEnabled), "Every song can be dragged with no search");

            var search = FindAll<TextBox>(window).First();
            search.Text = "et";
            Equal(string.Join(" ", Listed()), "Beta");
            // The order is kept in full, so it cannot be rearranged from a part of it.
            Assert(Handles() is [{ IsEnabled: false }], "The handle waits for the search to be cleared");

            search.Text = "no such song";
            Equal(string.Join(" ", Listed()), "");
            Assert(FindAll<TextBlock>(window).Any(text => text.Text == "\uac80\uc0c9 \uacb0\uacfc\uac00 \uc5c6\uc2b5\ub2c8\ub2e4."), "It says nothing matched");

            search.Text = "";
            Equal(string.Join(" ", Listed()), "Alpha Beta Gamma");
            Assert(Handles().All(handle => handle.IsEnabled), "Dragging is back");
        }),

        ("Removing a song from the song list asks once more and then happens at once", () =>
        {
            var songs = new[] { Song("a", "Alpha"), Song("b", "Beta") };
            Assert(!FindAll<Button>(new SongListWindow(songs, [], new VisualizerSettings())).Any(button => Equals(button.Content, "\uace1 \uc81c\uac70")),
                "A caller that cannot delete is not offered the button");

            var removed = new List<string>();
            var asked = 0;
            var window = new SongListWindow(songs, [], new VisualizerSettings()) { RemoveSong = entry => { removed.Add(entry.Id); return null; } };
            Assert(FindAll<Button>(window).Count(button => Equals(button.Content, "\uace1 \uc81c\uac70")) == 2, "One button per song");

            // Nothing is deleted until the second question is accepted.
            window.Confirm = (_, _) => { asked++; return false; };
            window.RemoveRow(0);
            Equal(asked, 1);
            Assert(removed.Count == 0, "Saying no deletes nothing");
            Assert(FindAll<TextBlock>(window).Any(text => text.Text == "Alpha"), "Alpha is still listed");

            window.Confirm = (_, _) => { asked++; return true; };
            window.RemoveRow(0);
            Equal(asked, 2);
            Equal(string.Join(" ", removed), "a");
            Assert(!FindAll<TextBlock>(window).Any(text => text.Text == "Alpha"), "Alpha left the list");
            Assert(window.Collect().Preferences.All(song => song.Id != "a"), "It is not handed back as a choice either");

            // A refused removal keeps the row and says why.
            window.RemoveSong = _ => "The song could not be removed.";
            window.RemoveRow(0);
            Assert(FindAll<TextBlock>(window).Any(text => text.Text == "The song could not be removed."), "The reason is shown");
            Assert(FindAll<TextBlock>(window).Any(text => text.Text == "Beta"), "Beta stays");
        }),

        ("Hidden songs are told apart by a dimmer title, not by a different button", () =>
        {
            var songs = new[] { Song("a", "Beta"), Song("b", "alpha") };
            var window = new SongListWindow(songs, [new SongPreference("a", Hidden: true)], new VisualizerSettings());
            Capture(window, Path.Combine(artifactRoot, "song-list.png"), 860, 420);

            // Both ways look the same: only the title says which song is hidden.
            var show = FindAll<Button>(window).Single(button => Equals(button.Content, "보이기"));
            var hide = FindAll<Button>(window).Single(button => Equals(button.Content, "숨기기"));
            Assert(ReferenceEquals(show.Style, hide.Style), "Show and hide share one style");
            Assert(ReferenceEquals(show.Style, window.FindResource("ToolButton")), "Both are plain buttons");

            var titles = FindAll<TextBlock>(window).Where(text => text.FontSize > 13).ToList();
            var hidden = titles.Single(text => text.Text == "Beta");
            var listed = titles.Single(text => text.Text == "alpha");
            Assert(hidden.Opacity < listed.Opacity, $"The hidden title is dimmer: {hidden.Opacity} vs {listed.Opacity}");
            Assert(ReferenceEquals(hidden.Foreground, window.FindResource("MutedTextBrush")), "The hidden title is muted");

            // Rows keep one height whatever they hold, so the list never jumps.
            var rows = FindAll<Border>(window).Where(border => border.AllowDrop).ToList();
            Equal(rows.Count, 2);
            Assert(rows.All(row => Math.Abs(row.Height - rows[0].Height) < 0.5 && row.Height > 0), "Rows share a fixed height");
        }),

        ("Dragging a song into a gap reorders the list and switches the sort to manual", () =>
        {
            var songs = new[] { Song("a", "One"), Song("b", "Two"), Song("c", "Three") };
            var settings = new VisualizerSettings { SongSortKey = nameof(SongSortField.Synced), SongSortHighFirst = false };
            var window = new SongListWindow(songs, [], settings);
            Assert(!window.Collect().Reordered, "Nothing has moved yet");

            // Gap 3 is below the last song: the first one goes to the end.
            Assert(window.MoveRow(0, 3), "The first song moved to the end");
            var moved = window.Collect();
            Assert(moved.Reordered, "The window reports a hand-made order");
            Sequence(moved.Preferences.OrderBy(song => song.Order).Select(song => song.Id), "b", "c", "a");

            // Where the pointer lets go decides the gap. Only the rows took the drop before, so letting go between two
            // of them, or below the last one, did nothing; every height here counts the row and the margin under it.
            Equal(window.GapAt(0), 0);
            Equal(window.GapAt(30), 0);
            Equal(window.GapAt(40), 1);
            Equal(window.GapAt(70), 1);
            Equal(window.GapAt(108), 2);
            Equal(window.GapAt(10_000), 3);
            Equal(window.GapAt(-50), 0);

            // A drop back into its own gap is not a change (no off-by-one from taking the row out).
            Assert(!window.MoveRow(1, 1) && !window.MoveRow(1, 2), "Dropping a song where it already is changes nothing");
            Assert(window.MoveRow(2, 0), "The last song moved to the top");
            Sequence(window.Collect().Preferences.OrderBy(song => song.Order).Select(song => song.Id), "a", "b", "c");

            static void Sequence(IEnumerable<string> actual, params string[] expected) =>
                Assert(actual.SequenceEqual(expected), "Order: " + string.Join(" ", actual));
        }),

        ("A level beside main.adofai starts hidden and can be brought back for good", () =>
        {
            // Three shapes seen in real map folders: an editor backup, a variant beside main.adofai, and a folder of
            // two songs with no main.adofai at all.
            var main = Song("m", "Song") with { RootDirectory = @"C:\mapsÁ2389896", RelativePath = "main.adofai" };
            var sub = Song("s", "Sub") with { RootDirectory = @"C:\mapsÁ2389896", RelativePath = "sub1.adofai" };
            var first = Song("f", "First") with { RootDirectory = @"C:\maps\pack", RelativePath = "5-X.adofai" };
            var second = Song("d", "Second") with { RootDirectory = @"C:\maps\pack", RelativePath = "6-X.adofai" };

            var model = new MainViewModel();
            model.SetLibrary([main, sub, first, second]);
            Assert(model.IsHiddenByDefault(sub) && !model.IsHiddenByDefault(main), "Only the file beside main.adofai is automatic");
            Assert(!model.IsHiddenByDefault(first) && !model.IsHiddenByDefault(second), "A folder without main.adofai keeps both songs");
            Equal(model.AllSongs.Count, 4);       // The song list window still lists everything.
            Equal(model.Library.Count, 3);        // The main screen leaves the variant out.
            Assert(model.Library.All(entry => entry.Id != "s"), "The variant is not in the main list");

            // Saying "show this one" is a decision, so it is stored and survives the next sync.
            model.SongPreferences = [new SongPreference("s", Hidden: false)];
            Equal(model.Library.Count, 4);
            model.SetLibrary([main, sub, first, second]);
            Equal(model.Library.Count, 4);

            // The window starts the row hidden, says why, and writes nothing until the user decides.
            var window = new SongListWindow([main, sub], [], new VisualizerSettings(), null, model.IsHiddenByDefault);
            Assert(FindAll<TextBlock>(window).Any(text => text.Text == "main.adofai 옆 파일"), "The reason is shown");
            Assert(window.Collect().Preferences.All(song => song.Hidden is null), "An automatic default is not a stored choice");
            FindAll<Button>(window).Single(button => Equals(button.Content, "보이기")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal(window.Collect().Preferences.Single(song => song.Id == "s").Hidden, (bool?)false);
        }),

        ("A song sitting only in the store folder syncs and plays on its own", () =>
        {
            var home = Path.Combine(artifactRoot, "store-only-home");
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
            var storage = AppStorage.At(home);
            var maps = new MapLibrary(storage);
            // The user drops a level into the store's base folder; no Steam folder is involved.
            var folder = Path.Combine(maps.BaseRoot, "my song");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "main.adofai"),
                "{\"settings\":{\"version\":15,\"bpm\":120},\"angleData\":[0,0,0,0],\"actions\":[]}");

            var cache = new WorkspaceMapCache(storage);
            var service = new MapSyncService(cache, cache, new AdofaiTimelineParser());
            var report = service.SyncAsync(new ReadOnlyDirectoryMapSource(maps.BaseRoot)).GetAwaiter().GetResult();
            Equal(report.Entries.Count, 1);
            var entry = report.Entries[0];
            Equal(entry.Status, MapParseStatus.Supported);
            Assert(entry.Timeline is { HitTimesMs.Count: > 0 }, "The song parsed into hit times");
            Equal(maps.KindOf(entry), MapKind.Base);

            // It reaches the list and plays from the cache alone, with no map folder anywhere.
            var model = new MainViewModel { SongKind = maps.KindOf, SongSlot = maps.SlotOf };
            model.SetLibrary(report.Entries);
            Equal(model.Library.Count, 1);
            model.SelectedMap = model.Library[0];
            Assert(model.LoadSelectedSong(), "The song loads");
            Assert(model.Playback.Timeline is not null, "Playback has its timeline");

            // A map folder that is not there throws, so the two scans must stay independent (the main window catches it).
            Throws<IOException>(() => new ReadOnlyDirectoryMapSource(Path.Combine(artifactRoot, "no-such-folder"))
                .DiscoverAsync().GetAwaiter().GetResult());

            Directory.Delete(home, recursive: true);
        }),

        ("A rebuild takes an unsubscribed song out of the cache as well as out of the folder", () =>
        {
            // User report: after a rebuild the songs that are no longer subscribed were still listed. The folder side was
            // already covered; this follows the same step through the cache, which is what the list reads.
            var home = Path.Combine(artifactRoot, "resync-home");
            var steam = Path.Combine(artifactRoot, "resync-steam");
            foreach (var folder in new[] { home, steam }) if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            foreach (var id in new[] { "1111", "2222" })
            {
                Directory.CreateDirectory(Path.Combine(steam, id));
                File.WriteAllText(Path.Combine(steam, id, "main.adofai"),
                    "{\"settings\":{\"version\":19,\"bpm\":120},\"angleData\":[0,0],\"actions\":[]}");
            }
            var store = AppStorage.At(home);
            var maps = new MapLibrary(store);
            var cache = new WorkspaceMapCache(store);
            var service = new MapSyncService(cache, cache, new AdofaiTimelineParser());
            IReadOnlyList<string> Cached()
            {
                maps.SyncCustomAsync(new ReadOnlyDirectoryMapSource(steam)).GetAwaiter().GetResult();
                service.SyncAsync(new ReadOnlyDirectoryMapSource(maps.CustomRoot)).GetAwaiter().GetResult();
                return [.. cache.ReadAsync().GetAwaiter().GetResult().Select(e => e.RelativePath).Order(StringComparer.Ordinal)];
            }
            Equal(string.Join(" ", Cached()), Path.Combine("1111", "main.adofai") + " " + Path.Combine("2222", "main.adofai"));

            // Unsubscribing 2222 and rebuilding: the copy goes, and so must its cache entry.
            Directory.Delete(Path.Combine(steam, "2222"), recursive: true);
            maps.RebuildCustomAsync(new ReadOnlyDirectoryMapSource(steam)).GetAwaiter().GetResult();
            Assert(!Directory.Exists(Path.Combine(maps.CustomRoot, "2222")), "The copy is gone");
            service.SyncAsync(new ReadOnlyDirectoryMapSource(maps.CustomRoot)).GetAwaiter().GetResult();
            var left = cache.ReadAsync().GetAwaiter().GetResult().Select(e => e.RelativePath).ToArray();
            Equal(string.Join(" ", left), Path.Combine("1111", "main.adofai"));
        }),

        ("The map library copies the Steam folder, rebuilds it, and deletes only its own files", () =>
        {
            var home = Path.Combine(artifactRoot, "library-home");
            var steam = Path.Combine(artifactRoot, "library-steam");
            foreach (var folder in new[] { home, steam }) if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(Path.Combine(steam, "1111"));
            Directory.CreateDirectory(Path.Combine(steam, "2222"));
            File.WriteAllText(Path.Combine(steam, "1111", "main.adofai"), "{\"angleData\":[0]}");
            File.WriteAllText(Path.Combine(steam, "2222", "main.adofai"), "{\"angleData\":[0,0]}");
            var maps = new MapLibrary(AppStorage.At(home));
            var source = new ReadOnlyDirectoryMapSource(steam);

            // Sync copies the levels; the Steam folder is only read.
            Equal(maps.SyncCustomAsync(source).GetAwaiter().GetResult(), 2);
            var copy = Path.Combine(maps.CustomRoot, "1111", "main.adofai");
            Assert(File.Exists(copy) && File.Exists(Path.Combine(maps.CustomRoot, "2222", "main.adofai")), "Both levels are copied");
            Equal(maps.SyncCustomAsync(source).GetAwaiter().GetResult(), 0); // Nothing changed, nothing copied again.

            var entry = new MapCacheEntry("a", Path.Combine(maps.CustomRoot, "1111"), "main.adofai", "One",
                MapParseStatus.Supported, null, [], "", "test");
            Equal(maps.KindOf(entry), MapKind.Custom);
            Equal(maps.KindOf(entry with { RootDirectory = Path.Combine(maps.BaseRoot, "x") }), MapKind.Base);
            Equal(maps.KindOf(entry with { RootDirectory = steam }), MapKind.External);
            var edited = entry with { RootDirectory = maps.EditedRoot, RelativePath = Path.Combine("1111", "2", "main.adofai") };
            Equal(maps.KindOf(edited), MapKind.Edited);
            Equal(maps.SlotOf(edited), (int?)2);
            Equal(maps.SlotOf(entry), (int?)null);

            // Unsubscribing a song: a rebuild is what takes it out of the custom folder.
            Directory.Delete(Path.Combine(steam, "2222"), recursive: true);
            // Reported straight through, since Progress<T> would post it and this list would still be empty below.
            var progress = new List<MapCopyProgress>();
            Equal(maps.RebuildCustomAsync(source, new Steps<MapCopyProgress>(progress.Add)).GetAwaiter().GetResult(), 1);
            // The copy is the first part of the percentage on screen: it counts up to the total it knew from the start.
            Assert(progress.Count > 0 && progress[^1].Copied == progress[^1].Total && progress[^1].Total > 0,
                "The copy reports its own progress: " + string.Join(" ", progress.Select(step => $"{step.Copied}/{step.Total}")));
            Assert(progress.Select(step => step.Copied).SequenceEqual(progress.Select(step => step.Copied).Order()), "Counting up");
            Assert(File.Exists(copy), "The subscribed song is still there");
            Assert(!Directory.Exists(Path.Combine(maps.CustomRoot, "2222")), "The unsubscribed song is gone");
            Assert(!Directory.Exists(maps.CustomRoot + ".new") && !Directory.Exists(maps.CustomRoot + ".old"), "No leftovers");
            Assert(File.Exists(Path.Combine(steam, "1111", "main.adofai")), "The Steam folder is untouched");

            // A failed rebuild keeps what was there: the half-built folder is dropped, nothing is swapped.
            Throws<OperationCanceledException>(() =>
            {
                using var stop = new CancellationTokenSource();
                stop.Cancel();
                maps.RebuildCustomAsync(source, null, stop.Token).GetAwaiter().GetResult();
            });
            Assert(File.Exists(copy) && !Directory.Exists(maps.CustomRoot + ".new"), "The old songs survive a cancel");

            // Remove deletes the app's own copy and the folder it leaves behind, and refuses anything else.
            maps.Remove(entry);
            Assert(!File.Exists(copy) && !Directory.Exists(Path.Combine(maps.CustomRoot, "1111")), "The copy and its folder go");
            Assert(Directory.Exists(maps.CustomRoot), "The custom folder itself stays");
            Throws<InvalidOperationException>(() => maps.Remove(entry with { RootDirectory = steam }));
            Assert(File.Exists(Path.Combine(steam, "1111", "main.adofai")), "A map folder song is never deleted");

            foreach (var folder in new[] { home, steam }) Directory.Delete(folder, recursive: true);
        }),

        ("The song box marks new, recently and forced songs until they are loaded; new is kept in the app store", () =>
        {
            // User request: besides unsupported, new songs, forced songs and edits just saved are marked in the song box.
            var model = new MainViewModel();
            var changes = 0;
            model.SongMarksChanged = () => changes++;
            var forced = Song("f", "Forced") with { ForcedFormatVersion = 2 };
            model.SetLibrary([Song("a", "Alpha"), Song("b", "Beta"), Song("c", "Gamma"), forced]);
            string? BadgeOf(string id) => model.Library.Single(entry => entry.Id == id).Badge;
            Assert(BadgeOf("a") is null && BadgeOf("f") == "forced" && model.Library.Single(entry => entry.Id == "f").IsForced, "Forced from the cache");
            model.MarkNew(["a", "b", "gone"]);
            Assert(BadgeOf("a") == "new" && BadgeOf("b") == "new" && changes == 1, "New after a sync; a song not in the library is not kept");
            Equal(model.NewSongs.Count, 2);
            model.MarkRecentEdit("b");
            Equal(BadgeOf("b"), "recently");
            // Forced wins over the others.
            model.MarkNew(["f"]);
            Equal(BadgeOf("f"), "forced");
            // Loading a song is looking at it: its marks go, and the new ones are saved again.
            model.LoadChosen(model.Library.Single(entry => entry.Id == "b"), null);
            Assert(BadgeOf("b") is null && !model.NewSongs.Contains("b") && changes == 3, "Loaded b");
            // The song loaded last is remembered for the next run (user rule).
            Equal(model.LastSongId, "b");
            model.SelectedMap = model.Library.Single(entry => entry.Id == "a");
            model.SongQuery = "Alpha";
            Assert(model.LoadSelectedSong() && BadgeOf("a") is null, "Loaded a");
            Equal(BadgeOf("f"), "forced");
            // The marks read at start come back as they were.
            Equal(model.LastSongId, "a");
            var fresh = new MainViewModel();
            fresh.SetLibrary([Song("a", "Alpha"), Song("c", "Gamma")]);
            fresh.RestoreMarks(["c"], "a");
            Assert(fresh.Library.Single(entry => entry.Id == "c").Badge == "new" && fresh.LastSongId == "a", "Marks read at start");
            // On screen: the mark sits after the title in the open list, orange for forced, and an unmarked song has none.
            var window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false };
            try
            {
                var shown = (MainViewModel)window.DataContext;
                window.Show();
                shown.SetLibrary([Song("a", "Alpha"), Song("n", "Newest"), forced]);
                shown.MarkNew(["n"]);
                var picker = (ComboBox)window.FindName("SongPicker");
                picker.IsDropDownOpen = true;
                var frame = new System.Windows.Threading.DispatcherFrame();
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                static IEnumerable<DependencyObject> Below(DependencyObject root)
                {
                    for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                    {
                        var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                        yield return child;
                        foreach (var nested in Below(child)) yield return nested;
                    }
                }
                TextBlock Mark(string id) => Below((ComboBoxItem)picker.ItemContainerGenerator.ContainerFromItem(shown.Library.Single(entry => entry.Id == id)))
                    .OfType<TextBlock>().Single(text => text.Name == "Badge");
                Assert(Mark("n") is { Text: "new", Visibility: Visibility.Visible }, "new is shown");
                Equal(Mark("a").Visibility, Visibility.Collapsed);
                var fire = ((System.Windows.Media.SolidColorBrush)window.FindResource("FireBrush")).Color;
                Assert(Mark("f") is { Text: "forced", Visibility: Visibility.Visible, Foreground: System.Windows.Media.SolidColorBrush { Color: var color } } && color == fire,
                    "forced in orange");
                picker.IsDropDownOpen = false;
            }
            finally { window.Close(); }

            // The store keeps them in song-marks.json, apart from the user's choices in songs.json.
            var home = Path.Combine(artifactRoot, "song-marks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            var book = new SongMarkBook(AppStorage.At(home));
            Assert(book.Read() is { New.Count: 0, Last: null }, "Nothing marked yet");
            book.Write(new SongMarks(new[] { "c", "a" }, "b"));
            Assert(book.Read() is { Last: "b" } kept && kept.New.SequenceEqual(["a", "c"])
                && File.Exists(Path.Combine(home, "song-marks.json")) && !File.Exists(Path.Combine(home, "songs.json")),
                "Kept in its own file");
            Directory.Delete(home, recursive: true);

            // The question the app asks when it starts: the song's name, a Details button and the facts the song list shows.
            var text = new UiText("korean");
            var asked = new ConfirmDialog(new VisualizerSettings(), text["LastSongTitle"], text.Format("LastSongQuestion", "Alpha"),
                text["Load"], text["Cancel"], details: SongDetails.Lines(Song("a", "Alpha", difficulty: 12, notes: 6, artist: "Someone"), text),
                detailsLabel: text["SongDetailsButton"]);
            Assert(asked.DetailsOpen == false && asked.DetailsText is { } lines && lines.Contains("Someone", StringComparison.Ordinal)
                && lines.Contains("12", StringComparison.Ordinal) && lines.Contains("6", StringComparison.Ordinal), "Folded details: " + asked.DetailsText);
            asked.DetailsOpen = true;
            Equal(asked.DetailsOpen, true);
            asked.Close();
        }),

        ("The song box loads the first song of a search, and a song picked in it keeps the searched list", () =>
        {
            // User request: a typed search used to load the song picked before it.
            var model = new MainViewModel();
            model.ApplySettings(model.Settings with { SongSortKey = nameof(SongSortField.Title), SongSortHighFirst = false });
            model.SetLibrary([Song("a", "Alpha"), Song("b", "Raimukun - C†hulhu"), Song("c", "2hello"), Song("d", "2hi")]);
            IReadOnlyList<MapCacheEntry> Shown() => model.LibraryView.Cast<MapCacheEntry>().ToArray();
            model.SelectedMap = model.Library.Single(entry => entry.Id == "a");
            model.SongQuery = "hulhu";
            Equal(Shown().Count, 1);
            Equal(model.SongToLoad!.Id, "b");
            Assert(model.LoadSelectedSong() && model.LoadedEntry!.Id == "b", "The only match loads, not the song picked before");
            // Several matches: the first of the searched list.
            model.SongQuery = "2h";
            Equal(model.SongToLoad!.Id, "c");
            // An arrow picks within the search: the box shows the picked title, and the list stays the searched one.
            var second = Shown()[1];
            model.SelectedMap = second;
            model.SongQuery = second.SongName;
            Equal(Shown().Count, 2);
            Equal(model.SongToLoad!.Id, "d");
            Assert(model.LoadSelectedSong() && model.LoadedEntry!.Id == "d", "The picked one loads");
            // Loaded: the box shows its title and the whole list again, and there is nothing new to load.
            Assert(model.SongQuery == "2hi" && Shown().Count == 4 && !model.LoadPending, "After loading");
            model.SongQuery = "zzz";
            Assert(model.SongToLoad is null && !model.LoadSelectedSong(), "Nothing matches, nothing loads");
        }),

        ("Loading a song puts the practice speed back to 100% unless it is kept, as a settings change of its own", () =>
        {
            // User request: the speed of the song before does not carry over, unless keep speed on load is on.
            var model = new MainViewModel();
            var saved = 0;
            model.SettingsCommitted += () => saved++;
            model.SetLibrary([Song("a", "Alpha"), Song("b", "Beta")]);
            model.ApplySettings(model.Settings with { PlaybackSpeedPercent = 80 });
            var steps = model.SettingsHistory.Count;
            saved = 0;
            Assert(model.LoadSelectedSong(), "Loads");
            Assert(model.Settings.PlaybackSpeedPercent == 100 && model.Playback.Settings.PlaybackSpeedPercent == 100, "Back to 100%");
            Assert(model.SettingsHistory.Count == steps + 1 && model.SettingsHistory[^1].Before.PlaybackSpeedPercent == 80 && saved == 1,
                "One change of its own, saved");
            model.RollBack();
            Equal(model.Settings.PlaybackSpeedPercent, 80d);
            // The arrows beside the song box load too.
            model.MoveSong(1);
            Equal(model.Settings.PlaybackSpeedPercent, 100d);
            // Kept when the setting says so, and for a song put back by a guide button.
            model.ApplySettings(model.Settings with { PlaybackSpeedPercent = 80, KeepSpeedOnLoad = true });
            model.MoveSong(1);
            Equal(model.Settings.PlaybackSpeedPercent, 80d);
            model.ApplySettings(model.Settings with { KeepSpeedOnLoad = false });
            model.LoadCached(model.Library[0]);
            Equal(model.Settings.PlaybackSpeedPercent, 80d);
            // The console's -sm and -sr win over the setting.
            model.LoadChosen(model.Library[1], keepSpeed: true);
            Equal(model.Settings.PlaybackSpeedPercent, 80d);
            model.ApplySettings(model.Settings with { KeepSpeedOnLoad = true });
            model.LoadChosen(model.Library[1], keepSpeed: false);
            Equal(model.Settings.PlaybackSpeedPercent, 100d);
            // At 100% already, loading records nothing.
            model.ApplySettings(model.Settings with { KeepSpeedOnLoad = false });
            var count = model.SettingsHistory.Count;
            model.LoadSelectedSong();
            Equal(model.SettingsHistory.Count, count);
            Assert(!new VisualizerSettings().KeepSpeedOnLoad, "Off by default");
        }),

        ("With every group shown, edits stay right under their original whatever the order", () =>
        {
            // User request: only originals are sorted; an edit is never above its original.
            var places = new Dictionary<string, (string Family, int? Slot, MapKind Kind)>
            {
                ["a"] = ("A", null, MapKind.Custom), ["b"] = ("B", null, MapKind.Custom), ["c"] = ("C", null, MapKind.Base),
                ["a2"] = ("A", 2, MapKind.Edited), ["a1"] = ("A", 1, MapKind.Edited), ["x1"] = ("X", 1, MapKind.Edited), ["x3"] = ("X", 3, MapKind.Edited)
            };
            var model = new MainViewModel
            {
                SongKind = entry => places[entry.Id].Kind,
                SongSlot = entry => places[entry.Id].Slot,
                SongFamily = entry => places[entry.Id].Family
            };
            model.ApplySettings(model.Settings with { SongSortKey = nameof(SongSortField.NoteCount), SongSortHighFirst = true, SongGroup = "All" });
            // The edits have more notes than any original, so a plain order would put them on top; x1 and x3 lost their original.
            model.SetLibrary([Song("a", "Alpha", notes: 5), Song("b", "Beta", notes: 9), Song("c", "Gamma", notes: 7),
                Song("a2", "Alpha", notes: 50), Song("a1", "Alpha", notes: 40), Song("x1", "Lost", notes: 60), Song("x3", "Lost", notes: 1)]);
            string Order() => string.Join(" ", model.Library.Select(entry => entry.Id));
            Equal(Order(), "x1 x3 b c a a1 a2");
            model.ApplySettings(model.Settings with { SongSortKey = nameof(SongSortField.Title), SongSortHighFirst = false });
            Equal(Order(), "x1 x3 a a1 a2 b c");
            // A hidden original leaves its edits where it would be.
            model.ApplySettings(model.Settings with { SongSortKey = nameof(SongSortField.NoteCount), SongSortHighFirst = true });
            model.SetHidden(model.AllSongs.Single(entry => entry.Id == "a"), true);
            Equal(Order(), "x1 x3 b c a1 a2");
            // Other groups sort as before.
            model.ApplySettings(model.Settings with { SongGroup = "Edited" });
            Equal(Order(), "x1 a2 a1 x3");
            model.ApplySettings(model.Settings with { SongGroup = "Custom" });
            Equal(Order(), "b");
            // Stepping with the arrows follows the same order.
            model.ApplySettings(model.Settings with { SongGroup = "All" });
            model.SetHidden(model.AllSongs.Single(entry => entry.Id == "a"), false);
            model.SelectedMap = model.Library.Single(entry => entry.Id == "a");
            model.SongQuery = model.SelectedMap.SongName;
            model.LoadSelectedSong();
            model.MoveSong(1);
            Equal(model.LoadedEntry!.Id, "a1");
        }),

        ("The song list window keeps edits under their original and drags them along with it", () =>
        {
            var places = new Dictionary<string, (string Family, int? Slot)>
            {
                ["a"] = ("A", null), ["b"] = ("B", null), ["a1"] = ("A", 1), ["a2"] = ("A", 2)
            };
            var settings = new VisualizerSettings { SongSortKey = nameof(SongSortField.Title), SongSortHighFirst = false };
            var window = new SongListWindow([Song("a1", "Alpha"), Song("b", "Beta"), Song("a2", "Alpha"), Song("a", "Alpha")], [], settings,
                null, null, entry => places[entry.Id]);
            string Order() => string.Join(" ", window.Collect().Preferences.OrderBy(song => song.Order).Select(song => song.Id));
            var handles = FindAll<Button>(window).Where(button => Equals(button.Content, "≡")).ToList();
            Assert(handles.Select(handle => handle.IsEnabled).SequenceEqual([true, false, false, true]), "Only originals have a handle to drag");
            Equal(handles[1].ToolTip, "수정본은 원곡과 함께 움직입니다. 원곡을 끌어 옮기세요.");
            // An edit is not dragged on its own; its original takes it along.
            Assert(!window.MoveRow(1, 4) && !window.MoveRow(2, 0), "An edit stays put");
            Assert(window.MoveRow(0, 4), "The original moves");
            Equal(Order(), "b a a1 a2");
            // A gap among the edits is the gap after them.
            Assert(window.MoveRow(0, 2), "Dropped among the edits");
            Equal(Order(), "a a1 a2 b");
            Assert(!window.MoveRow(0, 2) && !window.MoveRow(0, 3), "Into its own edits changes nothing");
        }),

        ("Short ids start with the folder digit, never repeat, and stay with the song", () =>
        {
            var dice = new Random(7);
            var songs = new[] { ("base-song", 0), ("custom-song", 1), ("edited-song", 2) };
            var ids = SongIds.Assign(songs, null, dice);
            Equal(ids.Count, 3);
            Assert(ids["base-song"][0] == '0' && ids["custom-song"][0] == '1' && ids["edited-song"][0] == '2', "First digit is the folder");
            // The folder digit, then seven lower-case letters and digits with at least one letter.
            Assert(ids.Values.All(id => id.Length == 8 && id[1..].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
                && id[1..].Any(char.IsAsciiLetterLower)), "Eight characters: " + string.Join(" ", ids.Values));
            Equal(ids.Values.Distinct().Count(), 3);

            // A song keeps its id the next time round, and a song that moved folders gets a new one.
            var again = SongIds.Assign(songs, ids, dice);
            Equal(again["custom-song"], ids["custom-song"]);
            var moved = SongIds.Assign([("custom-song", 2)], ids, dice);
            Assert(moved["custom-song"] != ids["custom-song"] && moved["custom-song"][0] == '2', "A moved song is renumbered");
            // Ids of songs that are gone are kept, so putting one back gives it the same id.
            Assert(moved.ContainsKey("base-song"), "Other songs keep their ids");
            Assert(SongIds.Matches(ids["base-song"], 0) && !SongIds.Matches(ids["base-song"], 1), "Matches checks the digit");
            // Ids of the old form (all digits) no longer match, so every song gets a new one the first time round.
            var old = new Dictionary<string, string> { ["base-song"] = "01234567", ["custom-song"] = "11111111" };
            var renewed = SongIds.Assign(songs, old, dice);
            Assert(renewed.Values.All(id => id[1..].Any(char.IsAsciiLetterLower)) && renewed["base-song"][0] == '0', "Old ids are replaced");
            Assert(!SongIds.Matches("01234567", 0) && SongIds.Matches("0abc1234", 0) && !SongIds.Matches("0ABC1234", 0), "The new form only");
            Assert(SongIds.LooksLikeId("12345678") && SongIds.LooksLikeId("2Ab3c4d5") && !SongIds.LooksLikeId("32345678") && !SongIds.LooksLikeId("1234567"),
                "What load tries as an id");
            // Many ids never repeat, and none reads as a number.
            var many = SongIds.Assign(Enumerable.Range(0, 3000).Select(i => ("song" + i, i % 3)), null, new Random(11));
            Assert(many.Values.Distinct().Count() == 3000 && many.Values.All(id => !id.All(char.IsAsciiDigit)), "Unique, with letters");
        }),

        ("The group list filters the song list and edited copies carry their slot", () =>
        {
            var model = new MainViewModel();
            var home = Path.Combine(@"C:\store");
            var maps = new MapLibrary(AppStorage.At(home));
            model.SongKind = maps.KindOf;
            model.SongSlot = maps.SlotOf;
            MapCacheEntry In(string id, string name, string root, string relative) =>
                Song(id, name) with { RootDirectory = root, RelativePath = relative };
            var baseSong = In("b", "Built in", Path.Combine(maps.BaseRoot, "1-1"), "main.adofai");
            var custom = In("c", "Plum - R", Path.Combine(maps.CustomRoot, "1234"), "main.adofai");
            var edited = In("e", "Plum - R", maps.EditedRoot, Path.Combine("1234", "2", "main.adofai"));
            model.SetLibrary([baseSong, custom, edited]);

            // Every folder is parsed; the group only decides what the list shows. Out of the box that is base and custom
            // (user rule), since an edit is picked by its slot on the main screen rather than as a song of its own.
            Equal(model.AllSongs.Count, 3);
            Equal(model.Library.Count, 2);
            Assert(model.Library.All(entry => entry.Id != "e"), "The edit is not listed by default");
            model.ApplySettings(model.Settings with { SongGroup = nameof(SongGroupField.All) });
            Equal(model.Library.Count, 3);
            Assert(model.Library.Any(entry => entry.SongName == "2*Plum - R"), "The edited copy carries its slot");
            Equal(model.DisplayName(custom), "Plum - R");
            // Loading the copy from the list names it once, never "2*2*Plum - R".
            model.SelectedMap = model.Library.Single(entry => entry.Id == "e");
            model.LoadSelectedSong();
            Equal(model.LoadedSongName, "2*Plum - R");
            Equal(model.DisplayName(model.LoadedEntry!), "2*Plum - R");

            void Group(SongGroupField group) => model.ApplySettings(model.Settings with { SongGroup = group.ToString() });
            Group(SongGroupField.Custom);
            Equal(model.Library.Count, 1);
            Equal(model.Library[0].Id, "c");
            Group(SongGroupField.Edited);
            Equal(model.Library.Single().SongName, "2*Plum - R");
            Group(SongGroupField.Base);
            Equal(model.Library.Single().Id, "b");
            // The pairs (user request): two folders at a time, with the edits still under their originals.
            string Listed() => string.Join(" ", model.Library.Select(entry => entry.Id).Order(StringComparer.Ordinal));
            Group(SongGroupField.BaseCustom);
            Equal(Listed(), "b c");
            Group(SongGroupField.CustomEdited);
            Equal(Listed(), "c e");
            Group(SongGroupField.BaseEdited);
            Equal(Listed(), "b e");
            Group(SongGroupField.All);
            Equal(model.Library.Count, 3);

            // A renamed edited copy keeps the slot in front of the new name.
            model.SongPreferences = [new SongPreference("e", DisplayName: "My mix")];
            Assert(model.Library.Any(entry => entry.SongName == "2*My mix"), "Slot then display name");

            // The short id finds the song it belongs to.
            model.SongIdMap = new Dictionary<string, string> { ["c"] = "10000001", ["e"] = "20000002" };
            Equal(model.SongWithId("20000002")?.Id, "e");
            Assert(model.SongWithId("99999999") is null && model.SongWithId("") is null, "An unknown id finds nothing");
        }),

        ("The slot picker holds the loaded song's versions and swaps between them on the spot", () =>
        {
            // User request: the edit is chosen by its slot on the field now, so the song list no longer has to carry it.
            var model = new MainViewModel();
            var maps = new MapLibrary(AppStorage.At(@"C:\store"));
            model.SongKind = maps.KindOf;
            model.SongSlot = maps.SlotOf;
            model.SongFamily = entry => maps.SlotOf(entry) is null
                ? Path.GetFileName(Path.GetDirectoryName(Path.Combine(entry.RootDirectory, entry.RelativePath)))!
                : maps.EditedSongOf(entry)!;
            MapCacheEntry In(string id, string root, string relative) =>
                Song(id, "Plum - R") with { RootDirectory = root, RelativePath = relative };
            var custom = In("c", Path.Combine(maps.CustomRoot, "1234"), "main.adofai");
            var third = In("e3", maps.EditedRoot, Path.Combine("1234", "3", "main.adofai"));
            var first = In("e1", maps.EditedRoot, Path.Combine("1234", "1", "main.adofai"));
            var other = In("z", Path.Combine(maps.CustomRoot, "9999"), "main.adofai");
            model.SetLibrary([custom, third, first, other]);
            model.SelectedMap = model.Library.Single(entry => entry.Id == "c");
            model.LoadSelectedSong();

            // The original and its edits, in slot order; another song's copies are not in it.
            Sequence([.. model.SlotOptions.Select(choice => choice.Number)], 0, 1, 3);
            Equal(model.SlotOptions[0].Label, "원본");
            Equal(model.SlotOptions[2].Label, "수정본 3");
            Assert(model.ShowSlotPicker && model.SelectedSlot == 0, "The original is what is loaded");

            // Picking one loads it at once, keeping the speed, and the bottom line says which version this is.
            model.ApplySettings(model.Settings with { PlaybackSpeedPercent = 75, KeepSpeedOnLoad = false });
            model.SelectedSlot = 3;
            Equal(model.LoadedEntry?.Id, "e3");
            Equal(model.Settings.PlaybackSpeedPercent, 75);
            Equal(model.SelectedSlot, 3);
            Assert(model.OverlayTileParts.Any(part => part.Text == "수정본 3" && part.Changed), "The slot is still shown: " + model.OverlayTiles);
            model.SelectedSlot = 0;
            Equal(model.LoadedEntry?.Id, "c");
            Assert(model.OverlayTileParts.All(part => part.Text != "수정본 3"), "The original says nothing about a slot");

            // A song with no edits still shows the picker, reading "원본" (user rule): the three controls keep one shape.
            model.SelectedMap = model.Library.Single(entry => entry.Id == "z");
            model.LoadSelectedSong();
            Assert(model.ShowSlotPicker && model.SlotOptions.Count == 1 && model.SlotOptions[0].Number == 0,
                "The only version is still offered");
        }),

        ("The song list window hides, renames and reorders without touching the cache", () =>
        {
            var songs = new[] { Song("a", "Beta", difficulty: 12), Song("b", "alpha", difficulty: 3) };
            var settings = new VisualizerSettings { SongSortKey = nameof(SongSortField.Title), SongSortHighFirst = false };
            var window = new SongListWindow(songs, [], settings);
            var buttons = FindAll<Button>(window).ToList();
            var hide = buttons.First(button => Equals(button.Content, "숨기기"));
            var rename = buttons.First(button => Equals(button.Content, "이름 변경"));

            hide.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            rename = FindAll<Button>(window).First(button => Equals(button.Content, "이름 변경"));
            rename.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var box = FindAll<TextBox>(window).Last(); // The search box comes first; the rename box is the one a row just opened.
            box.Text = "새 이름";
            // The reset hint sits beside Confirm, and the row keeps its height while a name is edited.
            Capture(window, Path.Combine(artifactRoot, "song-list-rename.png"), 860, 260);
            FindAll<Button>(window).Last(button => Equals(button.Content, "확인")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            var result = window.Collect();
            Assert(!result.Reordered, "Nothing was dragged");
            var first = result.Preferences.Single(song => song.Id == "b"); // "alpha" sorts first with lowest-first titles.
            Assert(first.Hidden == true, "The first row was hidden");
            Equal(first.DisplayName, "새 이름");
            // The cache entry itself is untouched: only the choices carry the new name.
            Equal(songs.Single(entry => entry.Id == "b").SongName, "alpha");

            // Confirming an empty box puts the original name back instead of storing a blank one.
            var second = new SongListWindow(songs, result.Preferences, settings);
            FindAll<Button>(second).First(button => Equals(button.Content, "이름 변경")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            FindAll<TextBox>(second).Last().Text = "   ";
            FindAll<Button>(second).Last(button => Equals(button.Content, "확인")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert(second.Collect().Preferences.Single(song => song.Id == "b").DisplayName is null, "An empty name resets it");
        }),
    };
}
