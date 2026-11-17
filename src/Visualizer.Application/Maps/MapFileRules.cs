namespace Visualizer.Application.Maps;

/// <summary>
/// Which discovered .adofai files are separate levels. Verified against the game assembly (read-only):
/// <c>scnEditor.SaveBackup</c> auto-saves unsaved edits as <c>backup.adofai</c> (or
/// <c>backup2.adofai</c> while editing a backup) next to the level; <c>scnCLS.DisplayLevel</c> plays
/// <c>main.adofai</c>; <c>AdoPackageInstaller.FindLevelFile</c> and <c>scnEditor.FindAdofaiLevelOnDirectory</c>
/// skip the backup when another level file exists. Such a backup is therefore not listed as its own song.
/// </summary>
public static class MapFileRules
{
    private static readonly string[] EditorBackupNames = ["backup.adofai", "backup2.adofai"];

    public static bool IsEditorBackupName(string relativePath) =>
        EditorBackupNames.Contains(Path.GetFileName(relativePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>True for an editor backup whose folder also holds a non-backup level file.</summary>
    public static bool IsShadowedEditorBackup(string relativePath, IEnumerable<string> allRelativePaths)
    {
        if (!IsEditorBackupName(relativePath)) return false;
        var folder = Path.GetDirectoryName(relativePath) ?? "";
        return allRelativePaths.Any(other => string.Equals(Path.GetDirectoryName(other) ?? "", folder, StringComparison.OrdinalIgnoreCase)
            && !IsEditorBackupName(other) && !Path.GetFileName(other).StartsWith('.'));
    }

    public const string PlayedName = "main.adofai";

    /// <summary>
    /// A level file that sits beside <c>main.adofai</c> in the same folder. The song select plays main.adofai
    /// (<c>scnCLS.DisplayLevel</c>), so these are variants or old copies rather than the song of that folder; they are
    /// listed but hidden by default, and the song list window can bring one back.
    /// A folder without main.adofai keeps every file, since each is a song of its own.
    /// </summary>
    public static bool IsBesideMainLevel(string relativePath, IEnumerable<string> allRelativePaths)
    {
        var name = Path.GetFileName(relativePath);
        if (string.Equals(name, PlayedName, StringComparison.OrdinalIgnoreCase)) return false;
        var folder = Path.GetDirectoryName(relativePath) ?? "";
        return allRelativePaths.Any(other => string.Equals(Path.GetDirectoryName(other) ?? "", folder, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(other), PlayedName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Which levels of one folder are practice maps rather than its song (user rule: some maps put practice levels in front
    /// of the real one). With main.adofai there it is the song, as the game's song select plays it, and every other level
    /// is practice. Without one the longest level (its last hit latest) is taken as the song; a level whose length is not
    /// known (it could not be read) is not judged by length and stays a song. A folder of one level has nothing to choose
    /// between, and <paramref name="byLength"/> false (the store's top folder, where unrelated songs sit side by side) uses
    /// main.adofai alone. Returns the indexes of the practice levels.
    /// </summary>
    public static IReadOnlyList<int> PracticeLevels(IReadOnlyList<(string Path, double? LengthMs)> folder, bool byLength = true)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (folder.Count < 2) return [];
        var main = -1;
        for (var index = 0; index < folder.Count && main < 0; index++)
            if (string.Equals(Path.GetFileName(folder[index].Path), PlayedName, StringComparison.OrdinalIgnoreCase)) main = index;
        if (main >= 0) return Enumerable.Range(0, folder.Count).Where(index => index != main).ToArray();
        if (!byLength) return [];
        for (var index = 0; index < folder.Count; index++)
            if (folder[index].LengthMs is double length && (main < 0 || length > folder[main].LengthMs!.Value)) main = index;
        return main < 0 ? [] : Enumerable.Range(0, folder.Count).Where(index => index != main && folder[index].LengthMs is not null).ToArray();
    }

    /// <summary>Level files to list, in their original order.</summary>
    public static IReadOnlyList<string> SelectLevels(IReadOnlyList<string> relativePaths) =>
        relativePaths.Where(path => !IsShadowedEditorBackup(path, relativePaths)).ToArray();
}
