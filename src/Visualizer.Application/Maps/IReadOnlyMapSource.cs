using Visualizer.Core.Maps;

namespace Visualizer.Application.Maps;

/// <summary>Read one relative .adofai path from a selected map folder. No writable handles.</summary>
public interface IReadOnlyMapSource
{
    Task<RawMapDocument> ReadAsync(string relativePath, CancellationToken cancellationToken = default);
}
