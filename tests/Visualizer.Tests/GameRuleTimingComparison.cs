using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Visualizer.Core.Maps;
using static Visualizer.Tests.Program;

namespace Visualizer.Tests;

/// <summary>
/// Independent offline reference from read-only inspection of installed game IL.
/// No game assembly is loaded/executed here and no live game state is observed.
/// </summary>
internal static class GameRuleTimingComparison
{
    private const double Pi = 3.1415927410125732;
    private const double Tau = 6.2831854820251465;
    private const float DegreesToRadians = 0.01745329238474369f;
    private const double ToleranceMs = 1;
    private const string ExpectedAssemblyHash = "0D1524DABF0F8C67AA58BA1992D6ED858522B9094490EBBB00E5E8AEFF7F40CD";
    private const string AssemblyPath = @"C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed\Assembly-CSharp.dll";

    private sealed class Floor
    {
        public double EntryAngle;
        public double ExitAngle;
        public bool Midspin;
        public bool Counterclockwise;
        public float Speed = 1;
        public float ExtraBeats;
        public int HoldLength = -1;
        public int FreeRoamBeats;
        public bool Auto;
        public double TimeMs;
    }

    public sealed record Result(string Map, int Hits, int Sections, double MaxHitErrorMs,
        double LastHitErrorMs, double MaxIntervalErrorMs, double MaxRestartErrorMs, double MaxCheckpointErrorMs, int WorstFloor);

    public static void VerifyInspectedAssembly()
    {
        using var stream = File.OpenRead(AssemblyPath);
        Equal(Convert.ToHexString(SHA256.HashData(stream)), ExpectedAssemblyHash);
    }

