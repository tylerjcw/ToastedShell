using System.Reflection;
using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What typing the readiness probe found — `TOAST-0038`.
/// </summary>
/// <remarks>
/// Each of these was reached by annotating `bench/probes/compiler_shape.tosh` end to end
/// and reading what the compiler said next. None was reported by a user; the probe exists
/// to find out which parts of ToastScript fight back when you write compiler-shaped code,
/// and these are what it caught.
/// </remarks>
// Captures `Console.Out` to read a compiled program's output, so it must not run beside
// another test doing the same — the symptom is an empty capture, not a wrong one.
[Collection(ConsoleSerialCollection.Name)]
public sealed class TypedCollectionAndMatchTests
{
    private static string RunInterpreted(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = engine.ExecuteToListAsync(source).GetAwaiter().GetResult();
        return string.Join("\n", results.Select(v => ToastRenderer.Render(v)?.Trim())).Trim();
    }

    private static (bool Clean, string Output, IReadOnlyList<string> Shapes) Compile(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        var parse = engine.Parse(source, "<typed>");
        Assert.True(parse.Diagnostics.Count == 0, $"parse: {string.Join(", ", parse.Diagnostics)}");

        var unit = Lowerer.Lower(parse, runtime.Commands);
        var assemblyName = $"ToshTyped_{Guid.NewGuid():N}";
        using var stream = new MemoryStream();
        var result = BoundUnitEmitter.Emit(unit, assemblyName, stream);
        if (!result.IsClean) { return (false, string.Empty, result.UnsupportedShapes); }

        var main = Assembly.Load(stream.ToArray()).GetType($"{assemblyName}.Program")!
            .GetMethod("Main", BindingFlags.Public | BindingFlags.Static)!;

        var originalOut = Console.Out;
        var capture = new StringWriter();
        try { Console.SetOut(capture); main.Invoke(null, new object?[] { Array.Empty<string>() }); }
        finally { Console.SetOut(originalOut); }

        return (true, capture.ToString().Replace("\r", "").Trim(), result.UnsupportedShapes);
    }

    private const string Tokens = """
        class Token(k: string) { prop K: string = $k }
        class Other { }

        """;

    /// <summary>
    /// A collection annotation can name a type the program declared.
    /// </summary>
    /// <remarks>
    /// It could not before. `list&lt;int&gt;` works because `List&lt;int&gt;` is a real CLR
    /// type to convert to; a tōast class is a `ToshClassInstance`, so there is no
    /// `List&lt;Token&gt;` and every such annotation failed with "could not be converted".
    /// A lexer returning `list&lt;Token&gt;` is the ordinary shape of compiler-shaped code.
    /// </remarks>
    [Theory]
    [InlineData("list<Token>")]
    [InlineData("array<Token>")]
    public void A_collection_can_be_annotated_with_a_declared_element_type(string annotation)
        => Assert.Equal(
            "1",
            RunInterpreted(Tokens + $"func make() -> {annotation} {{ return [new Token(\"a\")] }}\n"
                                  + "echo $\"{((make) | count)}\""));

    /// <summary>The element type is checked, not assumed.</summary>
    /// <remarks>
    /// The control. Accepting any sequence for `list&lt;Token&gt;` would pass the theory
    /// above and make the annotation decorative.
    /// </remarks>
    [Fact]
    public void A_wrong_element_type_is_still_rejected()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var source = Tokens + "func make() -> list<Token> { return [new Other()] }\necho $\"{((make) | count)}\"";

        var thrown = Assert.ThrowsAny<Exception>(
            () => engine.ExecuteToListAsync(source).GetAwaiter().GetResult());

        Assert.Contains("could not be converted", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A property annotated with a declared type does not report a mismatch against itself.
    /// </summary>
    /// <remarks>
    /// `prop Operand: Node = $operand` reported "Cannot assign value of type 'Node' to
    /// property 'Operand' of type 'Node'" — two different types with one name. The checker
    /// resolved member annotations with a resolver that had no user types, so once
    /// `TOAST-0034` gave resolution the platform index, a user type name found whatever CLR
    /// type happened to share it.
    /// </remarks>
    [Fact]
    public void A_property_annotated_with_a_declared_type_is_not_a_mismatch()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        var parse = engine.Parse(
            "class Node { prop Kind: string = \"n\" }\n" +
            "class Unary(operand: Node) extends Node { prop Operand: Node = $operand }",
            "<props>");

        var diagnostics = TypeChecker.Check(Lowerer.Lower(parse, runtime.Commands));

        Assert.DoesNotContain(diagnostics, d => d.Code == "tosh.type.mismatch");
    }

    /// <summary>A `match` arm may throw, and it compiles.</summary>
    /// <remarks>
    /// `default => throw …` is the ordinary way to say an arm cannot happen. It was refused
    /// in value context, and the refusal abandoned the match's end label — so the assembly
    /// writer crashed with `InvalidOperationException: Label 5 has not been marked` and the
    /// real diagnostic was lost underneath a CLR stack trace.
    /// </remarks>
    [Fact]
    public void A_match_arm_can_throw()
    {
        const string Source = """
            class A { prop K: string = "a" }
            class B extends A { prop K: string = "b" }
            func f(n: A) -> string => match ($n) {
                _ is B  => "b"
                default => throw new Error("no")
            }
            echo $"{(f (new B()))}"
            """;

        var compiled = Compile(Source);
        Assert.True(compiled.Clean, $"unsupported: {string.Join(", ", compiled.Shapes)}");
        Assert.Equal("b", compiled.Output);
        Assert.Equal("b", RunInterpreted(Source));
    }

