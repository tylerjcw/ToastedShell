using System.Runtime.InteropServices;

using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The interpreter and the IL emitter each used to carry their own copy of the
/// native-interop type table, the by-ref-string rule, and the calling-convention
/// table. They agreed only by hand, and where they disagreed the compiler
/// degraded to source replay without saying so.
///
/// These tests pin the shared <see cref="NativeTypeLexicon"/> against
/// <see cref="DotNetTypeResolver"/> — the interpreter's actual resolution path —
/// so a future edit to one table that is not mirrored in the other fails here
/// instead of at some caller's runtime.
/// </summary>
public class NativeTypeLexiconTests
{
    /// <summary>
    /// The invariant that would have caught the original duplication: every
    /// name the lexicon claims to resolve must resolve to the *same* CLR type
    /// through the resolver the engine really uses.
    /// </summary>
    [Fact]
    public void Every_lexicon_scalar_matches_the_engine_type_resolver()
    {
        var resolver = new DotNetTypeResolver();
        var mismatches = new List<string>();

        foreach (var name in NativeTypeLexicon.ScalarNames)
        {
            Assert.True(NativeTypeLexicon.TryResolveScalar(name, out var lexiconType),
                $"lexicon failed to resolve its own name '{name}'");

            var resolverType = resolver.Resolve(name);

            if (resolverType != lexiconType)
            {
                mismatches.Add($"{name}: lexicon={lexiconType?.Name ?? "<null>"} resolver={resolverType?.Name ?? "<null>"}");
            }
        }

        Assert.True(mismatches.Count == 0,
            "lexicon and DotNetTypeResolver disagree on: " + string.Join(", ", mismatches));
    }

    /// <summary>
    /// Everything the lexicon resolves must also survive the interop filter,
    /// or a name would be nameable in a signature yet rejected downstream.
    /// </summary>
    [Fact]
    public void Every_lexicon_scalar_is_a_supported_interop_type()
    {
        foreach (var name in NativeTypeLexicon.ScalarNames)
        {
            Assert.True(NativeTypeLexicon.TryResolveScalar(name, out var clrType));
            Assert.True(NativeTypeLexicon.IsSupportedInteropType(clrType),
                $"'{name}' resolves to {clrType.Name}, which the interop filter rejects");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_type_name_is_void(string? name)
    {
        Assert.True(NativeTypeLexicon.TryResolveScalar(name, out var clrType));
        Assert.Equal(typeof(void), clrType);
    }

    [Fact]
    public void Unknown_type_names_do_not_resolve()
    {
        Assert.False(NativeTypeLexicon.TryResolveScalar("struct sysinfo", out _));
        Assert.False(NativeTypeLexicon.TryResolveScalar("nint32", out _));
    }

    [Theory]
    [InlineData("cstring")]
    [InlineData("cstr")]
    [InlineData("CSTRING")]
    public void CString_names_are_recognised_case_insensitively(string name)
    {
        Assert.True(NativeTypeLexicon.IsCStringName(name));
        Assert.True(NativeTypeLexicon.IsStringLikeName(name));
    }

    [Fact]
    public void Plain_string_is_string_like_but_not_a_cstring()
    {
        Assert.False(NativeTypeLexicon.IsCStringName("string"));
        Assert.True(NativeTypeLexicon.IsStringLikeName("string"));
    }

    // --- the by-ref-string rule, previously duplicated across three sites ---

    /// <summary>
    /// A managed `string` still cannot be written back: the marshaller would have to
    /// guess both the callee's encoding and its allocator (`TS-P3-26`).
    /// </summary>
    [Fact]
    public void A_managed_string_by_reference_is_rejected()
    {
        var diagnostic = NativeTypeLexicon.ValidateByRef("string", isByRef: true);

        Assert.NotNull(diagnostic);
        Assert.Equal("tosh.runtime.unsupported_native_byref_string", diagnostic.Code);
        Assert.Contains("cstring", diagnostic.Help, StringComparison.Ordinal);
    }

    /// <summary>
    /// `cstring` by reference is a `char**` and is supported now — the pointer the
    /// callee leaves is decoded, and its bytes are left alone.
    /// </summary>
    [Theory]
    [InlineData("cstring")]
    [InlineData("cstr")]
    public void A_cstring_by_reference_is_accepted(string typeName)
    {
        Assert.Null(NativeTypeLexicon.ValidateByRef(typeName, isByRef: true));
    }

    [Theory]
    [InlineData("string")]
    [InlineData("cstring")]
    public void In_strings_are_accepted(string typeName)
    {
        Assert.Null(NativeTypeLexicon.ValidateByRef(typeName, isByRef: false));
    }

    [Theory]
    [InlineData("int")]
    [InlineData("nint")]
    [InlineData("double")]
    public void By_ref_non_strings_are_accepted(string typeName)
    {
        Assert.Null(NativeTypeLexicon.ValidateByRef(typeName, isByRef: true));
    }

    /// <summary>
    /// `cstring` and `string` fail for different reasons, so they get different
    /// help text — the cstring case is about ownership, the string case about
    /// input-only support.
    /// </summary>
    [Fact]
    public void The_rejection_points_at_the_spelling_that_works()
    {
        var plain = NativeTypeLexicon.ValidateByRef("string", isByRef: true);

        Assert.NotNull(plain?.Help);

        // The help has to name `cstring`, since that is now the answer rather than a
        // second thing that also fails.
        Assert.Contains("cstring", plain.Help, StringComparison.Ordinal);
        Assert.Contains("cstring", plain.Label, StringComparison.Ordinal);
    }

    // --- calling conventions ---

    [Theory]
    [InlineData("cdecl", CallingConvention.Cdecl)]
    [InlineData("stdcall", CallingConvention.StdCall)]
    [InlineData("thiscall", CallingConvention.ThisCall)]
    [InlineData("fastcall", CallingConvention.FastCall)]
    [InlineData("winapi", CallingConvention.Winapi)]
    [InlineData("CDECL", CallingConvention.Cdecl)]
    public void Calling_conventions_resolve(string name, CallingConvention expected)
    {
        Assert.True(NativeTypeLexicon.TryResolveCallingConvention(name, out var convention));
        Assert.Equal(expected, convention);
    }

    [Fact]
    public void Missing_calling_convention_defaults_to_cdecl()
    {
        Assert.True(NativeTypeLexicon.TryResolveCallingConvention(null, out var convention));
        Assert.Equal(CallingConvention.Cdecl, convention);
    }

    /// <summary>
    /// The emitter used to silently fall back to Cdecl for an unknown name
    /// while the engine threw, so `callconv bogus` compiled to something the
    /// interpreter would have rejected.
    /// </summary>
    [Fact]
    public void Unknown_calling_convention_does_not_silently_default()
    {
        Assert.False(NativeTypeLexicon.TryResolveCallingConvention("bogus", out _));
    }
}
