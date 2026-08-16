using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A native binding can take a <c>char**</c>: <c>out</c>/<c>ref cstring</c>.
///
/// `TS-P3-26`. This was refused, and for a stated reason — *"out/ref string
/// marshalling has no ownership story: the callee would have to allocate with an
/// allocator the marshaller cannot know."* That is true, and it is answered by
/// declining to own anything: the pointer the callee leaves is **copied** into a
/// managed string and the bytes behind it are left alone. Tosh frees only memory
/// tosh allocated. A caller who must free the callee's buffer declares `ptr` and
/// frees it explicitly, which is what that spelling is for.
///
/// The hard part turned out to be lifetime rather than ownership. A `char*` handed
/// back very often points *into an input string* — `strtol`'s `endptr` is the
/// canonical case — and the default marshaller frees its temporary copy of an
/// input the moment the call returns. Decoding afterwards read freed memory and
/// produced convincing garbage: `strtol("42abc")` reported its `endptr` as `"Q"`.
/// So a binding with a by-ref `cstring` allocates its input strings itself and
/// holds them until every returned pointer has been decoded.
///
/// These use libc, so they need no toolchain, and both functions here return
/// pointers into their own input — which is exactly the case that was broken.
/// </summary>
public class NativeStringByRefTests
{
    private static bool SkipOffLinux => !OperatingSystem.IsLinux();

    private static async Task<object?> RunAsync(string source)
    {
        var results = await new ToshEngine(ToshRuntime.CreateDefault()).ExecuteToListAsync(source);
        return results.Count == 0 ? null : results[^1];
    }

    private const string Strtol = """
        bind native "libc.so.6" as C {
            func strtol(s: cstring, out endptr: cstring, base: int) -> long
        }

        """;

    /// <summary>
    /// `strtol` writes into `endptr` the position where parsing stopped — a pointer
    /// into the string it was given. Both halves are asserted: the number proves the
    /// call still works, the remainder proves the `char**` was read.
    /// </summary>
    [Fact]
    public async Task An_out_cstring_is_decoded()
    {
        if (SkipOffLinux) return;

        Assert.Equal("42", (await RunAsync(Strtol + "(C.strtol(\"42abc\", 10)).ReturnValue"))?.ToString());
        Assert.Equal("abc", (await RunAsync(Strtol + "(C.strtol(\"42abc\", 10)).endptr"))?.ToString());
    }

    /// <summary>
    /// The lifetime case, stated as a value rather than a mechanism: this returned
    /// `"Q"` — a plausible-looking single character out of freed memory — when the
    /// decode happened after the input's temporary was released.
    /// </summary>
    [Theory]
    [InlineData("\"100xyz\"", "xyz")]
    [InlineData("\"7 rest\"", " rest")]
    [InlineData("\"55\"", "")]
    public async Task An_out_pointer_into_the_input_survives_the_call(string input, string expected)
    {
        if (SkipOffLinux) return;

        Assert.Equal(expected, (await RunAsync(Strtol + $"(C.strtol({input}, 10)).endptr"))?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// `strsep` is genuinely in/out: it reads the pointer, advances it past the
    /// first token, and returns the token — so both the `ref` slot and the return
    /// point into the caller's buffer.
    /// </summary>
    [Fact]
    public async Task A_ref_cstring_is_read_and_written()
    {
        if (SkipOffLinux) return;

        const string strsep = """
            bind native "libc.so.6" as C {
                func strsep(ref stringp: cstring, delim: cstring) -> cstring
            }

            """;

        Assert.Equal("a", (await RunAsync(strsep + "(C.strsep(\"a,b,c\", \",\")).ReturnValue"))?.ToString());
        Assert.Equal("b,c", (await RunAsync(strsep + "(C.strsep(\"a,b,c\", \",\")).stringp"))?.ToString());
    }

    /// <summary>
    /// Repeated calls, because the failure mode of getting the release wrong is not a
    /// wrong answer on the first call but corruption on a later one.
    /// </summary>
    [Fact]
    public async Task Repeated_calls_stay_correct()
    {
        if (SkipOffLinux) return;

        var result = await RunAsync(
            Strtol +
            """
            var i = 0
            var last = ""
            until ($i == 500) {
                $i += 1
                $last = (C.strtol("42abc", 10)).endptr
            }
            $last
            """);

        Assert.Equal("abc", result?.ToString());
    }

    /// <summary>
    /// A managed `string` by reference is still refused: the marshaller would have to
    /// guess the callee's encoding *and* its allocator to write one back. The
    /// diagnostic names the spelling that works, since `cstring` is now an answer
    /// rather than a second thing that also fails.
    /// </summary>
    [Fact]
    public async Task A_managed_string_by_reference_is_still_refused()
    {
        if (SkipOffLinux) return;

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync(
                """
                bind native "libc.so.6" as C {
                    func strtol(s: cstring, out endptr: string, base: int) -> long
                }
                C.strtol("1", 10)
                """));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.runtime.unsupported_native_byref_string", diagnostic.Code);
        Assert.Contains("cstring", diagnostic.Help, StringComparison.Ordinal);
    }

    /// <summary>
    /// Input and returned strings are unaffected, which is the control: the
    /// by-ref work rewrites how a binding's *input* strings are marshalled when it
    /// has a `char**`, so the ordinary spellings have to keep working unchanged.
    /// </summary>
    [Theory]
    [InlineData("""
        bind native "libc.so.6" as C { func strlen(s: cstring) -> long }
        C.strlen("unchanged")
        """, "9")]
    [InlineData("""
        bind native "libc.so.6" as C { func getenv(name: cstring) -> cstring }
        C.getenv("PATH") != null
        """, "True")]
    public async Task Ordinary_string_parameters_and_returns_are_unaffected(string source, string expected)
    {
        if (SkipOffLinux) return;

        Assert.Equal(expected, (await RunAsync(source))?.ToString());
    }
}
