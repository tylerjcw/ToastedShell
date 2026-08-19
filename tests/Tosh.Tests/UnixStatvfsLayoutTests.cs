using System.Reflection;
using System.Runtime.InteropServices;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The marshalled <c>struct statvfs</c> is the size the platform's is.
///
/// It declared eleven `ulong` fields — 88 bytes — while glibc's struct is **112**, so every
/// `statvfs()` call wrote 24 bytes past the end of the buffer. The field offsets were
/// right, so the readings were correct and only the memory *after* them was destroyed,
/// which is why it survived so long.
///
/// It surfaced as an `AccessViolationException` inside `TryGetInodeInfo` in the **published**
/// single-file build, deterministically, while a Release build of the same source ran
/// clean. That difference is luck about what sits after the buffer, not a difference in
/// correctness — and it is why a "does it still work?" check run against a Release build
/// could not have found it.
///
/// Asserted by size rather than by running `df`, because the symptom of getting this wrong
/// is memory corruption: a test that merely called the function would pass while corrupting
/// whatever came next.
/// </summary>
public sealed class UnixStatvfsLayoutTests
{
    private static Type StatvfsType =>
        typeof(UnixSystemServices)
            .GetNestedType("Statvfs", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("UnixSystemServices.Statvfs was not found — has it been renamed?");

    /// <summary>
    /// 112 bytes, as reported by the platform. If a future glibc grows the struct this test
    /// is the thing that should fail, rather than a shell that crashes somewhere else.
    /// </summary>
    [Fact]
    public void The_marshalled_struct_matches_the_platform_size()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Equal(112, Marshal.SizeOf(StatvfsType));
    }

    /// <summary>
    /// The fields that are actually read stay where the native struct puts them — the half
    /// that was already right, pinned so that adding the padding cannot have moved them.
    /// </summary>
    [Theory]
    [InlineData("f_files", 40)]
    [InlineData("f_ffree", 48)]
    [InlineData("f_flag", 72)]
    [InlineData("f_namemax", 80)]
    public void The_read_fields_keep_their_offsets(string field, int expectedOffset)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Equal(expectedOffset, (int)Marshal.OffsetOf(StatvfsType, field));
    }
}
