namespace Tosh.Runtime;

public interface IObjectAccessor
{
    object? GetValue(object? target, string memberPath);
    void SetValue(object? target, string memberPath, object? value);

    ValueTask<object?> GetValueAsync(
        object? target,
        string memberPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetValue(target, memberPath));
    }

    ValueTask SetValueAsync(
        object? target,
        string memberPath,
        object? value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetValue(target, memberPath, value);
        return ValueTask.CompletedTask;
    }

    bool IsNullablePath(Type targetType, string memberPath);
}
