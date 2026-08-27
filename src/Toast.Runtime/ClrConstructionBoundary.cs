namespace Tosh.Runtime;

/// <summary>
/// Constructs a compiler-resolved CLR type with the same overload binder used by the
/// interactive runtime, without requiring the language engine or compiler host.
/// </summary>
public static class ClrConstructionBoundary
{
    private static readonly ReflectionInvoker Invoker = new();

    public static object Create(Type type, object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(arguments);
        return Invoker.CreateInstance(type, arguments);
    }
}
