using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Which names an `extend` declaration answers to — `TOAST-0016`.
///
/// An extension was stored under the type name exactly as written, and looked up against
/// the names a *receiver* can produce — `Int32`, `System.Int32`. The shell alias was never
/// among them, so `extend int` was accepted, registered, and then matched nothing:
///
///     extend int { func tag() -> string => "ext" }
///     (1).tag()      # error: no overload matched instance method 'tag' on 'System.Int32'
///
/// `int` is the spelling the language uses everywhere else — `var n: int`, `func f(x: int)`,
/// `cast int` — so it is the one an author reaches for first, and the failure surfaced at a
/// call site that looked unrelated to the declaration.
/// </summary>
public sealed class ExtendTypeAliasTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>
    /// The alias reaches the CLR type it names, for every alias the annotation position
    /// accepts.
    /// </summary>
    [Theory]
    [InlineData("extend int { func tag() -> string => \"ext\" }\n(1).tag()", "ext")]
    [InlineData("extend string { func tag() -> string => \"ext\" }\n\"a\".tag()", "ext")]
    [InlineData("extend bool { func tag() -> string => \"ext\" }\n(true).tag()", "ext")]
    [InlineData("extend double { func tag() -> string => \"ext\" }\n(1.5).tag()", "ext")]
    // `float` is `Single` in Tōast — consistently, in annotations, casts and here — and a
    // `1.5` literal is a `Double`, so the value has to be cast to meet the extension.
    [InlineData("extend float { func tag() -> string => \"ext\" }\n(1.5 as float).tag()", "ext")]
    public async Task An_alias_reaches_the_type_it_names(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The CLR spellings keep working — they were the only ones that did, so the fix must
    /// not trade one for the other.
    /// </summary>
    /// <remarks>
    /// Only the simple name. A dotted `extend System.Int32` does not parse at all — the
    /// statement is not recognised and the word `extend` is reported as an unknown command.
    /// That is a pre-existing limitation of the `extend` statement's own grammar rather
    /// than anything to do with which names it registers under, and is recorded on the item.
    /// </remarks>
    [Fact]
    public async Task The_clr_spelling_still_works()
        => Assert.Equal("ext", await RunAsync(
            "extend Int32 { func tag() -> string => \"ext\" }\n(1).tag()"));

    /// <summary>
    /// An alias and its CLR name are the *same type*, so two declarations add to one table
    /// rather than to two that shadow each other.
    /// </summary>
    [Fact]
    public async Task An_alias_and_its_clr_name_share_one_table()
        => Assert.Equal("a-b", await RunAsync(
            """
            extend int { func first() -> string => "a" }
            extend Int32 { func second() -> string => "b" }
            ((1).first() + "-" + (1).second())
            """));

    /// <summary>
    /// A ToastScript type is still matched by name — that is what lets `extend Point` reach
    /// a declared class, and it is why the registration key is a string rather than a
    /// resolved type.
    /// </summary>
    [Fact]
    public async Task A_toastscript_class_is_still_extended_by_name()
        => Assert.Equal("2", await RunAsync(
            """
            class ExtP { prop N: int = 1 }
            extend ExtP { func twice() -> int => ($this.N * 2) }
            (new ExtP()).twice()
            """));

    /// <summary>
    /// And a forward reference still works: the class may be declared *after* the extension
    /// that adds to it.
    /// </summary>
    /// <remarks>
    /// This is why `TOAST-0016`'s "report an unknown type at declaration" box is not met.
    /// At the moment an `extend` is evaluated, a name that resolves to nothing is
    /// indistinguishable from one whose type has not been declared yet, and this spelling is
    /// legal and useful.
    /// </remarks>
    [Fact]
    public async Task A_forward_reference_still_works()
        => Assert.Equal("6", await RunAsync(
            """
            extend ExtLater { func twice() -> int => ($this.N * 2) }
            class ExtLater { prop N: int = 3 }
            (new ExtLater()).twice()
            """));

    /// <summary>
    /// A real member still wins over an extension — `TS-P3-27`, unchanged and pinned here
    /// because widening the registration keys is exactly the kind of change that could
    /// quietly let an extension shadow something.
    /// </summary>
    [Fact]
    public async Task A_real_member_still_wins_over_an_extension()
        => Assert.Equal("AB", await RunAsync(
            """
            extend string { func ToUpper() -> string => "from-extension" }
            "ab".ToUpper()
            """));
}
