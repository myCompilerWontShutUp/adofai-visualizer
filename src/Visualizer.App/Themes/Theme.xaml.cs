using System.Windows;

namespace Visualizer.App.Themes;

/// <summary>Shared dark theme. Merged per window so it also loads without an Application instance.</summary>
public partial class Theme : ResourceDictionary
{
    public Theme()
    {
        InitializeComponent();
    }
}
