using System.Windows.Input;
using Visualizer.Application.Input;
using Visualizer.Core.Localization;
using Visualizer.Core.Settings;

namespace Visualizer.App.Input;

/// <summary>A key setting that cannot be bound, with the name of that setting.</summary>
public sealed class ControlKeyException(string message, string field) : ArgumentException(message)
{
    public string Field { get; } = field;
}

public static class ControlBindings
{
    /// <summary>Virtual key → command. Also validates lane keys so settings never apply half-valid.</summary>
    public static IReadOnlyDictionary<int, InputAction> Create(VisualizerSettings settings)
    {
        var actions = CreateActions(settings);
        _ = CreateLaneKeys(settings, actions);
        return actions;
    }

    /// <summary>Virtual key → logical tile (1..8). Only the start key may also be a lane key.</summary>
    public static IReadOnlyDictionary<int, int> CreateLaneKeys(VisualizerSettings settings) =>
        CreateLaneKeys(settings, CreateActions(settings));

    private static Dictionary<int, InputAction> CreateActions(VisualizerSettings settings)
    {
        settings.EnsureValid();
        var bindings = new Dictionary<int, InputAction>();
        void Add(string field, string name, InputAction action)
        {
            if (!bindings.TryAdd(VirtualKey(settings, name, field), action))
                throw new ControlKeyException(Languages.Get(settings.Language, "Error.ControlKeyOverlap"), field);
        }
        // Observed only: the game still receives Escape and opens its pause menu. Added first so a clash names the user's key.
        Add(VisualizerSettings.EscapeKey, VisualizerSettings.EscapeKey, InputAction.Pause);
        Add(nameof(VisualizerSettings.ResetKey), settings.ResetKey, InputAction.Reset);
        Add(nameof(VisualizerSettings.StartTriggerKey), settings.StartTriggerKey, InputAction.Start);
        Add(nameof(VisualizerSettings.VisualizerToggleKey), settings.VisualizerToggleKey, InputAction.Toggle);
        if (settings.PreviousCheckpointKey is { } previous) Add(nameof(VisualizerSettings.PreviousCheckpointKey), previous, InputAction.PreviousCheckpoint);
        if (settings.NextCheckpointKey is { } next) Add(nameof(VisualizerSettings.NextCheckpointKey), next, InputAction.NextCheckpoint);
        if (settings.SettingsHotkey is { } shortcut) Add(nameof(VisualizerSettings.SettingsHotkey), shortcut, InputAction.OpenSettings);
        return bindings;
    }

    private static Dictionary<int, int> CreateLaneKeys(VisualizerSettings settings, IReadOnlyDictionary<int, InputAction> actions)
    {
        var lanes = new Dictionary<int, int>();
        var tokens = settings.GetLaneKeyTokens();
        for (var i = 0; i < tokens.Count; i++)
        {
            var code = VirtualKey(settings, tokens[i], nameof(VisualizerSettings.LaneKeys));
            if (actions.TryGetValue(code, out var action) && action != InputAction.Start)
                throw new ControlKeyException(Languages.Format(settings.Language, "Error.LaneKeyConflict", tokens[i]), nameof(VisualizerSettings.LaneKeys));
            if (!lanes.TryAdd(code, i + 1))
                throw new ControlKeyException(Languages.Format(settings.Language, "Error.LaneKeyDuplicate", tokens[i]), nameof(VisualizerSettings.LaneKeys));
        }
        return lanes;
    }

    private static int VirtualKey(VisualizerSettings settings, string name, string field)
    {
        var code = KeyNames.TryParse(name, out var key) ? KeyInterop.VirtualKeyFromKey(key) : 0;
        return code != 0 ? code : throw new ControlKeyException(Languages.Format(settings.Language, "Error.UnsupportedKey", name), field);
    }
}
