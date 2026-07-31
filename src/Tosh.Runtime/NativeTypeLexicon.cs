using System.Runtime.InteropServices;

namespace Tosh.Runtime;

/// <summary>
/// Single source of truth for the native-interop type surface, shared by the
/// interpreter (<c>Tosh.Language</c>) and the IL emitter (<c>Tosh.Compiler</c>).
///
/// Before this existed the two tiers each carried their own copy of the type
/// table, the by-ref-string rule, and the calling-convention table. They agreed
/// by hand, and where they disagreed the compiler degraded silently to source
/// replay instead of reporting the mismatch.
///
/// This lives in <c>Tosh.Runtime</c> because it is the only assembly both tiers
/// already reference. It deliberately does <em>not</em> mention
/// <c>NativeParameterPassingMode</c> — that enum lives in
/// <c>Tosh.Compiler.IR</c>, which references this assembly, so taking it here
/// would be circular. Callers project the mode down to <c>isByRef</c>.
/// </summary>
public static class NativeTypeLexicon
{
    /// <summary>
    /// Type names both tiers resolve without consulting a type resolver.
    /// Mirrors the interop-relevant subset of
    /// <see cref="DotNetTypeResolver"/>'s alias table; the round-trip between
    /// the two is covered by a table-driven test.
    /// </summary>
    private static readonly Dictionary<string, Type> Scalars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["void"] = typeof(void),
        ["bool"] = typeof(bool),
        ["byte"] = typeof(byte),
        ["sbyte"] = typeof(sbyte),
        ["short"] = typeof(short),
        ["ushort"] = typeof(ushort),
        ["int"] = typeof(int),
        ["uint"] = typeof(uint),
        ["long"] = typeof(long),
        ["ulong"] = typeof(ulong),
        ["char"] = typeof(char),
        ["float"] = typeof(float),
        ["double"] = typeof(double),
        ["nint"] = typeof(IntPtr),
        ["intptr"] = typeof(IntPtr),
        ["ptr"] = typeof(IntPtr),
        ["nuint"] = typeof(UIntPtr),
        ["uintptr"] = typeof(UIntPtr),
        ["uptr"] = typeof(UIntPtr),
        ["string"] = typeof(string),
        ["cstring"] = typeof(string),
        ["cstr"] = typeof(string),
    };

    private static readonly Dictionary<string, CallingConvention> CallingConventions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cdecl"] = CallingConvention.Cdecl,
        ["stdcall"] = CallingConvention.StdCall,
        ["thiscall"] = CallingConvention.ThisCall,
        ["fastcall"] = CallingConvention.FastCall,
        ["winapi"] = CallingConvention.Winapi,
    };

    /// <summary>Every name in the shared table, for conformance tests.</summary>
    public static IReadOnlyCollection<string> ScalarNames => Scalars.Keys;

    /// <summary>
    /// Resolves a scalar/pointer/string interop type name. A null or empty name
    /// is <c>void</c>, matching an omitted return annotation.
    /// </summary>
    public static bool TryResolveScalar(string? name, out Type clrType)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            clrType = typeof(void);
            return true;
        }

        return Scalars.TryGetValue(name.Trim(), out clrType!);
    }

    /// <summary>A borrowed NUL-terminated C string: <c>cstring</c> or <c>cstr</c>.</summary>
    public static bool IsCStringName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Trim() is var trimmed &&
        (trimmed.Equals("cstring", StringComparison.OrdinalIgnoreCase) ||
         trimmed.Equals("cstr", StringComparison.OrdinalIgnoreCase));

    /// <summary>Any name that marshals as a string: <c>string</c>, <c>cstring</c>, <c>cstr</c>.</summary>
    public static bool IsStringLikeName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name.Trim().Equals("string", StringComparison.OrdinalIgnoreCase) || IsCStringName(name));

    public static bool TryResolveCallingConvention(string? name, out CallingConvention convention)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            convention = CallingConvention.Cdecl;
            return true;
        }

        return CallingConventions.TryGetValue(name.Trim(), out convention);
    }

    /// <summary>
    /// The interop filter: which CLR types can cross the native boundary.
    /// Forwarded from <see cref="NativeInteropUtilities.IsSupportedInteropType"/>,
    /// which remains the implementation so existing callers keep working.
    /// </summary>
    public static bool IsSupportedInteropType(Type type, bool allowString = true) =>
        NativeInteropUtilities.IsSupportedInteropType(type, allowString);

    /// <summary>
    /// The by-ref-string rule, previously duplicated across three sites.
    /// Returns a diagnostic when the combination is rejected, else <c>null</c>.
    ///
    /// <c>out</c>/<c>ref</c> string marshalling has no ownership story — the
    /// callee would have to allocate with an allocator the marshaller cannot
    /// know — so it needs an explicit pointer type instead.
    /// </summary>
    public static ToshDiagnostic? ValidateByRef(
        string? typeName,
        bool isByRef,
        string? sourceName = null,
        string? sourceText = null,
        TextSpan? span = null)
    {
        if (!isByRef || !IsStringLikeName(typeName))
        {
            return null;
        }

        var help = IsCStringName(typeName)
            ? "borrowed `cstring` works for input parameters and returns, but `out`/`ref` string marshalling is not supported yet."
            : "plain `string` is only supported for input parameters today.";

        return new ToshDiagnostic(
            Code: "tosh.runtime.unsupported_native_byref_string",
            Title: "By-ref native string parameters need an explicit pointer type.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: "use 'nint', 'ptr', or a buffer-backed struct type here",
            Help: help);
    }
}
