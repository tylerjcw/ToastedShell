using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// TS-P1-15 — enum members order by their backing value and compare
/// equal to it, so a numeric-backed declaration such as
/// <c>enum Permissions : int</c> behaves the way the specification's
/// examples imply. Members of different enums never compare equal or
/// order against each other.
/// </summary>
public sealed class EnumComparisonTests
{
    private const string Declaration =
        """
        enum E { Low, Mid, High }
        enum F { A, B }
        """;

    [Theory]
    [InlineData("E.Low < E.High", true)]
    [InlineData("E.High > E.Low", true)]
    [InlineData("E.Low <= E.Low", true)]
    [InlineData("E.High < E.Low", false)]
    public async Task Members_order_by_their_backing_value(string expression, bool expected)
    {
        Assert.Equal(expected, await EvaluateAsync(expression));
    }

    [Theory]
    [InlineData("E.Low == 0", true)]
    [InlineData("E.Mid == 1", true)]
    [InlineData("0 == E.Low", true)]
    [InlineData("E.Mid != 1", false)]
    [InlineData("E.Mid == 2", false)]
    public async Task Members_compare_equal_to_their_backing_value(string expression, bool expected)
    {
        Assert.Equal(expected, await EvaluateAsync(expression));
    }

    [Fact]
    public async Task Numeric_equality_is_symmetric()
    {
        Assert.True(await EvaluateAsync("E.Mid == 1"));
        Assert.True(await EvaluateAsync("1 == E.Mid"));
    }

    [Theory]
    [InlineData("E.Low == E.Low", true)]
    [InlineData("E.Low == E.High", false)]
    [InlineData("E.Low == F.A", false)]
    public async Task Member_identity_respects_the_declaring_enum(string expression, bool expected)
    {
        // E.Low and F.A share backing value 0 but are different types,
        // so they must not compare equal.
        Assert.Equal(expected, await EvaluateAsync(expression));
    }

    [Fact]
    public async Task Member_name_equality_still_works()
    {
        Assert.True(await EvaluateAsync("E.Low == \"Low\""));
    }

    [Fact]
    public async Task Explicit_backing_values_are_honoured()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        await engine.ExecuteToListAsync(
            """
            enum Permissions : int {
                None = 0
                Execute = 1
                Write = 2
                Read = 4
            }
            var ordered = (Permissions.Read > Permissions.Write)
            var numeric = (Permissions.Read == 4)
            """);

        Assert.True(engine.TryGetVariableValue("ordered", out var ordered));
        Assert.Equal(true, ordered);
        Assert.True(engine.TryGetVariableValue("numeric", out var numeric));
        Assert.Equal(true, numeric);
    }

    [Fact]
    public async Task Sort_orders_members_by_backing_value()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            """
            enum E { Low, Mid, High }
            [E.High, E.Low, E.Mid] | sort
            """);

        Assert.Equal(
            ["Low", "Mid", "High"],
            results.Select(value => value?.ToString() ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task Members_of_different_enums_do_not_order_against_each_other()
    {
        // E.Low and F.A both back onto 0, so without the type guard they
        // would silently compare as equal-ranked.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            engine.ExecuteToListAsync(
                $"""
                {Declaration}
                var bad = (E.Low < F.B)
                """));
    }

    private static async Task<bool> EvaluateAsync(string expression)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        await engine.ExecuteToListAsync(
            $"""
            {Declaration}
            var result = ({expression})
            """);

        Assert.True(engine.TryGetVariableValue("result", out var result));
        return Assert.IsType<bool>(result);
    }
}
