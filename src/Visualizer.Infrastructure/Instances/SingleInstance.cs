using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Visualizer.Infrastructure.Instances;

/// <summary>
/// One visualizer at a time in a Windows session (user request). Every copy of the app shares one store and the game link
/// pipe has a single name, so a second app started beside the first got neither: the game stayed linked to the window
/// started first while link auto was typed into the new one (user report: link auto did nothing). The name is the same for
/// every copy for that reason.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SingleInstance : IDisposable
{
    public const string Name = @"Local\AdofaiVisualizer.App";
    private const int RestoreWindow = 9;

    private readonly Mutex _mutex;
    private bool _disposed;

    /// <summary>Takes the name for this app; <see cref="First"/> says whether it was free.</summary>
    public SingleInstance(string name = Name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _mutex = new Mutex(initiallyOwned: true, name, out var created);
        First = created;
    }

    /// <summary>Whether no other visualizer held the name: false means one is running already.</summary>
    public bool First { get; }

    /// <summary>
    /// Puts the visualizer that is already running in front, restored if it was minimized. False when no window of it was
    /// found (it may still be starting, and then it shows itself).
    /// </summary>
    public static bool BringOtherToFront()
    {
        using var self = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName(self.ProcessName);
        try
        {
            foreach (var other in others)
            {
                if (other.Id == self.Id) continue;
                var window = other.MainWindowHandle;
                if (window == 0) continue;
                if (IsIconic(window)) ShowWindow(window, RestoreWindow);
                SetForegroundWindow(window);
                return true;
            }
            return false;
        }
        finally { foreach (var other in others) other.Dispose(); }
    }

    /// <summary>Gives the name back. Called on the thread that took it, as the mutex requires.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (First) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
