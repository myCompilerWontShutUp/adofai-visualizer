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

    /// <summary>Level files to list, in their original order.</summary>
    public static IReadOnlyList<string> SelectLevels(IReadOnlyList<string> relativePaths) =>
        relativePaths.Where(path => !IsShadowedEditorBackup(path, relativePaths)).ToArray();
}
