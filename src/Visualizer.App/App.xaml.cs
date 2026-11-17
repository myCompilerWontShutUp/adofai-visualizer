using System.Runtime;
using System.Windows;
using Visualizer.Infrastructure.Instances;

namespace Visualizer.App;

public partial class App : System.Windows.Application
{
    /// <summary>Held while this visualizer runs, so one started meanwhile hands over to it.</summary>
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One visualizer at a time (user request): starting it again brings the running one forward instead of opening a
        // second window that the game link would never reach.
        _instance = new SingleInstance();
        if (!_instance.First)
        {
            SingleInstance.BringOtherToFront();
            _instance.Dispose();
            _instance = null;
            StartupUri = null;
            Shutdown();
            return;
        }
        // The frame loop is the one thing that must stay even, so the collector is asked to keep blocking full collections
        // out of the way. It only changes when this process collects; nothing is reserved from the system or the game.
        try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; }
        // Server or no-GC modes refuse it; the app runs the same, only with the usual collections.
        catch (InvalidOperationException) { }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
