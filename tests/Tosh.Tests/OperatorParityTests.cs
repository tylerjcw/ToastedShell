using System.Text.RegularExpressions;
using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

/// <summary>
/// Operator parity check: every binary operator handled by <see cref="OperatorEvaluator"/>
/// must be reachable through the parser, and conversely the parser must not produce operator
/// strings the evaluator doesn't know about. We extract the canonical operator set directly
/// from <c>OperatorEvaluator.cs</c> source, then exercise each via the public engine. Any
/// "Unsupported operator" exception means lexer / parser / evaluator have drifted apart.
/// </summary>
public sealed class OperatorParityTests
{
    [Fact]
    public void Every_binary_operator_in_OperatorEvaluator_round_trips_through_the_parser()
    {
        var evaluatorPath = LocateEvaluatorSource();
        var source = File.ReadAllText(evaluatorPath);

        var binarySection = ExtractSwitchSection(source, "EvaluateBinary");
        var binaryOps = ExtractOperatorKeys(binarySection);
        Assert.NotEmpty(binaryOps);

        // Sanity floor — if anyone deletes operators wholesale, fail loudly.
        Assert.True(binaryOps.Count >= 20,
            $"Expected at least 20 binary operators in EvaluateBinary, found {binaryOps.Count}: " +
            string.Join(", ", binaryOps));

        var problems = new List<string>();
        var engine = new ToshEngine();

        foreach (var op in binaryOps)
        {
            var script = ScriptForOperator(op);
            if (script is null) continue; // explicitly skipped (e.g. assignment "=")

            try
            {
                // Drain the result; some operators short-circuit and never invoke the evaluator
                // arm we're testing, so ALSO ensure the operator string actually appears in the
                // evaluator switch — that's already proven by extraction. The runtime check
                // only catches "Unsupported operator" exceptions.
                var collected = engine.EvaluateAsync(script, default).ToBlockingEnumerable().ToList();
                _ = collected;
            }
            catch (Exception ex) when (ex.Message.Contains("Unsupported operator", StringComparison.Ordinal))
            {
                problems.Add($"Operator '{op}' is in OperatorEvaluator but unreachable via parser: {ex.Message}");
            }
            catch (Exception ex) when (ex.Message.Contains("Unsupported unary operator", StringComparison.Ordinal))
            {
                problems.Add($"Operator '{op}' produced an unsupported-unary error: {ex.Message}");
            }
            catch
            {
                // Non-parity errors (type mismatches, etc.) are fine — the operator was
                // accepted by the parser and dispatched through the switch arm.
            }
        }

        Assert.True(problems.Count == 0,
            "Operator parity issues:\n  - " + string.Join("\n  - ", problems));
    }

    [Fact]
    public void Unary_operator_set_is_known_and_reachable()
    {
        var evaluatorPath = LocateEvaluatorSource();
        var source = File.ReadAllText(evaluatorPath);

        var unarySection = ExtractSwitchSection(source, "EvaluateUnary");
        var unaryOps = ExtractOperatorKeys(unarySection);
        Assert.NotEmpty(unaryOps);

        // The tripwire did its job: `TS-P2-02` added `-` and `+`, which the parser had
        // accepted all along while `EvaluateUnary` implemented neither — so `- $x`
        // reported "Unsupported unary operator '-'". Each new operator gets a probe
        // below, which is what this assertion is here to force.
        // `!` shares a case with `not` (`"!" or "not" => …`) and the extractor reports one
        // key per case, so it does not appear here — which is also why the assertion read
        // `["not"]` before rather than `["!", "not"]`.
        Assert.Equal(
            new[] { "+", "-", "bnot", "not" },
            unaryOps.OrderBy(o => o, StringComparer.Ordinal).ToArray());

        var engine = new ToshEngine();

        foreach (var (probe, expected) in new[]
                 {
                     ("echo (not true)", "False"),
                     ("var x = 3\necho (- $x)", "-3"),
                     ("var x = 3\necho (+ $x)", "3"),
                     // Glued to a variable, which the lexer used to scan as one word and
                     // report as `Command '-$x' was not found`.
                     ("var x = 3\necho (-$x)", "-3"),
                     // `TS-P3-14`. Complement, at the same level as `not` and unary `-`.
                     ("echo (bnot 0)", "-1"),
                 })
        {
            var collected = engine.EvaluateAsync(probe, default).ToBlockingEnumerable().ToList();
            Assert.Equal(expected, Assert.Single(collected)?.ToString());
        }
    }

