using System.Globalization;

namespace Tosh.Runtime.Formats;

/// <summary>
/// Infers numeric and boolean column types for delimited input (<c>TS-P2-27</c>).
/// </summary>
/// <remarks>
/// <para>
/// CSV carries no types, so every column arrived as <see cref="string"/> and the
/// specification's own <c>| where _.Amount &gt; 100</c> failed with "Values of type
/// 'System.String' and 'System.Int32' cannot be ordered". `from json` produced
/// typed values because JSON declares them, so the inconsistency was internal as
/// well as against the document.
/// </para>
/// <para>
/// Inference is deliberately narrow: integers, decimals, and <c>true</c>/<c>false</c>.
/// Dates are excluded because that is where inference stops being obvious —
/// <c>01/02/26</c> is three different days depending on locale, and a shell that
/// guesses wrong there corrupts data silently rather than loudly.
/// </para>
/// <para>
/// Decided **per column, not per cell.** A column where only some cells parse would
/// otherwise hold an <see cref="int"/> beside a <see cref="string"/>, and the values
/// in one column could not be compared with each other — which is a worse failure
/// than leaving the column textual, because it appears only on the rows that differ.
/// So a column is typed only when every non-empty cell agrees.
/// </para>
/// </remarks>
internal static class DelimitedValueInference
{
    private enum ColumnKind
    {
        /// <summary>Nothing seen yet — every cell so far was empty.</summary>
        Unknown,
        Integer,
        Number,
        Boolean,
        /// <summary>Textual, which is also the answer for any column that disagrees with itself.</summary>
        String,
    }

    /// <summary>
    /// Returns one converter per column, or <see langword="null"/> in a slot whose
    /// column stays textual.
    /// </summary>
    public static Func<string, object?>?[] InferColumns(
        int columnCount,
        IReadOnlyList<string[]> rows)
    {
        var converters = new Func<string, object?>?[columnCount];

        for (var column = 0; column < columnCount; column++)
        {
            var kind = ColumnKind.Unknown;

            foreach (var row in rows)
            {
                if (column >= row.Length)
                {
                    continue;
                }

                var cell = row[column];

                // An empty cell is not evidence either way — a column of numbers
                // with a gap is still a column of numbers.
                if (string.IsNullOrWhiteSpace(cell))
                {
                    continue;
                }

                kind = Combine(kind, Classify(cell));

                if (kind == ColumnKind.String)
                {
                    break;
                }
            }

            converters[column] = kind switch
            {
                ColumnKind.Integer => ConvertInteger,
                ColumnKind.Number => ConvertNumber,
                ColumnKind.Boolean => ConvertBoolean,
                _ => null,
            };
        }

        return converters;
    }

    private static ColumnKind Combine(ColumnKind seen, ColumnKind cell)
    {
        if (seen == ColumnKind.Unknown)
        {
            return cell;
        }

        if (seen == cell)
        {
            return seen;
        }

        // Integers and decimals are the one pair that reconciles: a column of
        // 1, 2, 2.5 is numeric. Anything else disagreeing means textual.
        if ((seen, cell) is (ColumnKind.Integer, ColumnKind.Number)
                         or (ColumnKind.Number, ColumnKind.Integer))
        {
            return ColumnKind.Number;
        }

        return ColumnKind.String;
    }

    private static ColumnKind Classify(string cell)
    {
        var text = cell.Trim();

        if (bool.TryParse(text, out _))
        {
            return ColumnKind.Boolean;
        }

        // A leading zero is nearly always an identifier rather than a number —
        // `007`, a zip code, a phone extension — and converting it destroys the
        // zero irreversibly. `0` alone, and `0.5`, are ordinary numbers.
        if (HasSignificantLeadingZero(text))
        {
            return ColumnKind.String;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return ColumnKind.Integer;
        }

        // NumberStyles.Float admits a sign, a decimal point, and an exponent, but
        // not thousands separators — `1,234` stays textual, which it must, since
        // the comma is also the delimiter.
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return ColumnKind.Number;
        }

        return ColumnKind.String;
    }

    private static bool HasSignificantLeadingZero(string text)
    {
        var digits = text.Length > 0 && (text[0] == '-' || text[0] == '+')
            ? text[1..]
            : text;

        return digits.Length > 1 && digits[0] == '0' && digits[1] != '.';
    }

    /// <summary>
    /// <see cref="int"/> when the value fits, so `150` is the `int` a user expects
    /// rather than a `long`; widening only where it must.
    /// </summary>
    private static object? ConvertInteger(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
        {
            return null;
        }

        var text = cell.Trim();

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var small))
        {
            return small;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wide))
        {
            return wide;
        }

        return text;
    }

    private static object? ConvertNumber(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
        {
            return null;
        }

        var text = cell.Trim();

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : text;
    }

    private static object? ConvertBoolean(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
        {
            return null;
        }

        return bool.TryParse(cell.Trim(), out var value) ? value : cell.Trim();
    }
}
