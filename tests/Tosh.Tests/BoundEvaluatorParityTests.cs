using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Codifies the contract that <see cref="BoundEvaluator"/> is observably
/// indistinguishable from the parse-tree evaluator (<see cref="ToshEngine.ExecuteToListAsync(string, CancellationToken)"/>).
///
/// Today both paths flow through the same code, so every assertion here
/// holds trivially. The point is to fail loudly the moment any future
/// commit replaces a bound-IR shape with a fast path that diverges —
/// well before such a divergence reaches the IL backend.
///
/// Each fixture runs a snippet through both paths in fresh engines so
/// stateful side effects (variable bindings, event handlers) cannot
/// leak between them.
/// </summary>
public sealed class BoundEvaluatorParityTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public BoundEvaluatorParityTests(ToshRuntimeFixture fixture)
    {
        _runtime = fixture.Runtime;
    }

    /// <summary>
    /// Representative corpus. Keep entries small, hermetic (no I/O, no
    /// processes), and exercise one shape each. Add an entry the first
    /// time a divergence is suspected so this test grows alongside the IR.
    /// </summary>
    public static IEnumerable<object[]> Corpus => new[]
    {
        new object[] { "literals/scalar",         "echo 42" },
        new object[] { "literals/string",         "echo hello" },
        new object[] { "literals/quoted",         "echo \"hello, world\"" },
        new object[] { "literals/list",           "[1, 2, 3]" },
        new object[] { "var/decl",                "var x = 42\necho $x" },
        new object[] { "var/reassign",            "var x = 1\n$x = 2\necho $x" },
        new object[] { "arith/int_add",           "echo (1 + 2)" },
        new object[] { "arith/folded_chain",      "echo (60 * 60 * 24)" },
        new object[] { "arith/unary_neg",         "var x = -5\necho $x" },
        new object[] { "string/concat",           "echo (\"foo\" + \"bar\")" },
        new object[] { "string/interp",           "var n = \"world\"\necho $\"hello, {$n}!\"" },
        new object[] { "bool/and_or",             "echo (true and false or true)" },
        new object[] { "compare/gt",              "echo (5 > 3)" },
        new object[] { "range/materialize",       "1..5 | sum" },
        new object[] { "pipe/where_first",        "1..10 | where $_ > 5 | first 2" },
        new object[] { "pipe/sort_first_fused",   "1..100 | where $_ > 50 | sort | first 5" },
        new object[] { "pipe/sort_reverse",       "1..10 | sort -r | first 3" },
        new object[] { "func/oneliner",           "func square(n) => $n * $n\nsquare 7" },
        new object[] { "func/body",               "func sq(n) { $n * $n }\nsq 7" },
        new object[] { "control/if",              "if (1 > 0) { echo yes } else { echo no }" },
        new object[] { "control/for",             "for i in [1, 2, 3] { echo $i }" },
        new object[] { "var/compound_assign",     "var x = 1\n$x += 5\necho $x" },
        new object[] { "array/literal",           "echo [1, 2, 3]" },
        new object[] { "array/spread",            "var xs = [2, 3]\necho [1, ...$xs, 4]" },
        new object[] { "control/if_no_else",      "if (1 > 0) { echo yes }" },
        new object[] { "control/while",           "var n = 0\nwhile ($n < 3) { $n = $n + 1 }\necho $n" },
        new object[] { "control/break",           "for i in [1, 2, 3, 4] { if ($i == 3) { break }\n echo $i }" },
        new object[] { "control/continue",        "for i in [1, 2, 3, 4] { if ($i == 2) { continue }\n echo $i }" },
        new object[] { "closure/where_capture",   "var t = 2\n[1, 2, 3] | where { $_ > $t }" },
        new object[] { "closure/each_lambda",     "[1, 2, 3] | each func(x) { $x * 2 }" },
        // Phase C-1: try/throw/return/match/switch
        new object[] { "control/try_catch",       "try {\n    throw \"boom\"\n} catch (e) {\n    echo \"caught\"\n}" },
        new object[] { "control/try_finally",     "var n = 0\ntry {\n    $n = 1\n} finally {\n    $n = 2\n}\necho $n" },
        new object[] { "control/return",          "func sq(n) { return ($n * $n) }\nsq 7" },
        new object[] { "control/switch",          "var x = 2\nswitch ($x) {\n    case 1 { echo one }\n    case 2 { echo two }\n    default { echo other }\n}" },
        new object[] { "control/match_expr",      "var x = 2\necho (match ($x) { 1 => \"one\"; 2 => \"two\"; default => \"other\" })" },
        // Phase C-2: types & object access
        new object[] { "types/static_member",     "echo (Math.PI)" },
        new object[] { "types/static_call",       "echo (Math.Sqrt(16))" },
        new object[] { "types/index_access",      "var xs = [10, 20, 30]\necho ($xs[1])" },
        new object[] { "types/method_call",       "var s = \"hello\"\necho ($s.ToUpper())" },
        // Phase C-3: declarations + niche shapes
        // NOTE: only declaration shapes that the bound evaluator
        // already executes via fallback are exercised here. New
        // declarations like `class` / `enum` produce typed bound
        // nodes that an IR-only evaluator/emitter would have to
        // register with the runtime — that work belongs to the
        // emitter milestone, not the IR carve-out.
        new object[] { "decl/func_body",          "func dbl(n) { $n * 2 }\ndbl 21" },
        new object[] { "decl/destructure_array",  "var [a, b, c] = [1, 2, 3]\necho $a\necho $b\necho $c" },
        new object[] { "literal/tuple",           "echo ((1, 2, 3))" },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task Bound_path_matches_parse_tree_path(string label, string source)
    {
        // Each path gets its own engine so variable bindings, function
        // declarations, and any other engine-level state do not leak.
        // Both paths receive the same sourceName so that values which
        // capture their origin (e.g. ShellBlock) compare equal.
        var parseEngine = new ToshEngine(_runtime.Language);
        var boundEngine = new ToshEngine(_runtime.Language);

        var fromParse = await parseEngine.ExecuteToListAsync(source, label);
        var fromBound = await BoundEvaluator.EvaluateToListAsync(boundEngine, source, sourceName: label);

        Assert.Equal(fromParse, fromBound);
    }

    /// <summary>
    /// The bound path with lowering bypassed should also match — this
    /// catches the case where a future fast path becomes the only way
    /// to get a correct answer (which would be a different kind of bug
    /// than divergence).
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task Both_paths_agree_with_lowerer_disabled(string label, string source)
    {
        Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", "1");
        try
        {
            var parseEngine = new ToshEngine(_runtime.Language);
            var boundEngine = new ToshEngine(_runtime.Language);

            var fromParse = await parseEngine.ExecuteToListAsync(source, label);
            var fromBound = await BoundEvaluator.EvaluateToListAsync(boundEngine, source, sourceName: label);

            Assert.Equal(fromParse, fromBound);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", null);
        }
    }
}
