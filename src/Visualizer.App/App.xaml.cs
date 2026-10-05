using System.Runtime;
using System.Windows;

namespace Visualizer.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // The frame loop is the one thing that must stay even, so the collector is asked to keep blocking full collections
        // out of the way. It only changes when this process collects; nothing is reserved from the system or the game.
        try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; }
        // Server or no-GC modes refuse it; the app runs the same, only with the usual collections.
        catch (InvalidOperationException) { }
        base.OnStartup(e);
    }
}
