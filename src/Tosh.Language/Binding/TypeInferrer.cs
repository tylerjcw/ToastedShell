using Tosh.Language.Binding.BoundNodes;

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
        if (step.HasValue) element = PromoteNumeric(element, step.Value);
        if (!IsNumeric(element) || element.ClrType is null) return BoundType.Dynamic;

        var enumerable = typeof(IEnumerable<>).MakeGenericType(element.ClrType);
        return BoundType.FromClr(enumerable);
    }

    /// <summary>
    /// Best-effort: a pipeline whose only stage is a single positional
    /// expression takes that expression's type. Anything more
    /// complicated (commands, multiple stages) stays dynamic in v1 —
    /// command-output typing requires per-command return-type metadata
    /// that the registry doesn't expose yet.
    /// </summary>
    public static BoundType InferPipelineValue(BoundPipeline pipeline)
    {
        if (pipeline.Stages.Count != 1) return BoundType.Dynamic;

        return pipeline.Stages[0] switch
        {
            BoundExpressionStage expr => expr.Value.Type,
            _ => BoundType.Dynamic,
        };
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
