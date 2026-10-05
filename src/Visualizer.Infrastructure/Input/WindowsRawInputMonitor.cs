using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Visualizer.Application.Input;

namespace Visualizer.Infrastructure.Input;

/// <summary>Passive keyboard observation. No NOLEGACY, hooks, hotkey reservation or input synthesis.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRawInputMonitor : IInputMonitor
{
    private const int WmInput = 0x00FF;
    private const int WmInputDeviceChange = 0x00FE;
    private const uint RidInput = 0x10000003;
    private const uint InputSink = 0x100;
    private const uint DeviceNotify = 0x2000;
    private readonly KeyPressTracker _tracker = new();
    private bool _registered;
    public event Action<int>? KeyPressed;
    public event Action<int>? KeyReleased;

    public void Start(nint windowHandle)
    {
        if (_registered) throw new InvalidOperationException("Input monitor is already registered.");
        if (windowHandle == 0) throw new ArgumentException("A valid window handle is required.");
        var devices = new[] { new RawInputDevice { UsagePage = 1, Usage = 6, Flags = InputSink | DeviceNotify, Target = windowHandle } };
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _registered = true;
    }

    public void SetObservedKeys(IEnumerable<int> virtualKeys) => _tracker.SetObservedKeys(virtualKeys);

    public void ObserveWindowMessage(int message, nint parameter)
    {
        if (!_registered) return;
        if (message == WmInputDeviceChange)
        {
            foreach (var key in _tracker.RemoveDevice(parameter)) KeyReleased?.Invoke(key);
            return;
        }
        if (message != WmInput) return;
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(parameter, RidInput, 0, ref size, headerSize) == uint.MaxValue
            || size < headerSize + Marshal.SizeOf<RawKeyboard>() || size > 4096) return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var received = GetRawInputData(parameter, RidInput, buffer, ref size, headerSize);
            if (received == uint.MaxValue || received < headerSize + Marshal.SizeOf<RawKeyboard>()) return;
            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != 1) return;
            var keyboard = Marshal.PtrToStructure<RawKeyboard>(buffer + (int)headerSize);
            if (keyboard.MakeCode == 0xFF || keyboard.VirtualKey >= 0xFF) return;
            switch (_tracker.Transition(header.Device, keyboard.VirtualKey, (keyboard.Flags & 1) != 0))
            {
                case KeyTransition.Pressed: KeyPressed?.Invoke(keyboard.VirtualKey); break;
                case KeyTransition.Released: KeyReleased?.Invoke(keyboard.VirtualKey); break;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        // Caller leaves WM_INPUT unhandled so the normal DefWindowProc cleanup runs.
    }

    public void Dispose()
    {
        if (!_registered) return;
        var devices = new[] { new RawInputDevice { UsagePage = 1, Usage = 6, Flags = 1, Target = 0 } };
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _registered = false;
        _tracker.SetObservedKeys([]);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage; public ushort Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader { public uint Type; public uint Size; public nint Device; public nuint Parameter; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard { public ushort MakeCode; public ushort Flags; public ushort Reserved; public ushort VirtualKey; public uint Message; public uint ExtraInformation; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint rawInput, uint command, nint data, ref uint size, uint headerSize);
}
