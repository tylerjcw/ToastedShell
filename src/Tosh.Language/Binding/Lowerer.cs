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

        VariableAssignmentStatementSyntax assign =>
            new BoundVariableAssignment(
                Name: assign.Name,
                Symbol: ctx.LookupSymbol(assign.Name),
                Operator: assign.Operator,
                Value: LowerPipeline(assign.Value, ctx),
                Span: assign.Span),

        IfStatementSyntax ifStmt =>
            LowerIfStatement(ifStmt, ctx),

        ForStatementSyntax forStmt =>
            LowerForStatement(forStmt, ctx),

        WhileStatementSyntax whileStmt =>
            new BoundWhileStatement(
                Condition: LowerExpression(whileStmt.Condition, ctx),
                Body: LowerBlock(whileStmt.Body, ctx),
                IsUntil: false,
                Span: whileStmt.Span),

        UntilStatementSyntax untilStmt =>
            new BoundWhileStatement(
                Condition: LowerExpression(untilStmt.Condition, ctx),
                Body: LowerBlock(untilStmt.Body, ctx),
                IsUntil: true,
                Span: untilStmt.Span),

        BreakStatementSyntax brk =>
            new BoundBreakStatement(brk.Span),

        ContinueStatementSyntax cont =>
            new BoundContinueStatement(cont.Span),

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

    /// <summary>
    /// Lowers a <see cref="BlockSyntax"/>, opening a fresh scope frame
    /// so locals declared inside the block don't leak out. The body of
    /// every control-flow construct routes through here.
    /// </summary>
    private static BoundBlock LowerBlock(BlockSyntax block, LowerContext ctx)
    {
        ctx.PushScope();
        try
        {
            var statements = new List<BoundStatement>(block.Statements.Count);
            foreach (var inner in block.Statements)
            {
                statements.Add(LowerStatement(inner, ctx));
            }
            return new BoundBlock(statements, block.Span);
        }
        finally
        {
            ctx.PopScope();
        }
    }

    private static BoundIfStatement LowerIfStatement(IfStatementSyntax ifStmt, LowerContext ctx)
    {
        var condition = LowerExpression(ifStmt.Condition, ctx);
        var thenBlock = LowerBlock(ifStmt.ThenBlock, ctx);
        var elseBlock = ifStmt.ElseBlock is null ? null : LowerBlock(ifStmt.ElseBlock, ctx);
        return new BoundIfStatement(condition, thenBlock, elseBlock, ifStmt.Span);
    }

    private static BoundForStatement LowerForStatement(ForStatementSyntax forStmt, LowerContext ctx)
    {
        // Source pipeline runs in the *outer* scope (otherwise it could
        // not reference the loop variable retroactively, which would
        // be nonsensical, but more importantly so its bindings stay
        // visible after the loop ends).
        var source = LowerPipeline(forStmt.Source, ctx);

        // The loop variable lives in a fresh scope shared with the
        // body. We push the scope manually here (rather than using
        // LowerBlock) so the loop variable is in scope while the
        // body's statements are lowered.
        ctx.PushScope();
        try
        {
            var loopVar = ctx.DeclareLocal(
                forStmt.VariableName,
                BoundSymbolKind.LoopVariable,
                BoundType.Dynamic);

            var statements = new List<BoundStatement>(forStmt.Body.Statements.Count);
            foreach (var inner in forStmt.Body.Statements)
            {
                statements.Add(LowerStatement(inner, ctx));
            }
            var body = new BoundBlock(statements, forStmt.Body.Span);

            return new BoundForStatement(loopVar, source, body, forStmt.Span);
        }
        finally
        {
            ctx.PopScope();
        }
    }

    // ── pipelines ──────────────────────────────────────────────

    private static BoundPipeline LowerPipeline(PipelineSyntax pipeline, LowerContext ctx)
    {
        var stages = new List<BoundPipelineStage>(pipeline.Stages.Count);
        foreach (var stage in pipeline.Stages)
        {
            stages.Add(LowerPipelineStage(stage, ctx));
        }

        TryAttachSortFirstFusion(pipeline);

        var span = pipeline.Stages.Count > 0
            ? pipeline.Stages[0].Span
            : new TextSpan(0, 0);

        return new BoundPipeline(stages, pipeline, span);
    }

    /// <summary>
    /// Detects <c>... | sort [-r] | first N</c> and stamps a
    /// <see cref="SortFirstFusion"/> on the parse-tree pipeline. Only
    /// fires when sort has no key selector and no flags other than
    /// reverse, and when first's count is a literal non-negative int.
    /// </summary>
    private static void TryAttachSortFirstFusion(PipelineSyntax pipeline)
    {
        if (pipeline.Stages.Count < 2)
        {
            return;
        }

        // Last two stages must be `sort` then `first`.
        if (pipeline.Stages[^2] is not CommandSyntax sortCmd
            || !string.Equals(sortCmd.Name, "sort", StringComparison.Ordinal))
        {
            return;
        }

        if (pipeline.Stages[^1] is not CommandSyntax firstCmd
            || !string.Equals(firstCmd.Name, "first", StringComparison.Ordinal))
        {
            return;
        }

        // Sort must have no positionals (no key selector) and at most a
        // single -r/--reverse flag. Flags are surfaced as barewords like
        // "-r" or "--reverse"; anything else (a literal, a var-ref) is a
        // positional that we refuse to fuse.
        bool reverse = false;
        foreach (var arg in sortCmd.Arguments)
        {
            if (arg is BarewordArgumentSyntax bareword && bareword.Value.StartsWith('-'))
            {
                var name = bareword.Value.TrimStart('-');
                if (string.Equals(name, "r", StringComparison.Ordinal)
                    || string.Equals(name, "reverse", StringComparison.Ordinal)
                    || string.Equals(name, "d", StringComparison.Ordinal)
                    || string.Equals(name, "desc", StringComparison.Ordinal)
                    || string.Equals(name, "descending", StringComparison.Ordinal))
                {
                    reverse = true;
                    continue;
                }

                // Any other flag (-n, -u, -h) changes semantics — bail.
                return;
            }

            // Any non-flag argument (selector, var-ref, literal) — bail.
            return;
        }

        // first's count must be a literal int (or absent → 1).
        int count;
        if (firstCmd.Arguments.Count == 0)
        {
            count = 1;
        }
        else if (firstCmd.Arguments.Count == 1 && TryReadLiteralInt(firstCmd.Arguments[0], out var parsed))
        {
            count = parsed;
        }
        else
        {
            return;
        }

        if (count < 0)
        {
            return;
        }

        pipeline.Fusion = new SortFirstFusion(StagesConsumed: 2, Count: count, Reverse: reverse);
    }

    private static bool TryReadLiteralInt(ArgumentSyntax arg, out int value)
    {
        // Honour the constant-fold annotation produced earlier in
        // lowering so expressions like `first (1 + 2)` participate.
        if (arg is OperatorArgumentSyntax op && op.FoldedConstant is { Value: int folded })
        {
            value = folded;
            return true;
        }

        if (arg is LiteralArgumentSyntax literal && literal.Value is int direct)
        {
            value = direct;
            return true;
        }

        value = 0;
        return false;
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

        ArrayLiteralArgumentSyntax array => BuildArrayLiteral(array, ctx),

        InterpolatedStringArgumentSyntax interp => BuildInterpolatedString(interp, ctx),

        ConditionalArgumentSyntax cond =>
            new BoundConditional(
                Condition: LowerExpression(cond.Condition, ctx),
                WhenTrue: LowerExpression(cond.WhenTrue, ctx),
                WhenFalse: LowerExpression(cond.WhenFalse, ctx),
                Span: cond.Span,
                Type: BoundType.Dynamic),

        IfExpressionArgumentSyntax ifExpr =>
            new BoundIfExpression(
                Condition: LowerExpression(ifExpr.Condition, ctx),
                ThenBlock: LowerBlock(ifExpr.ThenBlock, ctx),
                ElseBlock: LowerBlock(ifExpr.ElseBlock, ctx),
                Span: ifExpr.Span,
                Type: BoundType.Dynamic),

        BlockArgumentSyntax block => BuildBlockExpression(block, ctx),

        AnonymousFunctionArgumentSyntax lambda => BuildLambda(lambda, ctx),

        CallableInvocationArgumentSyntax invoke =>
            new BoundCallableInvocation(
                Target: LowerExpression(invoke.Target, ctx),
                Arguments: BuildArgumentList(invoke.Arguments, ctx),
                Span: invoke.Span,
                Type: BoundType.Dynamic),

        // Everything else stays dynamic for now.
        _ => new BoundDynamicExpression(expression, expression.Span),
    };

    private static BoundVariableReference BuildVariableReference(VariableReferenceArgumentSyntax varRef, LowerContext ctx)
    {
        var symbol = ctx.LookupSymbol(varRef.Name);
        // If the reference resolves to a symbol declared outside any
        // currently-active lambda frame, mark the symbol as captured
        // by every enclosing lambda whose entry-depth is deeper than
        // the symbol's own scope depth. The lambda itself records the
        // captures; this side effect is O(active-lambdas) per ref.
        if (symbol is not null)
        {
            ctx.RecordPotentialCapture(symbol);
        }
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

    private static BoundArrayLiteral BuildArrayLiteral(ArrayLiteralArgumentSyntax array, LowerContext ctx)
    {
        var items = new List<BoundArrayLiteralItem>(array.Items.Count);
        foreach (var raw in array.Items)
        {
            // ...$xs spreads splice into the array; everything else
            // is a single element. Track the flag so the IL emitter
            // can pick the right opcode shape later.
            if (raw is SpreadElementArgumentSyntax spread)
            {
                items.Add(new BoundArrayLiteralItem(
                    Value: LowerExpression(spread.Value, ctx),
                    IsSpread: true,
                    Span: spread.Span));
            }
            else
            {
                items.Add(new BoundArrayLiteralItem(
                    Value: LowerExpression(raw, ctx),
                    IsSpread: false,
                    Span: raw.Span));
            }
        }

        return new BoundArrayLiteral(items, array.Span, BoundType.FromClr(typeof(System.Collections.IList)));
    }

    private static BoundInterpolatedString BuildInterpolatedString(
        InterpolatedStringArgumentSyntax interp,
        LowerContext ctx)
    {
        var parts = new List<BoundInterpolatedPart>(interp.Parts.Count);
        foreach (var part in interp.Parts)
        {
            switch (part)
            {
                case InterpolatedStringLiteralPart literal:
                    parts.Add(new BoundInterpolatedLiteral(literal.Text, interp.Span));
                    break;

                case InterpolatedStringExpressionPart expr:
                    // The parser stores the hole as raw source text
                    // (re-parsed at runtime today). We don't yet
                    // re-lex+parse here; the IL emitter can fall back
                    // to runtime re-parse via SourceText. Once
                    // expressions inside holes are first-class in the
                    // parse tree, this becomes a recursive lower.
                    parts.Add(new BoundInterpolatedExpression(
                        SourceText: expr.Expression,
                        Expression: null,
                        Span: expr.ExpressionSpan));
                    break;
            }
        }

        return new BoundInterpolatedString(parts, interp.Span, BoundType.FromClr(typeof(string)));
    }

    /// <summary>
    /// Lowers a bare block argument: <c>where { $_ > 5 }</c>. The
    /// block itself has no formal parameters; <c>$_</c> is supplied by
    /// the host command at runtime. Captures are recorded by the
    /// lambda frame on <see cref="LowerContext"/>.
    /// </summary>
    private static BoundBlockExpression BuildBlockExpression(BlockArgumentSyntax block, LowerContext ctx)
    {
        var captures = ctx.EnterLambda();
        try
        {
            var body = LowerBlock(block.Block, ctx);
            return new BoundBlockExpression(
                Body: body,
                Captures: captures.ToImmutableList(),
                Span: block.Span,
                Type: BoundType.Dynamic);
        }
        finally
        {
            ctx.ExitLambda();
        }
    }

    /// <summary>
    /// Lowers <c>fn(x, y) => …</c> / <c>{|x, y| …}</c>. Defaults are
    /// lowered in the *outer* scope (so they can capture but not
    /// shadow), then a fresh scope is pushed and parameters are
    /// declared inside it before the body lowers.
    /// </summary>
    private static BoundLambda BuildLambda(AnonymousFunctionArgumentSyntax lambda, LowerContext ctx)
    {
        // Lower default-value pipelines in the outer scope first.
        var pendingDefaults = new BoundPipeline?[lambda.Parameters.Count];
        for (var i = 0; i < lambda.Parameters.Count; i++)
        {
            var param = lambda.Parameters[i];
            pendingDefaults[i] = param.DefaultValue is null
                ? null
                : LowerPipeline(param.DefaultValue, ctx);
        }

        var captures = ctx.EnterLambda();
        try
        {
            ctx.PushScope();
            try
            {
                var bound = new List<BoundParameter>(lambda.Parameters.Count);
                for (var i = 0; i < lambda.Parameters.Count; i++)
                {
                    var param = lambda.Parameters[i];
                    var symbol = ctx.DeclareLocal(
                        param.Name,
                        BoundSymbolKind.Parameter,
                        BoundType.Dynamic);
                    bound.Add(new BoundParameter(
                        Name: param.Name,
                        Symbol: symbol,
                        Default: pendingDefaults[i],
                        IsOptional: param.IsOptional,
                        IsRest: param.IsRest,
                        Span: param.Span));
                }

                // Lower body statements directly (we already have the
                // outer scope pushed by EnterLambda → PushScope, so a
                // second LowerBlock would push *another* scope and
                // hide the parameters from immediate references).
                var statements = new List<BoundStatement>(lambda.Body.Statements.Count);
                foreach (var inner in lambda.Body.Statements)
                {
                    statements.Add(LowerStatement(inner, ctx));
                }
                var body = new BoundBlock(statements, lambda.Body.Span);

                return new BoundLambda(
                    Parameters: bound,
                    Body: body,
                    Captures: captures.ToImmutableList(),
                    Span: lambda.Span,
                    Type: BoundType.Dynamic);
            }
            finally
            {
                ctx.PopScope();
            }
        }
        finally
        {
            ctx.ExitLambda();
        }
    }

    private static IReadOnlyList<BoundArgument> BuildArgumentList(
        IReadOnlyList<ArgumentSyntax> arguments,
        LowerContext ctx)
    {
        var result = new List<BoundArgument>(arguments.Count);
        foreach (var arg in arguments)
        {
            result.Add(LowerArgument(arg, ctx));
        }
        return result;
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

        // Each active lambda frame records the scope-depth at which
        // it was entered plus an ordered set of captures discovered
        // so far. Insertion order is preserved so the IL emitter sees
        // a stable closure-field layout (HashSet on .NET preserves
        // insertion-order semantics for enumeration in practice; we
        // additionally keep a parallel List to make this guarantee
        // explicit).
        private readonly List<(int EntryDepth, HashSet<BoundSymbol> Seen, List<BoundSymbol> Order)> _lambdaFrames = new();

        public LowerContext(ShellCommandRegistry commands)
        {
            Commands = commands;
            _scopes.Add(new Dictionary<string, BoundSymbol>(StringComparer.Ordinal));
        }

        public ShellCommandRegistry Commands { get; }

        public List<BoundSymbol> Symbols { get; } = new();

        public void PushScope()
        {
            _scopes.Add(new Dictionary<string, BoundSymbol>(StringComparer.Ordinal));
        }

        public void PopScope()
        {
            // Outermost scope is the file-level frame and is never popped.
            if (_scopes.Count <= 1) return;
            _scopes.RemoveAt(_scopes.Count - 1);
        }

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

        public BoundSymbol DeclareLocal(string name, BoundSymbolKind kind, BoundType declaredType)
        {
            var symbol = new BoundSymbol(
                Name: name,
                Kind: kind,
                ScopeDepth: _scopes.Count - 1,
                DeclaredType: declaredType);

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

        /// <summary>
        /// Begins a lambda frame. All variable references made before
        /// the matching <see cref="ExitLambda"/> will, if they resolve
        /// to a symbol declared at a shallower scope than the entry
        /// depth, be recorded as captures by this frame (and any
        /// enclosing frames whose entry-depth is also shallower).
        /// Returns the ordered capture list so the caller can attach
        /// it to the <see cref="BoundLambda"/> / <see cref="BoundBlockExpression"/>
        /// once lowering of the body completes.
        /// </summary>
        public List<BoundSymbol> EnterLambda()
        {
            var order = new List<BoundSymbol>();
            _lambdaFrames.Add((EntryDepth: _scopes.Count, Seen: new HashSet<BoundSymbol>(), Order: order));
            return order;
        }

        public void ExitLambda()
        {
            if (_lambdaFrames.Count == 0) return;
            _lambdaFrames.RemoveAt(_lambdaFrames.Count - 1);
        }

        /// <summary>
        /// Called from <see cref="BuildVariableReference"/> for every
        /// resolved symbol. If any active lambda frame's entry-depth
        /// is deeper than the symbol's own scope-depth, the symbol is
        /// captured by that frame.
        /// </summary>
        public void RecordPotentialCapture(BoundSymbol symbol)
        {
            for (var i = 0; i < _lambdaFrames.Count; i++)
            {
                var frame = _lambdaFrames[i];
                if (symbol.ScopeDepth < frame.EntryDepth)
                {
                    if (frame.Seen.Add(symbol))
                    {
                        frame.Order.Add(symbol);
                    }
                }
            }
        }
    }
}