    [Fact]
    public void Match_operator_subset_is_a_subset_of_binary_operators()
    {
        // The `Matches` switch (used for case/where guards) handles a deliberate subset of
        // binary operators. It must never grow operators that EvaluateBinary doesn't have.
        var evaluatorPath = LocateEvaluatorSource();
        var source = File.ReadAllText(evaluatorPath);

        var binaryOps = ExtractOperatorKeys(ExtractSwitchSection(source, "EvaluateBinary")).ToHashSet(StringComparer.Ordinal);
        var matchOps = ExtractOperatorKeys(ExtractSwitchSection(source, "Matches"));

        var stray = matchOps.Where(o => !binaryOps.Contains(o)).ToList();
        Assert.True(stray.Count == 0,
            $"Match-only operators not present in EvaluateBinary: {string.Join(", ", stray)}");
    }

    [Fact]
    public void Every_parser_recognized_arithmetic_operator_is_handled_by_the_evaluator()
    {
        // Reverse parity check: any operator the parser will *accept* in an expression
        // must have an arm in OperatorEvaluator.EvaluateBinary. Catches the case where
        // a token (e.g. `//`) is added to the parser's operator predicates but the
        // evaluator switch was never updated, producing a runtime "Unsupported operator"
        // for code that looks valid.
        var parserPath = LocateParserSource();
        var parserSource = File.ReadAllText(parserPath);

        var evaluatorPath = LocateEvaluatorSource();
        var evaluatorSource = File.ReadAllText(evaluatorPath);
        var binaryOps = ExtractOperatorKeys(ExtractSwitchSection(evaluatorSource, "EvaluateBinary"))
            .ToHashSet(StringComparer.Ordinal);

        var parserOps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var predicate in new[]
        {
            "IsAdditiveOperatorToken",
            "IsMultiplicativeOperatorToken",
            "IsExponentiationOperatorToken",
        })
        {
            var body = ExtractMethodBody(parserSource, predicate);
            // Pick out every "op" string literal that appears in the predicate body.
            foreach (Match m in Regex.Matches(body, "\"([^\"\\\\]+)\""))
            {
                parserOps.Add(m.Groups[1].Value);
            }
        }
        Assert.NotEmpty(parserOps);

