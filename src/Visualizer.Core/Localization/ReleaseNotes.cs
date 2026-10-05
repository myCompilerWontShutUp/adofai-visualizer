namespace Visualizer.Core.Localization;

/// <summary>
/// What changed in each version, kept in the language files as "ReleaseNotes.&lt;version&gt;" so every language has its own
/// words (user request: the first start after an update says what changed, for the player, before anything else).
/// </summary>
public static class ReleaseNotes
{
    public const string Prefix = "ReleaseNotes.";

    /// <summary>The versions that have notes in <paramref name="language"/>, newest first.</summary>
    public static IReadOnlyList<Version> Versions(string language) => Languages.Keys(language)
        .Where(key => key.StartsWith(Prefix, StringComparison.Ordinal))
        .Select(key => Version.TryParse(key[Prefix.Length..], out var version) ? version : null)
        .OfType<Version>().OrderDescending().ToArray();

    /// <summary>
    /// The versions whose notes the first start of <paramref name="current"/> shows, newest first: every one after the
    /// version seen last. With none seen, only <paramref name="current"/> and only when an earlier version had run, since a
    /// fresh copy has nothing to compare with. Back on an older version, that version's own notes.
    /// </summary>
    public static IReadOnlyList<string> ToShow(string language, string? seen, string current, bool ranBefore)
    {
        if (!Version.TryParse(current, out var now)) return [];
        var known = Versions(language);
        IEnumerable<Version> shown;
        if (seen is null || !Version.TryParse(seen, out var last)) shown = ranBefore ? known.Where(version => version == now) : [];
        else if (last == now) shown = [];
        else if (last < now) shown = known.Where(version => version > last && version <= now);
        else shown = known.Where(version => version == now);
        return shown.Select(version => version.ToString(3)).ToArray();
    }
}
