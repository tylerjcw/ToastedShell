using System.Globalization;

namespace Tosh.Runtime.Units;

/// <summary>
/// Parses unit expression strings like "m", "m/s", "kg*m/s^2", "m/s^2".
/// Supports multiplication (*,·), division (/), and exponentiation (^).
/// </summary>
public static class UnitExpressionParser
{
    /// <summary>
    /// Parse a full unit string from a backtick literal.
    /// Returns the resolved UnitDefinition if it's a simple symbol,
    /// or constructs a compound dimension + computes the conversion factor.
    /// </summary>
    [Obsolete("Use TryParseConversion so affine offsets are not discarded.")]
    public static bool TryParse(string text, out double factor, out UnitExpression dimension, out string normalizedSymbol)
    {
        var parsed = TryParseConversion(text, out var conversion, out dimension, out normalizedSymbol);
        factor = conversion.ToBaseFactor;
        return parsed && !conversion.IsAffine;
    }

    /// <summary>
    /// Parses a unit expression and retains the complete display-to-base transform.
    /// Affine units are valid only as simple symbols: composing an absolute scale such
    /// as Celsius into <c>°C/s</c> would incorrectly apply its origin as a multiplier.
    /// </summary>
    public static bool TryParseConversion(
        string text,
        out UnitConversion conversion,
        out UnitExpression dimension,
        out string normalizedSymbol)
    {
        conversion = UnitConversion.Identity;
        dimension = UnitExpression.Dimensionless;
        normalizedSymbol = text;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Fast path: simple single unit symbol
        var registry = UnitRegistry.Instance;
        var simple = registry.TryResolve(text);

        if (simple is not null)
        {
            conversion = simple.Conversion;
            dimension = simple.Dimension;
            normalizedSymbol = simple.Symbol;
            return true;
        }

        // Compound unit: tokenize and parse
        if (!TryParseCompound(text, out var factor, out dimension, out normalizedSymbol))
        {
            return false;
        }

        conversion = new UnitConversion(factor);
        return true;
    }

    private static bool TryParseCompound(string text, out double factor, out UnitExpression dimension, out string normalizedSymbol)
    {
        factor = 1.0;
        dimension = UnitExpression.Dimensionless;
        normalizedSymbol = text;

        var tokens = Tokenize(text);
        if (tokens.Count == 0) return false;

        var index = 0;
        if (!TryParseExpression(tokens, ref index, out factor, out dimension))
        {
            return false;
        }

        return index >= tokens.Count && double.IsFinite(factor) && factor != 0.0;
    }

    private static bool TryParseExpression(List<UnitToken> tokens, ref int index, out double factor, out UnitExpression dimension)
    {
        // Numeric one is syntax, not a registered unit. It is accepted only as
        // the leading numerator of a reciprocal canonical form such as 1/s.
        // This prevents 5`1, m*1, m/1, and 1^2 from becoming unit expressions.
        if (index == 0 &&
            tokens.Count > 2 &&
            tokens[0] is { Kind: UnitTokenKind.Number, Text: "1" } &&
            tokens[1].Kind == UnitTokenKind.Divide)
        {
            factor = 1.0;
            dimension = UnitExpression.Dimensionless;
            index++;
        }
        else if (!TryParseTerm(tokens, ref index, out factor, out dimension))
        {
            return false;
        }

        while (index < tokens.Count)
        {
            if (tokens[index].Kind == UnitTokenKind.Multiply)
            {
                index++;
                if (!TryParseTerm(tokens, ref index, out var rightFactor, out var rightDim))
                    return false;
                factor *= rightFactor;
                dimension = dimension.Multiply(rightDim);
            }
            else if (tokens[index].Kind == UnitTokenKind.Divide)
            {
                index++;
                if (!TryParseTerm(tokens, ref index, out var rightFactor, out var rightDim))
                    return false;
                if (rightFactor == 0) return false;
                factor /= rightFactor;
                dimension = dimension.Divide(rightDim);
            }
            else
            {
                break;
            }
        }

        return true;
    }

    private static bool TryParseTerm(List<UnitToken> tokens, ref int index, out double factor, out UnitExpression dimension)
    {
        factor = 1.0;
        dimension = UnitExpression.Dimensionless;

        if (index >= tokens.Count) return false;

        if (tokens[index].Kind == UnitTokenKind.Symbol)
        {
            var symbol = tokens[index].Text;
            index++;

            var unit = UnitRegistry.Instance.TryResolve(symbol);
            if (unit is null) return false;

            // A temperature point cannot be multiplied, divided, or raised to
            // a power. Kelvin and Rankine prove why checking only HasOffset is
            // insufficient: both have a zero offset but still denote points.
            // Explicit linear delta units will use UnitRole.Linear later.
            if (unit.Role == UnitRole.AbsoluteTemperature || unit.HasOffset) return false;

            factor = unit.ToBaseFactor;
            dimension = unit.Dimension;
        }
        else
        {
            return false;
        }

        // Check for exponent: ^N
        if (index < tokens.Count && tokens[index].Kind == UnitTokenKind.Caret)
        {
            index++; // consume ^
            if (index >= tokens.Count || tokens[index].Kind != UnitTokenKind.Number)
                return false;

            if (!int.TryParse(tokens[index].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exponent))
                return false;

            index++;
            factor = Math.Pow(factor, exponent);
            dimension = dimension.Power(exponent);
        }

        return true;
    }

    #region Tokenizer

    private enum UnitTokenKind { Symbol, Number, Multiply, Divide, Caret }

    private readonly record struct UnitToken(UnitTokenKind Kind, string Text);

    private static List<UnitToken> Tokenize(string text)
    {
        var tokens = new List<UnitToken>();
        var i = 0;

        while (i < text.Length)
        {
            var ch = text[i];

            switch (ch)
            {
                case '*' or '·' or '\u00b7':
                    tokens.Add(new UnitToken(UnitTokenKind.Multiply, "*"));
                    i++;
                    continue;
                case '/':
                    tokens.Add(new UnitToken(UnitTokenKind.Divide, "/"));
                    i++;
                    continue;
                case '^':
                    tokens.Add(new UnitToken(UnitTokenKind.Caret, "^"));
                    i++;
                    continue;
            }

            // Negative exponent: consume '-' followed by digits
            if (ch == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                var start = i;
                i++; // consume '-'
                while (i < text.Length && char.IsDigit(text[i])) i++;
                tokens.Add(new UnitToken(UnitTokenKind.Number, text[start..i]));
                continue;
            }

            // Digits (exponent)
            if (char.IsDigit(ch))
            {
                var start = i;
                while (i < text.Length && char.IsDigit(text[i])) i++;
                tokens.Add(new UnitToken(UnitTokenKind.Number, text[start..i]));
                continue;
            }

            // Letters, °, μ — unit symbols
            if (char.IsLetter(ch) || ch == '°' || ch == 'μ')
            {
                var start = i;
                while (i < text.Length && (char.IsLetter(text[i]) || text[i] == '°' || text[i] == 'μ'))
                {
                    i++;
                }
                tokens.Add(new UnitToken(UnitTokenKind.Symbol, text[start..i]));
                continue;
            }

            // Skip whitespace
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            // Unknown character → fail
            return [];
        }

        return tokens;
    }

    #endregion
}
