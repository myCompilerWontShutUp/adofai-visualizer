using Visualizer.Core.Localization;

namespace Visualizer.App.Localization;

/// <summary>Binding helper over the language files (Core Localization/Languages): Text[key] in XAML, Get and Format in code.</summary>
public sealed class UiText(string language)
{
    public string Language => language;
    public string this[string key] => Languages.Get(language, key);
    public string Format(string key, params object?[] args) => Languages.Format(language, key, args);
    public static string Get(string language, string key) => Languages.Get(language, key);
}
