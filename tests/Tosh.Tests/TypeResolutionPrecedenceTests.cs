using System.Reflection;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// An explicit <c>using</c> outranks an incidental match — <c>TS-P2-66</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Resolve</c> asked the unqualified global scan first and consulted the imports only when it
/// found nothing. That scan searches the platform index and every loaded assembly, private nested
/// implementation types included, so a stated intention lost to whatever the runtime happened to
/// be holding: <c>Complex</c> resolved to
/// <c>System.Threading.PortableThreadPool+HillClimbing+Complex</c> rather than
/// <c>System.Numerics.Complex</c>, and <c>BigInteger</c> to <c>System.Number+BigInteger</c>.
/// </para>
/// <para>
/// <b>Measured before it was changed</b>, because the reorder touches every type name in the
/// language and <c>DefaultImplicitUsings</c> carries a dozen namespaces. Across the 16,727 simple
/// names the platform index knows: 33 resolve differently under the two orders, every one of them
/// toward the public type an import names; 15,544 resolve only through the direct scan and are
/// untouched, since imports-first falls through to it; and none resolve through imports alone. So
/// the blast radius is 33 names, all improvements.
/// </para>
/// <para>
/// A *qualified* name is already an instruction about where to look, so it keeps direct
/// resolution first. Found while fixing <c>TS-P2-48</c>, where an emitted test type captured a
/// name an import had asked for.
/// </para>
/// </remarks>
public sealed class TypeResolutionPrecedenceTests
{
    [Theory]
    // Each of these resolved to a private nested implementation type before the reorder, and
    // each fails against the unfixed resolver. `BigInteger` is deliberately not here: the direct
    // scan disagreed with the imports for it, but `Resolve` already answered correctly by
    // another route, so a case for it would pass either way and prove nothing.
    //
    // `FileStatus` was a case here and is not any more. It expected `System.IO.FileStatus`,
    // which is *internal* — the reorder moved it from one implementation detail to another,
    // which was an improvement but not the answer. `TOAST-0078` stopped the resolver returning
    // types a script cannot legally name, so `FileStatus` now resolves to nothing at all, and
    // the test below says so.
    [InlineData("SpinLock", "System.Threading.SpinLock")]
    public void An_unqualified_name_prefers_the_type_an_import_names(string name, string expected)
    {
        var resolved = new DotNetTypeResolver().Resolve(name);

        Assert.NotNull(resolved);
        Assert.Equal(expected, resolved!.FullName);
    }

    /// <summary>
    /// A name that only matches a non-public type resolves to nothing — <c>TOAST-0078</c>.
    /// </summary>
    /// <remarks>
    /// Both of these exist in <c>System.Private.CoreLib</c> and both used to resolve.
    /// <c>Sys</c> is the one that cost something: it is <c>Interop+Sys</c>, so every
    /// <c>Sys.Math.Clamp</c> in a script failed with *"Static member 'Math' was not found on
    /// type 'Interop+Sys'"* — a type the author had never heard of, named in an error about
    /// code that looked right.
    /// </remarks>
    [Theory]
    [InlineData("Sys")]
    [InlineData("Interop")]
    [InlineData("FileStatus")]
    public void A_name_that_only_matches_a_non_public_type_does_not_resolve(string name)
    {
        Assert.Null(new DotNetTypeResolver().Resolve(name));
    }

    [Fact]
    public void The_reorder_never_loses_a_resolution()
    {
        // The measurement that justified the change, kept as a guard: no name resolves through
        // the imports alone, so putting them first cannot take an answer away — it can only
        // change which of two answers is given.
        var resolver = new DotNetTypeResolver();
        var resolverType = typeof(DotNetTypeResolver);
        var direct = resolverType.GetMethod("TryResolveDirect", BindingFlags.NonPublic | BindingFlags.Static)!;

        var names = DotNetTypeResolver.GetKnownTypes()
            .Select(type => type.Name)
            .Where(name => !name.Contains('`'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // A sample rather than all 16,727: the full sweep took 40 seconds, and the property
            // being guarded is uniform across the corpus. The whole set was measured once, by
            // hand, when the change was made.
            .Take(600)
            .ToArray();

        foreach (var name in names)
        {
            object?[] arguments = [name, null];

            if ((bool)direct.Invoke(null, arguments)!)
            {
                Assert.NotNull(resolver.Resolve(name));
            }
        }
    }

    [Fact]
    public void A_qualified_name_still_resolves_directly()
    {
        var resolver = new DotNetTypeResolver();

        Assert.Equal("System.IO.Path", resolver.Resolve("System.IO.Path")?.FullName);
        Assert.Equal("System.Text.StringBuilder", resolver.Resolve("System.Text.StringBuilder")?.FullName);
    }

    [Fact]
    public void A_declared_using_beats_the_implicit_ones()
    {
        // Precedence among imports is first-wins, and an explicitly added namespace is consulted
        // in the order it was added — which is what makes `using` a statement about intent.
        var resolver = new DotNetTypeResolver(includeDefaultUsings: false);
        resolver.AddUsing("System.Numerics");

        Assert.Equal("System.Numerics.Complex", resolver.Resolve("Complex")?.FullName);
    }

    [Theory]
    // The everyday names, none of which move.
    [InlineData("StringBuilder", "System.Text.StringBuilder")]
    [InlineData("Regex", "System.Text.RegularExpressions.Regex")]
    [InlineData("Uri", "System.Uri")]
    [InlineData("Path", "System.IO.Path")]
    [InlineData("DateTime", "System.DateTime")]
    [InlineData("BigInteger", "System.Numerics.BigInteger")]
    public void The_names_people_actually_write_are_unchanged(string name, string expected)
    {
        Assert.Equal(expected, new DotNetTypeResolver().Resolve(name)?.FullName);
    }

    [Fact]
    public void A_builtin_alias_still_wins_over_everything()
    {
        // Aliases are consulted before either path, so `int` is `System.Int32` regardless.
        var resolver = new DotNetTypeResolver();

        Assert.Equal(typeof(int), resolver.Resolve("int"));
        Assert.Equal(typeof(string), resolver.Resolve("string"));
    }

    [Fact]
    public void A_constructed_generic_follows_the_same_precedence()
    {
        var resolver = new DotNetTypeResolver();
        var resolved = resolver.Resolve("list<int>");

        Assert.NotNull(resolved);
        Assert.Equal(typeof(List<int>), resolved);
    }

    [Fact]
    public void A_name_that_exists_nowhere_still_resolves_to_nothing()
    {
        Assert.Null(new DotNetTypeResolver().Resolve("NoSuchTypeAnywhereInThisProcess"));
    }
}
