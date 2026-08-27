using System.Collections;

namespace Tosh.Runtime;

/// <summary>
/// Shell static type for <c>Matrix</c> and <c>mat</c>.
/// Exposes matrix factories and linear-algebra helpers as static methods.
/// </summary>
public sealed class MatrixShellType : IShellNamedType
{
    private static readonly IReadOnlyList<ShellMemberDescriptor> Members =
    [
        new("RowCount", "Property", "System.Int32", IsStatic: false, IsWritable: false),
        new("ColumnCount", "Property", "System.Int32", IsStatic: false, IsWritable: false),
        new("IsSquare", "Property", "System.Boolean", IsStatic: false, IsWritable: false),
        new("Shape", "Property", "System.Int32[]", IsStatic: false, IsWritable: false),
    ];

    private static readonly IReadOnlyList<ShellMethodDescriptor> Methods =
    [
        new("GetRow", "Vector", IsStatic: false, ParameterCount: 1, Signature: "Vector GetRow(System.Int32 index)"),
        new("GetColumn", "Vector", IsStatic: false, ParameterCount: 1, Signature: "Vector GetColumn(System.Int32 index)"),
        new("ToJaggedArray", "System.Double[][]", IsStatic: false, ParameterCount: 0, Signature: "System.Double[][] ToJaggedArray()"),
        new("ToArray2D", "System.Double[,]", IsStatic: false, ParameterCount: 0, Signature: "System.Double[,] ToArray2D()"),
        new("Transpose", "Matrix", IsStatic: false, ParameterCount: 0, Signature: "Matrix Transpose()"),
        new("Determinant", "System.Double", IsStatic: false, ParameterCount: 0, Signature: "System.Double Determinant()"),
        new("Inverse", "Matrix", IsStatic: false, ParameterCount: 0, Signature: "Matrix Inverse()"),
        new("Trace", "System.Double", IsStatic: false, ParameterCount: 0, Signature: "System.Double Trace()"),
        new("FrobeniusNorm", "System.Double", IsStatic: false, ParameterCount: 0, Signature: "System.Double FrobeniusNorm()"),
        new("Sum", "System.Double", IsStatic: false, ParameterCount: 0, Signature: "System.Double Sum()"),
        new("Min", "System.Double", IsStatic: false, ParameterCount: 0, Signature: "System.Double Min()"),
        new("Max", "System.Double", IsStatic: false, ParameterCount: 0, Signature: "System.Double Max()"),
        new("Mean", "System.Double", IsStatic: false, ParameterCount: 0, Signature: "System.Double Mean()"),
        new("zeros", "Matrix", IsStatic: true, ParameterCount: 2, Signature: "static Matrix zeros(System.Int32 rows, System.Int32 columns)"),
        new("ones", "Matrix", IsStatic: true, ParameterCount: 2, Signature: "static Matrix ones(System.Int32 rows, System.Int32 columns)"),
        new("fill", "Matrix", IsStatic: true, ParameterCount: 3, Signature: "static Matrix fill(System.Int32 rows, System.Int32 columns, System.Double value)"),
        new("identity", "Matrix", IsStatic: true, ParameterCount: 1, Signature: "static Matrix identity(System.Int32 size)"),
        new("random", "Matrix", IsStatic: true, ParameterCount: 2, Signature: "static Matrix random(System.Int32 rows, System.Int32 columns)"),
        new("from-rows", "Matrix", IsStatic: true, ParameterCount: 1, Signature: "static Matrix from-rows(rows)"),
        new("transpose", "Matrix", IsStatic: true, ParameterCount: 1, Signature: "static Matrix transpose(Matrix value)"),
        new("determinant", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double determinant(Matrix value)"),
        new("inverse", "Matrix", IsStatic: true, ParameterCount: 1, Signature: "static Matrix inverse(Matrix value)"),
        new("trace", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double trace(Matrix value)"),
        new("norm", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double norm(Matrix value)"),
        new("sum", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double sum(Matrix value)"),
        new("min", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double min(Matrix value)"),
        new("max", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double max(Matrix value)"),
        new("mean", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double mean(Matrix value)"),
        new("multiply", "Matrix", IsStatic: true, ParameterCount: 2, Signature: "static Matrix multiply(Matrix left, Matrix right)"),
        new("hadamard", "Matrix", IsStatic: true, ParameterCount: 2, Signature: "static Matrix hadamard(Matrix left, Matrix right)"),
    ];

    private static readonly IReadOnlyList<ShellConstructorDescriptor> Constructors =
    [
        new(-1, "Matrix(rows...)"),
        new(1, "Matrix(rows)"),
    ];

    public static readonly MatrixShellType Instance = new();

    public string ShellTypeName => "Matrix";

    public string ShellFullName => "ToSh.Matrix";

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
        if (arguments.Count == 1 && ShouldTreatAsSingleMatrixValue(arguments[0]))
            return ToshMatrix.FromValue(arguments[0]);

        return ToshMatrix.FromRows(arguments);
    }

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        object result = methodName.ToLowerInvariant() switch
        {
            "zeros" => ToshMatrix.Zeros(ToInt(arguments, 0), ToInt(arguments, 1)),
            "ones" => ToshMatrix.Ones(ToInt(arguments, 0), ToInt(arguments, 1)),
            "fill" => ToshMatrix.Fill(ToInt(arguments, 0), ToInt(arguments, 1), ToDouble(arguments, 2)),
            "identity" => ToshMatrix.Identity(ToInt(arguments, 0)),
            "random" => ToshMatrix.Random(ToInt(arguments, 0), ToInt(arguments, 1)),
            "from-rows" or "fromrows" => CreateInstance(arguments),
            "transpose" => ToMatrix(arguments, 0).Transpose(),
            "determinant" => ToMatrix(arguments, 0).Determinant(),
            "inverse" => ToMatrix(arguments, 0).Inverse(),
            "trace" => ToMatrix(arguments, 0).Trace(),
            "norm" => ToMatrix(arguments, 0).FrobeniusNorm(),
            "sum" => ToMatrix(arguments, 0).Sum(),
            "min" => ToMatrix(arguments, 0).Min(),
            "max" => ToMatrix(arguments, 0).Max(),
            "mean" => ToMatrix(arguments, 0).Mean(),
            "multiply" => ToshMatrix.Multiply(ToMatrix(arguments, 0), ToMatrix(arguments, 1)),
            "hadamard" => ToshMatrix.Hadamard(ToMatrix(arguments, 0), ToMatrix(arguments, 1)),
            _ => throw new InvalidOperationException(
                $"Matrix.{methodName} is not a recognized function. Available: zeros, ones, fill, identity, random, from-rows, transpose, determinant, inverse, trace, norm, sum, min, max, mean, multiply, hadamard."),
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

    private static int ToInt(IReadOnlyList<object?> arguments, int index)
    {
        if (index >= arguments.Count)
            throw new InvalidOperationException($"Matrix function expected at least {index + 1} argument(s), got {arguments.Count}.");

        return arguments[index] switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            _ => Convert.ToInt32(arguments[index]),
        };
    }

    private static double ToDouble(IReadOnlyList<object?> arguments, int index)
    {
        if (index >= arguments.Count)
            throw new InvalidOperationException($"Matrix function expected at least {index + 1} argument(s), got {arguments.Count}.");

        return arguments[index] switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            _ => Convert.ToDouble(arguments[index]),
        };
    }

    private static ToshMatrix ToMatrix(IReadOnlyList<object?> arguments, int index)
    {
        if (index >= arguments.Count)
            throw new InvalidOperationException($"Matrix function expected at least {index + 1} argument(s), got {arguments.Count}.");

        return arguments[index] switch
        {
            ToshMatrix matrix => matrix,
            _ => ToshMatrix.FromValue(arguments[index]),
        };
    }

    private static bool ShouldTreatAsSingleMatrixValue(object? value)
    {
        return value switch
        {
            ToshMatrix => true,
            Array array when array.Rank == 2 => true,
            null or string or ShellTextLine or IDictionary => false,
            _ when ShellRecordUtilities.IsRecordLike(value) => false,
            IEnumerable => true,
            _ => false,
        };
    }
}
