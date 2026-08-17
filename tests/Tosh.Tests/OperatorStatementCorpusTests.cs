using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Every operator the language claims to have must parse in **bare statement
/// position**, and every operator in the registry must have a probe here.
///
/// `TOAST-0002`. The parser decides what it is looking at with sixty-odd
/// hand-maintained predicates that have to agree with one another, and the board
/// records three separate defects from them failing to:
///
/// * `TS-P2-105` — `as` was added to the precedence chain without being added to every
///   scan that asks "does this look like an expression?", so a bare `$x as int` stopped
///   parsing while every case with a second operator still worked.
/// * `TS-P2-116` — the unary operators could not open a statement at all. The scans look
///   for an operator *after* the leading token, and a unary operator **is** the leading
///   token, so none of them could have seen it. `not true` was read as a command name.
/// * `TS-P3-14` — six new word operators meant editing seven scan sites, and one was
///   missed.
///
/// **The shape they share is the point.** Each was invisible to any test that wrapped
/// the operator in parentheses or gave it a second operator, because that puts the
/// parser in expression mode before the ambiguity arises. The minimal bare form is the
/// only one that discriminates, and it is the form nobody writes a test for.
///
/// So this corpus is deliberately minimal — one operator, statement position, no
/// parentheses — and it is driven from <see cref="OperatorSurface"/> so that adding an
/// operator without a probe fails rather than passes quietly.
/// </summary>
public sealed class OperatorStatementCorpusTests
{
    /// <summary>
    /// Bindings the probes use. Kept to one place so a probe is only ever the operator
    /// and its operands.
    /// </summary>
    private const string Preamble = """
        var a = 6
        var b = 3
        var s = "abc"
        var list = [1, 2, 3]
        # Deliberately type-agnostic: a body like `$x + $y` fails at *runtime* for a
        # boolean argument, and this corpus must only ever report *parse* failures.
        func take2(x, y) => $x
        flags enum Perm: int { Read = 1, Write = 2 }
        var f = (Perm.Read bor Perm.Write)

        """;

    /// <summary>
    /// One minimal bare-statement probe per operator. **No parentheses around the
    /// operator, and never a second operator** — both mask exactly the defects this is
    /// here to catch.
    /// </summary>
    private static readonly Dictionary<string, string> Probes = new(StringComparer.Ordinal)
    {
        ["+"] = "$a + $b",
        ["-"] = "$a - $b",
        ["*"] = "$a * $b",
        ["/"] = "$a / $b",
        ["//"] = "$a // $b",
        ["%"] = "$a % $b",
        ["**"] = "$a ** $b",

        ["=="] = "$a == $b",
        ["!="] = "$a != $b",
        ["<"] = "$a < $b",
        ["<="] = "$a <= $b",
        [">"] = "$a > $b",
        [">="] = "$a >= $b",
        ["=~"] = "$s =~ \"a\"",
        ["!~"] = "$s !~ \"a\"",
        ["contains"] = "$s contains \"a\"",
        ["starts-with"] = "$s starts-with \"a\"",
        ["ends-with"] = "$s ends-with \"a\"",
        ["in"] = "$a in $list",
        ["not-in"] = "$a not-in $list",
        ["is-in"] = "$a is-in $list",
        ["is-not-in"] = "$a is-not-in $list",

        ["and"] = "true and false",
        ["or"] = "true or false",
        ["&&"] = "true && false",
        ["||"] = "true || false",
        ["not"] = "not true",

        ["band"] = "$a band $b",
        ["bor"] = "$a bor $b",
        ["bxor"] = "$a bxor $b",
        ["shl"] = "$a shl $b",
        ["shr"] = "$a shr $b",
        ["bnot"] = "bnot $a",
        ["has"] = "$f has Perm.Read",

        ["="] = "$a = 1",
        ["+="] = "$a += 1",
        ["-="] = "$a -= 1",
        ["*="] = "$a *= 1",
        ["/="] = "$a /= 1",
        ["//="] = "$a //= 1",
        ["%="] = "$a %= 1",
        ["**="] = "$a **= 1",
        ["??="] = "$a ??= 1",

        ["??"] = "null ?? 1",
        ["?."] = "$s?.Length",

        ["|"] = "$list | count",
        // Generator comprehension, not a pipeline separator — `body <| clause`.
        ["<|"] = "($x * $x <| for x in 1..3) | first",

        [".."] = "$a .. $b",

        ["is"] = "$a is int",
        ["is-not"] = "$a is-not int",
        ["as"] = "$a as int",
    };

