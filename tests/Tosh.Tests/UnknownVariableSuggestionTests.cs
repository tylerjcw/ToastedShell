using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// An unknown variable that is a namespace member elsewhere says where to look —
/// <c>TS-P2-44</c>.
/// </summary>
/// <remarks>
/// <para>
/// Script arguments live at <c>$tosh.Script.Args</c>. Anyone arriving from bash, Python or
/// PowerShell writes <c>$args</c> or <c>$argv</c> first and got "declare it first with
/// 'var args = ...'" — advice that points away from the answer and, if followed, produces an
/// empty local rather than the arguments. There was no path to the real spelling short of piping
/// <c>$tosh.Script</c> through <c>members</c>.
/// </para>
/// <para>
/// Found while switching this programme's own scripting from Python to ToastScript, which is the
/// argument for having done so: the gap is invisible to anyone who already knows the answer, and
/// no unit test would have produced it.
/// </para>
/// </remarks>
public sealed class UnknownVariableSuggestionTests
{
    /// <summary>
    /// The diagnostic's help text — which is where the suggestion lives.
    /// </summary>
    /// <remarks>
    /// Not <c>Exception.Message</c>: that carries only the title ("Variable 'argv' was not
    /// found."), while the help is a separate field the CLI renders on its own line. A first
    /// draft asserted against the message and failed everywhere, which looked like the feature
    /// was broken when it was the test reading the wrong field.
    /// </remarks>
    private static async Task<string> HelpFor(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var error = await Assert.ThrowsAnyAsync<ToshDiagnosticException>(
            async () => await engine.ExecuteToListAsync(source));

        return string.Join("\n", error.Diagnostics.Select(diagnostic => diagnostic.Help ?? string.Empty));
    }

    [Theory]
    [InlineData("args", "$tosh.Script.Args")]
    [InlineData("argv", "$tosh.Script.Args")]
    [InlineData("ARGV", "$tosh.Script.Args")]
    [InlineData("scriptname", "$tosh.Script.Name")]
    [InlineData("scriptdir", "$tosh.Script.Directory")]
    [InlineData("PWD", "$env.PWD")]
    [InlineData("HOME", "$env.HOME")]
    [InlineData("PATH", "$env.PATH")]
    [InlineData("status", "$tosh.Last.ExitCode")]
    public async Task A_habitual_spelling_is_pointed_at_the_real_one(string spelling, string suggestion)
    {
        Assert.Contains(suggestion, await HelpFor($"echo ${spelling}"), StringComparison.Ordinal);
    }

    [Theory]
    // Every suggestion must actually resolve. A diagnostic that sends the reader somewhere that
    // does not exist is worse than the generic advice it replaced, and this table is exactly the
    // sort of thing that rots when a namespace member is renamed.
    [InlineData("$tosh.Script.Args")]
    [InlineData("$tosh.Script.Name")]
    [InlineData("$tosh.Script.Directory")]
    [InlineData("$env.PWD")]
    [InlineData("$env.HOME")]
    [InlineData("$env.PATH")]
    [InlineData("$tosh.Last.ExitCode")]
    public async Task Every_suggested_spelling_resolves(string expression)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        // Throwing would mean the suggestion names something that is not there.
        await engine.ExecuteToListAsync($"echo ({expression})");
    }

    [Fact]
    public async Task An_ordinary_unknown_variable_keeps_the_generic_advice()
    {
        // The table is for habits borrowed from other shells, not a replacement for the normal
        // message — a genuine typo should still be told to declare the variable.
        var message = await HelpFor("echo $nosuchthing");

        Assert.Contains("declare it first", message, StringComparison.Ordinal);
        Assert.DoesNotContain("did you mean", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_suggestion_is_case_insensitive_like_the_lookup()
    {
        // `$Args` and `$args` are the same habit; matching only one spelling would leave the
        // other with advice that points away from the answer.
        Assert.Contains(
            "$tosh.Script.Args",
            await HelpFor("echo $Args"),
            StringComparison.Ordinal);
    }
}