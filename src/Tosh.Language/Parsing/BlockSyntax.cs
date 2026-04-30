using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public sealed record BlockSyntax(IReadOnlyList<StatementSyntax> Statements, TextSpan Span);
