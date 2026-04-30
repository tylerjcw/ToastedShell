using System.Globalization;

namespace Tosh.Runtime;

public readonly record struct StorageSize(long Bytes) : IComparable, IComparable<StorageSize>
{
    private static readonly IReadOnlyDictionary<string, decimal> UnitFactors = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
        [""] = 1m,
        ["b"] = 1m,
        ["kb"] = 1_000m,
        ["mb"] = 1_000_000m,
        ["gb"] = 1_000_000_000m,
        ["tb"] = 1_000_000_000_000m,
        ["pb"] = 1_000_000_000_000_000m,
        ["kib"] = 1_024m,
        ["mib"] = 1_048_576m,
        ["gib"] = 1_073_741_824m,
        ["tib"] = 1_099_511_627_776m,
        ["pib"] = 1_125_899_906_842_624m,
    };

    public static StorageSize FromBytes(long bytes) => new(bytes);

    public int CompareTo(object? obj)
    {
        return obj switch
        {
            null => 1,
            StorageSize size => CompareTo(size),
            _ => throw new ArgumentException($"Object must be of type {nameof(StorageSize)}.", nameof(obj)),
        };
    }

    public int CompareTo(StorageSize other) => Bytes.CompareTo(other.Bytes);

    public override string ToString() => $"{Bytes.ToString(CultureInfo.InvariantCulture)} B";

    public static bool TryParse(string? text, out StorageSize size)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            size = default;
            return false;
        }

        var trimmed = text.Trim();
        var splitIndex = 0;

        while (splitIndex < trimmed.Length &&
               (char.IsDigit(trimmed[splitIndex]) || trimmed[splitIndex] is '.' or '+' or '-'))
        {
            splitIndex++;
        }

        if (splitIndex == 0)
        {
            size = default;
            return false;
        }

        var numberText = trimmed[..splitIndex].Trim();
        var unitText = trimmed[splitIndex..].Trim().ToLowerInvariant();

        if (!decimal.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            size = default;
            return false;
        }

        if (!UnitFactors.TryGetValue(unitText, out var factor))
        {
            size = default;
            return false;
        }

        var bytes = decimal.Round(value * factor, 0, MidpointRounding.AwayFromZero);

        if (bytes < long.MinValue || bytes > long.MaxValue)
        {
            size = default;
            return false;
        }

        size = new StorageSize((long)bytes);
        return true;
    }
}
