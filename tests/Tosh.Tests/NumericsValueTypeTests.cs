using System.Numerics;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The value types graphics and physics code is written in — <c>TOAST-0061</c>.
/// </summary>
/// <remarks>
/// <para>
/// `Vector` is a shell-native dense vector of doubles: heap-allocated, variable-length and
/// enumerable, which is right for a column of readings and close to worst case for a transform
/// hierarchy. The `System.Numerics` family is the other shape — fixed, single-precision, by
/// value — and both are kept.
/// </para>
/// <para>
/// They resolved before this, but only through the platform-index fallback that finds a CLR
/// type by simple name. That fallback is what made `func` resolve to <c>System.Func`1</c>,
/// concrete and wrong, so a name worth relying on is worth aliasing explicitly.
/// </para>
/// </remarks>
public sealed class NumericsValueTypeTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private static async Task<object?> RunValueAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? null : results[^1];
    }

    /// <summary>
    /// Renders through the display pipeline, which is where a profile applies.
    /// </summary>
    /// <remarks>
    /// Not <c>ToString()</c>: the CLR one answers <c>&lt;1, 2, 3&gt;</c> for a
    /// <see cref="Vector3"/> and <c>{X:0 Y:0 Z:0 W:1}</c> for a <see cref="Quaternion"/>,
    /// which is what a first draft of these tests asserted against and would have passed
    /// whatever the profile did.
    /// </remarks>
    private static string Render(object? value) => new DisplayEngine(new ObjectFormatter()).Render(value).Trim();

    [Theory]
    [InlineData("Vector2", "new Vector2(1, 2)")]
    [InlineData("Vector3", "new Vector3(1, 2, 3)")]
    [InlineData("Vector4", "new Vector4(1, 2, 3, 4)")]
    [InlineData("Quaternion", "new Quaternion(0, 0, 0, 1)")]
    [InlineData("Matrix3x2", "System.Numerics.Matrix3x2.Identity")]
    [InlineData("Matrix4x4", "System.Numerics.Matrix4x4.Identity")]
    [InlineData("Plane", "new Plane(0, 1, 0, 0)")]
    public async Task A_numerics_type_is_nameable_in_an_annotation(string alias, string construction)
        => Assert.Equal("ok", await RunAsync($"var v: {alias} = ({construction})\nreturn \"ok\""));

    [Theory]
    [InlineData("Half", "1.5")]
    [InlineData("Int128", "5")]
    [InlineData("UInt128", "5")]
    [InlineData("nint", "5")]
    [InlineData("nuint", "5")]
    public async Task A_general_numeric_alias_resolves(string alias, string literal)
        => Assert.Equal("ok", await RunAsync($"var n: {alias} = {literal}\nreturn \"ok\""));

    /// <summary>
    /// `nint` and `ptr` name the same CLR type and are kept apart on purpose: one says
    /// "pointer-sized integer", the other says "pointer".
    /// </summary>
    [Fact]
    public async Task Nint_and_ptr_are_the_same_type_under_two_names()
        => Assert.Equal("True", await RunAsync(
            "var a: nint = 5\nvar b: ptr = 5\nreturn ((type-of $a) == (type-of $b))"));

    /// <summary>
    /// Arithmetic arrives through the ordinary CLR operator fallback, not a special case.
    /// </summary>
    /// <remarks>
    /// `TOAST-0051` gave the binary operators an `op_*` fallback on both operands. A special
    /// case for these types would be a second mechanism that could disagree with it.
    /// </remarks>
    [Fact]
    public async Task Arithmetic_goes_through_the_clr_operator_fallback()
        => Assert.Equal(
            new Vector3(2, 4, 6),
            await RunValueAsync("var v: Vector3 = (new Vector3(1, 2, 3))\nreturn ($v + $v)"));

    /// <summary>
    /// A vector prints as a value, named by its type.
    /// </summary>
    /// <remarks>
    /// It used to print as a transposed four-row table to say three numbers. The type leads
    /// the form because `(1, 2, 3)` alone does not say whether a fourth component was dropped
    /// or was never there.
    /// </remarks>
    [Theory]
    [InlineData("Vector2(1, 2)")]
    [InlineData("Vector3(1, 2, 3)")]
    [InlineData("Vector4(1, 2, 3, 4)")]
    public async Task A_vector_renders_as_a_named_value(string expected)
    {
        var value = await RunValueAsync($"return (new {expected.Replace("Vector", "Vector")})");

        Assert.Equal(expected, Render(value));
    }

    [Fact]
    public async Task A_quaternion_leads_with_its_scalar_part()
        => Assert.Equal(
            "Quaternion(1; 0, 0, 0)",
            Render(await RunValueAsync("return (new Quaternion(0, 0, 0, 1))")));

    /// <summary>
    /// A matrix keeps its table. Sixteen numbers on one line is not a reading of anything.
    /// </summary>
    [Fact]
    public void A_matrix_is_not_flattened_onto_one_line()
    {
        var rendered = Render(Matrix4x4.Identity);

        Assert.DoesNotContain("Matrix4x4(", rendered, StringComparison.Ordinal);
        Assert.Contains("M11", rendered, StringComparison.Ordinal);
    }
}
