using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A keyword survives a CLR type whose name differs only in case — <c>TS-P2-32</c>.
/// </summary>
/// <remarks>
/// <para>
/// Typing <c>match</c> offered <c>Match</c>, <c>MatchCasing</c>, <c>MatchCollection</c>,
/// <c>MatchEvaluator</c>, <c>MatchType</c> and the executable <c>match_parens</c> — everything
/// except the keyword. Filed as a ranking or de-duplication question; measured, it was neither
/// exactly. The dictionary the sources accumulate into was keyed <c>OrdinalIgnoreCase</c>, and
/// the CLR pass runs after the keyword pass, so <c>Match</c> *overwrote* <c>match</c> and the
/// keyword was gone before ranking ever saw it.
/// </para>
/// <para>
/// <c>OrderSuggestions</c> already de-duplicated ordinally, with its own note that <c>icmp</c>
/// and <c>Icmp</c> are different members and both belong in the list. The two passes now agree.
/// </para>
/// </remarks>
public sealed class CompletionCaseCollisionTests
{
    private static IReadOnlyList<string> Complete(string prefix)
    {
        var engine = new Tosh.Cli.ReplCompletionEngine(ToshRuntime.CreateDefault());
        var result = engine.GetCompletions(prefix, prefix.Length);

        return result is null
            ? Array.Empty<string>()
            : result.Suggestions.Select(suggestion => suggestion.Label).ToArray();
    }

    [Theory]
    [InlineData("match", "match")]
    [InlineData("matc", "match")]
    [InlineData("rune", "rune")]
    [InlineData("run", "rune")]
    public void A_keyword_is_offered_from_its_own_prefix(string prefix, string keyword)
    {
        Assert.Contains(keyword, Complete(prefix));
    }

    [Theory]
    [InlineData("match", "Match")]
    [InlineData("rune", "Rune")]
    public void The_colliding_clr_type_is_still_offered_too(string prefix, string type)
    {
        // Both belong in the list — the fix is that they are two entries, not that one wins.
        Assert.Contains(type, Complete(prefix));
    }

    [Theory]
    [InlineData("match")]
    [InlineData("rune")]
    public void The_keyword_outranks_the_type_it_collides_with(string prefix)
    {
        var engine = new Tosh.Cli.ReplCompletionEngine(ToshRuntime.CreateDefault());
        var suggestions = engine.GetCompletions(prefix, prefix.Length)!.Suggestions;

        var keyword = suggestions.First(s => string.Equals(s.Label, prefix, StringComparison.Ordinal));
        var clrType = suggestions.First(s =>
            string.Equals(s.Label, prefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(s.Label, prefix, StringComparison.Ordinal));

        Assert.True(
            keyword.Priority < clrType.Priority,
            $"'{keyword.Label}' ({keyword.Priority}) should rank ahead of '{clrType.Label}' ({clrType.Priority})");
    }

    [Fact]
    public void A_prefix_with_no_collision_is_unaffected()
    {
        // `defer` collides with nothing and was already offered; it is here so a regression in
        // the keyword source is not mistaken for this fix failing.
        Assert.Contains("defer", Complete("defe"));
    }

    [Fact]
    public void Case_distinct_labels_are_not_collapsed()
    {
        // The general form of the same rule: two labels differing only in case are two entries.
        var labels = Complete("match");

        Assert.Contains("match", labels);
        Assert.Contains("Match", labels);
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
    }
}
