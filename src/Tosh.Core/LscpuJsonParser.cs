using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tosh.Core;

public static partial class LscpuJsonParser
{
    public static CpuInfo ParseSummary(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("The `lscpu --json` output was empty.");
        }

        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("lscpu", out var summaryElement) ||
            summaryElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The `lscpu --json` output did not contain an 'lscpu' array.");
        }

        var flattened = new List<KeyValuePair<string, string?>>();
        FlattenSummaryEntries(summaryElement, flattened);

        var vulnerabilities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var numaNodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var additional = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fields = flattened
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToDictionary(item => item.Key, item => item.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in fields)
        {
            if (pair.Key.StartsWith("Vulnerability ", StringComparison.OrdinalIgnoreCase))
            {
                vulnerabilities[pair.Key["Vulnerability ".Length..]] = pair.Value;
            }
            else if (pair.Key.StartsWith("NUMA node", StringComparison.OrdinalIgnoreCase) &&
                     pair.Key.EndsWith(" CPU(s)", StringComparison.OrdinalIgnoreCase))
            {
                numaNodes[pair.Key] = pair.Value;
            }
        }

        foreach (var pair in fields)
        {
            if (IsKnownSummaryField(pair.Key) ||
                pair.Key.StartsWith("Vulnerability ", StringComparison.OrdinalIgnoreCase) ||
                (pair.Key.StartsWith("NUMA node", StringComparison.OrdinalIgnoreCase) &&
                 pair.Key.EndsWith(" CPU(s)", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            additional[pair.Key] = pair.Value;
        }

        ParseAddressSizes(fields.TryGetValue("Address sizes", out var addressSizes) ? addressSizes : null, out var physicalBits, out var virtualBits);

        return new CpuInfo
        {
            Architecture = GetValue(fields, "Architecture"),
            CpuOpModes = SplitCommaList(GetValue(fields, "CPU op-mode(s)")),
            PhysicalAddressBits = physicalBits,
            VirtualAddressBits = virtualBits,
            AddressSizesText = addressSizes,
            ByteOrder = GetValue(fields, "Byte Order"),
            CpuCount = ParseInt(GetValue(fields, "CPU(s)")),
            OnlineCpuList = GetValue(fields, "On-line CPU(s) list"),
            VendorId = GetValue(fields, "Vendor ID"),
            ModelName = GetValue(fields, "Model name"),
            CpuFamily = ParseInt(GetValue(fields, "CPU family")),
            Model = ParseInt(GetValue(fields, "Model")),
            ThreadsPerCore = ParseInt(GetValue(fields, "Thread(s) per core")),
            CoresPerSocket = ParseInt(GetValue(fields, "Core(s) per socket")),
            SocketCount = ParseInt(GetValue(fields, "Socket(s)")),
            Stepping = ParseInt(GetValue(fields, "Stepping")),
            FrequencyBoostEnabled = ParseEnabled(GetValue(fields, "Frequency boost")),
            FrequencyBoostText = GetValue(fields, "Frequency boost"),
            ScalingPercent = ParsePercent(GetValue(fields, "CPU(s) scaling MHz")),
            MaxMhz = ParseDouble(GetValue(fields, "CPU max MHz")),
            MinMhz = ParseDouble(GetValue(fields, "CPU min MHz")),
            BogoMips = ParseDouble(GetValue(fields, "BogoMIPS")),
            Flags = SplitWhitespace(GetValue(fields, "Flags")),
            Virtualization = GetValue(fields, "Virtualization"),
            L1dCache = GetValue(fields, "L1d cache"),
            L1iCache = GetValue(fields, "L1i cache"),
            L2Cache = GetValue(fields, "L2 cache"),
            L3Cache = GetValue(fields, "L3 cache"),
            NumaNodeCount = ParseInt(GetValue(fields, "NUMA node(s)")),
            NumaNodes = numaNodes,
            Vulnerabilities = vulnerabilities,
            AdditionalFields = additional,
        };
    }

    public static IReadOnlyList<CpuTopologyInfo> ParseTopology(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("cpus", out var cpusElement) ||
            cpusElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The structured `lscpu --extended --json` output did not contain a 'cpus' array.");
        }

        var results = new List<CpuTopologyInfo>();

        foreach (var item in cpusElement.EnumerateArray())
        {
            results.Add(new CpuTopologyInfo
            {
                BogoMips = GetDouble(item, "bogomips"),
                Cpu = GetInt(item, "cpu"),
                Core = GetInt(item, "core"),
                Socket = GetInt(item, "socket"),
                Cluster = GetInt(item, "cluster"),
                Node = GetInt(item, "node"),
                Book = GetInt(item, "book"),
                Drawer = GetInt(item, "drawer"),
                CacheIds = GetString(item, "l1d:l1i:l2:l3"),
                Polarization = NormalizeDash(GetString(item, "polarization")),
                Address = NormalizeDash(GetString(item, "address")),
                Configured = NormalizeDash(GetString(item, "configured")),
                Online = GetBool(item, "online"),
                Mhz = GetDouble(item, "mhz"),
                ScalingPercent = ParsePercent(GetString(item, "scalmhz%")),
                MaxMhz = GetDouble(item, "maxmhz"),
                MinMhz = GetDouble(item, "minmhz"),
                ModelName = GetString(item, "modelname"),
            });
        }

        return results;
    }

    public static IReadOnlyList<CpuCacheInfo> ParseCaches(string json, bool preferByteSizes)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("caches", out var cachesElement) ||
            cachesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The structured `lscpu --caches --json` output did not contain a 'caches' array.");
        }

        var results = new List<CpuCacheInfo>();

        foreach (var item in cachesElement.EnumerateArray())
        {
            var oneSizeText = GetString(item, "one-size");
            var allSizeText = GetString(item, "all-size");

            results.Add(new CpuCacheInfo
            {
                Name = GetString(item, "name"),
                Level = GetInt(item, "level"),
                Type = GetString(item, "type"),
                OneSize = ParseLooseSize(oneSizeText),
                OneSizeText = oneSizeText,
                AllSize = ParseLooseSize(allSizeText),
                AllSizeText = allSizeText,
                Ways = GetInt(item, "ways"),
                AllocationPolicy = GetString(item, "alloc-policy"),
                WritePolicy = GetString(item, "write-policy"),
                PhysicalLineCount = GetInt(item, "phy-line"),
                Sets = GetInt(item, "sets"),
                CoherencySize = GetInt(item, "coherency-size"),
                PreferByteSizes = preferByteSizes,
            });
        }

        return results;
    }

    private static void FlattenSummaryEntries(JsonElement arrayElement, ICollection<KeyValuePair<string, string?>> target)
    {
        foreach (var item in arrayElement.EnumerateArray())
        {
            FlattenSummaryEntry(item, target);
        }
    }

    private static void FlattenSummaryEntry(JsonElement element, ICollection<KeyValuePair<string, string?>> target)
    {
        var field = NormalizeField(GetString(element, "field"));
        var data = GetString(element, "data");

        if (!string.IsNullOrWhiteSpace(field))
        {
            target.Add(new KeyValuePair<string, string?>(field, data));
        }

        if (element.TryGetProperty("children", out var childrenElement) &&
            childrenElement.ValueKind == JsonValueKind.Array)
        {
            FlattenSummaryEntries(childrenElement, target);
        }
    }

    private static string? NormalizeField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.EndsWith(':')
            ? trimmed[..^1]
            : trimmed;
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static bool IsKnownSummaryField(string key)
    {
        return key.Equals("Architecture", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("CPU op-mode(s)", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Address sizes", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Byte Order", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("CPU(s)", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("On-line CPU(s) list", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Vendor ID", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Model name", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("CPU family", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Model", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Thread(s) per core", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Core(s) per socket", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Socket(s)", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Stepping", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Frequency boost", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("CPU(s) scaling MHz", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("CPU max MHz", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("CPU min MHz", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("BogoMIPS", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Flags", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Virtualization", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("L1d cache", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("L1i cache", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("L2 cache", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("L3 cache", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("NUMA node(s)", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SplitCommaList(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static IReadOnlyList<string> SplitWhitespace(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split((char[]?)null, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static void ParseAddressSizes(string? text, out int? physicalBits, out int? virtualBits)
    {
        physicalBits = null;
        virtualBits = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var match = AddressSizesRegex().Match(text);

        if (!match.Success)
        {
            return;
        }

        physicalBits = ParseInt(match.Groups["physical"].Value);
        virtualBits = ParseInt(match.Groups["virtual"].Value);
    }

    private static StorageSize? ParseLooseSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (StorageSize.TryParse(value, out var size))
        {
            return size;
        }

        var normalized = value
            .Replace("KiB", "kib", StringComparison.OrdinalIgnoreCase)
            .Replace("MiB", "mib", StringComparison.OrdinalIgnoreCase)
            .Replace("GiB", "gib", StringComparison.OrdinalIgnoreCase)
            .Replace("TiB", "tib", StringComparison.OrdinalIgnoreCase)
            .Replace("PiB", "pib", StringComparison.OrdinalIgnoreCase)
            .Replace("KB", "kb", StringComparison.OrdinalIgnoreCase)
            .Replace("MB", "mb", StringComparison.OrdinalIgnoreCase)
            .Replace("GB", "gb", StringComparison.OrdinalIgnoreCase)
            .Replace("TB", "tb", StringComparison.OrdinalIgnoreCase)
            .Replace("PB", "pb", StringComparison.OrdinalIgnoreCase);

        normalized = Regex.Replace(normalized, "(?<=[0-9])K\\b", "kb", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "(?<=[0-9])M\\b", "mb", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "(?<=[0-9])G\\b", "gb", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "(?<=[0-9])T\\b", "tb", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "(?<=[0-9])P\\b", "pb", RegexOptions.IgnoreCase);

        return StorageSize.TryParse(normalized, out size)
            ? size
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => property.GetRawText(),
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var direct))
        {
            return direct;
        }

        return ParseInt(GetString(element, propertyName));
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var direct))
        {
            return direct;
        }

        return ParseDouble(GetString(element, propertyName));
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static int? ParseInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? ParseDouble(string? value)
    {
        return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? ParsePercent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().TrimEnd('%');
        return ParseInt(normalized);
    }

    private static bool? ParseEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "enabled" or "yes" or "true" => true,
            "disabled" or "no" or "false" => false,
            _ => null,
        };
    }

    private static string? NormalizeDash(string? value)
    {
        return string.Equals(value, "-", StringComparison.Ordinal)
            ? null
            : value;
    }

    [GeneratedRegex(@"(?<physical>\d+)\s+bits\s+physical,\s+(?<virtual>\d+)\s+bits\s+virtual", RegexOptions.IgnoreCase)]
    private static partial Regex AddressSizesRegex();
}
