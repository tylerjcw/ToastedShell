using Tosh.Runtime;
using Tosh.Runtime.Units;
using Tosh.Language.Parsing;
using Tosh.Tome.Theme;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// Lex-driven colorizer for .tosh source files. Each line is lexed independently —
/// fast enough for interactive editing, and the worst case (an unterminated string
/// literal continuing onto the next line) is a useful visual cue rather than a bug.
/// Styles route through <see cref="TomeTheme"/>, so 24-bit terminals get
/// truecolor SGR while indexed terminals get the equivalent 256-color fallback.
/// </summary>
internal sealed class ToshSyntaxColorizer : ISyntaxColorizer
{
    private static string S(Role r) => TomeTheme.Active.Open(r);

    // Cached per-call because Role resolution is theme-instance-scoped; the
    // theme is process-wide so these lookups are O(1) dictionary hits.
    private static string Keyword       => S(Role.Keyword);
    private static string ControlFlow   => S(Role.ControlFlow);
    private static string EscapedString => S(Role.EscapedString);
    private static string Interpolated  => S(Role.Interpolated);
    private static string Number        => S(Role.Number);
    private static string Constant      => S(Role.Constant);
    private static string Operator      => S(Role.Operator);
    private static string Punctuation   => S(Role.Punctuation);
    private static string Variable      => S(Role.Variable);
    private static string Flag          => S(Role.Flag);
    private static string Comment       => S(Role.Comment);
    private static string TypeName      => S(Role.TypeName);

    // Derived from LanguageSurface rather than kept here (TS-P2-10). The lists this
    // replaced held 21 words against the CLI highlighter's 59 and were missing every
    // control-flow keyword except the nine in ControlFlowKeywords — so `if`, `while`,
    // and `for` were coloured in the terminal and not in the Tome. They were also
    // the only consumer that knew the C#-familiar member-modifier aliases, which the
    // highlighter lacked: the two disagreed in both directions at once.
    private static readonly HashSet<string> Keywords =
        LanguageSurface.Keywords.ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> ControlFlowKeywords =
        LanguageSurface.ControlFlow.ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> Modifiers =
        LanguageSurface.Modifiers.ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> OperatorWords =
        LanguageSurface.OperatorWords.ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> Constants =
        LanguageSurface.Constants.ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> LanguageForms =
        LanguageSurface.LanguageForms.ToHashSet(StringComparer.Ordinal);

    public IReadOnlyList<StyledSpan> Colorize(string line, int lineIndex)
    {
        if (string.IsNullOrEmpty(line))
            return Array.Empty<StyledSpan>();

        // Lex; if it throws, fall back to no highlighting.
        IReadOnlyList<SyntaxToken> tokens;
        try
        {
            tokens = new ToshLexer(line).Lex();
        }
        catch
        {
            return Array.Empty<StyledSpan>();
        }

        var spans = new List<StyledSpan>(tokens.Count);
        var commentStart = FindCommentStart(line);

        foreach (var token in tokens)
        {
            if (token.Kind == SyntaxTokenKind.EndOfFile)
                break;

            // A '#' encountered outside a string starts a line comment; the lexer
            // emits tokens up to but not always including the comment text, so we
            // mask off anything at/after commentStart and emit it as one span at the end.
            if (commentStart >= 0 && token.Span.Start >= commentStart)
                continue;

            var style = StyleFor(token);
            if (style is null) continue;

            var start = token.Span.Start;
            var length = token.Text.Length;

            // Clip a token that runs into a comment.
            if (commentStart >= 0 && start + length > commentStart)
                length = commentStart - start;

            if (length <= 0) continue;

            spans.Add(new StyledSpan(start, length, style));
        }

        if (commentStart >= 0)
            spans.Add(new StyledSpan(commentStart, line.Length - commentStart, Comment));

        spans.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return spans;
    }

    private static string? StyleFor(SyntaxToken token)
    {
        return token.Kind switch
        {
            SyntaxTokenKind.String => EscapedString,
            SyntaxTokenKind.InterpolatedString => Interpolated,
            SyntaxTokenKind.Number => Number,
            SyntaxTokenKind.UnitLiteral => Number,
            SyntaxTokenKind.Boolean or SyntaxTokenKind.Null => Constant,
            SyntaxTokenKind.Pipe or SyntaxTokenKind.Ampersand
                or SyntaxTokenKind.GreaterThan or SyntaxTokenKind.GreaterThanEqual
                or SyntaxTokenKind.LessThan or SyntaxTokenKind.LessThanEqual
                or SyntaxTokenKind.BangEqual or SyntaxTokenKind.BangTilde
                or SyntaxTokenKind.Bang or SyntaxTokenKind.GreaterThanGreaterThan
                or SyntaxTokenKind.LessThanLessThanLessThan
                or SyntaxTokenKind.QuestionQuestion or SyntaxTokenKind.QuestionDot
                or SyntaxTokenKind.DollarOpenParen or SyntaxTokenKind.LessThanOpenParen
                or SyntaxTokenKind.FatArrow
                => Operator,
            SyntaxTokenKind.OpenParen or SyntaxTokenKind.CloseParen
                or SyntaxTokenKind.OpenBrace or SyntaxTokenKind.CloseBrace
                or SyntaxTokenKind.OpenBracket or SyntaxTokenKind.CloseBracket
                or SyntaxTokenKind.OpenBraceColon or SyntaxTokenKind.ColonCloseBrace
                or SyntaxTokenKind.OpenBracePipe or SyntaxTokenKind.PipeCloseBrace
                or SyntaxTokenKind.OpenBracePercent or SyntaxTokenKind.PercentCloseBrace
                or SyntaxTokenKind.Comma or SyntaxTokenKind.Semicolon
                => Punctuation,
            SyntaxTokenKind.Bareword => BarewordStyle(token.Text),
            _ => null,
        };
    }

    private static string? BarewordStyle(string text)
    {
        if (text.StartsWith('`') &&
            UnitExpressionParser.TryParseConversion(text[1..], out _, out _, out _)) return Number;
        if (ControlFlowKeywords.Contains(text)) return ControlFlow;
        if (Keywords.Contains(text)) return Keyword;
        if (Modifiers.Contains(text)) return Keyword;
        if (OperatorWords.Contains(text)) return Operator;
        if (Constants.Contains(text)) return Constant;
        if (text.StartsWith('$') || text == "_") return Variable;
        if (text.StartsWith('-')) return Flag;
        if (text.Length > 0 && char.IsUpper(text[0])) return TypeName;
        return null;
    }

    /// <summary>
    /// Finds the start of a line comment ('#' outside any string). Returns -1
    /// when there is no comment on this line. The rule itself lives in
    /// <see cref="ToshCommentSyntax"/> so it cannot drift from the lexer.
    /// </summary>
    private static int FindCommentStart(string line)
        => ToshCommentSyntax.FindCommentStart(line);
}
