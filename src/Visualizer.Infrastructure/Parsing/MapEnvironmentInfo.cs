using System.Globalization;
using System.Text.Json;

namespace Visualizer.Infrastructure.Parsing;

/// <summary>Source metadata for diagnostics, not a guessed loading/countdown formula.</summary>
internal static class MapEnvironmentInfo
{
    internal static string Describe(JsonElement settings, int version)
    {
        string Numeric(string name, bool nonnegativeInteger = false)
        {
            if (!settings.TryGetProperty(name, out var field)) return "미지정";
            if (field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out var value) || !double.IsFinite(value)
                || nonnegativeInteger && (value < 0 || value > int.MaxValue || Math.Truncate(value) != value))
                throw new JsonException($"{name} 값이 유효하지 않습니다.");
            return value.ToString("G", CultureInfo.InvariantCulture);
        }
        var offset = Numeric("offset");
        var ticks = Numeric("countdownTicks", true);
        var separate = "미지정";
        if (settings.TryGetProperty("separateCountdownTime", out var flag))
            separate = flag.ValueKind switch
            {
                JsonValueKind.True => "Enabled",
                JsonValueKind.False => "Disabled",
                JsonValueKind.String when flag.GetString() is "Enabled" or "Disabled" => flag.GetString()!,
                _ => throw new JsonException("separateCountdownTime은 bool 또는 Enabled/Disabled여야 합니다.")
            };
        return $"맵 형식 version {version} (게임 앱 버전 아님), offset={offset}ms, countdownTicks={ticks}, separateCountdownTime={separate}. "
            + "시작 대기는 자동 추정하지 않습니다. 첫 타격에 시작하면 0ms, 그보다 먼저 시작하면 setting의 시작 대기 시간을 수동 지정하세요.";
    }
}
