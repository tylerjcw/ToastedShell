using System.Threading.Tasks;

namespace Tosh.Core;

/// <summary>
/// Represents deferred command execution started by the `async` command.
/// </summary>
public sealed class ShellFuture : IShellRecordObject
{
    private static int _nextId;
    private readonly Task<IReadOnlyList<object?>> _task;

    public ShellFuture(Task<IReadOnlyList<object?>> task)
    {
        _task = task ?? throw new ArgumentNullException(nameof(task));
        Id = Interlocked.Increment(ref _nextId);
    }

    public int Id { get; }

    public bool IsCompleted => _task.IsCompleted;

    public bool IsFaulted => _task.IsFaulted;

    public bool IsCanceled => _task.IsCanceled;

    public string Status => _task.Status.ToString();

    public async Task<IReadOnlyList<object?>> AwaitAsync(CancellationToken cancellationToken = default)
    {
        var completed = await _task.WaitAsync(cancellationToken);
        return completed;
    }

    public string ShellTypeName => "ShellFuture";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case nameof(Id):
                value = Id;
                return true;
            case nameof(IsCompleted):
                value = IsCompleted;
                return true;
            case nameof(IsFaulted):
                value = IsFaulted;
                return true;
            case nameof(IsCanceled):
                value = IsCanceled;
                return true;
            case nameof(Status):
                value = Status;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetMember(string name, object? value)
        => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
        =>
        [
            new(nameof(Id), Id),
            new(nameof(IsCompleted), IsCompleted),
            new(nameof(IsFaulted), IsFaulted),
            new(nameof(IsCanceled), IsCanceled),
            new(nameof(Status), Status),
        ];

    public override string ToString()
        => $"future#{Id} ({Status})";
}
