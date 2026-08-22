using System.Text;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A qualified type name is answered without building the platform index — `TOAST-0064`.
/// </summary>
/// <remarks>
/// <para>
/// Resolving any CLR type name used to wait for an index of every type in every assembly on
/// the trusted-platform list — 17,139 fully qualified names and 14,851 simple ones. It is
/// built on a background task, so a script that never names a CLR type pays nothing and one
/// that names a single type waits for all of it. Measured against a published build: a
/// one-line script annotating a parameter `string` starts in 111 ms, and the same script
/// annotating it `System.Text.StringBuilder` took 216 ms.
/// </para>
/// <para>
/// A qualified name is already an instruction about where to look, and every loaded assembly
/// can answer one with a lookup of its own — <c>Assembly.GetType</c> is a hash probe, not a
/// scan. Asking them first takes that case to 110 ms, level with the alias.
/// </para>
/// <para>
/// These are correctness tests rather than timing ones. The risk in asking a different
/// source first is that it gives a different answer, and that is what they pin.
/// </para>
/// </remarks>
public sealed class QualifiedTypeResolutionTests
{
    /// <summary>A qualified name resolves to the type it names.</summary>
    [Theory]
    [InlineData("System.Text.StringBuilder", typeof(StringBuilder))]
    [InlineData("System.IO.FileInfo", typeof(FileInfo))]
    [InlineData("System.Collections.Hashtable", typeof(System.Collections.Hashtable))]
    [InlineData("System.Dynamic.ExpandoObject", typeof(System.Dynamic.ExpandoObject))]
    [InlineData("System.Uri", typeof(Uri))]
    public void A_qualified_name_resolves_to_that_type(string name, Type expected)
    {
        Assert.True(DotNetTypeResolver.TryResolveKnownType(name, out var resolved));
        Assert.Equal(expected, resolved);
    }

    /// <summary>
    /// A simple name still comes from the index, and still answers what it answered.
    /// </summary>
    /// <remarks>
    /// The control that matters. `TS-P2-66` measured the simple-name answers across all
    /// 16,727 of them and pinned an order, because the obvious scan resolves `Complex` to
    /// `System.Threading.PortableThreadPool+HillClimbing+Complex` and `BigInteger` to
    /// `System.Number+BigInteger`. The fast path is deliberately restricted to names
    /// containing a dot so that none of that is disturbed.
    /// </remarks>
    [Theory]
    [InlineData("StringBuilder", typeof(StringBuilder))]
    [InlineData("Uri", typeof(Uri))]
    [InlineData("Hashtable", typeof(System.Collections.Hashtable))]
    public void A_simple_name_still_resolves_through_the_index(string name, Type expected)
    {
        Assert.True(DotNetTypeResolver.TryResolveKnownType(name, out var resolved));
        Assert.Equal(expected, resolved);
    }

    /// <summary>A name that is not a CLR type is still not one.</summary>
    /// <remarks>
    /// The negative case, and the one still paying for the index: proving a name absent
    /// means consulting assemblies that have not been loaded. A ToastScript-declared type
    /// such as `ToastLib.Filesystem.DirectoryName` looks exactly like this.
    /// </remarks>
    [Theory]
    [InlineData("ToastLib.Filesystem.DirectoryName")]
    [InlineData("No.Such.Namespace.NoSuchType")]
    public void A_name_that_is_not_a_clr_type_does_not_resolve(string name)
    {
        Assert.False(DotNetTypeResolver.TryResolveKnownType(name, out var resolved));
        Assert.Null(resolved);
    }

