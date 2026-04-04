namespace Tosh.Core;

public interface IShellRecordObject
{
    string ShellTypeName { get; }

    bool TryGetMember(string name, out object? value, bool includeHidden = false);

    bool TrySetMember(string name, object? value);

    IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false);
}
