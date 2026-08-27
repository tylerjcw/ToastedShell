namespace Tosh.Runtime;

/// <summary>
/// Marker interface for argument wrappers that carry an explicit parameter name.
/// Implemented by <c>Tosh.Language.NamedArgument</c>; <see cref="ReflectionInvoker"/>
/// uses this to unwrap and reorder arguments by parameter name so that
/// CLR-method/constructor invocations support tosh's <c>name = value</c> syntax.
/// </summary>
public interface INamedArgument
{
    string Name { get; }
    object? Value { get; }
}
