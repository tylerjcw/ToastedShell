using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Conversion never substitutes a placeholder for a value — <c>TS-P1-43</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ShellDataSerializer.Normalize</c> returned the string <c>"&lt;max-depth&gt;"</c> past
/// eight levels and carried on, so <c>to json</c> reported success while quietly
/// replacing real values with a placeholder. Found porting the VS Code grammar
/// generator: the grammar nests nine deep at
/// <c>repository → rule → captures → "2" → patterns → item → match</c>, and sixteen
/// rules came out with both <c>match</c> and <c>name</c> set to the literal
/// <c>"&lt;max-depth&gt;"</c>. Valid JSON, wrong content, no diagnostic — the failure mode
/// that costs the most to find.
/// </para>
/// <para>
/// The limit is now 64 and exceeding it raises
/// <c>tosh.runtime.serialization_depth_exceeded</c>. Cycles are caught separately by the
/// <c>visited</c> set, so the depth guard only bounds genuinely deep acyclic graphs —
/// reflecting over a CLR tree such as an <c>XDocument</c>, which is <c>TS-P2-71</c>.
/// </para>
/// </remarks>
public sealed class SerializationDepthTests
{
    private static async Task<string> RunAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var results = await engine.ExecuteToListAsync(script);
        return string.Join("\n", results.Select(v => v?.ToString() ?? string.Empty));
    }

    /// <summary>Builds `{| a = {| a = ... |} |}` nested <paramref name="depth"/> deep.</summary>
    private static string NestedRecord(int depth)
    {
        var value = "1";
        for (var i = 0; i < depth; i++)
        {
            value = "{| a = " + value + " |}";
        }
        return value;
    }

    [Fact]
    public async Task A_structure_deeper_than_eight_is_not_replaced_by_a_placeholder()
    {
        // Twelve deep: comfortably past the old limit of 8 and far short of the new 64.
        var json = await RunAsync($"var v = {NestedRecord(12)}\n$v | to json");

        Assert.DoesNotContain("max-depth", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"a\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_deep_value_survives_a_round_trip()
    {
        // The guard that keeps "no placeholder" from meaning "silently truncated".
        var json = await RunAsync($"var v = {NestedRecord(12)}\n$v | to json --compact");

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var node = document.RootElement;

        for (var i = 0; i < 12; i++)
        {
            node = node.GetProperty("a");
        }

        Assert.Equal(1, node.GetInt32());
    }

    [Fact]
    public async Task The_grammar_shape_that_found_this_serializes_intact()
    {
        // The real case, reduced: the nesting depth of a TextMate rule.
        var json = await RunAsync(
            """
            var g = {% "repository" => {% "r" => {% "captures" => {% "2" => {% "patterns" => [{% "match" => "\\b(x)\\b", "name" => "support.type.tosh" %}] %} %} %} %} %}
            $g | to json --compact
            """);

        Assert.Contains("support.type.tosh", json, StringComparison.Ordinal);
        Assert.DoesNotContain("max-depth", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Past_the_limit_it_fails_loudly_rather_than_quietly()
    {
        // The half that matters: refusing is fine, lying is not.
        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync($"var v = {NestedRecord(70)}\n$v | to json"));

        Assert.Contains(
            error.Diagnostics,
            diagnostic => diagnostic.Code == "tosh.runtime.serialization_depth_exceeded");
    }

    [Fact]
    public async Task A_shallow_value_is_unchanged()
    {
        var json = await RunAsync("""{| a = 1, b = [1, 2], c = {| d = "x" |} |} | to json --compact""");

        Assert.Equal("""{"a":1,"b":[1,2],"c":{"d":"x"}}""", json.Trim());
    }

    [Fact]
    public async Task A_cycle_is_still_caught_without_relying_on_the_depth_guard()
    {
        // Raising the limit must not turn a cycle into a stack overflow: cycles are the
        // `visited` set's job, and that is what makes a high depth limit safe.
        var json = await RunAsync(
            """
            var a = {| name = "a", next = null |}
            var b = {| name = "b", next = $a |}
            $a.next = $b
            $a | to json --compact
            """);

        Assert.Contains("\"a\"", json, StringComparison.Ordinal);
        Assert.Contains("\"b\"", json, StringComparison.Ordinal);
    }
}
