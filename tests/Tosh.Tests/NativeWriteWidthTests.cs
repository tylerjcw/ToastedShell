using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A native write states its width instead of inheriting it from the data — <c>TOAST-0077</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>write-buffer</c> writes <c>Marshal.SizeOf(value.GetType())</c> bytes, so a buffer's layout
/// was a consequence of what happened to be in it. An integer is <c>Int32</c> only while it
/// fits; the first value to arrive as a <c>long</c> — a file length, a tick count, any CLR
/// <c>long</c> — wrote eight bytes into a four-byte slot and destroyed its neighbour.
/// </para>
/// <para>
/// The bounds checks could not catch it, and that is not a gap in them: the write is *inside*
/// the buffer. Bounds checking cannot see a layout it was never told about, which is why the
/// answer is to let the layout be stated.
/// </para>
/// </remarks>
public sealed class NativeWriteWidthTests
{
    private static async Task<IReadOnlyList<object?>> RunAsync(string script)
    {
        var engine = ShellEngine.CreateFullShell();
        return await engine.ExecuteToListAsync(script);
    }

    private const string ThreeSlots = """
        var b = (alloc 12)
        write-buffer $b (cast int32 11) --at 0
        write-buffer $b (cast int32 22) --at 4
        write-buffer $b (cast int32 33) --at 8
        """;

    private const string ReadSlots = """
        echo $"{(read-buffer int32 $b --at 0)} {(read-buffer int32 $b --at 4)} {(read-buffer int32 $b --at 8)}"
        $b | native-free
        """;

    /// <summary>
    /// The defect, and the fix, side by side.
    /// </summary>
    [Fact]
    public async Task A_stated_width_leaves_the_neighbouring_slot_alone()
    {
        var withoutFlag = await RunAsync($"""
            {ThreeSlots}
            write-buffer $b (cast int64 99) --at 0
            {ReadSlots}
            """);

        // The inherited width is still the default, so this is what it does.
        Assert.Equal("99 0 33", withoutFlag[^1]?.ToString());

        var withFlag = await RunAsync($"""
            {ThreeSlots}
            write-buffer $b (cast int64 99) --as int32 --at 0
            {ReadSlots}
            """);

        Assert.Equal("99 22 33", withFlag[^1]?.ToString());
    }

    /// <summary>
    /// The stated width is the width, whatever the value's own type is. The buffer is filled
    /// with <c>0xFF</c> first because a fresh one is zeroed, which hides how far a write reached.
    /// </summary>
    [Theory]
    [InlineData("byte", "7 255 255 255 255 255 255 255")]
    [InlineData("int16", "7 0 255 255 255 255 255 255")]
    [InlineData("int32", "7 0 0 0 255 255 255 255")]
    [InlineData("int64", "7 0 0 0 0 0 0 0")]
    public async Task A_stated_width_is_the_width(string typeName, string expected)
    {
        var results = await RunAsync($"""
            var b = (alloc 16)
            write-buffer $b [255, 255, 255, 255, 255, 255, 255, 255] --at 0
            write-buffer $b (cast int64 7) --as {typeName} --at 0
            echo ((read-buffer bytes $b --count 8) | join " ")
            $b | native-free
            """);

        Assert.Equal(expected, results[^1]?.ToString());
    }

    /// <summary>
    /// A value that does not fit is refused rather than wrapped: truncating would replace a
    /// silent corruption of the next slot with a silent corruption of this one.
    /// </summary>
    [Fact]
    public async Task A_value_that_does_not_fit_the_stated_width_is_refused()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync("""
            var b = (alloc 16)
            write-buffer $b 5000000000 --as int32
            """));

        Assert.Contains("Int32", error.Message);
        Assert.Contains("5000000000", error.Message);
    }

    /// <summary>
    /// The vocabulary is the one <c>size-of</c>, <c>alloc</c> and <c>read-buffer</c> take,
    /// including a <c>raw struct</c> declared in the script.
    /// </summary>
    [Fact]
    public async Task A_stated_width_accepts_a_raw_struct_name()
    {
        var results = await RunAsync("""
            raw struct Pair {
                a: int
                b: int
            }
            var b = (alloc 16)
            write-buffer $b (cast int32 5) --as int32 --at 0
            write-buffer $b (cast int32 6) --as int32 --at 4
            echo $"{(read-buffer Pair $b).a} {(read-buffer Pair $b).b}"
            $b | native-free
            """);

        Assert.Equal("5 6", results[^1]?.ToString());
    }

    /// <summary>
    /// <c>offset-of</c> returns an <c>int</c>, like <c>size-of</c> and like its own declared
    /// output.
    /// </summary>
    /// <remarks>
    /// It returned <c>Int64</c>, so <c>(size-of T) + (offset-of T.f)</c> widened — and a width
    /// that comes from a value's type is exactly how a write corrupts a slot. The two are used
    /// together in every offset calculation, so disagreeing about their type put a <c>long</c>
    /// into the middle of the arithmetic that decides layout.
    /// </remarks>
    [Fact]
    public async Task Size_of_and_offset_of_agree_on_their_type()
    {
        var results = await RunAsync("""
            raw struct P {
                a: int
                b: int
            }
            echo (size-of P).GetType().Name
            echo (offset-of P.b).GetType().Name
            echo ((size-of P) + (offset-of P.b)).GetType().Name
            """);

        Assert.Equal("Int32", results[^3]?.ToString());
        Assert.Equal("Int32", results[^2]?.ToString());
        Assert.Equal("Int32", results[^1]?.ToString());
    }

    /// <summary>
    /// <c>alloc</c> returns a <c>NativeBuffer</c>, which is what it declares.
    /// </summary>
    /// <remarks>
    /// It declared <c>IntPtr</c>. The mismatch was silent except as a false
    /// <c>tosh.type.member_not_found</c> on <c>$buffer.Pointer</c> — a warning on code that
    /// works, which is the kind that teaches people to ignore warnings.
    /// </remarks>
    [Fact]
    public async Task Alloc_returns_what_it_declares()
    {
        var results = await RunAsync("""
            var b = (alloc 16)
            echo $b.GetType().Name
            echo $b.ByteLength
            $b | native-free
            """);

        Assert.Equal("NativeBuffer", results[^2]?.ToString());
        Assert.Equal("16", results[^1]?.ToString());
    }
}
