using System.Globalization;
using System.Text;
using Tosh.Core;

namespace Tosh.Language.Parsing;

public sealed class ToshLexer
{
    private readonly string _source;
    private int _position;

    public ToshLexer(string source)
    {
        _source = source ?? string.Empty;
    }

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
                SkipComment();
                continue;
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

            // |
            if (Current == '|')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.Pipe, _position, "|"));
                _position++;
                continue;
            }

            // & (background)
            if (Current == '&')
            {
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

                tokens.Add(new SyntaxToken(SyntaxTokenKind.LessThan, _position, "<"));
                _position++;
                continue;
            }

            if (Current == '{')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.OpenBrace, _position, "{"));
                _position++;
                continue;
            }

            if (Current == '}')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.CloseBrace, _position, "}"));
                _position++;
                continue;
            }

            if (Current == '[')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.OpenBracket, _position, "["));
                _position++;
                continue;
            }

            if (Current == ']')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.CloseBracket, _position, "]"));
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
                _position++;
                continue;
            }

            if (Current == ')')
            {
                tokens.Add(new SyntaxToken(SyntaxTokenKind.CloseParen, _position, ")"));
                _position++;
                continue;
            }

            // Bare _ as current pipeline item — emit as its own token so
            // postfix chains like _.Name.ToString() work correctly
            if (Current == '_' && (Peek() is '.' or '?' or ' ' or '\t' or '\r' or '\n' or '\0'
                or '|' or '#' or '(' or ')' or '{' or '}' or '[' or ']' or ';' or ','
                or '>' or '<' or '&' or '!'))
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

    private void SkipComment()
    {
        while (!IsAtEnd && Current != '\n')
        {
            _position++;
        }
    }

    private SyntaxToken ReadString()
    {
        var quote = Current;
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

            if (current == '\\' && !IsAtEnd)
            {
                builder.Append(ReadEscapeSequence());
                continue;
            }

            builder.Append(current);
        }

        throw new LexerDiagnosticException(new SyntaxDiagnostic(
            Code: "tosh::parser::unterminated_string",
            Title: "String literals must be terminated.",
            Span: new TextSpan(start, Math.Max(1, _position - start)),
            Label: "this string never closes",
            Help: "close the string with a matching quote."));
    }

    private char ReadEscapeSequence()
    {
        var escaped = Current;
        _position++;

        return escaped switch
        {
            '\\' => '\\',
            '"' => '"',
            '\'' => '\'',
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            'e' or 'E' => '\x1B',
            'a' => '\a',
            'b' => '\b',
            'f' => '\f',
            'v' => '\v',
            '0' => '\0',
            _ => escaped,
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

                var expression = _source[exprStart.._position].Trim();
                parts.Add(new InterpolatedStringExpressionPart(expression));

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
            Code: "tosh::parser::unterminated_interpolated_string",
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
            Code: "tosh::parser::unterminated_ansi_c_string",
            Title: "ANSI-C string literals must be terminated.",
            Span: new TextSpan(start, Math.Max(1, _position - start)),
            Label: "this $'...' string never closes",
            Help: "close the string with a matching single quote."));
    }

    private char ReadAnsiCEscapeSequence()
    {
        var escaped = Current;
        _position++;

        switch (escaped)
        {
            case '\\': return '\\';
            case '\'': return '\'';
            case '"': return '"';
            case 'a': return '\a';
            case 'b': return '\b';
            case 'e' or 'E': return '\x1B';
            case 'f': return '\f';
            case 'n': return '\n';
            case 'r': return '\r';
            case 't': return '\t';
            case 'v': return '\v';
            case '0':
                {
                    // Octal: \0nnn (up to 3 octal digits after the leading 0)
                    var value = 0;
                    for (var i = 0; i < 3 && !IsAtEnd && Current is >= '0' and <= '7'; i++)
                    {
                        value = (value * 8) + (Current - '0');
                        _position++;
                    }
                    return (char)value;
                }
            case 'x':
                {
                    // Hex: \xHH (up to 2 hex digits)
                    var value = 0;
                    for (var i = 0; i < 2 && !IsAtEnd && IsHexDigit(Current); i++)
                    {
                        value = (value * 16) + HexValue(Current);
                        _position++;
                    }
                    return (char)value;
                }
            case 'u':
                {
                    // Unicode: \uHHHH (up to 4 hex digits)
                    var value = 0;
                    for (var i = 0; i < 4 && !IsAtEnd && IsHexDigit(Current); i++)
                    {
                        value = (value * 16) + HexValue(Current);
                        _position++;
                    }
                    return (char)value;
                }
            default:
                return escaped;
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
            Code: "tosh::parser::unterminated_triple_quoted_string",
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
            Code: "tosh::parser::unterminated_triple_quoted_string",
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

                var expression = _source[exprStart.._position].Trim();
                parts.Add(new InterpolatedStringExpressionPart(expression));
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
            Code: "tosh::parser::unterminated_triple_quoted_string",
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

                var expression = _source[exprStart.._position].Trim();
                parts.Add(new InterpolatedStringExpressionPart(expression));
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
            Code: "tosh::parser::unterminated_triple_quoted_string",
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
            // Don't break on '...' (path component) or non-numeric prefixes like '../'.
            if (Current == '.' && Peek() == '.' && Peek(2) != '.'
                && _position > start
                && IsNumericText(_source.AsSpan(start, _position - start)))
            {
                break;
            }

            // Keep lone '?' inside barewords so nullable type/identifier forms like
            // 'string?' and 'name?' keep lexing as a single token. The dedicated
            // '??' and '?.' tokens are already handled before we reach this path.
            if (Current is '|' or '#' or '(' or ')' or '{' or '}' or '[' or ']' or ';' or ',' or '>' or '<' or '&' or '!')
            {
                break;
            }

            _position++;
        }

        var text = _source[start.._position];

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

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integerValue))
        {
            object boxed = integerValue is >= int.MinValue and <= int.MaxValue
                ? (object)(int)integerValue
                : integerValue;
            return new SyntaxToken(SyntaxTokenKind.Number, start, text, boxed);
        }

        if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var floatingValue))
        {
            return new SyntaxToken(SyntaxTokenKind.Number, start, text, floatingValue);
        }

        return new SyntaxToken(SyntaxTokenKind.Bareword, start, text, text);
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

    private static bool IsNumericText(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return false;
        foreach (var ch in text)
        {
            if (!char.IsAsciiDigit(ch)) return false;
        }
        return true;
    }

    /// <summary>
    /// Advances _position past the contents of an interpolated expression <c>{...}</c>,
    /// stopping when the matching <c>}</c> is reached (depth returns to 0).
    /// Tracks nested braces, and skips over string literals so that <c>}</c>
    /// characters inside strings do not prematurely close the expression.
    /// </summary>
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
    /// Handles both single and double quotes with backslash escaping.
    /// </summary>
    private void SkipStringLiteralInExpression(char quote)
    {
        _position++; // skip opening quote

        while (!IsAtEnd)
        {
            if (Current == '\\' && !IsAtEnd)
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
