using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Visualizer.App.Commands;
using Visualizer.App.Rendering;
using Visualizer.Application.Cache;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Core.Visualization;
using Visualizer.Infrastructure.Cache;
using Visualizer.Infrastructure.Parsing;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

/// <summary>Long notes (Hold) and auto tiles (AutoPlayTiles).</summary>
internal static class LongNoteTests
{
    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }

    // 120 BPM: one beat is 500ms. Straight 0-degree tiles turn 180 degrees, so hits of floors 1..4 come every beat.
    private static MapParseResult Parse(string actions, string angles = "0,0,0,0") => new AdofaiTimelineParser().Parse(
        new("long-note.adofai", $"{{\"settings\":{{\"version\":19,\"bpm\":120}},\"angleData\":[{angles}],\"actions\":{actions}}}"));

    private static ParsedTimeline Supported(MapParseResult result)
    {
        Assert(result.Status == MapParseStatus.Supported, string.Join(" ", result.Warnings));
        return result.Timeline!;
    }

    private static void Kinds(ParsedTimeline timeline, params HitKind[] expected) =>
        Assert(Enumerable.Range(0, timeline.HitTimesMs.Count).Select(timeline.KindOf).SequenceEqual(expected),
            "Wrong hit kinds: " + string.Join(" ", Enumerable.Range(0, timeline.HitTimesMs.Count).Select(timeline.KindOf)));

    private static void Times(ParsedTimeline timeline, params double[] expected) =>
        Assert(timeline.HitTimesMs.Count == expected.Length && timeline.HitTimesMs.Zip(expected).All(p => Math.Abs(p.First - p.Second) < 1e-6),
            "Wrong hit times: " + string.Join(" ", timeline.HitTimesMs));

    private static void NoNoteOnHeldLane(IReadOnlyList<VisualizerNote> notes)
    {
        foreach (var hold in notes.Where(n => n.IsHold))
            // A note exactly on the end is fine: that is where the key comes up, and a chained hold is pressed there.
            Assert(!notes.Any(n => n != hold && n.Tile == hold.Tile && n.HitTimeMs > hold.HitTimeMs && n.HitTimeMs < hold.HoldEndMs),
                $"A note starts on tile {hold.Tile} before its long note ends");
    }

    public static IEnumerable<(string Name, Action Run)> Create(string artifactRoot)
    {
        yield return ("An auto FreeRoam section adds Max(a, duration - a) beats and has no notes; others stay unsupported", () =>
        {
            const string Auto2 = "{\"floor\":2,\"eventType\":\"AutoPlayTiles\",\"enabled\":true},{\"floor\":3,\"eventType\":\"AutoPlayTiles\",\"enabled\":false}";
            string Roam(int floor, string duration, string extra = "") =>
                $"{{\"floor\":{floor},\"eventType\":\"FreeRoam\",\"duration\":{duration},\"size\":[5,4],\"angleCorrectionDir\":-1{extra}}}";
            // Up In My Jam's shape: the section starts on an auto tile, so the next hit comes `duration` beats later (8 here),
            // the start tile is played by the game, and the next tile is pressed as usual.
            var roam = Supported(Parse($"[{Roam(2, "8")},{Auto2}]"));
            Times(roam, 500, 1000, 5000, 5500);
            Kinds(roam, HitKind.Tap, HitKind.Auto, HitKind.Tap, HitKind.Tap);
            Equal(PlaybackSession.GenerateSongNotes(roam, new VisualizerSettings(), out _).Count, 3);
            // The band the screen shows: from the auto tile that starts the mini-game to the hit that ends it.
            Assert(roam.Sections.SequenceEqual(new[] { new TimelineSection(1000, 5000) }), "The free roam band");
            // The same section with no auto tile is played by hand (2850594034, floors 266 and 267): event 44 in the core
            // pass never looks at the auto flag, so the beats are the same; the tile that starts it is pressed as usual and
            // the free movement inside the grid is simply not shown.
            var byHand = Supported(Parse($"[{Roam(2, "8")}]"));
            Times(byHand, 500, 1000, 5000, 5500);
            Kinds(byHand, HitKind.Tap, HitKind.Tap, HitKind.Tap, HitKind.Tap);
            Assert(byHand.Sections.SequenceEqual(new[] { new TimelineSection(1000, 5000) }), "The band is the same when it is played by hand");
            Equal(PlaybackSession.GenerateSongNotes(byHand, new VisualizerSettings(), out _).Count, 4);
            // A whole-circle turn (2 beats) longer than duration - a: Max picks the turn, so 2 + 2 beats to the next hit.
            Times(Supported(Parse($"[{Roam(2, "2")},{Auto2}]", "0,0,180,0")), 500, 1000, 3000, 4000);
            // A hold into the section's auto tile, as in the song: the hold's beats still count, and it is only a press.
            var held = Supported(Parse($"[{{\"floor\":1,\"eventType\":\"Hold\",\"duration\":3}},{Roam(2, "8")},{Auto2}]"));
            Times(held, 500, 4000, 8000, 8500);
            Kinds(held, HitKind.Tap, HitKind.Auto, HitKind.Tap, HitKind.Tap);
            foreach (var (actions, angles) in new[]
            {
                ($"[{Roam(2, "1")},{Auto2}]", "0,0,0,0"),                                 // the game ignores a duration below 2
                ($"[{Roam(2, "8.5")},{Auto2}]", "0,0,0,0"),
                ($"[{Roam(0, "8")},{Auto2}]", "0,0,0,0"),
                ($"[{Roam(4, "8")},{Auto2}]", "0,0,0,0"),
                ($"[{Roam(2, "8", ",\"angleCorrectionDir\":1")},{Auto2}]", "0,0,0,0"),
                ($"[{Roam(2, "8")},{{\"floor\":2,\"eventType\":\"Pause\",\"duration\":1}},{Auto2}]", "0,0,0,0"),
                ($"[{Roam(2, "8")},{{\"floor\":2,\"eventType\":\"Hold\",\"duration\":1}},{Auto2}]", "0,0,0,0"),
                ($"[{Roam(2, "8")},{Auto2}]", "0,0,90,0"),                                // a 90° turn differs with the direction
                ($"[{Roam(2, "8")},{Auto2},{{\"floor\":2,\"eventType\":\"FreeRoamTwirl\"}}]", "0,0,0,0"),
                ($"[{Roam(2, "8")},{Auto2},{{\"floor\":3,\"eventType\":\"Checkpoint\",\"tileOffset\":-1}}]", "0,0,0,0")  // restart on the section
            })
                Equal(Parse(actions, angles).Status, MapParseStatus.Unsupported);
        });

        yield return ("Hold adds duration x2 beats (none for duration 0), pairs the release hit, and rejects unverified holds", () =>
        {
            var one = Supported(Parse("[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":1,\"distanceMultiplier\":0,\"landingAnimation\":true}]"));
            Times(one, 500, 2000, 2500, 3000);
            Kinds(one, HitKind.HoldStart, HitKind.HoldEnd, HitKind.Tap, HitKind.Tap);
            Times(Supported(Parse("[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":8}]")), 500, 9000, 9500, 10000);
            // Pause on the same floor adds its beats too; a hold may end on the last hit.
            Times(Supported(Parse("[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":1},{\"floor\":1,\"eventType\":\"Pause\",\"duration\":1}]")), 500, 2500, 3000, 3500);
            Kinds(Supported(Parse("[{\"floor\":3,\"eventType\":\"Hold\",\"duration\":1}]")), HitKind.Tap, HitKind.Tap, HitKind.HoldStart, HitKind.HoldEnd);
            // Duration 0 (Up In My Jam): no extra time (CalculateFloorEntryTimes adds only above 0), but still a hold released on the
            // next hit (UpdateHoldBehavior skips only -1).
            var zero = Supported(Parse("[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":0,\"distanceMultiplier\":100,\"landingAnimation\":false}]"));
            Times(zero, 500, 1000, 1500, 2000);
            Kinds(zero, HitKind.HoldStart, HitKind.HoldEnd, HitKind.Tap, HitKind.Tap);
            var zeroNotes = PlaybackSession.GenerateSongNotes(zero, new VisualizerSettings { SimultaneousWindowMs = 0 }, out _);
            Assert(zeroNotes.Count == 3 && zeroNotes[0].HoldEndMs is double end && Math.Abs(end - zeroNotes[0].HitTimeMs - 500) < 1e-6, "A zero-length hold is a long note to the next hit");
            Kinds(Supported(Parse("[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":0},{\"floor\":3,\"eventType\":\"Hold\",\"duration\":1}]")),
                HitKind.HoldStart, HitKind.HoldEnd, HitKind.HoldStart, HitKind.HoldEnd);
            foreach (var actions in new[]
            {
                "[{\"floor\":1,\"eventType\":\"Hold\"}]",
                "[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":-1}]",
                "[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":1.5}]",
                "[{\"floor\":0,\"eventType\":\"Hold\",\"duration\":1}]",
                "[{\"floor\":4,\"eventType\":\"Hold\",\"duration\":1}]",
                "[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":1},{\"floor\":1,\"eventType\":\"Hold\",\"duration\":2}]",
                "[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":1},{\"floor\":1,\"eventType\":\"AutoPlayTiles\",\"enabled\":true}]",
                "[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":1,\"eventTag\":\"h\"},{\"floor\":1,\"eventType\":\"RepeatEvents\",\"tag\":\"h\"}]",
                "[{\"floor\":2,\"eventType\":\"Hold\",\"duration\":1},{\"floor\":2,\"eventType\":\"Checkpoint\",\"tileOffset\":0}]"
            })
                Equal(Parse(actions).Status, MapParseStatus.Unsupported);
            // Two holds in a row (cosMo@暴走P - THE END OF HATSUNE MIKU has 21 such pairs). Each tile adds its own
            // holdLength x 2 beats, since CalculateSingleFloorAngleLength looks at that floor alone, and the hit between
            // them both releases the first and starts the second: scrPlayer.UpdateHoldKeys takes the queued press as soon
            // as _nextTileIsHold is set, without waiting for the running hold's holdCompletion. So it is a note of its own.
            var chain = Supported(Parse("[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":1},{\"floor\":2,\"eventType\":\"Hold\",\"duration\":1}]"));
            Times(chain, 500, 2000, 3500, 4000);
            Kinds(chain, HitKind.HoldStart, HitKind.HoldChain, HitKind.HoldEnd, HitKind.Tap);
            var chained = PlaybackSession.GenerateSongNotes(chain, new VisualizerSettings { SimultaneousWindowMs = 0 }, out _);
            Equal(chained.Count, 3);
            // Notes are normalized to the first hit, so the timeline's 500/2000/3500/4000 become 0/1500/3000/3500.
            Assert(chained[0].HoldEndMs == 1500 && chained[1].HitTimeMs == 1500 && chained[1].HoldEndMs == 3000,
                "The first long note ends where the second begins");
            Assert(chained[2].HitTimeMs == 3500 && !chained[2].IsHold, "The hit after the release is an ordinary note");
            NoNoteOnHeldLane(chained);
            // Three in a row, and a chain of zero-length holds: every tile keeps its own release.
            Kinds(Supported(Parse("[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":0},{\"floor\":2,\"eventType\":\"Hold\",\"duration\":0},"
                + "{\"floor\":3,\"eventType\":\"Hold\",\"duration\":0}]", "0,0,0,0,0")),
                HitKind.HoldStart, HitKind.HoldChain, HitKind.HoldChain, HitKind.HoldEnd, HitKind.Tap);
            // A chain whose own release is an auto tile has no release to show, so it stays unverified; and a checkpoint
            // may not restart on a release.
            Equal(Parse("[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":1},{\"floor\":2,\"eventType\":\"Hold\",\"duration\":1},"
                + "{\"floor\":3,\"eventType\":\"AutoPlayTiles\",\"enabled\":true}]").Status, MapParseStatus.Unsupported);
            Equal(Parse("[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":1},{\"floor\":2,\"eventType\":\"Hold\",\"duration\":1},"
                + "{\"floor\":2,\"eventType\":\"Checkpoint\"}]").Status, MapParseStatus.Unsupported);
            // A hold carries over a midspin to the tile after it (cosMo@暴走P - THE END OF HATSUNE MIKU, floor 644):
            // scrPlayer.Hit sets midspinInfiniteMargin when it lands on a midspin and UpdateHoldKeys hits once more straight
            // away, so the key is still held. The midspin is no note, and the release is the next tile that is pressed.
            var overMidSpin = Supported(Parse("[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":1}]", "0,0,999,0"));
            Times(overMidSpin, 500, 2000, 3000);
            Kinds(overMidSpin, HitKind.HoldStart, HitKind.HoldEnd, HitKind.Tap);
            // A hold into an auto tile is only a press: the game skips the release when the next tile is auto (Up In My Jam).
            var intoAuto = Supported(Parse("[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":1},{\"floor\":2,\"eventType\":\"AutoPlayTiles\",\"enabled\":true},"
                + "{\"floor\":3,\"eventType\":\"AutoPlayTiles\",\"enabled\":false}]"));
            Times(intoAuto, 500, 2000, 2500, 3000);
            Kinds(intoAuto, HitKind.Tap, HitKind.Auto, HitKind.Tap, HitKind.Tap);
            Equal(Parse("[{\"floor\":2,\"eventType\":\"Hold\",\"duration\":1}]", "0,0,999,0").Status, MapParseStatus.Unsupported);
            // Starting on the hit after a hold tile would release a key that was never pressed.
            Throws<ArgumentOutOfRangeException>(() => one.GetStartIndexForTile(1));
            Equal(one.GetStartIndexForTile(2), 2);
            Throws<ArgumentException>(() => _ = new ParsedTimeline(new[] { 0d, 1 }, hitKinds: new[] { HitKind.HoldStart, HitKind.Tap }));
            Throws<ArgumentException>(() => _ = new ParsedTimeline(new[] { 0d, 1 }, hitKinds: new[] { HitKind.HoldEnd, HitKind.Tap }));
            Throws<ArgumentException>(() => _ = new ParsedTimeline(new[] { 0d, 1, 2 }, new[] { new TimelineCheckpoint(1, 0), new TimelineCheckpoint(2, 2) }, true,
                hitKinds: new[] { HitKind.Tap, HitKind.HoldStart, HitKind.HoldEnd }));
            // Newly accepted visual events (ヤラララ) keep hit timing.
            foreach (var kind in new[] { "Hide", "ScaleRadius", "SetObject", "AddObject", "HallOfMirrors", "SetFrameRate", "EmitParticle", "SetHoldSound" })
                Times(Supported(Parse($"[{{\"floor\":1,\"eventType\":\"{kind}\"}}]")), 500, 1000, 1500, 2000);
        });

        yield return ("Auto tiles carry to later floors and are never notes; starts skip to the next pressed hit", () =>
        {
            var timeline = Supported(Parse("[{\"floor\":2,\"eventType\":\"AutoPlayTiles\",\"enabled\":true,\"showStatusText\":false},{\"floor\":3,\"eventType\":\"AutoPlayTiles\",\"enabled\":false}]"));
            Times(timeline, 500, 1000, 1500, 2000);
            Kinds(timeline, HitKind.Tap, HitKind.Auto, HitKind.Tap, HitKind.Tap);
            Kinds(Supported(Parse("[{\"floor\":2,\"eventType\":\"AutoPlayTiles\",\"enabled\":true}]")), HitKind.Tap, HitKind.Auto, HitKind.Auto, HitKind.Auto);
            Equal(Parse("[{\"floor\":2,\"eventType\":\"AutoPlayTiles\"}]").Status, MapParseStatus.Unsupported);
            Equal(timeline.CountPressedHits(), 3);
            Equal(timeline.CountPressedHits(1), 2);

            var settings = new VisualizerSettings { SimultaneousWindowMs = 0 };
            var notes = PlaybackSession.GenerateSongNotes(timeline, settings, out var hits);
            Sequence(hits, 0, 2, 3);
            Equal(notes.Count, 3);
            var session = new PlaybackSession(new Clock());
            session.LoadParsedTimeline(timeline, settings);
            Equal(session.Notes.Count, 3);
            session.ResetAtTile(settings, 1); // The first hit after tile 1 is the auto hit of floor 2.
            Equal(session.StartHitIndex, 1);
            Equal(session.Notes.Count, 2);
            Equal(session.Notes[0].HitTimeMs, settings.StartDelayMs);
            Equal(session.Notes[1].HitTimeMs - session.Notes[0].HitTimeMs, 500d);

            var autoEnd = Supported(Parse("[{\"floor\":3,\"eventType\":\"AutoPlayTiles\",\"enabled\":true}]"));
            var ended = new PlaybackSession(new Clock());
            ended.LoadParsedTimeline(autoEnd, settings);
            Throws<NotePlacementException>(() => ended.ResetAtTile(settings, 2));
            Equal(ended.Notes.Count, 0);
            Equal(ended.Timeline, autoEnd); // The source timeline stays for another start.
        });

        yield return ("Long notes carry their end, keep their lane busy, and survive the cache and the console", () =>
        {
            // The boundaries are pinned to the values this case was written for (a 100ms gap is 2K).
            var settings = new VisualizerSettings { SimultaneousWindowMs = 0, FourKeyBoundaryMs = 80 };
            var notes = new NoteGenerator().Generate(new[] { 0d, 100, 200, 300, 400 }, new double?[] { 250, null, null, null, null }, settings);
            Equal(notes[0].HoldEndMs, (double?)250);
            Assert(notes.Skip(1).All(n => !n.IsHold), "Only the hold start is a long note");
            NoNoteOnHeldLane(notes);
            // Without the long note the third note returns to the first lane; with it, that lane is still held.
            var taps = new NoteGenerator().Generate(new[] { 0d, 100, 200, 300, 400 }, settings);
            Equal(taps[2].Tile, taps[0].Tile);
            Assert(notes[2].Tile != notes[0].Tile, "The note during the long note takes another lane");
            // One lane only: nothing can avoid the held lane, so preparation stops with a reason.
            Throws<NotePlacementException>(() => new NoteGenerator().Generate(new[] { 0d, 600 }, new double?[] { 700, null },
                new VisualizerSettings { SimultaneousWindowMs = 0, DynamicTiming = false, MaximumTileCount = 1 }));
            Throws<ArgumentException>(() => new NoteGenerator().Generate(new[] { 0d, 600 }, new double?[] { 0, null }, settings));

            var timeline = Supported(Parse("[{\"floor\":1,\"eventType\":\"Hold\",\"duration\":1}]"));
            var songNotes = PlaybackSession.GenerateSongNotes(timeline, settings, out var hits);
            Sequence(hits, 0, 2, 3);
            Equal(songNotes[0].HoldEndMs, 1500d);
            // The speed scales the long note end together with its start.
            var half = PlaybackSession.GenerateSongNotes(timeline, settings with { PlaybackSpeedPercent = 50 }, out _);
            Equal(half[0].HoldEndMs, 3000d);
            var lines = CmdSession.FormatNotes(songNotes, hits, 1, 4);
            Assert(lines.SequenceEqual(new[] { $"n1 {songNotes[0].Tile} hold=1500 2000", $"n3 {songNotes[1].Tile} 500", $"n4 {songNotes[2].Tile}" }),
                string.Join(" | ", lines));
            Equal(CmdSession.FormatNotes(songNotes, hits, 2, 2).Count, 0);

            var repo = Path.Combine(artifactRoot, "cache-fixtures", Guid.NewGuid().ToString("N"));
            var maps = Path.Combine(repo, "maps");
            Directory.CreateDirectory(maps);
            File.WriteAllText(Path.Combine(repo, "AGENTS.md"), "Test-owned workspace fixture.");
            File.WriteAllText(Path.Combine(repo, "Visualizer.sln"), "Test-owned marker.");
            var cache = new WorkspaceMapCache(repo);
            cache.ReplaceAsync(new[] { new MapCacheEntry("hold", maps, "long.adofai", "Long", MapParseStatus.Supported, timeline,
                Array.Empty<string>(), "", AdofaiTimelineParser.CurrentVersion) }).GetAwaiter().GetResult();
            var read = cache.ReadAsync().GetAwaiter().GetResult().Single().Timeline!;
            Kinds(read, HitKind.HoldStart, HitKind.HoldEnd, HitKind.Tap, HitKind.Tap);
        });

        yield return ("A long note keeps its lane through the note after it", () =>
        {
            // User rule: no other note on a lane while its long note runs, and the note after a long note never takes its
            // lane (a long note on 2 and then 2 again is never placed), two long notes in a row included.
            var generator = new NoteGenerator();
            var settings = new VisualizerSettings { SimultaneousWindowMs = 0, TrillWindowMs = 0, MagicCircleFixedTiles = false,
                DynamicTiming = true, MinimumTileCount = 1, TwoKeyBoundaryMs = 500 };
            // 600ms apart is 1K: one lane, so the note right on the long note's end reaches for a second lane.
            var slow = generator.Generate(new[] { 0d, 600, 1200, 1800 }, new double?[] { 600, null, null, null }, settings);
            Assert(slow[1].Tile != slow[0].Tile, $"The note after the long note takes another lane: {string.Join(" ", slow.Select(n => n.Tile))}");
            // Two long notes in a row, the second starting where the first ends, and the note after both.
            var chain = generator.Generate(new[] { 0d, 600, 1200, 1800 }, new double?[] { 600, 1200, null, null }, settings);
            Assert(chain[1].Tile != chain[0].Tile && chain[2].Tile != chain[1].Tile,
                $"Each takes a lane of its own: {string.Join(" ", chain.Select(n => n.Tile))}");
            // With a single lane at most there is nowhere else: preparing stops and says why rather than breaking the rule.
            Throws<NotePlacementException>(() => generator.Generate(new[] { 0d, 600, 1200 }, new double?[] { 600, null, null },
                settings with { MaximumTileCount = 1 }));
        });

        yield return ("Long notes draw as a bar in the note color, are used up while held, and end on an early release", () =>
        {
            const int width = 200, height = 400;
            static byte[] Render(FrameworkElement element, string? capture = null)
            {
                element.Measure(new Size(width, height));
                element.Arrange(new Rect(0, 0, width, height));
                element.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(element);
                if (capture is not null)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(capture);
                    encoder.Save(stream);
                }
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                return pixels;
            }
            // Tile 5 is the third of four lanes (x 100..150). With a 36px gap the judgment line is at y 334 and travel is 720ms,
            // so a note 360ms after the displayed time is at y 167. Pbgra32 pixels are B, G, R, A; the note color is #5EDAF2.
            static bool NoteColor(byte[] pixels, int x, int y) => pixels[(y * width + x) * 4] > 200 && pixels[(y * width + x) * 4 + 2] < 140;

            // The transparency of a hit note is pinned so editing the initial settings does not move the pixel checks below.
            var settings = new VisualizerSettings
            {
                DynamicTiming = false, SimultaneousWindowMs = 0, JudgmentLineHeightDip = 36, PressedNoteTransparencyPercent = 60,
                NoteTravelTimeMs = 720, // Pinned: this test reads pixels at fixed positions.
                // The long note is the play's first note; the start color is set to the note color so the bar reads as one.
                StartNoteColor = "94, 218, 242"
            };
            var timeline = new ParsedTimeline(new[] { 0d, 360, 3000 }, hitKinds: new[] { HitKind.HoldStart, HitKind.HoldEnd, HitKind.Tap });
            var clock = new Clock();
            var session = new PlaybackSession(clock);
            session.LoadParsedTimeline(timeline, settings);
            session.Toggle();
            Equal(session.Notes[0].Tile, 5);
            var judge = new NotePressJudge();
            var field = new NoteField { Session = session, Judge = judge };
            field.Tick();
            var waiting = Render(field, Path.Combine(artifactRoot, "long-note.png"));
            Assert(NoteColor(waiting, 125, 250) && NoteColor(waiting, 125, 175), "The long note bar spans from its head to its tail");
            Assert(!NoteColor(waiting, 125, 140) && !NoteColor(waiting, 75, 250), "Nothing is drawn above the tail or on other lanes");

            var tap = new PlaybackSession(clock);
            tap.LoadTimeline(new[] { 0d, 360, 3000 }, settings);
            tap.Toggle();
            var tapField = new NoteField { Session = tap };
            tapField.Tick();
            Assert(!NoteColor(Render(tapField), 125, 250), "A tap at the same time draws no bar");

            // The long note width is the held part's only (user rule): head and tail stay as wide as a note, so a narrower
            // hold reads as an I rather than one thin bar.
            var narrow = new PlaybackSession(clock);
            narrow.LoadParsedTimeline(timeline, settings with { HoldNoteWidth = 0.4 });
            narrow.Toggle();
            var narrowField = new NoteField { Session = narrow };
            narrowField.Tick();
            var shape = Render(narrowField, Path.Combine(artifactRoot, "long-note-narrow.png"));
            int Span(int y) => Enumerable.Range(100, 50).Count(x => NoteColor(shape, x, y));
            Assert(Span(334) >= 40 && Span(168) >= 40, $"Head and tail are note wide: {Span(334)} and {Span(168)}");
            Assert(Span(250) is > 0 and <= 25, $"The held part is narrower between them: {Span(250)}");

            session.Start();
            Assert(judge.Press(session, 5, settings.KeyPressWindowMs) == 0, "The long note head is pressed");
            clock.NowMs = 100;
            field.Tick();
            var held = Render(field);
            // The play's first note keeps its color when pressed (user rule), so this bar stays in full color.
            static int Blue(byte[] pixels, int x, int y) => pixels[(y * width + x) * 4];
            Assert(Blue(held, 125, 250) > 200, $"A held long note that starts the play keeps its color (blue {Blue(held, 125, 250)})");
            // Unclamped, the head 100ms past the line would reach below y 370; held, the bar ends at the judgment line (y 334 + 9).
            Assert(Blue(held, 125, 330) > 200 && Blue(held, 125, 355) < 60, $"A held bar stops at the judgment line (blue {Blue(held, 125, 355)})");
            Assert(judge.Release(session, 5, settings.KeyPressWindowMs), "Letting go well before the end ends the long note early");
            Assert(judge.IsReleasedEarly(session.Notes, 0), "The early release is marked");
            Assert(!judge.Release(session, 5, settings.KeyPressWindowMs), "An ended long note is not released twice");
            field.Tick();
            var released = Render(field);
            Assert(Blue(released, 125, 250) < 60, $"An early released long note is no longer drawn (blue {Blue(released, 125, 250)})");

            // Any other long note is drawn dimmed while held: pressed notes blend 40% of the note color into the field (blue
            // about 108), and the lane background is far darker. The same bar one second later, behind a tap.
            var middle = new PlaybackSession(clock);
            clock.NowMs = 0;
            middle.LoadParsedTimeline(new ParsedTimeline(new[] { 0d, 1000, 1360, 3000 },
                hitKinds: new[] { HitKind.Tap, HitKind.HoldStart, HitKind.HoldEnd, HitKind.Tap }), settings);
            middle.Toggle();
            middle.Start();
            var middleJudge = new NotePressJudge();
            var hold = middle.Notes[1];
            Assert(hold.IsHold, "The second note is the long one");
            clock.NowMs = 1000;
            Equal(middleJudge.Press(middle, hold.Tile, settings.KeyPressWindowMs), (int?)1);
            clock.NowMs = 1100;
            var middleField = new NoteField { Session = middle, Judge = middleJudge };
            middleField.Tick();
            var lanes = settings.GetDisplayTiles(middle.Notes.Select(note => note.Tile)).ToList();
            var x = lanes.IndexOf(hold.Tile) * (width / lanes.Count) + width / lanes.Count / 2;
            var middleHeld = Render(middleField);
            Assert(Blue(middleHeld, x, 250) is > 80 and < 160, $"A held long note is drawn dimmed (blue {Blue(middleHeld, x, 250)})");

            var late = new PlaybackSession(clock);
            clock.NowMs = 0;
            late.LoadParsedTimeline(timeline, settings);
            late.Toggle();
            late.Start();
            var lateJudge = new NotePressJudge();
            Assert(lateJudge.Press(late, 5, settings.KeyPressWindowMs) == 0, "Pressed again from the start");
            clock.NowMs = 300;
            Assert(!lateJudge.Release(late, 5, settings.KeyPressWindowMs), "A release inside the end window is not early");
            Assert(!lateJudge.Release(late, 4, settings.KeyPressWindowMs), "Other lanes have no long note");
        });
    }
}
