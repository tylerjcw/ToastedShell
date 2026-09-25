using System.Globalization;
using System.Numerics;
using Tosh.Runtime.Units;

namespace Tosh.Stdlib.Cas;

public sealed class SymParser
{
    private readonly string _input;
    private int _pos;

    public SymParser(string input)
    {
        _input = input ?? string.Empty;
        _pos = 0;
    }

    public static SymExpr Parse(string expression)
    {
        var parser = new SymParser(expression);
        var result = parser.ParseExpression();
        parser.SkipWhitespace();
        if (parser._pos < parser._input.Length)
        {
            throw new FormatException($"Unexpected character '{parser._input[parser._pos]}' at position {parser._pos} in '{parser._input}'");
        }
        return result;
    }

    private SymExpr ParseExpression()
    {
        var left = ParseTerm();

        while (true)
        {
            SkipWhitespace();
            if (_pos < _input.Length && _input[_pos] == '+')
            {
                _pos++;
                var right = ParseTerm();
                left = Simplifier.Add(left, right);
            }
            else if (_pos < _input.Length && _input[_pos] == '-')
            {
                _pos++;
                var right = ParseTerm();
                left = Simplifier.Subtract(left, right);
            }
            else
            {
                break;
            }
        }

        return left;
    }

    private SymExpr ParseTerm()
    {
        var left = ParsePower();

        while (true)
        {
            SkipWhitespace();
            if (_pos < _input.Length && _input[_pos] == '*')
            {
                _pos++;
                var right = ParsePower();
                left = Simplifier.Multiply(left, right);
            }
            else if (_pos < _input.Length && _input[_pos] == '/')
            {
                _pos++;
                var right = ParsePower();
                left = Simplifier.Divide(left, right);
            }
            // Check for implicit multiplication: e.g. 2x, 2(x+1), (x+1)(x+2)
            else if (CanStartPrimary())
            {
                var right = ParsePower();
                left = Simplifier.Multiply(left, right);
            }
            else
            {
                break;
            }
        }

        return left;
    }

    private SymExpr ParsePower()
    {
        var left = ParseUnary();

        SkipWhitespace();
        if (_pos < _input.Length && _input[_pos] == '^')
        {
            _pos++;
            // Right-associative: a^b^c = a^(b^c)
            var right = ParsePower();
            return Simplifier.Power(left, right);
        }

        return left;
    }

    private SymExpr ParseUnary()
    {
        SkipWhitespace();
        if (_pos < _input.Length && _input[_pos] == '+')
        {
            _pos++;
            return ParseUnary();
        }
        if (_pos < _input.Length && _input[_pos] == '-')
        {
            _pos++;
            return Simplifier.Negate(ParseUnary());
        }

        return ParsePrimary();
    }

    private bool CanStartPrimary()
    {
        SkipWhitespace();
        if (_pos >= _input.Length) return false;
        char c = _input[_pos];
        return char.IsLetter(c) || char.IsDigit(c) || c == '(';
    }

    private SymExpr ParsePrimary()
    {
        SkipWhitespace();
        if (_pos >= _input.Length)
        {
            throw new FormatException("Unexpected end of expression.");
        }

        char c = _input[_pos];

        if (c == '(')
        {
            _pos++;
            var expr = ParseExpression();
            SkipWhitespace();
            if (_pos >= _input.Length || _input[_pos] != ')')
            {
                throw new FormatException("Missing closing parenthesis ')'.");
            }
            _pos++;
            return expr;
        }

        if (char.IsDigit(c) || c == '.')
        {
            return ParseNumber();
        }

        if (char.IsLetter(c) || c == '_')
        {
            return ParseIdentifierOrFunction();
        }

        throw new FormatException($"Unexpected character '{c}' at position {_pos}.");
    }

    private SymExpr ParseNumber()
    {
        int start = _pos;
        bool hasDot = false;

        while (_pos < _input.Length && (char.IsDigit(_input[_pos]) || _input[_pos] == '.'))
        {
            if (_input[_pos] == '.')
            {
                if (hasDot) break;
                hasDot = true;
            }
            _pos++;
        }

        var numStr = _input[start.._pos];
        BigRational rationalVal;
        if (hasDot)
        {
            // Parse decimal to exact rational
            var parts = numStr.Split('.');
            var intPart = parts[0].Length == 0 ? 0 : BigInteger.Parse(parts[0], CultureInfo.InvariantCulture);
            var fracPart = parts[1];
            var denom = BigInteger.Pow(10, fracPart.Length);
            var num = intPart * denom + BigInteger.Parse(fracPart, CultureInfo.InvariantCulture);
            rationalVal = new BigRational(num, denom);
        }
        else
        {
            var intVal = BigInteger.Parse(numStr, CultureInfo.InvariantCulture);
            rationalVal = new BigRational(intVal);
        }

        if (_pos < _input.Length && (_input[_pos] == '`' || _input[_pos] == '°'))
        {
            var isDegree = _input[_pos] == '°';
            if (!isDegree) _pos++;
            int unitStart = _pos;
            int unitParenDepth = 0;
            while (_pos < _input.Length && !char.IsWhiteSpace(_input[_pos]))
            {
                if (_input[_pos] == '(') { unitParenDepth++; _pos++; continue; }
                if (_input[_pos] == ')')
                {
                    if (unitParenDepth > 0) { unitParenDepth--; _pos++; continue; }
                    break;
                }
                if (_input[_pos] == '`')
                    break;
                _pos++;
            }
            var unitPart = _input[unitStart.._pos];
            if (_pos < _input.Length && _input[_pos] == '`') _pos++;

            if (UnitExpressionParser.TryParseConversion(
                    unitPart,
                    out _,
                    out var dim,
                    out var normSym))
            {
                return new SymQuantity(rationalVal, dim, normSym);
            }
        }

        return new SymNumber(rationalVal);
    }

    private SymExpr ParseIdentifierOrFunction()
    {
        int start = _pos;
        while (_pos < _input.Length && (char.IsLetterOrDigit(_input[_pos]) || _input[_pos] == '_'))
        {
            _pos++;
        }

        var name = _input[start.._pos];

        SkipWhitespace();
        if (_pos < _input.Length && _input[_pos] == '(')
        {
            // Function call
            _pos++;
            var args = new List<SymExpr>();
            SkipWhitespace();
            if (_pos < _input.Length && _input[_pos] != ')')
            {
                while (true)
                {
                    args.Add(ParseExpression());
                    SkipWhitespace();
                    if (_pos < _input.Length && _input[_pos] == ',')
                    {
                        _pos++;
                        continue;
                    }
                    break;
                }
            }

            SkipWhitespace();
            if (_pos >= _input.Length || _input[_pos] != ')')
            {
                throw new FormatException($"Missing closing parenthesis in function '{name}'.");
            }
            _pos++;

            return new SymFunction(name, args);
        }

        // Constants
        if (string.Equals(name, "pi", StringComparison.OrdinalIgnoreCase))
            return SymExpr.Pi;
        if (string.Equals(name, "e", StringComparison.OrdinalIgnoreCase))
            return SymExpr.E;

        return new SymVariable(name);
    }

    private void SkipWhitespace()
    {
        while (_pos < _input.Length && char.IsWhiteSpace(_input[_pos]))
        {
            _pos++;
        }
    }
}
