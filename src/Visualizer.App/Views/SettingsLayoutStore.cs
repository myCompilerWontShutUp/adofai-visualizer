namespace Visualizer.App.Views;

/// <summary>Remembers which settings sections are open. This UI state is never part of the settings JSON.</summary>
public interface ISettingsLayoutStore
{
    bool? IsExpanded(string section);
    void SetExpanded(string section, bool expanded);
}

/// <summary>
/// Keeps section state while the app runs. A file-backed store can replace it in a packaged build; any such file must
/// follow the project storage rules (app-owned storage only, never next to maps) and stay out of the settings JSON.
/// </summary>
public sealed class MemorySettingsLayoutStore : ISettingsLayoutStore
{
    private readonly Dictionary<string, bool> _expanded = [];

    public static MemorySettingsLayoutStore Shared { get; } = new();

    public bool? IsExpanded(string section) => _expanded.TryGetValue(section, out var expanded) ? expanded : null;

    public void SetExpanded(string section, bool expanded) => _expanded[section] = expanded;
}
