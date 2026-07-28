using System.Globalization;
using System.Net;

namespace Tosh.Runtime;

public static class IntrinsicLiteralParser
{
    public static bool TryParseExpressionLiteral(string? text, out object? value)
    {
        if (StorageSize.TryParseLiteral(text, out var storageSize))
        {
            value = storageSize;
            return true;
        }

        if (TemporalParser.TryParseTemporalAmount(text, out var amount))
        {
            value = amount.IsPureTimeSpan ? amount.Duration : amount;
            return true;
        }

        if (TemporalParser.TryParseDateTimeOffsetLiteral(text, out var instant))
        {
            value = instant;
            return true;
        }

        if (LooksLikeIpAddressLiteral(text) &&
            IPAddress.TryParse(text?.Trim(), out var address))
        {
            value = address;
            return true;
        }

        value = null;
        return false;
    }

    private static bool LooksLikeIpAddressLiteral(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        var colonCount = trimmed.Count(static character => character == ':');
        if (colonCount >= 2 || trimmed.Contains("::", StringComparison.Ordinal))
        {
            return true;
        }

        if (!trimmed.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        var octets = trimmed.Split('.');
        if (octets.Length != 4)
        {
            return false;
        }

        foreach (var octet in octets)
        {
            if (octet.Length == 0 ||
                octet.Any(static character => !char.IsAsciiDigit(character)) ||
                (octet.Length > 1 && octet[0] == '0') ||
                !byte.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return true;
    }
}
