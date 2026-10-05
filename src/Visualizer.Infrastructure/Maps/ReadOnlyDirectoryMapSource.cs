using Visualizer.Application.Maps;
using Visualizer.Core.Maps;

namespace Visualizer.Infrastructure.Maps;

/// <summary>Reads existing files only. Construction does not access or create the folder.</summary>
public sealed class ReadOnlyDirectoryMapSource : IReadOnlyMapDirectory
{
    private readonly string _root;
    public string RootDirectory => Path.TrimEndingDirectorySeparator(_root);

    public ReadOnlyDirectoryMapSource(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory))
            + Path.DirectorySeparatorChar;
    }

    public Task<MapDiscovery> DiscoverAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var files = new List<string>();
        var warnings = new List<string>();
        for (string? path = RootDirectory; path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse point map roots are not supported.");
        var pending = new Stack<string>();
        pending.Push(RootDirectory);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        warnings.Add($"Reparse point skipped: {path}");
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                    else if (string.Equals(Path.GetExtension(path), ".adofai", StringComparison.OrdinalIgnoreCase))
                        files.Add(Path.GetRelativePath(RootDirectory, path));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Scan incomplete: {directory}: {error.Message}");
            }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return new MapDiscovery(files.AsReadOnly(), warnings.AsReadOnly());
    }, cancellationToken);

    public async Task<RawMapDocument> ReadAsync(
        string relativePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
            throw new ArgumentException("An ordinary relative map path is required.", nameof(relativePath));

        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(path), ".adofai", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The path must identify an .adofai file within the selected folder.", nameof(relativePath));

        // Reject junctions/symlinks, including those in ancestors of the selected root.
        // This is a guard against accidental traversal, not an OS security sandbox.
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Map paths containing reparse points are not supported.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return new RawMapDocument(path, text);
    }
}
