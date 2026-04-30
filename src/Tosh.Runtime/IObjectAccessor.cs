namespace Tosh.Runtime;

public interface IObjectAccessor
{
    object? GetValue(object? target, string memberPath);
    void SetValue(object? target, string memberPath, object? value);

    bool IsNullablePath(Type targetType, string memberPath);
}
