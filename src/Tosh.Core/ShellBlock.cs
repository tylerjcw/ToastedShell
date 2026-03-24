namespace Tosh.Core;

public sealed record ShellBlock(
    object Syntax,
    string SourceName,
    string SourceText,
    TextSpan Span);
