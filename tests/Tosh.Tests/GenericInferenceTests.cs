using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A generic method infers its type arguments from the supplied arguments — <c>TS-P2-36</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Task.FromResult(7)</c> failed with "No overload matched static method 'FromResult' on
/// 'System.Threading.Tasks.Task' with 1 argument(s)", because a generic method *definition* can
/// never bind — its parameters are still open — and the overload selector filtered every one of
/// them out. That mattered more once <c>TS-P1-27</c> made explicit <c>await</c> the model, since
/// <c>Task.FromResult</c> and <c>Task.WhenAll</c> are exactly the helpers that model invites.
/// </para>
/// <para>
/// Reflection offers no equivalent of the compiler's inference, so this unifies each parameter's
/// declared type against the argument's runtime type. It is deliberately narrower than C#: no
/// lower/upper-bound lattice and no best-common-type step, so a type parameter used twice must
/// bind to one type or a base of it. Where it cannot infer, it declines rather than guesses and
/// says which type parameter it could not supply.
/// </para>
/// </remarks>
public sealed class GenericInferenceTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public GenericInferenceTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private async Task<object?> EvalAsync(string script)
    {
        var engine = new ToshEngine(_runtime);
        return (await engine.ExecuteToListAsync(script)).LastOrDefault();
    }

    // ── inference from the arguments ───────────────────────────────────────────

    [Theory]
    [InlineData("Task.FromResult(7) | await", "7")]
    [InlineData("Task.FromResult(\"a\") | await", "a")]
    [InlineData("Tuple.Create(1, 2) | get Item2", "2")]
    [InlineData("Tuple.Create(1, \"a\") | get Item2", "a")]
    [InlineData("Enumerable.Repeat(5, 3) | count", "3")]
    public async Task A_generic_static_infers_its_type_argument(string script, string expected)
    {
        Assert.Equal(expected, (await EvalAsync(script))?.ToString());
    }

    [Theory]
    // A trailing `params` array takes every remaining argument, each unified against the element
    // type. Without it these fell through to the non-generic `WhenAll(params Task[])`, which
    // returns a plain `Task` — so they resolved, awaited to nothing, and reported a count of
    // zero rather than failing. Silence, not an error.
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_generic_params_overload_is_preferred_over_its_non_generic_sibling(int count)
    {
        var declarations = string.Join("\n",
            Enumerable.Range(1, count).Select(i => $"var t{i} = Task.FromResult({i})"));
        var arguments = string.Join(", ", Enumerable.Range(1, count).Select(i => $"$t{i}"));

        // `TOAST-0028`: `await` yields the awaited `int[]` as one value, so the spread is
        // what keeps this counting *results* — which is what discriminates the generic
        // overload from the non-generic sibling that awaits to nothing and reports zero.
        var value = await EvalAsync(
            $"{declarations}\necho ...(Task.WhenAll({arguments}) | await) | count");

        Assert.Equal(count, Convert.ToInt32(value));
    }

    // ── where it cannot infer, it says so ──────────────────────────────────────

    [Theory]
    [InlineData("Array.Empty()", "T")]
    [InlineData("Enumerable.Empty()", "TResult")]
    public async Task An_uninferable_call_names_the_type_parameter(string script, string parameterName)
    {
        // "No overload matched" described the wrong problem: the method exists and the arguments
        // are fine — what is missing is the type argument, which no argument set can supply here.
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(async () => await EvalAsync(script));
        var title = exception.Diagnostics[0].Title;

        Assert.Contains("Cannot infer type argument", title, StringComparison.Ordinal);
        Assert.Contains($"'{parameterName}'", title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_genuinely_absent_overload_still_reports_as_missing()
    {
        // The new message must not swallow the old one. `Path.GetFileName` is not generic, so
        // too many arguments is an overload problem and is still described as one. (`Math.Max`
        // would not do here: the shell registers its own variadic `Math`, so `Math.Max(1, 2, 3)`
        // answers 2 rather than reaching CLR overload resolution at all.)
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            async () => await EvalAsync("Path.GetFileName(1, 2, 3)"));

        Assert.Contains("No overload matched", exception.Diagnostics[0].Title, StringComparison.Ordinal);
    }

    // ── what must not change ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Math.Max(1, 2)", "2")]
    [InlineData("Enumerable.Range(1, 3) | count", "3")]
    [InlineData("String.Join(\",\", [\"a\",\"b\"])", "a,b")]
    [InlineData("Path.GetFileName(\"/etc/hostname\")", "hostname")]
    public async Task Non_generic_resolution_is_unaffected(string script, string expected)
    {
        Assert.Equal(expected, (await EvalAsync(script))?.ToString());
    }

    [Fact]
    public async Task An_exact_parameter_type_outranks_a_base_class_one()
    {
        // Overload selection scored "already an instance of" and "exactly this type" alike, so a
        // tie was broken by candidate order. Ranking exact first is what lets the inferred
        // `WhenAll<int>(params Task<int>[])` win over `WhenAll(params Task[])`, and it is the
        // rule C# applies when preferring the more specific overload.
        var value = await EvalAsync(
            """
            var a = Task.FromResult(1)
            var b = Task.FromResult(2)
            type-of (Task.WhenAll($a, $b)) | get IsGenericType
            """);

        // The generic overload returns `Task<int[]>`; the non-generic one returns a bare `Task`.
        // Asserted on genericity rather than the type name, because the runtime type is an
        // internal `WhenAllPromise` in both cases and only its arity tells them apart.
        Assert.Equal(true, value);
    }
}
