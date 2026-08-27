using System.Reflection;
using System.Runtime.InteropServices;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Every struct handed to native code is the size the platform's is.
///
/// `TOSH-0007`: `Statvfs` declared eleven `ulong` fields — 88 bytes — while glibc's
/// `struct statvfs` is **112**, so every call wrote 24 bytes past the buffer. The field
/// offsets were all correct, so every value read back was right and only the memory *after*
/// the struct was destroyed. It surfaced as an `AccessViolationException` in the published
/// single-file build, deterministically, and never in a Release build of the same source —
/// which is luck about what sits after the buffer, not a difference in correctness.
///
/// **Asserted by size, not by calling the function.** The symptom of getting this wrong is
/// memory corruption: a test that merely invoked the import would pass while destroying
/// whatever came next. Only the declaration can be checked safely.
///
/// The expected values were measured on this platform rather than reasoned about, with a C
/// program including the real headers:
///
/// <code>
/// termios   60   sigaction 152   timespec 16   stat    144
/// passwd    48   group      32   statvfs 112   utsname 390   rlimit 16
/// TSNode    32   TSPoint     8   TSInputEdit 36   TSTreeCursor 32
/// </code>
///
/// A failure here means either a declaration drifted or the platform's struct changed. Both
/// want a human; neither should be discovered by a shell dying mid-session.
/// </summary>
public sealed class NativeStructLayoutTests
{
    private static Type Nested(Type owner, string name) =>
        owner.GetNestedType(name, BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException($"{owner.Name}.{name} was not found — has it been renamed?");

    private static Type Runtime(string typeName, string nested)
    {
        var owner = typeof(OperatorEvaluator).Assembly.GetType($"Tosh.Runtime.{typeName}")
            ?? throw new InvalidOperationException($"Tosh.Runtime.{typeName} was not found.");

        // Some interop structs sit inside a private nested holder class.
        foreach (var candidate in owner.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (candidate.Name == nested)
            {
                return candidate;
            }

            var deeper = candidate.GetNestedType(nested, BindingFlags.NonPublic | BindingFlags.Public);

            if (deeper is not null)
            {
                return deeper;
            }
        }

        throw new InvalidOperationException($"{typeName}.{nested} was not found — has it been renamed?");
    }

    public static TheoryData<string, string, int> LinuxStructs => new()
    {
        { "UnixSystemServices", "Statvfs", 112 },
        { "UnixSystemServices", "UtsName", 390 },
        { "UnixSystemServices", "Passwd", 48 },
        { "UnixSystemServices", "Group", 32 },
        { "UnixFileSystemMetadata", "Timespec", 16 },
        { "UnixFileSystemMetadata", "Stat", 144 },
        { "UnixFileSystemMetadata", "Passwd", 48 },
        { "UnixFileSystemMetadata", "PosixGroup", 32 },
        { "UnixOwnershipUtilities", "Passwd", 48 },
        { "UnixOwnershipUtilities", "Group", 32 },
    };

    [Theory]
    [MemberData(nameof(LinuxStructs))]
    public void A_marshalled_linux_struct_matches_the_platform(string owner, string nested, int expectedSize)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Equal(expectedSize, Marshal.SizeOf(Runtime(owner, nested)));
    }

    /// <summary>
    /// `Termios` and `SigAction` are public, so they are reached directly rather than by
    /// name. Both are the shape a terminal depends on: getting either wrong corrupts memory
    /// on every raw-mode transition.
    /// </summary>
    [Fact]
    public void The_terminal_structs_match_the_platform()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Equal(60, Marshal.SizeOf<Termios>());
        Assert.Equal(152, Marshal.SizeOf<SigAction>());
    }

    /// <summary>
    /// The fields actually read out of `stat` and `statvfs` stay where the native structs
    /// put them. Size alone would not catch a field moving *within* a correctly sized
    /// struct, which reads wrong values rather than corrupting memory — quieter, and just
    /// as wrong.
    /// </summary>
    [Theory]
    [InlineData("UnixFileSystemMetadata", "Stat", "st_mode", 24)]
    [InlineData("UnixFileSystemMetadata", "Stat", "st_uid", 28)]
    [InlineData("UnixFileSystemMetadata", "Stat", "st_size", 48)]
    [InlineData("UnixFileSystemMetadata", "Stat", "st_mtim", 88)]
    [InlineData("UnixSystemServices", "Statvfs", "f_files", 40)]
    [InlineData("UnixSystemServices", "Statvfs", "f_ffree", 48)]
    [InlineData("UnixSystemServices", "Statvfs", "f_flag", 72)]
    [InlineData("UnixSystemServices", "Statvfs", "f_namemax", 80)]
    [InlineData("UnixFileSystemMetadata", "Passwd", "pw_uid", 16)]
    [InlineData("UnixFileSystemMetadata", "Passwd", "pw_dir", 32)]
    [InlineData("UnixFileSystemMetadata", "PosixGroup", "gr_mem", 24)]
    public void A_read_field_keeps_its_platform_offset(string owner, string nested, string field, int expectedOffset)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Equal(expectedOffset, (int)Marshal.OffsetOf(Runtime(owner, nested), field));
    }

    /// <summary>
    /// `NativeBuffer`'s range check cannot be defeated by integer overflow.
    /// </summary>
    /// <remarks>
    /// It read `offset + count > ByteLength`, which wraps: `native-alloc` is script-reachable,
    /// so a script chooses `ByteLength`, and a buffer near `int.MaxValue` lets a large offset
    /// and count sum to a negative number that satisfies the check — handing `Marshal.Copy`
    /// a range outside the allocation.
    ///
    /// Asserted on a small buffer with values that would wrap, so the test needs no
    /// multi-gigabyte allocation to pin the arithmetic.
    ///
    /// **The exception type is the assertion.** A first version used `ThrowsAny` and passed
    /// with the fix reverted: the overflowing check let the call through, and
    /// `new byte[int.MaxValue]` then threw `OutOfMemoryException` — a throw, but from the
    /// allocator rather than the bounds check, and by then the range had already been
    /// accepted. Only `InvalidOperationException` comes from `ValidateRange`.
    /// </remarks>
    [Fact]
    public void A_native_buffer_range_check_survives_integer_overflow()
    {
        using var buffer = new NativeBuffer(16);

        // An offset past the end is refused. It reports as a *range* error rather than an
        // argument one, because `ValidateRange` bounds `offset` only against zero — the
        // range comparison is what catches an offset past the end.
        Assert.Throws<InvalidOperationException>(() => buffer.ReadBytes(1, offset: 17));

        // And a count that would wrap when added to a legal offset is refused *by the range
        // check*, rather than summing to a negative number, passing, and failing later
        // somewhere else.
        var overflowing = Assert.Throws<InvalidOperationException>(
            () => buffer.ReadBytes(int.MaxValue, offset: 8));

        Assert.Contains("exceeds native buffer size", overflowing.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => buffer.ReadBytes(int.MaxValue - 4, offset: 8));
    }
}
