namespace Tosh.Runtime;

public sealed record class CommandTimingInfo
{
    public required TimeSpan Elapsed { get; init; }

    public required TimeSpan UserCpuTime { get; init; }

    public required TimeSpan SystemCpuTime { get; init; }

    public required double CpuPercent { get; init; }

    public required StorageSize PeakWorkingSet { get; init; }

    public required StorageSize WorkingSetDelta { get; init; }

    public required StorageSize ThreadAllocations { get; init; }

    public required long MinorPageFaults { get; init; }

    public required long MajorPageFaults { get; init; }
}
