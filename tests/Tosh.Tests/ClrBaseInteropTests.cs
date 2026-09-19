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
    /// A CLR caller invoking a virtual runs the class's override.
    /// </summary>
    /// <remarks>
    /// The whole point of the emitted subclass. Before it, a class could declare
    /// <c>func ToString()</c>, see it honoured everywhere in the language, and see it ignored
    /// by the first CLR caller that asked — what crossed the boundary was the plain base
    /// object, which had never heard of the class.
    /// </remarks>
    [Fact]
    public async Task A_clr_caller_runs_the_classs_override()
    {
        var results = await RunAsync(
            """
            class MyUri(url) extends System.Uri($url) {
                func ToString() { return "OVERRIDDEN" }
            }
            var u = new MyUri("https://example.com/a/b")
            var crossed = new System.Collections.Generic.List<System.Uri>([$u])
            echo $crossed[0].ToString()
            """);

        Assert.Equal(["OVERRIDDEN"], results.OfType<string>());
    }

    /// <summary>
    /// <c>$super</c> reaches the base implementation rather than the override.
    /// </summary>
    /// <remarks>
    /// The override is what the base's own name now resolves to, so calling it from inside
    /// the override is infinite recursion — <c>$super.ToString()</c> inside
    /// <c>func ToString()</c> recursed until the depth guard stopped it. Each override is
    /// paired with a non-virtual thunk that reaches past it.
    /// </remarks>
    [Fact]
    public async Task Super_reaches_the_base_implementation_without_recursing()
    {
        var results = await RunAsync(
            """
            class MyUri(url) extends System.Uri($url) {
                func ToString() { return $"MINE<{ $super.ToString() }>" }
            }
            var u = new MyUri("https://example.com/a/b")
            var crossed = new System.Collections.Generic.List<System.Uri>([$u])
            echo $u.ToString()
            echo $crossed[0].ToString()
            """);

        Assert.Equal(
            ["MINE<https://example.com/a/b>", "MINE<https://example.com/a/b>"],
            results.OfType<string>());
    }

    /// <summary>
    /// A value-returning override answers the CLR with a value, not a box it cannot read.
    /// </summary>
    /// <remarks>
    /// The emitted override unboxes what the dispatch returns into the declared return type,
    /// so a member returning <c>int</c> has to come back as one. <c>HashSet&lt;object&gt;</c>
    /// is the check that the platform is really using both: it calls <c>GetHashCode</c> and
    /// then <c>Equals</c>, and collapses the two entries only if both answered.
    /// </remarks>
    [Fact]
    public async Task A_value_returning_override_answers_the_platform()
    {
        var results = await RunAsync(
            """
            class FixedUri(url) extends System.Uri($url) {
                func GetHashCode() { return 7 }
                func Equals(other) { return true }
            }
            var a = new FixedUri("https://example.com/one")
            var b = new FixedUri("https://example.com/two")
            # Through a Uri-typed list first, so what the set receives is what crossed the
            # boundary. A set of `object` would take the tosh instances themselves and never
            # reach an emitted override at all.
            var crossed = new System.Collections.Generic.List<System.Uri>([$a, $b])
            var set = new System.Collections.Generic.HashSet<System.Object>([$crossed[0], $crossed[1]])
            echo $set.Count
            """);

        Assert.Equal(["1"], results.Select(r => r?.ToString()));
    }

    /// <summary>
    /// A class that overrides nothing still constructs and still forwards.
    /// </summary>
    /// <remarks>
    /// Emitting is worth doing only where there is something to override; with nothing, the
    /// plain base is constructed as before. That path is the same one a sealed base or a
    /// runtime without Reflection.Emit takes, so it has to keep working.
    /// </remarks>
    [Fact]
    public async Task A_class_with_nothing_to_override_still_works()
    {
        var results = await RunAsync(
            """
            class Plain(url) extends System.Uri($url) { prop Tag = "t" }
            var p = new Plain("https://example.com/a/b")
            echo $p.Host
            echo $p.Tag
            echo ($p is System.Uri)
            """);

        Assert.Equal(["example.com", "t", "True"], results.Select(r => r?.ToString()));
    }

    /// <summary>
    /// A generic CLR base is closed over the type arguments that were written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name and its type arguments are parsed separately, and only the tōsh-class branch
    /// was putting them back together — a CLR base was recorded <em>open</em>, as
    /// <c>Comparer`1</c>. Nothing can construct or derive from an open generic, so every
    /// generic CLR base failed with "No constructor matched 'Comparer`1' with 0
    /// argument(s)", which reads like the constructor is at fault rather than the type.
    /// </para>
    /// <para>
    /// The payoff is a tōsh class that .NET can use as the interface it asked for: this one
    /// is handed to <c>List&lt;string&gt;.Sort</c> as an <c>IComparer&lt;string&gt;</c>, and
    /// the sort order proves the comparer ran.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_generic_clr_base_is_closed_over_its_type_arguments()
    {
        var results = await RunAsync(
            """
            class ByLength extends System.Collections.Generic.Comparer<string> {
                func Compare(a, b) { return $a.Length - $b.Length }
            }
            var sorted = new System.Collections.Generic.List<string>(["bbb", "a", "cc"])
            $sorted.Sort(new ByLength())
            echo $"{$sorted[0]},{$sorted[1]},{$sorted[2]}"
            """);

        Assert.Equal(["a,cc,bbb"], results.OfType<string>());
    }

    /// <summary>Type arguments that do not match the base's arity are refused.</summary>
    /// <remarks>
    /// Including none at all. Left alone, <c>extends Comparer</c> recorded the open generic
    /// and failed later at construction, naming the constructor rather than the omission.
    /// </remarks>
    [Fact]
    public async Task A_generic_clr_base_without_type_arguments_is_refused()
    {
        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync(
                """
                class Bad extends System.Collections.Generic.Comparer { }
                var b = new Bad()
                """));

        Assert.Contains("type argument", error.Message, StringComparison.OrdinalIgnoreCase);
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
