using System.Numerics;
using System.Runtime.InteropServices;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// `TOAST-0051`: CLR values keep TōSh's built-in operator rules, then fall back to
/// their public static <c>op_*</c> methods when the built-ins do not describe the pair.
/// </summary>
public sealed class ClrOperatorFallbackTests
{
    [Fact]
    public void Numerics_value_types_use_their_clr_addition_operators()
    {
        var vector = Assert.IsType<Vector3>(OperatorEvaluator.EvaluateBinary(
            new Vector3(1, 2, 3),
            "+",
            new Vector3(4, 5, 6)));
        Assert.Equal(new Vector3(5, 7, 9), vector);

        var quaternion = Assert.IsType<Quaternion>(OperatorEvaluator.EvaluateBinary(
            new Quaternion(1, 2, 3, 4),
            "+",
            new Quaternion(4, 3, 2, 1)));
        Assert.Equal(new Quaternion(5, 5, 5, 5), quaternion);

        var matrix = Assert.IsType<Matrix4x4>(OperatorEvaluator.EvaluateBinary(
            Matrix4x4.CreateScale(2),
            "+",
            Matrix4x4.CreateScale(3)));
        Assert.Equal(5, matrix.M11);
        Assert.Equal(2, matrix.M44);
    }

    [Fact]
    public void Clr_comparison_operators_are_used_after_language_builtins()
    {
        var low = new ClrOrderedValue(2);
        var high = new ClrOrderedValue(7);
        var anotherLow = new ClrOrderedValue(2);

        Assert.Equal(true, OperatorEvaluator.EvaluateBinary(low, "<", high));
        Assert.Equal(true, OperatorEvaluator.EvaluateBinary(high, ">=", low));
        Assert.Equal(true, OperatorEvaluator.EvaluateBinary(low, "==", anotherLow));
        Assert.Equal(false, OperatorEvaluator.EvaluateBinary(low, "!=", anotherLow));
    }

    [Fact]
    public void Native_layout_struct_uses_the_same_lookup_as_the_add_trait()
    {
        var left = new NativePoint(3, 4);
        var right = new NativePoint(5, 6);

        Assert.Equal(true, OperatorEvaluator.EvaluateBinary(left, "is", "Add"));

        var sum = Assert.IsType<NativePoint>(
            OperatorEvaluator.EvaluateBinary(left, "+", right));
        Assert.Equal(8, sum.X);
        Assert.Equal(10, sum.Y);
    }

    [Fact]
    public void Missing_operator_keeps_the_two_operand_type_diagnostic()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            OperatorEvaluator.EvaluateBinary(new Version(1, 0), "+", new Version(2, 0)));

        Assert.Equal(
            "Operator operands 'System.Version' and 'System.Version' are not compatible.",
            exception.Message);
    }

    [Fact]
    public void Clr_operator_body_failures_are_not_hidden_by_reflection()
    {
        var exception = Assert.Throws<OperatorProbeException>(() =>
            OperatorEvaluator.EvaluateBinary(new ThrowingClrOperator(), "+", new ThrowingClrOperator()));

        Assert.Equal("operator body failed", exception.Message);
    }

    private sealed class ClrOrderedValue(int value)
    {
        public int Value { get; } = value;

        public static bool operator <(ClrOrderedValue left, ClrOrderedValue right) =>
            left.Value < right.Value;

        public static bool operator >(ClrOrderedValue left, ClrOrderedValue right) =>
            left.Value > right.Value;

        public static bool operator <=(ClrOrderedValue left, ClrOrderedValue right) =>
            left.Value <= right.Value;

        public static bool operator >=(ClrOrderedValue left, ClrOrderedValue right) =>
            left.Value >= right.Value;

        public static bool operator ==(ClrOrderedValue? left, ClrOrderedValue? right) =>
            left?.Value == right?.Value;

        public static bool operator !=(ClrOrderedValue? left, ClrOrderedValue? right) =>
            left?.Value != right?.Value;

        // Deliberately reference-based: the operator tests prove dispatch does not merely
        // happen to agree with the ordinary Equals fallback.
        public override bool Equals(object? obj) => ReferenceEquals(this, obj);

        public override int GetHashCode() => Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;

        public static NativePoint operator +(NativePoint left, NativePoint right) =>
            new(left.X + right.X, left.Y + right.Y);
    }

    private sealed class ThrowingClrOperator
    {
        public static ThrowingClrOperator operator +(
            ThrowingClrOperator left,
            ThrowingClrOperator right)
        {
            _ = left;
            _ = right;
            throw new OperatorProbeException("operator body failed");
        }
    }

    private sealed class OperatorProbeException(string message) : Exception(message);
}
