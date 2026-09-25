namespace Tosh.Runtime;

/// <summary>
/// Implemented by shell types that can convert/coerce an instance from another representation
/// (such as converting an anonymous record or dictionary into a declared record instance).
/// </summary>
public interface IShellConvertibleType
{
    bool TryConvertInstance(object value, out object? converted, out string reason);
}
