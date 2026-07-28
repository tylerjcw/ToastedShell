namespace Tosh.Runtime;

public interface IShellRecordObject
{
    string ShellTypeName { get; }

    bool TryGetMember(string name, out object? value, bool includeHidden = false);

    bool TrySetMember(string name, object? value);

    IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false);

    ValueTask<(bool Found, object? Value)> TryGetMemberAsync(
        string name,
        bool includeHidden,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            TryGetMember(name, out var value, includeHidden)
                ? (true, value)
                : (false, (object?)null));
    }

    ValueTask<bool> TrySetMemberAsync(
        string name,
        object? value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TrySetMember(name, value));
    }

    ValueTask<IReadOnlyList<KeyValuePair<string, object?>>> GetMembersAsync(
        bool includeHidden,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetMembers(includeHidden));
    }
}
