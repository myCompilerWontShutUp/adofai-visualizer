using System.Reflection;

namespace Visualizer.Core.Settings;

/// <summary>
/// The settings as named fields: which ones two snapshots differ in, and a snapshot with some of them replaced. Rollback and
/// the console's set, get and import work field by field through this.
/// </summary>
public static class SettingsFields
{
    private static readonly Dictionary<string, PropertyInfo> Properties = typeof(VisualizerSettings).GetProperties()
        .Where(property => property.CanWrite && property.GetIndexParameters().Length == 0)
        .ToDictionary(property => property.Name, StringComparer.Ordinal);

    /// <summary>Every setting that can be set, by property name.</summary>
    public static IReadOnlyCollection<string> Names => Properties.Keys;

    /// <summary>The settings whose values differ between the two snapshots, in declaration order.</summary>
    public static IReadOnlyList<string> Changed(VisualizerSettings before, VisualizerSettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return Properties.Values.Where(property => !Equals(property.GetValue(before), property.GetValue(after)))
            .Select(property => property.Name).ToArray();
    }

    public static object? Get(VisualizerSettings settings, string name)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Property(name).GetValue(settings);
    }

    /// <summary>A copy with one setting replaced; nothing is validated here.</summary>
    public static VisualizerSettings With(VisualizerSettings settings, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings with { };
        Property(name).SetValue(copy, value);
        return copy;
    }

    /// <summary>
    /// <paramref name="current"/> with every setting the change from <paramref name="before"/> to <paramref name="after"/>
    /// touched put back to its earlier value; settings the change did not touch keep their current values.
    /// </summary>
    public static VisualizerSettings Restore(VisualizerSettings current, VisualizerSettings before, VisualizerSettings after) =>
        Changed(before, after).Aggregate(current, (settings, name) => With(settings, name, Get(before, name)));

    private static PropertyInfo Property(string name) => Properties.TryGetValue(name, out var property)
        ? property : throw new ArgumentException($"'{name}' is not a setting.", nameof(name));
}
