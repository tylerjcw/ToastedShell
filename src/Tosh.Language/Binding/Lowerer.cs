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

        ReturnStatementSyntax ret =>
            new BoundReturnStatement(
                Value: ret.Value is null ? null : LowerPipeline(ret.Value, ctx),
                Span: ret.Span),

        ThrowStatementSyntax thr =>
            new BoundThrowStatement(
                Value: thr.Value is null ? null : LowerPipeline(thr.Value, ctx),
                Span: thr.Span),

        TryStatementSyntax tryStmt =>
            LowerTryStatement(tryStmt, ctx),

        SwitchStatementSyntax switchStmt =>
            LowerSwitchStatement(switchStmt, ctx),

        MemberAssignmentStatementSyntax memberAssign =>
            new BoundMemberAssignment(
                Target: LowerExpression(memberAssign.Target, ctx),
                Operator: memberAssign.Operator,
                Value: LowerPipeline(memberAssign.Value, ctx),
                Span: memberAssign.Span),

        DeferStatementSyntax deferStmt =>
            new BoundDeferStatement(LowerBlock(deferStmt.Body, ctx), deferStmt.Span),

        YieldStatementSyntax yieldStmt =>
            new BoundYieldStatement(
                Value: yieldStmt.Value is null ? null : LowerPipeline(yieldStmt.Value, ctx),
                Span: yieldStmt.Span),

        UsingStatementSyntax usingStmt =>
            new BoundUsingStatement(usingStmt.Target, usingStmt.Alias, usingStmt.Modifier, usingStmt.Span),

        TupleAssignmentStatementSyntax tupleAssign =>
            new BoundTupleAssignment(
                Names: tupleAssign.LeftNames,
                Value: LowerPipeline(tupleAssign.Value, ctx),
                Span: tupleAssign.Span),

        DestructuringDeclarationStatementSyntax destruct =>
            LowerDestructuringDeclaration(destruct, ctx),

        AllocStatementSyntax alloc =>
            LowerAllocStatement(alloc, ctx),

        FunctionDefinitionStatementSyntax funcDef =>
            LowerFunctionDefinition(funcDef, ctx),

        RuneDefinitionStatementSyntax runeDef =>
            LowerRuneDefinition(runeDef, ctx),

        ClassDefinitionStatementSyntax classDef =>
            LowerClassDefinition(classDef, ctx),

        InterfaceDefinitionStatementSyntax ifaceDef =>
            LowerInterfaceDefinition(ifaceDef, ctx),

        UnionDefinitionStatementSyntax unionDef =>
            LowerUnionDefinition(unionDef, ctx),

        EnumDefinitionStatementSyntax enumDef =>
            LowerEnumDefinition(enumDef, ctx),

        RecordDefinitionStatementSyntax recordDef =>
            LowerRecordDefinition(recordDef, ctx),

        StructDefinitionStatementSyntax structDef =>
            LowerStructDefinition(structDef, ctx),

        TraitDefinitionStatementSyntax traitDef =>
            LowerTraitDefinition(traitDef, ctx),

        EventDefinitionStatementSyntax eventDef =>
            LowerEventDefinition(eventDef, ctx),

        ModuleDefinitionStatementSyntax moduleDef =>
            new BoundModuleDefinition(
                Name: moduleDef.Name,
                Body: LowerBlock(moduleDef.Body, ctx),
                Modifier: moduleDef.Modifier,
                Span: moduleDef.Span),

        SubcommandStatementSyntax subcmd =>
            new BoundSubcommandStatement(
                Name: subcmd.Name,
                Modifiers: subcmd.Modifiers,
                Body: LowerBlock(subcmd.Body, ctx),
                Span: subcmd.Span),

        ScriptInputStatementSyntax scriptInput =>
            LowerScriptInputStatement(scriptInput, ctx),

        TypeAliasStatementSyntax typeAlias =>
            new BoundTypeAliasStatement(
                Name: typeAlias.Name,
                TypeParameters: typeAlias.TypeParameters,
                BaseTypeName: typeAlias.BaseTypeName,
                Refinement: typeAlias.Refinement is null ? null : LowerExpression(typeAlias.Refinement, ctx),
                Modifier: typeAlias.Modifier,
                Span: typeAlias.Span),

        RequireStatementSyntax require =>
            LowerRequireStatement(require),

        BindStatementSyntax bind =>
            LowerBindStatement(bind),

        ScriptStatementSyntax inner =>
            LowerStatementAsScript(inner, ctx),

        // Anything else falls back to a dynamic statement for now.
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

    /// <summary>
    /// Lowers <c>try { … } catch [(name)] { … } finally { … }</c>.
    /// The catch variable, when present, is declared as a fresh
    /// <see cref="BoundSymbolKind.CatchVariable"/> binding inside the
    /// catch block's scope so the body can reference it.
    /// </summary>
    private static BoundTryStatement LowerTryStatement(TryStatementSyntax tryStmt, LowerContext ctx)
    {
        var tryBlock = LowerBlock(tryStmt.TryBlock, ctx);

        BoundCatchClause? catchClause = null;
        if (tryStmt.CatchClause is { } rawCatch)
        {
            ctx.PushScope();
            try
            {
                BoundSymbol? catchVar = null;
                if (!string.IsNullOrEmpty(rawCatch.VariableName))
                {
                    catchVar = ctx.DeclareLocal(
                        rawCatch.VariableName,
                        BoundSymbolKind.CatchVariable,
                        BoundType.Dynamic);
                }

                var statements = new List<BoundStatement>(rawCatch.Body.Statements.Count);
                foreach (var inner in rawCatch.Body.Statements)
                {
                    statements.Add(LowerStatement(inner, ctx));
                }
                var body = new BoundBlock(statements, rawCatch.Body.Span);

                catchClause = new BoundCatchClause(catchVar, body, rawCatch.Span);
            }
            finally
            {
                ctx.PopScope();
            }
        }

        var finallyBlock = tryStmt.FinallyBlock is null
            ? null
            : LowerBlock(tryStmt.FinallyBlock, ctx);

        return new BoundTryStatement(tryBlock, catchClause, finallyBlock, tryStmt.Span);
    }

    /// <summary>
    /// Lowers <c>switch ($v) { case … { } default { } }</c>. Each
    /// case body opens its own scope (matches the runtime's
    /// behavior).
    /// </summary>
    private static BoundSwitchStatement LowerSwitchStatement(SwitchStatementSyntax switchStmt, LowerContext ctx)
    {
        var value = LowerExpression(switchStmt.Value, ctx);
        var cases = new List<BoundSwitchCase>(switchStmt.Cases.Count);
        foreach (var rawCase in switchStmt.Cases)
        {
            cases.Add(new BoundSwitchCase(
                Pattern: LowerExpression(rawCase.MatchExpression, ctx),
                Guard: rawCase.Guard is null ? null : LowerExpression(rawCase.Guard, ctx),
                Body: LowerBlock(rawCase.Body, ctx),
                Span: rawCase.Span));
        }

        var defaultBlock = switchStmt.DefaultBlock is null
            ? null
            : LowerBlock(switchStmt.DefaultBlock, ctx);

        return new BoundSwitchStatement(value, cases, defaultBlock, switchStmt.Span);
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

        ThrowArgumentSyntax thr =>
            new BoundThrowExpression(
                Value: thr.Value is null ? null : LowerExpression(thr.Value, ctx),
                Span: thr.Span,
                Type: BoundType.Dynamic),

        MatchArgumentSyntax match => BuildMatchExpression(match, ctx),

        NewObjectArgumentSyntax newObj =>
            new BoundNewObject(
                TypeName: newObj.TypeName,
                Arguments: BuildArgumentList(newObj.Arguments, ctx),
                Span: newObj.Span,
                Type: BoundType.Dynamic),

        MethodCallArgumentSyntax method =>
            new BoundMethodCall(
                Target: LowerExpression(method.Target, ctx),
                MethodName: method.MethodName,
                Arguments: BuildArgumentList(method.Arguments, ctx),
                NullSafe: method.NullSafe,
                Span: method.Span,
                Type: BoundType.Dynamic),

        StaticMethodCallArgumentSyntax staticCall =>
            new BoundStaticMethodCall(
                Path: staticCall.Path,
                Arguments: BuildArgumentList(staticCall.Arguments, ctx),
                Span: staticCall.Span,
                Type: BoundType.Dynamic),

        StaticMemberAccessArgumentSyntax staticMember =>
            new BoundStaticMemberAccess(
                Path: staticMember.Path,
                Span: staticMember.Span,
                Type: BoundType.Dynamic),

        IndexAccessArgumentSyntax index =>
            new BoundIndexAccess(
                Target: LowerExpression(index.Target, ctx),
                Index: LowerExpression(index.Index, ctx),
                LookupKind: index.LookupKind,
                Span: index.Span,
                Type: BoundType.Dynamic),

        RecordLiteralArgumentSyntax record => BuildRecordLiteral(record, ctx),

        DictLiteralArgumentSyntax dict => BuildDictLiteral(dict, ctx),

        SetLiteralArgumentSyntax set =>
            new BoundSetLiteral(
                Items: BuildExpressionList(set.Items, ctx),
                Span: set.Span,
                Type: BoundType.FromClr(typeof(System.Collections.IList))),

        TupleLiteralArgumentSyntax tuple =>
            new BoundTupleLiteral(
                Items: BuildExpressionList(tuple.Items, ctx),
                Span: tuple.Span,
                Type: BoundType.Dynamic),

        SubexpressionArgumentSyntax subexpr =>
            new BoundSubexpression(
                Pipeline: LowerPipeline(subexpr.Pipeline, ctx),
                Span: subexpr.Span,
                Type: BoundType.Dynamic),

        CommandSubstitutionArgumentSyntax cmdSub =>
            new BoundCommandSubstitution(
                Pipeline: LowerPipeline(cmdSub.Pipeline, ctx),
                Span: cmdSub.Span,
                Type: BoundType.Dynamic),

        InputProcessSubstitutionArgumentSyntax inProc =>
            new BoundInputProcessSubstitution(
                Pipeline: LowerPipeline(inProc.Pipeline, ctx),
                Span: inProc.Span,
                Type: BoundType.Dynamic),

        OutputProcessSubstitutionArgumentSyntax outProc =>
            new BoundOutputProcessSubstitution(
                Pipeline: LowerPipeline(outProc.Pipeline, ctx),
                Span: outProc.Span,
                Type: BoundType.Dynamic),

        QuoteArgumentSyntax quote =>
            // We deliberately keep the inner AST verbatim — quote's
            // semantics are "capture the parse tree as a value".
            new BoundQuoteExpression(quote.Inner, quote.Span, BoundType.Dynamic),

        NameOfArgumentSyntax nameOf =>
            new BoundNameOfExpression(
                Identifier: nameOf.Identifier,
                IsVariableReference: nameOf.IsVariableReference,
                Span: nameOf.Span,
                Type: BoundType.FromClr(typeof(string))),

        FunctionReferenceArgumentSyntax fnRef =>
            new BoundFunctionReference(
                Name: fnRef.Name,
                Symbol: ctx.LookupSymbol(fnRef.Name),
                Span: fnRef.Span,
                Type: BoundType.Dynamic),

        MemberProjectionArgumentSyntax projection =>
            new BoundMemberProjection(
                MemberPaths: projection.MemberPaths,
                Span: projection.Span,
                Type: BoundType.Dynamic),

        ComparisonPatternSyntax cmpPat =>
            new BoundComparisonPattern(
                Operator: cmpPat.Operator,
                Operand: LowerExpression(cmpPat.Operand, ctx),
                Span: cmpPat.Span,
                Type: BoundType.Dynamic),

        // Comprehensions and refinement clauses are deeply recursive
        // shapes whose semantics are hard to flatten into a single
        // bound node. Keep them dynamic — the lowering coverage on
        // them can grow when (or if) the IL emitter needs structured
        // access.
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
                    // Holes are stored by the parser as raw source
                    // text; we re-parse the snippet and lower the
                    // resulting expression so consumers like the IL
                    // emitter can avoid a runtime re-parse for the
                    // common cases (variable references, arithmetic,
                    // string concat, etc.). If anything fails, we
                    // leave Expression null and the runtime fallback
                    // path takes over.
                    parts.Add(new BoundInterpolatedExpression(
                        SourceText: expr.Expression,
                        Expression: TryLowerInterpolationHole(expr.Expression, ctx),
                        Span: expr.ExpressionSpan));
                    break;
            }
        }

        return new BoundInterpolatedString(parts, interp.Span, BoundType.FromClr(typeof(string)));
    }

    /// <summary>
    /// Best-effort carve-out for a single interpolation hole. Re-parses
    /// the hole's source text and lowers the resulting expression
    /// using the surrounding <paramref name="ctx"/>, so variable
    /// references inside the hole resolve against the outer scope.
    /// Returns <c>null</c> when the snippet doesn't fit one of the
    /// supported shapes — that signals downstream consumers to fall
    /// back to a runtime re-parse.
    /// </summary>
    private static BoundExpression? TryLowerInterpolationHole(string text, LowerContext ctx)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        ParseResult parsed;
        try
        {
            parsed = ToshParser.Parse(text, "<interp-hole>");
        }
        catch
        {
            return null;
        }

        if (parsed.Diagnostics.Count > 0) return null;
        if (parsed.Statement is not PipelineStatementSyntax pipelineStmt) return null;

        var pipeline = pipelineStmt.Pipeline;
        if (pipeline.Stages.Count != 1) return null;

        // Direct expression stage: lower as-is.
        if (pipeline.Stages[0] is ExpressionPipelineStageSyntax exprStage)
        {
            return LowerExpression(exprStage.Expression, ctx);
        }

        // Otherwise (command stage, etc.) wrap the lowered pipeline in
        // a subexpression; the IL emitter already unwraps single-stage
        // subexpressions back into the inner expression.
        return new BoundSubexpression(
            Pipeline: LowerPipeline(pipeline, ctx),
            Span: pipelineStmt.Span,
            Type: BoundType.Dynamic);
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

    /// <summary>
    /// Lowers a <c>match</c> expression. Each arm's body is lowered
    /// as a <see cref="BoundBlock"/>; pipeline arms (<c>=&gt; expr</c>)
    /// are wrapped as a single-statement block so the IL emitter
    /// sees one consistent shape.
    /// </summary>
    private static BoundMatchExpression BuildMatchExpression(MatchArgumentSyntax match, LowerContext ctx)
    {
        var value = LowerExpression(match.Value, ctx);
        var arms = new List<BoundMatchArm>(match.Arms.Count);
        foreach (var arm in match.Arms)
        {
            var pattern = arm.Pattern is null ? null : LowerExpression(arm.Pattern, ctx);
            var guard = arm.Guard is null ? null : LowerExpression(arm.Guard, ctx);

            BoundBlock body;
            switch (arm.Body)
            {
                case MatchArmBlockBodySyntax blockBody:
                    body = LowerBlock(blockBody.Block, ctx);
                    break;

                case MatchArmPipelineBodySyntax pipelineBody:
                    ctx.PushScope();
                    try
                    {
                        var pipe = LowerPipeline(pipelineBody.Pipeline, ctx);
                        var stmt = new BoundPipelineStatement(pipe, pipelineBody.Span);
                        body = new BoundBlock(new BoundStatement[] { stmt }, pipelineBody.Span);
                    }
                    finally
                    {
                        ctx.PopScope();
                    }
                    break;

                default:
                    body = new BoundBlock(Array.Empty<BoundStatement>(), arm.Span);
                    break;
            }

            arms.Add(new BoundMatchArm(pattern, guard, body, arm.IsWildcard, arm.Span));
        }

        return new BoundMatchExpression(value, arms, match.Span, BoundType.Dynamic);
    }

    // ── Phase C-3 helpers ───────────────────────────────────────────

    private static IReadOnlyList<BoundExpression> BuildExpressionList(
        IReadOnlyList<ArgumentSyntax> items,
        LowerContext ctx)
    {
        var result = new List<BoundExpression>(items.Count);
        foreach (var item in items)
        {
            result.Add(LowerExpression(item, ctx));
        }
        return result;
    }

    private static BoundRecordLiteral BuildRecordLiteral(
        RecordLiteralArgumentSyntax record,
        LowerContext ctx)
    {
        var entries = new List<BoundRecordEntry>(record.Fields.Count);
        foreach (var field in record.Fields)
        {
            switch (field)
            {
                case RecordFieldSyntax named:
                    entries.Add(new BoundRecordField(
                        Name: named.Name,
                        Value: LowerExpression(named.Value, ctx),
                        Span: named.Span));
                    break;
                case ComputedRecordFieldSyntax computed:
                    entries.Add(new BoundComputedRecordField(
                        NameExpression: LowerExpression(computed.NameExpression, ctx),
                        Value: LowerExpression(computed.Value, ctx),
                        Span: computed.Span));
                    break;
                case SpreadRecordEntrySyntax spread:
                    entries.Add(new BoundRecordSpreadEntry(
                        Value: LowerExpression(spread.Value, ctx),
                        Span: spread.Span));
                    break;
            }
        }
        return new BoundRecordLiteral(entries, record.Span, BoundType.Dynamic);
    }

    private static BoundDictLiteral BuildDictLiteral(
        DictLiteralArgumentSyntax dict,
        LowerContext ctx)
    {
        var entries = new List<BoundDictEntry>(dict.Entries.Count);
        foreach (var entry in dict.Entries)
        {
            entries.Add(new BoundDictEntry(
                Key: LowerExpression(entry.Key, ctx),
                Value: LowerExpression(entry.Value, ctx),
                Span: entry.Span));
        }
        return new BoundDictLiteral(entries, dict.Span, BoundType.FromClr(typeof(System.Collections.IDictionary)));
    }

    private static BoundDestructuringDeclaration LowerDestructuringDeclaration(
        DestructuringDeclarationStatementSyntax destruct,
        LowerContext ctx)
    {
        var value = LowerPipeline(destruct.Value, ctx);
        BoundDestructuringPattern pattern = destruct.Pattern switch
        {
            ArrayDestructuringPatternSyntax arrayPat =>
                new BoundArrayDestructuringPattern(
                    Symbols: DeclareDestructuredNames(arrayPat.Names, ctx),
                    Span: arrayPat.Span),

            RecordDestructuringPatternSyntax recordPat =>
                new BoundRecordDestructuringPattern(
                    Symbols: DeclareDestructuredNames(recordPat.Names, ctx),
                    Span: recordPat.Span),

            _ => new BoundArrayDestructuringPattern(Array.Empty<BoundSymbol>(), destruct.Pattern.Span),
        };

        return new BoundDestructuringDeclaration(pattern, value, destruct.Modifier, destruct.Span);
    }

    private static IReadOnlyList<BoundSymbol> DeclareDestructuredNames(
        IReadOnlyList<string> names,
        LowerContext ctx)
    {
        var symbols = new List<BoundSymbol>(names.Count);
        foreach (var name in names)
        {
            symbols.Add(ctx.DeclareLocal(name, BoundSymbolKind.Destructured, BoundType.Dynamic));
        }
        return symbols;
    }

    private static BoundAllocStatement LowerAllocStatement(AllocStatementSyntax alloc, LowerContext ctx)
    {
        var value = LowerPipeline(alloc.Value, ctx);
        // Alloc binds a name like `var` does.
        ctx.DeclareLocal(alloc.Name, BoundType.Dynamic);
        return new BoundAllocStatement(alloc.Name, value, alloc.Modifier, alloc.Span);
    }

    /// <summary>
    /// Lowers a parameter list for use inside a function/lambda
    /// declaration. Defaults are lowered in the *outer* scope before
    /// any parameter is bound (matches lambda semantics).
    /// </summary>
    private static (IReadOnlyList<BoundParameter> Bound, BoundPipeline?[] Defaults) LowerParameterDefaults(
        IReadOnlyList<FunctionParameterSyntax> parameters,
        LowerContext ctx)
    {
        var defaults = new BoundPipeline?[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            defaults[i] = parameters[i].DefaultValue is null
                ? null
                : LowerPipeline(parameters[i].DefaultValue!, ctx);
        }
        return (Array.Empty<BoundParameter>(), defaults);
    }

    /// <summary>
    /// Declares the parameter symbols and produces the BoundParameter
    /// list. Caller is responsible for having opened a scope before
    /// calling this.
    /// </summary>
    private static IReadOnlyList<BoundParameter> DeclareParameters(
        IReadOnlyList<FunctionParameterSyntax> parameters,
        BoundPipeline?[] defaults,
        LowerContext ctx)
    {
        var bound = new List<BoundParameter>(parameters.Count);
        for (var i = 0; i < parameters.Count; i++)
        {
            var param = parameters[i];
            var symbol = ctx.DeclareLocal(param.Name, BoundSymbolKind.Parameter, BoundType.Dynamic);
            bound.Add(new BoundParameter(
                Name: param.Name,
                Symbol: symbol,
                Default: defaults[i],
                IsOptional: param.IsOptional,
                IsRest: param.IsRest,
                Span: param.Span));
        }
        return bound;
    }

    private static BoundFunctionDefinition LowerFunctionDefinition(
        FunctionDefinitionStatementSyntax funcDef,
        LowerContext ctx)
    {
        // Bind the function name in the outer scope so recursive
        // references inside the body can resolve to this symbol.
        var funcSymbol = ctx.DeclareLocal(funcDef.Name, BoundSymbolKind.LocalVariable, BoundType.Dynamic);

        var (_, defaults) = LowerParameterDefaults(funcDef.Parameters, ctx);

        var captures = ctx.EnterLambda();
        try
        {
            ctx.PushScope();
            try
            {
                var bound = DeclareParameters(funcDef.Parameters, defaults, ctx);
                var statements = new List<BoundStatement>(funcDef.Body.Statements.Count);
                foreach (var inner in funcDef.Body.Statements)
                {
                    statements.Add(LowerStatement(inner, ctx));
                }
                var body = new BoundBlock(statements, funcDef.Body.Span);

                return new BoundFunctionDefinition(
                    Name: funcDef.Name,
                    Symbol: funcSymbol,
                    Parameters: bound,
                    ReturnTypeName: funcDef.ReturnTypeName,
                    Body: body,
                    Captures: captures.ToImmutableList(),
                    IsCommandWrapper: funcDef.IsCommandWrapper,
                    Modifier: funcDef.Modifier,
                    Span: funcDef.Span);
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

    private static BoundRuneDefinition LowerRuneDefinition(
        RuneDefinitionStatementSyntax runeDef,
        LowerContext ctx)
    {
        var runeSymbol = ctx.DeclareLocal(runeDef.Name, BoundSymbolKind.LocalVariable, BoundType.Dynamic);
        var (_, defaults) = LowerParameterDefaults(runeDef.Parameters, ctx);

        var captures = ctx.EnterLambda();
        try
        {
            ctx.PushScope();
            try
            {
                var bound = DeclareParameters(runeDef.Parameters, defaults, ctx);
                var statements = new List<BoundStatement>(runeDef.Body.Statements.Count);
                foreach (var inner in runeDef.Body.Statements)
                {
                    statements.Add(LowerStatement(inner, ctx));
                }
                var body = new BoundBlock(statements, runeDef.Body.Span);

                return new BoundRuneDefinition(
                    Name: runeDef.Name,
                    Symbol: runeSymbol,
                    Parameters: bound,
                    Body: body,
                    Captures: captures.ToImmutableList(),
                    IsSealed: runeDef.IsSealed,
                    IsFixed: runeDef.IsFixed,
                    Modifier: runeDef.Modifier,
                    Span: runeDef.Span);
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

    /// <summary>
    /// Lowers a class member (property / method / constructor).
    /// Opens a scope around the member body for symbol declaration.
    /// </summary>
    private static BoundClassMember LowerClassMember(ClassMemberSyntax member, LowerContext ctx)
    {
        switch (member)
        {
            case ClassPropertyMemberSyntax prop:
                {
                    var initializer = prop.Initializer is null ? null : LowerPipeline(prop.Initializer, ctx);
                    var getter = prop.GetterBody is null ? null : LowerBlock(prop.GetterBody, ctx);
                    var setter = prop.SetterBody is null ? null : LowerBlock(prop.SetterBody, ctx);
                    return new BoundClassPropertyMember(
                        Name: prop.Name,
                        TypeName: prop.TypeName,
                        Initializer: initializer,
                        GetterBody: getter,
                        SetterBody: setter,
                        IsShy: prop.IsShy,
                        IsStatic: prop.IsStatic,
                        IsFixed: prop.IsFixed,
                        IsVital: prop.IsVital,
                        IsGuarded: prop.IsGuarded,
                        IsLazy: prop.IsLazy,
                        IsFading: prop.IsFading,
                        IsLocal: prop.IsLocal,
                        IsAbstract: prop.IsAbstract,
                        Span: prop.Span);
                }

            case ClassMethodMemberSyntax method:
                return new BoundClassMethodMember(
                    Method: LowerFunctionDefinition(method.Method, ctx),
                    IsStatic: method.IsStatic,
                    IsShy: method.IsShy,
                    IsAbstract: method.IsAbstract,
                    IsOverride: method.IsOverride,
                    IsGuarded: method.IsGuarded,
                    IsFading: method.IsFading,
                    IsLocal: method.IsLocal,
                    IsRaw: method.IsRaw,
                    Span: method.Span);

            case ClassConstructorMemberSyntax ctor:
                {
                    var (_, defaults) = LowerParameterDefaults(ctor.Parameters, ctx);
                    ctx.PushScope();
                    try
                    {
                        var bound = DeclareParameters(ctor.Parameters, defaults, ctx);
                        var statements = new List<BoundStatement>(ctor.Body.Statements.Count);
                        foreach (var inner in ctor.Body.Statements)
                        {
                            statements.Add(LowerStatement(inner, ctx));
                        }
                        var body = new BoundBlock(statements, ctor.Body.Span);
                        return new BoundClassConstructorMember(bound, body, ctor.Span);
                    }
                    finally
                    {
                        ctx.PopScope();
                    }
                }

            default:
                throw new InvalidOperationException($"Unknown class member kind: {member.GetType().Name}");
        }
    }

    private static BoundClassDefinition LowerClassDefinition(
        ClassDefinitionStatementSyntax classDef,
        LowerContext ctx)
    {
        var (_, ctorDefaults) = LowerParameterDefaults(classDef.PrimaryConstructorParameters, ctx);

        BoundParameter[] primaryCtorParams;
        IReadOnlyList<BoundClassMember> members;
        IReadOnlyList<BoundPipeline>? baseArgs = null;

        ctx.PushScope();
        try
        {
            primaryCtorParams = DeclareParameters(classDef.PrimaryConstructorParameters, ctorDefaults, ctx).ToArray();

            if (classDef.BaseConstructorArgs is not null)
            {
                var lowered = new List<BoundPipeline>(classDef.BaseConstructorArgs.Count);
                foreach (var arg in classDef.BaseConstructorArgs)
                {
                    lowered.Add(LowerPipeline(arg, ctx));
                }
                baseArgs = lowered;
            }

            var memberList = new List<BoundClassMember>(classDef.Members.Count);
            foreach (var raw in classDef.Members)
            {
                memberList.Add(LowerClassMember(raw, ctx));
            }
            members = memberList;
        }
        finally
        {
            ctx.PopScope();
        }

        return new BoundClassDefinition(
            Name: classDef.Name,
            PrimaryConstructorParameters: primaryCtorParams,
            Members: members,
            BaseClassName: classDef.BaseClassName,
            BaseConstructorArgs: baseArgs,
            ImplementedInterfaces: classDef.ImplementedInterfaces,
            UsedTraits: classDef.UsedTraits,
            IsSealed: classDef.IsSealed,
            IsAbstract: classDef.IsAbstract,
            IsHermit: classDef.IsHermit,
            IsStrict: classDef.IsStrict,
            IsPartial: classDef.IsPartial,
            Modifier: classDef.Modifier,
            Span: classDef.Span);
    }

    private static BoundInterfaceDefinition LowerInterfaceDefinition(
        InterfaceDefinitionStatementSyntax ifaceDef,
        LowerContext ctx)
    {
        var methods = new List<BoundInterfaceMethodSignature>(ifaceDef.Methods.Count);
        foreach (var raw in ifaceDef.Methods)
        {
            var (_, defaults) = LowerParameterDefaults(raw.Parameters, ctx);
            ctx.PushScope();
            try
            {
                var bound = DeclareParameters(raw.Parameters, defaults, ctx);
                methods.Add(new BoundInterfaceMethodSignature(
                    Name: raw.Name,
                    Parameters: bound,
                    ReturnTypeName: raw.ReturnTypeName,
                    Span: raw.Span));
            }
            finally
            {
                ctx.PopScope();
            }
        }

        return new BoundInterfaceDefinition(
            Name: ifaceDef.Name,
            Methods: methods,
            Modifier: ifaceDef.Modifier,
            Span: ifaceDef.Span);
    }

    private static BoundUnionDefinition LowerUnionDefinition(
        UnionDefinitionStatementSyntax unionDef,
        LowerContext ctx)
    {
        var variants = new List<BoundUnionVariant>(unionDef.Variants.Count);
        foreach (var variant in unionDef.Variants)
        {
            var (_, defaults) = LowerParameterDefaults(variant.Fields, ctx);
            ctx.PushScope();
            try
            {
                var bound = DeclareParameters(variant.Fields, defaults, ctx);
                variants.Add(new BoundUnionVariant(variant.Name, bound, variant.Span));
            }
            finally
            {
                ctx.PopScope();
            }
        }

        return new BoundUnionDefinition(
            Name: unionDef.Name,
            Variants: variants,
            Modifier: unionDef.Modifier,
            Span: unionDef.Span);
    }

    private static BoundEnumDefinition LowerEnumDefinition(
        EnumDefinitionStatementSyntax enumDef,
        LowerContext ctx)
    {
        var members = new List<BoundEnumMember>(enumDef.Members.Count);
        foreach (var raw in enumDef.Members)
        {
            members.Add(new BoundEnumMember(
                Name: raw.Name,
                Value: raw.Value is null ? null : LowerPipeline(raw.Value, ctx),
                Span: raw.Span));
        }

        return new BoundEnumDefinition(
            Name: enumDef.Name,
            UnderlyingTypeName: enumDef.UnderlyingTypeName,
            Members: members,
            Modifier: enumDef.Modifier,
            Span: enumDef.Span);
    }

    private static IReadOnlyList<BoundRecordFieldDefinition> LowerRecordFields(
        IReadOnlyList<RecordFieldDefinitionSyntax> fields,
        LowerContext ctx)
    {
        var bound = new List<BoundRecordFieldDefinition>(fields.Count);
        foreach (var field in fields)
        {
            bound.Add(new BoundRecordFieldDefinition(
                Name: field.Name,
                TypeName: field.TypeName,
                DefaultValue: field.DefaultValue is null ? null : LowerPipeline(field.DefaultValue, ctx),
                IsOptional: field.IsOptional,
                Span: field.Span));
        }
        return bound;
    }

    private static BoundRecordDefinition LowerRecordDefinition(
        RecordDefinitionStatementSyntax recordDef,
        LowerContext ctx) =>
        new(
            Name: recordDef.Name,
            Fields: LowerRecordFields(recordDef.Fields, ctx),
            IsSealed: recordDef.IsSealed,
            IsStrict: recordDef.IsStrict,
            IsPartial: recordDef.IsPartial,
            Modifier: recordDef.Modifier,
            Span: recordDef.Span);

    private static BoundStructDefinition LowerStructDefinition(
        StructDefinitionStatementSyntax structDef,
        LowerContext ctx)
    {
        var fields = LowerRecordFields(structDef.Fields, ctx);
        var members = new List<BoundClassMember>(structDef.Members.Count);
        ctx.PushScope();
        try
        {
            foreach (var raw in structDef.Members)
            {
                members.Add(LowerClassMember(raw, ctx));
            }
        }
        finally
        {
            ctx.PopScope();
        }

        return new BoundStructDefinition(
            Name: structDef.Name,
            Fields: fields,
            Members: members,
            IsSealed: structDef.IsSealed,
            IsFluid: structDef.IsFluid,
            IsPartial: structDef.IsPartial,
            Modifier: structDef.Modifier,
            Span: structDef.Span);
    }

    private static BoundTraitDefinition LowerTraitDefinition(
        TraitDefinitionStatementSyntax traitDef,
        LowerContext ctx)
    {
        var methods = new List<BoundTraitMethodSignature>(traitDef.Methods.Count);
        foreach (var raw in traitDef.Methods)
        {
            var (_, defaults) = LowerParameterDefaults(raw.Parameters, ctx);
            ctx.PushScope();
            try
            {
                var bound = DeclareParameters(raw.Parameters, defaults, ctx);
                var defaultBody = raw.DefaultBody is null ? null : LowerBlock(raw.DefaultBody, ctx);
                methods.Add(new BoundTraitMethodSignature(
                    Name: raw.Name,
                    Parameters: bound,
                    ReturnTypeName: raw.ReturnTypeName,
                    DefaultBody: defaultBody,
                    Span: raw.Span));
            }
            finally
            {
                ctx.PopScope();
            }
        }

        var properties = new List<BoundTraitPropertySignature>(traitDef.Properties.Count);
        foreach (var raw in traitDef.Properties)
        {
            properties.Add(new BoundTraitPropertySignature(
                Name: raw.Name,
                TypeName: raw.TypeName,
                DefaultValue: raw.DefaultValue is null ? null : LowerPipeline(raw.DefaultValue, ctx),
                Span: raw.Span));
        }

        return new BoundTraitDefinition(
            Name: traitDef.Name,
            Methods: methods,
            Properties: properties,
            Modifier: traitDef.Modifier,
            Span: traitDef.Span);
    }

    private static BoundEventDefinition LowerEventDefinition(
        EventDefinitionStatementSyntax eventDef,
        LowerContext ctx)
    {
        var fields = new List<BoundEventFieldDefinition>(eventDef.Fields.Count);
        foreach (var raw in eventDef.Fields)
        {
            fields.Add(new BoundEventFieldDefinition(
                Name: raw.Name,
                TypeName: raw.TypeName,
                DefaultValue: raw.DefaultValue is null ? null : LowerPipeline(raw.DefaultValue, ctx),
                Span: raw.Span));
        }

        return new BoundEventDefinition(
            Name: eventDef.Name,
            Fields: fields,
            IsRequired: eventDef.IsRequired,
            IsLocal: eventDef.IsLocal,
            Modifier: eventDef.Modifier,
            Span: eventDef.Span);
    }

    private static BoundScriptInputStatement LowerScriptInputStatement(
        ScriptInputStatementSyntax scriptInput,
        LowerContext ctx)
    {
        var (_, defaults) = LowerParameterDefaults(scriptInput.Parameters, ctx);
        // Script inputs are visible at file scope, so we declare them
        // there rather than in a fresh scope.
        var bound = DeclareParameters(scriptInput.Parameters, defaults, ctx);
        return new BoundScriptInputStatement(scriptInput.Kind, bound, scriptInput.Span);
    }

    private static BoundRequireStatement LowerRequireStatement(RequireStatementSyntax require)
    {
        var imports = new List<BoundRequireImport>(require.Imports.Count);
        foreach (var imp in require.Imports)
        {
            imports.Add(new BoundRequireImport(imp.Name, imp.Alias, imp.Span));
        }

        return new BoundRequireStatement(
            Target: require.Target,
            Imports: imports,
            IsNative: require.IsNative,
            Alias: require.Alias,
            Modifier: require.Modifier,
            Span: require.Span);
    }

    private static BoundBindStatement LowerBindStatement(BindStatementSyntax bind)
    {
        var functions = new List<BoundNativeFunctionBinding>(bind.Functions.Count);
        foreach (var fn in bind.Functions)
        {
            var parameters = new List<BoundNativeFunctionParameter>(fn.Parameters.Count);
            foreach (var p in fn.Parameters)
            {
                parameters.Add(new BoundNativeFunctionParameter(p.Name, p.TypeName, p.PassingMode, p.Span));
            }
            functions.Add(new BoundNativeFunctionBinding(
                Name: fn.Name,
                SymbolName: fn.SymbolName,
                Parameters: parameters,
                ReturnTypeName: fn.ReturnTypeName,
                CallingConventionName: fn.CallingConventionName,
                Span: fn.Span));
        }

        return new BoundBindStatement(
            ModuleName: bind.ModuleName,
            NativeTarget: bind.NativeTarget,
            Functions: functions,
            Span: bind.Span);
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
