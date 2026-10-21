using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Visualizer.App.Rendering;
using Visualizer.App.ViewModels;
using Visualizer.App.Views;
using Visualizer.Application.Cache;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using Visualizer.Infrastructure.Maps;
using Visualizer.Infrastructure.Parsing;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

internal static class MapCompatibilityTests
{
    private static MapParseResult Parse(int version = 18, string route = "\"angleData\":[0,0,0]", string actions = "[]", string extra = "") =>
        new AdofaiTimelineParser().Parse(new("test.adofai", $"{{\"settings\":{{\"version\":{version},\"bpm\":120{extra}}},{route},\"actions\":{actions}}}"));
    private static void Times(MapParseResult result, params double[] expected)
    {
        Assert(result.Status == MapParseStatus.Supported, string.Join(" ", result.Warnings));
        Equal(result.Timeline!.HitTimesMs.Count, expected.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert(Math.Abs(result.Timeline.HitTimesMs[i] - expected[i]) < 0.000001, $"Unexpected hit time at {i}");
    }

    public static IEnumerable<(string Name, Action Run)> Create(string artifactRoot, string? readOnlyMapRoot, bool compareGameRules = false)
    {
        yield return ("Wake Up Piano format 6 loads and prepares every note without force", () =>
        {
            using var stream = typeof(MapCompatibilityTests).Assembly.GetManifestResourceStream("Visualizer.Tests.Fixtures.WakeUpPiano.json")!;
            using var reader = new StreamReader(stream);
            var result = new AdofaiTimelineParser().Parse(new("WakeUpPiano.adofai", reader.ReadToEnd()));
            Assert(result.Status == MapParseStatus.Supported, string.Join(" ", result.Warnings));
            Equal(result.ForcedFormatVersion, null);
            Equal(result.SongName, "Plum - Wake Up, Piano!");
            var timeline = result.Timeline!;
            Equal(timeline.HitTimesMs.Count, 959);
            var session = new PlaybackSession(new Clock());
            session.LoadParsedTimeline(timeline, new VisualizerSettings());
            session.Toggle();
            Equal(session.Notes.Count, timeline.CountPressedHits());
            Assert(session.Notes.Count > 0, "The song is ready to play");
            // Format support must not relax the planet easing or unknown-event checks.
            Equal(Parse(6, extra: ",\"planetEase\":\"OutSine\"").Status, MapParseStatus.Unsupported);
            Equal(Parse(6, actions: "[{\"floor\":1,\"eventType\":\"UnknownTiming\"}]").Status, MapParseStatus.Unsupported);
        });
        yield return ("Version compatibility preserves timing and rejects unknown versions", () =>
        {
            foreach (var version in new[] { 2, 3, 4, 6, 8, 10, 11, 13, 15, 18, 19 }) Times(Parse(version), 500, 1000, 1500);
            foreach (var version in new[] { 0, 1, 5, 20, 999 }) Equal(Parse(version).Status, MapParseStatus.Unsupported);
        });
        yield return ("load -force reads an unverified format as the nearest verified one below, else above, and nothing else", () =>
        {
            static MapParseResult Forced(int version, string extra = "") => new AdofaiTimelineParser().ParseForced(
                new("test.adofai", $"{{\"settings\":{{\"version\":{version},\"bpm\":120{extra}}},\"angleData\":[0,0,0],\"actions\":[]}}"));
            // 3 to 2 is the user's example; 3 is verified now, so 5 goes to 4, 1 has none below and goes to 2, 20 goes to 19.
            foreach (var (own, read) in new[] { (5, 4), (1, 2), (20, 19), (999, 19) })
            {
                var forced = Forced(own);
                Times(forced, 500, 1000, 1500);
                Equal(forced.ForcedFormatVersion, read);
                Assert(forced.Warnings[0].StartsWith("강제 로드:", StringComparison.Ordinal), "The forced read says so first");
            }
            // A verified format needs no force, and anything held back by more than its format fails every format.
            Equal(Forced(18).ForcedFormatVersion, null);
            Times(Forced(18), 500, 1000, 1500);
            var stuck = Forced(5, ",\"planetEase\":\"OutSine\"");
            Assert(stuck.Status == MapParseStatus.Unsupported && stuck.ForcedFormatVersion is null && stuck.Timeline is null,
                "An unverified planet easing stays unsupported");
            Assert(stuck.Warnings[^1].StartsWith("강제 로드 실패:", StringComparison.Ordinal), "It says every format failed");
            Equal(new AdofaiTimelineParser().ParseForced(new("broken.adofai", "{\"settings\":")).Status, MapParseStatus.Error);
        });
        yield return ("Events switched off in the editor (active false) are left out of timing, as the game leaves them out", () =>
        {
            const string Route = "\"angleData\":[0,0,0,0]";
            static string Speed(string active) => $"[{{\"floor\":2,\"eventType\":\"SetSpeed\",\"speedType\":\"Bpm\",\"beatsPerMinute\":60{active}}}]";
            Times(Parse(18, Route, Speed("")), 500, 1000, 2000, 3000);
            Times(Parse(18, Route, Speed(",\"active\":true")), 500, 1000, 2000, 3000);
            // いますぐ輪廻 floor 529: a SetSpeed switched off in the editor is not played.
            Times(Parse(18, Route, Speed(",\"active\":false")), 500, 1000, 1500, 2000);
            // LevelEvent.Decode switches an event off only for a JSON false.
            Times(Parse(18, Route, Speed(",\"active\":\"false\"")), 500, 1000, 2000, 3000);
            Times(Parse(18, Route, Speed(",\"active\":0")), 500, 1000, 2000, 3000);
            // Switched off, even an unsupported event and a checkpoint are not there.
            Times(Parse(18, Route, "[{\"floor\":2,\"eventType\":\"FreeRoam\",\"duration\":8,\"active\":false}]"), 500, 1000, 1500, 2000);
            Equal(Parse(18, Route, "[{\"floor\":2,\"eventType\":\"Checkpoint\",\"active\":false}]").Timeline!.CheckpointCount, 0);
            Equal(Parse(18, Route, "[{\"floor\":2,\"eventType\":\"Checkpoint\"}]").Timeline!.CheckpointCount, 1);
            // A tag that could pick a timing event is still refused when that event is switched off.
            Equal(Parse(18, Route, "[{\"floor\":1,\"eventType\":\"RepeatEvents\",\"repetitions\":1,\"interval\":1,\"tag\":\"x\"},"
                + "{\"floor\":2,\"eventType\":\"SetSpeed\",\"speedType\":\"Bpm\",\"beatsPerMinute\":60,\"eventTag\":\"x\",\"active\":false}]").Status,
                MapParseStatus.Unsupported);
        });
        yield return ("Legacy literal newlines are normalized only inside JSON strings", () =>
        {
            var extra = ",\"artist\":\"A\",\"song\":\"B\",\"levelDesc\":\"first\r\nsecond\tthird\",\"url\":\"https://example.test/\\\"quoted\\\"\"";
            var result = Parse(4, extra: extra); Times(result, 500, 1000, 1500); Equal(result.SongName, "A - B");
            Times(Parse(extra: ",/* comment with \"quotes\" */\"song\":\"A\nB\", // another \"quote\"\n\"artist\":\"C\""), 500, 1000, 1500);
            var invalid = Parse(extra: ",\"song\":\"bad\\z\""); Equal(invalid.Status, MapParseStatus.Error);
            Equal(Parse(extra: ",\"song\":\"unterminated").Status, MapParseStatus.Error);
            Equal(Parse(extra: ",\"song\":\"A\"\u0001").Status, MapParseStatus.Error);
            var text = "\uFEFF{\"settings\":{\"version\":4,\"bpm\":120},\"pathData\":\"RR\",\"actions\":[],}";
            Times(new AdofaiTimelineParser().Parse(new("test.adofai", text)), 500, 1000);
        });
        yield return ("A comma missing between members loads, as the game's own reader never needed one", () =>
        {
            // 2850594034\main.adofai has no comma between the "actions" array and "decorations". The game loads a level
            // with GDMiniJSON, whose ParseObject and ParseArray reach the next member through the default case of a
            // switch on the next token and never throw, so it plays there; strict JSON was all that refused it.
            static MapParseResult Read(string text) => new AdofaiTimelineParser().Parse(new("test.adofai", text));
            const string Settings = "{\"settings\":{\"version\":18,\"bpm\":120}";
            Times(Read(Settings + ",\"angleData\":[0,0,0],\"actions\":[]}"), 500, 1000, 1500);
            // The real shape: no comma after the array that ends, and none before the name that follows.
            Times(Read(Settings + ",\"angleData\":[0,0,0],\"actions\":[]\"decorations\":[]}"), 500, 1000, 1500);
            Times(Read(Settings + "\"angleData\":[0,0,0],\"actions\":[]}"), 500, 1000, 1500);
            // Inside an array any value may follow another, so three angles stay three tiles.
            Times(Read(Settings + ",\"angleData\":[0 0 0],\"actions\":[]}"), 500, 1000, 1500);
            // Two events with nothing between them are two events: the same timing as with the comma in place.
            const string Events = "[{\"floor\":1,\"eventType\":\"SetSpeed\",\"speedType\":\"Bpm\",\"beatsPerMinute\":60},"
                + "{\"floor\":2,\"eventType\":\"SetSpeed\",\"speedType\":\"Bpm\",\"beatsPerMinute\":240}]";
            var spaced = Parse(18, "\"angleData\":[0,0,0]", Events);
            Times(spaced, 500, 1500, 1750);
            Times(Parse(18, "\"angleData\":[0,0,0]", Events.Replace("},{", "}{", StringComparison.Ordinal)),
                spaced.Timeline!.HitTimesMs.ToArray());
            // A name is still a name: the separator goes in front of the next one, never inside a string or before a colon.
            var named = Read("{\"settings\":{\"version\":18,\"bpm\":120,\"song\":\"A}\\\"B\"\"artist\":\"C,D\"}"
                + ",\"angleData\":[0,0,0],\"actions\":[]}");
            Equal(named.SongName, "C,D - A}\"B");
            // A comma with no value before it is a token the game skips and loops on (GDMiniJSON ParseObject/ParseArray,
            // COMMA = 6): PLUM MEGAMIX 2 (2924697157) has "requiredMods": [],, in its settings. Two in a row or one right after
            // a bracket are dropped, in an object and in an array, and an array keeps its values.
            Times(Read("{\"settings\":{\"version\":18,\"requiredMods\":[],,\"bpm\":120},\"angleData\":[0,0,0],\"actions\":[]}"), 500, 1000, 1500);
            Times(Read(Settings + ",,\"angleData\":[,0,,0,0],\"actions\":[,]}"), 500, 1000, 1500);
            Equal(Read(Settings + ",\"angleData\":[0,0,0],\"actions\":[],\"x\":\"a,,b\"}").Status, MapParseStatus.Supported);
            // A number cannot name a member, so a level the game could not read either is still an error here.
            Equal(Read("{\"settings\":{\"version\":18,\"bpm\":120} 5,\"angleData\":[0,0],\"actions\":[]}").Status, MapParseStatus.Error);
        });
        yield return ("Legacy pathData absolute angles and midspins", () =>
        {
            Times(Parse(4, "\"pathData\":\"RULD\""), 500, 750, 1000, 1250);
            Times(Parse(4, "\"pathData\":\"R!R\""), 500, 1500);
            // Independent compass/octant landmarks and all 15-degree subdivisions.
            const string symbols = "RpJEToUqGQHWLxNZFVDYBCMA";
            for (var i = 0; i < symbols.Length; i++)
            {
                var expected = Parse(4, "\"angleData\":[0," + i * 15 + "]");
                var actual = Parse(4, "\"pathData\":\"R" + symbols[i] + "\"");
                Times(actual, expected.Timeline!.HitTimesMs.ToArray());
            }
            foreach (var route in new[] { "R?", "R5", "Rr", "" })
                Equal(Parse(4, "\"pathData\":" + JsonSerializer.Serialize(route)).Status, MapParseStatus.Unsupported);
            Equal(Parse(4, "\"angleData\":null,\"pathData\":\"RR\"").Status, MapParseStatus.Error);
        });
        yield return ("Pause adds outgoing beats at current BPM and shifts subsequent hits", () =>
        {
            Times(Parse(actions: "[{\"floor\":1,\"eventType\":\"Pause\",\"duration\":2}]"), 500, 2000, 2500);
            Times(Parse(actions: "[{\"floor\":1,\"eventType\":\"Pause\",\"duration\":3.5,\"angleCorrectionDir\":\"Backward\",\"countdownTicks\":4},{\"floor\":1,\"eventType\":\"SetSpeed\",\"beatsPerMinute\":240}]"), 500, 1625, 1875);
            Times(Parse(actions: "[{\"floor\":1,\"eventType\":\"Pause\",\"duration\":0.5},{\"floor\":1,\"eventType\":\"Pause\",\"duration\":1.5}]"), 500, 2000, 2500);
            foreach (var floor in new[] { 0, 3 })
                Equal(Parse(actions: $"[{{\"floor\":{floor},\"eventType\":\"Pause\",\"duration\":2}}]").Status, MapParseStatus.Unsupported);
            Equal(Parse(route: "\"angleData\":[0,999,0]", actions: "[{\"floor\":1,\"eventType\":\"Pause\",\"duration\":2}]").Status, MapParseStatus.Unsupported);
            // The game also decodes enum values by number: -1 is Backward (Search Party writes it this way).
            Times(Parse(actions: "[{\"floor\":1,\"eventType\":\"Pause\",\"duration\":2,\"angleCorrectionDir\":-1}]"), 500, 2000, 2500);
            // The direction only turns the planet's drawn angle (scrPlanet.HandlePause → TweenCosmeticAngle), so every defined
            // one keeps the timing (柿崎ユウタ - 月が綺麗ねと言われたい！ has a Forward pause).
            foreach (var direction in new[] { "\"Forward\"", "\"None\"", "1", "0" })
                Times(Parse(actions: "[{\"floor\":1,\"eventType\":\"Pause\",\"duration\":1.5,\"angleCorrectionDir\":" + direction + "}]"), 500, 1750, 2250);
            foreach (var field in new[] { "\"duration\":-1", "\"duration\":2,\"angleOffset\":45", "\"duration\":2,\"angleCorrectionDir\":\"Sideways\"",
                "\"duration\":2,\"angleCorrectionDir\":2", "\"duration\":2,\"angleCorrectionDir\":-1.5" })
                Equal(Parse(actions: "[{\"floor\":1,\"eventType\":\"Pause\"," + field + "}]").Status, MapParseStatus.Unsupported);
            Equal(Parse(actions: "[{\"floor\":1,\"eventType\":\"Pause\",\"duration\":2,\"eventTag\":\"p\"},{\"floor\":1,\"eventType\":\"RepeatEvents\",\"tag\":\"p\"}]").Status, MapParseStatus.Unsupported);
        });
        yield return ("Negative and multi-turn angles preserve directions, not extra revolutions", () =>
        {
            Times(Parse(13, "\"angleData\":[0,-45,-180,-720]"), 500, 1125, 2000, 3000);
            Times(Parse(13, "\"angleData\":[0,315,180,0]"), 500, 1125, 2000, 3000);
            Times(Parse(13, "\"angleData\":[0,-720,999,-900]"), 500, 1000, 1500);
            Times(Parse(13, "\"angleData\":[360,720,1080]"), 500, 1000, 1500);
            Equal(Parse(13, "\"angleData\":[0,1e100]").Status, MapParseStatus.Unsupported);
        });
        yield return ("A turn within 1e-6 rad of none or of a whole circle is a whole circle, as the game counts it", () =>
        {
            // Tanchiky - ENERGY SYNERGY MATRIX: a 3.2e-6° tile, a midspin, then 0° leaves a 3.2e-6° turn, which the game plays
            // as a whole circle (scrLevelMaker.CalculateSingleFloorAngleLength works in radians with a 1e-6 margin).
            var tiny = 3.201651e-06 / 180 * 500;
            Times(Parse(13, "\"angleData\":[0,3.201651e-06,999,0]"), 500, 1000 - tiny, 2000 - tiny);
            // Just short of a whole circle is one too, and so is the tiny turn back.
            Times(Parse(13, "\"angleData\":[0,180.00001,0]"), 500, 1500, 2500);
            // 0.0001° is past the margin (1.7e-6 rad): it stays that small, as in the game.
            var small = 0.0001 / 180 * 500;
            Times(Parse(13, "\"angleData\":[0,180.0001,0]"), 500, 1500 - small, 1500);
        });
        yield return ("Verified audiovisual events do not affect hit timing", () =>
        {
            foreach (var kind in new[] { "ScalePlanets", "PlaySound", "SetHitsound", "AddDecoration", "SetText", "ScreenTile", "ScreenScroll", "SetParticle", "SetDefaultText", "AddText" })
                Times(Parse(actions: $"[{{\"floor\":1,\"eventType\":\"{kind}\"}}]"), 500, 1000, 1500);
            // PositionTrack moves tile positions only; the game writes no floor timing field for it.
            foreach (var editorOnly in new[] { "true", "false", "\"Enabled\"", "\"Disabled\"" })
                Times(Parse(actions: $"[{{\"floor\":1,\"eventType\":\"PositionTrack\",\"positionOffset\":[0,1],\"editorOnly\":{editorOnly}}}]"), 500, 1000, 1500);
            Times(Parse(actions: "[{\"floor\":1,\"eventType\":\"PositionTrack\"}]"), 500, 1000, 1500);
            // An empty RepeatEvents tag may repeat tagged visual events, never timing events without eventTag.
            Times(Parse(actions: "[{\"floor\":1,\"eventType\":\"SetFilter\",\"eventTag\":\"\"},{\"floor\":1,\"eventType\":\"RepeatEvents\",\"tag\":\"\"}]"), 500, 1000, 1500);
            Times(Parse(actions: "[{\"floor\":1,\"eventType\":\"RepeatEvents\",\"repetitions\":4,\"interval\":2}]"), 500, 1000, 1500);
            Equal(Parse(actions: "[{\"floor\":1,\"eventType\":\"Twirl\",\"eventTag\":\"\"},{\"floor\":1,\"eventType\":\"RepeatEvents\",\"tag\":\"\"}]").Status, MapParseStatus.Unsupported);
            Equal(Parse(actions: "[{\"floor\":1,\"eventType\":\"Pause\",\"duration\":1,\"eventTag\":\"a  b\"},{\"floor\":1,\"eventType\":\"RepeatEvents\",\"tag\":\"\"}]").Status, MapParseStatus.Unsupported);
            // SetConditionalEvents fills the floor's per-judgment effect lists; its tags may pick visual events only.
            Times(Parse(actions: "[{\"floor\":1,\"eventType\":\"SetConditionalEvents\",\"perfectTag\":\"NONE\",\"lossTag\":\"death\"}," +
                "{\"floor\":2,\"eventType\":\"Flash\",\"eventTag\":\"death\"}]"), 500, 1000, 1500);
            // Only the game's own none tags count as "no tag"; a localized placeholder is a real tag.
            foreach (var none in new[] { "NONE", "\u65e0", "\u7121", "AUCUN", "\u017d\u00c1DN\u00dd", "NICHTS" })
                Times(Parse(actions: $"[{{\"floor\":1,\"eventType\":\"SetConditionalEvents\",\"lossTag\":\"{none}\"}}," +
                    $"{{\"floor\":2,\"eventType\":\"Twirl\",\"eventTag\":\"{none}\"}}]"), 500, 1000, 1500);
            foreach (var tagged in new[] { "\"eventType\":\"Twirl\"", "\"eventType\":\"Pause\",\"duration\":1", "\"eventType\":\"Checkpoint\"" })
                Equal(Parse(actions: "[{\"floor\":1,\"eventType\":\"SetConditionalEvents\",\"lossTag\":\"\uc5c6\uc74c\"}," +
                    $"{{\"floor\":2,{tagged},\"eventTag\":\"\uc5c6\uc74c\"}}]").Status, MapParseStatus.Unsupported);
        });
        if (readOnlyMapRoot is not null)
            yield return ("Opt-in user maps: read-only parse, prepare, checkpoint and rendering", () => AuditMaps(readOnlyMapRoot, artifactRoot, compareGameRules));
    }

    // Songs left Unsupported on purpose until their rules are implemented (relative path → plan).
    private static readonly Dictionary<string, string> DeferredMaps = new(StringComparer.OrdinalIgnoreCase);

    private static void AuditMaps(string mapRoot, string artifactRoot, bool compareGameRules)
    {
        if (compareGameRules) GameRuleTimingComparison.VerifyInspectedAssembly();
        var comparisons = new List<GameRuleTimingComparison.Result>();
        var source = new ReadOnlyDirectoryMapSource(mapRoot);
        var discovery = source.DiscoverAsync().GetAwaiter().GetResult();
        Assert(discovery.Warnings.Count == 0, string.Join(" ", discovery.Warnings));
        Assert(discovery.RelativePaths.Count > 0, "No maps found in explicitly supplied root");
        var snapshots = discovery.RelativePaths.ToDictionary(path => path, path =>
        {
            var full = Path.Combine(mapRoot, path);
            return (Hash: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))), Stamp: File.GetLastWriteTimeUtc(full));
        });
        var failures = new List<string>(); var index = 0;
        try
        {
            foreach (var path in discovery.RelativePaths)
            {
                // The editor auto-save beside a level (Megamix: version 19 backup of a version 8 main.adofai) is not a
                // song, exactly as Map Sync lists it; it is still covered by the read-only hash check below.
                if (MapFileRules.IsShadowedEditorBackup(path, discovery.RelativePaths))
                {
                    Console.WriteLine($"EDITOR BACKUP SKIPPED {path}: auto-save beside its level, not listed as a song.");
                    continue;
                }
                var raw = source.ReadAsync(path).GetAwaiter().GetResult();
                var parser = new AdofaiTimelineParser(); var result = parser.Parse(raw);
                if (result.Status != MapParseStatus.Supported)
                {
                    // Deferred songs stay Unsupported on purpose until their rules are implemented (docs/long-notes-plan.md).
                    if (result.Status == MapParseStatus.Unsupported && DeferredMaps.TryGetValue(path, out var plan))
                        Console.WriteLine($"DEFERRED {path}: {string.Join(" ", result.Warnings)} ({plan})");
                    else
                        failures.Add(path + ": " + string.Join(" ", result.Warnings));
                    continue;
                }
                var timeline = result.Timeline!;
                Assert(timeline.HitTimesMs.Count > 0, "Empty supported timeline");
                // Timing first, so a song the default lanes cannot place still gets its timing compared.
                if (compareGameRules) comparisons.Add(GameRuleTimingComparison.Compare(raw, timeline, artifactRoot, index + 1));
                var window = new MainWindow();
                try
                {
                    var model = (MainViewModel)window.DataContext;
                    model.SelectedMap = new MapCacheEntry("audit", source.RootDirectory, path, result.SongName ?? path,
                        result.Status, timeline, result.Warnings, "audit", parser.Version);
                    // Loading already switches the session on, so only switch on when it did not.
                    model.LoadSelectedSong();
                    if (model.Playback.State == PlaybackState.Off) model.Toggle();
                    // This pinned map contains a dense 3.333ms stream. Its 3-4 hit groups
                    // are shown as two-note chords (plus a single) with the default window instead of failing.
                    if (path.Replace('\\', '/') == "2997617897/main.adofai")
                    {
                        var wide = SimultaneousWideGroups(model.Playback.Notes);
                        Assert(wide > 0, "Expected the dense stream to contain split 3+ hit groups");
                        Console.WriteLine($"CHORD SPLIT {path}: {wide} groups of 3+ hits prepared with the default window.");
                    }
                    if (model.Playback.Notes.Count == 0)
                    {
                        // The default lanes cannot place it, which the app answers by offering wider lanes; its timing was
                        // compared above, and the next map still gets audited.
                        Console.WriteLine($"PLACEMENT {path}: {model.Message}");
                        continue;
                    }
                    // One note per pressed hit: auto hits and long note ends have none.
                    Equal(model.Playback.Notes.Count, timeline.CountPressedHits());
                    AuditNotes(path, timeline, artifactRoot, index + 1);
                    foreach (var checkpoint in timeline.Checkpoints)
                    {
                        model.Checkpoint = checkpoint.Number; model.Reset();
                        Equal(model.Playback.State, PlaybackState.Waiting);
                        Equal(model.Playback.Notes[0].HitTimeMs, 0d);
                        Equal(model.Playback.Notes.Count, timeline.CountPressedHits(checkpoint.HitEventIndex));
                    }
                    model.Checkpoint = 1; model.Reset(); model.Start();
                    Equal(model.Playback.State, PlaybackState.Playing);
                    model.Reset();
                    Capture(window, Path.Combine(artifactRoot, $"user-map-{++index}.png"), 480, 800);
                    Console.WriteLine($"MAP {path}: {result.SongName}; {timeline.HitTimesMs.Count} hits; {timeline.CountPressedHits()} notes; {timeline.CheckpointCount} checkpoints; duration={timeline.HitTimesMs[^1] - timeline.HitTimesMs[0]:F6}ms");
                }
                finally { window.Close(); }
            }
        }
        finally
        {
            foreach (var pair in snapshots)
            {
                var full = Path.Combine(mapRoot, pair.Key);
                Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))), pair.Value.Hash);
                Equal(File.GetLastWriteTimeUtc(full), pair.Value.Stamp);
            }
            Console.WriteLine($"READ ONLY: {snapshots.Count} source files unchanged (SHA-256 and modification time).");
        }
        Assert(failures.Count == 0, string.Join(Environment.NewLine, failures));
        if (compareGameRules) GameRuleTimingComparison.WriteSummary(comparisons, artifactRoot);
    }

    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }

    /// <summary>
    /// Real-map note checks with default settings: chord shape and lanes, captures of the first 2- and 3-hit groups,
    /// practice starts keeping full-song lanes, and preparation under other lane settings (reported, not required).
    /// </summary>
    private static void AuditNotes(string path, ParsedTimeline timeline, string artifactRoot, int mapIndex)
    {
        var settings = new VisualizerSettings();
        var clock = new Clock();
        var session = new PlaybackSession(clock);
        session.LoadParsedTimeline(timeline, settings);
        session.Toggle();
        var notes = session.Notes;
        var visible = settings.GetVisibleTiles().ToList();
        var sizes = new SortedDictionary<int, int>();
        var pairs = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var firstGroup = new Dictionary<int, int>();
        for (var start = 0; start < notes.Count;)
        {
            var end = start + 1;
            while (end < notes.Count && notes[end].HitTimeMs == notes[start].HitTimeMs) end++;
            var size = end - start;
            sizes[size] = sizes.GetValueOrDefault(size) + 1;
            firstGroup.TryAdd(size, start);
            Assert(notes.Skip(start).Take(size).Select(n => n.Tile).Distinct().Count() == size, $"{path}: repeated lane at note {start + 1}");
            for (var m = start; m + 1 < end; m += 2)
            {
                var a = visible.IndexOf(notes[m].Tile);
                var b = visible.IndexOf(notes[m + 1].Tile);
                Assert(Math.Abs(a - b) == 1, $"{path}: chord at note {m + 1} is not on adjacent lanes");
                var key = $"{notes[m].Mode}K lanes {Math.Min(a, b) + 1}-{Math.Max(a, b) + 1}";
                pairs[key] = pairs.GetValueOrDefault(key) + 1;
            }
            start = end;
        }
        Console.WriteLine($"CHORDS {path}: display groups {string.Join(", ", sizes.Select(p => $"{p.Key} hit x{p.Value}"))}; chord lanes {string.Join(", ", pairs.Select(p => $"{p.Key} x{p.Value}"))}");

        foreach (var size in new[] { 2, 3, 4 })
        {
            if (!firstGroup.TryGetValue(size, out var first)) continue;
            session.Reset(settings);
            clock.NowMs = 0; session.Start();
            // Hold the group 35% of the travel above the judgment line, frozen for the capture.
            clock.NowMs = Math.Max(0, session.Notes[first].HitTimeMs - settings.NoteTravelTimeMs * 0.35);
            session.Pause();
            var window = new Window { Content = new NoteField { Session = session, Judge = new NotePressJudge() }, Background = Brushes.Black };
            try { Capture(window, Path.Combine(artifactRoot, $"user-map-{mapIndex}-group{size}.png"), 480, 700); }
            finally { window.Close(); }
        }

        if (timeline.HitFloors is { } floors)
        {
            session.Reset(settings);
            var full = session.Notes;
            var starts = 0;
            var rejected = 0;
            for (var s = 1; s <= 24; s++)
            {
                var tile = floors[(int)((long)(floors.Count - 1) * s / 25)] - 1;
                int index;
                try { index = timeline.GetStartIndexForTile(tile); }
                catch (ArgumentOutOfRangeException) { rejected++; continue; } // Midspin and long note tiles are rejected with a reason.
                // Notes exist for pressed hits only, so the section starts at the note of the first pressed hit from this index.
                var skip = timeline.CountPressedHits() - timeline.CountPressedHits(index);
                if (skip == full.Count)
                {
                    Throws<NotePlacementException>(() => session.ResetAtTile(settings, tile)); // Only auto hits are left.
                    rejected++;
                    continue;
                }
                session.ResetAtTile(settings, tile);
                Equal(session.Notes.Count, full.Count - skip);
                Assert(session.Notes.Select(n => n.Tile).SequenceEqual(full.Skip(skip).Select(n => n.Tile)), $"{path}: tile {tile} changed lanes");
                Equal(session.Notes[0].HitTimeMs, 0d);
                Equal(session.LaneStartTile, (int?)full[skip].Tile);
                starts++;
            }
            Assert(starts > 0, $"{path}: no practice start could be checked");
            Console.WriteLine($"SECTIONS {path}: {starts} practice starts keep full-song lanes and start keys ({rejected} midspin, long note or auto-only tiles rejected).");
        }

        var variants = new List<string>();
        var centered = new List<string> { "max 4 " + ChordPlacement(notes, settings) };
        foreach (var (name, variant) in new (string, VisualizerSettings)[]
        {
            ("max 1", settings with { MaximumTileCount = 1 }), ("max 2", settings with { MaximumTileCount = 2 }),
            ("max 6", settings with { MaximumTileCount = 6 }), ("max 8", settings with { MaximumTileCount = 8 }),
            ("dynamic off", settings with { DynamicTiming = false }),
            ("window 0ms", settings with { SimultaneousWindowMs = 0 }), ("speed 50%", settings with { PlaybackSpeedPercent = 50 })
        })
        {
            try
            {
                session.Reset(variant);
                variants.Add(name + " ok");
                if (variant.MaximumTileCount is 6 or 8) centered.Add($"{name} {ChordPlacement(session.Notes, variant)}");
            }
            catch (NotePlacementException error)
            {
                Equal(session.Notes.Count, 0);
                variants.Add($"{name} stopped ({error.Message})");
            }
        }
        Console.WriteLine($"VARIANTS {path}: {string.Join("; ", variants)}");
        Console.WriteLine($"CENTER {path}: {string.Join("; ", centered)}");
        // Checked last so every diagnostic line above is printed first.
        if (visible.Count == 4)
            Assert(!pairs.Keys.Any(k => k.EndsWith("lanes 2-3", StringComparison.Ordinal)), $"{path}: chords on the middle lanes 2-3");
    }

    /// <summary>
    /// How prepared chords sit relative to the screen center: centered pairs (tiles 3-4 and 5-6 first, the preferred placement),
    /// other pairs within one half, and pairs across the center (last resort; never on four lanes).
    /// </summary>
    private static string ChordPlacement(IReadOnlyList<VisualizerNote> notes, VisualizerSettings settings)
    {
        var visible = settings.GetVisibleTiles().ToList();
        int half = visible.Count / 2, centered = 0, inHalf = 0, across = 0;
        for (var i = 0; i + 1 < notes.Count; i++)
        {
            if (!notes[i].StartsChord) continue;
            var low = Math.Min(visible.IndexOf(notes[i].Tile), visible.IndexOf(notes[i + 1].Tile));
            if (low == half - 1) across++;
            else if (low < half ? (half - 2 - low) % 2 == 0 : (low - half) % 2 == 0) centered++;
            else inHalf++;
        }
        return $"chords centered {centered}, other in half {inHalf}, across center {across}";
    }

    /// <summary>Prepared display groups of three or more hits (split into two-note chords and a single).</summary>
    private static int SimultaneousWideGroups(IReadOnlyList<VisualizerNote> notes)
    {
        var count = 0;
        for (var start = 0; start < notes.Count;)
        {
            var end = start + 1;
            while (end < notes.Count && notes[end].HitTimeMs == notes[start].HitTimeMs) end++;
            if (end - start >= 3) count++;
            start = end;
        }
        return count;
    }
}
