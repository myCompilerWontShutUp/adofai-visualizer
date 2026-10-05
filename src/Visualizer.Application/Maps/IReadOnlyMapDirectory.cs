namespace Visualizer.Application.Maps;

public sealed record MapDiscovery(IReadOnlyList<string> RelativePaths, IReadOnlyList<string> Warnings);

public interface IReadOnlyMapDirectory : IReadOnlyMapSource
{
    string RootDirectory { get; }
    Task<MapDiscovery> DiscoverAsync(CancellationToken cancellationToken = default);
}
