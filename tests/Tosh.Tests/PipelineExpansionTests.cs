using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Piping a variable and piping the identical literal agree.
///
/// `TS-P2-113`. A collection reaching a pipeline is expanded element-wise, and
/// that rule is right — it is what makes `$items | where { … }` read the way it
/// does. It was being applied twice to the same value: a variable used as a stage
/// is enumerated *at the stage*, and then the lone-collection rule downstream
/// expanded the result again.
///
/// So with `r = [[1, 2, 3]]`, `$r | count` answered 3 where `[[1, 2, 3]] | count`
/// answered 1, `$r | first` came back an `Int32` rather than an `Int32[]`, and
/// `for x in $r` bound three integers instead of one array. The value itself was
/// never wrong — `$r.Length` was 1 throughout.
///
/// The producer now marks the stream it has already enumerated. Removing the
/// stage-level enumeration instead would have been simpler and wrong: `where`,
/// `sort`, `to` and `join` never call the replay helper and rely on receiving the
/// elements, so it would have fixed the count and broken the filter.
/// </summary>
public class PipelineExpansionTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(v => v?.ToString() ?? "null"));
    }

    /// <summary>
    /// The heart of it: every shape, piped from a variable and from the identical
    /// literal, must agree. Asserting the variable alone would not have caught the
    /// original defect, because the *literal* was the side that was right.
    /// </summary>
    [Theory]
    [InlineData("[1, 2, 3]", "3")]      // depth 0 — the common case
    [InlineData("[[1, 2, 3]]", "1")]    // depth 1, single element — the reported defect
    [InlineData("[[[1, 2]]]", "1")]     // depth 2
    [InlineData("[[1], [2]]", "2")]     // two elements never triggered it
    [InlineData("[]", "0")]             // empty
    [InlineData("[[]]", "1")]           // the shape the item was filed under
    public async Task A_variable_and_a_literal_count_the_same(string literal, string expected)
    {
        Assert.Equal(expected, await RunAsync($"var v = {literal}\n$v | count"));
        Assert.Equal(expected, await RunAsync($"{literal} | count"));
    }

    /// <summary>
    /// `first` is asserted by *type*, because the counts can agree while the
    /// element is one level too deep — which is exactly what happened.
    /// </summary>
    [Fact]
    public async Task The_first_element_has_the_same_type_either_way()
    {
        Assert.Equal("Int32[]", await RunAsync("var v = [[1, 2, 3]]\n($v | first).GetType().Name"));
        Assert.Equal("Int32[]", await RunAsync("([[1, 2, 3]] | first).GetType().Name"));
    }

    /// <summary>`for` reaches the same values through different machinery.</summary>
    [Theory]
    [InlineData("[[1, 2, 3]]", "1")]
    [InlineData("[1, 2, 3]", "3")]
    public async Task A_for_loop_agrees_with_the_literal(string literal, string expected)
    {
        const string body = "\nvar n = 0\nfor x in {0} {{ $n = ($n + 1) }}\n$n";

        Assert.Equal(expected, await RunAsync($"var v = {literal}" + string.Format(body, "$v")));
        Assert.Equal(expected, await RunAsync(string.Format(body, literal).TrimStart()));
    }

    /// <summary>
    /// The non-regression that constrains the whole design. Commands that do not
    /// use the replay helper need the elements, so the stage-level enumeration has
    /// to stay — a fix that removed it would satisfy every count above and break
    /// these.
    /// </summary>
    [Theory]
    [InlineData("$v | where { $_ > 1 } | count", "2")]
    [InlineData("$v | sort | first", "1")]
    [InlineData("$v | map { $_ * 2 } | count", "3")]
    [InlineData("$v | join \"-\"", "3-1-2")]
    public async Task An_ordinary_array_still_streams_element_wise(string body, string expected)
        => Assert.Equal(expected, await RunAsync("var v = [3, 1, 2]\n" + body));

    /// <summary>
    /// And the value itself was never in question — this is what made the defect
    /// confusing to read, since `.Length` disagreed with `| count`.
    /// </summary>
    [Fact]
    public async Task The_variables_own_length_is_unchanged()
        => Assert.Equal("1", await RunAsync("var v = [[1, 2, 3]]\n$v.Length"));
}
