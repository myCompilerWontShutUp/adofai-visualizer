using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Visualizer.Infrastructure.Input;

/// <summary>
/// Reads only the foreground window's process image name. Lane keys query this on every press,
/// so the result is cached per foreground window instead of enumerating processes each time.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsForegroundObserver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private static readonly long CacheDuration = Stopwatch.Frequency * 2;
    private static nint _cachedWindow;
    private static uint _cachedProcessId;
    private static string? _cachedName;
    private static long _cachedAt;

    public static bool IsProcessForeground(string processName)
    {
        var name = ForegroundProcessName();
        return name is not null && string.Equals(name, NormalizeProcessName(processName), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when a window of this process (the visualizer or one of its dialogs) is in the foreground. Opens no process handle.</summary>
    public static bool IsCurrentProcessForeground()
    {
        var window = GetForegroundWindow();
        return window != 0 && GetWindowThreadProcessId(window, out var id) != 0 && id == (uint)Environment.ProcessId;
    }

    public static string NormalizeProcessName(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;

    private static string? ForegroundProcessName()
    {
        var window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out var id) == 0 || id == 0) return null;
        var now = Stopwatch.GetTimestamp();
        if (window == _cachedWindow && id == _cachedProcessId && now - _cachedAt < CacheDuration) return _cachedName;
        _cachedName = QueryProcessName(id);
        _cachedWindow = window;
        _cachedProcessId = id;
        _cachedAt = now;
        return _cachedName;
    }

    private static string? QueryProcessName(uint processId)
    {
        // Limited query rights also work for elevated games; no memory is read.
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? Path.GetFileNameWithoutExtension(new string(buffer, 0, (int)size))
                : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] name, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
