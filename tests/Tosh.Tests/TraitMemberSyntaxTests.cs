using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A trait member is written the way a class member is — `TOAST-0019`.
///
/// Traits had their own hand-rolled member parser, so three spellings that work everywhere
/// else did not work inside a `trait`: `-> T` for a return type (traits took only `: T`),
/// `=> expr` for a default body (only `{ ... }`), and `prop X: T` for a typed requirement
/// (only a bare `prop X`). Two declarations two lines apart accepted different syntax for
/// the same thing.
///
/// The property case had a specific cause worth keeping: the lexer glues `X:` into one
/// bareword, so `prop X: int` reached the parser as the *name* `X:` and was rejected as an
/// invalid identifier. Class properties already handled this through
/// `ParseTypedIdentifierToken`; traits called `ExpectVariableName` and lost.
///
/// Found deciding how `TOAST-0014` should spell its rendering extension point. The chosen
/// answer is a `Display` trait, and the trait it wants is `trait Display { func render() ->
/// string }` — which was exactly the form that did not parse.
/// </summary>
public sealed class TraitMemberSyntaxTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>
    /// The form the decision needs: a required method with an arrow return type.
    /// </summary>
    [Fact]
    public async Task A_required_method_declares_an_arrow_return_type()
        => Assert.Equal("rendered", await RunAsync(
            """
            trait Display { func render() -> string }
            class T uses Display { func render() -> string => "rendered" }
            (new T()).render()
            """));

    /// <summary>
    /// A default body in arrow form, which a class method has always accepted.
    /// </summary>
    [Fact]
    public async Task A_default_body_may_be_written_with_an_arrow()
        => Assert.Equal("default", await RunAsync(
            """
            trait D { func render() -> string => "default" }
            class T uses D { }
            (new T()).render()
            """));

    /// <summary>
    /// A typed required property. This is the one the lexer's `X:` gluing broke.
    /// </summary>
    [Fact]
    public async Task A_required_property_declares_a_type()
        => Assert.Equal("p", await RunAsync(
            """
            trait Named { prop Name: string }
            class P uses Named { prop Name: string = "p" }
            (new P()).Name
            """));

    /// <summary>
    /// A class that does not supply a typed requirement is still reported. Without this,
    /// the property fix could have been "parse the type and forget it".
    /// </summary>
    [Fact]
    public async Task A_missing_typed_property_is_still_reported()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteToListAsync(
            """
            trait Named { prop Name: string }
            class P uses Named { }
            new P()
            """));

        Assert.Contains("Name", exception.Message);
    }

    /// <summary>
    /// Every spelling that worked before still works. `: T` was the *only* return-type
    /// form traits took, so dropping it would have been a second defect rather than a fix.
    /// </summary>
    [Theory]
    [InlineData("trait A { func f(): string }\nclass K uses A { func f() -> string => \"x\" }\n(new K()).f()", "x")]
    [InlineData("trait B { func g(): string { return \"y\" } }\nclass K uses B { }\n(new K()).g()", "y")]
    [InlineData("trait C { prop X }\nclass K uses C { prop X: int = 7 }\n(new K()).X", "7")]
    public async Task The_previous_spellings_still_work(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The two return-type spellings mean the same thing, asserted through a class that
    /// satisfies both traits at once.
    /// </summary>
    [Fact]
    public async Task Both_return_type_spellings_describe_the_same_requirement()
        => Assert.Equal("ab", await RunAsync(
            """
            trait Arrow { func a() -> string }
            trait Colon { func b(): string }
            class K uses Arrow, Colon {
                func a() -> string => "a"
                func b() -> string => "b"
            }
            var k = new K()
            ($k.a() + $k.b())
            """));

    /// <summary>
    /// A trait member's type is a *declaration*, not a check — a class may still return
    /// something else, and nothing reports it.
    /// </summary>
    /// <remarks>
    /// Pinned deliberately rather than left undiscovered. Enforcement needs a rule for what
    /// "compatible" means — exact name, alias, subclass, interface — and that is a variance
    /// decision, not a parser change. Filed as `TOAST-0020`. When it lands, this test is the
    /// one that must flip, and its failure is the signal that it did.
    /// </remarks>
    [Fact]
    public async Task A_declared_return_type_is_not_yet_enforced()
        => Assert.Equal("42", await RunAsync(
            """
            trait D { func render() -> string }
            class T uses D { func render() -> int => 42 }
            (new T()).render()
            """));
}
