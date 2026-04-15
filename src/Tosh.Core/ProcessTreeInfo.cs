namespace Tosh.Core;

public sealed class ProcessTreeInfo : IDisplayTreeNode
{
    public ProcessTreeInfo(ProcessInfo process)
    {
        Process = process;
    }

    public ProcessInfo Process { get; }

    public List<ProcessTreeInfo> Children { get; } = [];

    public int Id => Process.Id;
    public string Name => Process.Name;
    public int? ParentId => Process.ParentId;
    public StorageSize? Memory => Process.Memory;
    public TimeSpan? Cpu => Process.Cpu;
    public DateTime? Started => Process.Started;
    public string? Path => Process.Path;
    public string? UserName => Process.UserName;
    public FileSystemPrincipalInfo? User => Process.User;
    public string? Tty => Process.Tty;
    public int? ThreadCount => Process.ThreadCount;

    IEnumerable<object> IDisplayTreeNode.GetDisplayChildren() => Children;
}
