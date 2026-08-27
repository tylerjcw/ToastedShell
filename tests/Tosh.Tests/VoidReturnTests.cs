using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// `-> void` and `-> nothing` are one type and mean "produces nothing" — `TOAST-0046`.
/// </summary>
/// <remarks>
/// <para>
/// They were the same bound type already — `TypeNameResolver` maps both to
/// `BoundType.Void` — and they failed differently, which is how the disagreement was
/// visible at all. `-> void` tried to convert the produced value to the CLR's
/// `System.Void`, reporting *"could not be converted to 'void'"*; `-> nothing` was not a
/// type the *runtime* resolver had ever heard of, reporting `annotation_unknown_type`.
/// Neither worked, and the specification described neither.
/// </para>
/// <para>
/// The rule is C#'s, in a language where a function's output *is* its value: a void
/// function may not say what it evaluates to. `echo` emits a pipeline value and is
/// therefore this language's `return expr;`, while `writeline` writes to the console and
/// yields nothing — so a void function still prints, it just does not produce.
/// </para>
/// </remarks>
public sealed class VoidReturnTests
{
    private static async Task<(string Output, IReadOnlyList<string> Warnings)> RunAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        var results = await engine.ExecuteToListAsync(source);
        return (
            results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null",
            Array.Empty<string>());
    }

    private static async Task<string?> DiagnosticCodeAsync(string source)
    {
        try
        {
            await RunAsync(source);
            return null;
        }
        catch (ToshDiagnosticException diagnostic)
        {
            return diagnostic.Diagnostics[0].Code;
        }
    }

    /// <summary>A void function may print, because printing is not producing.</summary>
    [Theory]
    [InlineData("void")]
    [InlineData("nothing")]
    public async Task A_void_function_may_write_to_the_console(string spelling)
    {
        var (output, _) = await RunAsync(
            $"func f() -> {spelling} {{ writeline \"hi\" }}\nf\necho \"after\"");

        Assert.Equal("after", output);
    }

    /// <summary>
    /// Producing a value from a void function is refused, whichever spelling is used.
    /// </summary>
    /// <remarks>
    /// The point of the item: one type cannot have two behaviours. Both spellings are
    /// asserted for every case rather than one being taken as representative, because
    /// "they are the same type" was true before this change too — and they still disagreed.
    /// </remarks>
    [Theory]
    [InlineData("void", "echo \"hi\"")]
    [InlineData("nothing", "echo \"hi\"")]
    [InlineData("void", "return 1")]
    [InlineData("nothing", "return 1")]
    public async Task Producing_a_value_from_a_void_function_is_refused(string spelling, string body)
        => Assert.Equal(
            "tosh.runtime.void_function_produced_value",
            await DiagnosticCodeAsync($"func f() -> {spelling} {{ {body} }}\nf"));

    /// <summary>
    /// An empty void function is fine, and so is one that only assigns.
    /// </summary>
    /// <remarks>
    /// The control for the rule above: `-> void` already worked when the body ended in a
    /// statement rather than an expression, and it has to keep working. That asymmetry —
    /// failing only when the body ends in an expression — was the original symptom, because
    /// that is exactly when the trailing expression is collapsed into a `return`.
    /// </remarks>
    [Theory]
    [InlineData("void", "var x = 1")]
    [InlineData("nothing", "var x = 1")]
    [InlineData("void", "return")]
    [InlineData("nothing", "return")]
    public async Task A_void_function_that_produces_nothing_is_accepted(string spelling, string body)
    {
        var (output, _) = await RunAsync($"func f() -> {spelling} {{ {body} }}\nf\necho \"ok\"");

        Assert.Equal("ok", output);
    }

    /// <summary>
    /// A void function contributes nothing to a pipeline.
    /// </summary>
    /// <remarks>
    /// The claim the annotation makes, stated as the thing a caller can observe.
    /// </remarks>
    [Theory]
    [InlineData("void")]
    [InlineData("nothing")]
    public async Task A_void_function_contributes_nothing_to_a_pipeline(string spelling)
    {
        var (output, _) = await RunAsync(
            $"func f() -> {spelling} {{ writeline \"hi\" }}\necho (f | count)");

        Assert.Equal("0", output);
    }

    /// <summary>
    /// The two names work as a variable annotation too, and only accept nothing.
    /// </summary>
    /// <remarks>
    /// A return is not the only annotated position, and the rule has to be the same in all
    /// of them or `void` would mean one thing on a function and another on a variable.
    ///
    /// This is here because a first negative control passed: removing the shared
    /// conversion branch broke no test, which meant the branch was doing real work that
    /// nothing was watching. A control that passes is worth more than one that fails.
    /// </remarks>
    [Theory]
    [InlineData("void")]
    [InlineData("nothing")]
    public async Task Void_is_a_variable_annotation_that_accepts_only_nothing(string spelling)
    {
        var (output, _) = await RunAsync($"var x: {spelling} = null\necho \"ok\"");
        Assert.Equal("ok", output);

        Assert.NotNull(await DiagnosticCodeAsync($"var x: {spelling} = 5"));
    }

    /// <summary>
    /// Controls: the neighbouring return annotations are unaffected.
    /// </summary>
    [Theory]
    [InlineData("func f() -> int { return 1 }\necho (f)", "1")]
    [InlineData("func f() -> string { echo \"hi\" }\necho (f)", "hi")]
    [InlineData("func f() { echo \"hi\" }\necho (f)", "hi")]
    [InlineData("func f() -> dynamic { echo \"hi\" }\necho (f)", "hi")]
    public async Task Other_return_annotations_are_unaffected(string source, string expected)
    {
        var (output, _) = await RunAsync(source);

        Assert.Equal(expected, output);
    }
}
