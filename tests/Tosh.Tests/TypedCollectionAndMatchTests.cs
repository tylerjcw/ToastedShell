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
public sealed class TypedCollectionAndMatchTests
{
    private static string RunInterpreted(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = engine.ExecuteToListAsync(source).GetAwaiter().GetResult();
        return string.Join("\n", results.Select(v => ToastRenderer.Render(v)?.Trim())).Trim();
    }

    private static (bool Clean, string Output, IReadOnlyList<string> Shapes) Compile(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
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
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
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
        var engine = new ToshEngine(runtime);
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
        var engine = new ToshEngine(runtime);
        var parse = engine.Parse("bind libc {\n    func getpid() -> int\n}\necho \"x\"", "<refused>");
        var unit = Lowerer.Lower(parse, runtime.Commands);

        using var stream = new MemoryStream();
        var result = BoundUnitEmitter.Emit(unit, $"ToshRefused_{Guid.NewGuid():N}", stream);

        if (result.IsClean) { return; }

        Assert.NotEmpty(result.UnsupportedShapes);
        Assert.Equal(0, stream.Length);
    }
}
