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

        // `TOAST-0027`. `\xHH` and `\uHHHH` resolve here as they do in an ANSI-C string.
        // They used to be kept as text — silently — so `"\u00E9"` was six characters while
        // `$'\u00E9'` was one, and nothing said which table a string was being read with.
        // The specification claimed the double-quoted spelling worked, and was wrong.
        //
        // This does not make a backslash newly dangerous: `\t`, `\n` and the rest already
        // resolved here, so `"C:\path\to"` already contained a tab. A backslash before any
        // *other* letter is still kept, which is what leaves `"\q"` alone.
        if (escaped is 'x') return ReadHexEscape(maximumDigits: 2, fallback: "\\x");
        if (escaped is 'u') return ReadHexEscape(maximumDigits: 4, fallback: "\\u");

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

    /// <summary>
    /// Reads up to <paramref name="maximumDigits"/> hex digits as one code unit, or gives
    /// back <paramref name="fallback"/> when there are none — `TOAST-0027`.
    /// </summary>
    /// <remarks>
    /// One implementation for both string kinds. They had a copy each, which is how they
    /// came to differ about whether `\u` was an escape at all.
    /// </remarks>
    private string ReadHexEscape(int maximumDigits, string fallback)
    {
        var value = 0;
        var digits = 0;

        for (; digits < maximumDigits && !IsAtEnd && IsHexDigit(Current); digits++)
        {
            value = (value * 16) + HexValue(Current);
            _position++;
        }

        return digits == 0 ? fallback : ((char)value).ToString();
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

                parts.Add(CreateInterpolationHolePart(exprStart));

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
            // `TOAST-0027`. Shared with `ReadEscapeSequence`, which used to lack both.
            case 'x': return ReadHexEscape(maximumDigits: 2, fallback: "\\x");
            case 'u': return ReadHexEscape(maximumDigits: 4, fallback: "\\u");
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

                parts.Add(CreateInterpolationHolePart(exprStart));
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

                parts.Add(CreateInterpolationHolePart(exprStart));
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

            // `TOSH-0001`. A quote *inside* a word protects what it encloses, spaces
            // included, so `--opt="a b"` is one argument rather than two. Consumed inline
            // like the glob alternation above, so the whitespace break at the top of this
            // loop never sees the inside of the quotes.
            //
            // Only when the partner is on the same line. An apostrophe in `don't` or an
            // inch mark in `5" pipe` has none, and swallowing the rest of the line looking
            // for one would be a worse defect than the one being fixed — these are words
            // people type at an interactive prompt.
            if (Current is '"' or '\'' && _position > start && HasMatchingQuoteOnLine(Current))
            {
                var quote = Current;
                _position++;

                while (!IsAtEnd && Current != quote && Current != '\n')
                {
                    _position++;
                }

                if (!IsAtEnd && Current == quote)
                {
                    _position++;
                }

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

        // Unit literals. The backtick is the general form; U+00B0 is the one
        // adjacency shorthand (90°, 20°C, 90°/s). Both routes share numeric
        // separator validation so quantity literals cannot bypass the ordinary
        // number rules.
        if (TrySplitUnitLiteral(text, out var numPart, out var unitPart))
        {
            if (numPart.Contains('_') && !HasValidDigitSeparators(numPart))
            {
                throw new LexerDiagnosticException(new SyntaxDiagnostic(
                    Code: "tosh.parser.invalid_numeric_separator",
                    Title: "Digit separators must sit between digits.",
                    Span: new TextSpan(start, numPart.Length),
                    Label: "'_' may not lead, trail, or repeat inside a number",
                    Help: "write the quantity as, for example, 1_000`m or 1_000°."));
            }

            var numForParsing = numPart.Contains('_') ? numPart.Replace("_", "", StringComparison.Ordinal) : numPart;

            if (!double.TryParse(
                    numForParsing,
                    NumberStyles.Float | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var magnitude))
            {
                throw new LexerDiagnosticException(new SyntaxDiagnostic(
                    Code: "tosh.parser.invalid_unit_magnitude",
                    Title: "A unit literal must begin with a valid number.",
                    Span: new TextSpan(start, numPart.Length),
                    Label: "this is not a valid numeric magnitude",
                    Help: "write a decimal magnitude before the unit, for example 5`km or 90°."));
            }

            if (!UnitExpressionParser.TryParseConversion(
                    unitPart,
                    out _,
                    out var dimension,
                    out var normalizedSymbol))
            {
                var unitLength = Math.Max(1, unitPart.Length);
                var unitStart = unitPart.Length == 0
                    ? start + text.Length - 1
                    : start + text.Length - unitPart.Length;
                throw new LexerDiagnosticException(new SyntaxDiagnostic(
                    Code: "tosh.parser.invalid_unit_literal",
                    Title: "A unit literal contains an unknown or invalid unit expression.",
                    Span: new TextSpan(unitStart, unitLength),
                    Label: unitPart.Length == 0
                        ? "a unit is required after the backtick"
                        : $"'{unitPart}' is not registered or composable",
                    Help: "use a known unit symbol; absolute temperature scales cannot be used inside compound units."));
            }

            var quantity = Quantity.FromParsed(magnitude, dimension, normalizedSymbol);
            return new SyntaxToken(SyntaxTokenKind.UnitLiteral, start, text, quantity);
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

        // Integer literals — every base, and the optional width suffix.
        //
        // `TS-P2-123`. Hex was parsed into a `long`, so sixteen F's became -1 by
        // two's complement and then *fitted `int`*, leaving `0xFFFFFFFFFFFFFFFF`
        // an `Int32 -1`: a 64-bit mask silently truncated to 32 bits. Decimal past
        // `long.MaxValue` fell through to `double`, so the upper half of `ulong`
        // could not be written at all.
        //
        // The rule now is one rule for every base: a literal takes the narrowest of
        // `int`, `long`, `ulong` that holds it, and one that fits none of them is a
        // diagnostic rather than a silently different number.
        if (TryCreateIntegerToken(text, textForParsing, start, out var integerToken))
        {
            return integerToken;
        }

        if (double.TryParse(textForParsing, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var floatingValue))
        {
            // `TOAST-0026`. A literal too precise for a `double` becomes a `decimal`
            // rather than being quietly rounded. `1.0000000000000001` used to arrive as
            // `1.0`, so `1.0000000000000001 as decimal` was already `1.0` before the cast
            // could do anything — the one type people reach for when rounding is
            // unacceptable was the one that could not be written down.
            //
            // The test is whether the `double` **kept** the value: its round-trip form is
            // compared against the literal, both read as decimals. Comparing against
            // `(decimal)theDouble` instead was tried and is too eager by two digits —
            // that conversion rounds to 15 significant figures, so `2.718281828459045`
            // widened although its `double` holds every digit of it.
            if (CarriesMorePrecisionThanDouble(textForParsing, floatingValue, out var exact))
            {
                return new SyntaxToken(SyntaxTokenKind.Number, start, text, exact);
            }

            return new SyntaxToken(SyntaxTokenKind.Number, start, text, floatingValue);
        }

        return new SyntaxToken(SyntaxTokenKind.Bareword, start, text, text);
    }

    /// <summary>
    /// Whether a literal's text holds digits its <see cref="double"/> did not keep, and if
    /// so the <see cref="decimal"/> that does — `TOAST-0026`.
    /// </summary>
    /// <remarks>
    /// A number outside `decimal`'s range answers <see langword="false"/>: `1e300` has no
    /// decimal to widen into, and a `double` is the only thing that can hold it. So is a
    /// non-finite one, which cannot be a decimal at all.
    ///
    /// The comparison is against the `double`'s **round-trip** form rather than against
    /// `(decimal)parsed`: that cast rounds to 15 significant figures, which widened
    /// `2.718281828459045` even though its `double` holds all sixteen of its digits.
    /// </remarks>
    private static bool CarriesMorePrecisionThanDouble(string text, double parsed, out decimal exact)
    {
        exact = default;

        if (double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            return false;
        }

        if (!decimal.TryParse(
                text,
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var fromText))
        {
            return false;
        }

        // What the `double` gives back when asked for every digit it holds. If reading
        // that as a decimal recovers the literal, nothing was lost and the `double`
        // stands.
        if (!decimal.TryParse(
                parsed.ToString("R", CultureInfo.InvariantCulture),
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var fromDouble))
        {
            // The double round-trips to something no decimal can read — an exponent far
            // outside decimal's range — so it is the only type that can hold this.
            return false;
        }

        if (fromText == fromDouble)
        {
            return false;
        }

        exact = fromText;
        return true;
    }

    /// <summary>
    /// The width suffixes, longest first so <c>ul</c> is tried before <c>u</c>.
    /// </summary>
    /// <remarks>
    /// Static because the array is otherwise rebuilt on every call, and this runs for
    /// every numeric-looking token in every file the session parses. It showed up as
    /// the single largest allocation site in a trace, which is a poor showing for a
    /// table of four constants.
    /// </remarks>
    private static readonly (string Text, IntegerSuffix Kind)[] IntegerSuffixes =
    [
        ("ul", IntegerSuffix.UnsignedLong),
        ("lu", IntegerSuffix.UnsignedLong),
        ("u", IntegerSuffix.Unsigned),
        ("l", IntegerSuffix.Long),
    ];

    /// <summary>The width a literal's suffix pins it to, if it carries one.</summary>
    private enum IntegerSuffix { None, Unsigned, Long, UnsignedLong }

    /// <summary>
    /// Classifies an integer literal in any base, honouring a trailing width suffix.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> for anything that is not an integer literal —
    /// a decimal point, an exponent, a bareword — so the caller can carry on to the
    /// floating-point and identifier readings. A literal that *is* an integer but
    /// fits no integer type throws, rather than quietly becoming a `double`.
    /// </remarks>
    private static bool TryCreateIntegerToken(string text, string textForParsing, int start, out SyntaxToken token)
    {
        token = default!;

        var digits = textForParsing;
        var suffix = IntegerSuffix.None;

        // `u`, `L`, `UL` — case-insensitive, and `LU` for the people who write it
        // that way. Stripped before parsing so every base sees plain digits.
        foreach (var (suffixText, kind) in IntegerSuffixes)
        {
            if (digits.Length > suffixText.Length &&
                digits.EndsWith(suffixText, StringComparison.OrdinalIgnoreCase))
            {
                digits = digits[..^suffixText.Length];
                suffix = kind;
                break;
            }
        }

        var negative = digits.StartsWith('-');
        var magnitude = negative || digits.StartsWith('+') ? digits[1..] : digits;

        var radix = 10;
        if (magnitude.Length > 2 && magnitude[0] == '0')
        {
            radix = magnitude[1] switch
            {
                'x' or 'X' => 16,
                'b' or 'B' => 2,
                'o' or 'O' => 8,
                _ => 10,
            };

            if (radix != 10)
            {
                magnitude = magnitude[2..];
            }
        }

        if (magnitude.Length == 0 || !IsAllDigitsForRadix(magnitude, radix))
        {
            return false;
        }

        ulong value;

        try
        {
            value = radix == 10
                ? ulong.Parse(magnitude, NumberStyles.None, CultureInfo.InvariantCulture)
                : Convert.ToUInt64(magnitude, radix);
        }
        catch (OverflowException)
        {
            throw CreateNumericOverflowDiagnostic(text, start, DescribeRadix(radix));
        }
        catch (FormatException)
        {
            return false;
        }

        if (negative)
        {
            // A signed literal is still bounded by `long`, and `-9223372036854775808`
            // is legal while its magnitude is not, so the negation is applied before
            // the range check rather than after.
            if (value > (ulong)long.MaxValue + 1)
            {
                throw CreateNumericOverflowDiagnostic(text, start, DescribeRadix(radix));
            }

            var signed = value == (ulong)long.MaxValue + 1 ? long.MinValue : -(long)value;
            token = new SyntaxToken(
                SyntaxTokenKind.Number,
                start,
                text,
                signed is >= int.MinValue and <= int.MaxValue && suffix is IntegerSuffix.None
                    ? (object)(int)signed
                    : signed);
            return true;
        }

        object boxed = suffix switch
        {
            // Boxed explicitly: `uint` converts implicitly to `ulong`, so the two
            // branches acquire a natural common type and the narrowing is undone —
            // `100u` came back a `UInt64`. The other arms have no such conversion
            // between them, so target-typing already boxes each separately.
            IntegerSuffix.Unsigned => value <= uint.MaxValue ? (object)(uint)value : value,
            IntegerSuffix.Long => value <= long.MaxValue
                ? (long)value
                : throw CreateNumericOverflowDiagnostic(text, start, DescribeRadix(radix)),
            IntegerSuffix.UnsignedLong => value,
            _ => value <= int.MaxValue ? (int)value
                : value <= long.MaxValue ? (long)value
                : value,
        };

        token = new SyntaxToken(SyntaxTokenKind.Number, start, text, boxed);
        return true;
    }

    private static bool IsAllDigitsForRadix(string digits, int radix)
    {
        foreach (var c in digits)
        {
            var ok = radix switch
            {
                16 => char.IsAsciiHexDigit(c),
                8 => c is >= '0' and <= '7',
                2 => c is '0' or '1',
                _ => char.IsAsciiDigit(c),
            };

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static string DescribeRadix(int radix) => radix switch
    {
        16 => "hexadecimal",
        8 => "octal",
        2 => "binary",
        _ => "decimal",
    };

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

    private static bool TrySplitUnitLiteral(string text, out string magnitude, out string unit)
    {
        magnitude = string.Empty;
        unit = string.Empty;

        var backtickIndex = text.IndexOf('`');
        if (backtickIndex > 0 && LooksLikeUnitMagnitude(text[..backtickIndex]))
        {
            magnitude = text[..backtickIndex];
            unit = text[(backtickIndex + 1)..];
            return true;
        }

        var degreeIndex = text.IndexOf('°');
        if (degreeIndex > 0 && LooksLikeUnitMagnitude(text[..degreeIndex]))
        {
            magnitude = text[..degreeIndex];
            unit = text[degreeIndex..];
            return true;
        }

        return false;
    }

    private static bool LooksLikeUnitMagnitude(string text)
    {
        if (text.Length == 0 || !text.Any(char.IsAsciiDigit)) return false;
        return char.IsAsciiDigit(text[0]) || text[0] is '+' or '-' or '.' or '_';
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
        var radix = '\0';

        // Skip a sign and any radix prefix; a separator may not sit
        // immediately after either.
        if (body.Length > 0 && (body[0] == '-' || body[0] == '+')) offset = 1;
        if (body.Length > offset + 1
            && body[offset] == '0'
            && char.ToLowerInvariant(body[offset + 1]) is 'x' or 'b' or 'o')
        {
            radix = char.ToLowerInvariant(body[offset + 1]);
            offset += 2;
        }

        for (var i = offset; i < body.Length; i++)
        {
            if (body[i] != '_') continue;

            var hasLeft = i > offset && IsDigitForRadix(body[i - 1], radix);
            var hasRight = i + 1 < body.Length && IsDigitForRadix(body[i + 1], radix);
            if (!hasLeft || !hasRight) return false;
        }

        return true;
    }

    private static bool IsDigitForRadix(char c, char radix) => radix switch
    {
        'x' => char.IsAsciiDigit(c) || char.ToLowerInvariant(c) is >= 'a' and <= 'f',
        'b' => c is '0' or '1',
        'o' => c is >= '0' and <= '7',
        _ => char.IsAsciiDigit(c),
    };

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

    /// <summary>
    /// Whether <paramref name="quote"/> has a partner before the end of the current line.
    /// </summary>
    /// <remarks>
    /// `TOSH-0001`. The lookahead is what keeps an apostrophe usable: `don't` has no closing
    /// quote, so the mark stays an ordinary character and the word still breaks at
    /// whitespace exactly as before.
    /// </remarks>
    private bool HasMatchingQuoteOnLine(char quote)
    {
        for (var index = _position + 1; index < _source.Length; index++)
        {
            if (_source[index] == '\n')
            {
                return false;
            }

            if (_source[index] == quote)
            {
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// Builds the hole part between <paramref name="exprStart"/> and the current
    /// position, splitting off its alignment and format clauses.
    /// </summary>
    /// <remarks>
    /// One helper because there are three interpolated-string forms — plain, triple
    /// and ANSI — and the block was copied into each. A format clause that worked in
    /// one and not the others would be exactly the kind of drift this project keeps
    /// finding (<c>TS-P3-06</c>).
    /// </remarks>
    private InterpolatedStringExpressionPart CreateInterpolationHolePart(int exprStart)
    {
        var rawExpression = _source[exprStart.._position];
        var (trimmedStart, trimmedLength) = TrimSpan(rawExpression, exprStart);
        var (expressionText, alignment, format) = SplitInterpolationClauses(rawExpression);
        var expression = expressionText.Trim();

        // The span covers the expression rather than the whole hole, so a diagnostic
        // underlines what the reader wrote and not the format clause after it.
        var span = new TextSpan(trimmedStart, Math.Min(trimmedLength, Math.Max(expression.Length, 1)));

        return new InterpolatedStringExpressionPart(expression, span, format, alignment);
    }

    /// <summary>
    /// Splits a hole into its expression, alignment and format clauses:
    /// <c>{expr,align:format}</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only *top-level* separators count — inside parentheses, brackets, braces or a
    /// string they belong to the expression, which is what keeps <c>{f(a, b)}</c> and
    /// <c>{$d["a:b"]}</c> whole.
    /// </para>
    /// <para>
    /// A ternary is the one real ambiguity, and C# has it too: in
    /// <c>{$x > 0 ? "a" : "b"}</c> the colon belongs to the conditional. It is told
    /// apart by counting <c>?</c> at the same level — a colon with an open question
    /// mark before it closes that conditional rather than starting a format clause.
    /// The null-coalescing <c>??</c> is not a conditional, so it opens nothing.
    /// </para>
    /// </remarks>
    private static (string Expression, int? Alignment, string? Format) SplitInterpolationClauses(string hole)
    {
        var depth = 0;
        var pendingConditionals = 0;
        var alignmentStart = -1;
        var formatStart = -1;

        for (var index = 0; index < hole.Length; index++)
        {
            var ch = hole[index];

            if (ch is '(' or '[' or '{')
            {
                depth++;
                continue;
            }

            if (ch is ')' or ']' or '}')
            {
                depth--;
                continue;
            }

            if (ch is '"' or '\'')
            {
                index = SkipQuotedRun(hole, index, ch);
                continue;
            }

            if (depth != 0)
            {
                continue;
            }

            if (ch == '?')
            {
                // `??` is null-coalescing, not a conditional, and opens nothing.
                if (index + 1 < hole.Length && hole[index + 1] == '?')
                {
                    index++;
                    continue;
                }

                pendingConditionals++;
                continue;
            }

            if (ch == ':')
            {
                if (pendingConditionals > 0)
                {
                    pendingConditionals--;
                    continue;
                }

                formatStart = index;
                break;
            }

            if (ch == ',' && alignmentStart < 0)
            {
                alignmentStart = index;
            }
        }

        var expressionEnd = formatStart >= 0 ? formatStart : hole.Length;
        var format = formatStart >= 0 ? hole[(formatStart + 1)..].Trim() : null;

        if (alignmentStart >= 0 && alignmentStart < expressionEnd)
        {
            var alignmentText = hole[(alignmentStart + 1)..expressionEnd].Trim();

            if (int.TryParse(alignmentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var alignment))
            {
                return (hole[..alignmentStart], alignment, format);
            }
        }

        return (hole[..expressionEnd], null, format);
    }

    /// <summary>Index of the closing quote of a run starting at <paramref name="start"/>.</summary>
    private static int SkipQuotedRun(string text, int start, char quote)
    {
        for (var index = start + 1; index < text.Length; index++)
        {
            if (text[index] == '\\')
            {
                index++;
                continue;
            }

            if (text[index] == quote)
            {
                return index;
            }
        }

        return text.Length - 1;
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
