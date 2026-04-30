namespace Tosh.Language.Parsing;

/// <summary>
/// An inline `# hush &lt;code&gt;` directive recovered by the lexer.
/// </summary>
/// <param name="Line">1-based line number of the comment containing the directive.</param>
/// <param name="Code">The diagnostic code to suppress (e.g. <c>tosh.naming.shadowed_underscore</c>).</param>
public readonly record struct LineHushDirective(int Line, string Code);
