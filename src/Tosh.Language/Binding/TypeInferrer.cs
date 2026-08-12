using System.Collections.Concurrent;
using System.Reflection;
using Tosh.Compiler.IR;
using Tosh.Runtime;
using Tosh.Runtime.Units;

namespace Tosh.Language.Binding;

/// <summary>
/// Light static type inference for the lowered tree. The goal is not
/// soundness — anything we can't prove stays <see cref="BoundType.Dynamic"/>,
/// matching the dynamic-by-default policy. It exists so the IL backend
/// can emit unboxed numeric paths through the most common shape:
/// <c>1..N | where _ &gt; k | sort | first M</c>.
///
/// Rules implemented:
///   • Literal types are taken verbatim from <see cref="BoundLiteral.Type"/>.
///   • Binary numeric ops apply C#-style promotion across int/long/double/decimal.
///   • Comparison and logical ops produce bool when both operands are concrete.
///   • Unary <c>-</c> preserves the operand's numeric type; <c>!</c>/<c>not</c> → bool.
///   • <c>start..end[..step]</c> produces <c>IEnumerable&lt;T&gt;</c> when all sides
///     are the same numeric T (int by default for the common case).
///   • A var declaration whose value is a single-stage pipeline of one
///     argument adopts that argument's type.
/// Anything ambiguous stays dynamic.
/// </summary>
public static class TypeInferrer
{
    public static BoundType InferBinary(BoundType left, string op, BoundType right)
    {
        switch (op)
        {
            case "==":
            case "!=":
            case "<":
            case "<=":
            case ">":
            case ">=":
            case "&&":
            case "||":
            case "and":
            case "or":
                return BoundType.FromClr(typeof(bool));

            case "+":
            case "-":
            case "*":
            case "/":
            case "%":
            case "**":
                return PromoteNumeric(left, right);

            default:
                return BoundType.Dynamic;
        }
    }

    public static BoundType InferUnary(string op, BoundType operand) => op switch
    {
        "-" or "+" when IsNumeric(operand) => operand,
        "-" or "+" when operand.ClrType is { } type && typeof(Quantity).IsAssignableFrom(type) => operand,
        "!" or "not" => BoundType.FromClr(typeof(bool)),
        _ => BoundType.Dynamic,
    };

    /// <summary>
    /// Type of a range expression: <c>IEnumerable&lt;T&gt;</c> when all
    /// supplied sides agree on a numeric T; dynamic otherwise.
    /// </summary>
    public static BoundType InferRange(BoundType start, BoundType? step, BoundType end)
    {
        var element = PromoteNumeric(start, end);
        if (step is not null) element = PromoteNumeric(element, step);
        if (!IsNumeric(element) || element.ClrType is null) return BoundType.Dynamic;

        var enumerable = typeof(IEnumerable<>).MakeGenericType(element.ClrType);
        return BoundType.FromClr(enumerable);
    }

    /// <summary>
    /// Best-effort: a pipeline whose only stage is a single positional
    /// expression takes that expression's type. A single-stage
    /// command call uses the command's <c>[CommandOutput(ClrType=…)]</c>
    /// annotation when present.
    /// </summary>
    public static BoundType InferPipelineValue(BoundPipeline pipeline)
    {
        if (pipeline.Stages.Count != 1) return BoundType.Dynamic;

        return pipeline.Stages[0] switch
        {
            BoundExpressionStage expr => expr.Value.Type,
            BoundCommandCall call => InferCommandOutput(call),
            _ => BoundType.Dynamic,
        };
    }

    /// <summary>
    /// Reads <c>[CommandOutput(ClrType=…)]</c> off the resolved
    /// command and folds it into a <see cref="BoundType"/>:
    /// <c>IAsyncEnumerable&lt;T&gt;</c> and <c>IEnumerable&lt;T&gt;</c>
    /// flatten to <c>list&lt;T&gt;</c> (the pipeline's element view);
    /// arrays do the same; scalars become <see cref="ConcreteType"/>.
    /// </summary>
    public static BoundType InferCommandOutput(BoundCommandCall call)
    {
        if (call.ResolvedCommand is null) return BoundType.Dynamic;
        var clr = GetCommandOutputClrType(call.ResolvedCommand.GetType());
        if (clr is null) return BoundType.Dynamic;
        return ClrToBoundForCommandOutput(clr);
    }

    private static readonly ConcurrentDictionary<Type, Type?> s_commandOutputClrTypeCache = new();

    private static Type? GetCommandOutputClrType(Type commandType)
    {
        return s_commandOutputClrTypeCache.GetOrAdd(commandType, static t =>
        {
            var attr = t.GetCustomAttribute<CommandOutputAttribute>(inherit: false);
            return attr?.ClrType;
        });
    }

    /// <summary>
    /// Map a <c>[CommandOutput(ClrType=T)]</c> CLR type to the bound
    /// type the pipeline value would have. Stream / enumerable types
    /// flatten to <c>list&lt;element&gt;</c> because tosh pipelines
    /// always present their stages as a sequence of elements.
    /// </summary>
    private static BoundType ClrToBoundForCommandOutput(Type type)
    {
        // Unwrap Nullable<T>.
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            type = type.GenericTypeArguments[0];
        }

        // IAsyncEnumerable<T> / IEnumerable<T> / IReadOnlyList<T> / arrays.
        // Command output emerges through the pipeline as a sequence of
        // values; modelling it as `stream<T>` lets the type checker
        // honour tosh's runtime materialization rule (`values.Count == 1
        // ? values[0] : values.ToArray()`) — `stream<T>` is assignable
        // to `T`, `list<T>`, `T[]`, or `stream<T>`.
        if (TryGetEnumerableElement(type, out var element))
        {
            return new StreamType(BoundType.FromClr(element!));
        }

        return BoundType.FromClr(type);
    }

    private static bool TryGetEnumerableElement(Type type, out Type? element)
    {
        element = null;
        if (type == typeof(string)) return false;
        if (type.IsArray) { element = type.GetElementType(); return element is not null; }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(IAsyncEnumerable<>) || def == typeof(IEnumerable<>) ||
                def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>) ||
                def == typeof(IList<>) || def == typeof(ICollection<>) ||
                def == typeof(List<>))
            {
                element = type.GenericTypeArguments[0];
                return true;
            }
        }

        foreach (var iface in type.GetInterfaces())
        {
            if (!iface.IsGenericType) continue;
            var def = iface.GetGenericTypeDefinition();
            if (def == typeof(IAsyncEnumerable<>) || def == typeof(IEnumerable<>))
            {
                element = iface.GenericTypeArguments[0];
                return true;
            }
        }
        return false;
    }

    private static BoundType PromoteNumeric(BoundType a, BoundType b)
    {
        if (!IsNumeric(a) || !IsNumeric(b)) return BoundType.Dynamic;

        // Rank the two sides; the higher one wins. Order chosen to
        // match C#'s usual numeric conversions for the types we model.
        var ra = NumericRank(a.ClrType!);
        var rb = NumericRank(b.ClrType!);
        var winner = ra >= rb ? a.ClrType! : b.ClrType!;
        return BoundType.FromClr(winner);
    }

    private static bool IsNumeric(BoundType t)
    {
        if (!t.IsConcrete || t.ClrType is null) return false;
        return NumericRank(t.ClrType) > 0;
    }

    private static int NumericRank(Type t) => t switch
    {
        _ when t == typeof(int) => 1,
        _ when t == typeof(long) => 2,
        _ when t == typeof(float) => 3,
        _ when t == typeof(double) => 4,
        _ when t == typeof(decimal) => 5,
        _ => 0,
    };
}