    /// <summary>
    /// Probes that are expected to **fail**, each naming the open item responsible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The corpus found `TS-P2-117` on its first run, which is the reason this map
    /// exists rather than the probe being weakened until it passed. A line beginning with
    /// a unary operator is absorbed into the previous line's expression, so `not true`
    /// works alone and after a semicolon, and fails after a line break — reported as
    /// "Unsupported operator 'not'", because it ends up read as a *binary* operator
    /// joining the two lines.
    /// </para>
    /// <para>
    /// Worth carrying to that item: **`bnot` does not reproduce it.** The identical shape
    /// one line down parses fine, so whatever absorbs the line treats `not` as capable of
    /// being binary and `bnot` as not. That narrows the search considerably.
    /// </para>
    /// <para>
    /// The exemption cannot rot: the test asserts these probes *still* fail, so fixing the
    /// item breaks this test until the entry is removed.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> KnownFailures = new(StringComparer.Ordinal)
    {
        ["not"] = "TS-P2-117",
    };

    /// <summary>
    /// Binary operators as (left, right) operand pairs, so each can be placed in several
    /// syntactic positions rather than only one.
    /// </summary>
    /// <remarks>
    /// The single bare probe was not enough, and this is measured rather than assumed.
    /// Removing `IsCastOperatorToken` from **all seven** scan sites makes the `as` probe
    /// fail; removing it from **one** does not, because the bare statement only ever
    /// travels one of those paths. `TS-P2-105` was exactly a partial omission — added to
    /// the precedence chain, missing from some scans — so a corpus that only catches a
    /// total omission would not have caught the defect it exists for.
    ///
    /// The positions below were each verified to parse for all twenty-three composable
    /// binary operators before being adopted, so a failure means the operator, not the
    /// template.
    /// </remarks>
    private static readonly Dictionary<string, (string Left, string Right)> BinaryOperands =
        new(StringComparer.Ordinal)
        {
            ["+"] = ("$a", "$b"), ["-"] = ("$a", "$b"), ["*"] = ("$a", "$b"),
            ["/"] = ("$a", "$b"), ["//"] = ("$a", "$b"), ["%"] = ("$a", "$b"),
            ["**"] = ("$a", "$b"),

            ["=="] = ("$a", "$b"), ["!="] = ("$a", "$b"), ["<"] = ("$a", "$b"),
            ["<="] = ("$a", "$b"), [">"] = ("$a", "$b"), [">="] = ("$a", "$b"),
            ["=~"] = ("$s", "\"a\""), ["!~"] = ("$s", "\"a\""),
            ["contains"] = ("$s", "\"a\""),
            ["starts-with"] = ("$s", "\"a\""), ["ends-with"] = ("$s", "\"a\""),
            ["in"] = ("$a", "$list"), ["not-in"] = ("$a", "$list"),
            ["is-in"] = ("$a", "$list"), ["is-not-in"] = ("$a", "$list"),

            ["and"] = ("true", "false"), ["or"] = ("true", "false"),
            ["&&"] = ("true", "false"), ["||"] = ("true", "false"),

            ["band"] = ("$a", "$b"), ["bor"] = ("$a", "$b"), ["bxor"] = ("$a", "$b"),
            ["shl"] = ("$a", "$b"), ["shr"] = ("$a", "$b"),
            ["has"] = ("$f", "Perm.Read"),

            [".."] = ("$a", "$b"),
            ["??"] = ("null", "1"),
            ["is"] = ("$a", "int"), ["is-not"] = ("$a", "int"), ["as"] = ("$a", "int"),
        };

    /// <summary>
    /// Where a binary operator is placed. Each exercises a different "does this look like
    /// an expression?" scan; `{0}` is the operator applied to its operands.
    /// </summary>
    private static readonly string[] Positions =
    [
        "{0}",                        // bare statement
        "({0})",                      // parenthesised — the close-paren scan
        "[{0}, 1]",                   // collection element — the comma scan
        "echo ({0})",                 // command argument
        "take2({0}, 1)",              // call argument before a comma
        "({0} <| for x in $list)",    // generator comprehension
    ];

    public static TheoryData<string, string> BinaryPositions()
    {
        var data = new TheoryData<string, string>();
        foreach (var symbol in BinaryOperands.Keys.OrderBy(s => s, StringComparer.Ordinal))
        {
            var (left, right) = BinaryOperands[symbol];
            foreach (var position in Positions)
            {
                data.Add(symbol, string.Format(position, $"{left} {symbol} {right}"));
            }
        }

        return data;
    }

