using System.Globalization;
using System.Numerics;
using System.Text;

namespace Tosh.Runtime.Formats;

public sealed class NumberBaseFormat : IDataFormat
{
    private readonly int _base;
    private readonly string _prefix;

    public NumberBaseFormat(string name, IReadOnlyList<string> aliases, int numericBase, string prefix, string description)
    {
        Name = name;
        Aliases = aliases;
        _base = numericBase;
        _prefix = prefix;
        Description = description;
    }

    public string Name { get; }
    public IReadOnlyList<string> Aliases { get; }
    public string Description { get; }

    public async IAsyncEnumerable<object?> DeserializeAsync(string text, IReadOnlyList<object?> arguments)
    {
        await Task.CompletedTask;

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            yield break;
        }

        var negative = false;
        if (trimmed[0] == '-')
        {
            negative = true;
            trimmed = trimmed[1..];
        }
        else if (trimmed[0] == '+')
        {
            trimmed = trimmed[1..];
        }

        if (_prefix.Length > 0 && trimmed.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[_prefix.Length..];
        }

        if (!TryParse(trimmed, out var value))
        {
            throw new InvalidOperationException($"Could not parse '{text}' as {Name}.");
        }

        yield return negative ? -value : Narrow(value);
    }

    public async IAsyncEnumerable<object?> SerializeAsync(IReadOnlyList<object?> values, IReadOnlyList<object?> arguments)
    {
        await Task.CompletedTask;

        var args = ParsedCommandArguments.Parse(arguments);
        var noPrefix = args.HasFlag("no-prefix", "n");
        var upper = args.HasFlag("upper", "u");

        foreach (var value in values)
        {
            yield return new ShellTextLine(Format(value, noPrefix, upper));
        }
    }

    private string Format(object? value, bool noPrefix, bool upper)
    {
        if (value is null)
        {
            throw new InvalidOperationException($"Cannot convert null to {Name}.");
        }

        if (!TryToBigInteger(value, out var bigValue))
        {
            throw new InvalidOperationException($"Cannot convert '{value.GetType().Name}' to {Name}. Expected an integer.");
        }

        var sign = bigValue.Sign < 0 ? "-" : string.Empty;
        var absValue = BigInteger.Abs(bigValue);

        string digits = _base switch
        {
            16 => absValue.ToString("X", CultureInfo.InvariantCulture).TrimStart('0'),
            10 => absValue.ToString(CultureInfo.InvariantCulture),
            8 => ToBaseString(absValue, 8),
            2 => ToBaseString(absValue, 2),
            _ => throw new InvalidOperationException($"Unsupported base {_base}."),
        };

        if (digits.Length == 0) digits = "0";
        if (!upper) digits = digits.ToLowerInvariant();

        var prefix = noPrefix ? string.Empty : _prefix;
        return sign + prefix + digits;
    }

    private static string ToBaseString(BigInteger value, int radix)
    {
        if (value.IsZero) return "0";

        var sb = new StringBuilder();
        while (value > 0)
        {
            sb.Insert(0, (char)('0' + (int)(value % radix)));
            value /= radix;
        }
        return sb.ToString();
    }

    private bool TryParse(string text, out BigInteger value)
    {
        value = 0;
        if (text.Length == 0) return false;

        foreach (var c in text)
        {
            int digit;
            if (c is >= '0' and <= '9') digit = c - '0';
            else if (c is >= 'a' and <= 'z') digit = c - 'a' + 10;
            else if (c is >= 'A' and <= 'Z') digit = c - 'A' + 10;
            else if (c == '_') continue;
            else return false;

            if (digit >= _base) return false;
            value = value * _base + digit;
        }
        return true;
    }

    private static bool TryToBigInteger(object? value, out BigInteger result)
    {
        switch (value)
        {
            case BigInteger bi: result = bi; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case short s: result = s; return true;
            case sbyte sb: result = sb; return true;
            case byte b: result = b; return true;
            case ushort us: result = us; return true;
            case uint ui: result = ui; return true;
            case ulong ul: result = ul; return true;
            case string str when BigInteger.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                result = parsed; return true;
            default:
                result = 0;
                return false;
        }
    }

    private static object Narrow(BigInteger value)
    {
        if (value >= int.MinValue && value <= int.MaxValue) return (int)value;
        if (value >= long.MinValue && value <= long.MaxValue) return (long)value;
        return value;
    }
}
