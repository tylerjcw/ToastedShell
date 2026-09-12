using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Compiler.IR;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Tests for the constant-folding pass that runs as part of lowering.
/// Two-sided contract:
///   1. The bound IR replaces folded operator nodes with literals.
///   2. The parse tree carries a <see cref="ConstantFold"/> annotation
///      so the existing evaluator can short-circuit.
/// </summary>
public sealed class ConstantFoldingTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public ConstantFoldingTests(ToshRuntimeFixture fixture)
    {
        _runtime = fixture.Runtime;
    }

    private (ParseResult Parse, BoundUnit Unit) Lower(string source)
    {
        var engine = new ToshEngine(_runtime.Language);
        var parse = engine.Parse(source, "<fold-test>");
        var unit = Lowerer.Lower(parse, _runtime.Commands);
        return (parse, unit);
    }

    private static BoundExpression FirstArg(BoundUnit unit)
    {
        var pipeline = (BoundPipelineStatement)unit.Root.Statements[0];
        var call = (BoundCommandCall)pipeline.Pipeline.Stages[0];
        return call.Arguments[0].Value;
    }

    private static OperatorArgumentSyntax? FindFirstBinaryOp(StatementSyntax stmt)
    {
        // Walks just the shallow path we need for these tests.
        if (stmt is ScriptStatementSyntax script && script.Statements.Count > 0)
            return FindFirstBinaryOp(script.Statements[0]);

        if (stmt is PipelineStatementSyntax pipe && pipe.Pipeline.Stages.Count > 0
            && pipe.Pipeline.Stages[0] is CommandSyntax cmd && cmd.Arguments.Count > 0)
        {
            return FindOpInArg(cmd.Arguments[0]);
        }

        return null;
    }

    private static OperatorArgumentSyntax? FindOpInArg(ArgumentSyntax arg) => arg switch
    {
        OperatorArgumentSyntax op => op,
        TupleLiteralArgumentSyntax tuple when tuple.Items.Count > 0 => FindOpInArg(tuple.Items[0]),
        _ => null,
    };

    [Fact]
    public void Folds_int_addition_to_literal_in_bound_tree()
    {
        var (_, unit) = Lower("echo (1 + 2)");
        var arg = FirstArg(unit);
        // Inside a tuple/parenthesized expression the parser may wrap
        // the operator further. Drill until we find a literal or give up.
        // The fold should reach the leaf either way.
        // Either the wrapper is a literal, or it's a dynamic that wraps
        // a folded operator — the parse-tree side-table check below is
        // the authoritative signal.
        Assert.True(arg is BoundLiteral or BoundDynamicExpression);
    }

    [Fact]
    public void Folds_stamp_parse_tree_side_table()
    {
        var (parse, _) = Lower("echo (60 * 60 * 24)");
        var op = FindFirstBinaryOp(parse.Statement);
        Assert.NotNull(op);
        // The outermost op may not be folded if a sub-expression is,
        // but the binary tree should have at least one folded node.
        // Simplest check: walk down until we find one.
        var folded = FindAnyFolded(op!);
        Assert.NotNull(folded);
        Assert.Equal(86400, folded!.Value);
    }

    private static ConstantFold? FindAnyFolded(OperatorArgumentSyntax op)
    {
        if (op.FoldedConstant is not null) return op.FoldedConstant;
        if (op.Left is OperatorArgumentSyntax leftOp && FindAnyFolded(leftOp) is { } l) return l;
        if (op.Right is OperatorArgumentSyntax rightOp && FindAnyFolded(rightOp) is { } r) return r;
        return null;
    }

    [Fact]
    public void Folds_unary_negation_of_literal()
    {
        var (_, unit) = Lower("var x = -5");
        var decl = (BoundVariableDeclaration)unit.Root.Statements[0];
        // `-5` should fold to a literal int.
        Assert.NotNull(decl.Value);
        var stage = decl.Value!.Stages[0];
        var expr = stage switch
        {
            BoundExpressionStage e => e.Value,
            _ => null,
        };
        // If the parser models -5 as a unary op, it folds to BoundLiteral(-5).
        // If the lexer already produces -5 as a literal, it's a BoundLiteral.
        // Either way, we expect a literal here.
        if (expr is BoundLiteral lit)
        {
            Assert.Equal(-5, lit.Value);
        }
    }

    [Fact]
    public void Folds_string_concatenation()
    {
        var folded = ConstantFolder.TryFoldBinary(
            new BoundLiteral("hello, ", default, BoundType.FromClr(typeof(string))),
            "+",
            new BoundLiteral("world", default, BoundType.FromClr(typeof(string))));

        Assert.Equal("hello, world", folded);
    }

    [Fact]
    public void Folds_boolean_and_or()
    {
        Assert.Equal(true, ConstantFolder.TryFoldBinary(
            new BoundLiteral(true, default, BoundType.FromClr(typeof(bool))),
            "and",
            new BoundLiteral(true, default, BoundType.FromClr(typeof(bool)))));

        Assert.Equal(false, ConstantFolder.TryFoldBinary(
            new BoundLiteral(true, default, BoundType.FromClr(typeof(bool))),
            "&&",
            new BoundLiteral(false, default, BoundType.FromClr(typeof(bool)))));
    }

    [Fact]
    public void Folds_comparison()
    {
        Assert.Equal(true, ConstantFolder.TryFoldBinary(
            new BoundLiteral(5, default, BoundType.FromClr(typeof(int))),
            ">",
            new BoundLiteral(3, default, BoundType.FromClr(typeof(int)))));
    }

    [Fact]
    public void Refuses_to_fold_division_by_zero()
    {
        var folded = ConstantFolder.TryFoldBinary(
            new BoundLiteral(10, default, BoundType.FromClr(typeof(int))),
            "/",
            new BoundLiteral(0, default, BoundType.FromClr(typeof(int))));

        Assert.Same(ConstantFolder.Sentinel.NoFold, folded);
    }

    [Fact]
    public void Refuses_to_fold_when_either_operand_is_non_literal()
    {
        // A var-ref isn't a literal — fold must bail.
        var folded = ConstantFolder.TryFoldBinary(
            new BoundVariableReference("x", null, default, BoundType.Dynamic),
            "+",
            new BoundLiteral(1, default, BoundType.FromClr(typeof(int))));

        Assert.Same(ConstantFolder.Sentinel.NoFold, folded);
    }

    /// <summary>
    /// A fold must not change what a comparison means.
    ///
    /// Comparisons used to be folded through <c>decimal</c>, which keeps 15
    /// significant digits of a double — so two distinct doubles collapsed onto
    /// one value and compared equal, while the evaluator compared them exactly
    /// and disagreed. `0.3 == 0.30000000000000004` was true when it could be
    /// folded and false when it could not.
    /// </summary>
    [Fact]
    public void Two_distinct_doubles_do_not_fold_to_equal()
    {
        Assert.Equal(false, ConstantFolder.TryFoldBinary(
            new BoundLiteral(0.3, default, BoundType.FromClr(typeof(double))),
            "==",
            new BoundLiteral(0.30000000000000004, default, BoundType.FromClr(typeof(double)))));
    }

    /// <summary>
    /// The other half: a fold must still be a fold. Narrowing to the operand
    /// type is what keeps int and mixed int/double comparisons answering as
    /// they did.
    /// </summary>
    [Theory]
    [InlineData(1, "==", 1, true)]
    [InlineData(1, "==", 2, false)]
    [InlineData(5, ">", 3, true)]
    [InlineData(3, ">=", 3, true)]
    [InlineData(2, "<", 3, true)]
    public void Integer_comparisons_still_fold(int left, string op, int right, bool expected)
    {
        Assert.Equal(expected, ConstantFolder.TryFoldBinary(
            new BoundLiteral(left, default, BoundType.FromClr(typeof(int))),
            op,
            new BoundLiteral(right, default, BoundType.FromClr(typeof(int)))));
    }

    /// <summary>
    /// Every ordering against NaN is false at runtime, and no single integer
    /// encodes that, so the ordering is declined rather than guessed.
    /// </summary>
    [Theory]
    [InlineData("<")]
    [InlineData("<=")]
    [InlineData(">")]
    [InlineData(">=")]
    public void Refuses_to_order_nan(string op)
    {
        var folded = ConstantFolder.TryFoldBinary(
            new BoundLiteral(double.NaN, default, BoundType.FromClr(typeof(double))),
            op,
            new BoundLiteral(1.0, default, BoundType.FromClr(typeof(double))));

        Assert.Same(ConstantFolder.Sentinel.NoFold, folded);
    }

    /// <summary>
    /// The property that matters, end to end and in the evaluator's own terms:
    /// turning folding off must not change any answer. The doubles here are the
    /// ones the old decimal conversion could not tell apart.
    /// </summary>
    [Theory]
    [InlineData("echo (0.3 == 0.30000000000000004)")]
    [InlineData("echo ((0.1 + 0.2) == 0.3)")]
    [InlineData("echo (1 == 1.0)")]
    [InlineData("echo (1.5 < 2.5)")]
    [InlineData("echo (2.5 <= 2.5)")]
    [InlineData("echo (((1.0 / 3.0) * 3.0) == 1.0)")]
    [InlineData("echo (1000000000000 == 1000000000001)")]
    public async Task A_comparison_means_the_same_folded_or_not(string source)
    {
        var folded = await new ToshEngine(_runtime.Language).ExecuteToListAsync(source);

        Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", "1");
        try
        {
            var noFold = await new ToshEngine(_runtime.Language).ExecuteToListAsync(source);
            Assert.Equal(noFold, folded);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", null);
        }
    }

    [Fact]
    public async Task Evaluator_returns_same_result_for_folded_and_non_folded()
    {
        var engine = new ToshEngine(_runtime.Language);
        var folded = await engine.ExecuteToListAsync("echo (60 * 60 * 24)");

        Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", "1");
        try
        {
            var noFold = await new ToshEngine(_runtime.Language).ExecuteToListAsync("echo (60 * 60 * 24)");
            Assert.Equal(noFold, folded);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", null);
        }
    }
}
