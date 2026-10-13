using Visualizer.Core.Settings;

namespace Visualizer.Application.Input;

public enum InputAction { Reset, Start, Toggle, OpenSettings, PreviousCheckpoint, NextCheckpoint, LaneKey, Pause }

public static class InputPolicy
{
    /// <summary>
    /// Whether the observed console shortcut toggles the console: always with this app in front, otherwise by the global
    /// input scope like every other observed key, so with the game in front while the scope is on (user rule: the game or
    /// the visualizer).
    /// </summary>
    public static bool ShouldOpenConsole(VisualizerSettings settings, bool appForeground, bool gameForeground)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return appForeground || !settings.OnlyReactWhenAdofaiForeground || gameForeground;
    }

    /// <summary>
    /// Keys that put the playhead somewhere or stop it. With link auto on the linker alone does that (user rule: pressing
    /// Enter in the game brought the start of the song back in front of the notes the game was playing).
    /// </summary>
    public static bool MovesPlayhead(InputAction action) => action is InputAction.Reset or InputAction.Start or InputAction.Pause
        or InputAction.PreviousCheckpoint or InputAction.NextCheckpoint;

    public static bool ShouldReact(InputAction action, VisualizerSettings settings, bool adofaiForeground, bool editingUi)
    {
        if (editingUi) return false;
        // The toggle must remain reachable even while the visualizer is OFF.
        return action is InputAction.Toggle or InputAction.OpenSettings
            || !settings.OnlyReactWhenAdofaiForeground || adofaiForeground;
    }
}

public interface IInputMonitor : IDisposable
{
    event Action<int>? KeyPressed;
    event Action<int>? KeyReleased;
    void Start(nint windowHandle);
    void SetObservedKeys(IEnumerable<int> virtualKeys);
    void ObserveWindowMessage(int message, nint parameter);
}

public enum KeyTransition { None, Pressed, Released }

/// <summary>Remembers only currently held configured keys, not keyboard history.</summary>
public sealed class KeyPressTracker
{
    private readonly HashSet<(nint Device, int Key)> _held = [];
    private HashSet<int> _observed = [];
    public void SetObservedKeys(IEnumerable<int> keys) { _observed = keys.ToHashSet(); _held.Clear(); }
    public bool Observe(nint device, int key, bool keyUp) => Transition(device, key, keyUp) == KeyTransition.Pressed;

    /// <summary>Key repeat is not a new press; a release is reported only for a key seen going down.</summary>
    public KeyTransition Transition(nint device, int key, bool keyUp)
    {
        if (keyUp) return _held.Remove((device, key)) ? KeyTransition.Released : KeyTransition.None;
        return _observed.Contains(key) && _held.Add((device, key)) ? KeyTransition.Pressed : KeyTransition.None;
    }

    public IReadOnlyList<int> RemoveDevice(nint device)
    {
        var released = _held.Where(item => item.Device == device).Select(item => item.Key).ToArray();
        _held.RemoveWhere(item => item.Device == device);
        return released;
    }
}