    /// <summary>
    /// Every binary operator, in every position. This is what catches a *partial*
    /// omission — an operator known to some scans and not others.
    /// </summary>
    [Theory]
    [MemberData(nameof(BinaryPositions))]
    public async Task Each_binary_operator_parses_in_every_position(string symbol, string script)
    {
        var output = new StringWriter();
        var engine = new ToshEngine(ToshRuntime.CreateDefault(output, output));

        var exception = await Record.ExceptionAsync(
            () => engine.ExecuteToListAsync(Preamble + script));

        Assert.True(
            exception is null,
            $"Operator '{symbol}' does not parse here:\n    {script}\n" +
            $"  {exception?.GetType().Name}: {exception?.Message}\n\n" +
            "It very likely parses in other positions — that is the `TOAST-0002` shape. " +
            "Find the scan that has not been told about it; they are in " +
            "ToshParser.Lookahead.cs, ToshParser.Expressions.cs and ToshParser.Tokens.cs.");
    }

    /// <summary>
    /// Every binary operator in the registry must have operands here, so a new one gets
    /// the multi-position treatment rather than only the bare probe.
    /// </summary>
    [Fact]
    public void Every_binary_registry_operator_has_operands()
    {
        var binaryish = OperatorSurface.Operators
            .Where(pair => pair.Value is not OperatorCategory.Assignment and not OperatorCategory.Pipeline)
            .Select(pair => pair.Key)
            .Where(symbol => symbol is not ("not" or "bnot" or "?."))
            .Where(symbol => !BinaryOperands.ContainsKey(symbol))
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            binaryish.Count == 0,
            "Binary operators with no operand pair for the position matrix: " +
            string.Join(", ", binaryish) +
            "\nAdd operands to BinaryOperands, or exclude the symbol explicitly if it is " +
            "not binary — silence here means it is only covered in one position.");
    }

    /// <summary>
    /// The tripwire. An operator added to the registry with no probe here would
    /// otherwise be covered by nothing, which is how all three defects reached a
    /// release.
    /// </summary>
    [Fact]
    public void Every_registry_operator_has_a_bare_statement_probe()
    {
        var missing = OperatorSurface.Operators.Keys
            .Where(symbol => !Probes.ContainsKey(symbol))
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Operators in OperatorSurface with no bare-statement probe: " +
            string.Join(", ", missing) +
            "\nAdd one to Probes. A minimal statement — one operator, no parentheses — " +
            "is what discriminates; a probe with a second operator proves nothing.");
    }

    /// <summary>
    /// And the reverse, so a probe for something that is not an operator any more is
    /// removed rather than left to rot.
    /// </summary>
    [Fact]
    public void Every_probe_names_an_operator_that_still_exists()
    {
        var stray = Probes.Keys
            .Where(symbol => !OperatorSurface.Operators.ContainsKey(symbol))
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToList();

        Assert.True(stray.Count == 0, "Probes for symbols not in OperatorSurface: " + string.Join(", ", stray));
    }

    public static TheoryData<string> OperatorSymbols()
    {
        var data = new TheoryData<string>();
        foreach (var symbol in Probes.Keys.OrderBy(s => s, StringComparer.Ordinal)) data.Add(symbol);
        return data;
    }

    /// <summary>
    /// The corpus itself: each operator, alone, as a whole statement.
    /// </summary>
    /// <remarks>
    /// The oracle is a thrown <see cref="ToshDiagnosticException"/>, not an exit code and
    /// not the text of the output. Both of those were tried while writing this and both
    /// are wrong: `not true` exits **1** because the shell reports a falsy result, and
    /// scanning rendered output for the word "error" is defeated by the ANSI colouring
    /// around it. A probe whose oracle is wrong is worse than no probe.
    /// </remarks>
    [Theory]
    [MemberData(nameof(OperatorSymbols))]
    public async Task Each_operator_parses_in_bare_statement_position(string symbol)
    {
        var output = new StringWriter();
        var engine = new ToshEngine(ToshRuntime.CreateDefault(output, output));

        var exception = await Record.ExceptionAsync(
            () => engine.ExecuteToListAsync(Preamble + Probes[symbol]));

        if (KnownFailures.TryGetValue(symbol, out var owningItem))
        {
            Assert.True(
                exception is not null,
                $"Operator '{symbol}' now parses as a bare statement, but is listed as a known " +
                $"failure owned by {owningItem}. If that item is fixed, delete the entry from " +
                "KnownFailures — an exemption that outlives its defect hides the next one.");
            return;
        }

        Assert.True(
            exception is null,
            $"Operator '{symbol}' does not parse as a bare statement:\n" +
            $"    {Probes[symbol]}\n" +
            $"  {exception?.GetType().Name}: {exception?.Message}\n\n" +
            "This is the `TOAST-0002` shape. The operator very likely works inside " +
            "parentheses or alongside a second operator — check a scan site that has not " +
            "been told about it. They live in ToshParser.Lookahead.cs, " +
            "ToshParser.Expressions.cs and ToshParser.Tokens.cs.");
    }
}
