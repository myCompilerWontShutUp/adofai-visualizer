using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Visualizer.Infrastructure.Input;

/// <summary>
/// Puts the calling thread in the Windows multimedia scheduling class (MMCSS), so the frame loop keeps its turn while a
/// game is running. It changes how this process's own thread is scheduled and nothing else: no injection, no reading of
/// another process, no key handling. The handle belongs to the thread that took it, so it is released from the same one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MultimediaScheduling : IDisposable
{
    private nint _handle;

    private MultimediaScheduling(nint handle, string task, string? problem)
    {
        _handle = handle;
        Task = task;
        Problem = problem;
    }

    /// <summary>Whether the thread is in the class; false leaves everything exactly as it was.</summary>
    public bool Active => _handle != 0;
    /// <summary>The scheduling task asked for ("Games").</summary>
    public string Task { get; }
    /// <summary>Why it did not happen, or null. Windows without the MMCSS service simply refuses.</summary>
    public string? Problem { get; }

    /// <summary>
    /// Registers the calling thread. Never throws: a refusal comes back as <see cref="Problem"/>, since the app runs the
    /// same either way and only the frame arrival may be less even.
    /// </summary>
    public static MultimediaScheduling Register(string task = "Games")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        uint index = 0;
        var handle = AvSetMmThreadCharacteristics(task, ref index);
        return handle != 0
            ? new MultimediaScheduling(handle, task, null)
            : new MultimediaScheduling(0, task, new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    public void Dispose()
    {
        if (_handle == 0) return;
        AvRevertMmThreadCharacteristics(_handle);
        _handle = 0;
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    private static extern nint AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(nint handle);
}
