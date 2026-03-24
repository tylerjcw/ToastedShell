using System.Diagnostics;

namespace Tosh.Core;

public sealed record ProcessInfo(
    int Id,
    string Name,
    bool HasExited,
    DateTime? Started,
    TimeSpan? Cpu,
    StorageSize? WorkingSet,
    int? ThreadCount,
    string? Path)
{
    public int Pid => Id;

    public StorageSize? Memory => WorkingSet;

    public static ProcessInfo From(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        return new ProcessInfo(
            process.Id,
            SafeGet(() => process.ProcessName, process.Id.ToString()),
            SafeGet(() => process.HasExited, false),
            SafeGet(() => process.StartTime, (DateTime?)null),
            SafeGet(() => process.TotalProcessorTime, (TimeSpan?)null),
            SafeGet(() => StorageSize.FromBytes(process.WorkingSet64), (StorageSize?)null),
            SafeGet(() => process.Threads.Count, (int?)null),
            SafeGet(() => process.MainModule?.FileName, (string?)null));
    }

    private static T SafeGet<T>(Func<T> getValue, T fallback)
    {
        try
        {
            return getValue();
        }
        catch
        {
            return fallback;
        }
    }
}
