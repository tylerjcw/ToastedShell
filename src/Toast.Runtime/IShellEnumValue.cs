namespace Tosh.Runtime;

/// <summary>
/// A shell-defined enum member. Implemented by
/// <c>Tosh.Language.ToshEnumValue</c> so runtime operator dispatch can
/// order and compare enum members without depending on the language
/// assembly (TS-P1-15). Members compare by <see cref="UnderlyingValue"/>,
/// which is how a numeric-backed enum such as
/// <c>enum Permissions : int</c> is expected to behave.
/// </summary>
public interface IShellEnumValue
{
    /// <summary>The declared member name, e.g. <c>"Low"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// The member's backing value. Numeric for ordinary declarations,
    /// including the implicit 0, 1, 2 … assigned when a declaration
    /// gives no explicit values.
    /// </summary>
    object? UnderlyingValue { get; }

    /// <summary>The owning enum type's name, used to keep members of
    /// different enums from comparing equal.</summary>
    string EnumTypeName { get; }
}
