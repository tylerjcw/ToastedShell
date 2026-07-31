using System.Runtime.InteropServices;

namespace Tosh.Runtime;

/// <summary>
/// One field of a <c>raw struct</c>, resolved down to exactly what a
/// <see cref="System.Reflection.Emit.TypeBuilder"/> needs and nothing more.
/// </summary>
/// <param name="Name">Field name as written in the declaration.</param>
/// <param name="ClrType">
/// The managed type of the emitted field. For a fixed C array this is the
/// element type's array (<c>ulong[]</c>), and for an inline char buffer it is
/// <see cref="string"/> — the <see cref="MarshalAs"/> entry is what makes
/// either sit inline rather than becoming a pointer.
/// </param>
/// <param name="MarshalAs">Unmanaged shape, or <c>null</c> for a plain field.</param>
/// <param name="SizeConst">Element count for a fixed array or inline char buffer.</param>
/// <param name="ArraySubType">Element type for <see cref="UnmanagedType.ByValArray"/>.</param>
public sealed record RawStructFieldPlan(
    string Name,
    Type ClrType,
    UnmanagedType? MarshalAs = null,
    int? SizeConst = null,
    UnmanagedType? ArraySubType = null);

/// <summary>
/// The layout decision for a <c>raw struct</c>, computed once from the
/// declaration and consumed by <em>both</em> emitters — the interpreter's
/// runtime Reflection.Emit factory and the compiler's persisted-assembly
/// emitter.
///
/// This type exists specifically so there is only one implementation of the
/// layout algorithm. Two emitters computing offsets independently is the
/// failure mode most likely to produce a silent mismatch between the
/// interpreted and compiled tiers, and it is the one that would be hardest to
/// notice: both tiers would run, and only one would read the right bytes.
/// Tests assert against the <em>plan</em> rather than emitted IL, so the shared
/// decision is what gets pinned.
///
/// Note what is deliberately absent: padding. <see cref="LayoutKind.Sequential"/>
/// aligns each field naturally, exactly as a C compiler does, so declarations
/// transcribe the header and never restate its <c>pad</c> members.
/// </summary>
public sealed record RawStructLayoutPlan(
    string Name,
    LayoutKind Kind,
    IReadOnlyList<RawStructFieldPlan> Fields,
    int? Pack = null,
    int? DeclaredSize = null)
{
    /// <summary>
    /// Structural identity, used to cache emitted types. Caching by
    /// <em>name</em> instead would mint two incompatible CLR types for the same
    /// declaration whenever a module is re-required or a file is re-sourced in
    /// the REPL, and the resulting <c>Marshal.StructureToPtr</c> failure names
    /// neither cause.
    /// </summary>
    public string StructuralKey =>
        $"{Name}|{Kind}|pack={Pack?.ToString() ?? "-"}|size={DeclaredSize?.ToString() ?? "-"}|" +
        string.Join(",", Fields.Select(static f =>
            $"{f.Name}:{f.ClrType.FullName}:{f.MarshalAs?.ToString() ?? "-"}:" +
            $"{f.SizeConst?.ToString() ?? "-"}:{f.ArraySubType?.ToString() ?? "-"}"));
}
