using System.Collections;
using System.Globalization;

namespace Tosh.Runtime;

/// <summary>
/// Shell static type for <c>Vector</c> — exposes factory methods (zeros, ones, fill, range, random, from-list)
/// and linear algebra operations (dot, cross, magnitude, normalize, sum, min, max, mean) as static methods.
/// Also serves as constructor: <c>vec 1 2 3</c>, <c>new vec(1, 2, 3)</c>, or <c>new Vector(1, 2, 3)</c>.
/// </summary>
public sealed class VectorShellType : IShellNamedType
{
    private static readonly IReadOnlyList<ShellMemberDescriptor> Members =
    [
        new("Length", "Property", "System.Int32", IsStatic: false, IsWritable: false),
        new("Magnitude", "Property", "System.Double", IsStatic: false, IsWritable: false),
    ];

    private static readonly IReadOnlyList<ShellMethodDescriptor> Methods =
    [
        new("Slice", "Vector", IsStatic: false, ParameterCount: 2, Signature: "Vector Slice(System.Int32 start, System.Int32 end)"),
        new("AsSpan", "System.ReadOnlySpan<System.Double>", IsStatic: false, ParameterCount: 0, Signature: "System.ReadOnlySpan<System.Double> AsSpan()"),
        new("ToArray", "System.Double[]", IsStatic: false, ParameterCount: 0, Signature: "System.Double[] ToArray()"),
        new("Magnitude", "System.Double", IsStatic: false, ParameterCount: 0, Signature: "System.Double Magnitude()"),
        new("Normalize", "Vector", IsStatic: false, ParameterCount: 0, Signature: "Vector Normalize()"),
        new("Sum", "System.Double", IsStatic: false, ParameterCount: 0, Signature: "System.Double Sum()"),
        new("Min", "System.Double", IsStatic: false, ParameterCount: 0, Signature: "System.Double Min()"),
        new("Max", "System.Double", IsStatic: false, ParameterCount: 0, Signature: "System.Double Max()"),
        new("Mean", "System.Double", IsStatic: false, ParameterCount: 0, Signature: "System.Double Mean()"),
        new("zeros", "Vector", IsStatic: true, ParameterCount: 1, Signature: "static Vector zeros(System.Int32 length)"),
        new("ones", "Vector", IsStatic: true, ParameterCount: 1, Signature: "static Vector ones(System.Int32 length)"),
        new("fill", "Vector", IsStatic: true, ParameterCount: 2, Signature: "static Vector fill(System.Int32 length, System.Double value)"),
        new("range", "Vector", IsStatic: true, ParameterCount: 2, Signature: "static Vector range(System.Double start, System.Double end)"),
        new("range", "Vector", IsStatic: true, ParameterCount: 3, Signature: "static Vector range(System.Double start, System.Double end, System.Double step)"),
        new("random", "Vector", IsStatic: true, ParameterCount: 1, Signature: "static Vector random(System.Int32 length)"),
        new("from-list", "Vector", IsStatic: true, ParameterCount: 1, Signature: "static Vector from-list(items)"),
        new("dot", "System.Double", IsStatic: true, ParameterCount: 2, Signature: "static System.Double dot(Vector left, Vector right)"),
        new("cross", "Vector", IsStatic: true, ParameterCount: 2, Signature: "static Vector cross(Vector left, Vector right)"),
        new("magnitude", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double magnitude(Vector value)"),
        new("normalize", "Vector", IsStatic: true, ParameterCount: 1, Signature: "static Vector normalize(Vector value)"),
        new("sum", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double sum(Vector value)"),
        new("min", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double min(Vector value)"),
        new("max", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double max(Vector value)"),
        new("mean", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double mean(Vector value)"),
    ];

    private static readonly IReadOnlyList<ShellConstructorDescriptor> Constructors =
    [
        new(-1, "Vector(items...)"),
        new(1, "Vector(items)"),
    ];

    public static readonly VectorShellType Instance = new();

    public string ShellTypeName => "Vector";

    public string ShellFullName => "ToSh.Vector";

    public string? ShellNamespace => "ToSh";

    public string? ShellAssemblyName => "ToSh";

    public string? ShellBaseTypeName => typeof(object).FullName;

    public bool ShellIsClass => true;

    public bool ShellIsInterface => false;

    public bool ShellIsEnum => false;

    public bool ShellIsValueType => false;

    public bool ShellIsAbstract => false;

    public bool ShellIsGenericType => false;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments)
    {
        if (TryExpandSingleEnumerableArgument(arguments, out var enumerable))
            return ToshVector.FromList(enumerable);

        var values = new double[arguments.Count];
        for (int i = 0; i < arguments.Count; i++)
            values[i] = ToDouble(arguments[i], i);
        return new ToshVector(values);
    }

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        object result = methodName.ToLowerInvariant() switch
        {
            // Factories
            "zeros" => ToshVector.Zeros(ToInt(arguments, 0)),
            "ones" => ToshVector.Ones(ToInt(arguments, 0)),
            "fill" => ToshVector.Fill(ToInt(arguments, 0), ToDouble(arguments[1], 1)),
            "range" => arguments.Count >= 3
                ? ToshVector.Range(ToDouble(arguments[0], 0), ToDouble(arguments[1], 1), ToDouble(arguments[2], 2))
                : ToshVector.Range(ToDouble(arguments[0], 0), ToDouble(arguments[1], 1)),
            "random" => ToshVector.Random(ToInt(arguments, 0)),
            "from-list" or "fromlist" => ToshVector.FromList(ToEnumerable(arguments, 0)),

            // Linear algebra
            "dot" => ToshVector.Dot(ToVector(arguments, 0), ToVector(arguments, 1)),
            "cross" => ToshVector.Cross(ToVector(arguments, 0), ToVector(arguments, 1)),
            "magnitude" => ToVector(arguments, 0).Magnitude(),
            "normalize" => ToVector(arguments, 0).Normalize(),
            "sum" => ToVector(arguments, 0).Sum(),
            "min" => ToVector(arguments, 0).Min(),
            "max" => ToVector(arguments, 0).Max(),
            "mean" => ToVector(arguments, 0).Mean(),

            _ => throw new InvalidOperationException(
                $"Vector.{methodName} is not a recognized function. " +
                "Available: zeros, ones, fill, range, random, from-list, dot, cross, magnitude, normalize, sum, min, max, mean."),
        };

        return new InvocationResult(result, false);
    }

    public bool TryGetStaticMember(string memberName, out object? value)
    {
        value = null;
        return false;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        value = name switch
        {
            "Name" => ShellTypeName,
            "FullName" => ShellFullName,
            "Namespace" => ShellNamespace,
            "Assembly" => ShellAssemblyName,
            "BaseType" => ShellBaseTypeName,
            "IsClass" => ShellIsClass,
            "IsInterface" => ShellIsInterface,
            "IsEnum" => ShellIsEnum,
            "IsValueType" => ShellIsValueType,
            "IsAbstract" => ShellIsAbstract,
            "IsGenericType" => ShellIsGenericType,
            "IsArray" => ShellIsArray,
            "IsPublic" => ShellIsPublic,
            "PropertyCount" => Members.Count(member => !member.IsStatic),
            "MethodCount" => Methods.Count(method => !method.IsStatic),
            "StaticMethodCount" => Methods.Count(method => method.IsStatic),
            "ConstructorCount" => Constructors.Count,
            _ => null,
        };

        return value is not null;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false) =>
    [
        new("Name", ShellTypeName),
        new("FullName", ShellFullName),
        new("Namespace", ShellNamespace),
        new("Assembly", ShellAssemblyName),
        new("BaseType", ShellBaseTypeName),
        new("IsClass", ShellIsClass),
        new("IsInterface", ShellIsInterface),
        new("IsEnum", ShellIsEnum),
        new("IsValueType", ShellIsValueType),
        new("IsAbstract", ShellIsAbstract),
        new("IsGenericType", ShellIsGenericType),
        new("IsArray", ShellIsArray),
        new("IsPublic", ShellIsPublic),
        new("PropertyCount", Members.Count(member => !member.IsStatic)),
        new("MethodCount", Methods.Count(method => !method.IsStatic)),
        new("StaticMethodCount", Methods.Count(method => method.IsStatic)),
        new("ConstructorCount", Constructors.Count),
    ];

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false) => Members;

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false) => Methods;

    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors() => Constructors;

    #region Helpers

    private static double ToDouble(object? value, int argIndex)
    {
        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        };
    }

