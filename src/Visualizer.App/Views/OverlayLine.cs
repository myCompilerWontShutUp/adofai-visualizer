using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Visualizer.App.ViewModels;

namespace Visualizer.App.Views;

/// <summary>
/// Writes the bottom overlay line from its pieces, underlining the ones a control on the field has moved (user rule).
/// A plain string cannot carry that, so the pieces come through as a list and become runs of their own.
/// </summary>
public static class OverlayLine
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.RegisterAttached(
        "Parts", typeof(IReadOnlyList<OverlayPart>), typeof(OverlayLine), new PropertyMetadata(null, PartsChanged));

    public static void SetParts(DependencyObject element, IReadOnlyList<OverlayPart>? value) =>
        element.SetValue(PartsProperty, value);

    public static IReadOnlyList<OverlayPart>? GetParts(DependencyObject element) =>
        (IReadOnlyList<OverlayPart>?)element.GetValue(PartsProperty);

    private static void PartsChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block) return;
        block.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<OverlayPart> parts) return;
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) block.Inlines.Add(new Run(" · "));
            var run = new Run(parts[i].Text);
            if (parts[i].Changed) run.TextDecorations = TextDecorations.Underline;
            block.Inlines.Add(run);
        }
    }
}
