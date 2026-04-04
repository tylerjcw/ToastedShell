namespace Tosh.Core;

public sealed record class CpuTopologyInfo
{
    public double? BogoMips { get; init; }

    public int? Cpu { get; init; }

    public int? Core { get; init; }

    public int? Socket { get; init; }

    public int? Cluster { get; init; }

    public int? Node { get; init; }

    public int? Book { get; init; }

    public int? Drawer { get; init; }

    public string? CacheIds { get; init; }

    public string? Polarization { get; init; }

    public string? Address { get; init; }

    public string? Configured { get; init; }

    public bool? Online { get; init; }

    public double? Mhz { get; init; }

    public int? ScalingPercent { get; init; }

    public double? MaxMhz { get; init; }

    public double? MinMhz { get; init; }

    public string? ModelName { get; init; }

    public override string ToString()
    {
        return Cpu is int cpu
            ? $"CPU {cpu}"
            : "CPU";
    }
}
