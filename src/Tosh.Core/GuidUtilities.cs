using System.Globalization;

namespace Tosh.Core;

public static class GuidUtilities
{
    public static int? GetVersion(Guid value)
    {
        var digits = value.ToString("N", CultureInfo.InvariantCulture);

        return int.TryParse(digits[12].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var version)
            ? version
            : null;
    }

    public static string GetVersionText(Guid value)
    {
        return GetVersion(value)?.ToString(CultureInfo.InvariantCulture) ?? "?";
    }

    public static string GetVariantName(Guid value)
    {
        var digits = value.ToString("N", CultureInfo.InvariantCulture);

        if (!int.TryParse(digits[16].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var nibble))
        {
            return "Unknown";
        }

        return nibble switch
        {
            < 8 => "NCS",
            < 12 => "RFC 4122",
            < 14 => "Microsoft",
            _ => "Future",
        };
    }
}
