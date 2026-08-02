using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>to &lt;format&gt;</c> never answers <c>null</c> for missing input — <c>TS-P1-38</c>.
/// </summary>
/// <remarks>
/// <para>
/// `to json {| a = 1 |}` printed the literal text `null`. The record sat in the arguments while
/// the command read an empty pipeline and emitted `null` for it — output that reads exactly like a
/// successful serialization of a null value. `to csv` and `to xml` did the same. A converter that
/// answers `null` when handed data is indistinguishable from one that serialized a null, which is
/// why silence was the one option ruled out when this was filed.
/// </para>
/// <para>
/// The empty branch is only reachable when nothing was piped at all: an empty *collection* is one
/// value and serializes properly, so `[] | to json` gives `[]` rather than falling into it. That
/// is what makes it safe to treat the branch as a mistake in every case.
/// </para>
/// </remarks>
public sealed class ToCommandInputTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join("\n", results.Select(result => result?.ToString() ?? "null"));
    }

    [Theory]
    // The reported spelling, across the formats that shared the defect.
    [InlineData("to json {| a = 1 |}", "\"a\": 1")]
    [InlineData("to csv {| a = 1, b = 2 |}", "a,b")]
    [InlineData("to xml {| a = 1 |}", "<a>1</a>")]
    public async Task An_argument_is_serialized_rather_than_answered_with_null(
        string source,
        string expected)
    {
        var output = await RunAsync(source);

        Assert.Contains(expected, output, StringComparison.Ordinal);
        Assert.DoesNotContain("null", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_piped_form_is_unchanged()
    {
        // The spelling that always worked. Whatever the argument form does, it must not come at
        // the cost of the one people already use.
        Assert.Contains("\"a\": 1", await RunAsync("{| a = 1 |} | to json"), StringComparison.Ordinal);
    }

    [Theory]
    // An empty collection is a value, not missing input, and serializes as itself.
    [InlineData("[] | to json", "[]")]
    [InlineData("{% %} | to json", "{}")]
    public async Task An_empty_collection_still_serializes(string source, string expected)
    {
        Assert.Equal(expected, (await RunAsync(source)).Trim());
    }

    [Fact]
    public async Task Nothing_to_serialize_is_a_diagnostic_not_null()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync("to json"));

        Assert.Contains("nothing to serialize", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_flag_without_data_is_a_diagnostic_rather_than_a_guess()
    {
        // Arguments are only taken as data when none of them looks like a flag. Formats read
        // their options positionally after a switch — `to csv -d ","` — so a mixed list would
        // require knowing which flags take values, and guessing wrong would serialize a
        // delimiter instead of the user's data. The diagnostic costs one edit; a wrong guess
        // costs silent bad output.
        var error = await Assert.ThrowsAnyAsync<Exception>(
            async () => await RunAsync("to json --compact"));

        Assert.Contains("nothing to serialize", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_flagged_option_still_reaches_the_format_when_input_is_piped()
    {
        // The argument path must not have disturbed option handling on the normal route.
        //
        // Two fields, because a delimiter only appears *between* them: a first draft asserted
        // against a single-field record, where no delimiter could ever show up, and failed
        // against perfectly correct behaviour.
        var output = await RunAsync("{| a = 1, b = 2 |} | to csv -d \";\"");

        Assert.Contains(";", output, StringComparison.Ordinal);
    }
}