        var missing = parserOps.Where(o => !binaryOps.Contains(o)).ToList();
        Assert.True(missing.Count == 0,
            "Operators recognized by the parser but missing from OperatorEvaluator.EvaluateBinary: " +
            string.Join(", ", missing));
    }

    [Fact]
    public void Every_parser_assignment_operator_has_a_compound_lowering()
    {
        // Compound assignments like `+=`, `//=`, `**=` are lowered to a binary op by
        // ToshEngine before dispatching to OperatorEvaluator. Ensure every assignment
        // operator the parser accepts is in that lowering map.
        var parserPath = LocateParserSource();
        var parserSource = File.ReadAllText(parserPath);
        var assignBody = ExtractMethodBody(parserSource, "NormalizeAssignmentOperator");
        var assignOps = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(assignBody, "\"([^\"\\\\]+)\"\\s*=>"))
        {
            assignOps.Add(m.Groups[1].Value);
        }
        // Drop the plain "=" — it isn't compound, has no binary lowering.
        assignOps.Remove("=");
        // `??=` is null-coalescing; intentionally not lowered to a binary arith op.
        assignOps.Remove("??=");

        var enginePath = LocateEngineSource();
        var engineSource = File.ReadAllText(enginePath);
        // Find the "compound -> binary" map; matches lines like `"+=" => "+",`.
        var compoundMap = Regex.Matches(engineSource, "\"([+\\-*/%]+=)\"\\s*=>\\s*\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = assignOps.Where(o => !compoundMap.Contains(o)).ToList();
        Assert.True(missing.Count == 0,
            "Compound assignments accepted by the parser but unmapped in ToshEngine: " +
            string.Join(", ", missing));
    }

    private static string? ScriptForOperator(string op) => op switch
    {
        // Arithmetic / numeric.
        "+" => "echo (1 + 2)",
        "-" => "echo (5 - 2)",
        "*" => "echo (3 * 4)",
        "/" => "echo (10 / 2)",
        "//" => "echo (10 // 3)",
        "%" => "echo (10 % 3)",
        "**" => "echo (2 ** 8)",

        // Equality / comparison.
        "==" => "echo (1 == 1)",
        "!=" => "echo (1 != 2)",
        ">" => "echo (2 > 1)",
        ">=" => "echo (2 >= 2)",
        "<" => "echo (1 < 2)",
        "<=" => "echo (1 <= 2)",

        // Regex / text.
        "=~" => "echo (\"hello\" =~ \"hel\")",
        "!~" => "echo (\"hello\" !~ \"xyz\")",
        "contains" => "echo (\"hello world\" contains \"world\")",
        "starts-with" => "echo (\"hello\" starts-with \"he\")",
        "ends-with" => "echo (\"hello\" ends-with \"lo\")",

        // Membership.
        "in" => "echo (1 in [1, 2, 3])",
        "not-in" => "echo (4 not-in [1, 2, 3])",
        "is-in" => "echo (1 is-in [1, 2, 3])",
        "is-not-in" => "echo (4 is-not-in [1, 2, 3])",

        // Type / cast.
        "is" => "echo (1 is int)",
        "is-not" => "echo (1 is-not string)",
        "as" => "echo (\"42\" as int)",

        // Boolean.
        "and" => "echo (true and true)",
        "or" => "echo (true or false)",

        // Special: assignment is enforced at parse time and the evaluator throws a fixed
        // "Assignment operations require a variable" error if it ever reaches the switch.
        // It's not a stand-alone expression, so we exercise it indirectly via `var`.
        "=" => "var __op_parity_probe = 1\necho $__op_parity_probe",

        _ => null,
    };

    private static string ExtractSwitchSection(string source, string methodName)
    {
        // Grab from `MethodName(` up to the closing brace of its containing method.
        // Good enough for our well-structured evaluator file; we only need the operator-string keys.
        var marker = methodName + "(";
        var idx = source.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException($"Could not find method '{methodName}' in OperatorEvaluator source.");

        // Walk forward until brace-depth returns to zero after first '{'.
        var i = source.IndexOf('{', idx);
        if (i < 0) throw new InvalidOperationException($"No body brace after '{methodName}'.");
        var depth = 0;
        var start = i;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(start, i - start + 1);
            }
        }
        throw new InvalidOperationException($"Unterminated body for method '{methodName}'.");
    }

    /// <summary>
    /// Like <see cref="ExtractSwitchSection"/> but skips any occurrences of <paramref name="methodName"/>(
    /// that look like *call sites* (preceded by `.` or whitespace + identifier characters that
    /// indicate they're inside a larger expression). Picks the first occurrence whose
    /// preceding non-whitespace tokens look like a method *signature* — i.e. a return-type
    /// keyword such as `bool` or `string`.
    /// </summary>
    private static string ExtractMethodBody(string source, string methodName)
    {
        var marker = methodName + "(";
        var searchFrom = 0;
        while (true)
        {
            var idx = source.IndexOf(marker, searchFrom, StringComparison.Ordinal);
            if (idx < 0)
            {
                throw new InvalidOperationException($"Could not find a definition of '{methodName}'.");
            }
            // Look back for a return-type keyword on the same logical line.
            var lineStart = source.LastIndexOf('\n', idx);
            var prefix = source.Substring(lineStart < 0 ? 0 : lineStart, idx - (lineStart < 0 ? 0 : lineStart));
            if (Regex.IsMatch(prefix, "\\b(bool|string|void|int|double|object|TextSpan|SyntaxToken|TextWriter|float|long|byte|short|char)\\b"))
            {
                // It's a definition — extract its body.
                var braceStart = source.IndexOf('{', idx);
                if (braceStart < 0)
                {
                    // Expression-bodied method (=> ...). Capture the trailing expression up to `;`.
                    var arrow = source.IndexOf("=>", idx, StringComparison.Ordinal);
                    if (arrow < 0) { searchFrom = idx + marker.Length; continue; }
                    var semicolon = source.IndexOf(';', arrow);
                    if (semicolon < 0) { searchFrom = idx + marker.Length; continue; }
                    return source.Substring(arrow, semicolon - arrow + 1);
                }
                var depth = 0;
                for (var i = braceStart; i < source.Length; i++)
                {
                    if (source[i] == '{') depth++;
                    else if (source[i] == '}')
                    {
                        depth--;
                        if (depth == 0) return source.Substring(braceStart, i - braceStart + 1);
                    }
                }
                throw new InvalidOperationException($"Unterminated body for definition of '{methodName}'.");
            }
            searchFrom = idx + marker.Length;
        }
    }

    private static List<string> ExtractOperatorKeys(string switchBody)
    {
        // Match `"op" =>` arms. Skip the default arm and string literals that are clearly not keys
        // (they appear on the right-hand side of `=>` for messages — those don't have ` =>` after them).
        var ops = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(switchBody, "\"([^\"\\\\]+)\"\\s*=>"))
        {
            var key = m.Groups[1].Value;
            if (seen.Add(key)) ops.Add(key);
        }
        return ops;
    }

    private static string LocateEvaluatorSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Tosh.Runtime", "OperatorEvaluator.cs");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate src/Tosh.Runtime/OperatorEvaluator.cs.");
    }

    private static string LocateParserSource() => LocateRepoFile("src/Tosh.Language/Parsing/ToshParser.cs");

    private static string LocateEngineSource() => LocateRepoFile("src/Tosh.Language/ToshEngine.cs");

    private static string LocateRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relative} relative to repo root.");
    }
}
