namespace Tosh.Runtime;

/// <summary>
/// Implemented by shell objects that support type checking via the <c>is</c> operator
/// with awareness of custom type hierarchies (class inheritance, interface implementation, etc.).
/// </summary>
public interface IShellTypeCheckable
{
    /// <summary>
    /// Returns <c>true</c> if this instance should be considered an instance of the given type name.
    /// Implementations should check the instance's own type name, base class chain,
    /// implemented interfaces, and any CLR backing type.
    /// </summary>
    bool IsInstanceOf(string typeName);
}
