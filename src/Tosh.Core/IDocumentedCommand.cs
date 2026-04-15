namespace Tosh.Core;

/// <summary>
/// Optional interface for commands that carry user-authored documentation
/// (e.g. from <c>##</c> doc-comment blocks).
/// </summary>
public interface IDocumentedCommand
{
    IReadOnlyDictionary<string, string> ParameterDescriptions { get; }
    string? ReturnsDescription { get; }
    IReadOnlyList<string> DocExamples { get; }
    bool IsDeprecated { get; }
    string? DeprecatedMessage { get; }
    IReadOnlyList<string> SeeAlso { get; }
    string? Since { get; }
    IReadOnlyList<string> Throws { get; }
}
