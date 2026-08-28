using Tosh.Language;

namespace Tosh.Tests;

/// <summary>
/// List patterns in a match arm — <c>TOAST-0053</c>.
/// </summary>
/// <remarks>
/// <para>
/// The rest is spelled <c>...</c>, the language's existing spread, not the <c>..</c> the item
/// first sketched: <c>..</c> is the range operator, so <c>[a, ..b]</c> would have needed
/// lookahead to tell from a range and would read as one to anybody who knows the rest of the
/// language.
/// </para>
/// <para>
/// A rest binds an array rather than a list, because <c>[1, 2, 3]</c> is an array — binding a
/// <c>List</c> would answer <c>.Count</c> where the literal it came from answers
/// <c>.Length</c>, so the same value would need a different spelling depending on where it
/// came from.
/// </para>
/// </remarks>
public sealed class ListPatternTests
{
    private static async Task<IReadOnlyList<object?>> RunAsync(string body)
    {
        var engine = ShellEngine.CreateFullShell();
        return await engine.ExecuteToListAsync(body);
    }

    [Fact]
    public async Task A_list_pattern_binds_a_head_and_a_rest()
    {
        var results = await RunAsync("""
            echo (match ([1, 2, 3]) {
                [first, ...rest] => $first
                default => -1
            })
            echo (match ([1, 2, 3]) {
                [first, ...rest] => $rest.Length
                default => -1
            })
            """);

        Assert.Equal("1", results[^2]?.ToString());
        Assert.Equal("2", results[^1]?.ToString());
    }

    /// <summary>
    /// A rest may absorb nothing, and still binds — an arm can pass it on without a null check.
    /// </summary>
    [Fact]
    public async Task A_rest_may_be_empty()
    {
        var results = await RunAsync("""
            echo (match ([1]) {
                [first, ...rest] => $rest.Length
                default => -1
            })
            """);

        Assert.Equal("0", results[^1]?.ToString());
    }

    /// <summary>
    /// Without a rest the length is exact, which is what makes the form useful for dispatch.
    /// </summary>
    [Fact]
    public async Task A_fixed_list_pattern_matches_only_that_length()
    {
        var results = await RunAsync("""
            echo (match ([1, 2]) {
                [a, b] => $a + $b
                default => -1
            })
            echo (match ([1, 2, 3]) {
                [a, b] => $a + $b
                default => -1
            })
            echo (match ([]) {
                [] => "empty"
                default => "no"
            })
            """);

        Assert.Equal("3", results[^3]?.ToString());
        Assert.Equal("-1", results[^2]?.ToString());
        Assert.Equal("empty", results[^1]?.ToString());
    }

    /// <summary>
    /// The rest may sit in the middle, so a pattern can name both ends of a sequence.
    /// </summary>
    [Fact]
    public async Task A_rest_may_sit_between_fixed_elements()
    {
        var results = await RunAsync("""
            echo (match ([1, 2, 3, 4]) {
                [a, ...mid, d] => $d
                default => -1
            })
            echo (match ([1, 2, 3, 4]) {
                [a, ...mid, d] => $mid.Length
                default => -1
            })
            """);

        Assert.Equal("4", results[^2]?.ToString());
        Assert.Equal("2", results[^1]?.ToString());
    }

    /// <summary>
    /// Two rests would leave no unambiguous split, and are refused rather than ignored.
    /// </summary>
    [Fact]
    public async Task A_second_rest_is_refused()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync("""
            echo (match ([1, 2, 3]) {
                [a, ...x, b, ...y] => 1
                default => -1
            })
            """));

        Assert.Contains("one", error.Message);
    }

    /// <summary>
    /// An element is a pattern, so it may test rather than bind — the same rule as everywhere
    /// else in the grammar.
    /// </summary>
    [Fact]
    public async Task An_element_may_be_a_literal_that_tests()
    {
        var results = await RunAsync("""
            echo (match ([1, 9]) {
                [1, x] => $x
                default => -1
            })
            echo (match ([2, 9]) {
                [1, x] => $x
                default => -1
            })
            """);

        Assert.Equal("9", results[^2]?.ToString());
        Assert.Equal("-1", results[^1]?.ToString());
    }

    [Fact]
    public async Task An_element_may_be_a_variable_that_compares()
    {
        var results = await RunAsync("""
            var want = 1
            echo (match ([1, 2]) {
                [$want, b] => $b
                default => -1
            })
            var other = 9
            echo (match ([1, 2]) {
                [$other, b] => $b
                default => -1
            })
            """);

        Assert.Equal("2", results[^2]?.ToString());
        Assert.Equal("-1", results[^1]?.ToString());
    }

    [Fact]
    public async Task Underscore_discards_an_element()
    {
        var results = await RunAsync("""
            echo (match ([1, 2, 3]) {
                [_, second, _] => $second
                default => -1
            })
            """);

        Assert.Equal("2", results[^1]?.ToString());
    }

    /// <summary>
    /// An anonymous rest skips the middle without naming it.
    /// </summary>
    [Fact]
    public async Task A_rest_need_not_be_named()
    {
        var results = await RunAsync("""
            echo (match ([1, 2, 3]) {
                [first, ...] => $first
                default => -1
            })
            """);

        Assert.Equal("1", results[^1]?.ToString());
    }

    /// <summary>
    /// A string is an <c>IEnumerable&lt;char&gt;</c> to .NET and is not a list here.
    /// </summary>
    /// <remarks>
    /// Without the check, <c>[a, b]</c> would match "hi" and bind two characters — a string
    /// silently taking an arm written for a list.
    /// </remarks>
    [Fact]
    public async Task A_string_is_not_a_list()
    {
        var results = await RunAsync("""
            echo (match ("hi") {
                [a, b] => "chars"
                default => "not a list"
            })
            """);

        Assert.Equal("not a list", results[^1]?.ToString());
    }

    [Fact]
    public async Task A_guard_sees_what_a_list_pattern_bound()
    {
        var results = await RunAsync("""
            echo (match ([1, 5]) {
                [a, b] if (($b > 3)) => $b
                default => -1
            })
            """);

        Assert.Equal("5", results[^1]?.ToString());
    }

    /// <summary>
    /// List and variant patterns nest into each other, in both directions.
    /// </summary>
    [Fact]
    public async Task List_and_variant_patterns_nest()
    {
        var results = await RunAsync("""
            union E {
                Lit(v: int)
            }
            union W {
                Many(items: list)
            }
            echo (match ([E.Lit(4), E.Lit(5)]) {
                [Lit(a), Lit(b)] => $a * $b
                default => -1
            })
            echo (match (W.Many([1, 2, 3])) {
                Many([f, ...r]) => $f
                default => -1
            })
            """);

        Assert.Equal("20", results[^2]?.ToString());
        Assert.Equal("1", results[^1]?.ToString());
    }

    /// <summary>
    /// A rest can be matched again, which is what makes walking a sequence possible.
    /// </summary>
    [Fact]
    public async Task A_rest_can_be_matched_again()
    {
        var results = await RunAsync("""
            echo (match ([1, 2, 3]) {
                [f, ...rest] => (match ($rest) {
                    [a, b] => $a + $b
                    default => -1
                })
                default => -1
            })
            """);

        Assert.Equal("5", results[^1]?.ToString());
    }
}
