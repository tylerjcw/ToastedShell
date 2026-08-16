using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// `-&gt; count` can state the width the C function actually returns.
///
/// `TS-P2-124`. Bare `-&gt; count` marshals the return as `IntPtr`, which is right
/// for `ssize_t` and covers `read`, `write`, `readlink` and `sysconf` on LP64.
/// Nothing checked that the bound function really returns a pointer-sized value,
/// and bound to one returning `int32_t` a -1 failure arrived as **4294967295** —
/// the low half read as unsigned, the high half never written by the callee.
/// 4294967295 is non-negative, so the contract's own `&gt;= 0` check passed, no
/// `NativeError` was raised, and the caller received a huge count instead of an
/// error.
///
/// **The misread is ABI-dependent, which is what makes it dangerous.** Whether
/// the upper half of the return register holds garbage is up to the callee:
/// glibc's `open` happens to sign-extend, so the failure surfaces there, while a
/// gcc-built `int32_t` function does not and the value comes back as 4294967295.
/// A silent wrong value that appears only with some libraries is worse than one
/// that appears with all of them.
///
/// The width is knowable at the declaration site and nowhere else, so that is
/// where it is now said. These tests use libc, so they need no toolchain.
/// </summary>
public class NativeCountWidthTests
{
    private static bool SkipOffLinux => !OperatingSystem.IsLinux();

    private static async Task<IReadOnlyList<object?>> RunAsync(string source)
        => await new ToshEngine(ToshRuntime.CreateDefault()).ExecuteToListAsync(source);

    /// <summary>
    /// `open(2)` returns an `int` file descriptor, or -1 with errno — the exact
    /// shape the width-carrying form exists for.
    /// </summary>
    private const string OpenInt = """
        bind native "libc.so.6" as C {
            func open(path: cstring, flags: int) -> int count
        }

        """;

    [Fact]
    public async Task An_int_count_succeeds_and_yields_its_value()
    {
        if (SkipOffLinux) return;

        // O_RDONLY on a file that certainly exists; the fd is non-negative.
        var results = await RunAsync(OpenInt + "C.open(\"/proc/self/cmdline\", 0) >= 0");

        Assert.Equal("True", results.Single()?.ToString());
    }

    [Fact]
    public async Task An_int_count_raises_with_errno_on_failure()
    {
        if (SkipOffLinux) return;

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync(OpenInt + "C.open(\"/nonexistent-tosh-count-test\", 0)"));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.native.call_failed", diagnostic.Code);
        Assert.Contains("ENOENT", diagnostic.Help);
    }

    /// <summary>
    /// Bare `count` keeps meaning `ssize_t`, which is the whole point of adding a
    /// width rather than changing one. `sysconf` returns `long`.
    /// </summary>
    [Fact]
    public async Task Bare_count_still_means_ssize_t()
    {
        if (SkipOffLinux) return;

        var results = await RunAsync(
            """
            bind native "libc.so.6" as C {
                func sysconf(name: int) -> count
            }
            C.sysconf(30) > 0
            """);

        Assert.Equal("True", results.Single()?.ToString());
    }

    /// <summary>`long count` is accepted and behaves as the pointer-sized form.</summary>
    [Fact]
    public async Task A_long_count_is_accepted()
    {
        if (SkipOffLinux) return;

        var results = await RunAsync(
            """
            bind native "libc.so.6" as C {
                func sysconf(name: int) -> long count
            }
            C.sysconf(30) > 0
            """);

        Assert.Equal("True", results.Single()?.ToString());
    }

    /// <summary>
    /// A width that cannot carry a count — or its -1 — is refused at the
    /// declaration, where the mistake is. Accepting `double count` would put the
    /// failure back where it started: a silent wrong number.
    /// </summary>
    /// <remarks>
    /// Only `double` here: `string count` is refused earlier still, by the rule that
    /// a native return cannot be a managed string at all, which is the more specific
    /// diagnostic of the two and the right one to keep.
    /// </remarks>
    [Theory]
    [InlineData("double")]
    [InlineData("float")]
    public async Task A_non_integer_width_is_refused(string width)
    {
        if (SkipOffLinux) return;

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync(
                $$"""
                bind native "libc.so.6" as C {
                    func sysconf(name: int) -> {{width}} count
                }
                C.sysconf(30)
                """));

        Assert.Equal(
            "tosh.runtime.native_count_width_not_integer",
            Assert.Single(exception.Diagnostics).Code);
    }

    /// <summary>
    /// The contract still only applies where it is written: a plain `-&gt; int`
    /// return is unchecked, so -1 is just -1 and no error is raised. Without this
    /// the width form could be "working" by making every int return checked.
    /// </summary>
    [Fact]
    public async Task A_plain_int_return_is_still_unchecked()
    {
        if (SkipOffLinux) return;

        var results = await RunAsync(
            """
            bind native "libc.so.6" as C {
                func open(path: cstring, flags: int) -> int
            }
            C.open("/nonexistent-tosh-count-test", 0)
            """);

        Assert.Equal("-1", results.Single()?.ToString());
    }
}
