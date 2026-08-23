using System.Reflection;
using System.Runtime.CompilerServices;
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

        yield return Case(
            "module-simple-type-alias",
            "export module M {\n    export type Meters = int\n}\nvar d: M.Meters = 5\necho $d");

        yield return Case(
            "refinement-coerces-a-negative",
            "type PosInt = int where _ > 0 coerce (_ == 0 ? 1 : Math.abs(_))\n"
            + "var p: PosInt = -21\necho $p");

        // ── Refinement coercion: TOAST-0068 ───────────────────────────────
        yield return Case(
            "refinement-coerced-value-has-base-type",
            "type TimeoutMs = int where (_ > 0 and _ <= 300000) coerce Math.Clamp(_, 0, 300000)\n"
            + "var t: TimeoutMs = 999999\necho $t.GetType().FullName");

        // ── Module methods without source replay: TOAST-0035 ──────────────
        // These are the shapes that stopped being replayed. They are here as well as in
        // SourceReplaySurfaceTests because that one asserts a module *emits* and this one
        // asserts it answers — a module method emitted cleanly and returned null for an
        // expression body until the trailing-expression collapse was applied to it.
        yield return Case(
            "module-method-expression-body",
            "export module M {\n    export func Add(a: int, b: int) -> int => $a + $b\n}\n"
            + "var r: int = M.Add(1, 5)\necho $r");

        yield return Case(
            "module-method-default-parameter",
            "export module M {\n    export func Add(a: int, b: int = 2) -> int => $a + $b\n}\n"
            + "var r: int = M.Add(1)\necho $r");

        // A block argument inside a module method emits a helper on `Program`, and a module
        // shell is a different type. Both the ordering and the visibility of that helper
        // were wrong, and neither was reachable while such modules were replayed: the first
        // threw "Unable to change after type has been created" at compile time, the second a
        // MethodAccessException at the first call.
        yield return Case(
            "module-method-block-argument",
            "export module M {\n"
            + "    export func Doubled(items: list<int>, factor: int = 2) -> int {\n"
            + "        return ($items | each { $_ * $factor } | count)\n"
            + "    }\n}\n"
            + "var r: int = M.Doubled([1, 2, 3])\necho $r");

        yield return Case(
            "module-method-rest-parameter",
            "export module M {\n    export func Count(items...) -> int => ($items | count)\n}\n"
            + "var r: int = M.Count(1, 2, 3)\necho $r");

        // ── Record literal inference: TOAST-0034 ──────────────────────────
        yield return Case(
            "record-literal-fields",
            "var r = {| a = 1, b = \"x\" |}\necho $\"{$r.a}-{$r.b}\"");

        yield return Case(
            "record-literal-nested",
            "var r = {| a = {| b = 2 |} |}\necho $r.a.b");

        // ── void / nothing: TOAST-0046 ────────────────────────────────────
        yield return Case(
            "void-return-writes-without-producing",
            "func f() -> void { writeline \"hi\" }\nf\necho \"after\"");

        yield return Case(
            "nothing-return-writes-without-producing",
            "func f() -> nothing { writeline \"hi\" }\nf\necho \"after\"");

        // ── Function types: TOAST-0036 ────────────────────────────────────
        yield return Case(
            "function-type-parameter",
            "func dbl(x: int) -> int => $x * 2\n"
            + "func apply(g: func(int) -> int, v: int) -> int => $g($v)\n"
            + "echo (apply &dbl 21)");

        yield return Case(
            "function-type-variable",
            "func dbl(x: int) -> int => $x * 2\nvar f: func(int) -> int = &dbl\necho $f(21)");

        yield return Case(
            "function-type-inferred-lambda",
            "var lam = func(x: int) -> int => $x + 1\necho $lam(41)");

        yield return Case(
            "function-type-returning-function",
            "func adder(n: int) -> func(int) -> int {\n"
            + "    return func(x: int) -> int => $x + $n\n"
            + "}\n"
            + "var a: func(int) -> int = (adder 10)\n"
            + "echo $a(32)");

        // ── Tuple annotations: TOAST-0050 ─────────────────────────────────
        yield return Case(
            "tuple-annotation-var",
            "var t: (int, string) = (1, \"a\")\necho $t.Item1");

        yield return Case(
            "tuple-annotation-return",
            "func two() -> (int, string) {\n return (2, \"b\")\n}\nvar r = two\necho $r.Item2");

        yield return Case(
            "tuple-annotation-nested",
            "var t: (int, (string, bool)) = (1, (\"x\", true))\necho $t.Item2.Item1");

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
            "base-annotated-variable-accepts-subclass",
            Hierarchy + "var b: DiffBase = new DiffLeaf(4)\necho $b.Kind");

        yield return Case(
            "two-level-inheritance",
            "class DiffA { prop Tag: string = \"a\" }\nclass DiffB extends DiffA { prop Tag: string = \"b\" }\nclass DiffC extends DiffB { prop Tag: string = \"c\" }\nvar x: DiffA = new DiffC()\necho $x.Tag");

        // ── `TOAST-0030` cause A and D, fixed 2026-08-21 ──────────────────
        //
        // Moved up from `KnownDivergences`. Both were one missing lookup each, and both
        // lookups now live in `Tosh.Runtime` where the two backends already meet.

        // `new` resolves Tōast's own type names, not only CLR ones. The compiled side had
        // `Type.GetType`, which needs an assembly qualifier, so every bare name failed.
        yield return Case("new-error-resolves", "echo ((new Error(\"x\")).Message)");

        // And the failure was throwable, which is what made it two divergences: a
        // `try`/`catch` around `new Error` caught the *resolution error* and then reported
        // that the caught value was not an `Error` — true of an InvalidOperationException.
        yield return Case(
            "caught-error-is-an-error",
            "try { throw new Error(\"x\") } catch (e) { echo ($e is Error) }");

        yield return Case(
            "caught-error-is-a-failure",
            "try { throw new Error(\"x\") } catch (e) { echo ($e is Failure) }");

        // `is` against a declared base, at one and two levels. A compiled class is a real
        // emitted CLR type with real inheritance; what it does not have is
        // `IShellTypeCheckable`, which is how the interpreter's instances walk themselves.
        // So the walk moved into the shared operator instead.
        yield return Case(
            "is-declared-base-one-level",
            "class DiffIsA { }\nclass DiffIsB extends DiffIsA { }\necho ((new DiffIsB()) is DiffIsA)");

        yield return Case(
            "is-declared-base-two-levels",
            "class DiffIsC { }\nclass DiffIsD extends DiffIsC { }\nclass DiffIsE extends DiffIsD { }\n"
            + "echo ((new DiffIsE()) is DiffIsC)");

        // The control that keeps the walk honest: an unrelated class is still not a match.
        yield return Case(
            "is-unrelated-class-is-false",
            "class DiffIsF { }\nclass DiffIsG { }\necho ((new DiffIsG()) is DiffIsF)");

        // ── `TOAST-0030` cause C, fixed 2026-08-21 ───────────────────────
        //
        // A message is part of the behaviour, not decoration on it. `§What null Means` says
        // reaching a member of `null` reports it *and* says how to opt out, so a backend
        // raising `NullReferenceException: member access 'Length' on null target` had not
        // implemented that sentence. Both texts now come from `ToastMessages`.
        yield return Case("member-of-null-reports-it", "var x = null\necho $x.Length");

        // And the *kind* matters as much as the words: the compiled side raised a host
        // exception, which `catch (e) { $e is Diagnostic }` cannot see as the language
        // defines it. Asserted here rather than assumed, because comparing rendered output
        // alone would have accepted two different exception types with matching text.
        yield return Case(
            "member-of-null-is-a-diagnostic",
            "try { var x = null\necho $x.Length } catch (e) { echo ($e is Diagnostic) }");

        yield return Case("null-concatenation-explains-itself", "echo (null + \"a\")");

        // ── `TOAST-0030` cause B, fixed 2026-08-21 ───────────────────────
        //
        // The compiled head had its own answer to "which values are sequences?", and it
        // was not the language's. `SeedFromValue` walked any `IEnumerable` and
        // special-cased `string`; §Collection Shape says a dictionary and a record are
        // values with named parts, and that a range is a sequence. So dictionaries and
        // records spread and ranges did not — wrong in both directions at once.
        //
        // Only the dictionary was recorded. The rest were found by probing, which is the
        // argument for fixing causes rather than symptoms.
        yield return Case("dictionary-is-one-value", "echo ({% \"a\" => 1, \"b\" => 2 %} | count)");
        yield return Case("record-is-one-value", "echo ({| a = 1, b = 2 |} | count)");
        yield return Case("record-arrives-whole", "echo $\"{({| a = 1 |} | first)}\"");
        yield return Case("dictionary-arrives-whole", "echo $\"{({% \"a\" => 1 %} | first)}\"");
        yield return Case("a-range-is-a-sequence", "echo (1..3 | count)");

        // Controls: the values that were already right must stay right. Getting shape from
        // one predicate could have been done by making everything a single value.
        yield return Case("array-is-still-a-sequence", "echo ([1, 2, 3] | count)");
        yield return Case("nested-arrays-are-items", "echo ([[1, 2], [3]] | count)");
        yield return Case("a-string-is-one-value", "echo (\"abc\" | count)");
        yield return Case("a-set-is-a-sequence", "echo ({: 1, 2, 3 :} | count)");

        // ── `TOAST-0030` cause A completed, and `TOAST-0031`'s deferred case ──
        //
        // `class E extends Error` did not compile at all: any base not declared in the same
        // unit sent the whole declaration to source replay, which failed at runtime with
        // "Command 'class' was not found". An emitted type can now derive from a real CLR
        // parent, which is what `Error` and `Exception` are.
        yield return Case(
            "declared-error-type-is-an-error",
            "class DiffErr extends Error { }\necho ((new DiffErr()) is Error)");

        yield return Case(
            "declared-error-type-at-depth",
            "class DiffErrA extends Error { }\nclass DiffErrB extends DiffErrA { }\n"
            + "echo ((new DiffErrB()) is Error)");

        // Moved from `TOAST-0031`, which could not add it: a corpus case here would have
        // asserted that `class E extends Error` does not compile, which was already
        // recorded. `Failure` names anything the language raised; `Error` and `Diagnostic`
        // are the two kinds, and a plain thrown value is none of them.
        yield return Case(
            "a-declared-error-is-a-failure",
            "class DiffErrC extends Error { }\necho ((new DiffErrC()) is Failure)");

        yield return Case(
            "a-declared-error-is-not-a-diagnostic",
            "class DiffErrD extends Error { }\necho ((new DiffErrD()) is Diagnostic)");

        yield return Case(
            "a-runtime-diagnostic-is-a-failure",
            "try { (1 / 0) } catch (e) { echo $\"{($e is Failure)} {($e is Diagnostic)} {($e is Error)}\" }");

        // The control for deriving from an external base: a class with no base is still
        // rooted at `object`, and an unrelated declared error is still not this one.
        yield return Case(
            "an-ordinary-class-is-not-an-error",
            "class DiffPlain { }\necho ((new DiffPlain()) is Error)");

        // ── `TOAST-0044`: short-circuit operators inside a loop condition ──
        //
        // `EmitLogicalOr` ended `br.s done / truthy: ldc.i4.1 / done:` — two labels one byte
        // apart. That single-byte instruction was **dropped when the assembly was
        // persisted**, so every branch after it was one byte off and the code branched into
        // the middle of an instruction. The program compiled cleanly and died with
        // `InvalidProgramException`.
        //
        // These run the loop rather than testing the operator in isolation, because the
        // defect needed the condition to be re-entered.
        yield return Case(
            "or-in-a-loop-condition",
            "class DiffOr {\n"
            + "    prop N: int = 0\n"
            + "    func Run() -> int {\n"
            + "        while (($this.N < 3) or ($this.N < 0)) { $this.N = $this.N + 1 }\n"
            + "        return $this.N\n"
            + "    }\n"
            + "}\necho ((new DiffOr()).Run())");

        yield return Case(
            "and-in-a-loop-condition",
            "class DiffAnd {\n"
            + "    prop N: int = 0\n"
            + "    func Run() -> int {\n"
            + "        while (($this.N < 3) and ($this.N >= 0)) { $this.N = $this.N + 1 }\n"
            + "        return $this.N\n"
            + "    }\n"
            + "}\necho ((new DiffAnd()).Run())");

        // A chained comparison had the same shape and the same defect.
        yield return Case("chained-comparison", "var n = 5\necho (1 < $n and $n < 10)");

        // ── `TOAST-0045`: a record literal is a record, not a dict ────────
        //
        // The emitter built a `Dictionary<string, object?>`, whose shell type is `dict`,
        // where the interpreter builds an `ExpandoObject`, whose shell type is `record`. So
        // `{| a = 1 |}` was a record interpreted and a dict compiled, and
        // `func f() -> record` refused a function returning a record literal.
        yield return Case("record-literal-is-a-record", "echo ({| a = 1 |} | type-of | get Name)");
        yield return Case(
            "record-return-annotation",
            "func mk(a: string) -> record { return {| A = $a |} }\necho (mk \"x\").A");
        // The control: a dict literal is still a dict.
        yield return Case("dict-literal-is-a-dict", "echo ({% \"a\" => 1 %} | type-of | get Name)");

        // ── `TOAST-0044`: a class assigns its own private property ────────
        //
        // `shy prop` is emitted as a private field, and member *writes* went through
        // reflection over public members — so a class could not assign its own private
        // property, compiled, from inside the class that declares it.
        yield return Case(
            "shy-property-is-assignable",
            "class DiffShy {\n"
            + "    shy prop n_: int = 0\n"
            + "    func Bump() -> int { $this.n_ = $this.n_ + 1\n        return $this.n_ }\n"
            + "}\necho ((new DiffShy()).Bump())");

        // ── Portable semantics: the eight `TOAST-0018` concerns ───────────
        //
        // Phase A's exit asks that the core behaviour be "enforced by a backend-neutral
        // corpus", and these are what makes the eight specifications claims about the
        // *language* rather than about the interpreter. Two to four cases each, chosen for
        // the property the specification turns on rather than for coverage: a backend that
        // drifts on any of these has stopped implementing the same language.

        // Equality (§the cascade, §Numbers, null and Instances).
        yield return Case("eq-coercion", "echo (1 == \"1\")");
        yield return Case("eq-record-field-order", "echo ({| a = 1, b = 2 |} == {| b = 2, a = 1 |})");
        yield return Case("eq-nan-is-reflexive", "var n = 0.0 / 0.0\necho ($n == $n)");
        // The exactness rule: 2**53+1 has no exact double, and deciding by conversion made
        // equality intransitive.
        yield return Case(
            "eq-integer-against-float-is-exact",
            "var a = 9007199254740993 as long\nvar c = 9007199254740992 as long\necho ($a == ($c as double))");

        // Ordering (§Ordering) — by code point, and the same on every machine.
        yield return Case("ord-code-point", "echo (\"a\" < \"B\")");
        yield return Case("ord-not-culture", "echo (\"z\" < $'\\u00E4')");
        yield return Case("ord-null-is-unordered", "echo (null < 1)");

        // Key equality (§Key Equality) — the relation containers use.
        yield return Case(
            "key-reordered-records-are-one-key",
            "echo ([{| a = 1, b = 2 |}, {| b = 2, a = 1 |}, \"z\"] | distinct | count)");
        yield return Case(
            "key-number-and-string-are-different-keys",
            "echo ([1, \"1\", \"z\"] | distinct | count)");

        // Nullability (§What null Means).
        yield return Case("null-equals-only-null", "echo (null == 0)");
        yield return Case("null-is-not-text", "echo (\"abc\" contains null)");

        // Overflow (§Overflow) — promotion, not wrapping.
        yield return Case("ovf-promotes", "var m = 2147483647 as int\necho ($m + 1)");
        yield return Case("ovf-power-is-exact", "echo (2 ** 62)");
        yield return Case("ovf-power-narrows", "echo (2 ** 10)");
        yield return Case("ovf-integer-division-by-zero", "echo (1 / 0)");

        // Unicode (§Text and Unicode) — UTF-16 code units, no normalisation.
        yield return Case("uni-length-is-code-units", "echo ($'\\uD83D\\uDC4B'.Length)");
        yield return Case("uni-combining-is-two", "echo ($'e\\u0301'.Length)");
        yield return Case("uni-comparison-does-not-normalise", "echo ($'e\\u0301' == $'\\u00E9')");

        // Collection shape (§Collection Shape).
        yield return Case("shape-array-spreads", "echo ([1, 2, 3] | count)");
        yield return Case("shape-string-is-one-value", "echo (\"abc\" | count)");

        // Errors (§Errors and catch).
        yield return Case("err-any-value-is-catchable", "try { throw \"oops\" } catch (e) { echo $e }");
        yield return Case("err-finally-runs", "try { echo \"body\" } finally { echo \"after\" }");

        // ── Rebinding: the TS-P2-87 family ────────────────────────────────
        yield return Case(
            "rebound-variable-still-reads-back",
            "var x = 10\n$x = \"hello\"\necho $x");

        // ── Runes: expanded at lowering (`TOAST-0069`) ────────────────────
        //
        // A rune call used to send the whole program to the interpreter, so these
        // agreed for the uninteresting reason that both sides *were* the interpreter.
        // They compare two implementations now, which is the point of adding them.
        yield return Case(
            "rune-body-splice",
            "rune do-twice(body) {\n    $body\n    $body\n}\n"
            + "var n = 0\ndo-twice { $n = $n + 1 }\necho $n");

        // The splice has to work at any depth, not just at the body's top level. A
        // top-level-only pass compiled this into three iterations that each discarded
        // a block value and printed nothing — agreeing with no one, and silently.
        yield return Case(
            "rune-splice-nested-in-a-loop",
            "rune do-times(times, body) {\n    for i in (1..$times) { $body }\n}\n"
            + "var n = 0\ndo-times 3 { $n = $n + 1 }\necho $n");

        // An argument that names the parameter it is bound to. Lowering it with the
        // substitution still in scope finds itself: not a wrong binding, a stack
        // overflow that took the whole test run down rather than failing an assertion.
        yield return Case(
            "rune-argument-shadows-its-parameter",
            "rune do-times(count, body) {\n    for i in (1..$count) { $body }\n}\n"
            + "var count = 4\nvar n = 0\ndo-times $count { $n = $n + 1 }\necho $n");

        yield return Case(
            "rune-value-argument",
            "rune show(x) {\n    echo $x\n}\nshow 42");

        yield return Case(
            "rune-two-call-sites",
            "rune do-twice(body) {\n    $body\n    $body\n}\n"
            + "var n = 0\ndo-twice { $n = $n + 1 }\ndo-twice { $n = $n + 10 }\necho $n");

        // Declined by the expander, so this one still runs through replay — and must
        // still agree. It is the case that proves the fallback is intact.
        yield return Case(
            "rune-leaky-still-replays",
            "leaky rune bind-it(x) {\n    var bound = $x\n}\nbind-it 7\necho $bound");
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

        // `echo` with more than one argument emits one value per argument interpreted and a
        // single joined string compiled — `TOAST-0067`. Nothing to do with records; found
        // because a record case was written with two arguments.
        yield return Divergence(
            "TOAST-0067", "echo-multiple-arguments",
            "echo 1 2");

        // A compiled function whose result is null still contributes one value to the
        // pipeline; the interpreted one contributes none — `TOAST-0066`. Reached here
        // through `-> void`, which is simply the case where the null is guaranteed, but the
        // difference is general and has nothing to do with `void`.
        yield return Divergence(
            "TOAST-0066", "null-result-pipeline-contribution",
            "func f() -> void { writeline \"hi\" }\necho (f | count)");

        // `match` with type-pattern arms over a declared hierarchy yields null compiled and
        // the arm's value interpreted — `TOAST-0065`. `match` itself is fine: the same
        // statement over an `int` with literal arms agrees on both backends. It is narrowing
        // by type that does not survive emission.
        //
        // Found by *running* one of `TOAST-0036`'s control shapes rather than only compiling
        // it. That item recorded this shape as "compiles", which was true and was the
        // question it asked.
        yield return Divergence(
            "TOAST-0065", "match-type-pattern-narrowing",
            "class Shape { prop K: string = \"s\" }\n"
            + "class Circle extends Shape { prop K: string = \"c\" }\n"
            + "var s: Shape = new Circle()\n"
            + "var r: dynamic = match ($s) {\n"
            + "    Circle => \"circle\"\n"
            + "    Shape => \"shape\"\n"
            + "}\n"
            + "echo $r");

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

        // ── `TOAST-0018`'s specified semantics, not yet implemented compiled ──
        //
        // Found by running the eight concerns' corpora across both backends, which is
        // what Phase A's exit asks for. Each is the compiled side failing to implement
        // something `docs/spec/` now states, so each is a claim about the *language* that
        // only one backend currently honours. `TOAST-0030` carries them.

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
        // `TOAST-0018`. Through `Attempt`, as the divergence test already was. Two
        // backends raising the same error is agreement, and it is agreement the
        // specification asks for — `§Overflow` requires integer division by zero to raise,
        // and a corpus that cannot express "both raise, identically" cannot check that.
        // A message that differs is still a divergence, because the message is compared.
        var interpreted = Attempt(() => RunInterpreted(source));
        var compiled = Attempt(() => RunCompiled(source));

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

        // `TOAST-0018`. Rendered, not `ToString`d. The compiled side is captured *stdout*,
        // which is rendered, so comparing `ToString` against it measured two different
        // things: every boolean read `True` interpreted and `true` compiled, and a probe of
        // the eight portable-semantics concerns reported fifteen divergences of which ten
        // were this. `ToastRenderer` is the contract both backends are supposed to meet, so
        // it is what both sides are reduced to.
        return Canonicalize(results.Select(ToastRenderer.Render));
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

        // `TOAST-0044`. The emitted IL is checked structurally before the program runs.
        //
        // Not with `RuntimeHelpers.PrepareMethod`: that reports success for IL which throws
        // `InvalidProgramException` when the program actually runs, verified on this item's
        // own reproduction. `EmittedIl.Faults` decodes the bodies instead and asserts what
        // the runtime requires — branches landing on instruction boundaries, and `finally`
        // handlers ending with `endfinally`. Two real emitter defects violated exactly
        // those and nothing else caught either.
        var ilFaults = EmittedIl.Faults(assembly);
        Assert.True(
            ilFaults.Count == 0,
            "the emitter produced unsound IL:\n  " + string.Join("\n  ", ilFaults));

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

    /// <summary>
    /// Phase B's exit: the readiness probe compiles and runs through the IL path,
    /// producing what the interpreter produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The RFC's sentence is "the probe compiles and runs through the normal IL path
    /// without an interpreter dependency". This asserts the observable half of it — the
    /// same program, both backends, identical output — because that is the part a
    /// regression would silently break.
    /// </para>
    /// <para>
    /// Reaching it took `TOAST-0034` (inference), `TOAST-0038` (typing the probe, which
    /// found five defects), `TOAST-0043`, `TOAST-0044` and `TOAST-0045`. It is pinned here
    /// rather than left as a thing someone remembers to try.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_readiness_probe_agrees_across_backends()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var path = Path.Combine(root, "bench/probes/compiler_shape.tosh");
        Assert.True(File.Exists(path), $"missing {path}");

        var source = File.ReadAllText(path);
        var interpreted = Attempt(() => RunInterpreted(source));
        var compiled = Attempt(() => RunCompiled(source));

        Assert.True(
            interpreted == compiled,
            "the readiness probe diverges between backends.\n" +
            $"  interpreted: {Show(interpreted)}\n" +
            $"  compiled:    {Show(compiled)}");

        Assert.DoesNotContain("threw", interpreted, StringComparison.Ordinal);
    }

    /// <summary>
    /// The readiness probe compiles with no source replay — `TOAST-0038`, Phase B's exit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first half of that exit — compiles and runs, byte-identical — has been pinned by
    /// the test above for a while. This is the second: it compiles under the `runtime`
    /// profile, which refuses source replay, so the artifact carries no ToastScript for an
    /// evaluator to re-read.
    /// </para>
    /// <para>
    /// One thing stood between the probe and this, and it was not a type-system feature:
    /// `prop Label: string => …`. The class-shell guard refused any property with a getter
    /// body, so three computed properties sent every class declaring one to replay. The
    /// probe uses no records, enums, interfaces, traits, unions, structs or refinement
    /// types — measured, after a long stretch spent lifting those kinds out of replay for
    /// other reasons.
    /// </para>
    /// <para>
    /// Not the same as needing no runtime at all: the assembly still references
    /// `Tosh.Compiler.Runtime`, and the `pure` profile still refuses four tier-2 features —
    /// host-dispatched `new`, dynamic member access, and two command-dispatch shapes.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_readiness_probe_compiles_without_source_replay()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var path = Path.Combine(root, "bench/probes/compiler_shape.tosh");
        Assert.True(File.Exists(path), $"missing {path}");

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var parse = engine.Parse(File.ReadAllText(path), "<readiness-probe>");
        Assert.True(parse.Diagnostics.Count == 0, $"parse: {string.Join(", ", parse.Diagnostics)}");

        var unit = Lowerer.Lower(parse, runtime.Commands);
        using var stream = new MemoryStream();
        var result = BoundUnitEmitter.Emit(
            unit,
            $"ToshProbe_{Guid.NewGuid():N}",
            stream,
            CompileProfile.Runtime);

        Assert.True(
            result.IsClean,
            "the readiness probe fell back to source replay: " +
            string.Join(", ", result.UnsupportedShapes));
    }
}
