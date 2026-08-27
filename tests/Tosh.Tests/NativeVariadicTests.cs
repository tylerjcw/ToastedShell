using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A native binding can end in C's <c>...</c> tail.
///
/// `TS-P3-24`. `func f(count: int, ...)` was refused with *Native interop does not
/// currently support '...'*. The capability was never missing — declaring the
/// concrete arity worked, which is exactly what C# P/Invoke requires — but one
/// declaration per arity is a poor trade when the arity is the caller's choice.
///
/// A variadic call has no single signature to bind to, because C reads its tail
/// according to what the caller pushed: the signature is a property of the call,
/// not of the declaration. So the delegate is built at the call site from the
/// actual argument types, cached by that shape, and handed to the same emitter a
/// fixed binding uses. That is why this is no riskier than the concrete-arity
/// declarations it replaces — it is the same mechanism, with the shape computed
/// rather than written out.
///
/// C's default argument promotions are applied, because the callee reads them:
/// anything narrower than `int` arrives as `int`, and `float` as `double`.
/// `va_arg(ap, int)` is what a C function reads for a `char`, so passing the
/// narrow type would put the wrong bytes in the wrong place.
///
/// These use libc's `snprintf`, so they need no toolchain — and `snprintf` is the
/// sharpest available test, since it reports through the buffer what it actually
/// received.
/// </summary>
public class NativeVariadicTests
{
    private static bool SkipOffLinux => !OperatingSystem.IsLinux();

    private const string Snprintf = """
        bind native "libc.so.6" as C {
            func snprintf(dst: ptr, n: long, fmt: cstring, ...) -> int
        }
        alloc dest = 256

        """;

    /// <summary>Formats through the tail and reads back what C actually saw.</summary>
    private static async Task<string> FormatAsync(string format, string arguments)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            Snprintf +
            $"""
            C.snprintf($dest, 256, "{format}", {arguments}) | ignore
            read-buffer cstring $dest
            """);

        return results.Single()?.ToString() ?? "null";
    }

    [Fact]
    public async Task An_integer_tail_is_read_as_C_reads_it()
    {
        if (SkipOffLinux) return;

        Assert.Equal("1 2 3", await FormatAsync("%d %d %d", "1, 2, 3"));
    }

    /// <summary>
    /// The arity is the caller's, which is the whole point — one declaration, three
    /// call shapes, each getting its own cached delegate.
    /// </summary>
    [Theory]
    [InlineData("%d", "7", "7")]
    [InlineData("%d %d", "7, 8", "7 8")]
    [InlineData("%d %d %d %d", "1, 2, 3, 4", "1 2 3 4")]
    public async Task One_declaration_serves_every_arity(string format, string arguments, string expected)
    {
        if (SkipOffLinux) return;

        Assert.Equal(expected, await FormatAsync(format, arguments));
    }

    /// <summary>
    /// Mixed types in one tail. A `double` is the case that proves the ABI is being
    /// satisfied rather than accidentally working: on x86-64 SysV a floating-point
    /// variadic argument travels in a vector register, so getting `%f` right means
    /// the call is genuinely shaped correctly.
    /// </summary>
    [Fact]
    public async Task A_mixed_tail_carries_strings_and_doubles()
    {
        if (SkipOffLinux) return;

        Assert.Equal(
            "i=42 s=hi f=2.5",
            await FormatAsync("i=%d s=%s f=%.1f", "42, \"hi\", 2.5"));
    }

    /// <summary>
    /// C promotes `float` to `double` before a variadic call, so `%f` must read a
    /// promoted value. Passing the narrow type would read four bytes where the callee
    /// reads eight.
    /// </summary>
    [Fact]
    public async Task A_float_is_promoted_to_double()
    {
        if (SkipOffLinux) return;

        Assert.Equal("1.5", await FormatAsync("%.1f", "(1.5 as float)"));
    }

    /// <summary>An empty tail is a legal call, not a missing argument.</summary>
    [Fact]
    public async Task An_empty_tail_is_allowed()
    {
        if (SkipOffLinux) return;

        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            Snprintf +
            """
            C.snprintf($dest, 256, "no arguments here") | ignore
            read-buffer cstring $dest
            """);

        Assert.Equal("no arguments here", results.Single()?.ToString());
    }

    /// <summary>
    /// The fixed parameters are still required. Without this the relaxed arity check
    /// would accept a call with nothing at all.
    /// </summary>
    [Fact]
    public async Task The_fixed_parameters_are_still_required()
    {
        if (SkipOffLinux) return;

        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(Snprintf + "C.snprintf($dest)"));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.runtime.native_argument_count_mismatch", diagnostic.Code);
        Assert.Contains("at least", diagnostic.Title);
    }

    /// <summary>
    /// Nothing may follow the tail, because C reads it after every fixed parameter.
    /// Accepting it would produce a signature the callee cannot match.
    /// </summary>
    [Fact]
    public async Task Nothing_may_follow_the_tail()
    {
        if (SkipOffLinux) return;

        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                bind native "libc.so.6" as C {
                    func snprintf(dst: ptr, n: long, ..., fmt: cstring) -> int
                }
                C.snprintf(0, 0, "x")
                """));

        Assert.Equal(
            "tosh.runtime.native_variadic_not_last",
            Assert.Single(exception.Diagnostics).Code);
    }

    /// <summary>
    /// A non-variadic binding is unchanged: too many arguments is still an error, and
    /// still says so exactly. The relaxed check must apply only where `...` is written.
    /// </summary>
    [Fact]
    public async Task A_fixed_binding_still_rejects_extra_arguments()
    {
        if (SkipOffLinux) return;

        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                bind native "libc.so.6" as C {
                    func abs(v: int) -> int
                }
                C.abs(1, 2, 3)
                """));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.runtime.native_argument_count_mismatch", diagnostic.Code);
        Assert.DoesNotContain("at least", diagnostic.Title);
    }
}
