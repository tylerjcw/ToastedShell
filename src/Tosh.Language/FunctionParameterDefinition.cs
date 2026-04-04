using Tosh.Core;

namespace Tosh.Language;

public sealed record FunctionParameterDefinition(string Name, string? TypeName, bool IsOptional, bool IsRest, TextSpan Span);
