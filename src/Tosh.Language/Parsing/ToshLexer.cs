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

            // $ prefixed tokens: $((, $(, ${, $'
            if (Current == '$')
            {
                if (Peek() == '(' && Peek(2) == '(')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.DollarDoubleOpenParen, _position, "$(("));
                    _position += 3;
                    continue;
                }

                if (Peek() == '(')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.DollarOpenParen, _position, "$("));
                    _position += 2;
                    continue;
                }

                if (Peek() == '{')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.DollarOpenBrace, _position, "${"));
                    _position += 2;
                    continue;
                }

                if (Peek() == '\'')
                {
                    tokens.Add(ReadAnsiCString());
                    continue;
                }

                // Otherwise fall through to bareword (e.g. $variable)
            }

            // | and ||
            if (Current == '|')
            {
                if (Peek() == '|')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.PipePipe, _position, "||"));
                    _position += 2;
                    continue;
                }

                tokens.Add(new SyntaxToken(SyntaxTokenKind.Pipe, _position, "|"));
                _position++;
                continue;
            }

            // & and &&
            if (Current == '&')
            {
                if (Peek() == '&')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.AmpersandAmpersand, _position, "&&"));
                    _position += 2;
                    continue;
                }

                tokens.Add(new SyntaxToken(SyntaxTokenKind.Ampersand, _position, "&"));
                _position++;
                continue;
            }

            // != and !
            if (Current == '!')
            {
                if (Peek() == '=')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.BangEqual, _position, "!="));
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

                if (Peek() == '(')
                {
                    tokens.Add(new SyntaxToken(SyntaxTokenKind.GreaterThanOpenParen, _position, ">("));
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
            _ => escaped,
        };
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

    private SyntaxToken ReadBarewordOrLiteral()
    {
        var start = _position;

        while (!IsAtEnd &&
               !char.IsWhiteSpace(Current) &&
               Current is not ('|' or '#' or '(' or ')' or '{' or '}' or '[' or ']' or ';' or ',' or '>' or '<' or '&' or '!'))
        {
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

        if (string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
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

    internal sealed class LexerDiagnosticException : Exception
    {
        public LexerDiagnosticException(SyntaxDiagnostic diagnostic)
        {
            Diagnostic = diagnostic;
        }

        public SyntaxDiagnostic Diagnostic { get; }
    }
}
