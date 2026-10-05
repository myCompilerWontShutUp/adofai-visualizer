namespace Visualizer.Infrastructure.Parsing;

/// <summary>Absolute path symbols verified from ADOFAI.FloorHelper.TryGetPathInDegrees.</summary>
internal static class LegacyPathData
{
    // In increasing 15-degree steps. Case is significant; ! is a midspin, not an angle.
    private const string AbsoluteSymbols = "RpJEToUqGQHWLxNZFVDYBCMA";

    public static double[] Decode(string path)
    {
        var angles = new double[path.Length];
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '!') { angles[i] = 999; continue; }
            var index = AbsoluteSymbols.IndexOf(path[i]);
            if (index < 0)
                throw new NotSupportedException($"pathData의 미지원 문자 '{path[i]}' (위치 {i}). 상대 각도 문자는 아직 지원하지 않습니다.");
            angles[i] = index * 15;
        }
        return angles;
    }
}
