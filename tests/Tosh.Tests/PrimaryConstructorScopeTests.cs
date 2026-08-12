using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A primary-constructor parameter reaches a stored initializer and says so elsewhere —
/// <c>TS-P2-81</c>.
/// </summary>
/// <remarks>
/// <para>
/// Filed as a defect: <c>prop X => $x</c> fails where <c>prop X = $x</c> succeeds, with nothing
/// in the specification about the split. Measured, the behaviour is right and the account of it
/// was missing. A stored initializer runs *while* the value is being built, with the parameters
/// bound; a computed property, an accessor block, a method body and a static initializer all run
/// afterwards, and a constructor parameter is a local of the construction that was never stored.
/// </para>
/// <para>
/// So this is the intent's second branch — the rule is stated rather than changed — plus the
/// diagnostic. The bare spelling already said it (<c>TS-P2-41</c>); the <c>$</c> spelling
/// answered "Variable 'x' was not found. declare it first with 'var x = …'", which is advice for
/// a different problem.
/// </para>
/// </remarks>
public sealed class PrimaryConstructorScopeTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public PrimaryConstructorScopeTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private async Task<object?> EvalAsync(string script)
    {
        var engine = new ToshEngine(_runtime);
        return (await engine.ExecuteToListAsync(script)).LastOrDefault();
    }

    private async Task<string> FailureAsync(string script)
    {
        var engine = new ToshEngine(_runtime);
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            async () => await engine.ExecuteToListAsync(script));

        return exception is ToshDiagnosticException diagnostic
            ? string.Join(" ", diagnostic.Diagnostics.Select(d => d.Title + " " + (d.Help ?? "")))
            : exception.Message;
    }

    [Theory]
    [InlineData("class P(x: int) { prop A = $x }\n(new P(5)).A", 5)]
    [InlineData("class P(x: int) { prop A = x }\n(new P(5)).A", 5)]
    [InlineData("class P(x: int, y: int = 2) { prop A = ($x + $y) }\n(new P(5)).A", 7)]
    public async Task It_reaches_a_stored_initializer_and_a_later_default(string script, int expected)
    {
        Assert.Equal(expected, Convert.ToInt32(await EvalAsync(script)));
    }

    [Theory]
    [InlineData("class P(x: int) { prop A => $x }\nvar p = new P(5)\n$p.A")]
    [InlineData("class P(x: int) { prop A { get { return $x } } }\nvar p = new P(5)\n$p.A")]
    [InlineData("class P(x: int) { func g() { return $x } }\nvar p = new P(5)\n$p.g()")]
    public async Task Elsewhere_it_says_what_it_is_and_where_it_reaches(string script)
    {
        var message = await FailureAsync(script);

        Assert.Contains("constructor parameter of 'P'", message, StringComparison.Ordinal);
        Assert.Contains("stored property initializer", message, StringComparison.Ordinal);
        Assert.DoesNotContain("declare it first with 'var", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_static_initializer_keeps_the_generic_message_for_now()
    {
        // The explanation is keyed on the class whose code is *running*, and a static property
        // initializer runs at class-definition time, before the class is entered — so this one
        // position still answers "Variable 'x' was not found". It fails either way; only the
        // message is worse. Recorded rather than asserted as correct.
        var message = await FailureAsync("class P(x: int) { static prop S = $x }\nP.S");

        Assert.Contains("was not found", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ordinary_undeclared_variable_keeps_its_own_message()
    {
        var message = await FailureAsync("class P(x: int) { func g() { return $nope } }\nvar p = new P(5)\n$p.g()");

        Assert.Contains("was not found", message, StringComparison.Ordinal);
        Assert.DoesNotContain("constructor parameter", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outside_a_class_nothing_changes()
    {
        var message = await FailureAsync("$totallyUndefined");

        Assert.Contains("was not found", message, StringComparison.Ordinal);
    }
}