    public static Result Compare(RawMapDocument raw, ParsedTimeline actual, string artifactRoot, int fileNumber)
    {
        // Independent string-token normalization, not the production compatibility helper.
        var jsonText = Regex.Replace(raw.Text.TrimStart('\uFEFF'), "\"(?:\\\\.|[^\"\\\\])*\"", match =>
            string.Concat(match.Value.Select(c => c < ' ' ? "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture) : c.ToString())));
        using var document = JsonDocument.Parse(jsonText, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        var settings = root.GetProperty("settings");
        var angles = root.TryGetProperty("angleData", out var angleData)
            ? angleData.EnumerateArray().Select(a => a.GetSingle()).ToArray()
            : root.GetProperty("pathData").GetString()!.Select(PathAngle).ToArray();
        var floors = Enumerable.Range(0, angles.Length + 1).Select(_ => new Floor()).ToArray();
        floors[0].EntryAngle = 4.71238899230957;
        for (var i = 0; i < angles.Length; i++)
        {
            floors[i].Midspin = angles[i] == 999;
            // The IL multiplies r4 values, but the game keeps the product unrounded (evaluation-stack precision): Search Party's
            // hairpins at -180..-2880 degrees play at an even two beats in game (checked by the user in game), which single rounding would break.
            floors[i].ExitAngle = floors[i].Midspin ? (float)floors[i].EntryAngle : (90d - angles[i]) * DegreesToRadians;
            floors[i + 1].EntryAngle = (floors[i].ExitAngle + Pi) % Tau;
        }
        floors[^1].ExitAngle = floors[^1].EntryAngle + Pi;
        // ApplyEventsToFloors files only events whose LevelEvent.active is set; Decode clears it only for a JSON false.
        var events = root.GetProperty("actions").EnumerateArray()
            .Where(e => !e.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.False).ToArray();
        var byFloor = events.GroupBy(e => e.GetProperty("floor").GetInt32()).ToDictionary(g => g.Key, g => g.ToArray());
        var baseBpm = settings.GetProperty("bpm").GetSingle();
        float speed = 1;
        var counterclockwise = false;
        var auto = false;
        for (var i = 0; i < floors.Length; i++)
        {
            var floor = floors[i];
            if (byFloor.TryGetValue(i, out var actions))
            {
                foreach (var action in actions)
                {
                    var kind = action.GetProperty("eventType").GetString();
                    if (kind == "Twirl") counterclockwise = !counterclockwise;
                    if (kind == "Pause" && i < floors.Length - 1 && !floor.Midspin)
                        floor.ExtraBeats += action.GetProperty("duration").GetSingle();
                    // ApplyCoreEventsToFloors: holdLength = duration when a next floor exists and duration >= 0, otherwise -1.
                    // ApplyCoreEventsToFloors (event 44): FreeRoam counts only with a next floor and an integer duration >= 2.
                    if (kind == "FreeRoam" && i < floors.Length - 1 && action.GetProperty("duration").GetInt32() >= 2)
                        floor.FreeRoamBeats = action.GetProperty("duration").GetInt32();
                    if (kind == "Hold")
                    {
                        var duration = action.GetProperty("duration").GetInt32();
                        floor.HoldLength = i < floors.Length - 1 && duration >= 0 ? duration : -1;
                    }
                    // ApplyEventsToFloors keeps the latest AutoPlayTiles "enabled" and copies it to this and later floors.
                    if (kind == "AutoPlayTiles") auto = action.GetProperty("enabled").GetBoolean();
                }
                var speeds = actions.Where(a => a.GetProperty("eventType").GetString() == "SetSpeed").ToArray();
                if (speeds.Length > 0)
                {
                    Assert(speeds.All(a => !a.TryGetProperty("angleOffset", out var offset) || offset.GetDouble() == 0), "Reference supports zero angleOffset only");
                    float newBpm = baseBpm * speed;
                    foreach (var action in speeds)
                        newBpm = action.TryGetProperty("speedType", out var type) && type.GetString() == "Multiplier"
                            ? newBpm * action.GetProperty("bpmMultiplier").GetSingle()
                            : action.GetProperty("beatsPerMinute").GetSingle();
                    speed = newBpm / baseBpm;
                }
            }
            floor.Speed = speed;
            floor.Counterclockwise = counterclockwise;
            floor.Auto = auto;
        }
        // Countdown and song offset are common origins; subtract the first hit on both sides below.
        double seconds = 0;
        for (var i = 0; i < angles.Length; i++)
        {
            var floor = floors[i];
            var travel = PositiveModulo((floor.ExitAngle - floor.EntryAngle) * (floor.Counterclockwise ? -1 : 1), Tau);
            var beatSeconds = 60d / baseBpm / floor.Speed;
            var interval = travel / Pi * beatSeconds;
            // CalculateSingleFloorAngleLength: |angle| <= 1e-6 or !(|angle| < 6.28318430717959) is a whole circle (0 on a midspin).
            if (i > 0 && (travel <= 1e-6 || !(travel < 6.28318430717959)))
                interval = floor.Midspin ? 0 : 2 * beatSeconds;
            seconds += interval;
            // FreeRoam adds Max(a, duration - a) extra beats, a being the floor's angle length in beats (angleLength / π).
            if (i > 0 && floor.FreeRoamBeats > 0)
                floor.ExtraBeats += (float)Math.Max(interval / beatSeconds, floor.FreeRoamBeats - interval / beatSeconds);
            // CalculateFloorEntryTimes adds holdLength × 2 × (one beat) before the extra beats.
            if (i > 0 && floor.HoldLength > 0) seconds += floor.HoldLength * 2 * beatSeconds;
            if (i > 0) seconds += floor.ExtraBeats * beatSeconds;
            floors[i + 1].TimeMs = seconds * 1000;
        }
        var hitFloors = Enumerable.Range(1, angles.Length).Where(i => !floors[i].Midspin).ToArray();
        Equal(actual.HitTimesMs.Count, hitFloors.Length);
        // Hit kinds: the game auto-plays the arrival at an auto floor (scrPlayer.get__nextTileIsAuto) and plays the hit after a hold on
        // release; UpdateHoldBehavior holds on every holdLength above -1, so a hold of duration 0 is one too, and it skips the
        // release when the next tile is auto, so a hold into an auto tile is only a press.
        var expectedKinds = hitFloors.Select(f => floors[f].Auto ? HitKind.Auto
            : floors[f].HoldLength >= 0 && !floors[f + 1].Auto ? HitKind.HoldStart
            : floors[f - 1].HoldLength >= 0 ? HitKind.HoldEnd : HitKind.Tap).ToArray();
        Assert(expectedKinds.SequenceEqual(Enumerable.Range(0, hitFloors.Length).Select(actual.KindOf)), "Reference hit kinds differ from the parser");
        var referenceOrigin = floors[hitFloors[0]].TimeMs;
        var actualOrigin = actual.HitTimesMs[0];
        var error = new double[hitFloors.Length];
        var rows = new StringBuilder("hit_index,floor,visualizer_relative_ms,reference_relative_ms,error_ms\n");
        double maxInterval = 0;
        for (var i = 0; i < hitFloors.Length; i++)
        {
            var referenceTime = floors[hitFloors[i]].TimeMs - referenceOrigin;
            var actualTime = actual.HitTimesMs[i] - actualOrigin;
            error[i] = actualTime - referenceTime;
            if (i > 0) maxInterval = Math.Max(maxInterval, Math.Abs(error[i] - error[i - 1]));
            rows.AppendLine(FormattableString.Invariant($"{i},{hitFloors[i]},{actualTime:R},{referenceTime:R},{error[i]:R}"));
        }
        double maxRestart = 0, maxCheckpoint = 0;
        var sections = new StringBuilder("internal_number,source_floor,respawn_floor,first_hit_floor,activation_error_ms,max_rebased_error_ms\n");
        foreach (var checkpoint in actual.Checkpoints)
        {
            if (checkpoint.RespawnFloor is int respawn)
                Equal(checkpoint.HitEventIndex, Array.FindIndex(hitFloors, f => f > respawn));
            var start = checkpoint.HitEventIndex;
            var sectionError = error.Skip(start).Max(e => Math.Abs(e - error[start]));
            maxRestart = Math.Max(maxRestart, sectionError);
            var activationError = checkpoint.SourceFloor is int source
                ? (checkpoint.ActivationTimeMs!.Value - actualOrigin) - (floors[source].TimeMs - referenceOrigin) : 0;
            maxCheckpoint = Math.Max(maxCheckpoint, Math.Abs(activationError));
            sections.AppendLine(FormattableString.Invariant($"{checkpoint.Number},{checkpoint.SourceFloor},{checkpoint.RespawnFloor},{hitFloors[start]},{activationError:R},{sectionError:R}"));
        }
        File.WriteAllText(Path.Combine(artifactRoot, $"timing-map-{fileNumber}-hits.csv"), rows.ToString());
        File.WriteAllText(Path.Combine(artifactRoot, $"timing-map-{fileNumber}-checkpoints.csv"), sections.ToString());
        var worst = Enumerable.Range(0, error.Length).MaxBy(i => Math.Abs(error[i]));
        var result = new Result(raw.SourcePath, error.Length, actual.Checkpoints.Count, Math.Abs(error[worst]), error[^1],
            maxInterval, maxRestart, maxCheckpoint, hitFloors[worst]);
        Console.WriteLine(FormattableString.Invariant($"RULE COMPARISON {raw.SourcePath}: max={result.MaxHitErrorMs:F6}ms, interval={maxInterval:F6}ms, restart={maxRestart:F6}ms, checkpoint={maxCheckpoint:F6}ms, worst floor={result.WorstFloor}"));
        return result;
    }

    public static void WriteSummary(IReadOnlyList<Result> results, string artifactRoot)
    {
        var report = new StringBuilder("# Offline game-rule timing comparison\n\nThis is an independent calculation from inspected game IL, not a recording or measurement of live gameplay.\n\n");
        report.AppendLine($"Inspected Assembly-CSharp.dll SHA-256: `{ExpectedAssemblyHash}`. Both timelines normalized to their first hit; numeric acceptance threshold: {ToleranceMs} ms.\n");
        report.AppendLine("| Map | Hits | Sections | Max hit error ms | Last hit error ms | Max interval error ms | Max restart error ms | Max checkpoint error ms | Worst floor |");
        report.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in results)
            report.AppendLine(FormattableString.Invariant($"| {r.Map} | {r.Hits} | {r.Sections} | {r.MaxHitErrorMs:F6} | {r.LastHitErrorMs:F6} | {r.MaxIntervalErrorMs:F6} | {r.MaxRestartErrorMs:F6} | {r.MaxCheckpointErrorMs:F6} | {r.WorstFloor} |"));
        File.WriteAllText(Path.Combine(artifactRoot, "timing-comparison.md"), report.ToString());
        Assert(results.All(r => Math.Max(Math.Max(r.MaxHitErrorMs, r.MaxRestartErrorMs), Math.Max(r.MaxIntervalErrorMs, r.MaxCheckpointErrorMs)) <= ToleranceMs),
            "Game-rule reference differs by more than 1 ms; inspect timing-comparison.md and per-hit CSVs.");
    }

    private static double PositiveModulo(double value, double divisor) => (value % divisor + divisor) % divisor;
    private static float PathAngle(char c) => c switch
    {
        'R' => 0, 'p' => 15, 'J' => 30, 'E' => 45, 'T' => 60, 'o' => 75, 'U' => 90, 'q' => 105,
        'G' => 120, 'Q' => 135, 'H' => 150, 'W' => 165, 'L' => 180, 'x' => 195, 'N' => 210, 'Z' => 225,
        'F' => 240, 'V' => 255, 'D' => 270, 'Y' => 285, 'B' => 300, 'C' => 315, 'M' => 330, 'A' => 345, '!' => 999,
        _ => throw new InvalidDataException("Unknown reference path symbol")
    };
}
