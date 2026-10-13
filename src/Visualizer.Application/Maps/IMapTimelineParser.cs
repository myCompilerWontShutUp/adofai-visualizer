using Visualizer.Core.Maps;

namespace Visualizer.Application.Maps;

/// <summary>Reads a level into a timeline by verified timing rules only; anything else comes back Unsupported.</summary>
public interface IMapTimelineParser
{
    string Version { get; }
    MapParseResult Parse(RawMapDocument document);
    /// <summary>
    /// load -force (user rule): reads an Unsupported level as an older verified format. Anything else comes back as
    /// <see cref="Parse"/> gives it, which is all a parser without verified formats does.
    /// </summary>
    MapParseResult ParseForced(RawMapDocument document) => Parse(document);
}

public enum MapParseStatus { Supported, Unsupported, Error }

/// <summary>Descriptive level settings shown to the user; never used for timing. Missing values are null.</summary>
public sealed record MapSongInfo(string? Artist, string? Song, string? Author, int? Difficulty);

/// <param name="ForcedFormatVersion">The verified format a forced load read the level as; null for an ordinary parse.</param>
public sealed record MapParseResult(MapParseStatus Status, ParsedTimeline? Timeline,
    string? SongName, IReadOnlyList<string> Warnings, MapSongInfo? Info = null, int? ForcedFormatVersion = null);
