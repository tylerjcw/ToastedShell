using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A class that extends a CLR type, used where that CLR type is expected.
/// </summary>
/// <remarks>
/// <para>
/// Such a class is not a CLR instance of its base — it holds one and forwards to it. That is
/// enough to read a member or override one, and it was not enough to do the thing the base
/// is for: hand the object to the platform. Every CLR call wanting the base failed overload
/// resolution, because nothing at the boundary knew the base was in there.
/// </para>
/// <para>
/// This is not yet a real subclass. A CLR caller receives the contained base, so it sees the
/// base's own members rather than the overrides. What these pin is the part that is true
/// now: the call happens, and the language's own answers about the type agree with each
/// other.
/// </para>
/// </remarks>
public sealed class ClrBaseInteropTests
{
    private static Task<IReadOnlyList<object?>> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        return engine.ExecuteToListAsync(source);
    }

    /// <summary>
    /// The spelling the class was declared with answers true.
    /// </summary>
    /// <remarks>
    /// Only the qualified spelling was wrong, and only a qualified name resolves to a
    /// <see cref="Type"/>: that arrived at the operator as a type rather than a name, and the
    /// CLR's own instance check — correctly — said no. The bare spelling arrives as a string
    /// and walks the class's base chain, so <c>is Uri</c> was true while <c>is System.Uri</c>
    /// was false for the same object.
    /// </remarks>
    [Fact]
    public async Task A_clr_base_is_recognised_by_its_qualified_name()
    {
        var results = await RunAsync(
            """
            class MyUri(url) extends System.Uri($url) { }
            var u = new MyUri("https://example.com/a/b")
            echo ($u is Uri)
            echo ($u is System.Uri)
            echo ($u is MyUri)
            """);

        Assert.Equal(["True", "True", "True"], results.Select(r => r?.ToString()));
    }

    /// <summary>
    /// The base may belong to an ancestor rather than to the class itself.
    /// </summary>
    /// <remarks>
    /// For <c>class Leaf extends Mid</c> over <c>class Mid extends Error</c>, the CLR base is
    /// recorded on <c>Mid</c> and <c>Leaf</c>'s own is null. Reading only the instance's own
    /// definition is the mistake <c>TOAST-0018</c> documents on the <c>is</c> side, and the
    /// conversion boundary can make it just as easily.
    /// </remarks>
    [Fact]
    public async Task A_clr_base_two_levels_up_is_still_the_base()
    {
        var results = await RunAsync(
            """
            class MidErr extends Error { }
            class LeafErr extends MidErr { }
            var e = new LeafErr()
            echo ($e is Exception)
            echo ($e is System.Exception)
            """);

        Assert.Equal(["True", "True"], results.Select(r => r?.ToString()));
    }

    /// <summary>
    /// The object can be passed to a CLR method that requires the base type.
    /// </summary>
    /// <remarks>
    /// The failure this replaces was "No overload matched instance method 'MakeRelativeUri'
    /// on 'System.Uri' with 1 argument(s)" — for an argument whose entire purpose was to be
    /// a <c>Uri</c>.
    /// </remarks>
    [Fact]
    public async Task It_can_be_passed_where_its_clr_base_is_required()
    {
        var results = await RunAsync(
            """
            class MyUri(url) extends System.Uri($url) { }
            var mine = new MyUri("https://example.com/a/b")
            var plain = new System.Uri("https://example.com/")
            echo $plain.MakeRelativeUri($mine)
            """);

        Assert.Equal(["a/b"], results.Select(r => r?.ToString()));
    }

    /// <summary>Extending a CLR type does not cost the class its own members.</summary>
    [Fact]
    public async Task The_tosh_half_of_the_object_survives()
    {
        var results = await RunAsync(
            """
            class MyUri(url) extends System.Uri($url) {
                prop Tag = "mine"
                func Describe() { return $"{$this.Tag}:{$this.Host}" }
            }
            var u = new MyUri("https://example.com/a/b")
            echo $u.Tag
            echo $u.Host
            echo $u.Describe()
            """);

        Assert.Equal(["mine", "example.com", "mine:example.com"], results.Select(r => r?.ToString()));
    }

    /// <summary>
    /// A class with no CLR base is not convertible to an unrelated type.
    /// </summary>
    /// <remarks>
    /// The boundary rescues a conversion that had already failed; it must not invent one.
    /// Without this, "is there a CLR object in here" risks becoming "pass whatever is in
    /// here", and a wrong overload silently binds.
    /// </remarks>
    [Fact]
    public void A_plain_class_instance_is_not_converted_to_an_unrelated_clr_type()
    {
        // Not a string: `"https://example.com/"` converts to a `Uri` and always did, which
        // is a conversion of its own and none of this boundary's business.
        Assert.False(TypeConversion.TryConvert(new object(), typeof(Uri), out _));
        Assert.False(TypeConversion.TryConvert(new object(), typeof(System.IO.Stream), out _));
    }

    /// <summary>
    /// Conversion to the base does not outrank being the thing already.
    /// </summary>
    /// <remarks>
    /// The boundary sits below the exact-instance check for this reason: it can only turn a
    /// failure into a success, never reinterpret a conversion that was going to work.
    /// </remarks>
    [Fact]
    public void An_exact_instance_still_converts_to_itself()
    {
        var uri = new Uri("https://example.com/");

        Assert.True(TypeConversion.TryConvert(uri, typeof(Uri), out var converted));
        Assert.Same(uri, converted);
    }
}
