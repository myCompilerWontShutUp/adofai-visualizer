using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Visualizer.App.Views;

/// <summary>
/// False hides the element but keeps its place, unlike the usual collapse. The song-end buttons need it: the first one
/// has to stay at the same height whether the second one is there or not (user rule).
/// </summary>
public sealed class HiddenWhenFalse : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Hidden;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
