using System.Reflection;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A function value can be given a concrete type — `TOAST-0036`.
/// </summary>
/// <remarks>
/// <para>
/// Phase B's third bullet asked that higher-order calls be made "reliable". Measured, four
/// of its six features were already typed, and the two that were not shared one cause: there
/// was no way to write the type of a function. `FunctionType` existed in the bound tree with
/// a `DisplayName` and was **never constructed**, because the type-name grammar had no
/// function node — the representation was finished and unreachable.
/// </para>
/// <para>
/// `func(int) -> int` is the spelling, chosen over a named `delegate` declaration because it
/// mirrors the declaration syntax and needs no name for a signature used once.
/// </para>
/// </remarks>
public sealed class FunctionTypeTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public FunctionTypeTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
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

    /// <summary>The bindings lowering left dynamic without being asked to.</summary>
    private IReadOnlyList<string> Uninferred(string source)
    {
        var engine = new ToshEngine(_runtime.Language);
        var parse = engine.Parse(source, "<function-type-test>");
        Assert.True(parse.Diagnostics.Count == 0, $"parse errors: {string.Join(", ", parse.Diagnostics)}");

        return DynamicFallbacks.In(parse, _runtime.Commands);
    }

    private const string Dbl = "func dbl(x: int) -> int => $x * 2\n";

    /// <summary>A function type can be written where any type can.</summary>
    [Theory]
    [InlineData(Dbl + "func apply(g: func(int) -> int, v: int) -> int => $g($v)\necho (apply &dbl 21)", "42")]
    [InlineData(Dbl + "var f: func(int) -> int = &dbl\necho $f(21)", "42")]
    [InlineData("var f: func(int) -> int = func(x: int) -> int => $x + 1\necho $f(41)", "42")]
    public async Task A_function_type_can_be_written_where_a_type_is_expected(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>The return may itself be a function, so currying is writable.</summary>
    /// <remarks>
    /// Right-associative, because the return is parsed by the ordinary return-type
    /// production and that is greedy: `func(int) -> func(int) -> int` is a function
    /// returning a function, not a function of two arguments.
    /// </remarks>
    [Fact]
    public async Task A_function_type_may_return_a_function()
        => Assert.Equal(
            "42",
            await RunAsync("""
                func adder(n: int) -> func(int) -> int {
                    return func(x: int) -> int => $x + $n
                }
                var a: func(int) -> int = (adder 10)
                echo $a(32)
                """));

    /// <summary>
    /// What can be checked when the value arrives is checked: callability and arity.
    /// </summary>
    /// <remarks>
    /// The parameter *types* are a promise. At run time there is nothing
    /// to compare them against until the call happens, so rejecting on them here would be
    /// guessing rather than checking.
    /// </remarks>
    [Theory]
    [InlineData("var f: func(int) -> int = 5")]
    [InlineData("var f: func(int) -> int = \"not callable\"")]
    [InlineData("func two(x: int, y: int) -> int => $x\nvar f: func(int) -> int = &two")]
    public async Task A_value_that_cannot_be_called_this_way_is_rejected(string source)
        => Assert.Equal("tosh.runtime.annotation_conversion_failed", await DiagnosticCodeAsync(source));

    /// <summary>
    /// A bare `func` means "some callable", and used to mean `System.Func`1`.
    /// </summary>
    /// <remarks>
    /// The platform-index fallback added by `TOAST-0034` resolved the *keyword* to the CLR
    /// type by simple name, so `var f: func` was concrete and wrong rather than merely
    /// vague — it rejected every ToastScript function while accepting nothing useful.
    /// </remarks>
    [Fact]
    public async Task A_bare_func_accepts_any_callable()
    {
        Assert.Equal("ok", await RunAsync(Dbl + "var f: func = &dbl\necho \"ok\""));
        Assert.Equal("tosh.runtime.annotation_conversion_failed", await DiagnosticCodeAsync("var f: func = 5"));
    }

    /// <summary>An element naming no known type is reported.</summary>
    [Theory]
    [InlineData("var f: func(Nope) -> int = 5")]
    [InlineData("var f: func(int) -> Nope = 5")]
    public async Task An_unknown_signature_type_is_reported(string source)
        => Assert.Equal("tosh.runtime.annotation_unknown_type", await DiagnosticCodeAsync(source));

    /// <summary>
    /// The two shapes that could not be typed before are now typed with no dynamic fallback.
    /// </summary>
    /// <remarks>
    /// These are the item: a function-typed parameter stayed dynamic because there was no
    /// type to write, and a lambda in a variable stayed dynamic because nothing could
    /// describe it.
    /// </remarks>
    [Theory]
    [InlineData(Dbl + "func apply(g: func(int) -> int, v: int) -> int => $g($v)\necho (apply &dbl 21)")]
    [InlineData(Dbl + "var f: func(int) -> int = &dbl\necho $f(21)")]
    [InlineData("var f: func(int) -> int = func(x: int) -> int => $x + 1\necho $f(41)")]
    [InlineData("func adder(n: int) -> func(int) -> int {\n return func(x: int) -> int => $x + $n\n}\nvar a: func(int) -> int = (adder 10)\necho $a(32)")]
    public void A_typed_higher_order_shape_has_no_dynamic_fallback(string source)
    {
        var fallbacks = Uninferred(source);

        Assert.True(
            fallbacks.Count == 0,
            "expected no dynamic fallback, got: " + string.Join(", ", fallbacks));
    }

    /// <summary>
    /// A lambda that states its own types does not have to be described twice.
    /// </summary>
    /// <remarks>
    /// `func(x: int) -> int => $x + 1` already says what it takes and returns, so requiring
    /// the same signature again on the variable would be asking the author to repeat
    /// themselves.
    /// </remarks>
    [Fact]
    public void A_lambda_that_declares_its_types_is_inferred()
    {
        var fallbacks = Uninferred("var lam = func(x: int) -> int => $x + 1\necho $lam(41)");

        Assert.True(
            fallbacks.Count == 0,
            "expected the lambda's own annotations to be enough, got: " +
            string.Join(", ", fallbacks));
    }

    /// <summary>
    /// A lambda that states nothing stays dynamic, and says so.
    /// </summary>
    /// <remarks>
    /// The control for the test above. Inference that produced a signature here would be
    /// inventing one — `dynamic` is the honest answer to "this was not stated".
    /// </remarks>
    [Fact]
    public void An_unannotated_lambda_is_still_dynamic()
    {
        var fallbacks = Uninferred("var lam = func(x) => $x + 1\necho $lam(41)");

        Assert.Contains("variable 'lam'", fallbacks);
    }

    /// <summary>
    /// Controls: the four higher-order features that were already typed still are.
    /// </summary>
    /// <remarks>
    /// Measured before this item started, to find out how much of Phase B's bullet was
    /// actually unreliable. The answer was: one missing type and four working features.
    /// </remarks>
    [Theory]
    [InlineData("interface Drawable { func Draw() -> string }\nclass Box fulfills Drawable { func Draw() -> string => \"box\" }\necho (new Box()).Draw()")]
    [InlineData("union Result {\n    Ok(value)\n    Error(message)\n}\nvar s: Result = Result.Ok(42)\necho $s.Variant")]
    [InlineData("class Holder<T> { prop V: T\n    func Get() -> T => $this.V\n}\nvar b = new Holder<int>()\n$b.V = 7\necho $b.Get()")]
    [InlineData("class K { func M(x: int) -> int => $x + 1 }\nvar k = new K()\necho $k.M(41)")]
    public void The_features_that_were_already_typed_still_are(string source)
    {
        var fallbacks = Uninferred(source);

        Assert.True(
            fallbacks.Count == 0,
            "a feature that was typed before this item no longer is: " +
            string.Join(", ", fallbacks));
    }

    // --- A return annotation describes the returned value (`TOSH-0010`) ---

    /// <summary>
    /// A function that emits before returning is checked against what it *returns*.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A body statement that produces a value contributes it to the function's output, and
    /// the annotation used to be applied to every one of them — so a function that emitted
    /// anything before `return` was checked against the emission too. `§Refinement Types`
    /// describes `-> T where (_)` as a predicate over the **return value**, and this is that.
    /// </para>
    /// <para>
    /// It was not academic: `scripts/build.tosh`'s publish command emits `dotnet` build output
    /// through an annotated function, so every release ended by reporting a conversion failure
    /// that had not happened — the work had all succeeded.
    /// </para>
    /// <para>
    /// The emitted value is still *produced*. Only the annotation stops applying to it, so the
    /// function below yields two values and the caller sees both.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_return_annotation_ignores_values_emitted_on_the_way()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("""
            record R(A: string)
            func Make() -> R {
                42
                return new R("v")
            }
            echo (Make() | count)
            """);

        Assert.Equal("2", results[^1]?.ToString());
    }

    /// <summary>The same function without the emission returns cleanly.</summary>
    /// <remarks>
    /// The control. Without it the test above would pass for a function that simply cannot
    /// return a record at all, which is a different and much larger claim.
    /// </remarks>
    [Fact]
    public async Task The_same_function_without_an_emission_returns_its_record()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("""
            record R(A: string)
            func Make() -> R {
                return new R("v")
            }
            var made = Make()
            echo $made.A
            """);

        Assert.Equal("v", results[^1]?.ToString());
    }

    /// <summary>A function with no `return` still has its emitted value checked.</summary>
    /// <remarks>
    /// Why the fix is not simply "stop checking emitted values": for a function whose result
    /// *is* its trailing expression, the emission is the return value, and checking it is the
    /// annotation doing its job.
    /// </remarks>
    [Fact]
    public async Task An_emitted_value_is_still_checked_when_there_is_no_return()
    {
        var engine = ShellEngine.CreateFullShell();

        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
            await engine.ExecuteToListAsync("""
                func Make() -> int {
                    "not an int"
                }
                var v = Make()
                """));

        Assert.Contains(
            error.Diagnostics,
            d => d.Code == "tosh.runtime.return_type_conversion_failed");
    }
}
