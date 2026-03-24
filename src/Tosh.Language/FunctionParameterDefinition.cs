using Tosh.Core;

namespace Tosh.Language;

public sealed record FunctionParameterDefinition(string Name, string? TypeName, TextSpan Span);
