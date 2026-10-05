using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Visualizer.App.Input;

/// <summary>Keyboard playback commands come only from configured Raw Input bindings.</summary>
public class PlaybackButton : Button
{
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter) return;
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter) return;
        base.OnKeyUp(e);
    }
}

/// <summary>Same guard for toggles on the playback surface: Space/Enter never flip them.</summary>
public class PlaybackToggleButton : ToggleButton
{
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter) return;
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter) return;
        base.OnKeyUp(e);
    }
}
