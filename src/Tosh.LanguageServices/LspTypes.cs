using System.Text.Json.Serialization;

namespace Tosh.LanguageServices;

public sealed record LspPosition(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

public sealed record LspRange(
    [property: JsonPropertyName("start")] LspPosition Start,
    [property: JsonPropertyName("end")] LspPosition End);

public sealed record LspDiagnostic(
    [property: JsonPropertyName("range")] LspRange Range,
    [property: JsonPropertyName("severity")] int Severity,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("message")] string Message);

public sealed record LspCompletionItem(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("documentation")] string? Documentation = null,
    [property: JsonPropertyName("insertText")] string? InsertText = null);

public sealed record LspParameterInformation(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("documentation")] string? Documentation = null);

public sealed record LspSignatureInformation(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("documentation")] string? Documentation = null,
    [property: JsonPropertyName("parameters")] IReadOnlyList<LspParameterInformation>? Parameters = null);

public sealed record LspSignatureHelp(
    [property: JsonPropertyName("signatures")] IReadOnlyList<LspSignatureInformation> Signatures,
    [property: JsonPropertyName("activeSignature")] int ActiveSignature,
    [property: JsonPropertyName("activeParameter")] int ActiveParameter);

public sealed record LspMarkupContent(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("value")] string Value);

public sealed record LspHover(
    [property: JsonPropertyName("contents")] LspMarkupContent Contents,
    [property: JsonPropertyName("range")] LspRange? Range = null);

public sealed record LspDocumentSymbol(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("range")] LspRange Range,
    [property: JsonPropertyName("selectionRange")] LspRange SelectionRange,
    [property: JsonPropertyName("children")] IReadOnlyList<LspDocumentSymbol> Children);

public sealed record LspLocation(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("range")] LspRange Range);

public sealed record LspSymbolInformation(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("location")] LspLocation Location,
    [property: JsonPropertyName("containerName")] string? ContainerName = null);
