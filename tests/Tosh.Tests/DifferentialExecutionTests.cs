using System.Reflection;
using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The same program, interpreted and compiled, must produce the same output.
///
/// This is the discipline `TS-P3-23` asks for, and it is overdue: `TS-P2-109`
/// was a typed function that returned 42 interpreted and 0 compiled, and it
/// survived indefinitely because nothing compared the two. Every case here is a
/// shape that has already diverged, or sits adjacent to one that did.
///
/// The corpus is deliberately concentrated on **class hierarchies**. That is
/// where the divergences were found — subclass assignability, `match` narrowing,
/// and typed returns all involved them — and the existing suite covers them
/// least. Arithmetic cases are controls, not coverage.
///
/// A case that fails here has one of two meanings, and both matter: either the
/// compiler is wrong, or the interpreter is. The assertion deliberately does not
/// say which.
///
/// <para><b>Class names here are prefixed <c>Diff</c>, and must stay unique
/// across the whole test project.</b> `TS-P1-48`: every compiled assembly in the
/// process shares one static <c>ToshHost.s_engine</c>, and classes register into
/// it by bare name — so a plain <c>Base</c> here silently collides with
/// <c>BoundUnitEmitterTests</c>'s <c>Base</c>. Both files pass alone and fail
/// together, and the error blames the annotation rather than the collision. That
/// is how it was found.</para>
/// </summary>
[Collection(ConsoleSerialCollection.Name)]
public sealed class DifferentialExecutionTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public DifferentialExecutionTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    public static IEnumerable<object[]> Corpus()
    {
        // ── Controls ───────────────────────────────────────────────────────
        yield return Case("arithmetic", "echo (1 + 2 * 3)");
        yield return Case("interpolation", "var n = 4\necho $\"n is {$n}\"");

        // ── Typed functions: the TS-P2-109 family ─────────────────────────
        yield return Case(
            "typed-expression-body",
            "func add(a: int, b: int) -> int => $a + $b\necho (add 20 22)");

        yield return Case(
            "typed-statement-body",
            "func add(a: int, b: int) -> int { return $a + $b }\necho (add 20 22)");

        yield return Case(
            "typed-string-return",
            "func greet(n: string) -> string => $\"hi {$n}\"\necho (greet \"bob\")");

        yield return Case(
            "trailing-expression-after-statements",
            "func f(a: int) -> int {\n    var doubled = $a * 2\n    $doubled + 1\n}\necho (f 5)");

        // ── Rendering: one contract, both backends (`TOAST-0014` stage 4) ──
        //
        // The compiled path formatted through its own ObjectFormatter, built from a
        // *fresh* DisplayPreferences, while the interpreter used the shell's live one.
        // The two agreed only while nothing was configured, and nothing compared them —
        // which is the exact shape TS-P2-109 had, and the reason this file exists.
        yield return Case("render-list", "echo $\"{[1, 2, 3]}\"");
        yield return Case("render-nested-list", "echo $\"{[[1, 2], [3]]}\"");
        yield return Case("render-record", "echo $\"{{| N = 5, S = \\\"a b\\\" |}}\"");
        yield return Case("render-null-and-bool", "echo $\"{null} {true}\"");
        yield return Case("render-float-specials", "echo $\"{(0.0 / 0.0)} {(1.0 / 0.0)}\"");
        // A literal, not a variable: a hole is evaluated as a pipeline, so a variable
        // holding a collection spreads into several results and is space-joined rather
        // than rendered. That is `TOAST-0023`, and it is a question about holes rather
        // than about rendering.
        yield return Case("render-nested-string-quoting", "echo $\"{[1, 2, 3]}\" ");

        // `TOAST-0023`, moved here from `KnownDivergences` when it was fixed: a hole is one
        // value, so a variable holding a collection renders rather than spreading, and the
        // two backends agree. The compiled side always rendered; it was the interpreter
        // that spread.
        yield return Case(
            "hole-with-a-collection-variable",
            "var xs = [\"a b\", \"c\"]\necho $\"{$xs}\"");
        yield return Case("render-enum", "enum DiffHue { Red, Green }\necho $\"{DiffHue.Red}\"");

        // ── Class hierarchies: where the bugs lived ────────────────────────
        yield return Case(
            "subclass-returned-as-base",
            Hierarchy + "func make(v: int) -> DiffBase => new DiffLeaf($v)\necho ((make 3)).Kind");

        yield return Case(
            "subclass-as-base-parameter",
            Hierarchy + "func kindOf(n: DiffBase) -> string => $n.Kind\necho (kindOf (new DiffLeaf(5)))");

        yield return Case(
            "static-factory-on-class",
            "class DiffBox {\n    prop V: int = 0\n    static func Of(v: int) -> DiffBox {\n        var made = new DiffBox()\n        $made.V = $v\n        return $made\n    }\n}\necho (DiffBox.Of(9)).V");

        yield return Case(
            "match-narrowing-over-hierarchy",
            Hierarchy
            + "func describe(n: DiffBase) -> string => match ($n) {\n"
            + "    _ is DiffLeaf => $\"leaf {$n.V}\"\n"
            + "    default   => \"other\"\n"
            + "}\n"
            + "echo (describe (new DiffLeaf(7)))\n"
            + "echo (describe (new DiffBase()))");

        yield return Case(
            "two-level-inheritance",
            "class DiffA { prop Tag: string = \"a\" }\nclass DiffB extends DiffA { prop Tag: string = \"b\" }\nclass DiffC extends DiffB { prop Tag: string = \"c\" }\nvar x: DiffA = new DiffC()\necho $x.Tag");

        // ── Rebinding: the TS-P2-87 family ────────────────────────────────
        yield return Case(
            "rebound-variable-still-reads-back",
            "var x = 10\n$x = \"hello\"\necho $x");
    }

    /// <summary>
    /// Divergences this corpus found and the project has decided not to fix yet.
    ///
    /// Compiled ToastScript is an experiment until the interpreted language is
    /// solid, and the interpreter is authoritative — so the disposition here is
    /// the one `TS-P1-40` already took: record the divergence so it is known
    /// rather than rediscovered, and leave it. Fixing them means compiler work,
    /// which is deliberately not the current priority.
    ///
    /// These are asserted to *still diverge*. That is not a claim the behaviour
    /// is correct — it is a tripwire. When someone fixes one, this test fails and
    /// says so, and the case moves up into <see cref="Corpus"/> where it belongs.
    /// </summary>
    public static IEnumerable<object[]> KnownDivergences()
    {
        // The compiled backend represents an array literal as List<object>; the
        // interpreter produces a real array (System.Int32[] for this one). Every
        // member that differs between the two — .Length against .Count — differs
        // with it.
        yield return Divergence(
            "TS-P1-46", "array-literal-representation",
            "var xs = [1, 2, 3]\necho $xs.Length");

        // A variable annotated with a base class rejects a subclass value when
        // compiled. Parameters and returns take a different path and accept it,
        // so this is specific to the variable annotation.
        yield return Divergence(
            "TS-P1-47", "base-annotated-variable-rejects-subclass",
            Hierarchy + "var b: DiffBase = new DiffLeaf(4)\necho $b.Kind");

        // A compiled class is a real emitted CLR type rather than a ToshClassInstance, so
        // it never answers `TryGetOwnRendering` and its `Display` implementation is not
        // reached: `21deg` interpreted, `DiffTemp` compiled.
        yield return Divergence(
            "TOAST-0022", "render-class-with-display",
            "trait Display { func render() -> string }\n"
            + "class DiffTemp uses Display {\n"
            + "    prop C: int = 21\n"
            + "    func render() -> string => $\"{$this.C}deg\"\n"
            + "}\n"
            + "echo $\"{(new DiffTemp())}\"");

        // The emitter drops an interpolation hole's format clause entirely, so
        // `$"{42:X}"` is `2A` interpreted and `42` compiled.
        yield return Divergence(
            "TOAST-0022", "render-format-clause",
            "echo $\"{42:X} {3.14159:F2}\"");


    }

    private static object[] Divergence(string boardItem, string name, string source) =>
        [boardItem, name, source];

    /// <summary>Shared two-level hierarchy with an overridden property.</summary>
    private const string Hierarchy =
        "class DiffBase { prop Kind: string = \"base\" }\n"
        + "class DiffLeaf(v: int) extends DiffBase {\n"
        + "    prop Kind: string = \"leaf\"\n"
        + "    prop V: int = $v\n"
        + "}\n";

    private static object[] Case(string name, string source) => [name, source];

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Interpreted_and_compiled_agree(string name, string source)
    {
        var interpreted = RunInterpreted(source);
        var compiled = RunCompiled(source);

        Assert.True(
            interpreted == compiled,
            $"'{name}' diverges between backends.\n" +
            $"  interpreted: {Show(interpreted)}\n" +
            $"  compiled:    {Show(compiled)}\n" +
            "One of the two is wrong; this assertion does not know which.");
    }

    [Theory]
    [MemberData(nameof(KnownDivergences))]
    public void A_recorded_divergence_still_diverges(string boardItem, string name, string source)
    {
        var interpreted = Attempt(() => RunInterpreted(source));
        var compiled = Attempt(() => RunCompiled(source));

        Assert.False(
            interpreted == compiled,
            $"{boardItem} '{name}' no longer diverges — both backends now produce " +
            $"{Show(interpreted)}.\n" +
            "If that was deliberate, move this case into Corpus() and delete the " +
            "entry from KnownDivergences() and the board.");
    }

    /// <summary>
    /// A divergence is just as real when one backend throws and the other does
    /// not, so the failure is folded into the compared value rather than escaping.
    /// </summary>
    private static string Attempt(Func<string> run)
    {
        try
        {
            return run();
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
            return $"<threw {inner.GetType().Name}: {inner.Message}>";
        }
    }

    private static string Show(string value) => value.Length == 0 ? "<empty>" : value.Replace("\n", " ⏎ ");

    /// <summary>
    /// The two backends do not share an output surface, and cannot: interpreted
    /// <c>echo</c> yields pipeline objects for a display engine to render, while
    /// the compiler inlines a <c>Console.WriteLine</c> because a standalone
    /// assembly has no pipeline to yield into. Comparing raw stdout would compare
    /// a printer against a pipeline and fail on every case.
    ///
    /// So each side is reduced to the one thing both genuinely mean — the
    /// sequence of values the program produced, one per line. Every corpus case
    /// passes a single argument to <c>echo</c>, so one value is one line on both
    /// sides.
    /// </summary>
    private static string Canonicalize(IEnumerable<string?> values) =>
        string.Join("\n", values.Select(v => (v ?? "null").Trim())).Trim();

    private string RunInterpreted(string source)
    {
        var engine = new ToshEngine(_runtime);
        var results = engine.ExecuteToListAsync(source).GetAwaiter().GetResult();

        return Canonicalize(results.Select(value => value?.ToString()));
    }

    private string RunCompiled(string source)
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse(source, "<differential>");

        Assert.True(parse.Diagnostics.Count == 0,
            $"parse errors: {string.Join(", ", parse.Diagnostics)}");

        var unit = Lowerer.Lower(parse, _runtime.Commands);

        // A fresh assembly name per case; AssemblyLoadContext caches otherwise.
        var assemblyName = $"ToshDiff_{Guid.NewGuid():N}";
        using var stream = new MemoryStream();
        var result = BoundUnitEmitter.Emit(unit, assemblyName, stream);

        Assert.True(result.IsClean,
            $"unexpected emit diagnostics: {string.Join(", ", result.UnsupportedShapes)}");

        var assembly = Assembly.Load(stream.ToArray());
        var program = assembly.GetType($"{assemblyName}.Program");
        Assert.NotNull(program);

        var main = program!.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(main);

        var originalOut = Console.Out;
        var capture = new StringWriter();

        try
        {
            Console.SetOut(capture);
            main!.Invoke(null, new object?[] { Array.Empty<string>() });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return Canonicalize(
            capture.ToString().Replace("\r", "").Split('\n'));
    }
}
