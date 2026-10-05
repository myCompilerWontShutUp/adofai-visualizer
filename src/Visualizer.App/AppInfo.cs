using System.Reflection;

namespace Visualizer.App;

public static class AppInfo
{
    /// <summary>Major.minor.patch from the build's Version property (Directory.Build.props).</summary>
    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version is { } version
        ? $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}"
        : "0.0.0";

    /// <summary>
    /// When this build was made, taken from the file the app runs out of. The build is deterministic, so the stamp in the
    /// file header is a hash rather than a time; the file's own time is the honest one.
    /// </summary>
    public static string BuildTime { get; } = Built();

    private static string Built()
    {
        try
        {
            var path = typeof(AppInfo).Assembly.Location;
            return path.Length > 0
                ? System.IO.File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : "unknown";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { return "unknown"; }
    }

    /// <summary>The date the version was applied, from the build's VersionDate property (Directory.Build.props).</summary>
    public static string VersionDate { get; } = typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "VersionDate")?.Value is { Length: > 0 } date ? date : "unknown";
}
