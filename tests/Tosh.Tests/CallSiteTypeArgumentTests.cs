using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Type arguments written at a member call site — <c>TS-P2-49</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>$a.m&lt;int&gt;(11)</c> did not parse, while the free-function form and inference both did.
/// Three pieces were needed: the parser reading the list, the engine resolving the names, and the
/// invoker carrying them to whichever route serves the call.
/// </para>
/// <para>
/// Names resolve in the engine rather than the invoker, because scope, aliases and
/// ToastScript-declared types are knowledge the invoker does not have. And the invoker's overload
/// <i>refuses</i> by default rather than ignoring: a target that does not understand type
/// arguments says so, because binding by inference instead would answer a request for a specific
/// instantiation with a different one — worse than the parse error this replaced.
/// </para>
/// </remarks>
public sealed class CallSiteTypeArgumentTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private const string Generic =
        "class A { func m<U>(x: U) -> U { return $x } }\nvar a = new A()\n";

    [Fact]
    public async Task A_member_call_accepts_explicit_type_arguments()
    {
        Assert.Equal("11", await RunAsync($"{Generic}$a.m<int>(11)"));
    }

    [Fact]
    public async Task An_inherited_generic_method_accepts_them_too()
    {
        Assert.Equal("5", await RunAsync(
            "class B { func m<U>(x: U) -> U { return $x } }\nclass D extends B { }\n(new D()).m<int>(5)"));
    }

    [Fact]
    public async Task Explicit_arguments_replace_inference_rather_than_merging()
    {
        // Asking for <string> while passing an int must not quietly bind U to int. The conversion
        // is what fails, which is the point: the request was honoured, not second-guessed.
        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync($"{Generic}$a.m<string>(11)"));
    }

    [Fact]
    public async Task An_arity_mismatch_is_refused()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(
            async () => await RunAsync($"{Generic}$a.m<int, string>(11)"));

        Assert.Contains("type parameter", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_non_generic_method_refuses_type_arguments()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(
            "class A { func plain(x) { return $x } }\nvar a = new A()\n$a.plain<int>(11)"));

        Assert.Contains("not generic", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unresolvable_type_argument_is_a_diagnostic()
    {
        // Never a quiet fall back to inference.
        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync($"{Generic}$a.m<NoSuchType>(11)"));
    }

    [Theory]
    // The controls: inference, and the comparison that makes `<` ambiguous in the first place.
    [InlineData("class A { func m<U>(x: U) -> U { return $x } }\nvar a = new A()\n$a.m(11)", "11")]
    [InlineData("var a = 1\nvar b = 2\n($a < $b)", "True")]
    [InlineData("class C { prop V = 1 }\nvar c = new C()\nvar b = 5\n($c.V < $b)", "True")]
    public async Task Forms_that_already_worked_are_unchanged(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }
}
