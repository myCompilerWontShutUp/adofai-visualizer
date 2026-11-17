using System.Text.Json;
using System.Text.RegularExpressions;
using Visualizer.Application.Maps;
using Visualizer.Core.Maps;

namespace Visualizer.Infrastructure.Parsing;

/// <summary>The verified subset of the level format; unsupported timing is never silently skipped.</summary>
public sealed class AdofaiTimelineParser : IMapTimelineParser
{
    // Raise this whenever what is cached changes (the app then asks for a map sync); docs/adofai-parser.md has the evidence.
    public const string CurrentVersion = "adofai-v29-hit-windows";
    /// <summary>
    /// Level formats checked against the game and a real map each. The game reads the level version only for 7 (LoadLevel),
    /// 9 (editor MakeLevel), decoration components up to 12 and the level list (above 19), so none of these has a timing branch
    /// of its own. Version 19's legacy settings are visual (legacyTween/legacyFlash/legacyCamRelativeTo) or not read by the game
    /// (legacyPause, planetEasePartBehavior).
    /// <para>
    /// Format 9's branch is a one-time editor migration: scrLevelMaker.MakeLevel adds a beat to a Pause on a tile whose
    /// entry and exit angles agree modulo a quarter turn and which is not a whole turn, but only when LevelData.shouldTryMigrate
    /// is set and ADOBase.editor exists. There is no editor while a level is played, so a format 9 level is played with the
    /// durations the file holds, which is what is read here.
    /// </para>
    /// </summary>
    public static IReadOnlyList<int> FormatVersions { get; } = [2, 3, 4, 6, 8, 9, 10, 11, 13, 15, 18, 19];
    /// <summary>The game's margin (rad) around no turn and a whole circle; see the floor loop.</summary>
    private const double WholeTurnMargin = 1e-6;
    /// <summary>
    /// The game's own pi, as scrMisc.GetInverseAnglePerBeatMultiplanet holds it. It is a digit short of pi, and the
    /// whole turn margin is small enough to notice, so the difference is kept rather than rounded away.
    /// </summary>
    private const double GamePi = 3.1415926;
    /// <summary>The game's own two pi, from the floor loop's midspin branch; also a hair off.</summary>
    private const double GameTwoPi = 6.28318548202515;
    public string Version => CurrentVersion;
    private static readonly HashSet<string> VisualEvents = new(StringComparer.Ordinal)
    {
        "Bloom", "SetFilter", "MoveTrack", "AnimateTrack", "ColorTrack", "CustomBackground", "Flash",
        "Bookmark", "EditorComment", "MoveCamera", "RecolorTrack", "ShakeScreen", "SetPlanetRotation",
        "SetFilterAdvanced", "MoveDecorations", "ScalePlanets", "PlaySound", "SetHitsound", "AddDecoration",
        // Moves tile positions only: scnGame.ApplyEventsToFloors writes no timing field for it and
        // scrLevelMaker.CalculateFloorEntryTimes reads angles, speed and extra beats, never positions.
        "PositionTrack",
        // Screen and text effects (Search Party): ApplyCoreEventsToFloors never branches on their event types (21, 36, 37),
        // and ffxSetTextPlus/ffxScreenTilePlus/ffxScreenScrollPlus only change text decorations and screen tile/scroll values.
        "SetText", "ScreenTile", "ScreenScroll",
        // Event type 63 (야르송): no floor timing branch; ffxSetParticlePlus only changes tagged particle decorations.
        "SetParticle",
        // Event type 60 (雑魚): no branch in ApplyEventsToFloors or ApplyCoreEventsToFloors;
        // ffxSetDefaultText only changes HUD text color, shadow, level title and congrats/perfect text.
        "SetDefaultText",
        // ヤラララ. Types 27/34/58/59/61/64 are compared nowhere in scnGame.ApplyEventsToFloors, its core floor events or
        // scrLevelMaker (AddObject floors are decoration objects, not level floors). Hide (50) writes only hideJudgment/hideIcon (read by
        // death effects and tile icons); ScaleRadius (52) writes only radiusScale, which CalculateFloorEntryTimes never reads.
        "Hide", "ScaleRadius", "SetObject", "AddObject", "HallOfMirrors", "SetFrameRate", "EmitParticle", "SetHoldSound",
        // C†hulhu: LevelEventType 20 is only read by scrDecorationManager.CreateDecoration, which builds a text
        // decoration from "decText". Neither floor function compares it.
        "AddText",
        // Type 51: the core (timing) pass sends 51 to its default (its dispatch handles 2, 3, 33, 43, 44 and then
        // 48/52/53/54), and the visual pass only carries the value into scrFloor.marginScale. CalculateFloorEntryTimes
        // reads isCCW, midSpin, entryangle, exitangle, holdLength, numPlanets, extraBeats, speed and prevfloor, so the
        // hit window it scales cannot move a hit time.
        "ScaleMargin"
    };
    /// <summary>
    /// Tag values the game itself reads as "no conditional tag" (RDUtils.noneTags, compared with the default string comparer).
    /// A localized placeholder such as the Korean "없음" is not in that list, so it counts as a real tag.
    /// </summary>
    private static readonly string[] NoneTags = ["NONE", "无", "無", "AUCUN", "ŽÁDNÝ", "NICHTS"];
    private sealed class Unsupported(string message) : Exception(message);

