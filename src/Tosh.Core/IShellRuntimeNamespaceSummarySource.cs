namespace Tosh.Core;

public interface IShellRuntimeNamespaceSummarySource
{
    RuntimeNamespaceDisplaySummary GetDisplaySummary();
}

public sealed record RuntimeNamespaceDisplaySummary(
    string Title,
    string TypeName,
    IReadOnlyList<(string Label, string Value)> TopLevelItems,
    IReadOnlyList<RuntimeNamespaceSection> Sections,
    IReadOnlyList<string> Footnotes);

public sealed record RuntimeNamespaceSection(
    string Path,
    string Description,
    IReadOnlyList<(string Label, string Value)> Items);
