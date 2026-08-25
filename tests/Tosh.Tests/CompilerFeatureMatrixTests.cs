using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Current-state compiler feature matrix. This is intentionally a
/// baseline, not an aspirational test: when a feature moves from
/// unsupported -> permissive replay -> runtime/pure IL, update the
/// expected profile flags in this file as part of the implementation.
/// </summary>
[Collection(ConsoleSerialCollection.Name)]
public sealed class CompilerFeatureMatrixTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public CompilerFeatureMatrixTests(ToshRuntimeFixture fixture)
    {
        _runtime = fixture.Runtime;
    }

    public static IEnumerable<object[]> FeatureCases()
    {
        yield return Case(
            "core.literals-arithmetic",
            "Core expressions",
            "var x = 40\nvar y = 2\necho ($x + $y)",
            permissive: true,
            runtime: true,
            pure: true,
            "Pure IL plus the inlined echo fast path.");

        yield return Case(
            "functions.typed-top-level",
            "Functions",
            "func add(a: int, b: int) -> int { return $a + $b }\necho (add 20 22)",
            permissive: true,
            runtime: true,
            pure: true,
            "Fully typed top-level functions emit real CLR methods.");

        yield return Case(
            "functions.overload-set",
            "Functions",
            "func id(value: int) -> int { return $value }\nfunc id(value: string) -> string { return $value }",
            permissive: true,
            runtime: true,
            pure: true,
            "Overload sets emit distinct suffixed CLR methods; call sites resolve via OverloadIndex stamped at lowering time.");

        yield return Case(
            "functions.optional-rest-parameters",
            "Functions",
            "func sum(first: int, rest...: dynamic) -> dynamic { return $first }\necho (sum 1 2 3)",
            permissive: true,
            runtime: true,
            pure: true,
            "Declared functions with rest/optional parameters emit packed-argument CLR entrypoints.");

        yield return Case(
            "commands.builtin-host-dispatch",
            "Commands and pipelines",
            "ls /tmp",
            permissive: true,
            runtime: true,
            pure: false,
            "Builtin command dispatch is runtime-hosted Tier 2.");

        yield return Case(
            "commands.writeline-positional",
            "Commands and pipelines",
            "writeline \"answer\" 42 true",
            permissive: true,
            runtime: true,
            pure: true,
            "Positional writeline calls serialize and join directly in pure IL.");

        yield return Case(
            "blocks.pipeline-block",
            "Commands and pipelines",
            "seq 3 | where { _ > 1 }",
            permissive: true,
            runtime: true,
            pure: false,
            "Compiled block support is runtime-hosted, not pure IL.");

        yield return Case(
            "types.class-simple-shell",
            "Types",
            "class Point(x, y) { prop X = x\nprop Y = y }\nvar p = new Point(1, 2)\necho $p.X",
            permissive: true,
            runtime: true,
            pure: true,
            "Simple class shells use direct CLR fields/constructors.");

        yield return Case(
            "types.class-inheritance",
            "Types",
            "class Animal { prop Name = \"animal\" }\nclass Dog extends Animal { prop Breed = \"mutt\" }",
            permissive: true,
            runtime: true,
            pure: true,
            "Inherited classes emit a real CLR type hierarchy (base TypeBuilder as parent).");

        yield return Case(
            "types.class-defaulted-ctor-param",
            "Types",
            "class P(x, y = $x * 10) { prop Y = $y }",
            permissive: true,
            runtime: false,
            pure: false,
            "TS-P1-05: defaulted constructor parameters need the engine's callable default binder, so the class stays on Tier-3 source replay.");

        yield return Case(
            "types.class-defaulted-method-param",
            "Types",
            "class C { func m(a, b = $a * 2) { return $b } }",
            permissive: true,
            runtime: false,
            pure: false,
            "TS-P1-05: defaulted method parameters need the engine's callable default binder, so the class stays on Tier-3 source replay.");

        yield return Case(
            "types.class-construction-chain",
            "Types",
            "class MatrixCtorRoot(root: int) { prop R = $root }\n"
                + "class MatrixCtorMiddle(middle: int) extends MatrixCtorRoot(42) { prop M = $middle }\n"
                + "class MatrixCtorLeaf(leaf: int) extends MatrixCtorMiddle(41) { prop L = $leaf }\n"
                + "var value = new MatrixCtorLeaf(40)\necho $value.R",
            permissive: true,
            runtime: true,
            pure: false,
            "Complete CLR shell hierarchies bind each layer's constructor locals; inherited construction remains Tier 2 at the call site.");

        yield return Case(
            "types.class-implements-interface",
            "Types",
            "interface Runnable { func run() }\nclass Job implements Runnable { func run() { return \"ok\" } }",
            permissive: true,
            runtime: true,
            pure: true,
            "Classes implementing interfaces emit AddInterfaceImplementation at the CLR level.");

        yield return Case(
            "types.class-uses-trait",
            "Types",
            "trait Named { prop Name = \"unknown\" }\nclass Person uses Named { prop Name = \"Ada\" }",
            permissive: true,
            runtime: true,
            pure: true,
            "Traits emit CLR interfaces (DIM-capable) and classes can use them without type-definition replay.");

        yield return Case(
            "types.class-hermit-static",
            "Types",
            "hermit class MathBox { static func answer() { return 42 } }",
            permissive: true,
            runtime: true,
            pure: true,
            "Hermit/static-only classes emit CLR static class shells.");

        yield return Case(
            "types.record-simple-shell",
            "Types",
            "record Pair(x, y)\nvar p = new Pair(1, 2)\necho $p.x",
            permissive: true,
            runtime: true,
            pure: true,
            "Simple records emit CLR shell classes.");

        yield return Case(
            "types.interface-definition",
            "Types",
            "interface Printable { func print() }",
            permissive: true,
            runtime: true,
            pure: true,
            "Interfaces emit real CLR interface metadata shells.");

        yield return Case(
            "types.union-definition",
            "Types",
            "union Result { Ok(value) Err(message) }",
            permissive: true,
            runtime: true,
            pure: true,
            "Unions emit a CLR abstract base class + sealed variant subclasses.");

        yield return Case(
            "types.enum-definition",
            "Types",
            "enum Color { Red, Green, Blue }",
            permissive: true,
            runtime: true,
            pure: true,
            "Simple integral enums emit real CLR enum metadata.");

        yield return Case(
            "types.enum-non-integral",
            "Types",
            "enum Label: string { Good = \"good\", Bad = \"bad\" }",
            permissive: true,
            runtime: true,
            pure: true,
            "Non-integral / dynamic-value enums emit a CLR static class shell with `public static readonly object` fields populated in `.cctor`.");

        yield return Case(
            "args.named",
            "Args",
            "func tag(label, value) { echo $\"{$label}={$value}\" }\ntag(value = \"v\", label = \"k\")",
            permissive: true,
            runtime: true,
            pure: true,
            "Named arguments to compiled user functions bind by parameter name through `ToshHost.TryBuildOverloadInvocation`.");

        yield return Case(
            "args.splat",
            "Args",
            "func tag(label, value) { echo $\"{$label}={$value}\" }\nvar pair = [\"k\", \"v\"]\ntag ...$pair",
            permissive: true,
            runtime: true,
            pure: true,
            "Splatted arguments expand at runtime through `ToshHost.SpreadArgs` for command/user-function calls.");

        yield return Case(
            "types.struct-definition",
            "Types",
            "struct Point(x: int, y: int) { }",
            permissive: true,
            runtime: true,
            pure: true,
            "Structs emit real CLR value-type shells.");

        yield return Case(
            "types.type-alias-simple",
            "Types",
            "type MyStr = string",
            permissive: true,
            runtime: true,
            pure: true,
            "Simple type aliases (no refinement) emit a CLR sealed-class shell; no source replay needed.");

        yield return Case(
            "types.type-alias-refinement",
            "Types",
            "type Port = int where (_ >= 1 and _ <= 65535)",
            permissive: true,
            runtime: true,
            pure: true,
            "Refinement aliases emit CLR shells and register compiled alias metadata without executable source replay.");

        yield return Case(
            "types.event-definition",
            "Types and events",
            "event BuildCompleted { status = \"ok\" }",
            permissive: true,
            runtime: true,
            pure: true,
            "Event definitions emit a CLR sealed class shell.");

        yield return Case(
            "runes.definition",
            "Runes",
            "rune twice(body) { $body\n$body }",
            permissive: true,
            runtime: true,
            pure: false,
            "Rune definitions compile to a Tier-2 RegisterRuneFromSource call; definition-only scripts are runtime-clean.");

        yield return Case(
            "modules.pure-module-shell",
            "Modules",
            "module MathLib { var answer = 42\nfunc get() { return $answer } }\necho (MathLib.get())",
            permissive: true,
            runtime: true,
            pure: false,
            "Module shells are real CLR types but require runtime registration.");

        yield return Case(
            "modules.module-with-nested-class",
            "Modules",
            "module Models { class User(name) { prop Name = name } }",
            permissive: true,
            runtime: true,
            pure: true,
            "Modules containing simple class declarations now lift out of Tier-3 replay (first-class .NET plan, step 1).");

        yield return Case(
            "interop.require-statement",
            "Interop",
            "require Inventory from \"./inventory.tosh\"",
            permissive: true,
            runtime: true,
            pure: false,
            "Non-native require statements compile to a Tier-2 RequireModule call; the target is loaded at runtime without replaying the parent script.");

        yield return Case(
            "interop.native-bind",
            "Interop",
            "bind native \"libc.so.6\" as LibC { func abs(value: int) -> int }",
            permissive: true,
            runtime: true,
            pure: true,
            "Primitive-typed native binds lift to a CLR P/Invoke class (first-class .NET plan, step 7 phase 1).");

        // ── Operators routed through OperatorEvaluator runtime fallback ──
        yield return Case(
            "operators.power",
            "Operators",
            "echo (2 ** 8)",
            permissive: true,
            runtime: true,
            pure: true,
            "Power operator '**' routes through OperatorEvaluator.EvaluateBinary (no source replay).");

        yield return Case(
            "operators.floor-division",
            "Operators",
            "echo (10 // 3)",
            permissive: true,
            runtime: true,
            pure: true,
            "Floor-division '//' routes through OperatorEvaluator.EvaluateBinary.");

        yield return Case(
            "operators.regex-match",
            "Operators",
            "echo (\"abc\" =~ /a/)",
            permissive: true,
            runtime: true,
            pure: true,
            "Regex-match '=~' routes through OperatorEvaluator.EvaluateBinary.");

        yield return Case(
            "operators.in-membership",
            "Operators",
            "echo (3 in [1, 2, 3])",
            permissive: true,
            runtime: true,
            pure: true,
            "Membership 'in' routes through OperatorEvaluator.EvaluateBinary.");

        yield return Case(
            "operators.string-relational",
            "Operators",
            "echo (\"hello\" starts-with \"he\")",
            permissive: true,
            runtime: true,
            pure: true,
            "String-relational predicates ('starts-with', 'ends-with') route through OperatorEvaluator.");

        yield return Case(
            "operators.is-type",
            "Operators",
            "echo (1 is int)",
            permissive: true,
            runtime: true,
            pure: true,
            "Type-test 'is' routes through OperatorEvaluator.EvaluateBinary.");

        yield return Case(
            "operators.null-coalesce",
            "Operators",
            "var x = null\necho ($x ?? 5)",
            permissive: true,
            runtime: true,
            pure: true,
            "Null-coalesce '??' emits inline branchless IL (Dup; Brtrue; Pop; right).");

        yield return Case(
            "operators.short-circuit-and",
            "Operators",
            "echo (true and 1 == 1)",
            permissive: true,
            runtime: true,
            pure: true,
            "Short-circuit 'and' emits inline IL using OperatorEvaluator.ToBoolean — right side skipped when left is falsey.");

        yield return Case(
            "operators.short-circuit-or",
            "Operators",
            "echo (false or 2 == 2)",
            permissive: true,
            runtime: true,
            pure: true,
            "Short-circuit 'or' emits inline IL using OperatorEvaluator.ToBoolean — right side skipped when left is truthy.");

        yield return Case(
            "operators.unary-not",
            "Operators",
            "echo (not true)",
            permissive: true,
            runtime: true,
            pure: true,
            "Unary 'not' routes through OperatorEvaluator.EvaluateUnary.");

        // ── Control flow with parenthesized predicates ──
        yield return Case(
            "control.if-else",
            "Control flow",
            "if (1 < 2) { echo a } else { echo b }",
            permissive: true,
            runtime: true,
            pure: true,
            "if/else lowers to native IL branch+block emission.");

        yield return Case(
            "control.while",
            "Control flow",
            "var i = 0\nwhile ($i < 3) { $i = $i + 1 }",
            permissive: true,
            runtime: true,
            pure: true,
            "while-loop lowers to native IL with branch back-edges.");

        yield return Case(
            "control.until",
            "Control flow",
            "var i = 0\nuntil ($i >= 3) { $i = $i + 1 }",
            permissive: true,
            runtime: true,
            pure: true,
            "until-loop lowers identically to while with negated condition.");

        yield return Case(
            "control.switch",
            "Control flow",
            "switch (1) { case 1 { echo one } default { echo other } }",
            permissive: true,
            runtime: true,
            pure: true,
            "switch lowers to a chain of equality+branch IL.");

        yield return Case(
            "control.try-catch",
            "Control flow",
            "try { echo a } catch (e) { echo $e }",
            permissive: true,
            runtime: true,
            pure: true,
            "try/catch emits .NET exception handlers natively.");

        yield return Case(
            "control.throw",
            "Control flow",
            "throw \"boom\"",
            permissive: true,
            runtime: true,
            pure: true,
            "throw emits OpCodes.Throw natively.");

        yield return Case(
            "control.defer",
            "Control flow",
            "func f() { defer { echo end }\necho start }\nf",
            permissive: true,
            runtime: true,
            pure: true,
            "defer lowers to nested try/finally around remaining block statements.");

        yield return Case(
            "control.break-continue",
            "Control flow",
            "for i in (1..5) { if ($i == 3) { break }\nif ($i == 1) { continue }\necho $i }",
            permissive: true,
            runtime: true,
            pure: true,
            "break/continue emit OpCodes.Leave to the loop's labels.");

        yield return Case(
            "control.yield",
            "Control flow",
            "func gen() { yield 1\nyield 2 }",
            permissive: true,
            runtime: true,
            pure: true,
            "yield in functions emits the generator-body lowering directly.");

        // ── Variables / patterns ──
        yield return Case(
            "vars.destructuring-array",
            "Variables",
            "var [a, b] = [1, 2]\necho $a",
            permissive: true,
            runtime: true,
            pure: true,
            "Array destructuring (`var [a, b] = …`) emits ToshHost.DestructureArray plus per-symbol stores.");

        yield return Case(
            "vars.destructuring-record",
            "Variables",
            "var rec = {% \"name\" => \"Alice\" %}\nvar { name } = $rec",
            permissive: true,
            runtime: true,
            pure: true,
            "Record destructuring (`var { a } = …`) emits ToshHost.DestructureRecord plus per-symbol stores.");

        yield return Case(
            "vars.tuple-assignment",
            "Variables",
            "var a = 0\nvar b = 0\n($a, $b) = [1, 2]\necho $a",
            permissive: true,
            runtime: true,
            pure: true,
            "Tuple assignment resolves targets by symbol and stages every conversion before committing atomically.");

        yield return Case(
            "vars.member-assignment",
            "Variables",
            "class Box { prop V = 0 }\nvar b = new Box()\n$b.V = 5",
            permissive: true,
            runtime: true,
            pure: true,
            "Member assignment ($obj.X = v) emits direct field/property stores.");

        yield return Case(
            "vars.null-coalescing-assignment",
            "Variables",
            "var x = null\n$x ??= 5\necho $x",
            permissive: true,
            runtime: true,
            pure: true,
            "Null-coalescing assignment short-circuits the RHS and stores only when the target is null.");

        yield return Case(
            "vars.using-statement",
            "Variables",
            "using System\necho 1",
            permissive: true,
            runtime: true,
            pure: true,
            "using/import affects binder resolution; emits as no-op in compiled mode.");

        // ── Expressions / literals ──
        yield return Case(
            "expr.range",
            "Expressions",
            "for i in (1..5) { echo $i }",
            permissive: true,
            runtime: true,
            pure: true,
            "Numeric ranges (a..b) emit a counted-loop iterator natively.");

        yield return Case(
            "expr.list-literal",
            "Expressions",
            "var xs = [1, 2, 3]\necho $xs.Count",
            permissive: true,
            runtime: true,
            pure: true,
            "List literals emit `new List<object>` plus per-item Adds.");

        yield return Case(
            "expr.dict-literal",
            "Expressions",
            "var m = {% \"k\" => 1 %}\necho $m.Count",
            permissive: true,
            runtime: true,
            pure: true,
            "Dict literals emit `new Dictionary<object,object>` plus indexer sets.");

        yield return Case(
            "expr.set-literal",
            "Expressions",
            "var s = {: 1, 2, 2 :}\necho $s.Count",
            permissive: true,
            runtime: true,
            pure: true,
            "Set literals emit `new HashSet<object>` plus Adds.");

        yield return Case(
            "expr.tuple-literal",
            "Expressions",
            "var t = (1, 2)\necho $t.Item1",
            permissive: true,
            runtime: true,
            pure: true,
            "Tuple literals emit `new ToshTuple(object[])`.");

        yield return Case(
            "expr.string-interpolation",
            "Expressions",
            "var n = 7\necho $\"value={$n}\"",
            permissive: true,
            runtime: true,
            pure: true,
            "Interpolated strings lower to string.Concat / Format chains natively.");

        yield return Case(
            "expr.lambda-callable",
            "Expressions",
            "var f = func(x) => ($x * 2)",
            permissive: true,
            runtime: true,
            pure: true,
            "Lambda expressions emit ToshCallable wrappers; declaration alone is pure IL.");

        yield return Case(
            "expr.spread",
            "Expressions",
            "var l = [1, 2, 3]\nvar m = [0, ...$l, 4]\necho $m.Count",
            permissive: true,
            runtime: true,
            pure: true,
            "Spread (`...$x`) inside list literals emits AddRange-style runtime walks.");

        yield return Case(
            "expr.member-access",
            "Expressions",
            "var p = {% \"x\" => 7 %}\necho $p[\"x\"]",
            permissive: true,
            runtime: true,
            pure: true,
            "Member/index access emits direct property/indexer calls.");

        // ── Strings & redirections ──
        yield return Case(
            "strings.heredoc",
            "Strings",
            "var s = '''\nline\n'''",
            permissive: true,
            runtime: true,
            pure: true,
            "Heredoc strings lower to plain string literals.");

        yield return Case(
            "strings.regex-literal",
            "Strings",
            "var r = /\\d+/",
            permissive: true,
            runtime: true,
            pure: false,
            "Regex literals (/.../) lower through a runtime command-invocation pathway (Tier 2).");

        // ── Pipelines and redirections ──
        yield return Case(
            "redirection.out-file",
            "Pipelines and redirections",
            "echo hi out> /tmp/x.txt",
            permissive: true,
            runtime: true,
            pure: false,
            "File redirections route through ToshHost.BeginRedirection at runtime (Tier 2).");

        // ── Modifiers / declarations ──
        yield return Case(
            "declaration.fixed-var",
            "Declarations",
            "fixed var pi = 3.14",
            permissive: true,
            runtime: true,
            pure: false,
            "fixed-var declarations register through the host (Tier 2).");

        yield return Case(
            "declaration.annotated-var",
            "Declarations",
            "var count: long = 1",
            permissive: true,
            runtime: true,
            pure: false,
            "Annotated mutable variables use the canonical host conversion path on every write (Tier 2).");

        yield return Case(
            "declaration.refinement-var",
            "Declarations",
            "var port: int where (_ > 0) = 8080",
            permissive: true,
            runtime: true,
            pure: false,
            "Refinement-typed `var` uses the canonical host annotation converter (Tier 2) before storing the value.");

        // ── Subcommand trees ──
        yield return Case(
            "subcommand.flag",
            "Subcommands",
            "flag verbose: bool\necho $verbose",
            permissive: true,
            runtime: true,
            pure: false,
            "Subcommand flags drive argv-bound entry points; runtime tier (Tier 2) by design.");

        // ── Async / await as Tier-2 commands ──
        yield return Case(
            "concurrency.async-await",
            "Concurrency",
            "var f = async { sleep 0 }\nawait $f",
            permissive: true,
            runtime: true,
            pure: false,
            "async/await are stdlib commands (AsyncCommand/AwaitCommand) — they ride the standard Tier-2 builtin-dispatch path; no state-machine lowering needed.");
    }

    [Theory]
    [MemberData(nameof(FeatureCases))]
    public void Current_emitter_profile_matrix_matches_known_language_surface(FeatureCase feature)
    {
        foreach (var profile in Profiles)
        {
            var expected = feature.Expected(profile);
            var outcome = TryEmit(feature.Source, profile);

            Assert.False(
                outcome.Threw,
                $"Feature '{feature.Id}' threw during {profile} emit instead of returning diagnostics: {outcome.ExceptionText}");
            Assert.True(
                outcome.IsClean == expected,
                $"Feature '{feature.Id}' under {profile} expected clean={expected} but got clean={outcome.IsClean}.\n" +
                $"Note: {feature.Note}\n" +
                $"Diagnostics: {outcome.Diagnostics ?? "<none>"}");
        }
    }

    /// <summary>
    /// CI gate: every feature row marked <c>runtime: true</c> must compile
    /// cleanly under <see cref="CompileProfile.Runtime"/>. This is a
    /// regression contract — adding source replay to a previously
    /// runtime-clean feature is a breaking change that must be deliberate.
    /// Run in isolation with:
    ///   dotnet test --filter "FullyQualifiedName~Runtime_profile_gate_is_clean"
    /// </summary>
    public static IEnumerable<object[]> RuntimeGateCases() =>
        FeatureCases()
            .Where(row => ((FeatureCase)row[0]).Runtime)
            .ToList();

    [Theory]
    [MemberData(nameof(RuntimeGateCases))]
    public void Runtime_profile_gate_is_clean(FeatureCase feature)
    {
        var outcome = TryEmit(feature.Source, CompileProfile.Runtime);

        Assert.False(
            outcome.Threw,
            $"Runtime gate '{feature.Id}' threw: {outcome.ExceptionText}");
        Assert.True(
            outcome.IsClean,
            $"Runtime gate REGRESSION: '{feature.Id}' was previously runtime-clean but now has unsupported shapes.\n" +
            $"Note: {feature.Note}\n" +
            $"Diagnostics: {outcome.Diagnostics ?? "<none>"}");
    }

    /// <summary>
    /// Conformance cases: observable, not just emit-clean. Each
    /// entry compiles the source, loads the assembly, invokes
    /// <c>Main</c>, and asserts the captured stdout matches.
    /// Filesystem cases use temp paths threaded into the source via
    /// <see cref="ConformanceCase.Render"/>.
    /// </summary>
    public static IEnumerable<object[]> ConformanceCases()
    {
        yield return ExecCase(
            "core.echo",
            "echo 42",
            stdout: "42",
            "echo fast path");

        yield return ExecCase(
            "core.arith",
            "var x = 40\nvar y = 2\necho ($x + $y)",
            stdout: "42",
            "arithmetic + var");

        yield return ExecCase(
            "redirection.out-file",
            "echo hi out> \"{TMPFILE}\"",
            stdout: "",
            "redirection writes to file (no stdout)",
            expectedFile: "hi");

        yield return ExecCase(
            "redirection.append",
            "echo two out>> \"{TMPFILE}\"",
            stdout: "",
            "redirection appends to file",
            expectedFile: "one\ntwo",
            seedFile: "one\n");

        yield return ExecCase(
            "redirection.in-file",
            "echo (read-line) in< \"{TMPFILE}\"",
            stdout: "first",
            "input redirection feeds Console.In from a file",
            seedFile: "first\nsecond\n");

        yield return ExecCase(
            "redirection.in-pipeline",
            "cat in< \"{TMPFILE}\"",
            stdout: "alpha\nbeta",
            "input redirection seeds pipeline-input commands like cat",
            seedFile: "alpha\nbeta\n");

        yield return ExecCase(
            "func.return",
            "func add(a, b) { return $a + $b }\necho (add 2 3)",
            stdout: "5",
            "user function with return");

        yield return ExecCase(
            "control.if-else",
            "if (1 < 2) { echo yes } else { echo no }",
            stdout: "yes",
            "if/else");

        yield return ExecCase(
            "control.while",
            "var i = 0\nwhile ($i < 3) { echo $i\n$i = $i + 1 }",
            stdout: "0\n1\n2",
            "while loop");

        yield return ExecCase(
            "named-args",
            "func tag(label, value) { echo $\"{$label}={$value}\" }\ntag(value = \"v\", label = \"k\")",
            stdout: "k=v",
            "named args route by parameter name");

        yield return ExecCase(
            "defaults.func-chain",
            "func f(a: int, b: int = $a + 1, c: int = $b + 1) -> string { return $\"{$a},{$b},{$c}\" }\necho (f 1)",
            stdout: "1,2,3",
            "TS-P1-05: defaults evaluate left-to-right with earlier parameters visible");

        yield return ExecCase(
            "defaults.func-lexical-call-time",
            "var g: int = 1\nfunc f(x: int = $g) -> int { return $x }\necho (f)\n$g = 2\necho (f)",
            stdout: "1\n2",
            "TS-P1-05: defaults evaluate at call time against the captured lexical scope");

        yield return ExecCase(
            "defaults.class-ctor-and-method",
            "class P(x, y = $x * 10) { prop Y = $y }\necho ((new P(3)).Y)\nclass C { func m(a, b = $a * 2) { return $b } }\necho ((new C()).m(5))",
            stdout: "30\n10",
            "TS-P1-05: defaulted ctor/method parameters resolve through engine replay");

        yield return ExecCase(
            "defaults.named-arg-gap",
            "func f(a, b = $a + 1, c = $b * 10) { return $\"{$a},{$b},{$c}\" }\necho (f(1, c = 99))",
            stdout: "1,2,99",
            "TS-P1-05: named arguments bind before the remaining defaults fill the gap");

        yield return ExecCase(
            "pipeline.value-single-collapse",
            "var xs = [1, 2, 3]\nvar n = ($xs | count)\necho $n",
            stdout: "3",
            "TS-P1-20: a one-item value pipeline collapses to the item, not a single-element list");

        yield return ExecCase(
            "pipeline.value-literal-seed-collapse",
            "var n = ([1, 2, 3] | count)\necho $n",
            stdout: "3",
            "TS-P1-20: literal-seeded value pipelines collapse identically to variable-seeded ones");

        yield return ExecCase(
            "pipeline.value-empty-is-null",
            "var n = ([1, 2, 3] | where { _ > 99 })\necho ($n == null ? \"NULL\" : \"NOTNULL\")",
            stdout: "NULL",
            "TS-P1-20: a value pipeline that yields nothing produces null");

        yield return ExecCase(
            "pipeline.sequence-source-keeps-items",
            "for x in ([1, 2, 3] | each { _ }) { echo $x }",
            stdout: "1\n2\n3",
            "TS-P1-20: iteration sources keep every item rather than demanding a single value");
    }

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public void Compiled_output_matches_expected_observable_behavior(ConformanceCase c)
    {
        string? tmpPath = null;
        if (c.Source.Contains("{TMPFILE}"))
        {
            tmpPath = Path.Combine(Path.GetTempPath(),
                $"tosh_conf_{c.Id.Replace('.', '_')}_{Guid.NewGuid():N}.txt");
            if (c.SeedFile is not null)
            {
                File.WriteAllText(tmpPath, c.SeedFile);
            }
        }

        try
        {
            var source = c.Render(tmpPath);
            var engine = new ToshEngine(_runtime);
            var parse = engine.Parse(source, "<conformance>");
            Assert.True(parse.Diagnostics.Count == 0,
                $"parse errors in '{c.Id}': {string.Join(", ", parse.Diagnostics)}");
            var unit = Lowerer.Lower(parse, _runtime.Commands);

            var asmName = $"ConformanceMatrix_{Guid.NewGuid():N}";
            using var stream = new MemoryStream();
            var result = BoundUnitEmitter.Emit(unit, asmName, stream);
            Assert.True(result.IsClean,
                $"'{c.Id}' had unsupported shapes: {string.Join(", ", result.UnsupportedShapes)}");

            var asm = System.Reflection.Assembly.Load(stream.ToArray());
            var program = asm.GetType($"{asmName}.Program");
            Assert.NotNull(program);
            var main = program!.GetMethod("Main",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.NotNull(main);

            var capture = new StringWriter();
            var origOut = Console.Out;
            Console.SetOut(capture);
            try { main!.Invoke(null, new object?[] { Array.Empty<string>() }); }
            finally { Console.SetOut(origOut); }

            var actualStdout = capture.ToString().Replace("\r\n", "\n").TrimEnd('\n');
            Assert.Equal(c.ExpectedStdout, actualStdout);

            if (c.ExpectedFile is not null && tmpPath is not null)
            {
                Assert.True(File.Exists(tmpPath),
                    $"'{c.Id}' expected file '{tmpPath}' to exist");
                var fileContent = File.ReadAllText(tmpPath).Replace("\r\n", "\n").TrimEnd('\n');
                Assert.Equal(c.ExpectedFile, fileContent);
            }
        }
        finally
        {
            if (tmpPath is not null && File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }

    private EmitOutcome TryEmit(string source, CompileProfile profile)
    {
        try
        {
            var engine = new ToshEngine(_runtime);
            var parse = engine.Parse(source, "<compiler-feature-matrix>");
            if (parse.Diagnostics.Count > 0)
            {
                return EmitOutcome.Rejected(
                    string.Join(Environment.NewLine, parse.Diagnostics));
            }

            var unit = Lowerer.Lower(parse, _runtime.Commands);
            using var stream = new MemoryStream();
            var result = BoundUnitEmitter.Emit(
                unit,
                $"ToshFeatureMatrix_{Guid.NewGuid():N}",
                stream,
                profile);

            return result.IsClean
                ? EmitOutcome.Clean()
                : EmitOutcome.Rejected(string.Join(Environment.NewLine, result.UnsupportedShapes));
        }
        catch (Exception ex)
        {
            return EmitOutcome.FromException(ex);
        }
    }

    private static object[] Case(
        string id,
        string category,
        string source,
        bool permissive,
        bool runtime,
        bool pure,
        string note)
    {
        return [new FeatureCase(id, category, source, permissive, runtime, pure, note)];
    }

    private static object[] ExecCase(
        string id,
        string source,
        string stdout,
        string note,
        string? expectedFile = null,
        string? seedFile = null)
    {
        return [new ConformanceCase(id, source, stdout, note, expectedFile, seedFile)];
    }

    public sealed record ConformanceCase(
        string Id,
        string Source,
        string ExpectedStdout,
        string Note,
        string? ExpectedFile,
        string? SeedFile)
    {
        public string Render(string? tmpPath) =>
            tmpPath is null ? Source : Source.Replace("{TMPFILE}", tmpPath);

        public override string ToString() => $"Conformance: {Id}";
    }

    private static readonly CompileProfile[] Profiles =
    [
        CompileProfile.Permissive,
        CompileProfile.Runtime,
        CompileProfile.Pure,
    ];

    public sealed record FeatureCase(
        string Id,
        string Category,
        string Source,
        bool Permissive,
        bool Runtime,
        bool Pure,
        string Note)
    {
        public bool Expected(CompileProfile profile)
        {
            return profile switch
            {
                CompileProfile.Permissive => Permissive,
                CompileProfile.Runtime => Runtime,
                CompileProfile.Pure => Pure,
                _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
            };
        }

        public override string ToString() => $"{Category}: {Id}";
    }

    private readonly record struct EmitOutcome(
        bool IsClean,
        bool Threw,
        string? Diagnostics,
        string? ExceptionText)
    {
        public static EmitOutcome Clean() => new(true, false, null, null);
        public static EmitOutcome Rejected(string diagnostics) => new(false, false, diagnostics, null);
        public static EmitOutcome FromException(Exception exception) =>
            new(false, true, null, $"{exception.GetType().Name}: {exception.Message}");
    }
}
