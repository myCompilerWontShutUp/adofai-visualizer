namespace Visualizer.Core.Maps;

/// <summary>Unparsed source text; makes no claims about timing or map validity.</summary>
public sealed record RawMapDocument(string SourcePath, string Text);
