using Visualizer.Application.Maps;
using Visualizer.Core.Maps;

namespace Visualizer.Infrastructure.Parsing;

/// <summary>Explicit unsupported result until ADOFAI rules and fixtures are verified.</summary>
public sealed class UnsupportedMapTimelineParser : IMapTimelineParser
{
    public string Version => "adofai-unsupported-v1";
    public MapParseResult Parse(RawMapDocument document) => new(MapParseStatus.Unsupported, null, null,
        Array.AsReadOnly(new[] { "검증된 ADOFAI 타이밍 파서가 없습니다. 타임라인·체크포인트를 추측하지 않으며 재생할 수 없습니다." }));
}
