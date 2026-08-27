using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A declared type reaches the values derived from it — `TOAST-0034`.
/// </summary>
/// <remarks>
/// <para>
/// Inference used to reach literals and <c>new</c> on a class declared in the same file,
/// and stop at every call and every member read. So a type the author had already written
/// down went unused:
/// </para>
/// <code>
/// func f() -> int => 7
/// var v = f()          // "could not pin down a concrete type"
/// </code>
/// <para>
/// That is what made Phase B's exit look unreachable: the readiness probe's failures were
/// four values that came from calls, and compiler-shaped code is mostly calls.
/// </para>
/// <para>
/// These assert the absence of <c>tosh.compile.implicit_dynamic</c> rather than a specific
/// inferred type, because the question is whether the compiler learned anything — the exact
/// type is then pinned by the program running.
/// </para>
/// </remarks>
public sealed class TypeInferencePropagationTests
{
    private static IReadOnlyList<ToshDiagnostic> StrictDiagnostics(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        var parse = engine.Parse(source, "<inference>");
        Assert.True(parse.Diagnostics.Count == 0, $"parse: {string.Join(", ", parse.Diagnostics)}");

        var unit = Lowerer.Lower(parse, runtime.Commands);
        return TypeChecker.CheckCompileAnnotations(unit, allowDynamic: false);
    }

    private static void AssertInfers(string source)
    {
        var diagnostics = StrictDiagnostics(source);
        Assert.True(
            diagnostics.Count == 0,
            "expected the type to be inferred, but the compiler reported:\n  " +
            string.Join("\n  ", diagnostics.Select(d => d.Title)));
    }

    /// <summary>A declared type propagates through whatever produced the value.</summary>
    [Theory]
    // Already worked, kept as controls: a literal and a locally declared class.
    [InlineData("var a = 1\necho $\"{$a}\"")]
    [InlineData("var xs = [1, 2, 3]\necho $\"{($xs | count)}\"")]
    [InlineData("class K { prop V: int = 1 }\nvar k = new K()\necho $\"{$k.V}\"")]
    // A function's declared return. The headline case.
    [InlineData("func f() -> int => 7\nvar v = f()\necho $\"{$v}\"")]
    // A method's declared return, and a property's declared type.
    [InlineData("class K { func M() -> int => 7 }\nvar k = new K()\nvar v = $k.M()\necho $\"{$v}\"")]
    [InlineData("class K { prop V: int = 1 }\nvar k = new K()\nvar v = $k.V\necho $\"{$v}\"")]
    // A CLR type: construction, a property read, an instance call, and a static call whose
    // overloads agree on one return type.
    [InlineData("var h = new System.Collections.Hashtable()\necho $\"{$h.Count}\"")]
    [InlineData("var b = new StringBuilder()\nvar n = $b.Length\necho $\"{$n}\"")]
    [InlineData("var b = new StringBuilder()\nvar s = $b.ToString()\necho $\"{$s}\"")]
    [InlineData("var v = Math.Sqrt(2.0)\necho $\"{$v}\"")]
    // A record literal. The last row of the item's table, and the one that needed a type
    // rather than a lookup: a record is an `ExpandoObject` on both backends — the
    // interpreter builds one and the emitter emits one — so that is what it infers to,
    // rather than a structural type invented for the occasion that nothing else would know.
    [InlineData("var r = {| a = 1 |}\necho $\"{$r.a}\"")]
    [InlineData("var r = {| a = 1, b = \"x\" |}\necho $\"{$r.b}\"")]
    [InlineData("var r = {| a = {| b = 2 |} |}\necho $\"{$r.a.b}\"")]
    // The shape the readiness probe is made of: a class method returning a collection,
    // consumed by a local with no annotation of its own.
    [InlineData("""
        class Token(kind: string) { prop Kind: string = $kind }
        class Lexer(source: string) {
            func Tokenize() -> list<Token> { return [new Token("a")] }
        }
        func Compile(source: string) -> int {
            var lexer = new Lexer($source)
            var tokens = $lexer.Tokenize()
            return ($tokens | count)
        }
        echo $"{(Compile "x")}"
        """)]
    public void A_declared_type_reaches_what_is_derived_from_it(string source) => AssertInfers(source);

    /// <summary>
    /// Where nothing was declared, nothing is invented.
    /// </summary>
    /// <remarks>
    /// The control, and it is the whole reason to trust the theory above. Propagation could
    /// have been "implemented" by treating anything unresolvable as `object`, which would
    /// silence every diagnostic and infer nothing.
    ///
    /// Overloads that disagree about their return type are in here deliberately:
    /// `Math.Round` returns `double` for one signature and `decimal` for another, and
    /// answering either without resolving the arguments would be a guess.
    /// </remarks>
    [Theory]
    // No declared return.
    [InlineData("func f() => 7\nvar v = f()\necho $\"{$v}\"")]
    [InlineData("class K { func M() => 7 }\nvar k = new K()\nvar v = $k.M()\necho $\"{$v}\"")]
    // A member that does not exist, and a target that is not a known type.
    [InlineData("var k = 1\nvar v = $k.NoSuchMember\necho $\"{$v}\"")]
    [InlineData("var v = Math.NoSuchMethod(1)\necho $\"{$v}\"")]
    // Overloads that disagree — about a function's return, and a CLR method's.
    [InlineData("func h(a: int) -> int => 1\nfunc h(a: int, b: int) -> string => \"x\"\n"
                + "var v = h(1)\necho $\"{$v}\"")]
    [InlineData("var v = Math.Round(1.5, 0)\necho $\"{$v}\"")]
    public void Where_nothing_is_declared_nothing_is_invented(string source)
        => Assert.NotEmpty(StrictDiagnostics(source));

    /// <summary>
    /// Inferring a record's type does not make its members static.
    /// </summary>
    /// <remarks>
    /// The risk this row carried, and the reason the type was measured before being chosen.
    /// A record's fields are not CLR properties, so giving the literal a *concrete* type
    /// could have turned `$r.a` into a static member lookup that fails — trading one
    /// `implicit_dynamic` for a worse failure than the one being fixed. `ExpandoObject`
    /// keeps member access dynamic, and this asserts it by reading the field back rather
    /// than by trusting that.
    /// </remarks>
    [Theory]
    [InlineData("var r = {| a = 1 |}\necho $r.a", "1")]
    [InlineData("var r = {| a = 1, b = \"x\" |}\necho $r.b", "x")]
    [InlineData("var r = {| a = {| b = 2 |} |}\necho $r.a.b", "2")]
    public async Task An_inferred_record_still_reads_its_fields(string source, string expected)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);

        Assert.Equal(expected, results[^1]?.ToString());
    }
}