    public MapParseResult Parse(RawMapDocument document) => Parse(document, null);

    /// <summary>
    /// load -force (user rule): an Unsupported level is read as a verified format below its own, the nearest first, then as
    /// the ones above; versions nobody verified are skipped. The first format that reads it wins and is kept in
    /// <see cref="MapParseResult.ForcedFormatVersion"/>. The format only gates the parser, so a level held back by anything
    /// else fails every format and comes back Unsupported with the reason.
    /// </summary>
    public MapParseResult ParseForced(RawMapDocument document)
    {
        var plain = Parse(document);
        if (plain.Status != MapParseStatus.Unsupported || FormatVersionOf(document) is not { } own) return plain;
        var order = FormatVersions.Where(version => version < own).Reverse().Concat(FormatVersions.Where(version => version > own)).ToArray();
        foreach (var version in order)
        {
            var forced = Parse(document, version);
            if (forced.Status == MapParseStatus.Supported)
                return forced with
                {
                    ForcedFormatVersion = version,
                    Warnings = forced.Warnings.Prepend($"강제 로드: 미검증 형식 version {own}을 version {version}으로 읽었습니다. 게임과 타이밍이 다를 수 있습니다.").ToArray()
                };
        }
        return plain with
        {
            Warnings = plain.Warnings.Append($"강제 로드 실패: 형식 version {string.Join("/", order)}로 읽어도 위 이유로 지원하지 않습니다.").ToArray()
        };
    }

