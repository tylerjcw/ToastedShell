using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>unfold</c> stops when its callable returns <c>null</c>, however that null is
/// spelled.
/// </summary>
/// <remarks>
/// <para>
/// The command's own argument documentation says the callable returns
/// <c>[value, next-state]</c> "or null to stop", and the documented spelling could
/// not stop it. An arrow body or a block whose value *is* <c>null</c> produces no
/// pipeline value at all — only an explicit <c>return null</c> produces one — so the
/// single-result check saw zero results and raised
/// <c>tosh.runtime.unfold_requires_single_result</c> where the loop should have
/// ended.
/// </para>
/// <para>
/// The fix is the canonical value-context collapse (`TS-P1-20`: none to <c>null</c>,
/// one to the item, several a diagnostic), applied only where <c>null</c> carries a
/// meaning. It stays off for <c>map</c>, <c>sort</c> and <c>get</c>, where a lambda
/// producing nothing is a mistake worth naming rather than a null to carry forward —
/// which the last test here pins.
/// </para>
/// </remarks>
public sealed class UnfoldStopsOnNullTests
{
    private static async Task<IReadOnlyList<object?>> RunAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        return await engine.ExecuteToListAsync(script);
    }

    /// <summary>Every spelling of the same generator, all stopping at five.</summary>
    [Theory]
    [InlineData("unfold 1 func(n) => (($n <= 5) ? [$n, ($n + 1)] : null) | collect")]
    [InlineData("unfold 1 func(n) => if ($n <= 5) { [$n, ($n + 1)] } else { null } | collect")]
    [InlineData("unfold 1 { if ($_ <= 5) { [$_, ($_ + 1)] } else { null } } | collect")]
    [InlineData("unfold 1 func(n) { return (($n <= 5) ? [$n, ($n + 1)] : null) } | collect")]
    public async Task Unfold_stops_however_the_null_is_spelled(string script)
    {
        var items = Assert.IsAssignableFrom<IEnumerable<object?>>(Assert.Single(await RunAsync(script)));

        Assert.Equal(new object?[] { 1, 2, 3, 4, 5 }, items.ToArray());
    }

    /// <summary>
    /// And a callable that never returns null still runs forever, so the collapse has
    /// not turned an ordinary value into a stop signal.
    /// </summary>
    [Fact]
    public async Task Unfold_does_not_stop_on_an_ordinary_value()
    {
        var items = Assert.IsAssignableFrom<IEnumerable<object?>>(Assert.Single(
            await RunAsync("unfold 1 func(n) => [$n, ($n + 1)] | first 4 | collect")));

        Assert.Equal(new object?[] { 1, 2, 3, 4 }, items.ToArray());
    }

    /// <summary>
    /// The half that must not change: for a command where <c>null</c> means nothing in
    /// particular, a lambda that produces no value is still named as the mistake it is.
    /// </summary>
    [Fact]
    public async Task A_map_lambda_that_produces_nothing_is_still_an_error()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => RunAsync("[1, 2, 3] | map func(x) => null | collect"));

        Assert.Contains("exactly one value", error.Message, StringComparison.Ordinal);
    }
}
