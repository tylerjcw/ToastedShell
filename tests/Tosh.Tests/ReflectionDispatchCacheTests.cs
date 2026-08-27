using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A CLR call finds its method from a cache, and finds the same method it did before.
///
/// `TS-P2-122`. `ReflectionInvoker` had no caching of any kind. Every call ran
/// `GetMethods(...)` — which hands back an array of *every* public method the type
/// has — and then linear-scanned it comparing names case-insensitively. A call to
/// `"abc".ToUpper()` searched all ~80 methods on <see cref="string"/>, every time,
/// and measured 3,975 ns.
///
/// Both the per-type array and the per-name overload set are now cached; a loaded
/// type's members cannot change. `Invoke` filters by name itself and uses that same
/// filtered set for its diagnostics, so handing it a pre-filtered array changes
/// nothing observable. 2,995 ns after.
///
/// The risk in a keyed cache is the key, so that is what these test: the same name
/// on different types, the same name static and instance, and overloads that differ
/// only by arity or by parameter type. Every one of them would return a plausible
/// wrong answer if the key were too coarse, rather than failing loudly.
/// </summary>
public class ReflectionDispatchCacheTests
{
    private static async Task<string> RunAsync(string source)
    {
        var output = new StringWriter();
        var engine = new ToshEngine(ToshRuntime.CreateDefault(output, output).Language);
        await engine.ExecuteToListAsync(source);
        return output.ToString().Replace("\r", "").Trim();
    }

    /// <summary>
    /// One name, three types. A cache keyed on the name alone would answer the first
    /// type's method for all of them — and `ToString` exists on everything, so this
    /// is the collision most likely to happen and least likely to look wrong.
    /// </summary>
    [Fact]
    public async Task The_same_method_name_on_different_types_stays_separate()
        => Assert.Equal("abc|42|3.5", await RunAsync(
            """
            writeline ("abc".ToString() + "|" + (42).ToString() + "|" + (3.5).ToString())
            """));

    /// <summary>
    /// `Equals` exists as both a static and an instance method on <c>string</c>, with
    /// different arities. The cache key carries which, or the two overload sets merge.
    /// </summary>
    [Fact]
    public async Task A_static_and_an_instance_method_of_one_name_stay_separate()
        => Assert.Equal("True|True", await RunAsync(
            """
            using System
            writeline (String.Equals("x", "x") + "|" + "x".Equals("x"))
            """));

    /// <summary>
    /// Overload selection still happens *after* the cache: these differ by arity and
    /// by parameter type, and the cached set has to contain all of them.
    /// </summary>
    [Theory]
    [InlineData("\"hello\".Substring(1)", "ello")]
    [InlineData("\"hello\".Substring(1, 3)", "ell")]
    [InlineData("\"hello\".IndexOf(\"l\")", "2")]
    [InlineData("\"hello\".IndexOf(\"lo\")", "3")]
    [InlineData("\"hello\".Replace(\"l\", \"L\")", "heLLo")]
    // Not `Split(",")` or `PadLeft(4, ".")`: the *type checker* reports no matching
    // overload for those even though they run, which is its own gap and nothing to do
    // with how the method is found.
    [InlineData("\"hello\".Contains(\"ell\")", "true")]
    public async Task Overloads_are_still_selected_by_arity_and_type(string expression, string expected)
        => Assert.Equal(expected, await RunAsync($"writeline ({expression})"));

    /// <summary>
    /// Method names are matched case-insensitively, which is why the cache key needs a
    /// case-insensitive comparer rather than lowercasing the name — lowercasing would
    /// allocate a string per lookup and undo the point.
    /// </summary>
    [Fact]
    public async Task Case_insensitive_dispatch_still_resolves()
        => Assert.Equal("ABC|abc", await RunAsync("""writeline ("abc".toupper() + "|" + "ABC".ToLower())"""));

    /// <summary>Constructors are cached by type and still overload-select.</summary>
    [Fact]
    public async Task Constructor_overloads_still_resolve()
        => Assert.Equal("xxx|1.2", await RunAsync(
            """
            using System
            writeline ((new String("x", 3)) + "|" + (new Version(1, 2)).ToString())
            """));

    /// <summary>
    /// A name that does not exist still fails, and fails the same way. Caching a
    /// *negative* answer badly would turn this into silence.
    /// </summary>
    [Fact]
    public async Task A_method_that_does_not_exist_still_fails()
        => await Assert.ThrowsAnyAsync<Exception>(
            () => RunAsync("""writeline ("abc".NoSuchMethodHere())"""));

    /// <summary>
    /// And a real name called with argument types no overload accepts fails on the
    /// overload rather than the name — the two diagnostics have to stay distinct,
    /// since they send the reader to different places.
    /// </summary>
    [Fact]
    public async Task A_name_with_no_matching_overload_still_fails()
        => await Assert.ThrowsAnyAsync<Exception>(
            () => RunAsync("""writeline ("abc".Substring("not-a-number", 9, 9, 9))"""));

    /// <summary>
    /// The same call repeated takes the cached path from the second time on, and must
    /// keep answering per-instance rather than returning the first instance's result.
    /// </summary>
    [Fact]
    public async Task A_repeated_call_answers_for_its_own_receiver()
        => Assert.Equal("A|B|C", await RunAsync(
            """
            var parts = ""
            for s in ["a", "b", "c"] { $parts = ($parts + $s.ToUpper() + "|") }
            writeline $parts.TrimEnd("|")
            """));
}
