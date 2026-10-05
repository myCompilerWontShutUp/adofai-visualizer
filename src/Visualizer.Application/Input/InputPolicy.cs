using Visualizer.Core.Settings;

namespace Visualizer.Application.Input;

public enum InputAction { Reset, Start, Toggle, OpenSettings, PreviousCheckpoint, NextCheckpoint, LaneKey, Pause }

public static class InputPolicy
{
    /// <param name="adofaiForeground">True while ADOFAI or this visualizer is the foreground app.</param>
    /// <summary>
    /// Whether the console shortcut seen outside this app should open it. The app's own windows already handle it while
    /// they have the focus, so acting on it there as well would toggle it twice; otherwise it follows the global input
    /// scope like every other observed key (user rule).
    /// </summary>
    public static bool ShouldOpenConsole(VisualizerSettings settings, bool appForeground, bool gameForeground)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (appForeground) return false;
        return !settings.OnlyReactWhenAdofaiForeground || gameForeground;
    }

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
