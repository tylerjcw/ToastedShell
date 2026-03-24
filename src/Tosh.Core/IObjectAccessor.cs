namespace Tosh.Core;

public interface IObjectAccessor
{
    object? GetValue(object? target, string memberPath);

    bool IsNullablePath(Type targetType, string memberPath);
}
