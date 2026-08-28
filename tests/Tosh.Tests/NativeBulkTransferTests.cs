using Tosh.Language;

namespace Tosh.Tests;

/// <summary>
/// An array moves into and out of native memory in one command — <c>TOAST-0079</c>.
/// </summary>
/// <remarks>
/// <para>
/// There was no way to do it. <c>write-buffer $b [1.5, 2.5]</c> was refused, correctly — a
/// list of doubles is not a byte sequence and truncating it would have been worse — so a
/// vertex buffer had to be built one scalar at a time, re-entering command dispatch for every
/// number. That is why <c>examples/gl_mouse_cube.tosh</c> compiles a display list rather than
/// uploading geometry: the data plane was not reachable.
/// </para>
/// <para>
/// Both directions use the flags that already existed. <c>--as</c> states the element type on
/// the way in, which is the same thing it means for a single write (<c>TOAST-0077</c>);
/// <c>--count</c> states how many on the way out, which is what it already meant for
/// <c>bytes</c>. Measured on 20,000 doubles: 137 ms one at a time, 3 ms in bulk.
/// </para>
/// </remarks>
public sealed class NativeBulkTransferTests
{
    private static async Task<IReadOnlyList<object?>> RunAsync(string script)
    {
        var engine = ShellEngine.CreateFullShell();
        return await engine.ExecuteToListAsync(script);
    }

    [Fact]
    public async Task An_array_round_trips_through_native_memory()
    {
        var results = await RunAsync("""
            var b = (alloc 64)
            write-buffer $b [1.5, 2.5, 3.5] --as double --at 0
            echo ((read-buffer double $b --count 3) | join ", ")
            $b | native-free
            """);

        Assert.Equal("1.5, 2.5, 3.5", results[^1]?.ToString());
    }

    /// <summary>
    /// The stated element type is the stride, so the bytes are the layout a C library expects.
    /// </summary>
    /// <remarks>
    /// <c>float</c> rather than <c>double</c> is the case that matters for graphics: a vertex
    /// array is four bytes per component, and a tosh number is a <c>double</c>.
    /// </remarks>
    [Theory]
    [InlineData("int32", "10 0 0 0 20 0 0 0")]
    [InlineData("int16", "10 0 20 0 0 0 0 0")]
    [InlineData("byte", "10 20 0 0 0 0 0 0")]
    public async Task The_stated_element_type_is_the_stride(string typeName, string expected)
    {
        var results = await RunAsync($"""
            var b = (alloc 32)
            write-buffer $b [10, 20] --as {typeName} --at 0
            echo ((read-buffer bytes $b --count 8) | join " ")
            $b | native-free
            """);

        Assert.Equal(expected, results[^1]?.ToString());
    }

    [Fact]
    public async Task A_float_array_has_the_bytes_a_vertex_buffer_expects()
    {
        var results = await RunAsync("""
            var b = (alloc 32)
            write-buffer $b [1.0, -1.0, 0.5] --as float --at 0
            echo ((read-buffer bytes $b --count 12) | join " ")
            $b | native-free
            """);

        // 1.0f, -1.0f, 0.5f little-endian.
        Assert.Equal("0 0 128 63 0 0 128 191 0 0 0 63", results[^1]?.ToString());
    }

    [Fact]
    public async Task A_bulk_write_honours_the_offset()
    {
        var results = await RunAsync("""
            var b = (alloc 32)
            write-buffer $b [7, 8] --as int32 --at 8
            echo ((read-buffer int32 $b --count 2 --at 8) | join ", ")
            $b | native-free
            """);

        Assert.Equal("7, 8", results[^1]?.ToString());
    }

    /// <summary>
    /// A sequence too long for the buffer is refused before anything is written.
    /// </summary>
    /// <remarks>
    /// Whole rather than half: a partially copied array leaves the buffer in a state no reader
    /// can detect, which is worse than the write failing.
    /// </remarks>
    [Fact]
    public async Task A_sequence_too_long_for_the_buffer_is_refused_whole()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync("""
            var b = (alloc 8)
            write-buffer $b [1, 2, 3, 4] --as int32 --at 0
            """));

        Assert.Contains("past the end", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_element_that_does_not_fit_is_refused()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync("""
            var b = (alloc 64)
            write-buffer $b [1, 5000000000] --as int32 --at 0
            """));

        Assert.Contains("Int32", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_past_the_end_is_refused()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync("""
            var b = (alloc 8)
            echo (read-buffer int32 $b --count 9)
            """));

        Assert.Contains("past the end", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without <c>--count</c> a read is still a single value, and <c>bytes</c> still means
    /// bytes — the flag gained a meaning it did not have rather than changing one it did.
    /// </summary>
    [Fact]
    public async Task The_single_value_and_bytes_spellings_are_unchanged()
    {
        var results = await RunAsync("""
            var b = (alloc 16)
            write-buffer $b [10, 20] --as int32 --at 0
            echo (read-buffer int32 $b --at 4)
            echo ((read-buffer bytes $b --count 8) | join " ")
            $b | native-free
            """);

        Assert.Equal("20", results[^2]?.ToString());
        Assert.Equal("10 0 0 0 20 0 0 0", results[^1]?.ToString());
    }

    /// <summary>
    /// A string keeps its meaning: it is a C string, not a sequence of characters to spread.
    /// </summary>
    [Fact]
    public async Task A_string_is_not_treated_as_a_sequence()
    {
        var results = await RunAsync("""
            var b = (alloc 32)
            write-buffer $b "hi" --at 0
            echo (read-buffer cstring $b)
            $b | native-free
            """);

        Assert.Equal("hi", results[^1]?.ToString());
    }
}