    /// <summary>The level's own format version, or null when it cannot be read.</summary>
    private static int? FormatVersionOf(RawMapDocument document)
    {
        try
        {
            using var json = JsonDocument.Parse(AdofaiJson.NormalizeStrings(document.Text), new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return json.RootElement.GetProperty("settings").GetProperty("version").TryGetInt32(out var version) ? version : null;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { return null; }
    }

    /// <summary>Parses the level; <paramref name="assumedFormat"/>, from a forced load only, stands in for its own format.</summary>
    private MapParseResult Parse(RawMapDocument document, int? assumedFormat)
    {
        string? title = null;
        MapSongInfo? info = null;
        try
        {
            using var json = JsonDocument.Parse(AdofaiJson.NormalizeStrings(document.Text), new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = json.RootElement;
            var settings = root.GetProperty("settings");
            title = CleanText(settings, "artist") + " - " + CleanText(settings, "song");
            if (title == " - ") title = null;
            // Descriptive only (shown while stopped); a missing or non-numeric difficulty is simply unknown.
            info = new MapSongInfo(Optional(CleanText(settings, "artist")), Optional(CleanText(settings, "song")), Optional(CleanText(settings, "author")),
                settings.TryGetProperty("difficulty", out var difficulty) && difficulty.ValueKind == JsonValueKind.Number
                    && difficulty.TryGetInt32(out var level) ? level : null);
            var formatVersion = settings.GetProperty("version").GetInt32();
            if (assumedFormat is null && !FormatVersions.Contains(formatVersion))
                throw new Unsupported($"맵 형식 version {formatVersion}은 미검증입니다. 지원 형식은 {string.Join("/", FormatVersions)}이며 게임 앱 버전과는 다릅니다. 원본 version 값을 바꾸어 우회하지 마세요.");
            var environmentInfo = MapEnvironmentInfo.Describe(settings, formatVersion);
            if (settings.TryGetProperty("requiredMods", out var mods) && mods.GetArrayLength() > 0)
                throw new Unsupported("외부 모드가 필요한 맵은 지원하지 않습니다.");
            // The level's pitch plays the whole song faster or slower, so every real time is divided by it:
            // scrConductor.SetupConductorWithLevelData sets song.pitch = pitch * 0.01 (times GCS.currentSpeedTrial in game, which
            // is the game's own speed trial and not part of the map), and CalculateFloorEntryTimes keeps
            // entryTimePitchAdj = entryTime / song.pitch beside the beat-based entryTime. LevelData.get_pitch unboxes an int.
            var pitchValue = Number(settings, "pitch", 100);
            if (!double.IsFinite(pitchValue) || pitchValue != Math.Floor(pitchValue) || pitchValue <= 0)
                throw new Unsupported("pitch는 0보다 큰 정수여야 합니다.");
            var pitch = pitchValue / 100;
            if (Text(settings, "planetEase", "Linear") != "Linear")
                throw new Unsupported("기본 행성 easing이 Linear가 아닌 맵은 아직 지원하지 않습니다.");
            double[] angles;
            if (root.TryGetProperty("angleData", out var angleData))
                angles = angleData.EnumerateArray().Select(a => a.GetDouble()).ToArray();
            else if (root.TryGetProperty("pathData", out var pathData))
                angles = LegacyPathData.Decode(pathData.GetString() ?? "");
            else throw new Unsupported("angleData 또는 pathData가 필요합니다.");
            if (angles.Any(a => !double.IsFinite(a) || !float.IsFinite((float)a)))
                throw new Unsupported("각도는 게임의 숫자 범위 안에 있는 유한한 값이어야 합니다.");
            // Ordinary negative/multi-turn angles are directions; 999 alone is the midspin sentinel.
            angles = angles.Select(a => a == 999 ? a : Mod(a)).ToArray();
            // Any first direction: scrLevelMaker.InstantiateFloatFloors gives floor 0 the entry angle 3π/2, exactly what a floor
            // after a 0° direction gets (each exit is (90° − direction), the next entry that plus π), so the first turn is measured
            // like every other (S3RL - play it loud starts at 345°). A midspin there is not verified.
            if (angles.Length == 0 || angles[0] == 999)
                throw new Unsupported("첫 타일이 없거나 중간 회전인 맵은 지원하지 않습니다.");
            var allActions = root.GetProperty("actions").EnumerateArray().ToArray();
            // scnGame.ApplyEventsToFloors files an event under its floor only while LevelEvent.active is set, and LevelEvent.Decode
            // clears it only for a JSON false. So an event switched off in the editor never reaches the timing code: the
            // SetSpeed switched off at floor 529 of いますぐ輪廻 counted here put every later note late. Tag lookups below still
            // see those events, so a tag that could pick a timing event is refused either way.
            var actions = allActions.Where(IsActive).ToArray();
            var holds = new Dictionary<int, int>();
            var freeRoams = new Dictionary<int, int>();
            var pauseFloors = new HashSet<int>();
            foreach (var action in actions)
            {
                var type = Text(action, "eventType");
                var floor = action.GetProperty("floor").GetInt32();
                if (floor < 0 || floor > angles.Length) throw new Unsupported($"유효하지 않은 이벤트 floor: {floor}");
                if (type is "SetSpeed" or "Twirl" or "Pause")
                {
                    if (Number(action, "angleOffset", 0) != 0)
                        throw new Unsupported($"{type}의 0이 아닌 angleOffset은 지원하지 않습니다 (floor {floor}).");
                    if (action.TryGetProperty("enabled", out var enabled) && enabled.ValueKind != JsonValueKind.True)
                        throw new Unsupported("조건부 timing 이벤트는 지원하지 않습니다.");
                    if (type == "Pause")
                    {
                        pauseFloors.Add(floor);
                        var beats = Number(action, "duration");
                        if (!double.IsFinite(beats) || beats < 0 || floor == 0 || floor == angles.Length || angles[floor] == 999)
                            throw new Unsupported("Pause는 일반 중간 타일의 유한한 0 이상 duration만 지원합니다.");
                        // The direction only turns the planet's drawn angle: ApplyCoreEventsToFloors stores it in
                        // scrFloor.angleCorrectionType, which CalculateFloorEntryTimes never reads, and scrPlanet.HandlePause
                        // sets lastHit and the snapped angle the same way for every direction and multiplies it into
                        // TweenCosmeticAngle only. So every defined direction keeps the same timing (map: 柿崎ユウタ - 月が綺麗ねと言われたい！).
                        if (!IsCorrectionDirection(action))
                            throw new Unsupported("Pause angleCorrectionDir는 Backward·None·Forward만 지원합니다.");
                    }
                }
                else if (type == "Hold")
                {
                    // ApplyCoreEventsToFloors sets holdLength = duration when a next floor exists and duration >= 0, otherwise -1.
                    // CalculateFloorEntryTimes adds holdLength×2 beats only when holdLength > 0, and scrPlayer.UpdateHoldBehavior
                    // skips only holdLength -1, so duration 0 is a long note to the next hit without extra time, released there.
                    // A midspin in between does not change it: the game hits it itself and carries the hold on (see the release check below).
                    if (!action.TryGetProperty("duration", out var duration) || duration.ValueKind != JsonValueKind.Number
                        || !duration.TryGetInt32(out var length) || length < 0)
                        throw new Unsupported($"Hold는 0 이상의 정수 duration만 지원합니다 (floor {floor}).");
                    if (floor == 0 || floor >= angles.Length || angles[floor] == 999)
                        throw new Unsupported($"Hold는 일반 중간 타일에서만 지원합니다 (floor {floor}).");
                    if (!holds.TryAdd(floor, length)) throw new Unsupported($"한 타일에 여러 Hold는 지원하지 않습니다 (floor {floor}).");
                }
                else if (type == "FreeRoam")
                {
                    // ApplyCoreEventsToFloors (event 44): with a next floor and an integer duration of at least 2, extraBeats grows
                    // by Max(a, duration - a), a being the floor's turn in beats, so the next hit comes `duration` beats after this
                    // one. scrController.UpdateFreeroam then hands the planet back to the path, and the next floor is hit as usual.
                    // The moves inside the grid are not hits of the path, so the section itself has no notes, whether the game
                    // plays it (an auto floor) or the player moves through it by hand. extraBeats is the same either way:
                    // event 44 in the core pass never looks at the floor's auto flag.
                    if (!action.TryGetProperty("duration", out var roam) || roam.ValueKind != JsonValueKind.Number
                        || !roam.TryGetInt32(out var roamBeats) || roamBeats < 2)
                        throw new Unsupported($"FreeRoam은 2 이상의 정수 duration만 지원합니다 (floor {floor}).");
                    if (floor == 0 || floor >= angles.Length || angles[floor] == 999 || (floor + 1 < angles.Length && angles[floor + 1] == 999))
                        throw new Unsupported($"FreeRoam은 다음 타일이 일반 타일인 중간 타일에서만 지원합니다 (floor {floor}).");
                    if (!IsBackward(action))
                        throw new Unsupported("FreeRoam angleCorrectionDir는 Backward만 검증했습니다.");
                    if (!freeRoams.TryAdd(floor, roamBeats)) throw new Unsupported($"한 타일에 여러 FreeRoam은 지원하지 않습니다 (floor {floor}).");
                }
                else if (type == "AutoPlayTiles")
                {
                    // ApplyEventsToFloors keeps the latest "enabled" and copies it to every following floor (scrFloor.auto).
                    // The editor writes a toggle as "Enabled"/"Disabled"; a missing one would take the event definition's
                    // default, which is not read here, so it stays unsupported.
                    if (!action.TryGetProperty("enabled", out var autoEnabled)
                        || autoEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.String))
                        throw new Unsupported($"AutoPlayTiles는 enabled 값이 필요합니다 (floor {floor}).");
                }
                else if (type == "SetConditionalEvents")
                {
                    // LevelEventType 35 is compared only in a filter inside scnGame.ApplyEventsToFloors (the visual pass) and in
                    // PrepVfx. It sets scrFloor.hasConditionalChange and fills the per-judgment effect lists that
                    // scnGame.GetFloorConditionalEvents collects (perfect/early/late/loss/checkpoint ...). ApplyCoreEventsToFloors and
                    // scrLevelMaker never compare it, so hit times are unchanged. As with RepeatEvents, the events those tags pick
                    // still have to be visual only (Plum - Timeline(remake)).
                    var conditional = action.EnumerateObject()
                        .Where(property => property.Name.EndsWith("Tag", StringComparison.Ordinal) && property.Name != "eventTag"
                            && property.Value.ValueKind == JsonValueKind.String)
                        .SelectMany(property => (property.Value.GetString() ?? "").Split(' '))
                        .Where(tag => tag.Length > 0 && !NoneTags.Contains(tag, StringComparer.Ordinal))
                        .ToArray();
                    foreach (var target in allActions)
                    {
                        if (!target.TryGetProperty("eventTag", out var conditionalTag) || conditionalTag.ValueKind != JsonValueKind.String) continue;
                        if (conditional.Intersect((conditionalTag.GetString() ?? "").Split(' '), StringComparer.Ordinal).Any()
                            && !VisualEvents.Contains(Text(target, "eventType")))
                            throw new Unsupported("\ud0c0\uc774\ubc0d/\uccb4\ud06c\ud3ec\uc778\ud2b8/\ubc18\ubcf5 \uc774\ubca4\ud2b8\ub97c \ub300\uc0c1\uc73c\ub85c \ud55c SetConditionalEvents\ub294 \uc9c0\uc6d0\ud558\uc9c0 \uc54a\uc2b5\ub2c8\ub2e4.");
                    }
                }
                else if (type == "RepeatEvents")
                {
                    // The game repeats only events that carry an eventTag property, splitting on ' ' without
                    // dropping empty entries, so an empty tag can match an empty eventTag. Any possible
                    // timing, checkpoint or repeat target is still rejected.
                    var tags = Text(action, "tag", "").Split(' ');
                    foreach (var target in allActions)
                    {
                        if (!target.TryGetProperty("eventTag", out var eventTag) || eventTag.ValueKind != JsonValueKind.String) continue;
                        if (tags.Intersect((eventTag.GetString() ?? "").Split(' '), StringComparer.Ordinal).Any()
                            && !VisualEvents.Contains(Text(target, "eventType")))
                            throw new Unsupported("타이밍/체크포인트/반복 이벤트를 대상으로 한 RepeatEvents는 지원하지 않습니다.");
                    }
                }
                else if (type == "SetInputEvent")
                {
                    // LevelEventType 65 is compared in neither floor pass nor scrLevelMaker. ffxSetInputEventPlus.StartEffect only files
                    // itself in scrController.inputEventFfx[state][target], and its PrepVfx takes the floor's effects whose eventTag
                    // (split on ' ') holds targetEventTag out of the floor's run, for scrPlayer.HitInputEvent to start when that key
                    // goes down or up. scrPlayer.Hit drops a press only when HitInputEvent returns false, which takes ignoreInput on a
                    // Down slot, so with it off every hit is as before (句るり - 忘れもの注意: arrow keys switch the lyrics).
                    // Decode reads ignoreInput with GetBool; a missing one would take the event definition's default, not read here.
                    if (!action.TryGetProperty("ignoreInput", out var ignoreInput)
                        || ignoreInput.ValueKind is not (JsonValueKind.False or JsonValueKind.String) || Toggle(action, "ignoreInput"))
                        throw new Unsupported($"SetInputEvent는 ignoreInput이 꺼진 것만 지원합니다 (floor {floor}).");
                    // Like the tags above, the events a key starts must be visual only, looked up on every floor.
                    var inputTag = Text(action, "targetEventTag", "");
                    foreach (var target in allActions)
                    {
                        if (!target.TryGetProperty("eventTag", out var inputEventTag) || inputEventTag.ValueKind != JsonValueKind.String) continue;
                        if ((inputEventTag.GetString() ?? "").Split(' ').Contains(inputTag, StringComparer.Ordinal)
                            && !VisualEvents.Contains(Text(target, "eventType")))
                            throw new Unsupported("타이밍/체크포인트/반복 이벤트를 대상으로 한 SetInputEvent는 지원하지 않습니다.");
                    }
                }
                else if (type == "MultiPlanet")
                {
                    // A timing event like the three above: it moves the entry angle of every floor from here on, so it is
                    // read in the floor loop. Anything but two or three planets is refused there.
                    _ = Planets(action);
                }
                else if (type != "Checkpoint" && !VisualEvents.Contains(type))
                    throw new Unsupported($"지원하지 않는 이벤트: {type} (floor {floor}).");
            }
            if (freeRoams.Keys.Any(floor => holds.ContainsKey(floor) || pauseFloors.Contains(floor)))
                throw new Unsupported("같은 타일의 Hold·Pause와 FreeRoam은 검증하지 않았습니다.");

            var bpm = Number(settings, "bpm");
            RequireBpm(bpm);
            var byFloor = actions.GroupBy(a => a.GetProperty("floor").GetInt32()).ToDictionary(g => g.Key, g => g.ToArray());
            var entryTimes = new double[angles.Length + 1];
            var clockwise = true;
            var previousDirection = 0d;
            // scnGame.ApplyEventsToFloors writes floor.numPlanets at the end of each floor's events, so the count carries
            // to the floors after it exactly as the speed does. PlanetCount is { TwoPlanets = 2, ThreePlanets = 3 }.
            var planets = 2;
            // The hit windows (HitGrading): the tempo each tile turns at, and the margin scale. scnGame.ApplyEventsToFloors
            // starts the scale at 1, sets it to "scale" / 100 on a ScaleMargin and writes it into every floor after that floor's
            // events, as it does the speed; the game grades a hit with the tile it lands on (scrPlayer.get__marginScale reads
            // currFloor.nextfloor) and the speed of the tile it turns from (planetarySystem.speed).
            var floorTempos = new double[angles.Length + 1];
            var floorScales = new double[angles.Length + 1];
            var marginScale = 1d;
            for (var floor = 0; floor < angles.Length; floor++)
            {
                var extraBeats = 0d;
                if (byFloor.TryGetValue(floor, out var events))
                {
                    foreach (var action in events)
                    {
                        switch (Text(action, "eventType"))
                        {
                            case "Twirl": clockwise = !clockwise; break;
                            case "MultiPlanet": planets = Planets(action); break;
                            case "Pause": extraBeats += Number(action, "duration"); break;
                            // Below zero the fixed angles drop out and the minimum times are the windows, as at zero.
                            case "ScaleMargin": marginScale = Math.Max(0, Number(action, "scale", 100) / 100); break;
                            case "SetSpeed":
                                bpm = Text(action, "speedType", "Bpm") switch
                                {
                                    "Bpm" => Number(action, "beatsPerMinute"),
                                    "Multiplier" => bpm * Number(action, "bpmMultiplier"),
                                    _ => throw new Unsupported("알 수 없는 speedType입니다.")
                                };
                                RequireBpm(bpm);
                                break;
                        }
                    }
                }
                floorTempos[floor] = bpm;
                floorScales[floor] = marginScale;
                double degrees;
                if (angles[floor] == 999)
                {
                    degrees = 0;
                    previousDirection = Mod(previousDirection + 180);
                }
                else
                {
                    // More than two planets moves the entry angle before the turn is measured, which makes every tile of
                    // the section shorter (cosMo@暴走P - THE END OF HATSUNE MIKU, floors 310-324).
                    var shift = PlanetShift(planets, floor > 0 && angles[floor - 1] == 999);
                    if (floor == 0 && shift != 0)
                        throw new Unsupported("첫 타일부터 3행성인 맵은 검증하지 않았습니다.");
                    degrees = Mod((180 + previousDirection - angles[floor]) * (clockwise ? 1 : -1) + shift);
                    // scrLevelMaker.CalculateSingleFloorAngleLength measures the turn in radians and makes any turn within 1e-6 rad
                    // of none or of a whole circle a whole circle (2π). A 3.2e-6° tile after a midspin is such a turn
                    // (Tanchiky - ENERGY SYNERGY MATRIX); a threshold in degrees played it as no time at all.
                    var radians = degrees * Math.PI / 180;
                    if (radians <= WholeTurnMargin || !(radians < 2 * Math.PI - WholeTurnMargin)) degrees = 360;
                    previousDirection = angles[floor];
                }
                if (freeRoams.TryGetValue(floor, out var freeRoamBeats))
                {
                    // The game measures that turn before twirls are applied (the angle lengths come first in ApplyEventsToFloors),
                    // so only a turn that is the same either way round, 180° or a whole circle, is verified.
                    if (Math.Abs(degrees - 180) > 1e-9 && degrees != 360)
                        throw new Unsupported($"FreeRoam 타일의 회전이 180°·360°가 아니면 검증하지 않았습니다 (floor {floor}).");
                    var turn = degrees / 180;
                    extraBeats += Math.Max(turn, freeRoamBeats - turn);
                }
                var holdBeats = holds.TryGetValue(floor, out var holdLength) ? 2d * holdLength : 0;
                entryTimes[floor + 1] = entryTimes[floor] + (degrees / 180 + holdBeats + extraBeats) * 60000 / bpm;
                if (!double.IsFinite(entryTimes[floor + 1])) throw new Unsupported("타이밍 계산 범위를 초과했습니다.");
            }
            // Floor 0 is a start marker; midspin floors do not require a hit.
            var hitFloors = Enumerable.Range(1, angles.Length)
                .Where(floor => floor == angles.Length || angles[floor] != 999).ToArray();
            // Real time, as the player hears and plays it: the beat-based times divided by the level's pitch.
            if (pitch != 1)
                for (var floor = 0; floor < entryTimes.Length; floor++) entryTimes[floor] /= pitch;
            var hitTimes = hitFloors.Select(floor => entryTimes[floor]).ToArray();
            // The end tile is not turned from, but a ScaleMargin on it still sets its own scale.
            if (byFloor.TryGetValue(angles.Length, out var endEvents))
                foreach (var action in endEvents.Where(a => Text(a, "eventType") == "ScaleMargin"))
                    marginScale = Math.Max(0, Number(action, "scale", 100) / 100);
            floorScales[angles.Length] = marginScale;
            var hitTempos = hitFloors.Select(floor => floorTempos[floor - 1] * pitch).ToArray();
            var hitScales = hitFloors.Select(floor => floorScales[floor]).ToArray();
            // Auto state per floor: the latest AutoPlayTiles "enabled", events of a floor applying to that floor.
            var auto = new bool[angles.Length + 1];
            var autoOn = false;
            for (var floor = 0; floor <= angles.Length; floor++)
            {
                if (byFloor.TryGetValue(floor, out var events))
                    foreach (var action in events.Where(a => Text(a, "eventType") == "AutoPlayTiles"))
                        autoOn = Toggle(action, "enabled");
                auto[floor] = autoOn;
            }
            // A hold on an auto tile stays unverified. A hold into an auto tile is only a press: scrPlayer.UpdateHoldBehavior skips
            // the release when the next tile is auto (no hit, no fail), and the game plays that tile itself.
            if (holds.Keys.Any(floor => auto[floor]))
                throw new Unsupported("자동 타일과 겹친 Hold는 검증하지 않았습니다.");
            // A hold is released on the next tile that is pressed, which a midspin in between does not change: scrPlayer.Hit
            // sets midspinInfiniteMargin when the floor it lands on is a midspin, and scrPlayer.UpdateHoldKeys then hits once
            // more straight away, so the held key carries over it (cosMo@暴走P - THE END OF HATSUNE MIKU, floor 644).
            // With nothing left to press there is no release to show.
            if (holds.Keys.Any(floor => !hitFloors.Any(hit => hit > floor)))
                throw new Unsupported("뗄 타일이 없는 Hold는 지원하지 않습니다.");
            // A hold that both releases the one before it and ends on an auto tile would be a press with no release to
            // show, which no hit kind describes, so it stays unverified.
            for (var index = 1; index < hitFloors.Length - 1; index++)
                if (holds.ContainsKey(hitFloors[index]) && holds.ContainsKey(hitFloors[index - 1]) && auto[hitFloors[index + 1]])
                    throw new Unsupported($"자동 타일에서 끝나는 연속 Hold는 검증하지 않았습니다 (floor {hitFloors[index]}).");
            // A hit that both ends the hold before it and starts the next one is pressed in its own right: UpdateHoldKeys
            // takes the queued press as soon as the next tile is a hold, without waiting for holdCompletion.
            var hitKinds = hitFloors.Select((floor, index) =>
            {
                var releases = index > 0 && holds.ContainsKey(hitFloors[index - 1]);
                if (auto[floor]) return HitKind.Auto;
                if (holds.ContainsKey(floor) && !auto[hitFloors[index + 1]]) return releases ? HitKind.HoldChain : HitKind.HoldStart;
                return releases ? HitKind.HoldEnd : HitKind.Tap;
            }).ToArray();
            var warnings = new List<string>
            {
                environmentInfo,
                "두 행성/angleData·pathData/즉시 BPM 변경/회전 반전/중간 회전/Pause/Hold/자동 타일/FreeRoam 구간 지원. 게임 실행 시각과의 종단 간 대조는 아직 수행하지 않았습니다.",
                "원문 offset·카운트다운은 J로 시작하는 첫 hit 기준 정규화에서 제외합니다. 오디오는 재생하지 않습니다."
            };
            if (freeRoams.Count > 0)
                warnings.Add($"FreeRoam 구간 {freeRoams.Count}개({string.Join(", ", freeRoams.Select(pair => $"floor {pair.Key} {pair.Value}박" + (auto[pair.Key] ? " 자동" : " 직접 조작")))}): 구간 동안은 노트가 없습니다. 자동 구간은 게임이 움직이고, 직접 조작 구간은 사용자가 자유롭게 움직이므로 그 움직임은 트랙의 hit이 아닙니다.");
            if (holds.Count > 0 || hitKinds.Contains(HitKind.Auto))
                warnings.Add($"롱노트(Hold) {holds.Count}개, 자동 타일 hit {hitKinds.Count(kind => kind == HitKind.Auto)}개: 자동 타일은 노트로 표시하지 않고, Hold 다음 hit는 롱노트를 떼는 시점입니다.");
            var checkpoints = new List<TimelineCheckpoint> { new(1, 0) };
            var latestRespawn = 0;
            foreach (var action in actions.Where(a => Text(a, "eventType") == "Checkpoint")
                .OrderBy(a => a.GetProperty("floor").GetInt32()))
            {
                var floor = action.GetProperty("floor").GetInt32();
                var offset = action.TryGetProperty("tileOffset", out var value) ? value.GetInt32() : 0;
                if (Number(action, "angleOffset", 0) != 0 || action.TryGetProperty("enabled", out var enabled) && enabled.ValueKind != JsonValueKind.True)
                    throw new Unsupported("조건부/각도 오프셋 Checkpoint는 지원하지 않습니다.");
                // Verified against installed ffxCheckpoint.Decode / StartEffect (see docs/checkpoints.md).
                var respawn = (int)Math.Clamp((long)floor + offset, 0, angles.Length - 1L);
                if (respawn <= latestRespawn) continue; // Game only advances its saved respawn floor.
                // A checkpoint on a midspin tile still fires: scrPlanet.MoveToNextFloor runs a floor's effects when the planet
                // moves onto it (it reads scrFloor.midSpin itself), and ffxCheckpoint.StartEffect saves floor.seqID + tileOffset.
                // The restart tile must be a normal one, since the run starts from the first hit at or after it.
                if (floor == 0 || floor == angles.Length || angles[respawn] == 999)
                    throw new Unsupported("시작/종료 타일의 Checkpoint 또는 중간 회전 타일로의 재시작은 아직 검증하지 않았습니다.");
                var index = Array.FindIndex(hitFloors, hitFloor => hitFloor > respawn);
                if (index < 0) throw new Unsupported("Checkpoint 이후 타격 노트가 없습니다.");
                if (hitKinds[index] == HitKind.HoldEnd) throw new Unsupported("롱노트 도중의 Checkpoint 재시작은 검증하지 않았습니다.");
                if (freeRoams.ContainsKey(respawn)) throw new Unsupported("FreeRoam 타일로의 Checkpoint 재시작은 검증하지 않았습니다.");
                checkpoints.Add(new(checkpoints.Count + 1, index, entryTimes[floor], floor, respawn));
                latestRespawn = respawn;
            }
            // The mini-game band: from the auto tile that starts it to the next hit, which is where notes come back.
            var sections = freeRoams.Keys.Order().Select(floor => new TimelineSection(entryTimes[floor], entryTimes[floor + 1])).ToArray();
            return new(MapParseStatus.Supported, new ParsedTimeline(hitTimes, checkpoints, true, hitFloors,
                hitKinds.All(kind => kind == HitKind.Tap) ? null : hitKinds, sections, hitTempos,
                hitScales.All(scale => scale == 1) ? null : hitScales), title, warnings.AsReadOnly(), info);
        }
        catch (Unsupported error) { return new(MapParseStatus.Unsupported, null, title, new[] { error.Message }, info); }
        catch (NotSupportedException error) { return new(MapParseStatus.Unsupported, null, title, new[] { error.Message }, info); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { return new(MapParseStatus.Error, null, title, new[] { "맵 데이터 검증 실패: " + error.Message }, info); }
    }

    private static string? Optional(string text) => text.Length == 0 ? null : text;

    private static double Mod(double value) => (value % 360 + 360) % 360;

    /// <summary>
    /// How many degrees a tile's turn changes by with <paramref name="planets"/> planets.
    ///
    /// scrLevelMaker.CalculateFloorEntryTimes measures the turn from <c>entryangle + shift</c>, where
    /// <c>shift = scrMisc.GetInverseAnglePerBeatMultiplanet(numPlanets) * (isCCW ? -1 : 1)</c> and that function is
    /// <c>pi * (n - 2) / n</c>. The direction cancels, and the next planet is that much nearer: about 60° shorter for
    /// three planets, either way round. The game's own tile times say so (THE END OF HATSUNE MIKU over the sync pipe:
    /// 180° tiles take 120°, 90° take 30°, 270° take 210°, clockwise and counterclockwise alike).
    ///
    /// After a midspin tile the game instead moves the entry a whole turn back (<c>shift -= (2pi + inverse) * dir</c>),
    /// so the planets' 60° cancels and the turn is as with two planets plus the game's slightly large two pi — kept as
    /// the game has it, since the whole-turn margin can see it.
    /// </summary>
    private static double PlanetShift(int planets, bool afterMidSpin)
    {
        if (planets <= 2) return 0;
        return afterMidSpin ? Degrees(GameTwoPi) : -Degrees(GamePi * (planets - 2) / planets);
    }

    private static double Degrees(double radians) => radians * 180 / Math.PI;

    /// <summary>
    /// The planet count a MultiPlanet event asks for. The level file writes the PlanetCount name, which the loader hands
    /// over as its number (TwoPlanets = 2, ThreePlanets = 3); the game then clamps it to 2..3.
    /// </summary>
    private static int Planets(JsonElement action)
    {
        if (!action.TryGetProperty("planets", out var value)) return 2;
        var count = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt32(),
            JsonValueKind.String => value.GetString() switch
            {
                "TwoPlanets" => 2,
                "ThreePlanets" => 3,
                var other => throw new Unsupported($"알 수 없는 행성 수입니다: {other}")
            },
            _ => throw new Unsupported("MultiPlanet의 planets 값을 읽을 수 없습니다.")
        };
        return Math.Clamp(count, 2, 3);
    }
    private static void RequireBpm(double bpm)
    { if (!double.IsFinite(bpm) || bpm <= 0) throw new Unsupported("BPM은 유한한 양수여야 합니다."); }
    private static double Number(JsonElement e, string name, double? fallback = null) =>
        e.TryGetProperty(name, out var value) ? value.GetDouble() : fallback ?? throw new KeyNotFoundException(name);
    private static string Text(JsonElement e, string name, string? fallback = null) =>
        e.TryGetProperty(name, out var value) ? value.GetString() ?? "" : fallback ?? throw new KeyNotFoundException(name);
    private static bool IsActive(JsonElement action) =>
        !action.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.False;
    /// <summary>
    /// A toggle property as the game reads it. LevelEvent.Decode holds a PropertyType 1 value as a bool, turning the
    /// string "Enabled" into true and every other string into false, and LevelEvent.GetBool hands that bool back (a value
    /// of another type gives GetBool's own false). A JSON true/false is kept as it is.
    /// </summary>
    private static bool Toggle(JsonElement action, string name) => action.TryGetProperty(name, out var value)
        && (value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String && value.GetString() == "Enabled"));

    /// <summary>
    /// A defined AngleCorrectionDirection (Backward -1, None 0, Forward 1) or none at all. LevelEvent.Decode reads an enum
    /// value by name (Enum.Parse) or by number (Enum.ToObject).
    /// </summary>
    private static bool IsCorrectionDirection(JsonElement action) =>
        !action.TryGetProperty("angleCorrectionDir", out var value)
        || value.ValueKind == JsonValueKind.String && value.GetString() is "Backward" or "None" or "Forward"
        || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number is -1 or 0 or 1;

    private static bool IsBackward(JsonElement action) =>
        !action.TryGetProperty("angleCorrectionDir", out var value)
        || value.ValueKind == JsonValueKind.String && value.GetString() == "Backward"
        || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number == -1;
    private static string CleanText(JsonElement e, string name) => Regex.Replace(Text(e, name, ""), "<[^>]*>", "").Trim();
}
