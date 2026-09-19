using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A <see cref="Type"/> held in a variable is both a type and an object.
/// </summary>
/// <remarks>
/// <para>
/// Used as a receiver it nearly always stands in for the type itself, so a static of the
/// name should win — and it did, to the exclusion of everything else. A <c>Type</c> receiver
/// went straight to static dispatch, so <c>$t.GetMethods()</c> answered "No overload matched
/// static method 'GetMethods' on 'System.String'": it had looked for a static on the type
/// being <em>described</em> rather than an instance method on <c>Type</c>.
/// </para>
/// <para>
/// Reflecting on a value from a script was therefore impossible, which is where interop
/// stops being debuggable from inside the language — and is the workaround people reach for
/// when something else in the bridge is not cooperating.
/// </para>
/// <para>
/// Property access never had the problem: it reads a static if there is one and otherwise
/// resolves against the object. This makes methods agree with the accessor.
/// </para>
/// </remarks>
public sealed class TypeValueMemberTests
{
    private static Task<IReadOnlyList<object?>> RunAsync(string source)
        => new ToshEngine(ToshRuntime.CreateDefault().Language).ExecuteToListAsync(source);

    /// <summary>A Type's own instance methods are reachable.</summary>
    [Fact]
    public async Task Instance_methods_on_a_type_value_are_reachable()
    {
        var results = await RunAsync(
            """
            var t = "hello".GetType()
            echo ($t.GetMethods().Length > 0)
            echo ($t.GetInterfaces().Length > 0)
            # A single-overload method: `Trim` has several, and `GetMethod(string)` throws
            # AmbiguousMatchException for those — .NET's own rule, reached because this now
            # really is reflection.
            echo $t.GetMethod("ToUpperInvariant").Name
            """);

        Assert.Equal(["True", "True", "ToUpperInvariant"], results.Select(r => r?.ToString()));
    }

    /// <summary>
    /// A static of the same name still wins.
    /// </summary>
    /// <remarks>
    /// The fallback may only rescue a call that had nothing to bind to. Preferring the
    /// instance method would change what existing code means, which is a far worse trade
    /// than the one it fixes.
    /// </remarks>
    [Fact]
    public async Task A_static_on_the_described_type_still_wins()
    {
        var results = await RunAsync(
            """
            var math = System.Type::GetType("System.Math")
            echo $math.Sqrt(25)
            var text = "x".GetType()
            echo $text.Concat("a", "b")
            """);

        Assert.Equal(["5", "ab"], results.Select(r => r?.ToString()));
    }

    /// <summary>Both kinds resolve on the same value, by name.</summary>
    [Fact]
    public async Task Static_and_instance_members_coexist_on_one_type_value()
    {
        var results = await RunAsync(
            """
            var math = System.Type::GetType("System.Math")
            echo $math.Sqrt(16)
            echo $math.FullName
            echo ($math.GetMethods().Length > 0)
            """);

        Assert.Equal(["4", "System.Math", "True"], results.Select(r => r?.ToString()));
    }

    /// <summary>Properties on a Type value keep working as they always did.</summary>
    [Fact]
    public async Task Properties_on_a_type_value_are_unchanged()
    {
        var results = await RunAsync(
            """
            var t = "hello".GetType()
            echo $t.Name
            echo $t.FullName
            echo $t.IsSealed
            """);

        Assert.Equal(["String", "System.String", "True"], results.Select(r => r?.ToString()));
    }
}
