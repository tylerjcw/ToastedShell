using System.Globalization;
using System.Text;
using System.Numerics;
using Tosh.Runtime;
using Tosh.Runtime.Units;

namespace Tosh.Language.Parsing;

public sealed class ToshLexer
{
    private enum BraceContextKind
    {
        Block,
        CollectionLiteral,
    }

    private readonly string _source;
    private int _position;

    /// <summary>
    /// Parenthesis, bracket, and collection-literal nesting depth for
    /// expression contexts (TS-P2-11). Command
    /// position must keep greedy barewords so <c>ls -la</c>,
    /// <c>./script.tosh</c>, and <c>*.txt</c> survive intact, while
    /// expression position needs operators as real tokens. The lexer
    /// tracks this itself: the parser cannot drive it, because lexing
    /// completes before parsing begins and the parser relies on
    /// arbitrary lookahead over the finished token list.
    /// </summary>
    private int _expressionDepth;

    /// <summary>
    /// Braces need their own context stack because a plain block may be nested
    /// inside a collection literal. A plain <c>}</c> closes the nearest brace;
    /// it leaves expression depth alone for a block, but restores command mode
    /// when recovering a literal whose sigil was separated from the brace
    /// (<c>| }</c>, <c>: }</c>, or <c>% }</c>).
    /// </summary>
    private readonly Stack<BraceContextKind> _braceContexts = new();

    /// <summary>True when the lexer is inside a parenthesised, bracketed,
    /// or collection-literal expression context.</summary>
    private bool InExpressionContext => _expressionDepth > 0;
    private readonly List<LineHushDirective> _lineHushDirectives = [];
    private readonly List<LineComment> _lineComments = [];

    public ToshLexer(string source)
    {
        _source = source ?? string.Empty;
    }

    /// <summary>
    /// Inline `# hush &lt;code&gt;` directives discovered during lexing. Each directive
    /// records the 1-based line on which the comment appeared and the diagnostic code(s)
    /// to suppress. The engine consults this list when emitting non-error diagnostics
    /// so users can silence a single noisy line without changing global config.
    /// </summary>
    public IReadOnlyList<LineHushDirective> LineHushDirectives => _lineHushDirectives;

    /// <summary>
    /// All `#`-style line comments encountered during lexing, in source order.
    /// Used by the source formatter to preserve comments across a format pass.
    /// Doc comments (<c>##</c>) are emitted as <see cref="SyntaxTokenKind.DocComment"/>
    /// tokens instead and are not included here.
    ///
    /// Every entry's <c>Text</c> is a bare <c>"#"</c> or begins <c>"# "</c>: a lone
    /// <c>#</c> only opens a comment when whitespace or end-of-line follows it, so a
    /// glued form like <c>#ff0000</c> never reaches this list. The formatter relies on
    /// that invariant to re-emit <c>Text</c> verbatim and still round-trip.
    /// </summary>
    public IReadOnlyList<LineComment> LineComments => _lineComments;