    /// <summary>
    /// A refused shape is reported, and nothing is written.
    /// </summary>
    /// <remarks>
    /// The emitter serialized unconditionally, so a shape it had already declined left
    /// incomplete IL — a branch whose target was never marked — and the assembly writer
    /// threw from deep inside `PersistedAssemblyBuilder`. The diagnostic naming the actual
    /// problem had been recorded and was then discarded with a stack trace on top of it.
    ///
    /// This asserts the *mechanism* rather than a particular unsupported shape, so it keeps
    /// working as shapes become supported: a `require` of a file that is not part of the
    /// compilation stays Tier 3 by design.
    /// </remarks>
    [Fact]
    public void A_refused_shape_reports_instead_of_crashing()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        var parse = engine.Parse("bind libc {\n    func getpid() -> int\n}\necho \"x\"", "<refused>");
        var unit = Lowerer.Lower(parse, runtime.Commands);

        using var stream = new MemoryStream();
        var result = BoundUnitEmitter.Emit(unit, $"ToshRefused_{Guid.NewGuid():N}", stream);

        if (result.IsClean) { return; }

        Assert.NotEmpty(result.UnsupportedShapes);
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// A class method with an expression body returns its expression — `TOAST-0043`.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It returned <c>null</c>. Free functions had this rule and class methods did not, so
    /// <c>class E { func M() -> int =&gt; 7 }</c> answered null compiled and 7 interpreted,
    /// while the block-bodied <c>{ return 7 }</c> was correct throughout.
    /// </para>
    /// <para>
    /// The comment on the free-function version already described the symptom exactly —
    /// "the block was emitted for effect, its value dropped, and the fall-through returned
    /// default(T) … silently, and for the most idiomatic way to write a function". The rule
    /// is shared now rather than written twice, which is what let them drift.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("class E { func M() -> int => 7 }\necho $\"{((new E()).M())}\"", "7")]
    // Through a private method, which is a separate emission path.
    [InlineData("class E {\n    shy func Inner() -> int => 7\n    func M() -> int { return $this.Inner() }\n}\n"
                + "echo $\"{((new E()).M())}\"", "7")]
    // A match expression as the body — the shape the readiness probe is built from.
    [InlineData("""
        class N { prop K: string = "n" }
        class Leaf(v: double) extends N { prop V: double = $v }
        class E {
            func Visit(n: N) -> double => match ($n) {
                _ is Leaf => $n.V
                default   => throw new Error("x")
            }
        }
        echo $"{((new E()).Visit(new Leaf(3.0)))}"
        """, "3")]
    public void A_class_method_with_an_expression_body_returns_its_value(string source, string expected)
    {
        var compiled = Compile(source);
        Assert.True(compiled.Clean, $"unsupported: {string.Join(", ", compiled.Shapes)}");
        Assert.Equal(expected, compiled.Output);
        Assert.Equal(expected, RunInterpreted(source));
    }

    /// <summary>
    /// A block body and a `dynamic` return are untouched.
    /// </summary>
    /// <remarks>
    /// The controls. Collapsing a trailing expression into a return could have been applied
    /// to every method, and `dynamic` is the documented way to opt out of an annotation —
    /// a method declared that way yields a stream, and collapsing it would keep only one
    /// value of however many it produced.
    /// </remarks>
    [Fact]
    public void A_block_body_and_a_dynamic_return_are_unchanged()
    {
        var block = Compile("class E { func M() -> int { return 7 } }\necho $\"{((new E()).M())}\"");
        Assert.True(block.Clean);
        Assert.Equal("7", block.Output);

        // Not compared against the interpreter: a `dynamic` method's stream is a separate
        // divergence, present before this change and unaffected by it.
        var dyn = Compile("class E { func M() -> dynamic { echo 1\n echo 2 } }\n"
                          + "echo $\"{(((new E()).M()) | count)}\"");
        Assert.True(dyn.Clean);
    }

    /// <summary>
    /// A class this program declares wins over a CLR type of the same name — `TOAST-0044`.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `Token` is a name a compiler-shaped program is very likely to choose, and the CLR has
    /// a nested `System.Runtime.InteropServices.PosixSignalRegistration+Token`. Compiled,
    /// `new Token(…)` found *that one*, and said so as a constructor-arity complaint about a
    /// type the author has never heard of.
    /// </para>
    /// <para>
    /// It needs a class the emitter cannot shell — a computed property is enough — because
    /// such a class stays on source replay, and replayed source resolves through the engine.
    /// The engine knew nothing about the shells emitted beside it, so the name fell through
    /// to the platform index.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_declared_class_wins_over_a_clr_type_of_the_same_name()
    {
        const string Source = """
            class Token(k: string) { prop K: string = $k }
            class Mk {
                prop N: int = 1
                prop Computed: bool => $this.N > 0
                func Make() -> Token { return new Token("a") }
            }
            echo $"{((new Mk()).Make().K)}"
            """;

        var compiled = Compile(Source);
        Assert.True(compiled.Clean, $"unsupported: {string.Join(", ", compiled.Shapes)}");
        Assert.Equal("a", compiled.Output);
        Assert.Equal("a", RunInterpreted(Source));
    }

    /// <summary>A CLR type the program does *not* declare still resolves.</summary>
    /// <remarks>
    /// The control. Making declared names win could have been done by disabling the
    /// platform-index fallback, which would take `Error`, `StringBuilder` and every other
    /// bare CLR name with it.
    /// </remarks>
    [Fact]
    public void An_undeclared_clr_type_still_resolves()
    {
        var compiled = Compile("var sb = new StringBuilder()\necho $\"{$sb.Length}\"");
        Assert.True(compiled.Clean, $"unsupported: {string.Join(", ", compiled.Shapes)}");
        Assert.Equal("0", compiled.Output);
    }
}
