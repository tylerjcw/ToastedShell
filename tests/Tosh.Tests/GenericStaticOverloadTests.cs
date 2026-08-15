using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A generic static call selects among same-named overloads by argument count.
///
/// `TS-P2-97` was filed for this and **does not reproduce**. It was measured on
/// `Avalonia.AppBuilder.Configure&lt;TApp&gt;()`, which reported "Static member
/// 'Configure' was not found" while `methods` listed both overloads.
///
/// The likely explanation is `TS-P2-96`, fixed in the same session: `load-assembly`
/// forced the whole type closure and threw partway through, so loading Avalonia's
/// assemblies **stopped early**. Overload selection was then being asked to choose
/// among methods whose parameter types lived in assemblies that had never
/// finished loading. With that fixed, the reported call returns an `AppBuilder`.
///
/// The corpus is BCL-based rather than Avalonia-based so it runs anywhere:
/// <c>ImmutableArray.Create&lt;T&gt;</c> has the same shape the item describes —
/// generic static overloads differing only in parameter count, including a
/// zero-argument one.
/// </summary>
public class GenericStaticOverloadTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(v => v?.ToString() ?? "null"));
    }

    /// <summary>
    /// The zero-argument form is the one the item reported failing, so it leads.
    /// </summary>
    [Theory]
    [InlineData("(System.Collections.Immutable.ImmutableArray.Create<int>()).Length", "0")]
    [InlineData("(System.Collections.Immutable.ImmutableArray.Create<int>(7)).Length", "1")]
    [InlineData("(System.Collections.Immutable.ImmutableArray.Create<int>(7, 8)).Length", "2")]
    [InlineData("(System.Collections.Immutable.ImmutableArray.Create<int>(7, 8, 9)).Length", "3")]
    public async Task A_generic_static_selects_by_argument_count(string expression, string expected)
        => Assert.Equal(expected, await RunAsync(expression));

    /// <summary>
    /// The type argument is honoured rather than inferred away — a selection that
    /// picked the right arity but the wrong instantiation would pass the counts
    /// above.
    /// </summary>
    [Fact]
    public async Task The_explicit_type_argument_decides_the_element_type()
        => Assert.Equal("String", await RunAsync(
            """
            var a = System.Collections.Immutable.ImmutableArray.Create<string>("x")
            $a[0].GetType().Name
            """));

    /// <summary>
    /// Generic statics taking a single argument were never in question, and are
    /// kept as the reference the failing case was compared against.
    /// </summary>
    [Theory]
    [InlineData("(Array.Empty<int>()).Length", "0")]
    [InlineData("System.Linq.Enumerable.Count<int>([1, 2, 3])", "3")]
    [InlineData("System.Linq.Enumerable.First<int>([4, 5])", "4")]
    public async Task Ordinary_generic_statics_still_resolve(string expression, string expected)
        => Assert.Equal(expected, await RunAsync(expression));

    /// <summary>
    /// A genuinely absent member still fails, so "selects an overload" has not
    /// become "accepts anything".
    /// </summary>
    [Fact]
    public async Task A_missing_generic_static_is_still_reported()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new ToshEngine(ToshRuntime.CreateDefault())
                .ExecuteToListAsync("System.Collections.Immutable.ImmutableArray.Nope<int>()"));

        Assert.Contains("Nope", exception.Message);
    }

    /// <summary>
    /// And an arity no overload provides is still refused rather than silently
    /// matched to the nearest one.
    /// </summary>
    [Fact]
    public async Task An_unsupported_arity_is_still_refused()
    {
        await Assert.ThrowsAnyAsync<Exception>(
            () => new ToshEngine(ToshRuntime.CreateDefault())
                .ExecuteToListAsync("Array.Empty<int>(1, 2, 3)"));
    }
}
