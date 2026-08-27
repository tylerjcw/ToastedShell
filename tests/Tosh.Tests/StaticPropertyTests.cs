using Tosh.Cli;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Computed <c>static</c>/<c>shared</c> properties — <c>TS-P1-28</c> — and the
/// completion collision that hid them — <c>TS-P2-40</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reported from a real library: a <c>hermit class</c> whose static functions worked
/// while its static properties "do not work, at all" and "do not even show up in
/// autocomplete". Two independent defects wearing one symptom.
/// </para>
/// <para>
/// The value half: static properties were only ever *initialized*, never *evaluated*.
/// Both initialization sites read <c>IsStatic &amp;&amp; Initializer is not null &amp;&amp;
/// !IsComputed</c>, so a computed one never entered <c>_staticValues</c> and
/// <c>TryGetStaticMember</c> fell through to a line commented <c>// null default</c>.
/// <c>static prop Y =&gt; 7</c> answered <c>null</c> with no diagnostic, and so did an
/// accessor-block form. Stored static properties worked throughout, which is what made
/// the report look like "properties don't work" rather than "computed ones don't".
/// </para>
/// <para>
/// The autocomplete half had nothing to do with static-ness. The reporter's class held
/// <c>func icmp()</c> beside <c>prop Icmp</c>, and completion de-duplicated labels
/// case-insensitively — in the suggestion dictionaries and again in
/// <c>OrderSuggestions</c> — so one of the pair vanished. Which one depended on
/// enumeration order, because <c>DistinctBy</c> ran before <c>OrderBy</c>.
/// </para>
/// </remarks>
public sealed class StaticPropertyTests
{
    private static async Task<object?> EvaluateAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? null : results[^1];
    }

    [Theory]
    // Every spelling of a computed static property, in a hermit class and a plain one.
    [InlineData("hermit class C { static prop Y => 7 }")]
    [InlineData("hermit class C { shared prop Y => 7 }")]
    [InlineData("class C { static prop Y => 7 }")]
    [InlineData("class C { shared prop Y => 7 }")]
    [InlineData("hermit class C {\n    static prop Y {\n        get { return 7 }\n    }\n}")]
    public async Task A_computed_static_property_evaluates_its_getter(string declaration)
    {
        // Before the fix every one of these answered null, silently.
        Assert.Equal(7, await EvaluateAsync($"{declaration}\nC.Y"));
    }

    [Fact]
    public async Task A_stored_static_property_still_works()
    {
        // The half that always worked, pinned so evaluating computed ones cannot have
        // disturbed the stored path.
        Assert.Equal(42, await EvaluateAsync("hermit class C { static prop X = 42 }\nC.X"));
    }

    [Fact]
    public async Task A_computed_static_property_may_call_a_qualified_sibling()
    {
        // The reporter's shape, written the way the language requires: members are
        // reached through `ClassName.` or `$this.`, never bare. Bare `f()` fails from an
        // instance method too, so that is the rule rather than a defect — see TS-P2-41
        // for the diagnostic, which suggests shell commands instead of the member.
        Assert.Equal(7, await EvaluateAsync(
            """
            hermit class C {
                static func f() -> int { return 7 }
                static prop Y => C.f()
            }
            C.Y
            """));
    }

    [Fact]
    public async Task A_computed_static_property_is_evaluated_on_each_read()
    {
        // It is a getter, not a cached initializer, so a second read re-runs it. Asserted
        // because the stored path this shared code with does cache, and conflating them
        // would make a stale value look correct.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            """
            var counter = 0
            hermit class C { static prop Next => 1 }
            C.Next
            C.Next
            """);

        Assert.Equal(2, results.Count);
        Assert.All(results, value => Assert.Equal(1, value));
    }

    [Fact]
    public async Task An_instance_computed_property_is_unaffected()
    {
        Assert.Equal(7, await EvaluateAsync("class C { prop Y => 7 }\n(new C()).Y"));
    }

    [Fact]
    public async Task Completion_offers_members_differing_only_in_case()
    {
        // The autocomplete half. `icmp` and `Icmp` are different members and both belong
        // in the list; case-insensitive de-duplication dropped one, and which one
        // depended on enumeration order.
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync(
            """
            hermit class State {
                shared func icmp() -> bool { return true }
                shared prop Icmp => true
                shared prop Other = 1
            }
            """);

        var result = new ReplCompletionEngine(runtime).GetCompletions("State.", "State.".Length);

        Assert.NotNull(result);
        var labels = result!.Suggestions.Select(suggestion => suggestion.Label).ToArray();

        Assert.Contains("icmp", labels);
        Assert.Contains("Icmp", labels);
        Assert.Contains("Other", labels);
    }

    [Fact]
    public async Task Completion_offers_a_computed_static_property()
    {
        // The reporter's other observation: the property did not appear at all. Kept
        // separate from the case-collision test so a regression names which cause.
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync("hermit class C { shared prop Computed => 1 }");

        var result = new ReplCompletionEngine(runtime).GetCompletions("C.", "C.".Length);

        Assert.NotNull(result);
        Assert.Contains("Computed", result!.Suggestions.Select(suggestion => suggestion.Label));
    }

    [Fact]
    public async Task Completion_still_collapses_an_exact_duplicate()
    {
        // Making de-duplication case-sensitive must not have stopped it de-duplicating
        // the case that matters: the same spelling arriving twice.
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync("hermit class C { shared prop Only = 1 }");

        var result = new ReplCompletionEngine(runtime).GetCompletions("C.", "C.".Length);

        Assert.NotNull(result);
        Assert.Single(result!.Suggestions.Where(
            suggestion => string.Equals(suggestion.Label, "Only", StringComparison.Ordinal)));
    }
}
