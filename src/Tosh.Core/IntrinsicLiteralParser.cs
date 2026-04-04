using System.Net;

namespace Tosh.Core;

public static class IntrinsicLiteralParser
{
    public static bool TryParseExpressionLiteral(string? text, out object? value)
    {
        if (TemporalParser.TryParseTemporalAmount(text, out var amount))
        {
            value = amount.IsPureTimeSpan ? amount.Duration : amount;
            return true;
        }

        if (TemporalParser.TryParseDateTimeOffset(text, out var instant))
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

        if (trimmed.Contains('.', StringComparison.Ordinal))
        {
            return true;
        }

        var colonCount = trimmed.Count(static character => character == ':');
        return colonCount >= 2 || trimmed.Contains("::", StringComparison.Ordinal);
    }
}
