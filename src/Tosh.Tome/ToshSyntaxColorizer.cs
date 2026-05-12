using Tosh.Language.Parsing;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// Lex-driven colorizer for .tosh source files. Each line is lexed independently —
/// fast enough for interactive editing, and the worst case (an unterminated string
/// literal continuing onto the next line) is a useful visual cue rather than a bug.
/// Styles are fixed ANSI 256-color foregrounds; theming can be hooked in later.
/// </summary>
internal sealed class ToshSyntaxColorizer : ISyntaxColorizer
{
    // ANSI 38;5;<n>m foreground colors picked to match the REPL's default palette
    // roughly. These are intentionally hardcoded for now; a future revision can
    // pull from ToshSyntaxThemeConfig once highlighting lives in a shared lib.
    private const string Keyword = "\u001b[38;5;141m";       // soft purple
    private const string ControlFlow = "\u001b[38;5;204m";   // pink/red
    private const string String = "\u001b[38;5;150m";        // green
    private const string EscapedString = "\u001b[38;5;108m"; // muted green
    private const string Interpolated = "\u001b[38;5;144m";  // tan-green
    private const string Number = "\u001b[38;5;215m";        // orange
    private const string Constant = "\u001b[38;5;215m";      // orange
    private const string Operator = "\u001b[38;5;110m";      // soft blue
    private const string Punctuation = "\u001b[38;5;245m";   // grey
    private const string Variable = "\u001b[38;5;110m";      // soft blue
    private const string Flag = "\u001b[38;5;180m";          // tan
    private const string Comment = "\u001b[38;5;244m";       // dim grey
    private const string TypeName = "\u001b[38;5;180m";      // tan

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "var", "func", "class", "struct", "trait", "module", "enum", "record",
        "prop", "static", "global", "export", "using", "require", "from", "as",
        "new", "nameof", "name-of", "fulfills", "uses",
    };

    private static readonly HashSet<string> ControlFlowKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "in", "while", "until", "break", "continue",
        "return", "throw", "try", "catch", "finally", "switch", "case",
        "default", "match", "yield", "defer",
    };

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "shy", "shared", "sealed", "hollow", "fixed", "vital", "guarded",
        "overrule", "hermit", "strict", "lazy", "fading", "local", "raw",
        "partial", "proud", "public", "fluid", "private", "protected",
        "abstract", "readonly", "required", "override", "obsolete",
    };

    private static readonly HashSet<string> OperatorWords = new(StringComparer.Ordinal)
    {
        "and", "or", "not", "is", "is-not", "not-in",
    };

    private static readonly HashSet<string> Constants = new(StringComparer.Ordinal)
    {
        "true", "false", "null",
    };

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
                => Operator,
            SyntaxTokenKind.OpenParen or SyntaxTokenKind.CloseParen
                or SyntaxTokenKind.OpenBrace or SyntaxTokenKind.CloseBrace
                or SyntaxTokenKind.OpenBracket or SyntaxTokenKind.CloseBracket
                or SyntaxTokenKind.Comma or SyntaxTokenKind.Semicolon
                => Punctuation,
            SyntaxTokenKind.Bareword => BarewordStyle(token.Text),
            _ => null,
        };
    }

    private static string? BarewordStyle(string text)
    {
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
    /// when there is no comment on this line.
    /// </summary>
    private static int FindCommentStart(string line)
    {
        var inString = false;
        var stringChar = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inString)
            {
                if (ch == '\\' && i + 1 < line.Length) { i++; continue; }
                if (ch == stringChar) inString = false;
                continue;
            }
            if (ch is '"' or '\'')
            {
                inString = true;
                stringChar = ch;
                continue;
            }
            if (ch == '#') return i;
        }
        return -1;
    }
}
