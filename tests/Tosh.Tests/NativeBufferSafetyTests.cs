using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Bounds checks on the buffer surface.
///
/// <c>NativeBuffer</c> validates its own <c>ReadBytes</c>/<c>WriteBytes</c>, but
/// the buffer commands resolved straight to an <c>IntPtr</c> and called
/// <c>Marshal</c> directly, so those checks never applied. Reading a
/// <c>long</c> from a four-byte buffer returned adjacent heap; writing a struct
/// into an undersized one corrupted it. These pin the checks that close that.
/// </summary>
public class NativeBufferSafetyTests
{
    private static ToshEngine NewEngine() => new(ToshRuntime.CreateDefault().Language);

    private static async Task<ToshDiagnostic> ExpectDiagnostic(string source)
    {
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => NewEngine().ExecuteToListAsync(source));

        return Assert.Single(exception.Diagnostics);
    }

    [Fact]
    public async Task Reading_a_scalar_past_the_end_is_rejected()
    {
        var diagnostic = await ExpectDiagnostic(
            """
            alloc buf = 4
            read-buffer long $buf
            """);

        Assert.Equal("tosh.runtime.native_buffer_range", diagnostic.Code);
        Assert.Contains("4-byte buffer", diagnostic.Title);
    }

    [Fact]
    public async Task Reading_a_scalar_past_the_end_via_offset_is_rejected()
    {
        var diagnostic = await ExpectDiagnostic(
            """
            alloc buf = 8
            read-buffer long $buf --at 4
            """);

        Assert.Equal("tosh.runtime.native_buffer_range", diagnostic.Code);
    }

    [Fact]
    public async Task Reading_more_bytes_than_allocated_is_rejected()
    {
        var diagnostic = await ExpectDiagnostic(
            """
            alloc buf = 4
            read-buffer bytes $buf --count 64
            """);

        Assert.Equal("tosh.runtime.native_buffer_range", diagnostic.Code);
    }

    /// <summary>
    /// The dangerous direction: <c>Marshal.StructureToPtr</c> into an undersized
    /// buffer writes past the allocation.
    /// </summary>
    [Fact]
    public async Task Writing_a_struct_past_the_end_is_rejected()
    {
        var diagnostic = await ExpectDiagnostic(
            """
            raw struct Big { a: long; b: long; c: long }
            alloc buf = 4
            write-buffer $buf (new Big())
            """);

        Assert.Equal("tosh.runtime.native_buffer_range", diagnostic.Code);
    }

    /// <summary>
    /// A `ref` buffer is both seeded from and written back to through a raw
    /// pointer, so an undersized one reads adjacent heap and then corrupts it.
    /// </summary>
    [Fact]
    public async Task Ref_parameters_reject_an_undersized_buffer()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var diagnostic = await ExpectDiagnostic(
            """
            raw struct TV { tv_sec: long; tv_usec: long }
            bind native "libc.so.6" as L {
                func gettimeofday(ref tv: TV, nint) -> int
            }
            alloc buf = 4
            L.gettimeofday($buf, 0)
            """);

        Assert.Equal("tosh.runtime.native_buffer_range", diagnostic.Code);
        Assert.Contains("16-byte buffer", diagnostic.Title);
    }

    /// <summary>
    /// The checks must not cost the legitimate cases anything.
    /// </summary>
    [Fact]
    public async Task Reads_and_writes_within_bounds_still_work()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            raw struct P { a: int; b: long }
            alloc buf = P
            write-buffer $buf (new P(7, 99))
            read-buffer int $buf
            read-buffer long $buf --at 8
            (read-buffer bytes $buf --count 16).Length
            forget $buf | ignore
            """);

        Assert.Equal(7, Convert.ToInt32(results[0]));
        Assert.Equal(99L, Convert.ToInt64(results[1]));
        Assert.Equal(16, Convert.ToInt32(results[2]));
    }

    /// <summary>
    /// A bare pointer carries no length, so nothing can be checked there — that
    /// is the boundary a caller opts into by handling raw pointers, and it must
    /// keep working.
    /// </summary>
    [Fact]
    public async Task Raw_pointers_remain_unchecked()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var results = await NewEngine().ExecuteToListAsync(
            """
            bind native "libc.so.6" as L {
                func getenv(string) -> nint
            }
            var p = L.getenv("PATH")
            read-buffer cstring $p
            """);

        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(Assert.Single(results))));
    }

    // --- layout correctness found during review ---

    /// <summary>
    /// Two fields with one name emitted two CLR fields and shifted every
    /// subsequent offset, silently. It is always a typo.
    /// </summary>
    [Fact]
    public async Task Duplicate_field_names_are_rejected()
    {
        var diagnostic = await ExpectDiagnostic("raw struct D { a: int; a: long }");

        Assert.Equal("tosh.runtime.raw_struct_duplicate_field", diagnostic.Code);
    }

    /// <summary>`struct foo bar[4]` is ordinary C and must be expressible.</summary>
    [Fact]
    public async Task Inline_arrays_of_structs_are_supported()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            raw struct I { a: int; b: int }
            raw struct O { head: long; xs: I[4] }
            size-of I
            size-of O
            offset-of O xs
            """);

        Assert.Equal(8, Convert.ToInt32(results[0]));
        Assert.Equal(40, Convert.ToInt32(results[1]));   // 8 + 4 * 8
        Assert.Equal(8L, Convert.ToInt64(results[2]));
    }

    [Fact]
    public async Task Unions_are_sized_by_their_largest_field()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            raw union U { small: byte; big: long; mid: int }
            size-of U
            offset-of U big
            offset-of U small
            """);

        Assert.Equal(8, Convert.ToInt32(results[0]));
        Assert.Equal(0L, Convert.ToInt64(results[1]));
        Assert.Equal(0L, Convert.ToInt64(results[2]));
    }

    /// <summary>
    /// `methods` must describe the callable surface, not the raw ABI: the
    /// synthesized length of a `buffer[n]` is plumbing, and an `out` parameter
    /// is engine-supplied, so neither is an argument the caller passes.
    /// </summary>
    [Fact]
    public async Task Native_member_signatures_describe_the_callable_surface()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var results = await NewEngine().ExecuteToListAsync(
            """
            hermit class C {
                proud bind native "libc.so.6" {
                    func gethostname(out name: buffer[256]) -> ok
                }
            }
            methods C | get Signature
            methods C | get ParameterCount
            """);

        var signature = Assert.IsType<string>(results[0]);
        Assert.Contains("out name: buffer[256]", signature);
        Assert.DoesNotContain("__length", signature);
        Assert.Equal(0, Convert.ToInt32(results[1]));
    }

    [Fact]
    public async Task Referencing_a_native_member_without_parentheses_explains_itself()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => NewEngine().ExecuteToListAsync(
                """
                hermit class C {
                    shy bind native "libc.so.6" { func abs(int) -> int }
                }
                C.abs
                """));

        Assert.Contains("native binding", exception.Message);
        Assert.Contains("parentheses", exception.Message);
    }
}