    /// <summary>Asking twice gives the same answer.</summary>
    /// <remarks>
    /// The fast path caches per name against the loaded-assembly generation, so this guards
    /// the cache returning something different — or nothing — on a second look.
    /// </remarks>
    [Fact]
    public void Repeating_a_lookup_is_stable()
    {
        Assert.True(DotNetTypeResolver.TryResolveKnownType("System.Text.StringBuilder", out var first));
        Assert.True(DotNetTypeResolver.TryResolveKnownType("System.Text.StringBuilder", out var second));
        Assert.Same(first, second);

        Assert.False(DotNetTypeResolver.TryResolveKnownType("No.Such.Namespace.NoSuchType", out _));
        Assert.False(DotNetTypeResolver.TryResolveKnownType("No.Such.Namespace.NoSuchType", out _));
    }

    /// <summary>
    /// Names spread across the sort order all resolve to themselves — `TOAST-0064`.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The platform index is cached between runs as a **sorted** record file, searched in
    /// place rather than parsed into a dictionary — building a 32,000-entry dictionary costs
    /// about 60 ms, which is most of what the cache exists to save. What that buys is a
    /// binary search, and what a binary search gets wrong is edges: the first record, the
    /// last, and the midpoint landing inside a record rather than on its boundary.
    /// </para>
    /// <para>
    /// These names are chosen to spread across that ordering rather than to be interesting
    /// in themselves. A search that lands on the wrong record answers a *different type*
    /// rather than failing, so asserting the exact type is the point.
    /// </para>
    /// <para>
    /// Which path answers depends on whether a cache exists for this machine's framework —
    /// the first suite run on a machine builds one, later runs read it, so both are covered
    /// across runs rather than within one. The answers must not differ either way, which is
    /// the property being pinned.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("System.Action", typeof(Action))]
    [InlineData("System.Boolean", typeof(bool))]
    [InlineData("System.DateTime", typeof(DateTime))]
    [InlineData("System.Guid", typeof(Guid))]
    [InlineData("System.IO.Path", typeof(Path))]
    [InlineData("System.Int32", typeof(int))]
    [InlineData("System.Numerics.BigInteger", typeof(System.Numerics.BigInteger))]
    [InlineData("System.Object", typeof(object))]
    [InlineData("System.Reflection.Assembly", typeof(System.Reflection.Assembly))]
    [InlineData("System.String", typeof(string))]
    [InlineData("System.Threading.Tasks.Task", typeof(Task))]
    [InlineData("System.TimeSpan", typeof(TimeSpan))]
    [InlineData("System.Version", typeof(Version))]
    public void Names_across_the_sort_order_resolve_to_themselves(string name, Type expected)
    {
        Assert.True(DotNetTypeResolver.TryResolveKnownType(name, out var resolved), name);
        Assert.Equal(expected, resolved);
    }

    /// <summary>
    /// Neighbouring names are told apart.
    /// </summary>
    /// <remarks>
    /// A binary search that stops one record early or late still returns *a* type, and
    /// these are the pairs where that would be least visible: the same namespace, adjacent
    /// in ordering, and both real.
    /// </remarks>
    [Theory]
    [InlineData("System.IO.File", typeof(File))]
    [InlineData("System.IO.FileInfo", typeof(FileInfo))]
    [InlineData("System.IO.FileMode", typeof(FileMode))]
    [InlineData("System.IO.Directory", typeof(Directory))]
    [InlineData("System.IO.DirectoryInfo", typeof(DirectoryInfo))]
    public void Adjacent_names_are_not_confused(string name, Type expected)
    {
        Assert.True(DotNetTypeResolver.TryResolveKnownType(name, out var resolved), name);
        Assert.Equal(expected, resolved);
    }

    /// <summary>An annotation naming a qualified CLR type is still inferred as that type.</summary>
    /// <remarks>
    /// The end-to-end shape `TOAST-0034` added the index lookup for, asserted here so the
    /// reorder cannot quietly cost the inference it was added to provide.
    /// </remarks>
    [Fact]
    public async Task A_qualified_annotation_is_still_inferred()
    {
        var engine = new Tosh.Language.ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(
            "var h: System.Collections.Hashtable = new System.Collections.Hashtable()\necho $h.Count");

        Assert.Equal("0", results[^1]?.ToString());
    }
}
