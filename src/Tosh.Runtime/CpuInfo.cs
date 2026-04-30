namespace Tosh.Runtime;

public sealed record class CpuInfo
{
    public string? Architecture { get; init; }

    public IReadOnlyList<string> CpuOpModes { get; init; } = Array.Empty<string>();

    public int? PhysicalAddressBits { get; init; }

    public int? VirtualAddressBits { get; init; }

    public string? AddressSizesText { get; init; }

    public string? ByteOrder { get; init; }

    public int? CpuCount { get; init; }

    public string? OnlineCpuList { get; init; }

    public string? VendorId { get; init; }

    public string? ModelName { get; init; }

    public int? CpuFamily { get; init; }

    public int? Model { get; init; }

    public int? ThreadsPerCore { get; init; }

    public int? CoresPerSocket { get; init; }

    public int? SocketCount { get; init; }

    public int? Stepping { get; init; }

    public bool? FrequencyBoostEnabled { get; init; }

    public string? FrequencyBoostText { get; init; }

    public int? ScalingPercent { get; init; }

    public double? MaxMhz { get; init; }

    public double? MinMhz { get; init; }

    public double? BogoMips { get; init; }

    public IReadOnlyList<string> Flags { get; init; } = Array.Empty<string>();

    public string? Virtualization { get; init; }

    public string? L1dCache { get; init; }

    public string? L1iCache { get; init; }

    public string? L2Cache { get; init; }

    public string? L3Cache { get; init; }

    public int? NumaNodeCount { get; init; }

    public IReadOnlyDictionary<string, string> NumaNodes { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Vulnerabilities { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> AdditionalFields { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string CpuOpModesText => CpuOpModes.Count == 0 ? string.Empty : string.Join(", ", CpuOpModes);

    public string FlagsText => Flags.Count == 0 ? string.Empty : string.Join(Environment.NewLine, Flags);

    public string VulnerabilitiesText => FormatDictionary(Vulnerabilities);

    public string NumaNodesText => FormatDictionary(NumaNodes);

    public string AdditionalFieldsText => FormatDictionary(AdditionalFields);

    public override string ToString()
    {
        if (!string.IsNullOrWhiteSpace(ModelName))
        {
            return ModelName!;
        }

        if (!string.IsNullOrWhiteSpace(Architecture))
        {
            return Architecture!;
        }

        return "CPU";
    }

    private static string FormatDictionary(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            values.Select(pair => $"{pair.Key}: {pair.Value}"));
    }
}
