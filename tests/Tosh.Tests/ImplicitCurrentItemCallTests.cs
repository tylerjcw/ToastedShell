using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What a bare call means inside a closure — `TOAST-0001`.
///
/// Inside a predicate a bare name is implicit member access, so `where { double($_) }`
/// parses as `$_.double($_)` and reported "No overload matched instance method 'double'
/// on 'System.Int32'" — an error about a construct the reader had not written. The same
/// call one line earlier worked.
///
/// The rule these pin is **member, then extension, then function**. It is one order, not
/// a third one: an `extend` method already resolves only where the receiver has no such
/// member (`TS-P3-27`), and a free function is one step further out again. Only the
/// receiver the *parser* synthesized may fall back — an explicitly written `$_.f()` is a
/// member access the reader asked for.
/// </summary>
public sealed class ImplicitCurrentItemCallTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private const string Double = """
        func double(n) { return ($n * 2) }

        """;

    /// <summary>
    /// The item's own repro: the call that worked as a statement, inside a predicate.
    /// </summary>
    [Fact]
    public async Task A_free_function_is_reached_from_a_where_predicate()
        => Assert.Equal("2", await RunAsync(Double + "[1, 2, 3] | where { double($_) > 2 } | count"));

    /// <summary>
    /// The other stages that read their body as a current-item expression.
    /// </summary>
    /// <remarks>
    /// Not "every closure-taking stage", which is what the item assumed. Implicit member
    /// access belongs to a specific set — `IsPredicateExpressionCommand`'s thirteen names
    /// in the brace form, and `map`/`sort-by`/`group-by` and friends in the parenthesized
    /// form — and only those ever synthesized a receiver, so only those could have had the
    /// bug. `map { f($_) }` with braces is a command pipeline and always worked; the
    /// control below pins that, and a theory written that way would pass with the fix
    /// reverted.
    /// </remarks>
    [Theory]
    [InlineData("[1, 2, 3] | all { double($_) > 0 }", "True")]
    [InlineData("[1, 2, 3] | any { double($_) > 4 }", "True")]
    [InlineData("[1, 2, 3] | take-while { double($_) < 5 } | join \",\"", "1,2")]
    [InlineData("[1, 2, 3] | map (double($_) + 0) | join \",\"", "2,4,6")]
    [InlineData("[3, 1, 2] | sort-by (double($_) + 0) | join \",\"", "1,2,3")]
    public async Task A_free_function_is_reached_from_every_current_item_expression(string body, string expected)
        => Assert.Equal(expected, await RunAsync(Double + body));

    /// <summary>
    /// A brace body that is a command pipeline, which was never broken — the name is in
    /// command position and reaches the function through ordinary dispatch.
    /// </summary>
    /// <remarks>
    /// Here to keep the boundary visible. Two spellings that look alike take different
    /// routes to the same name, and only one of them failed; a reader who does not know
    /// that will assume the fix is what makes these work.
    /// </remarks>
    [Theory]
    [InlineData("[1, 2, 3] | map { double($_) } | join \",\"", "2,4,6")]
    [InlineData("[3, 1, 2] | sort-by { double($_) } | join \",\"", "1,2,3")]
    public async Task A_call_in_command_position_reaches_the_function_as_it_always_did(string body, string expected)
        => Assert.Equal(expected, await RunAsync(Double + body));

    /// <summary>
    /// A real member on the item still wins, which is the half a naive fix would break.
    /// `ToUpper` exists on `string`, so the free function of the same name must not be
    /// reached — otherwise adding a function would silently change what existing scripts
    /// mean.
    /// </summary>
    [Fact]
    public async Task A_real_member_on_the_item_still_wins()
        => Assert.Equal("1", await RunAsync(
            """
            func ToUpper(s) { return "FROM-FUNCTION" }
            ["ab"] | where { ToUpper() == "AB" } | count
            """));

    /// <summary>
    /// And an extension wins over a function too, so the three readings form one order
    /// rather than two rules that disagree.
    /// </summary>
    [Fact]
    public async Task An_extension_wins_over_a_free_function()
        => Assert.Equal("1", await RunAsync(
            """
            extend Int32 { func tag(k) -> string => "from-extension" }
            func tag(n) { return "from-function" }
            [1] | where { tag(9) == "from-extension" } | count
            """));

    /// <summary>
    /// The function is still reached when no extension supplies the name — the control
    /// for the test above, which would also pass if the fallback had simply stopped
    /// working.
    /// </summary>
    [Fact]
    public async Task The_function_is_reached_when_no_extension_supplies_the_name()
        => Assert.Equal("1", await RunAsync(
            """
            func tag(n) { return "from-function" }
            [1] | where { tag(9) == "from-function" } | count
            """));

    /// <summary>
    /// A receiver that is a ToastScript class instance answers for its own members, so
    /// both readings work against one: its method, and a free function taking it.
    /// </summary>
    [Fact]
    public async Task A_class_instance_receiver_answers_for_its_own_members()
        => Assert.Equal("2", await RunAsync(
            """
            class Item { func Own() -> string => "member" }
            func Free(i) { return "function" }
            var xs = [new Item(), new Item()]
            ($xs | where { Own() == "member" } | count)
            ($xs | where { Free($_) == "function" } | count)
            """));

    /// <summary>
    /// An **explicitly written** receiver does not fall back. The reader asked for a
    /// member of the item; answering with a function of the same name would make
    /// `$_.f()` and `f()` mean the same thing, and then there would be no way to say
    /// "the member" at all.
    /// </summary>
    [Fact]
    public async Task An_explicit_receiver_does_not_fall_back_to_a_function()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteToListAsync(
            """
            func Free(n) { return "function" }
            [1] | where { $_.Free(1) == "function" } | count
            """));

        Assert.Contains("Free", exception.Message);
    }

    /// <summary>
    /// A name that is neither reports both readings. At that point it is known not to be
    /// a member, not to be an extension and not to be in scope — and a reader who meant
    /// either one is helped only by being told about the other.
    /// </summary>
    [Fact]
    public async Task A_name_that_is_neither_reports_both_readings()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync("[1] | where { nosuch() == 1 }"));

        Assert.Contains("nosuch", exception.Message);
        Assert.Contains("in scope", exception.Message);
    }
}