    public IReadOnlyList<SyntaxToken> Lex()
    {
        var tokens = new List<SyntaxToken>();

        while (true)
        {
            SkipWhitespace();

            if (IsAtEnd)
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.EndOfFile, _position, string.Empty));
                return tokens;
            }

            if (Current == '#')
            {
                if (Peek() == '#' && Peek(2) == '{')
                {
                    SkipBlockComment();
                    continue;
                }

                if (Peek() == '#')
                {
                    tokens.Add(ReadDocComment());
                    continue;
                }

                // `#!` on the very first line is a shebang, not a comment and
                // not a bareword. It only has this meaning at offset 0.
                if (_position == 0 && Peek() == '!')
                {
                    SkipComment();
                    continue;
                }

                // A lone `#` opens a line comment only when it stands alone as a
                // word. We reach here straight after SkipWhitespace, so the
                // word-start half of the rule already holds and only the
                // following character is left to check. Glued to a non-space it
                // falls through to the bareword reader, which is what lets
                // `#ff0000`, `issue#42`, and `C#` go unquoted.
                if (IsCommentTerminator(Peek()))
                {
                    SkipComment();
                    continue;
                }

                // Otherwise fall through: '#' is lexed as part of a bareword.
            }

            // $ prefixed tokens: $(, $'''...''', $"""...""", $'...', $"..."
            if (Current == '$')
            {
                if (Peek() == '(')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.DollarOpenParen, _position, "$("));
                    _position += 2;
                    continue;
                }

                if (Peek() == '\'' && Peek(2) == '\'' && Peek(3) == '\'')
                {
                    tokens.Add(ReadTripleQuotedInterpolatedAnsiCString());
                    continue;
                }

                if (Peek() == '"' && Peek(2) == '"' && Peek(3) == '"')
                {
                    tokens.Add(ReadTripleQuotedInterpolatedString());
                    continue;
                }

                if (Peek() == '\'')
                {
                    tokens.Add(ReadAnsiCString());
                    continue;
                }

                if (Peek() == '"')
                {
                    tokens.Add(ReadInterpolatedString());
                    continue;
                }

                // Otherwise fall through to bareword (e.g. $variable)
            }

            // || , |
            if (Current == '|')
            {
                // `|}` closes a record literal and must be tested before `||`
                // (TS-P2-25). In `{||}` the `{|` opener has already consumed the
                // first `|`, so this branch only ever sees the closer. A trailing
                // pipe before a brace is never valid ToastScript, which is why
                // claiming the adjacent pair costs nothing: the interior pipe in
                // `{| a = ls | count |}` is not adjacent to the brace and keeps
                // its ordinary meaning.
                if (Peek() == '}')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.PipeCloseBrace, _position, "|}"));
                    ExitBraceContext();
                    _position += 2;
                    continue;
                }

                if (Peek() == '|')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.DoublePipe, _position, "||"));
                    _position += 2;
                    continue;
                }

                tokens.Add(new SyntaxToken(SyntaxTokenKind.Pipe, _position, "|"));
                _position++;
                continue;
            }

            // && , & (background)
            if (Current == '&')
            {
                if (Peek() == '&')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.DoubleAmpersand, _position, "&&"));
                    _position += 2;
                    continue;
                }

                tokens.Add(new SyntaxToken(SyntaxTokenKind.Ampersand, _position, "&"));
                _position++;
                continue;
            }

            // !=, !~, !
            if (Current == '!')
            {
                if (Peek() == '=')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.BangEqual, _position, "!="));
                    _position += 2;
                    continue;
                }

                if (Peek() == '~')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.BangTilde, _position, "!~"));
                    _position += 2;
                    continue;
                }

                tokens.Add(new SyntaxToken(SyntaxTokenKind.Bang, _position, "!"));
                _position++;
                continue;
            }

            // >= , >> , >( , >
            if (Current == '>')
            {
                if (Peek() == '=')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.GreaterThanEqual, _position, ">="));
                    _position += 2;
                    continue;
                }

                if (Peek() == '>')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.GreaterThanGreaterThan, _position, ">>"));
                    _position += 2;
                    continue;
                }

                tokens.Add(new SyntaxToken(SyntaxTokenKind.GreaterThan, _position, ">"));
                _position++;
                continue;
            }

            // <= , <<< , <( , <
            if (Current == '<')
            {
                if (Peek() == '=')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.LessThanEqual, _position, "<="));
                    _position += 2;
                    continue;
                }

                if (Peek() == '<' && Peek(2) == '<')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.LessThanLessThanLessThan, _position, "<<<"));
                    _position += 3;
                    continue;
                }

                if (Peek() == '(')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.LessThanOpenParen, _position, "<("));
                    _position += 2;
                    continue;
                }

                if (Peek() == '|')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.LessThanPipe, _position, "<|"));
                    _position += 2;
                    continue;
                }

                tokens.Add(new SyntaxToken(SyntaxTokenKind.LessThan, _position, "<"));
                _position++;
                continue;
            }

            // Paired collection-literal delimiters (TS-P2-25). `{` opens a block
            // and nothing else; each literal kind carries its own pair.
            //
            // The literal openers enter expression context exactly as `(` and `[`
            // do, and that is load-bearing rather than incidental: it makes
            // `{|a=1|}` lex identically to `{| a = 1 |}` instead of collapsing
            // `a=1` into one bareword. The removed bare-record form had that
            // flaw — `{ a=1 }` was a block while `{ a = 1 }` was a record —
            // and carrying it forward would repeat TS-P2-04 and TS-P2-15.
            if (Current == '{')
            {
                if (Peek() == ':')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.OpenBraceColon, _position, "{:"));
                    EnterCollectionLiteral();
                    _position += 2;
                    continue;
                }

                if (Peek() == '|')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.OpenBracePipe, _position, "{|"));
                    EnterCollectionLiteral();
                    _position += 2;
                    continue;
                }

                if (Peek() == '%')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.OpenBracePercent, _position, "{%"));
                    EnterCollectionLiteral();
                    _position += 2;
                    continue;
                }

                tokens.Add(new SyntaxToken(SyntaxTokenKind.OpenBrace, _position, "{"));
                _braceContexts.Push(BraceContextKind.Block);
                _position++;
                continue;
            }

            if (Current == '}')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.CloseBrace, _position, "}"));
                ExitBraceContext();
                _position++;
                continue;
            }

            // `:}` and `%}` close set and dict literals. Adjacency is what keeps
            // a record shorthand key (`{| a: 1 |}`) and a modulo expression
            // (`{% "k" => $a % $b %}`) intact — neither puts its `:` or `%`
            // immediately before the brace. Without these branches both would be
            // read as barewords, which is how `{: :}` was matched before it had
            // real tokens.
            if (Current == ':' && Peek() == '}')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.ColonCloseBrace, _position, ":}"));
                ExitBraceContext();
                _position += 2;
                continue;
            }

            if (Current == '%' && Peek() == '}')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.PercentCloseBrace, _position, "%}"));
                ExitBraceContext();
                _position += 2;
                continue;
            }

            if (Current == '[')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.OpenBracket, _position, "["));
                _expressionDepth++;
                _position++;
                continue;
            }

            if (Current == ']')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.CloseBracket, _position, "]"));
                if (_expressionDepth > 0) _expressionDepth--;
                _position++;
                continue;
            }

            if (Current == ';')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.Semicolon, _position, ";"));
                _position++;
                continue;
            }

            if (Current == ',')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.Comma, _position, ","));
                _position++;
                continue;
            }

            if (Current == '(')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.OpenParen, _position, "("));
                _expressionDepth++;
                _position++;
                continue;
            }

            if (Current == ')')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.CloseParen, _position, ")"));
                if (_expressionDepth > 0) _expressionDepth--;
                _position++;
                continue;
            }

            // Bare _ as current pipeline item — emit as its own token so
            // postfix chains like _.Name.ToString() work correctly.
            // A following '#' only ends the '_' when it opens a comment
            // ('# ' or '##'); glued to a word it belongs to the bareword.
            if (Current == '_' && (Peek() is '.' or '?' or ' ' or '\t' or '\r' or '\n' or '\0'
                or '|' or '(' or ')' or '{' or '}' or '[' or ']' or ';' or ','
                or '>' or '<' or '&' or '!'
                || (Peek() == '#' && (IsCommentTerminator(Peek(2)) || Peek(2) == '#'))))
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.Bareword, _position, "_", "_"));
                _position++;
                continue;
            }

            // .. (range operator) — only after a number, close paren, or variable reference.
            // Otherwise it's a path component like ".." or "..." for cd.
            if (Current == '.' && Peek() == '.' && Peek(2) != '.'
                && tokens.Count > 0
                && IsRangeOperatorContext(tokens[^1]))
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.DotDot, _position, ".."));
                _position += 2;
                continue;
            }

            // ??= , ?? , ?.
            if (Current == '?')
            {
                if (Peek() == '?' && Peek(2) == '=')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.Bareword, _position, "??="));
                    _position += 3;
                    continue;
                }

                if (Peek() == '?')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.QuestionQuestion, _position, "??"));
                    _position += 2;
                    continue;
                }

                if (Peek() == '.')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.QuestionDot, _position, "?."));
                    _position += 2;
                    continue;
                }
            }

            // '=>' is one token in every context. It used to be none:
            // the bareword reader stopped at '>', so the parser rebuilt
            // the arrow from a `Bareword "=" + GreaterThan` pair at
            // roughly twenty sites, each carrying a skewed two-slot
            // lookahead. Emitting it here lets those become ordinary
            // single-token checks (TS-P2-25).
            if (Current == '=' && Peek() == '>')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.FatArrow, _position, "=>"));
                _position += 2;
                continue;
            }

            // Standalone '=' inside an expression context, so a named
            // argument written without spaces (`f(a="z")`) produces the
            // same tokens as the spaced form (TS-P2-15). Compound
            // operators keep their own spellings: '==' and '=~' are
            // comparisons.
            if (Current == '=' && InExpressionContext
                && Peek() is not ('=' or '~'))
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.Bareword, _position, "=", "="));
                _position++;
                continue;
            }

            if (Current == '"' && Peek() == '"' && Peek(2) == '"')
            {
                tokens.Add(ReadTripleQuotedRawString('"'));
                continue;
            }

            if (Current == '\'' && Peek() == '\'' && Peek(2) == '\'')
            {
                tokens.Add(ReadTripleQuotedAnsiCString());
                continue;
            }

            if (Current is '"' or '\'')
            {
                tokens.Add(ReadString());
                continue;
            }

            tokens.Add(ReadBarewordOrLiteral());
        }
    }

    private bool IsAtEnd => _position >= _source.Length;

    private void EnterCollectionLiteral()
    {
        _braceContexts.Push(BraceContextKind.CollectionLiteral);
        _expressionDepth++;
    }

    private void ExitBraceContext()
    {
        if (_braceContexts.TryPop(out var context) &&
            context == BraceContextKind.CollectionLiteral &&
            _expressionDepth > 0)
        {
            _expressionDepth--;
        }
    }

    private char Current => IsAtEnd ? '\0' : _source[_position];

    private char Peek(int offset = 1)
    {
        var index = _position + offset;
        return index < _source.Length ? _source[index] : '\0';
    }

    private void SkipWhitespace()
    {
        while (!IsAtEnd && char.IsWhiteSpace(Current))
        {
            _position++;
        }
    }

    /// <summary>
    /// True when the character following a lone <c>#</c> makes it a line-comment
    /// opener rather than a bareword character. Delegates to
    /// <see cref="ToshCommentSyntax"/>, which is the one definition of the rule.
    /// </summary>
    private static bool IsCommentTerminator(char next)
        => ToshCommentSyntax.IsLineCommentTerminator(next);

    private void SkipComment()
    {
        // Capture the comment body so we can recognize inline directives like
        //   echo $value  # hush tosh.naming.shadowed_underscore
        var hashStart = _position;
        var bodyStart = _position + 1; // skip leading '#'
        while (!IsAtEnd && Current != '\n')
        {
            _position++;
        }
        var bodyEnd = _position;
        var body = bodyEnd > bodyStart ? _source.Substring(bodyStart, bodyEnd - bodyStart) : string.Empty;
        if (body.Length > 0)
        {
            TryRecordHushDirective(bodyStart, body);
        }
        var line = GetLineNumber(hashStart);
        var isFullLine = IsFullLineComment(hashStart);
        _lineComments.Add(new LineComment(
            Position: hashStart,
            EndPosition: bodyEnd,
            Line: line,
            IsFullLine: isFullLine,
            Text: "#" + body));
    }

    private bool IsFullLineComment(int hashStart)
    {
        // A comment is "full-line" if only whitespace precedes it on its line.
        var i = hashStart - 1;
        while (i >= 0 && _source[i] != '\n')
        {
            if (!char.IsWhiteSpace(_source[i])) return false;
            i--;
        }
        return true;
    }

    private void TryRecordHushDirective(int bodyStart, string body)
    {
        // Accept `# hush <code1>[,<code2>...]` at the start of the comment body
        // (after the leading `#` already consumed). Whitespace and a leading
        // colon are tolerated to keep the directive unobtrusive.
        var trimmed = body.TrimStart();
        if (trimmed.Length < 5)
        {
            return;
        }
        if (!(trimmed.StartsWith("hush ", StringComparison.Ordinal) ||
              trimmed.StartsWith("hush\t", StringComparison.Ordinal)))
        {
            return;
        }
        var rest = trimmed[4..].Trim();
        if (rest.Length == 0)
        {
            return;
        }

        // Strip a trailing comment ender (none in tosh, but be defensive).
        // Split on whitespace and commas; accept any token shaped like a tosh code.
        var line = GetLineNumber(bodyStart);
        foreach (var raw in rest.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length == 0 || !token.StartsWith("tosh.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            _lineHushDirectives.Add(new LineHushDirective(line, token));
        }
    }

    private int GetLineNumber(int offset)
    {
        // 1-based line number for an offset into _source.
        var line = 1;
        var bound = Math.Min(offset, _source.Length);
        for (var i = 0; i < bound; i++)
        {
            if (_source[i] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    private void SkipBlockComment()
    {
        var start = _position;
        _position += 3; // skip ##{

        while (!IsAtEnd)
        {
            if (Current == '}' && Peek() == '#' && Peek(2) == '#')
            {
                _position += 3; // skip }##
                return;
            }

            _position++;
        }

        // An unterminated block comment used to consume the rest of the
        // file in silence, so every statement after it simply never ran
        // and nothing said why (TS-P2-06).
        throw new LexerDiagnosticException(new SyntaxDiagnostic(
            Code: "tosh.parser.unterminated_block_comment",
            Title: "Block comments must be closed.",
            Span: new TextSpan(start, Math.Max(3, _position - start)),
            Label: "this '##{' comment never closes",
            Help: "close the comment with '}##'; everything after an unclosed block comment is ignored."));
    }

    private SyntaxToken ReadDocComment()
    {
        var start = _position;
        _position += 2; // skip ##

        // Skip optional leading space after ##
        if (!IsAtEnd && Current == ' ')
            _position++;

        var textStart = _position;
        while (!IsAtEnd && Current != '\n')
            _position++;

        var text = _source[textStart.._position].TrimEnd();
        return new SyntaxToken(SyntaxTokenKind.DocComment, start, text);
    }

    private SyntaxToken ReadString()
    {
        var quote = Current;
        var isRaw = quote == '\'';
        var start = _position;
        _position++;

        var builder = new StringBuilder();

        while (!IsAtEnd)
        {
            var current = Current;
            _position++;

            if (current == quote)
            {
                var text = _source[start.._position];
                return new SyntaxToken(SyntaxTokenKind.String, start, text, builder.ToString());
            }

            if (!isRaw && current == '\\' && !IsAtEnd)
            {
                builder.Append(ReadEscapeSequence());
                continue;
            }

            builder.Append(current);
        }

        throw new LexerDiagnosticException(new SyntaxDiagnostic(
            Code: "tosh.parser.unterminated_string",
            Title: "String literals must be terminated.",
            Span: new TextSpan(start, Math.Max(1, _position - start)),
            Label: "this string never closes",
            Help: "close the string with a matching quote."));
    }

    private string ReadEscapeSequence()
    {
        var escaped = Current;
        _position++;

        return escaped switch
        {
            '\\' => "\\",
            '"' => "\"",
            '\'' => "'",
            'n' => "\n",
            'r' => "\r",
            't' => "\t",
            'e' or 'E' => "\x1B",
            'a' => "\a",
            'b' => "\b",
            'f' => "\f",
            'v' => "\v",
            '0' => "\0",
            _ => $"\\{escaped}",
        };
    }

    private SyntaxToken ReadInterpolatedString()
    {
        // Current is '$', Peek() is '"'
        var start = _position;
        _position += 2; // skip $"

        var parts = new List<InterpolatedStringPart>();
        var literal = new StringBuilder();

        while (!IsAtEnd)
        {
            var current = Current;

            if (current == '"')
            {
                _position++;
                if (literal.Length > 0)
                {
                    parts.Add(new InterpolatedStringLiteralPart(literal.ToString()));
                }

                var text = _source[start.._position];
                return new SyntaxToken(SyntaxTokenKind.InterpolatedString, start, text, parts.AsReadOnly());
            }

            if (current == '\\' && !IsAtEnd)
            {
                _position++;
                literal.Append(ReadEscapeSequence());
                continue;
            }

            if (current == '{')
            {
                if (Peek() == '{')
                {
                    // Escaped brace {{ → literal {
                    literal.Append('{');
                    _position += 2;
                    continue;
                }

                // Flush pending literal
                if (literal.Length > 0)
                {
                    parts.Add(new InterpolatedStringLiteralPart(literal.ToString()));
                    literal.Clear();
                }

                // Read expression until matching }
                _position++; // skip {
                var exprStart = _position;
                SkipInterpolatedExpression();

                var rawExpression = _source[exprStart.._position];
                var (trimmedStart, trimmedLength) = TrimSpan(rawExpression, exprStart);
                var expression = rawExpression.Trim();
                parts.Add(new InterpolatedStringExpressionPart(
                    expression,
                    new TextSpan(trimmedStart, trimmedLength)));

                if (!IsAtEnd) _position++; // skip closing }
                continue;
            }

            if (current == '}')
            {
                if (Peek() == '}')
                {
                    // Escaped brace }} → literal }
                    literal.Append('}');
                    _position += 2;
                    continue;
                }
            }

            literal.Append(current);
            _position++;
        }

        throw new LexerDiagnosticException(new SyntaxDiagnostic(
            Code: "tosh.parser.unterminated_interpolated_string",
            Title: "Interpolated string literals must be terminated.",
            Span: new TextSpan(start, Math.Max(1, _position - start)),
            Label: "this $\"...\" string never closes",
            Help: "close the string with a matching double quote."));
    }

    private SyntaxToken ReadAnsiCString()
    {
        // Current is '$', Peek() is '\''
        var start = _position;
        _position += 2; // skip $'

        var builder = new StringBuilder();

        while (!IsAtEnd)
        {
            var current = Current;
            _position++;

            if (current == '\'')
            {
                var text = _source[start.._position];
                return new SyntaxToken(SyntaxTokenKind.String, start, text, builder.ToString());
            }

            if (current == '\\' && !IsAtEnd)
            {
                builder.Append(ReadAnsiCEscapeSequence());
                continue;
            }

            builder.Append(current);
        }

        throw new LexerDiagnosticException(new SyntaxDiagnostic(
            Code: "tosh.parser.unterminated_ansi_c_string",
            Title: "ANSI-C string literals must be terminated.",
            Span: new TextSpan(start, Math.Max(1, _position - start)),
            Label: "this $'...' string never closes",
            Help: "close the string with a matching single quote."));
    }

    private string ReadAnsiCEscapeSequence()
    {
        var escaped = Current;
        _position++;

        switch (escaped)
        {
            case '\\': return "\\";
            case '\'': return "'";
            case '"': return "\"";
            case 'a': return "\a";
            case 'b': return "\b";
            case 'e' or 'E': return "\x1B";
            case 'f': return "\f";
            case 'n': return "\n";
            case 'r': return "\r";
            case 't': return "\t";
            case 'v': return "\v";
            case '0':
                {
                    // Octal: \0nnn (up to 3 octal digits after the leading 0)
                    var value = 0;
                    for (var i = 0; i < 3 && !IsAtEnd && Current is >= '0' and <= '7'; i++)
                    {
                        value = (value * 8) + (Current - '0');
                        _position++;
                    }
                    return ((char)value).ToString();
                }
            case 'x':
                {
                    // Hex: \xHH (up to 2 hex digits)
                    var value = 0;
                    var digits = 0;
                    for (; digits < 2 && !IsAtEnd && IsHexDigit(Current); digits++)
                    {
                        value = (value * 16) + HexValue(Current);
                        _position++;
                    }
                    return digits == 0 ? "\\x" : ((char)value).ToString();
                }
            case 'u':
                {
                    // Unicode: \uHHHH (up to 4 hex digits)
                    var value = 0;
                    var digits = 0;
                    for (; digits < 4 && !IsAtEnd && IsHexDigit(Current); digits++)
                    {
                        value = (value * 16) + HexValue(Current);
                        _position++;
                    }
                    return digits == 0 ? "\\u" : ((char)value).ToString();
                }
            default:
                return $"\\{escaped}";
        }
    }

    private static bool IsHexDigit(char c) =>
        c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0,
    };

    // --- Triple-quoted string methods ---

    private SyntaxToken ReadTripleQuotedRawString(char quote)
    {
        // Current is the first quote of """ or '''
        var start = _position;
        _position += 3; // skip opening triple-quote

        var builder = new StringBuilder();

        while (!IsAtEnd)
        {
            if (Current == quote && Peek() == quote && Peek(2) == quote)
            {
                _position += 3; // skip closing triple-quote
                var text = _source[start.._position];
                return new SyntaxToken(SyntaxTokenKind.String, start, text, TrimMultilineIndentation(builder.ToString()));
            }

            builder.Append(Current);
            _position++;
        }

        throw new LexerDiagnosticException(new SyntaxDiagnostic(
            Code: "tosh.parser.unterminated_triple_quoted_string",
            Title: "Triple-quoted string literals must be terminated.",
            Span: new TextSpan(start, Math.Max(1, _position - start)),
            Label: $"this {quote}{quote}{quote}...{quote}{quote}{quote} string never closes",
            Help: $"close the string with {quote}{quote}{quote}."));
    }

    private SyntaxToken ReadTripleQuotedAnsiCString()
    {
        // Current is the first ' of '''
        var start = _position;
        _position += 3; // skip opening '''

        var builder = new StringBuilder();

        while (!IsAtEnd)
        {
            if (Current == '\'' && Peek() == '\'' && Peek(2) == '\'')
            {
                _position += 3; // skip closing '''
                var text = _source[start.._position];
                return new SyntaxToken(SyntaxTokenKind.String, start, text, TrimMultilineIndentation(builder.ToString()));
            }

            if (Current == '\\')
            {
                _position++; // skip backslash
                if (!IsAtEnd)
                {
                    builder.Append(ReadAnsiCEscapeSequence());
                }
                continue;
            }

            builder.Append(Current);
            _position++;
        }

        throw new LexerDiagnosticException(new SyntaxDiagnostic(
            Code: "tosh.parser.unterminated_triple_quoted_string",
            Title: "Triple-quoted string literals must be terminated.",
            Span: new TextSpan(start, Math.Max(1, _position - start)),
            Label: "this '''...''' string never closes",
            Help: "close the string with '''."));
    }

    private SyntaxToken ReadTripleQuotedInterpolatedString()
    {
        // Current is '$', next three are """
        var start = _position;
        _position += 4; // skip $"""

        var parts = new List<InterpolatedStringPart>();
        var literal = new StringBuilder();
        var isFirstLiteral = true;

        while (!IsAtEnd)
        {
            if (Current == '"' && Peek() == '"' && Peek(2) == '"')
            {
                _position += 3; // skip closing """
                FlushTripleQuotedInterpolatedLiteral(parts, literal, isFirstLiteral, isClosing: true);

                var text = _source[start.._position];
                return new SyntaxToken(SyntaxTokenKind.InterpolatedString, start, text, parts.AsReadOnly());
            }

            if (Current == '{')
            {
                if (Peek() == '{')
                {
                    literal.Append('{');
                    _position += 2;
                    continue;
                }

                FlushTripleQuotedInterpolatedLiteral(parts, literal, isFirstLiteral, isClosing: false);
                isFirstLiteral = false;

                _position++; // skip {
                var exprStart = _position;
                SkipInterpolatedExpression();

                var rawExpression = _source[exprStart.._position];
                var (trimmedStart, trimmedLength) = TrimSpan(rawExpression, exprStart);
                var expression = rawExpression.Trim();
                parts.Add(new InterpolatedStringExpressionPart(
                    expression,
                    new TextSpan(trimmedStart, trimmedLength)));
                if (!IsAtEnd) _position++; // skip closing }
                continue;
            }

            if (Current == '}')
            {
                if (Peek() == '}')
                {
                    literal.Append('}');
                    _position += 2;
                    continue;
                }
            }

            literal.Append(Current);
            _position++;
        }

        throw new LexerDiagnosticException(new SyntaxDiagnostic(
            Code: "tosh.parser.unterminated_triple_quoted_string",
            Title: "Triple-quoted interpolated string literals must be terminated.",
            Span: new TextSpan(start, Math.Max(1, _position - start)),
            Label: "this $\"\"\"...\"\"\" string never closes",
            Help: "close the string with \"\"\"."));
    }

    private SyntaxToken ReadTripleQuotedInterpolatedAnsiCString()
    {
        // Current is '$', next three are '''
        var start = _position;
        _position += 4; // skip $'''

        var parts = new List<InterpolatedStringPart>();
        var literal = new StringBuilder();
        var isFirstLiteral = true;

        while (!IsAtEnd)
        {
            if (Current == '\'' && Peek() == '\'' && Peek(2) == '\'')
            {
                _position += 3; // skip closing '''
                FlushTripleQuotedInterpolatedLiteral(parts, literal, isFirstLiteral, isClosing: true);

                var text = _source[start.._position];
                return new SyntaxToken(SyntaxTokenKind.InterpolatedString, start, text, parts.AsReadOnly());
            }

            if (Current == '\\')
            {
                _position++; // skip backslash
                if (!IsAtEnd)
                {
                    literal.Append(ReadAnsiCEscapeSequence());
                }
                continue;
            }

            if (Current == '{')
            {
                if (Peek() == '{')
                {
                    literal.Append('{');
                    _position += 2;
                    continue;
                }

                FlushTripleQuotedInterpolatedLiteral(parts, literal, isFirstLiteral, isClosing: false);
                isFirstLiteral = false;

                _position++; // skip {
                var exprStart = _position;
                SkipInterpolatedExpression();

                var rawExpression = _source[exprStart.._position];
                var (trimmedStart, trimmedLength) = TrimSpan(rawExpression, exprStart);
                var expression = rawExpression.Trim();
                parts.Add(new InterpolatedStringExpressionPart(
                    expression,
                    new TextSpan(trimmedStart, trimmedLength)));
                if (!IsAtEnd) _position++; // skip closing }
                continue;
            }

            if (Current == '}')
            {
                if (Peek() == '}')
                {
                    literal.Append('}');
                    _position += 2;
                    continue;
                }
            }

            literal.Append(Current);
            _position++;
        }

        throw new LexerDiagnosticException(new SyntaxDiagnostic(
            Code: "tosh.parser.unterminated_triple_quoted_string",
            Title: "Triple-quoted interpolated ANSI-C string literals must be terminated.",
            Span: new TextSpan(start, Math.Max(1, _position - start)),
            Label: "this $'''...''' string never closes",
            Help: "close the string with '''."));
    }

    private static void FlushTripleQuotedInterpolatedLiteral(
        List<InterpolatedStringPart> parts,
        StringBuilder literal,
        bool isFirstLiteral,
        bool isClosing)
    {
        if (literal.Length == 0)
        {
            return;
        }

        var text = literal.ToString();
        literal.Clear();

        // Strip leading newline on the very first literal part
        if (isFirstLiteral)
        {
            if (text.Length > 0 && text[0] == '\n')
            {
                text = text[1..];
            }
            else if (text.Length > 1 && text[0] == '\r' && text[1] == '\n')
            {
                text = text[2..];
            }
        }

        // Strip trailing whitespace-only closing line
        if (isClosing)
        {
            var lastNewline = text.LastIndexOf('\n');

            if (lastNewline >= 0)
            {
                var closingLine = text[(lastNewline + 1)..];

                if (closingLine.Length == 0 || closingLine.All(char.IsWhiteSpace))
                {
                    text = text[..lastNewline];
                }
            }
        }

        if (text.Length > 0)
        {
            parts.Add(new InterpolatedStringLiteralPart(text));
        }
    }

    private static string TrimMultilineIndentation(string content)
    {
        // Skip leading newline immediately after opening triple-quote
        if (content.Length > 0 && content[0] == '\n')
        {
            content = content[1..];
        }
        else if (content.Length > 1 && content[0] == '\r' && content[1] == '\n')
        {
            content = content[2..];
        }

        // Strip trailing newline + whitespace on the closing line
        // and use the closing line's indentation as the trim amount
        var lastNewline = content.LastIndexOf('\n');
        var indent = 0;

        if (lastNewline >= 0)
        {
            var closingLine = content[(lastNewline + 1)..];

            if (closingLine.Length == 0 || closingLine.All(char.IsWhiteSpace))
            {
                indent = closingLine.Length;
                content = content[..lastNewline];
            }
        }

        if (indent == 0)
        {
            return content;
        }

        // Remove the common indentation from each line
        var lines = content.Split('\n');
        var builder = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.Length == 0)
            {
                // Preserve empty lines
            }
            else if (line.Length >= indent && line[..indent].All(char.IsWhiteSpace))
            {
                line = line[indent..];
            }
            else
            {
                line = line.TrimStart();
            }

            builder.Append(line);

            if (i < lines.Length - 1)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    private SyntaxToken ReadBarewordOrLiteral()
    {
        var start = _position;

        while (!IsAtEnd)
        {
            if (char.IsWhiteSpace(Current))
            {
                break;
            }

            // Keep glob alternation like `*.@(txt,png)` inside a single bareword token.
            if (Current == '@' && Peek() == '(')
            {
                ReadGlobAlternation();
                continue;
            }

            // Break on '..' range operator when the text so far is numeric,
            // so that '2..10' lexes as Number DotDot Number instead of one bareword.
            // Signed and floating-point prefixes still split here so the parser can
            // either form an integer range or issue its range-specific diagnostic.
            // Don't break on '...' (path component) or non-numeric prefixes like '../'.
            if (Current == '.' && Peek() == '.' && Peek(2) != '.'
                && _position > start
                && IsNumericRangePrefix(_source.AsSpan(start, _position - start)))
            {
                break;
            }

            // Fused safe navigation (TS-P2-04): `$x?.Length` must mean the
            // same thing as `$x ?. Length`. Only break when the text so far
            // is a variable reference, so nullable forms such as `string?`
            // and `name?` — and any `?` not followed by `.` — are untouched.
            if (Current == '?' && Peek() == '.'
                && _position > start
                && _source[start] == '$')
            {
                break;
            }

            // Named arguments (TS-P2-15): inside a parenthesised or
            // bracketed expression, `f(a="z")` must bind like `f(a = "z")`.
            // Restricted to a bare identifier followed by a single '=', so
            // option-style command arguments such as `--opt=value` and the
            // comparison operators keep their greedy behaviour.
            // `TS-P2-02`. A lone `-` or `+` before a variable is a unary operator, not
            // the first character of a word: `-$x` was scanned whole and reported as
            // "Command '-$x' was not found", while the spaced `- $x` parsed. Narrow on
            // purpose — only when the text so far is exactly one sign character, so
            // flags (`--name`), paths and `a$b` are untouched — and only in expression
            // context, where `-$x` cannot be a command's flag.
            if (Current == '$' && InExpressionContext
                && _position == start + 1
                && _source[start] is '-' or '+')
            {
                break;
            }

            if (Current == '=' && Peek() != '=' && InExpressionContext
                && _position > start
                && IsIdentifierText(_source.AsSpan(start, _position - start)))
            {
                break;
            }

            // '=>' ends a bareword wherever it appears, so the spacing of
            // an arrow never changes its meaning: `{%a=>1%}` lexes the same
            // way as `{% a => 1 %}`. Without this the reader swallowed the
            // '=' and left a stray '>' behind, and the compact spelling
            // silently failed to parse as a dict entry.
            if (Current == '=' && Peek() == '>' && _position > start)
            {
                break;
            }

            // A lone '#' inside a bareword is never a comment: a comment opener has
            // to stand alone as a word, and by construction this '#' is preceded by
            // the bareword we are already reading. So `issue#42`, `#ff0000` and
            // `C#` all stay whole. Only '##' breaks out, because doc comments and
            // '##{' blocks keep their meaning wherever they appear.
            if (Current == '#' && Peek() == '#')
            {
                break;
            }

            // Keep lone '?' inside barewords so nullable type/identifier forms like
            // 'string?' and 'name?' keep lexing as a single token. The dedicated
            // '??' and '?.' tokens are already handled before we reach this path.
            if (Current is '|' or '(' or ')' or '{' or '}' or '[' or ']' or ';' or ',' or '>' or '<' or '&' or '!')
            {
                break;
            }

            // A collection-literal closer ends the bareword (TS-P2-25). `|` is
            // already a terminator above, which is why `{|a=1|}` works, but `:`
            // and `%` are ordinary bareword characters — without this, `{%a=>1%}`
            // reads `1%` as one word and the literal never closes. The lookahead
            // keeps the adjacency rule honest in the other direction too: a lone
            // `:` or `%` not followed by `}` stays part of the word, so record
            // shorthand keys and modulo expressions are untouched.
            if (Current is ':' or '%' && Peek() == '}')
            {
                break;
            }

            _position++;
        }

        var text = _source[start.._position];

        // Unit literal: number`unit (e.g. 100`m, 9.8`m/s^2, 1_000`kg)
        var backtickIndex = text.IndexOf('`');
        if (backtickIndex > 0 && backtickIndex < text.Length - 1)
        {
            var numPart = text[..backtickIndex];
            var unitPart = text[(backtickIndex + 1)..];

            // Strip underscore separators from the numeric part
            var numForParsing = numPart.Contains('_') ? numPart.Replace("_", "", StringComparison.Ordinal) : numPart;

            if (double.TryParse(numForParsing, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var magnitude)
                && UnitExpressionParser.TryParse(unitPart, out var unitFactor, out var dimension, out var normalizedSymbol))
            {
                var baseValue = magnitude * unitFactor;
                var quantity = Quantity.FromParsed(baseValue, magnitude, dimension, normalizedSymbol);
                return new SyntaxToken(SyntaxTokenKind.UnitLiteral, start, text, quantity);
            }
        }

        if (TryParseImaginaryLiteral(text, out var imaginaryValue))
        {
            return new SyntaxToken(SyntaxTokenKind.Number, start, text, imaginaryValue);
        }

        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            return new SyntaxToken(SyntaxTokenKind.Boolean, start, text, true);
        }

        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            return new SyntaxToken(SyntaxTokenKind.Boolean, start, text, false);
        }

        if (string.Equals(text, "null", StringComparison.Ordinal))
        {
            return new SyntaxToken(SyntaxTokenKind.Null, start, text, null);
        }

        // Digit separators (TS-P2-05). A leading underscore means this is
        // an identifier such as `_1`, not a number, so separator handling
        // is skipped entirely and the text falls through to a bareword.
        // Only numeric-looking text is validated, leaving ordinary
        // identifiers like `my_var` alone.
        if (text.Contains('_') && LooksNumeric(text) && !HasValidDigitSeparators(text))
        {
            throw new LexerDiagnosticException(new SyntaxDiagnostic(
                Code: "tosh.parser.invalid_numeric_separator",
                Title: "Digit separators must sit between digits.",
                Span: new TextSpan(start, text.Length),
                Label: "'_' may not lead, trail, or repeat inside a number",
                Help: "write the number as, for example, 1_000_000."));
        }

        var textForParsing = text.Contains('_') && LooksNumeric(text)
            ? text.Replace("_", "", StringComparison.Ordinal)
            : text;

        // Hex: 0x/0X prefix
        if (textForParsing.Length > 2 && textForParsing[0] == '0' && textForParsing[1] is 'x' or 'X')
        {
            if (long.TryParse(textForParsing.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
            {
                object boxed = hexValue is >= int.MinValue and <= int.MaxValue ? (object)(int)hexValue : hexValue;
                return new SyntaxToken(SyntaxTokenKind.Number, start, text, boxed);
            }
        }

        // Binary: 0b/0B prefix
        if (textForParsing.Length > 2 && textForParsing[0] == '0' && textForParsing[1] is 'b' or 'B')
        {
            try
            {
                var binValue = Convert.ToInt64(textForParsing[2..], 2);
                object boxed = binValue is >= int.MinValue and <= int.MaxValue ? (object)(int)binValue : binValue;
                return new SyntaxToken(SyntaxTokenKind.Number, start, text, boxed);
            }
            catch (FormatException) { /* not a valid binary literal */ }
            catch (OverflowException)
            {
                throw CreateNumericOverflowDiagnostic(text, start, "binary");
            }
        }

        // Octal: 0o/0O prefix
        if (textForParsing.Length > 2 && textForParsing[0] == '0' && textForParsing[1] is 'o' or 'O')
        {
            try
            {
                var octValue = Convert.ToInt64(textForParsing[2..], 8);
                object boxed = octValue is >= int.MinValue and <= int.MaxValue ? (object)(int)octValue : octValue;
                return new SyntaxToken(SyntaxTokenKind.Number, start, text, boxed);
            }
            catch (FormatException) { /* not a valid octal literal */ }
            catch (OverflowException)
            {
                throw CreateNumericOverflowDiagnostic(text, start, "octal");
            }
        }

        if (long.TryParse(textForParsing, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integerValue))
        {
            object boxed = integerValue is >= int.MinValue and <= int.MaxValue
                ? (object)(int)integerValue
                : integerValue;
            return new SyntaxToken(SyntaxTokenKind.Number, start, text, boxed);
        }

        if (double.TryParse(textForParsing, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var floatingValue))
        {
            return new SyntaxToken(SyntaxTokenKind.Number, start, text, floatingValue);
        }

        return new SyntaxToken(SyntaxTokenKind.Bareword, start, text, text);
    }

    /// <summary>
    /// True when text is shaped like a number, so digit-separator rules
    /// apply to it. Identifiers such as <c>my_var</c> and <c>_1</c> are
    /// excluded: a leading underscore makes it a name, not a literal.
    /// </summary>
    private static bool LooksNumeric(string text)
    {
        if (text.Length == 0) return false;
        if (char.IsAsciiDigit(text[0])) return true;
        return text.Length > 1
            && text[0] is '-' or '+' or '.'
            && char.IsAsciiDigit(text[1]);
    }

    /// <summary>
    /// Every separator must sit between two digits of the literal's own
    /// radix, so <c>1_000</c> is accepted while <c>1__2</c>, <c>1_</c>,
    /// and <c>0x_FF</c> are not.
    /// </summary>
    private static bool HasValidDigitSeparators(string text)
    {
        var body = text.AsSpan();
        var offset = 0;

        // Skip a sign and any radix prefix; a separator may not sit
        // immediately after either.
        if (body.Length > 0 && (body[0] == '-' || body[0] == '+')) offset = 1;
        if (body.Length > offset + 1
            && body[offset] == '0'
            && char.ToLowerInvariant(body[offset + 1]) is 'x' or 'b' or 'o')
        {
            offset += 2;
        }

        for (var i = offset; i < body.Length; i++)
        {
            if (body[i] != '_') continue;

            var hasLeft = i > offset && IsRadixDigit(body[i - 1]);
            var hasRight = i + 1 < body.Length && IsRadixDigit(body[i + 1]);
            if (!hasLeft || !hasRight) return false;
        }

        return true;
    }

    private static bool IsRadixDigit(char c) =>
        char.IsAsciiDigit(c) || (char.ToLowerInvariant(c) is >= 'a' and <= 'f');

    private static LexerDiagnosticException CreateNumericOverflowDiagnostic(
        string text,
        int start,
        string radix)
    {
        return new LexerDiagnosticException(new SyntaxDiagnostic(
            Code: "tosh.parser.numeric_literal_overflow",
            Title: $"This {radix} literal is too large for a 64-bit integer.",
            Span: new TextSpan(start, text.Length),
            Label: "the value does not fit in 64 bits",
            Help: "use a smaller value, or compute it at runtime where a wider numeric type applies."));
    }

    private static bool TryParseImaginaryLiteral(string text, out Complex value)
    {
        value = default;

        if (text.Length < 2 || text[^1] is not ('i' or 'I'))
        {
            return false;
        }

        var coefficientText = text[..^1];
        if (string.IsNullOrWhiteSpace(coefficientText))
        {
            return false;
        }

        if (!coefficientText.Any(char.IsDigit))
        {
            return false;
        }

        var coefficientForParsing = coefficientText.Contains('_')
            ? coefficientText.Replace("_", string.Empty, StringComparison.Ordinal)
            : coefficientText;

        if (!double.TryParse(
                coefficientForParsing,
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var coefficient))
        {
            return false;
        }

        value = new Complex(0d, coefficient);
        return true;
    }

    private void ReadGlobAlternation()
    {
        _position += 2; // consume @(
        var depth = 1;

        while (!IsAtEnd && depth > 0)
        {
            if (Current == '@' && Peek() == '(')
            {
                depth++;
                _position += 2;
                continue;
            }

            if (Current == ')')
            {
                depth--;
                _position++;
                continue;
            }

            _position++;
        }
    }

    private static bool IsRangeOperatorContext(SyntaxToken previousToken)
    {
        return previousToken.Kind switch
        {
            SyntaxTokenKind.Number => true,
            SyntaxTokenKind.CloseParen => true,
            // Variable references like $n
            SyntaxTokenKind.Bareword when previousToken.Text.StartsWith('$') => true,
            // Pipeline item _
            SyntaxTokenKind.Bareword when previousToken.Text == "_" => true,
            _ => false,
        };
    }

    /// <summary>
    /// True for a plain identifier: a letter or underscore followed by
    /// letters, digits, or underscores. Deliberately excludes hyphens so
    /// an option-style argument such as <c>--name=value</c> is not split
    /// at its '='.
    /// </summary>
    private static bool IsIdentifierText(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return false;
        if (!char.IsLetter(text[0]) && text[0] != '_') return false;
        foreach (var ch in text)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_') return false;
        }
        return true;
    }

    private static bool IsNumericRangePrefix(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        var normalized = text.Contains('_')
            ? text.ToString().Replace("_", string.Empty, StringComparison.Ordinal)
            : text.ToString();

        if (long.TryParse(
                normalized,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _))
        {
            return true;
        }

        if (double.TryParse(
                normalized,
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out _))
        {
            return true;
        }

        if (normalized.Length <= 2 || normalized[0] != '0')
        {
            return false;
        }

        return normalized[1] switch
        {
            'x' or 'X' => long.TryParse(
                normalized.AsSpan(2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out _),
            'b' or 'B' => TryParseRadixInteger(normalized.AsSpan(2), 2),
            'o' or 'O' => TryParseRadixInteger(normalized.AsSpan(2), 8),
            _ => false,
        };
    }

    private static bool TryParseRadixInteger(ReadOnlySpan<char> digits, int radix)
    {
        if (digits.IsEmpty)
        {
            return false;
        }

        try
        {
            _ = Convert.ToInt64(digits.ToString(), radix);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Advances _position past the contents of an interpolated expression <c>{...}</c>,
    /// stopping when the matching <c>}</c> is reached (depth returns to 0).
    /// Tracks nested braces, and skips over string literals so that <c>}</c>
    /// characters inside strings do not prematurely close the expression.
    /// </summary>
    private static (int Start, int Length) TrimSpan(string raw, int rawStart)
    {
        var leading = 0;
        while (leading < raw.Length && char.IsWhiteSpace(raw[leading])) leading++;
        var trailing = raw.Length;
        while (trailing > leading && char.IsWhiteSpace(raw[trailing - 1])) trailing--;
        return (rawStart + leading, trailing - leading);
    }

    private void SkipInterpolatedExpression()
    {
        var depth = 1;

        while (!IsAtEnd && depth > 0)
        {
            var ch = Current;

            if (ch == '{')
            {
                depth++;
                _position++;
                continue;
            }

            if (ch == '}')
            {
                depth--;
                if (depth > 0) _position++;
                continue;
            }

            // Skip over string literals to avoid false brace matches
            if (ch is '"' or '\'')
            {
                SkipStringLiteralInExpression(ch);
                continue;
            }

            _position++;
        }
    }

    /// <summary>
    /// Skips a string literal starting at <c>Current</c> (which must be the opening quote).
    /// Double-quoted strings honor backslash escaping; single-quoted
    /// strings are raw and close at the next single quote.
    /// </summary>
    private void SkipStringLiteralInExpression(char quote)
    {
        _position++; // skip opening quote

        while (!IsAtEnd)
        {
            if (quote == '"' && Current == '\\' && !IsAtEnd)
            {
                _position += 2; // skip backslash and the escaped character
                continue;
            }

            if (Current == quote)
            {
                _position++; // skip closing quote
                return;
            }

            _position++;
        }
    }

    internal sealed class LexerDiagnosticException : Exception
    {
        public LexerDiagnosticException(SyntaxDiagnostic diagnostic)
        {
            Diagnostic = diagnostic;
        }

        public SyntaxDiagnostic Diagnostic { get; }
    }
}