    private static int ToInt(IReadOnlyList<object?> arguments, int index)
    {
        if (index >= arguments.Count)
            throw new InvalidOperationException($"Vector function expected at least {index + 1} argument(s), got {arguments.Count}.");
        return arguments[index] switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            _ => Convert.ToInt32(arguments[index], CultureInfo.InvariantCulture),
        };
    }

    private static ToshVector ToVector(IReadOnlyList<object?> arguments, int index)
    {
        if (index >= arguments.Count)
            throw new InvalidOperationException($"Vector function expected at least {index + 1} argument(s), got {arguments.Count}.");
        if (arguments[index] is ToshVector v) return v;
        if (TryExpandSingleEnumerable(arguments[index], out var enumerable))
            return ToshVector.FromList(enumerable);
        throw new InvalidOperationException($"Expected a Vector argument at position {index}, got {arguments[index]?.GetType().Name ?? "null"}.");
    }

    private static IEnumerable<object?> ToEnumerable(IReadOnlyList<object?> arguments, int index)
    {
        if (index >= arguments.Count)
            throw new InvalidOperationException($"Vector.from-list expected at least {index + 1} argument(s), got {arguments.Count}.");
        if (arguments[index] is IEnumerable<object?> enumerable) return enumerable;
        if (arguments[index] is System.Collections.IEnumerable ie) return ie.Cast<object?>();
        throw new InvalidOperationException($"Expected a list argument at position {index}, got {arguments[index]?.GetType().Name ?? "null"}.");
    }

    private static bool TryExpandSingleEnumerableArgument(IReadOnlyList<object?> arguments, out IEnumerable<object?> enumerable)
    {
        if (arguments.Count == 1 && TryExpandSingleEnumerable(arguments[0], out enumerable))
            return true;

        enumerable = Array.Empty<object?>();
        return false;
    }

    private static bool TryExpandSingleEnumerable(object? value, out IEnumerable<object?> enumerable)
    {
        switch (value)
        {
            case null:
            case string:
            case IDictionary:
                enumerable = Array.Empty<object?>();
                return false;

            case ToshVector vector:
                enumerable = vector.Cast<object?>();
                return true;

            case IEnumerable<object?> typedEnumerable:
                enumerable = typedEnumerable;
                return true;

            case IEnumerable<double> doubleEnumerable:
                enumerable = doubleEnumerable.Cast<object?>();
                return true;

            case IEnumerable rawEnumerable when value is not IShellRecordObject:
                enumerable = rawEnumerable.Cast<object?>();
                return true;

            default:
                enumerable = Array.Empty<object?>();
                return false;
        }
    }

    #endregion
}
