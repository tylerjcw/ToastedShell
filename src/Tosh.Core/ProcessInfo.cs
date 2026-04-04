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
    string? Path,
    int? ParentId = null,
    FileSystemPrincipalInfo? User = null,
    string? Tty = null)
{
    public int Pid => Id;

    public int? Ppid => ParentId;

    public StorageSize? Memory => WorkingSet;

    public string? UserName => User?.DisplayName;

    public static ProcessInfo From(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var supplemental = ProcessMetadataUtilities.Read(process);

        return new ProcessInfo(
            process.Id,
            SafeGet(() => process.ProcessName, process.Id.ToString()),
            SafeGet(() => process.HasExited, false),
            SafeGet(() => process.StartTime, (DateTime?)null),
            SafeGet(() => process.TotalProcessorTime, (TimeSpan?)null),
            SafeGet(() => StorageSize.FromBytes(process.WorkingSet64), (StorageSize?)null),
            SafeGet(() => process.Threads.Count, (int?)null),
            SafeGet(() => process.MainModule?.FileName, (string?)null),
            supplemental.ParentId,
            supplemental.User,
            supplemental.Tty);
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
