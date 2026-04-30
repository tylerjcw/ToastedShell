using System.Collections.Immutable;
using Tosh.Language.Binding.BoundNodes;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Language.Binding;

/// <summary>
/// Converts a <see cref="ParseResult"/> into a <see cref="BoundUnit"/>.
///
/// The lowering pass is the bridge between the parser and the
/// (future) IL emitter. v1 carves out only the highest-leverage
/// shapes — pipeline statements, command calls, literal arguments,
/// variable references — and wraps everything else in
/// <see cref="BoundDynamicExpression"/> / <see cref="BoundDynamicStatement"/>
/// so the resulting tree is always complete. Each carved-out shape
/// removes one wrapper.
///
/// Symbol resolution piggy-backs on the existing
/// <see cref="VariableBinder"/>: the lowering pass tracks the same
/// scope stack and produces <see cref="BoundSymbol"/> instances at
/// declaration sites, then attaches them to references it can resolve.
/// Anything the binder leaves dynamic (env, tosh, externally sourced
/// names) becomes a symbol-less <see cref="BoundVariableReference"/>
/// that the evaluator falls back on at runtime.
/// </summary>
public static class Lowerer
{
    public static BoundUnit Lower(ParseResult parseResult, ShellCommandRegistry commands)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(commands);

        var ctx = new LowerContext(commands);
        var root = LowerStatementAsScript(parseResult.Statement, ctx);
        return new BoundUnit(root, parseResult, ctx.Symbols.ToImmutableList());
    }

    // ── statements ──────────────────────────────────────────────

    private static BoundScript LowerStatementAsScript(StatementSyntax statement, LowerContext ctx)
    {
        if (statement is ScriptStatementSyntax script)
        {
            var lowered = new List<BoundStatement>(script.Statements.Count);
            foreach (var inner in script.Statements)
            {
                lowered.Add(LowerStatement(inner, ctx));
            }
            return new BoundScript(lowered, script.Span);
        }

        return new BoundScript(new[] { LowerStatement(statement, ctx) }, statement.Span);
    }

    private static BoundStatement LowerStatement(StatementSyntax statement, LowerContext ctx) => statement switch
    {
        PipelineStatementSyntax pipeline =>
            new BoundPipelineStatement(LowerPipeline(pipeline.Pipeline, ctx), pipeline.Span),

        VariableDeclarationStatementSyntax decl =>
            LowerVariableDeclaration(decl, ctx),

        ScriptStatementSyntax inner =>
            LowerStatementAsScript(inner, ctx),

        // Anything else (destructuring, control flow, function defs)
        // is preserved as a dynamic statement for now. The
        // evaluator-on-IR will delegate to the existing tree-walking
        // evaluator for these.
        _ => new BoundDynamicStatement(statement, statement.Span),
    };

    private static BoundVariableDeclaration LowerVariableDeclaration(
        VariableDeclarationStatementSyntax decl,
        LowerContext ctx)
    {
        // Lower the initializer first so the variable is not yet in
        // scope while its own RHS is being lowered (matches existing
        // VariableBinder semantics).
        var value = decl.Value is null ? null : LowerPipeline(decl.Value, ctx);

        // Explicit `: T` annotations would override inference, but the
        // parser doesn't carry resolved CLR types yet — leave them
        // dynamic for now and fall through to value inference.
        var declaredType = value is null
            ? BoundType.Dynamic
            : TypeInferrer.InferPipelineValue(value);
        var symbol = ctx.DeclareLocal(decl.Name, declaredType);

        return new BoundVariableDeclaration(
            Symbol: symbol,
            Value: value,
            IsConst: decl.IsConst,
            Modifier: decl.Modifier,
            Span: decl.Span);
    }

    // ── pipelines ──────────────────────────────────────────────

    private static BoundPipeline LowerPipeline(PipelineSyntax pipeline, LowerContext ctx)
    {
        var stages = new List<BoundPipelineStage>(pipeline.Stages.Count);
        foreach (var stage in pipeline.Stages)
        {
            stages.Add(LowerPipelineStage(stage, ctx));
        }

        var span = pipeline.Stages.Count > 0
            ? pipeline.Stages[0].Span
            : new TextSpan(0, 0);

        return new BoundPipeline(stages, pipeline, span);
    }

    private static BoundPipelineStage LowerPipelineStage(PipelineStageSyntax stage, LowerContext ctx) => stage switch
    {
        CommandSyntax command => LowerCommand(command, ctx),

        ExpressionPipelineStageSyntax expr =>
            new BoundExpressionStage(LowerExpression(expr.Expression, ctx), expr.Span),

        // Other stage shapes (subexpression stages, etc.) fall back
        // to a dynamic command so the evaluator-on-IR still has
        // something to dispatch on.
        _ => new BoundCommandCall(
            Name: "<dynamic-stage>",
            NameSpan: stage.Span,
            ResolvedCommand: null,
            Arguments: Array.Empty<BoundArgument>(),
            Span: stage.Span),
    };

    private static BoundCommandCall LowerCommand(CommandSyntax command, LowerContext ctx)
    {
        var resolved = ctx.Commands.TryGet(command.Name, out var registered) ? registered : null;

        var arguments = new List<BoundArgument>(command.Arguments.Count);
        foreach (var argument in command.Arguments)
        {
            arguments.Add(LowerArgument(argument, ctx));
        }

        return new BoundCommandCall(
            Name: command.Name,
            NameSpan: command.NameSpan,
            ResolvedCommand: resolved,
            Arguments: arguments,
            Span: command.Span);
    }

    // ── arguments / expressions ──────────────────────────────────

    private static BoundArgument LowerArgument(ArgumentSyntax argument, LowerContext ctx)
    {
        switch (argument)
        {
            case NamedArgumentSyntax named:
                return new BoundArgument(
                    Name: named.Name,
                    Value: LowerExpression(named.Value, ctx),
                    IsSplat: false,
                    Span: named.Span);

            case SplatArgumentSyntax splat:
                return new BoundArgument(
                    Name: null,
                    Value: LowerExpression(splat.Value, ctx),
                    IsSplat: true,
                    Span: splat.Span);

            default:
                return new BoundArgument(
                    Name: null,
                    Value: LowerExpression(argument, ctx),
                    IsSplat: false,
                    Span: argument.Span);
        }
    }

    private static BoundExpression LowerExpression(ArgumentSyntax expression, LowerContext ctx) => expression switch
    {
        LiteralArgumentSyntax literal =>
            new BoundLiteral(literal.Value, literal.Span, InferLiteralType(literal.Value)),

        BarewordArgumentSyntax bareword =>
            // Barewords are strings at runtime — capture that statically.
            new BoundLiteral(bareword.Value, bareword.Span, BoundType.FromClr(typeof(string))),

        VariableReferenceArgumentSyntax varRef =>
            BuildVariableReference(varRef, ctx),

        MemberAccessArgumentSyntax member =>
            new BoundMemberAccess(
                Target: LowerExpression(member.Target, ctx),
                MemberPath: member.MemberPath,
                NullSafe: member.NullSafe,
                Span: member.Span,
                Type: BoundType.Dynamic),

        OperatorArgumentSyntax binary => BuildBinary(binary, ctx),

        UnaryOperatorArgumentSyntax unary => BuildUnary(unary, ctx),

        RangeArgumentSyntax range => BuildRange(range, ctx),

        // Everything else stays dynamic for now.
        _ => new BoundDynamicExpression(expression, expression.Span),
    };

    private static BoundVariableReference BuildVariableReference(VariableReferenceArgumentSyntax varRef, LowerContext ctx)
    {
        var symbol = ctx.LookupSymbol(varRef.Name);
        // If we resolved the symbol, lift its declared type onto the
        // reference so callers downstream see propagated typing.
        var type = symbol?.DeclaredType ?? BoundType.Dynamic;
        return new BoundVariableReference(varRef.Name, symbol, varRef.Span, type);
    }

    private static BoundExpression BuildBinary(OperatorArgumentSyntax binary, LowerContext ctx)
    {
        var left = LowerExpression(binary.Left, ctx);
        var right = LowerExpression(binary.Right, ctx);
        var type = TypeInferrer.InferBinary(left.Type, binary.Operator, right.Type);

        // Try to fold to a constant. On success, stamp the parse
        // tree's side-table (so the existing evaluator's
        // expression-evaluation path can short-circuit without any
        // structural changes) AND replace this node with a literal
        // in the bound IR so future bound-IR consumers see the
        // simpler shape.
        var folded = ConstantFolder.TryFoldBinary(left, binary.Operator, right);
        if (!ReferenceEquals(folded, ConstantFolder.Sentinel.NoFold))
        {
            binary.FoldedConstant = new ConstantFold(folded);
            var foldedType = folded is null ? BoundType.Dynamic : BoundType.FromClr(folded.GetType());
            return new BoundLiteral(folded, binary.Span, foldedType);
        }

        return new BoundBinaryOperator(left, binary.Operator, right, binary.Span, type);
    }

    private static BoundExpression BuildUnary(UnaryOperatorArgumentSyntax unary, LowerContext ctx)
    {
        var operand = LowerExpression(unary.Operand, ctx);
        var type = TypeInferrer.InferUnary(unary.Operator, operand.Type);

        var folded = ConstantFolder.TryFoldUnary(unary.Operator, operand);
        if (!ReferenceEquals(folded, ConstantFolder.Sentinel.NoFold))
        {
            unary.FoldedConstant = new ConstantFold(folded);
            var foldedType = folded is null ? BoundType.Dynamic : BoundType.FromClr(folded.GetType());
            return new BoundLiteral(folded, unary.Span, foldedType);
        }

        return new BoundUnaryOperator(unary.Operator, operand, unary.Span, type);
    }

    private static BoundRange BuildRange(RangeArgumentSyntax range, LowerContext ctx)
    {
        var start = LowerExpression(range.Start, ctx);
        var step = range.Step is null ? null : LowerExpression(range.Step, ctx);
        var end = range.End is null ? null : LowerExpression(range.End, ctx);
        var type = end is null
            ? BoundType.Dynamic
            : TypeInferrer.InferRange(start.Type, step?.Type, end.Type);
        return new BoundRange(start, step, end, range.Span, type);
    }

    private static BoundType InferLiteralType(object? value) => value switch
    {
        null => BoundType.Dynamic,
        bool => BoundType.FromClr(typeof(bool)),
        int => BoundType.FromClr(typeof(int)),
        long => BoundType.FromClr(typeof(long)),
        double => BoundType.FromClr(typeof(double)),
        decimal => BoundType.FromClr(typeof(decimal)),
        string => BoundType.FromClr(typeof(string)),
        _ => BoundType.FromClr(value.GetType()),
    };

    // ── lowering context ────────────────────────────────────────

    private sealed class LowerContext
    {
        // Scope-stack of name → symbol. Top of stack is innermost.
        // Mirrors the layout used by VariableBinder.
        private readonly List<Dictionary<string, BoundSymbol>> _scopes = new();

        public LowerContext(ShellCommandRegistry commands)
        {
            Commands = commands;
            _scopes.Add(new Dictionary<string, BoundSymbol>(StringComparer.Ordinal));
        }

        public ShellCommandRegistry Commands { get; }

        public List<BoundSymbol> Symbols { get; } = new();

        public BoundSymbol DeclareLocal(string name, BoundType declaredType)
        {
            var symbol = new BoundSymbol(
                Name: name,
                Kind: BoundSymbolKind.LocalVariable,
                ScopeDepth: _scopes.Count - 1,
                DeclaredType: declaredType);

            // Shadowing is permitted — the innermost binding wins.
            _scopes[^1][name] = symbol;
            Symbols.Add(symbol);
            return symbol;
        }

        public BoundSymbol? LookupSymbol(string name)
        {
            for (var i = _scopes.Count - 1; i >= 0; i--)
            {
                if (_scopes[i].TryGetValue(name, out var symbol)) return symbol;
            }
            return null;
        }
    }
}
