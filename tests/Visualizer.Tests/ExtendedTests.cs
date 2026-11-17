using System.IO;
using System.Windows.Interop;
using System.Windows.Input;
using Visualizer.App.Input;
using Visualizer.App.ViewModels;
using Visualizer.Application.Cache;
using Visualizer.Application.Input;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using Visualizer.Infrastructure.Cache;
using Visualizer.Infrastructure.Input;
using Visualizer.Infrastructure.Maps;
using Visualizer.Infrastructure.Parsing;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

internal static class ExtendedTests
{
    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }
    /// <summary>Collects progress as it is reported. Progress&lt;T&gt; would post it and the test would read an empty list.</summary>
    private sealed class Steps<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];
        public void Report(T value) => Items.Add(value);
    }
    private sealed class FixtureParser : IMapTimelineParser
    {
        public int Calls { get; private set; }
        public string Version => "synthetic-test-fixture-v1";
        public MapParseResult Parse(RawMapDocument document)
        {
            Calls++;
            return new(MapParseStatus.Supported,
                new ParsedTimeline(new[] { 1000d, 1030, 1060, 1090, 1120 },
                    new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 2) }, true),
                "Synthetic fixture", Array.Empty<string>());
        }
    }
    /// <summary>Counts the cache writes a sync makes and passes them on.</summary>
    private sealed class CountedWriter(WorkspaceMapCache cache) : IMapCacheWriter
    {
        public int Writes { get; private set; }
        public Task ReplaceAsync(IReadOnlyList<MapCacheEntry> entries, CancellationToken cancellationToken = default)
        { Writes++; return cache.ReplaceAsync(entries, cancellationToken); }
        public void ValidateMapRoot(string rootDirectory) => cache.ValidateMapRoot(rootDirectory);
    }
    private sealed class CountedSource(IReadOnlyMapDirectory source) : IReadOnlyMapDirectory
    {
        public int Reads { get; private set; }
        public string RootDirectory => source.RootDirectory;
        public Task<MapDiscovery> DiscoverAsync(CancellationToken cancellationToken = default) => source.DiscoverAsync(cancellationToken);
        public Task<RawMapDocument> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
        { Reads++; return source.ReadAsync(relativePath, cancellationToken); }
    }

    private static (string Repo, string Maps, WorkspaceMapCache Cache) Fixture(string artifactRoot)
    {
        var repo = Path.Combine(artifactRoot, "cache-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "AGENTS.md"), "Test-owned workspace fixture.");
        File.WriteAllText(Path.Combine(repo, "Visualizer.sln"), "Test-owned marker.");
        var maps = Path.Combine(repo, "maps");
        Directory.CreateDirectory(maps);
        File.WriteAllText(Path.Combine(maps, "fixture.adofai"), "synthetic test input, not ADOFAI timing rules");
        return (repo, maps, new WorkspaceMapCache(repo));
    }

    public static IEnumerable<(string Name, Action Run)> Create(string artifactRoot) => new (string, Action)[]
    {
        ("A map sync counts the levels it parses, which is what the percentage on screen is made of", () =>
        {
            var f = Fixture(artifactRoot);
            File.Delete(Path.Combine(f.Maps, "fixture.adofai"));
            for (var i = 1; i <= 3; i++)
                File.WriteAllText(Path.Combine(f.Maps, $"song{i}.adofai"),
                    "{\"settings\":{\"version\":11,\"bpm\":120},\"angleData\":[0,0,0],\"actions\":[]}");
            var steps = new Steps<MapScanProgress>();
            var report = new MapSyncService(f.Cache, f.Cache, new AdofaiTimelineParser())
                .SyncAsync(new ReadOnlyDirectoryMapSource(f.Maps), steps).GetAwaiter().GetResult();
            Equal(report.Entries.Count, 3);
            // An opening step with the total, then one per level: the screen turns these into a percentage, so a count
            // that stalls or a total that is not known yet would show a bar that never moves.
            Sequence(steps.Items.Select(step => step.Parsed), 0, 1, 2, 3);
            Assert(steps.Items.All(step => step.Total == 3), "The total is known from the start");
            Assert(steps.Items.Skip(1).All(step => step.Name.Length > 0), "Each parsed level is named");
            // Nothing to parse still reports, and with a total of zero: the screen must read that as a finished folder
            // rather than dividing by it.
            foreach (var level in Directory.GetFiles(f.Maps, "*.adofai")) File.Delete(level);
            var empty = new Steps<MapScanProgress>();
            Equal(new MapSyncService(f.Cache, f.Cache, new AdofaiTimelineParser())
                .SyncAsync(new ReadOnlyDirectoryMapSource(f.Maps), empty).GetAwaiter().GetResult().Entries.Count, 0);
            Assert(empty.Items.All(step => step.Total == 0), "An empty folder reports no total to count against");
        }),
        ("load -force keeps a forced song in the cache, and a sync keeps it forced while its file stays the same", () =>
        {
            var f = Fixture(artifactRoot);
            File.Delete(Path.Combine(f.Maps, "fixture.adofai"));
            var level = Path.Combine(f.Maps, "song.adofai");
            static string Level(string extra) => "{\"settings\":{\"version\":5,\"bpm\":120" + extra + "},\"angleData\":[0,0,0],\"actions\":[]}";
            File.WriteAllText(level, Level(""));
            var source = new ReadOnlyDirectoryMapSource(f.Maps);
            var sync = new MapSyncService(f.Cache, f.Cache, new AdofaiTimelineParser());
            MapCacheEntry Synced() => sync.SyncAsync(source).GetAwaiter().GetResult().Entries.Single();
            var song = Synced();
            Equal(song.Status, MapParseStatus.Unsupported);
            var report = sync.ForceAsync(source, song).GetAwaiter().GetResult();
            Equal(report.Result.ForcedFormatVersion, 4);
            // Kept in the cache file under the same id, so the song list and the next run have it.
            var kept = new WorkspaceMapCache(f.Repo).ReadAsync().GetAwaiter().GetResult().Single();
            Assert(kept.Id == song.Id && kept.Status == MapParseStatus.Supported && kept.IsForced && kept.ForcedFormatVersion == 4
                && kept.Timeline!.HitTimesMs.Count == 3, "The forced song is in the cache");
            // A sync reads it the forced way again while the file is the same...
            Equal(Synced().ForcedFormatVersion, 4);
            // ...and not once the file changed: forcing it again is the user's call.
            File.WriteAllText(level, Level(",\"offset\":10"));
            var changed = Synced();
            Assert(changed.Status == MapParseStatus.Unsupported && !changed.IsForced, "A changed file is not forced");
            // Only an unsupported song can be forced, and one no format reads leaves the cache as it was.
            Throws<ArgumentException>(() => sync.ForceAsync(source, kept).GetAwaiter().GetResult());
            File.WriteAllText(level, Level(",\"planetEase\":\"OutSine\""));
            var stuck = Synced();
            var failed = sync.ForceAsync(source, stuck).GetAwaiter().GetResult();
            Assert(failed.Result.Status == MapParseStatus.Unsupported && failed.Entries.Single() is { Status: MapParseStatus.Unsupported, IsForced: false } left
                && left.Id == stuck.Id, "Nothing reads it");
            Equal(new WorkspaceMapCache(f.Repo).ReadAsync().GetAwaiter().GetResult().Single().Status, MapParseStatus.Unsupported);
            Assert(File.ReadAllText(level) == Level(",\"planetEase\":\"OutSine\""), "The map is only read");
        }),
        ("Threshold availability follows Maximum 1/2/4/8", () =>
        {
            foreach (var maximum in new[] { 1, 2, 4, 8 })
            foreach (var upper in new[] { 2, 4, 8 })
            {
                var s = new VisualizerSettings { MaximumTileCount = maximum };
                Equal(s.IsThresholdEnabled(upper), maximum >= upper);
                Assert(!(s with { DynamicTiming = false }).IsThresholdEnabled(upper), "OFF must disable thresholds");
            }
            var floored = new VisualizerSettings { MinimumTileCount = 2 };
            Assert(!floored.IsThresholdEnabled(2) && floored.IsThresholdEnabled(4), "The minimum disables thresholds below it");
            var two = new VisualizerSettings { MaximumTileCount = 2, TwoKeyBoundaryMs = 10 };
            two.EnsureValid();
            Equal(new DynamicTimingSelector(two).Select(9), 2);
            Equal(new DynamicTimingSelector(two).Select(10), 1);
            Throws<ArgumentException>(() => (two with { MaximumTileCount = 4 }).EnsureValid());
        }),
        ("New controls, optional Settings key and offset validation", () =>
        {
            var s = new VisualizerSettings();
            Equal(s.VisualizerToggleKey, "F8"); Equal(s.SettingsHotkey, null);
            Assert(s.OnlyReactWhenAdofaiForeground, "Global input scope defaults on (ADOFAI or the visualizer, app 0.1.2 user request)");
            Equal(s.GlobalDisplayOffsetMs, 0d);
            (s with { GlobalDisplayOffsetMs = -25.5 }).EnsureValid();
            Throws<ArgumentException>(() => (s with { GlobalDisplayOffsetMs = double.NaN }).EnsureValid());
            Throws<ArgumentException>(() => (s with { VisualizerToggleKey = "J" }).EnsureValid());
            Throws<ArgumentException>(() => ControlBindings.Create(s with { ResetKey = "Return", StartTriggerKey = "Enter" }));
            var bindings = ControlBindings.Create(s with { SettingsHotkey = "F7" });
            Equal(bindings[KeyInterop.VirtualKeyFromKey(Key.F7)], InputAction.OpenSettings);
        }),
        ("Reset places first note at judgment, later notes prefilled", () =>
        {
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            session.LoadTimeline(new[] { 1000d, 2500, 4000, 7000 }, new() { DynamicTiming = false });
            session.Toggle();
            Equal(session.Notes[0].HitTimeMs, 0d); Equal(session.Notes[0].Tile, 5);
            var positions = session.Notes.Select(n => NoteLayout.Progress(n.HitTimeMs, session.TimelineTimeMs, 3000, session.Offsets)).ToArray();
            Equal(positions[0], 1d); Equal(positions[1], 0.5d); Equal(positions[2], 0d);
            Assert(positions[3] < 0, "Far future note must be above spawn");
            clock.NowMs = 9999; session.Start();
            Assert(positions.SequenceEqual(session.Notes.Select(n => NoteLayout.Progress(n.HitTimeMs, session.TimelineTimeMs, 3000, session.Offsets))), "Start cannot reposition");
        }),
        ("OFF stops playback and ON prepares without autostart", () =>
        {
            var clock = new Clock();
            var p = new PlaybackSession(clock);
            Equal(p.State, PlaybackState.Off); Assert(!p.Start(), "OFF cannot start");
            p.Toggle(); Equal(p.State, PlaybackState.NoTimeline);
            p.LoadTimeline(new[] { 0d, 100 }, new());
            Equal(p.State, PlaybackState.Waiting); p.Start(); clock.NowMs = 50;
            p.Toggle(); Equal(p.State, PlaybackState.Off); Assert(!p.Start(), "OFF must ignore start");
            clock.NowMs = 5000; p.Toggle();
            Equal(p.State, PlaybackState.Waiting); Equal(p.TimelineTimeMs, 0d);
        }),
        ("Visual updates preserve note list and elapsed time", () =>
        {
            var clock = new Clock(); var p = new PlaybackSession(clock);
            var s = new VisualizerSettings();
            p.LoadTimeline(new[] { 0d, 1000 }, s); p.Toggle(); p.Start(); clock.NowMs = 500;
            var notes = p.Notes;
            p.ApplySettings(s with { NoteTravelTimeMs = 2000, NoteThickness = 1.25, GlobalDisplayOffsetMs = 50 });
            Assert(ReferenceEquals(notes, p.Notes), "Visual edit must not regenerate");
            Equal(p.State, PlaybackState.Playing); Equal(p.TimelineTimeMs, 500d);
            Equal(NoteLayout.ThicknessDip(1.25), NoteLayout.BaseThicknessDip * 1.25);
            Equal(NoteLayout.Progress(1000, 1050, 2000, p.Offsets), 1d);
            p.SetMapDisplayOffset(-25);
            Equal(NoteLayout.Progress(1000, 1025, 2000, p.Offsets), 1d);
            Equal(p.Timeline!.HitTimesMs[1], 1000d);
            p.ApplySettings(p.Settings with { DynamicTiming = false, MaximumTileCount = 2 });
            Equal(p.State, PlaybackState.Waiting); Assert(!ReferenceEquals(notes, p.Notes), "Pattern edits regenerate notes");
        }),
        ("Verified checkpoint reset normalizes time and Initial Tile", () =>
        {
            var times = new[] { 1000d, 1100, 1200, 1300, 1400 };
            var timeline = new ParsedTimeline(times, new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 2) }, true);
            times[0] = 99; Equal(timeline.HitTimesMs[0], 1000d);
            var clock = new Clock(); var p = new PlaybackSession(clock);
            p.LoadParsedTimeline(timeline, new() { DynamicTiming = false }); p.Toggle(); p.Start();
            clock.NowMs = 1200; Equal(p.ActiveCheckpoint, (int?)2);
            p.Reset(p.Settings, 2);
            // Sliced from a full play (5 4 3 6 5), not re-patterned from the Initial Tile (user rule).
            Equal(p.Notes[0].HitTimeMs, 0d); Equal(p.Notes[1].HitTimeMs, 100d); Equal(p.Notes[0].Tile, 3);
            Equal(p.LaneStartTile, (int?)3);
            Equal(p.Notes.Count, 3); Equal(p.State, PlaybackState.Waiting);
            var before = p.Notes; p.Start(); Assert(ReferenceEquals(before, p.Notes), "Checkpoint Start must preserve notes");
            Throws<ArgumentOutOfRangeException>(() => timeline.GetStartIndex(3));
            Throws<InvalidOperationException>(() => new ParsedTimeline(new[] { 0d }).GetStartIndex(2));
        }),
        ("Cache roundtrip and Load/settings do not reread source", () =>
        {
            var f = Fixture(artifactRoot);
            Assert(!Directory.Exists(f.Cache.StorageDirectory), "Cache constructor must not write");
            Equal(f.Cache.ReadAsync().GetAwaiter().GetResult().Count, 0);
            var source = new CountedSource(new ReadOnlyDirectoryMapSource(f.Maps));
            var parser = new FixtureParser();
            var before = File.ReadAllBytes(Path.Combine(f.Maps, "fixture.adofai"));
            var modified = File.GetLastWriteTimeUtc(Path.Combine(f.Maps, "fixture.adofai"));
            new MapSyncService(f.Cache, f.Cache, parser).SyncAsync(source).GetAwaiter().GetResult();
            var freshCache = new WorkspaceMapCache(f.Repo);
            var entries = freshCache.ReadAsync().GetAwaiter().GetResult();
            Equal(entries.Count, 1); Equal(entries[0].Timeline!.HitTimesMs[0], 1000d);
            Equal(entries[0].Timeline!.Checkpoints.Count, 2);
            var model = new MainViewModel { SelectedMap = entries[0] };
            model.LoadSelectedSong(); // Loading switches the visualizer on.
            var cached = model.Playback.Timeline;
            model.ApplySettings(model.Settings with { MaximumTileCount = 2, DynamicTiming = false });
            Assert(ReferenceEquals(cached, model.Playback.Timeline), "Settings must retain cached timeline");
            Equal(source.Reads, 1); Equal(parser.Calls, 1);
            Assert(before.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Maps, "fixture.adofai"))), "Map bytes changed");
            Equal(File.GetLastWriteTimeUtc(Path.Combine(f.Maps, "fixture.adofai")), modified);
            Equal(Directory.GetFiles(f.Maps).Length, 1);
            var json = File.ReadAllText(Path.Combine(f.Cache.StorageDirectory, "library.json"));
            Assert(!json.Contains("InitialTile") && !json.Contains("Pattern") && !json.Contains("GlobalDisplayOffset"), "Visual settings leaked into parsed cache");
        }),
        ("Unknown checkpoint Reset stops instead of guessing", () =>
        {
            var clock = new Clock(); var p = new PlaybackSession(clock);
            p.LoadTimeline(new[] { 0d, 100, 200 }, new()); p.Toggle(); p.Start();
            clock.NowMs = 50;
            Throws<InvalidOperationException>(() => p.Reset(p.Settings, 3));
            Equal(p.State, PlaybackState.Waiting); Equal(p.TimelineTimeMs, 0d);
            Equal(p.Notes[0].HitTimeMs, 0d);
        }),
        ("Unsupported maps cannot acquire synthetic timing", () =>
        {
            var f = Fixture(artifactRoot);
            var report = new MapSyncService(f.Cache, f.Cache, new UnsupportedMapTimelineParser())
                .SyncAsync(new ReadOnlyDirectoryMapSource(f.Maps)).GetAwaiter().GetResult();
            var entry = report.Entries.Single();
            Equal(entry.Status, MapParseStatus.Unsupported); Equal(entry.Timeline, null);
            Assert(entry.Warnings.Count > 0, "Unsupported must be explained");
            var model = new MainViewModel(); model.LoadPreview(); model.Start();
            model.SelectedMap = entry; model.LoadSelectedSong(); model.Start();
            Equal(model.Playback.State, PlaybackState.NoTimeline); Equal(model.Playback.Notes.Count, 0);
        }),
        ("Cache rejects overlap, corruption and external repository", () =>
        {
            var f = Fixture(artifactRoot);
            Throws<IOException>(() => f.Cache.ValidateMapRoot(f.Repo));
            Throws<IOException>(() => f.Cache.ValidateMapRoot(f.Cache.StorageDirectory));
            Throws<IOException>(() => f.Cache.ValidateMapRoot(Path.Combine(f.Cache.StorageDirectory, "maps")));
            Throws<ArgumentException>(() => new WorkspaceMapCache(f.Maps));
            new MapSyncService(f.Cache, f.Cache, new FixtureParser()).SyncAsync(new ReadOnlyDirectoryMapSource(f.Maps)).GetAwaiter().GetResult();
            var path = Path.Combine(f.Cache.StorageDirectory, "library.json");
            File.WriteAllText(path, "{ broken fixture");
            Throws<System.Text.Json.JsonException>(() => new MapSyncService(f.Cache, f.Cache, new FixtureParser())
                .SyncAsync(new ReadOnlyDirectoryMapSource(f.Maps)).GetAwaiter().GetResult());
            Equal(File.ReadAllText(path), "{ broken fixture");
        }),
        ("Manual sync is recursive and repeated sync is deduplicated", () =>
        {
            var f = Fixture(artifactRoot);
            var nested = Path.Combine(f.Maps, "nested"); Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "second.ADOFAI"), "synthetic");
            File.WriteAllText(Path.Combine(nested, "ignored.txt"), "not a map");
            var source = new ReadOnlyDirectoryMapSource(f.Maps);
            var sync = new MapSyncService(f.Cache, f.Cache, new FixtureParser());
            Equal(sync.SyncAsync(source).GetAwaiter().GetResult().Entries.Count, 2);
            Equal(sync.SyncAsync(source).GetAwaiter().GetResult().Entries.Count, 2);
            Equal(Directory.GetFiles(nested).Length, 2);
        }),
        ("The parser keeps each hit's tempo and margin scale for the judgment bar, and the cache keeps them too", () =>
        {
            // The game grades a hit with the speed of the tile the planet turns from (planetarySystem.speed) and the margin
            // scale of the tile it lands on (scrPlayer.get__marginScale); ScaleMargin sets "scale" / 100 and carries on like
            // the speed (scnGame.ApplyEventsToFloors). The song's pitch makes every tempo real time.
            static MapParseResult Parse(string settings, string actions) => new AdofaiTimelineParser().Parse(
                new("windows.adofai", "{\"settings\":{\"version\":13,\"bpm\":120" + settings + "},\"angleData\":[0,0,0,0,0],\"actions\":" + actions + "}"));
            var plain = Parse("", "[]").Timeline!;
            Sequence(plain.HitTempos!.Select(t => (int)t), 120, 120, 120, 120, 120);
            Equal(plain.HitMarginScales, null);
            var changed = Parse(",\"pitch\":200", """
                [{"floor":1,"eventType":"SetSpeed","speedType":"Bpm","beatsPerMinute":300},
                 {"floor":2,"eventType":"ScaleMargin","scale":50},
                 {"floor":4,"eventType":"ScaleMargin","scale":150,"active":false},
                 {"floor":5,"eventType":"ScaleMargin","scale":80}]
                """).Timeline!;
            // Hit 1 is reached from tile 0 (120), the rest from tile 1 on (300); all twice as fast at pitch 200.
            Sequence(changed.HitTempos!.Select(t => (int)t), 240, 600, 600, 600, 600);
            // Tiles 2 to 4 have half the margin; a switched-off event changes nothing; the end tile's own event counts.
            Sequence(changed.HitMarginScales!.Select(s => (int)Math.Round(s * 100)), 100, 50, 50, 50, 80);
            Assert(changed.WindowsOf(1, 1).PureMs == 25 && Math.Abs(changed.WindowsOf(0, 1).PureMs - 30 * 60000 / 240.0 / 180) < 1e-9,
                "The windows follow the tempo");
            // Kept in the cache file, and an older file without them still reads.
            var f = Fixture(artifactRoot);
            var entry = new MapCacheEntry("windows", f.Maps, "windows.adofai", "Windows", MapParseStatus.Supported, changed,
                Array.Empty<string>(), "", AdofaiTimelineParser.CurrentVersion);
            f.Cache.ReplaceAsync([entry]).GetAwaiter().GetResult();
            var read = new WorkspaceMapCache(f.Repo).ReadAsync().GetAwaiter().GetResult().Single().Timeline!;
            Assert(read.HitTempos!.SequenceEqual(changed.HitTempos!) && read.HitMarginScales!.SequenceEqual(changed.HitMarginScales!),
                "The tempos and scales come back");
            var file = Path.Combine(f.Cache.StorageDirectory, "library.json");
            var text = File.ReadAllText(file);
            Assert(text.Contains("\"HitTempos\"", StringComparison.Ordinal), "Written as HitTempos: " + text[..Math.Min(200, text.Length)]);
            File.WriteAllText(file, System.Text.RegularExpressions.Regex.Replace(text, ",\\s*\"Hit(Tempos|MarginScales)\":\\s*(null|\\[[^\\]]*\\])", ""));
            var old = new WorkspaceMapCache(f.Repo).ReadAsync().GetAwaiter().GetResult().Single().Timeline!;
            Assert(old.HitTempos is null && old.HitMarginScales is null, "An older cache reads without them");
        }),
        ("A sync parses only the levels that came in or changed, and the rebuild parses every one again", () =>
        {
            // User report: sync took about as long as sync rebuild. Both parsed every level of all three folders, which is
            // the slow part; a sync is for the songs that came in (user rule).
            var f = Fixture(artifactRoot);
            var source = new ReadOnlyDirectoryMapSource(f.Maps);
            var parser = new FixtureParser();
            var writer = new CountedWriter(f.Cache);
            var sync = new MapSyncService(f.Cache, writer, parser);
            MapSyncReport Run(bool reparse = false) => sync.SyncAsync(source, reparse: reparse).GetAwaiter().GetResult();
            var first = Run().Entries.Single();
            Equal(parser.Calls, 1);
            Equal(writer.Writes, 1);
            // Nothing new: nothing is parsed and the cache file is not written again, and the song is as it was.
            Run();
            var again = Run().Entries.Single();
            Equal(parser.Calls, 1);
            Equal(writer.Writes, 1);
            Assert(again.Id == first.Id && again.SourceHash == first.SourceHash && again.Timeline!.HitTimesMs.SequenceEqual(first.Timeline!.HitTimesMs),
                "The kept entry is the synced one");
            // A level that came in is parsed, and only it.
            File.WriteAllText(Path.Combine(f.Maps, "second.adofai"), "synthetic second level");
            Equal(Run().Entries.Count, 2);
            Equal(parser.Calls, 2);
            Equal(writer.Writes, 2);
            // A level whose text changed is parsed again.
            File.WriteAllText(Path.Combine(f.Maps, "fixture.adofai"), "synthetic test input, changed");
            Run();
            Equal(parser.Calls, 3);
            // So is one an older parser read, since its times may not be this parser's.
            var cached = f.Cache.ReadAsync().GetAwaiter().GetResult();
            f.Cache.ReplaceAsync(cached.Select(entry => entry.RelativePath == "second.adofai" ? entry with { ParserVersion = "older" } : entry).ToArray())
                .GetAwaiter().GetResult();
            Run();
            Equal(parser.Calls, 4);
            // The rebuild reads every level again.
            Run(reparse: true);
            Equal(parser.Calls, 6);
        }),
        ("Editor backup.adofai beside a level is not listed as a second song", () =>
        {
            var paths = new[] { "a\\backup.adofai", "a\\main.adofai", "b\\BACKUP.adofai", "c\\backup2.adofai", "c\\level.adofai", "d\\.hidden.adofai", "d\\backup.adofai" };
            // Only-backup folders stay (the game's installer falls back to them); dot files do not shadow a backup.
            Assert(MapFileRules.SelectLevels(paths).SequenceEqual(new[] { "a\\main.adofai", "b\\BACKUP.adofai", "c\\level.adofai", "d\\.hidden.adofai", "d\\backup.adofai" }),
                "Wrong level selection: " + string.Join(" ", MapFileRules.SelectLevels(paths)));
            var f = Fixture(artifactRoot);
            var mega = Path.Combine(f.Maps, "2411672670"); Directory.CreateDirectory(mega);
            File.WriteAllText(Path.Combine(mega, "main.adofai"), "synthetic level");
            File.WriteAllText(Path.Combine(mega, "backup.adofai"), "synthetic editor auto-save");
            var source = new ReadOnlyDirectoryMapSource(f.Maps);
            // A cache synced before this rule still holds the backup entry.
            var stale = new MapCacheEntry("stale-backup", source.RootDirectory, Path.Combine("2411672670", "backup.adofai"), "Synthetic fixture",
                MapParseStatus.Unsupported, null, new[] { "old sync" }, "", "old");
            var level = new MapCacheEntry("level", source.RootDirectory, Path.Combine("2411672670", "main.adofai"), "Synthetic fixture",
                MapParseStatus.Unsupported, null, new[] { "old sync" }, "", "old");
            f.Cache.ReplaceAsync(new[] { stale, level }).GetAwaiter().GetResult();
            var model = new MainViewModel();
            model.RefreshLibraryAsync(f.Cache).GetAwaiter().GetResult();
            Assert(model.Library.Select(e => e.Id).SequenceEqual(new[] { "level" }), "The stale backup entry is hidden without a re-sync");
            var report = new MapSyncService(f.Cache, f.Cache, new FixtureParser()).SyncAsync(source).GetAwaiter().GetResult();
            Equal(report.Entries.Count, 2); // fixture.adofai and the level; the backup is neither parsed nor cached.
            Assert(report.Entries.All(e => !MapFileRules.IsEditorBackupName(e.RelativePath)), "Sync removes the cached backup entry");
            Equal(Directory.GetFiles(mega).Length, 2);
        }),
        ("Three planets make every tile of the section shorter, and the count carries on like the speed", () =>
        {
            // cosMo@暴走P - THE END OF HATSUNE MIKU holds three planets over floors 310-324. The game adds
            // scrMisc.GetInverseAnglePerBeatMultiplanet(n) = pi(n-2)/n to the entry angle before measuring the turn
            // (scrLevelMaker.CalculateFloorEntryTimes), so the next planet is that much nearer: nothing for two planets,
            // 60 degrees for three. The game's own tile times for that map agree in both directions (its 180 degree
            // tiles take 120, read over the sync pipe). PlanetCount is { TwoPlanets = 2, ThreePlanets = 3 }.
            static MapParseResult Parse(string actions) => new AdofaiTimelineParser().Parse(
                new("planets.adofai", "{\"settings\":{\"version\":13,\"bpm\":120},\"angleData\":[0,0,0,0],\"actions\":" + actions + "}"));

            var plain = Parse("[]");
            Assert(plain.Status == MapParseStatus.Supported, string.Join(" ", plain.Warnings));
            Sequence([.. plain.Timeline!.HitTimesMs.Select(t => (int)Math.Round(t))], 500, 1000, 1500, 2000);

            // Three planets from floor 1: every turn from there is 120 degrees instead of 180, so two thirds of a beat.
            var three = Parse("[{\"floor\":1,\"eventType\":\"MultiPlanet\",\"planets\":\"ThreePlanets\"}]");
            Assert(three.Status == MapParseStatus.Supported, string.Join(" ", three.Warnings));
            var times = three.Timeline!.HitTimesMs;
            Equal((int)Math.Round(times[0]), 500);
            foreach (var gap in new[] { times[1] - times[0], times[2] - times[1], times[3] - times[2] })
                Assert(Math.Abs(gap - 500 * 120.0 / 180) < 0.01, "Each tile of the section is a third shorter: " + gap);

            // Counterclockwise alike: a twirl first, and the 180 degree tiles still take 120.
            var twirled = Parse("[{\"floor\":1,\"eventType\":\"Twirl\"},{\"floor\":1,\"eventType\":\"MultiPlanet\",\"planets\":\"ThreePlanets\"}]");
            var turned = twirled.Timeline!.HitTimesMs;
            Assert(Math.Abs((turned[2] - turned[1]) - 500 * 120.0 / 180) < 0.01, "The other way round is shorter too: " + (turned[2] - turned[1]));

            // Back to two planets, and the floors after it are plain again.
            var back = Parse("[{\"floor\":1,\"eventType\":\"MultiPlanet\",\"planets\":\"ThreePlanets\"},"
                + "{\"floor\":2,\"eventType\":\"MultiPlanet\",\"planets\":\"TwoPlanets\"}]");
            Assert(back.Status == MapParseStatus.Supported, string.Join(" ", back.Warnings));
            var two = back.Timeline!.HitTimesMs;
            Assert(Math.Abs((two[1] - two[0]) - 500 * 120.0 / 180) < 0.01, "Only floor 1 is shorter: " + (two[1] - two[0]));
            Assert(Math.Abs((two[2] - two[1]) - 500) < 0.01, "and floor 2 is back to a beat: " + (two[2] - two[1]));

            // Anything else the file might say is refused rather than guessed at.
            Equal(Parse("[{\"floor\":1,\"eventType\":\"MultiPlanet\",\"planets\":\"FourPlanets\"}]").Status, MapParseStatus.Unsupported);
        }),

        ("Input filter observes foreground without blocking toggle", () =>
        {
            var s = new VisualizerSettings { OnlyReactWhenAdofaiForeground = true };
            Assert(!InputPolicy.ShouldReact(InputAction.Start, s, false, false), "Background app must not start");
            Assert(InputPolicy.ShouldReact(InputAction.Start, s, true, false), "ADOFAI foreground must start");
            Assert(InputPolicy.ShouldReact(InputAction.Toggle, s, false, false), "Toggle must work globally");
            Assert(!InputPolicy.ShouldReact(InputAction.Reset, s, true, true), "Editing must not reset");
            // The scope is on by default: reset waits for ADOFAI or the visualizer, unless it is switched off.
            Assert(!InputPolicy.ShouldReact(InputAction.Reset, new VisualizerSettings(), false, false), "The default gate keeps reset to the focused apps");
            Assert(InputPolicy.ShouldReact(InputAction.Reset, new VisualizerSettings { OnlyReactWhenAdofaiForeground = false }, false, false),
                "With the scope off, reset reacts in any app");
        }),
        ("Raw key repeat suppression and device removal", () =>
        {
            var tracker = new KeyPressTracker(); tracker.SetObservedKeys(new[] { 74, 13, 119 });
            Assert(!tracker.Observe(1, 65, false), "Unconfigured key must be discarded");
            Assert(tracker.Observe(1, 74, false), "First down");
            Assert(!tracker.Observe(1, 74, false), "Repeat suppressed");
            Assert(!tracker.Observe(1, 74, true), "Up never triggers");
            Assert(tracker.Observe(1, 74, false), "New down");
            tracker.RemoveDevice(1); Assert(tracker.Observe(1, 74, false), "Removed devices cannot leave stuck keys");
        }),
        ("Native Raw Input registration and cleanup (no key injection)", () =>
        {
            using var window = new HwndSource(new HwndSourceParameters("Visualizer input registration test")
                { Width = 1, Height = 1, WindowStyle = 0, ParentWindow = new nint(-3) });
            using var monitor = new WindowsRawInputMonitor();
            monitor.SetObservedKeys(new[] { 74, 13, 119 });
            monitor.Start(window.Handle);
            monitor.ObserveWindowMessage(0, 0);
            monitor.Dispose(); // also verify repeated Dispose.
        })
    };
}
