namespace Tosh.Runtime;

/// <summary>
/// Structured, machine-readable type description for a command
/// argument, option value, or output element. Sits alongside the
/// existing free-form <c>TypeName</c> string so older consumers
/// keep working while new tooling (LSP, MCP, the compiler's
/// argument-shape checker) can reason about types programmatically.
/// </summary>
/// <param name="ClrTypeName">
/// Short CLR type name (<c>System.String</c>, <c>System.Int32</c>,
/// <c>Tosh.Runtime.FileSystemEntry</c>) when the value is bound to
/// a real .NET type. <c>null</c> when the binding is purely
/// syntactic (e.g. a block / path expression).
/// </param>
/// <param name="AssemblyQualifiedName">
/// Full assembly-qualified name. Populated when the attribute
/// supplies a <see cref="System.Type"/> so external tooling can
/// resolve the type via reflection without re-parsing.
/// </param>
/// <param name="Kind">High-level shape of the value.</param>
/// <param name="ElementType">
/// For <see cref="TypedTypeKind.List"/> / <see cref="TypedTypeKind.Stream"/>,
/// the element's own <see cref="TypedTypeRef"/>. <c>null</c>
/// otherwise (or when the element type is unknown).
/// </param>
/// <param name="Refinement">
/// Human-readable refinement label preserved from the source
/// declaration (e.g. <c>"positive int"</c>, <c>"non-empty"</c>).
/// </param>
/// <param name="IsNullable">
/// True if the value may be <c>null</c>. Defaults to true for
/// reference types and false for declared value types.
/// </param>
public sealed record TypedTypeRef(
    string? ClrTypeName,
    string? AssemblyQualifiedName,
    TypedTypeKind Kind,
    TypedTypeRef? ElementType,
    string? Refinement,
    bool IsNullable);

/// <summary>
/// Coarse classification of a typed-metadata value's shape.
/// Names mirror the language-level distinctions tosh's parser
/// already makes: scalars, lists, streams, records, blocks,
/// paths, raw expressions, and the catch-all <see cref="Any"/>.
/// </summary>
public enum TypedTypeKind
{
    /// <summary>Unspecified / opaque — type is not statically known.</summary>
    Any,
    /// <summary>Single value (number, string, bool, custom object).</summary>
    Scalar,
    /// <summary>Eagerly materialized ordered collection.</summary>
    List,
    /// <summary>Lazy stream of values (<c>IAsyncEnumerable&lt;T&gt;</c>).</summary>
    Stream,
    /// <summary>Object / dict-like value with named fields.</summary>
    Record,
    /// <summary>Unevaluated block expression (passed as a callable).</summary>
    Block,
    /// <summary>Filesystem path string.</summary>
    Path,
    /// <summary>Raw expression / AST fragment.</summary>
    Expression,
}

/// <summary>
/// Helpers for producing <see cref="TypedTypeRef"/> values from
/// a CLR <see cref="System.Type"/>. Used by the metadata exporter
/// when an attribute supplies <c>ClrType = typeof(…)</c>.
/// </summary>
public static class TypedTypeRefBuilder
{
    /// <summary>
    /// Build a <see cref="TypedTypeRef"/> from a CLR type, inferring
    /// kind / element type / nullability from the type's structure.
    /// Recognises:
    /// <list type="bullet">
    ///   <item><c>IAsyncEnumerable&lt;T&gt;</c> → <see cref="TypedTypeKind.Stream"/>.</item>
    ///   <item><c>IEnumerable&lt;T&gt;</c> / arrays / <c>IReadOnlyList&lt;T&gt;</c> → <see cref="TypedTypeKind.List"/>.</item>
    ///   <item>
    ///     Primitives, <c>string</c>, <c>DateTime</c>, etc. →
    ///     <see cref="TypedTypeKind.Scalar"/>.
    ///   </item>
    ///   <item>Everything else → <see cref="TypedTypeKind.Record"/>.</item>
    /// </list>
    /// </summary>
    public static TypedTypeRef FromType(Type type, string? refinement = null)
    {
        ArgumentNullException.ThrowIfNull(type);

        var nullable = !type.IsValueType ||
                       (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>));
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            type = type.GenericTypeArguments[0];
        }

        if (TryGetGenericInterfaceArg(type, typeof(IAsyncEnumerable<>), out var streamElem))
        {
            return new TypedTypeRef(
                ClrTypeName: TypeShortName(type),
                AssemblyQualifiedName: type.AssemblyQualifiedName,
                Kind: TypedTypeKind.Stream,
                ElementType: FromType(streamElem!),
                Refinement: refinement,
                IsNullable: nullable);
        }

        if (type.IsArray)
        {
            return new TypedTypeRef(
                ClrTypeName: TypeShortName(type),
                AssemblyQualifiedName: type.AssemblyQualifiedName,
                Kind: TypedTypeKind.List,
                ElementType: FromType(type.GetElementType()!),
                Refinement: refinement,
                IsNullable: nullable);
        }

        if (type != typeof(string) &&
            TryGetGenericInterfaceArg(type, typeof(IEnumerable<>), out var listElem))
        {
            return new TypedTypeRef(
                ClrTypeName: TypeShortName(type),
                AssemblyQualifiedName: type.AssemblyQualifiedName,
                Kind: TypedTypeKind.List,
                ElementType: FromType(listElem!),
                Refinement: refinement,
                IsNullable: nullable);
        }

        var kind = IsScalar(type) ? TypedTypeKind.Scalar : TypedTypeKind.Record;
        return new TypedTypeRef(
            ClrTypeName: TypeShortName(type),
            AssemblyQualifiedName: type.AssemblyQualifiedName,
            Kind: kind,
            ElementType: null,
            Refinement: refinement,
            IsNullable: nullable);
    }

    private static bool IsScalar(Type type)
    {
        if (type.IsPrimitive) return true;
        if (type.IsEnum) return true;
        if (type == typeof(string)) return true;
        if (type == typeof(decimal)) return true;
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return true;
        if (type == typeof(TimeSpan)) return true;
        if (type == typeof(Guid)) return true;
        return false;
    }

    private static bool TryGetGenericInterfaceArg(Type type, Type openGeneric, out Type? arg)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == openGeneric)
        {
            arg = type.GenericTypeArguments[0];
            return true;
        }
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == openGeneric)
            {
                arg = iface.GenericTypeArguments[0];
                return true;
            }
        }
        arg = null;
        return false;
    }

    private static string TypeShortName(Type type)
    {
        if (!type.IsGenericType) return type.FullName ?? type.Name;
        var open = type.GetGenericTypeDefinition().FullName ?? type.Name;
        var tickIdx = open.IndexOf('`');
        if (tickIdx > 0) open = open[..tickIdx];
        var args = string.Join(", ", type.GenericTypeArguments.Select(t => t.FullName ?? t.Name));
        return $"{open}<{args}>";
    }
}
