namespace Tosh.Language.Parsing;

/// <summary>
/// A single <c>#</c>-style line comment captured by the lexer.
/// </summary>
/// <param name="Position">0-based offset of the leading <c>#</c> in source.</param>
/// <param name="EndPosition">0-based offset just past the last comment char (excludes the newline).</param>
/// <param name="Line">1-based line number on which the comment appears.</param>
/// <param name="IsFullLine">True when only whitespace precedes the <c>#</c> on its line.</param>
/// <param name="Text">The full comment text including the leading <c>#</c>.</param>
public sealed record LineComment(
    int Position,
    int EndPosition,
    int Line,
    bool IsFullLine,
    string Text);
