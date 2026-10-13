using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Visualizer.Infrastructure.Input;

/// <summary>
/// Presses or releases one key the way the keyboard does, by its scan code (SendInput). This is the one place the app makes
/// input, the narrow exception to AGENTS.md rule 6 that link inspect has: the app calls it only while every rule of that
/// command holds (the game in front, link auto off, the same song, No Fail on), and nothing else may.
/// </summary>
[SupportedOSPlatform("windows")]
public static class KeyboardInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;
    private const uint MapVkToVscEx = 4;

    /// <summary>Sends the key down or up. Throws <see cref="Win32Exception"/> when Windows does not take it.</summary>
    public static void Send(int virtualKey, bool down)
    {
        var scan = MapVirtualKeyW((uint)virtualKey, MapVkToVscEx);
        if (scan == 0) throw new ArgumentException("That key has no scan code.", nameof(virtualKey));
        // A scan code with E0 in front is one of the extended keys (the arrows, the right Ctrl, ...).
        var flags = KeyEventScanCode | (down ? 0 : KeyEventKeyUp) | ((scan & 0xFF00) == 0xE000 ? KeyEventExtended : 0);
        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion { Keyboard = new KeyboardInput { Scan = (ushort)(scan & 0xFF), Flags = flags } }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    // The union is as large as its largest member, the mouse one, which SendInput checks through the size it is given.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);
}
