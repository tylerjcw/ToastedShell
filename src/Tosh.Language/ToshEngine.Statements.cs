using System.Collections;
using Tosh.Runtime;
using Tosh.Language.Debugging;
using Tosh.Language.Parsing;

namespace Tosh.Language;

/// <summary>
/// Statements: dispatching one, the control-flow forms (`if`, `for`, `while`, `until`,
/// `try`, `switch`), block execution, and `defer` unwinding.
///
/// Moved out of ToshEngine.cs by `TOAST-0005`. Every member moved **verbatim**.
///
/// `StatementStreamsOutput` and `CanStreamStatementResults` are here rather than with
/// the pipelines, and it is worth knowing why: they decide whether a statement's values
/// reach the next stage as they are produced or after it finishes. That is a property
/// of the *statement*, and `TS-P1-07` and `TS-P1-45` are both scars from getting it
/// wrong — a function that buffered its whole output, and a bare command statement that
/// was drained before the next stage saw it.
/// </summary>
public sealed partial class ToshEngine
{

    /// <param name="outputIsCaptured">
    /// Whether a consumer is waiting for this statement's value, so an external command must have
    /// its stdout piped rather than inherited (<c>TS-P1-30</c>). Only the pipeline arm can act on
    /// it; every other arm ignores it, which is why it rides here rather than in the pipeline
    /// syntax. Defaulted so the engine's hottest dispatch keeps its existing call shape — the
    /// single caller that sets it is the interpolation hole (<c>TS-P1-32</c>).
    /// </param>
    private IAsyncEnumerable<object?> EvaluateStatementAsync(
        string sourceName,
        string sourceText,
        StatementSyntax statement,
        CancellationToken cancellationToken,
        bool outputIsCaptured = false)
    {
        return statement switch
        {
            ScriptStatementSyntax script => EvaluateScriptStatementAsync(sourceName, sourceText, script, cancellationToken),
            PipelineStatementSyntax pipeline => pipeline.Pipeline.IsBackground
                ? EvaluateBackgroundPipelineAsync(sourceName, sourceText, pipeline, cancellationToken)
                : EvaluatePipelineWithRedirectionAsync(
                    sourceName,
                    sourceText,
                    pipeline.Pipeline,
                    cancellationToken,
                    outputIsCaptured: outputIsCaptured),
            VariableDeclarationStatementSyntax declaration => EvaluateVariableDeclarationAsync(sourceName, sourceText, declaration, cancellationToken),
            ScriptInputStatementSyntax input => EvaluateScriptInputStatementAsync(sourceName, sourceText, input),
            SubcommandStatementSyntax subcommand => EvaluateOrphanSubcommandStatementAsync(sourceName, sourceText, subcommand),
            DestructuringDeclarationStatementSyntax destructuring => EvaluateDestructuringDeclarationAsync(sourceName, sourceText, destructuring, cancellationToken),
            AllocStatementSyntax alloc => EvaluateAllocStatementAsync(sourceName, sourceText, alloc, cancellationToken),
            UsingStatementSyntax @using => EvaluateUsingStatementAsync(sourceName, sourceText, @using, cancellationToken),
            TypeAliasStatementSyntax typeAlias => EvaluateTypeAliasStatementAsync(sourceName, sourceText, typeAlias),
            RequireStatementSyntax require => EvaluateRequireStatementAsync(sourceName, sourceText, require, cancellationToken),
            BindStatementSyntax bind => EvaluateBindStatementAsync(sourceName, sourceText, bind, cancellationToken),
            ReturnStatementSyntax @return => EvaluateReturnStatementAsync(sourceName, sourceText, @return, cancellationToken),
            ThrowStatementSyntax @throw => EvaluateThrowStatementAsync(sourceName, sourceText, @throw, cancellationToken),
            BreakStatementSyntax @break => EvaluateBreakStatementAsync(@break),
            ContinueStatementSyntax @continue => EvaluateContinueStatementAsync(@continue),
            VariableAssignmentStatementSyntax assignment => EvaluateVariableAssignmentAsync(sourceName, sourceText, assignment, cancellationToken),
            MemberAssignmentStatementSyntax assignment => EvaluateMemberAssignmentAsync(sourceName, sourceText, assignment, cancellationToken),
            TupleAssignmentStatementSyntax tupleAssign => EvaluateTupleAssignmentAsync(sourceName, sourceText, tupleAssign, cancellationToken),
            FunctionDefinitionStatementSyntax function => EvaluateFunctionDefinitionAsync(sourceName, sourceText, function, cancellationToken),
            RuneDefinitionStatementSyntax rune => EvaluateRuneDefinitionAsync(sourceName, sourceText, rune, cancellationToken),
            ExtendStatementSyntax extend => EvaluateExtendStatementAsync(sourceName, sourceText, extend, cancellationToken),
            ClassDefinitionStatementSyntax @class => EvaluateClassDefinitionAsync(sourceName, sourceText, @class, cancellationToken),
            InterfaceDefinitionStatementSyntax @interface => EvaluateInterfaceDefinitionAsync(sourceName, sourceText, @interface, cancellationToken),
            UnionDefinitionStatementSyntax union => EvaluateUnionDefinitionAsync(sourceName, sourceText, union, cancellationToken),
            ModuleDefinitionStatementSyntax module => EvaluateModuleDefinitionAsync(sourceName, sourceText, module, cancellationToken),
            EnumDefinitionStatementSyntax @enum => EvaluateEnumDefinitionAsync(sourceName, sourceText, @enum, cancellationToken),
            RecordDefinitionStatementSyntax record => EvaluateRecordDefinitionAsync(sourceName, sourceText, record, cancellationToken),
            StructDefinitionStatementSyntax @struct => EvaluateStructDefinitionAsync(sourceName, sourceText, @struct, cancellationToken),
            RawStructDefinitionStatementSyntax rawStruct => EvaluateRawStructDefinitionAsync(sourceName, sourceText, rawStruct, cancellationToken),
            RawCallbackDefinitionStatementSyntax rawCallback => EvaluateRawCallbackDefinitionAsync(sourceName, sourceText, rawCallback, cancellationToken),
            RawFunctionStatementSyntax rawFunction => EvaluateRawFunctionStatementAsync(sourceName, sourceText, rawFunction, cancellationToken),
            TraitDefinitionStatementSyntax trait => EvaluateTraitDefinitionAsync(sourceName, sourceText, trait, cancellationToken),
            EventDefinitionStatementSyntax @event => EvaluateEventDefinitionAsync(sourceName, sourceText, @event, cancellationToken),
            IfStatementSyntax @if => EvaluateIfStatementAsync(sourceName, sourceText, @if, cancellationToken),
            ForStatementSyntax @for => EvaluateForStatementAsync(sourceName, sourceText, @for, cancellationToken),
            WhileStatementSyntax @while => EvaluateWhileStatementAsync(sourceName, sourceText, @while, cancellationToken),
            UntilStatementSyntax until => EvaluateUntilStatementAsync(sourceName, sourceText, until, cancellationToken),
            TryStatementSyntax @try => EvaluateTryStatementAsync(sourceName, sourceText, @try, cancellationToken),
            SwitchStatementSyntax @switch => EvaluateSwitchStatementAsync(sourceName, sourceText, @switch, cancellationToken),
            DeferStatementSyntax => AsyncEnumerableExtensions.Empty<object?>(),
            _ => throw new InvalidOperationException($"Unsupported statement syntax: {statement.GetType().Name}."),
        };
    }

    /// <summary>
    /// Refuses a destructuring whose target count does not match a <em>tuple</em> source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `TS-P2-59` asked for an arity mismatch to be named rather than absorbed. Applying that to
    /// every source would have contradicted the specification, which documents taking a prefix
    /// of an array on purpose: <c>var [first, second] = $fiveItems</c> is a worked example.
    /// </para>
    /// <para>
    /// The distinction that resolves it is a real one rather than a compromise. An array is a
    /// variable-length collection and reading the first two of it is a meaningful thing to ask
    /// for. A tuple has a fixed, declared shape, so naming two targets for three elements is a
    /// miscount every time — and absorbing it silently is what turns that miscount into a
    /// <c>null</c> reported three lines later.
    /// </para>
    /// </remarks>
    private static void EnsureTupleArityMatches(
        string sourceName,
        string sourceText,
        object? source,
        int valueCount,
        int targetCount,
        TextSpan span)
    {
        if (source is not ToshTuple || valueCount == targetCount)
        {
            return;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.tuple_assignment_arity_mismatch",
            Title: $"This destructuring has {targetCount} targets but the tuple has {valueCount} elements.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: valueCount < targetCount
                ? "there are not enough elements to fill every target"
                : "there are more elements than targets to hold them",
            Help: "give one target per element, or use '_' to discard one you do not want. "
                + "An array may be destructured into fewer targets; a tuple's shape is fixed."));
    }

    /// <summary>
    /// Spreads one value into positional elements, or answers <see langword="null"/> when it is
    /// not a positional shape at all.
    /// </summary>
    /// <remarks>
    /// `TS-P2-59`. This rule existed twice with two different answers. The declaring form,
    /// <c>var [a, b] = …</c>, accepted arrays, lists, tuples and any other enumerable; the
    /// assigning form, <c>(a, b) = …</c>, accepted arrays alone. So <c>var [a, b] = (1, 2)</c>
    /// bound 1 and 2, while <c>(a, b) = (1, 2)</c> bound the whole tuple to <c>a</c> and null to
    /// <c>b</c> — one language, two destructurings, different results, and no diagnostic to say
    /// so. A string is excluded because spreading text into characters is never what a
    /// destructuring meant.
    /// </remarks>
    private static object?[]? TryUnpackPositionalValue(object? value)
    {
        return value switch
        {
            object?[] array => array,
            Array typedArray => Enumerable.Range(0, typedArray.Length).Select(index => typedArray.GetValue(index)).ToArray(),
            IReadOnlyList<object?> list => list.ToArray(),
            IEnumerable enumerable when value is not string => enumerable.Cast<object?>().ToArray(),
            _ => null,
        };
    }

    private async IAsyncEnumerable<object?> EvaluateScriptStatementAsync(
        string sourceName,
        string sourceText,
        ScriptStatementSyntax script,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        PreRegisterTypeDefinitions(sourceName, sourceText, script.Statements);
        PreRegisterRefinementTypeAliases(sourceName, sourceText, script.Statements);

        if (script.Statements.Any(static s => s is SubcommandStatementSyntax))
        {
            await foreach (var value in EvaluateScriptWithSubcommandsAsync(sourceName, sourceText, script, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return value;
            }
            yield break;
        }

        await BindScriptInputsAsync(
            sourceName,
            sourceText,
            script.Statements.OfType<ScriptInputStatementSyntax>().ToArray(),
            script.DocComment,
            cancellationToken);

        // `TS-P2-89`. A top-level `defer` used to dispatch to an empty sequence:
        // it parsed, bound, reported nothing and never ran, which pushed every
        // resource-owning script into wrapping its body in a function it did not
        // otherwise need. The defer machinery lived in `ExecuteBlockAsync`, and the
        // top level runs its own statement loop rather than going through it.
        //
        // Buffering is the cost, and it is why this is gated: cleanup has to run
        // before the values are handed on, so a script that uses `defer` cannot
        // stream. A script that does not is untouched — the same trade
        // `ExecuteBlockAsync` already makes one scope down.
        if (script.Statements.Any(static s => s is DeferStatementSyntax))
        {
            await foreach (var value in EvaluateScriptWithDeferAsync(
                                   sourceName, sourceText, script, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return value;
            }

            yield break;
        }

        foreach (var statement in script.Statements)
        {
            // The top level runs its own statement loop rather than going through
            // ExecuteBlockAsync, so the check that stops a body on `exit` has to be made here
            // too — the first attempt at this fixed only the block executor and a plain script
            // carried on exactly as before.
            if (Host.ExitRequested)
            {
                break;
            }

            if (statement is ScriptInputStatementSyntax)
            {
                continue;
            }

            IReadOnlyList<object?> values = await AsyncEnumerableExtensions.ToListAsync(
                EvaluateStatementAsync(sourceName, sourceText, statement, cancellationToken),
                cancellationToken);

            if (ShouldSuppressStatementResults(statement, values))
            {
                values = Array.Empty<object?>();
            }

            UpdateLastResultIfAny(values);

            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
            }
        }
    }

    /// <summary>
    /// The top-level statement loop for a script that registers a <c>defer</c>.
    /// </summary>
    /// <remarks>
    /// `TS-P2-89`. Mirrors what <see cref="ExecuteBlockAsync"/> does one scope
    /// down, including the reason for buffering: values produced before an exit
    /// stay visible, but they cannot be yielded until cleanup has run, or a script
    /// that exits early would emit its output and then its cleanup out of order.
    ///
    /// <c>exit</c> is why the deferred blocks are gathered as the loop goes rather
    /// than collected up front: the loop stops at <c>LanguageRuntime.ExitRequested</c>, and
    /// only the <c>defer</c> statements actually *reached* by then should run —
    /// registering one that execution never got to would invent cleanup for a
    /// resource that was never acquired.
    /// </remarks>
    private async IAsyncEnumerable<object?> EvaluateScriptWithDeferAsync(
        string sourceName,
        string sourceText,
        ScriptStatementSyntax script,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var deferredBlocks = new List<BlockSyntax>();
        var outputValues = new List<object?>();
        var deferFailures = new ToshDeferFailureState();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? pendingException = null;

        try
        {
            foreach (var statement in script.Statements)
            {
                if (Host.ExitRequested)
                {
                    break;
                }

                if (statement is ScriptInputStatementSyntax)
                {
                    continue;
                }

                if (statement is DeferStatementSyntax deferStatement)
                {
                    deferredBlocks.Add(deferStatement.Body);
                    continue;
                }

                IReadOnlyList<object?> values = await AsyncEnumerableExtensions.ToListAsync(
                    EvaluateStatementAsync(sourceName, sourceText, statement, cancellationToken),
                    cancellationToken);

                if (ShouldSuppressStatementResults(statement, values))
                {
                    values = Array.Empty<object?>();
                }

                UpdateLastResultIfAny(values);
                outputValues.AddRange(values);
            }
        }
        catch (ShellControlFlowException ex)
        {
            pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
        }
        catch (Exception ex)
        {
            pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            ToshDeferFailures.AttachSourceContext(ex, sourceName, sourceText);
            deferFailures.CaptureBodyFailure(ex);
        }

        await RunDeferredBlocksAsync(sourceName, sourceText, deferredBlocks, deferFailures);

        foreach (var value in outputValues)
        {
            yield return value;
        }

        deferFailures.ThrowIfCleanupFailed();
        pendingException?.Throw();
    }

    private async IAsyncEnumerable<object?> EvaluateScriptInputStatementAsync(
        string sourceName,
        string sourceText,
        ScriptInputStatementSyntax statement)
    {
        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.script_inputs_must_be_top_level",
            Title: "Script input declarations must be top-level statements.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: statement.Span,
            Label: "move this input declaration to the top level of the script"));

#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private async IAsyncEnumerable<object?> EvaluateOrphanSubcommandStatementAsync(
        string sourceName,
        string sourceText,
        SubcommandStatementSyntax statement)
    {
        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.subcommand_must_be_script_scoped",
            Title: $"Subcommand '{statement.Name}' must be declared at script or parent-subcommand scope.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: statement.Span,
            Label: "move this subcommand to the top level of the script or inside another subcommand"));

#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private async IAsyncEnumerable<object?> EvaluateAllocStatementAsync(
        string sourceName,
        string sourceText,
        AllocStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, statement.Name, statement.Span, "reserved runtime namespace");

        object? allocationSpecification;

        if (TryGetSimpleAllocationTypeName(statement.Value, out var typeName))
        {
            allocationSpecification = typeName;
        }
        else if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, statement.Value, cancellationToken) is { Matched: true } raw)
        {
            allocationSpecification = raw.Value;
        }
        else
        {
            var values = await AsyncEnumerableExtensions.ToListAsync(
                EvaluatePipelineAsync(sourceName, sourceText, statement.Value, cancellationToken, outputIsCaptured: true),
                cancellationToken);

            if (values.Count != 1)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.alloc_requires_single_value",
                    Title: "Allocated buffer declarations require exactly one size or type value.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: statement.Span,
                    Label: values.Count == 0
                        ? "this allocation expression produced no values"
                        : $"this allocation expression produced {values.Count} values",
                    Help: "use a byte count or a single interop type name here."));
            }

            allocationSpecification = values[0];
        }

        var context = new CommandContext(
            LanguageRuntime,
            AsyncEnumerableExtensions.Empty<object?>(),
            [allocationSpecification],
            cancellationToken,
            ScopedTypeResolver: CreateScopedTypeResolver(),
            BlockExecutor: _ownBlockExecutor,
            ScopedCommands: CreateScopedCommandView(),
            ShellTypes: this);
        var size = NativeCommandUtilities.ResolveAllocationSize(context, allocationSpecification, 0);

        if (size < 0)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.alloc_negative_size",
                Title: "Allocated buffers cannot have a negative size.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: "use zero or a positive size"));
        }

        DeclareVariable(statement.Name, ToVariableBinding(new NativeBuffer(size)), statement.Modifier);
        yield break;
    }

    private static bool TryGetSimpleAllocationTypeName(PipelineSyntax pipeline, out string typeName)
    {
        typeName = string.Empty;
        var redirections = pipeline.Redirections ?? Array.Empty<RedirectionSyntax>();

        if (redirections.Count != 0 || pipeline.IsBackground || pipeline.Stages.Count != 1)
        {
            return false;
        }

        if (pipeline.Stages[0] is CommandSyntax command && command.Arguments.Count == 0)
        {
            typeName = command.Name;
            return true;
        }

        return false;
    }

    private async IAsyncEnumerable<object?> EvaluateUsingStatementAsync(
        string sourceName,
        string sourceText,
        UsingStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (statement.Modifier == DeclarationModifier.Export)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.using_export_not_supported",
                Title: "'using' cannot be exported.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: "use 'global using ...' if you want the import to persist outside the current scope"));
        }

        if (statement.Alias is not null)
        {
            EnsureBindingNameIsNotReserved(sourceName, sourceText, statement.Alias, statement.Span, "reserved runtime namespace");
        }

        if (statement.Modifier == DeclarationModifier.Global || (statement.Modifier == DeclarationModifier.Default && _scopes.Count == 0))
        {
            if (LanguageRuntime.TypeResolver is not IImportingTypeResolver importingResolver)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.using_not_supported",
                    Title: "This runtime does not support 'using' statements.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: statement.Span,
                    Label: "the active type resolver cannot record imports or aliases"));
            }

            if (statement.Alias is null)
            {
                importingResolver.AddUsing(statement.Target);
            }
            else
            {
                importingResolver.AddAlias(statement.Alias, statement.Target);
            }

            yield break;
        }

        if (_scopes.Count == 0)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.shy_using_requires_scope",
                Title: "Shy using statements require a function, block, or module scope.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: "remove 'shy' or place this using inside a scoped block"));
        }

        var scope = _scopes.Peek();

        if (statement.Alias is null)
        {
            scope.TypeImports.Add(statement.Target);
        }
        else
        {
            scope.TypeAliases[statement.Alias] = statement.Target;
        }

        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateTypeAliasStatementAsync(
        string sourceName,
        string sourceText,
        TypeAliasStatementSyntax statement)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, statement.Name, statement.Span, "reserved runtime namespace");
        DeclareRefinementType(
            CreateRefinementTypeDefinition(sourceName, sourceText, statement),
            statement.Modifier,
            sourceName,
            sourceText,
            statement.Span,
            allowTypeNameConflict: false);

        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// Declares a top-level <c>raw func … from "lib"</c> as a command in the
    /// enclosing scope, so it is callable by the name it was given rather than
    /// through a module the caller never asked for.
    /// </summary>
    private async IAsyncEnumerable<object?> EvaluateRawFunctionStatementAsync(
        string sourceName,
        string sourceText,
        RawFunctionStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var target = statement.Binding.NativeTarget;

        if (string.IsNullOrWhiteSpace(target))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.raw_func_requires_library",
                Title: $"Raw function '{statement.Binding.Name}' needs a library.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: "write 'from \"libc.so.6\"' after the signature"));
        }

        // The backing module only exists to own the loaded handle; the command
        // itself is what gets declared.
        var moduleName = $"__raw_{statement.Binding.Name}";
        EnsureNativeModuleAvailable(sourceName, target!, moduleName, DeclarationModifier.Default);

        if (!TryGetModule(moduleName, out var module) || module.NativeLibraryBinding is null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.bind_target_not_native_module",
                Title: $"'{target}' could not be loaded as a native library.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: $"while binding '{statement.Binding.Name}'"));
        }

        DeclareCommand(
            BuildNativeFunctionCommand(
                sourceName, sourceText, statement.Binding.Name, module.NativeLibraryBinding, statement.Binding),
            statement.Modifier);

        await ValueTask.CompletedTask;
        yield break;
    }

    private IAsyncEnumerable<object?> EvaluateReturnStatementAsync(
        string sourceName,
        string sourceText,
        ReturnStatementSyntax statement,
        CancellationToken cancellationToken)
    {
        return EvaluateReturnStatementCoreAsync(sourceName, sourceText, statement, cancellationToken);
    }

    private async IAsyncEnumerable<object?> EvaluateReturnStatementCoreAsync(
        string sourceName,
        string sourceText,
        ReturnStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<object?> values;

        if (statement.Value is null)
        {
            values = Array.Empty<object?>();
        }
        else if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, statement.Value, cancellationToken) is { Matched: true } raw)
        {
            values = [raw.Value];
        }
        else
        {
            values = await AsyncEnumerableExtensions.ToListAsync(
                EvaluatePipelineAsync(sourceName, sourceText, statement.Value, cancellationToken, outputIsCaptured: true),
                cancellationToken);
        }

        throw new ReturnSignalException(statement.Span, values);
#pragma warning disable CS0162 // required by async-iterator contract
        yield break;
#pragma warning restore CS0162
    }

    private IAsyncEnumerable<object?> EvaluateBreakStatementAsync(BreakStatementSyntax statement)
    {
        throw new BreakSignalException(statement.Span);
    }

    private IAsyncEnumerable<object?> EvaluateContinueStatementAsync(ContinueStatementSyntax statement)
    {
        throw new ContinueSignalException(statement.Span);
    }

    private IAsyncEnumerable<object?> EvaluateThrowStatementAsync(
        string sourceName,
        string sourceText,
        ThrowStatementSyntax statement,
        CancellationToken cancellationToken)
    {
        return EvaluateThrowStatementCoreAsync(sourceName, sourceText, statement, cancellationToken);
    }

    private async IAsyncEnumerable<object?> EvaluateThrowStatementCoreAsync(
        string sourceName,
        string sourceText,
        ThrowStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        object? value;

        if (statement.Value is null)
        {
            value = new CommandFailure("An error was thrown.");
        }
        else if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, statement.Value, cancellationToken) is { Matched: true } raw)
        {
            value = raw.Value;
        }
        else
        {
            var values = await AsyncEnumerableExtensions.ToListAsync(
                EvaluatePipelineAsync(sourceName, sourceText, statement.Value, cancellationToken, outputIsCaptured: true),
                cancellationToken);
            value = values.Count switch
            {
                0 => new CommandFailure("An error was thrown."),
                1 => values[0],
                _ => values.ToArray(),
            };
        }

        await RaiseThrownValueAsync(statement.Span, value, cancellationToken);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>
    /// Whether executing <paramref name="statement"/> can produce a <c>yield</c>, and so must be
    /// streamed rather than collected. Answered per syntax node and cached, because the block
    /// executor asks on every statement of every iteration while the answer is fixed at parse
    /// time; the table holds weak references, so a discarded parse tree still collects.
    /// </summary>
    private static bool StatementYields(StatementSyntax statement)
    {
        return YieldingStatements
            .GetValue(statement, static node => new System.Runtime.CompilerServices.StrongBox<bool>(ContainsYieldInStatement(node)))
            .Value;
    }

    private static bool StatementStreamsOutput(StatementSyntax statement)
    {
        // These statements forward a nested block's values and can terminate through a jump or
        // failure after producing some of them. Draining the whole statement to a list first
        // loses those already-produced values when the jump escapes. Their nested blocks maintain
        // the last-result state, and result suppression applies only to pipeline statements, so
        // streaming them changes neither of those contracts.
        return StatementYields(statement) || statement is
            IfStatementSyntax or
            ForStatementSyntax or
            WhileStatementSyntax or
            UntilStatementSyntax or
            TryStatementSyntax or
            SwitchStatementSyntax;
    }

    private static bool ContainsYieldStatement(BlockSyntax block)
    {
        foreach (var statement in block.Statements)
        {
            if (statement is YieldStatementSyntax)
                return true;

            // Check nested blocks (if, for, while, try, etc.)
            if (ContainsYieldInStatement(statement))
                return true;
        }

        return false;
    }

    private static bool ContainsYieldInStatement(StatementSyntax statement)
    {
        return statement switch
        {
            IfStatementSyntax ifStmt =>
                ContainsYieldStatement(ifStmt.ThenBlock) ||
                (ifStmt.ElseBlock is not null && ContainsYieldStatement(ifStmt.ElseBlock)),
            ForStatementSyntax forStmt => ContainsYieldStatement(forStmt.Body),
            WhileStatementSyntax whileStmt => ContainsYieldStatement(whileStmt.Body),
            // `until` is `while` with the condition negated, and it was the one loop missing
            // here. The omission cost twice over: a function whose only yield sat in an `until`
            // was not recognised as a generator, and the loop was collected rather than
            // streamed, so an endless one never returned.
            UntilStatementSyntax untilStmt => ContainsYieldStatement(untilStmt.Body),
            TryStatementSyntax tryStmt =>
                ContainsYieldStatement(tryStmt.TryBlock) ||
                (tryStmt.CatchClause is not null && ContainsYieldStatement(tryStmt.CatchClause.Body)) ||
                (tryStmt.FinallyBlock is not null && ContainsYieldStatement(tryStmt.FinallyBlock)),
            SwitchStatementSyntax switchStmt =>
                switchStmt.Cases.Any(c => ContainsYieldStatement(c.Body)) ||
                (switchStmt.DefaultBlock is not null && ContainsYieldStatement(switchStmt.DefaultBlock)),
            _ => false,
        };
    }

    private async IAsyncEnumerable<object?> EvaluateExtendStatementAsync(
        string sourceName,
        string sourceText,
        ExtendStatementSyntax extend,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // `TOAST-0016`. Registered under every name the receiver can answer to, not just
        // the one that was written. Lookup offers a value's CLR names — `Int32`,
        // `System.Int32` — and never the shell alias, so `extend int` was accepted, stored,
        // and then matched nothing: a declaration that failed silently at the point of
        // *use*, in a place that looked unrelated.
        var registrationKeys = new List<string> { extend.TypeName };

        if (ResolveTypeName(extend.TypeName) is { } resolvedClrType)
        {
            registrationKeys.Add(resolvedClrType.Name);

            if (resolvedClrType.FullName is { } fullName)
            {
                registrationKeys.Add(fullName);
            }
        }

        Dictionary<string, FunctionDefinition>? methods = null;

        foreach (var key in registrationKeys)
        {
            if (_extensionMethods.TryGetValue(key, out var existing))
            {
                // An alias and its CLR name are the same type, so `extend int` and
                // `extend Int32` must add to one table rather than two that shadow.
                methods ??= existing;
                continue;
            }

            methods ??= new Dictionary<string, FunctionDefinition>(StringComparer.OrdinalIgnoreCase);
            _extensionMethods[key] = methods;
        }

        methods ??= new Dictionary<string, FunctionDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in registrationKeys)
        {
            _extensionMethods[key] = methods;
        }

        foreach (var member in extend.Members)
        {
            if (member is not ClassMethodMemberSyntax method)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.extend_member_not_a_method",
                    Title: $"'extend {extend.TypeName}' can only declare methods.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: member.Span,
                    Label: "only 'func' members are allowed here",
                    Help: "an extension has nowhere to put state, so it cannot add properties or fields."));
            }

            methods[method.Method.Name] = CreateFunctionDefinition(
                method.Method.Name,
                method.Method.Parameters,
                method.Method.ReturnTypeName,
                method.Method.Body,
                isCommandWrapper: false,
                sourceName,
                sourceText,
                method.Span,
                method.Method.DocComment);
        }

        yield break;
    }

    /// <summary>
    /// Finds and runs an <c>extend</c> method for a receiver that has none of its own.
    /// </summary>
    /// <remarks>
    /// Matched on the receiver's shell type name and its CLR name, so `extend Color`
    /// reaches `System.Drawing.Color` and `extend Point` reaches a ToastScript class
    /// alike. Reached only after ordinary member lookup has failed, so a real member
    /// always wins.
    /// </remarks>
    internal async ValueTask<InvocationResult?> TryInvokeExtensionAsync(
        object receiver,
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        if (_extensionMethods.Count == 0)
        {
            return null;
        }

        FunctionDefinition? definition = null;

        foreach (var name in EnumerateReceiverTypeNames(receiver))
        {
            if (_extensionMethods.TryGetValue(name, out var methods) &&
                methods.TryGetValue(methodName, out definition))
            {
                break;
            }
        }

        if (definition is null)
        {
            return null;
        }

        var context = new CommandContext(
            LanguageRuntime,
            AsyncEnumerableExtensions.Empty<object?>(),
            arguments,
            cancellationToken,
            ScopedTypeResolver: CreateScopedTypeResolver(),
            BlockExecutor: _ownBlockExecutor,
            ScopedCommands: CreateScopedCommandView(),
            ShellTypes: this);

        // `$this` is the receiver itself rather than a self-reference: an extension
        // adds behaviour to a value, and has no instance state of its own to reach.
        using var scope = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["this"] = receiver,
        });

        var results = await AsyncEnumerableExtensions.ToListAsync(
            ExecuteFunctionAsync(definition, context),
            cancellationToken);

        return results.Count switch
        {
            0 => new InvocationResult(null, ReturnedVoid: true),
            1 => new InvocationResult(results[0], ReturnedVoid: false),
            _ => new InvocationResult(results, ReturnedVoid: false),
        };
    }

    private async IAsyncEnumerable<object?> EvaluateIfStatementAsync(
        string sourceName,
        string sourceText,
        IfStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var condition = await EvaluateConditionAsync(sourceName, sourceText, statement.Condition, cancellationToken);
        var block = condition ? statement.ThenBlock : statement.ElseBlock;

        if (block is null)
        {
            yield break;
        }

        await foreach (var value in ExecuteBlockAsync(sourceName, sourceText, block, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return value;
        }
    }

    private async IAsyncEnumerable<object?> EvaluateForStatementAsync(
        string sourceName,
        string sourceText,
        ForStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, statement.VariableName, statement.Span, "reserved runtime namespace");

        // `for line in curl -s … { … }` consumes the values, so the child's stdout must be
        // captured even at a terminal. The loop still streams — the flag changes how the
        // process is spawned, not how values flow (TS-P1-30).
        var source = EvaluatePipelineAsync(
            sourceName, sourceText, statement.Source, cancellationToken,
            outputIsCaptured: true);

        // `TS-P2-113`. A stream whose producer already enumerated a collection into
        // it carries its items directly; expanding each one again is the second
        // expansion that made `for x in $r` bind three integers where the identical
        // `for x in [[1, 2, 3]]` bound one array.
        var alreadyExpanded = source is PreExpandedSequence;

        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            await foreach (var current in (alreadyExpanded
                               ? SingleItemAsync(item)
                               : ShellIterationUtilities.ExpandIterationItemsAsync(item, cancellationToken))
                               .WithCancellation(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                System.Runtime.ExceptionServices.ExceptionDispatchInfo? pendingControlFlow = null;
                await using (var bodyEnumerator = ExecuteBlockAsync(
                                 sourceName,
                                 sourceText,
                                 statement.Body,
                                 cancellationToken,
                                 new Dictionary<string, object?>(StringComparer.Ordinal)
                                 {
                                     [statement.VariableName] = current,
                                     ["_"] = current,
                                 })
                                 .GetAsyncEnumerator(cancellationToken))
                {
                    while (true)
                    {
                        var move = await MoveNextCapturingFailureAsync(bodyEnumerator);
                        if (move.Failure is not null)
                        {
                            if (move.Failure.SourceException is ShellControlFlowException)
                            {
                                pendingControlFlow = move.Failure;
                                break;
                            }

                            move.Failure.Throw();
                        }

                        if (!move.HasValue)
                            break;

                        yield return move.Value;
                    }
                }

                if (pendingControlFlow?.SourceException is ReturnSignalException)
                {
                    pendingControlFlow.Throw();
                    yield break;
                }

                if (pendingControlFlow?.SourceException is BreakSignalException)
                {
                    yield break;
                }

                if (pendingControlFlow?.SourceException is ContinueSignalException)
                {
                    continue;
                }

                pendingControlFlow?.Throw();
            }
        }
    }

    private async IAsyncEnumerable<object?> EvaluateWhileStatementAsync(
        string sourceName,
        string sourceText,
        WhileStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await EvaluateConditionAsync(sourceName, sourceText, statement.Condition, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            System.Runtime.ExceptionServices.ExceptionDispatchInfo? pendingControlFlow = null;
            await using (var bodyEnumerator = ExecuteBlockAsync(
                             sourceName,
                             sourceText,
                             statement.Body,
                             cancellationToken)
                             .GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    var move = await MoveNextCapturingFailureAsync(bodyEnumerator);
                    if (move.Failure is not null)
                    {
                        if (move.Failure.SourceException is ShellControlFlowException)
                        {
                            pendingControlFlow = move.Failure;
                            break;
                        }

                        move.Failure.Throw();
                    }

                    if (!move.HasValue)
                        break;

                    yield return move.Value;
                }
            }

            if (pendingControlFlow?.SourceException is ReturnSignalException)
            {
                pendingControlFlow.Throw();
                yield break;
            }

            if (pendingControlFlow?.SourceException is BreakSignalException)
            {
                yield break;
            }

            if (pendingControlFlow?.SourceException is ContinueSignalException)
            {
                continue;
            }

            pendingControlFlow?.Throw();
        }
    }

    private async IAsyncEnumerable<object?> EvaluateUntilStatementAsync(
        string sourceName,
        string sourceText,
        UntilStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!await EvaluateConditionAsync(sourceName, sourceText, statement.Condition, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            System.Runtime.ExceptionServices.ExceptionDispatchInfo? pendingControlFlow = null;
            await using (var bodyEnumerator = ExecuteBlockAsync(
                             sourceName,
                             sourceText,
                             statement.Body,
                             cancellationToken)
                             .GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    var move = await MoveNextCapturingFailureAsync(bodyEnumerator);
                    if (move.Failure is not null)
                    {
                        if (move.Failure.SourceException is ShellControlFlowException)
                        {
                            pendingControlFlow = move.Failure;
                            break;
                        }

                        move.Failure.Throw();
                    }

                    if (!move.HasValue)
                        break;

                    yield return move.Value;
                }
            }

            if (pendingControlFlow?.SourceException is ReturnSignalException)
            {
                pendingControlFlow.Throw();
                yield break;
            }

            if (pendingControlFlow?.SourceException is BreakSignalException)
            {
                yield break;
            }

            if (pendingControlFlow?.SourceException is ContinueSignalException)
            {
                continue;
            }

            pendingControlFlow?.Throw();
        }
    }

    private async IAsyncEnumerable<object?> EvaluateTryStatementAsync(
        string sourceName,
        string sourceText,
        TryStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? pendingFailure = null;
        Exception? caughtException = null;
        var bodyCompleted = false;
        try
        {
            await using (var tryEnumerator = ExecuteBlockAsync(
                             sourceName,
                             sourceText,
                             statement.TryBlock,
                             cancellationToken)
                             .GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    var move = await MoveNextCapturingFailureAsync(tryEnumerator);
                    if (move.Failure is not null)
                    {
                        if (move.Failure.SourceException is ShellControlFlowException ||
                            statement.CatchClause is null)
                        {
                            pendingFailure = move.Failure;
                        }
                        else
                        {
                            caughtException = move.Failure.SourceException;
                        }

                        break;
                    }

                    if (!move.HasValue)
                        break;

                    yield return move.Value;
                }
            }

            if (caughtException is not null)
            {
                var catchClause = statement.CatchClause!;
                var catchLocals = new Dictionary<string, object?>(StringComparer.Ordinal);

                if (!string.IsNullOrWhiteSpace(catchClause.VariableName))
                {
                    EnsureBindingNameIsNotReserved(
                        sourceName,
                        sourceText,
                        catchClause.VariableName!,
                        catchClause.Span,
                        "reserved runtime namespace");
                    catchLocals[catchClause.VariableName!] = CreateCaughtErrorValue(caughtException);
                }

                await using var catchEnumerator = ExecuteBlockAsync(
                        sourceName,
                        sourceText,
                        catchClause.Body,
                        cancellationToken,
                        catchLocals)
                    .GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    var move = await MoveNextCapturingFailureAsync(catchEnumerator);
                    if (move.Failure is not null)
                    {
                        pendingFailure = move.Failure;
                        break;
                    }

                    if (!move.HasValue)
                        break;

                    yield return move.Value;
                }
            }

            bodyCompleted = true;
        }
        finally
        {
            if (!bodyCompleted && statement.FinallyBlock is not null)
            {
                // A downstream short-circuit disposes this iterator at the active yield. The
                // source-level finally must still run to completion, although its values have no
                // remaining consumer and are therefore discarded on this disposal path.
                await foreach (var _ in ExecuteBlockAsync(
                                   sourceName,
                                   sourceText,
                                   statement.FinallyBlock,
                                   cancellationToken)
                                   .WithCancellation(cancellationToken))
                {
                }
            }
        }

        if (statement.FinallyBlock is not null)
        {
            // Finish cleanup before exposing its values. Besides matching the established
            // finally ordering, this ensures a downstream short-circuit cannot abandon the
            // remaining cleanup statements after taking the first finally value.
            var finallyValues = await AsyncEnumerableExtensions.ToListAsync(
                ExecuteBlockAsync(
                    sourceName,
                    sourceText,
                    statement.FinallyBlock,
                    cancellationToken),
                cancellationToken);
            foreach (var value in finallyValues)
            {
                yield return value;
            }
        }

        pendingFailure?.Throw();
    }

    private async IAsyncEnumerable<object?> EvaluateSwitchStatementAsync(
        string sourceName,
        string sourceText,
        SwitchStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var switchValue = await EvaluateArgumentAsync(sourceName, sourceText, statement.Value, cancellationToken);
        BlockSyntax? blockToExecute = null;

        foreach (var @case in statement.Cases)
        {
            if (!await MatchesPatternAsync(switchValue, sourceName, sourceText, @case.MatchExpression, cancellationToken))
            {
                continue;
            }

            if (@case.Guard is not null)
            {
                if (!await EvaluateGuardWithCurrentItemAsync(sourceName, sourceText, @case.Guard, switchValue, cancellationToken))
                {
                    continue;
                }
            }

            blockToExecute = @case.Body;
            break;
        }

        blockToExecute ??= statement.DefaultBlock;

        if (blockToExecute is null)
        {
            yield break;
        }

        await foreach (var value in ExecuteBlockAsync(sourceName, sourceText, blockToExecute, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return value;
        }
    }

    /// <summary>An arm that matched, and the names its pattern bound — <c>TOAST-0053</c>.</summary>
    private readonly record struct MatchArmSelection(
        MatchArmSyntax Arm,
        IReadOnlyDictionary<string, object?>? Bindings);

    private async Task<MatchArmSelection> ResolveMatchArmAsync(
        string sourceName,
        string sourceText,
        MatchArgumentSyntax match,
        CancellationToken cancellationToken)
    {
        var value = await EvaluateArgumentAsync(sourceName, sourceText, match.Value, cancellationToken);

        foreach (var arm in match.Arms)
        {
            var matched = arm.IsWildcard;

            if (!matched && arm.Pattern is not null)
            {
                matched = await MatchesPatternAsync(value, sourceName, sourceText, arm.Pattern, cancellationToken);
            }

            if (!matched)
            {
                continue;
            }

            // `TOAST-0053`. The guard sees the bindings: `Add(l, r) if ($l is Lit)` is the
            // shape the item was filed for, and a guard that cannot read what the pattern
            // bound would make it useless.
            var bindings = arm.Pattern is VariantPatternSyntax or ListPatternSyntax
                ? BindPatternNames(arm.Pattern, value)
                : null;

            if (arm.Guard is not null)
            {
                using var guardScope = bindings is null ? null : PushScope(bindings);

                if (!await EvaluateGuardWithCurrentItemAsync(sourceName, sourceText, arm.Guard, value, cancellationToken))
                {
                    continue;
                }
            }

            return new MatchArmSelection(arm, bindings);
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.non_exhaustive_match",
            Title: "This match expression did not match any arm.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: match.Span,
            Label: "add a matching arm or a fallback arm like `default => ...`",
            Help: "match expressions should usually end with a `default => ...` arm to cover unmatched values."));
    }

    private async Task<object?> EvaluateMatchArmValueAsync(
        string sourceName,
        string sourceText,
        MatchArmSelection selection,
        CancellationToken cancellationToken)
    {
        // `TOAST-0053`. Scoped to the arm: a name a pattern bound is gone once the arm is
        // done, which is what makes two arms free to bind the same name for different things.
        using var armScope = selection.Bindings is null ? null : PushScope(selection.Bindings);

        var values = await AsyncEnumerableExtensions.ToListAsync(
            ExecuteMatchArmAsync(sourceName, sourceText, selection.Arm, cancellationToken),
            cancellationToken);

        return values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => values.ToArray(),
        };
    }

    /// <summary>
    /// Binds a variant's fields to the pattern's names, positionally — <c>TOAST-0053</c>.
    /// </summary>
    /// <remarks>
    /// Declaration order, because that is the order the variant was written in and the order
    /// its constructor takes. `_` discards a position rather than binding it, so a pattern can
    /// reach the third field without naming the first two.
    /// </remarks>
    private static Dictionary<string, object?> BindPatternNames(
        ArgumentSyntax pattern,
        object? value)
    {
        var bindings = new Dictionary<string, object?>(StringComparer.Ordinal);
        BindSubPattern(pattern, value, bindings);
        return bindings;
    }

    /// <summary>
    /// Gathers every name a pattern binds, to any depth — <c>TOAST-0053</c>.
    /// </summary>
    /// <remarks>
    /// Runs after the pattern has already matched, so it can assume the shape fits and simply
    /// walk it. A nested pattern contributes its own bindings to the same flat set: the arm
    /// sees `Some(Point(x, y))`'s `x` and `y` as ordinary names, which is the point of writing
    /// it that way rather than reaching through two member accesses.
    /// </remarks>
    private static void CollectVariantBindings(
        VariantPatternSyntax pattern,
        object? instance,
        Dictionary<string, object?> bindings)
    {
        if (!TryDescribePatternSubject(instance, out var subject)) { return; }

        for (var index = 0; index < pattern.Positional.Count && index < subject.Positional.Count; index++)
        {
            TryReadPatternMember(instance, subject.Positional[index], out var value);
            BindSubPattern(pattern.Positional[index], value, bindings);
        }

        foreach (var named in pattern.Named)
        {
            TryReadPatternMember(instance, named.Field, out var value);
            BindSubPattern(named.Pattern, value, bindings);
        }
    }

    /// <summary>
    /// Gathers what a list pattern binds — its elements, and the rest — <c>TOAST-0053</c>.
    /// </summary>
    /// <remarks>
    /// Runs after the pattern matched, so the lengths are known to fit. The rest is bound as an
    /// array even when it is empty, so an arm can pass it on without checking for null; an
    /// anonymous `...` skips the middle without naming it.
    /// </remarks>
    private static void CollectListBindings(
        ListPatternSyntax pattern,
        object? value,
        Dictionary<string, object?> bindings)
    {
        if (!TryReadPatternSequence(value, out var items)) { return; }

        for (var index = 0; index < pattern.Before.Count && index < items.Count; index++)
        {
            BindSubPattern(pattern.Before[index], items[index], bindings);
        }

        for (var index = 0; index < pattern.After.Count; index++)
        {
            var position = items.Count - pattern.After.Count + index;
            if (position < 0) { continue; }
            BindSubPattern(pattern.After[index], items[position], bindings);
        }

        if (!pattern.HasRest || pattern.RestName.Length == 0) { return; }

        var restStart = pattern.Before.Count;
        var restLength = Math.Max(0, items.Count - pattern.After.Count - restStart);

        // An array rather than a list, because `[1, 2, 3]` is an array: binding the rest as a
        // `List` would answer `.Count` where the literal it came from answers `.Length`, so
        // the same value would need a different spelling depending on where it came from.
        var rest = new object?[restLength];

        for (var index = 0; index < restLength; index++)
        {
            rest[index] = items[restStart + index];
        }

        bindings[pattern.RestName] = rest;
    }

    /// <summary>Binds one sub-pattern: a name takes the value, a nested pattern recurses.</summary>
    private static void BindSubPattern(
        ArgumentSyntax pattern,
        object? value,
        Dictionary<string, object?> bindings)
    {
        switch (pattern)
        {
            case BarewordArgumentSyntax { Value: var name } when !string.Equals(name, "_", StringComparison.Ordinal):
                bindings[name] = value;
                break;

            case VariantPatternSyntax nested:
                CollectVariantBindings(nested, value, bindings);
                break;

            case ListPatternSyntax list:
                CollectListBindings(list, value, bindings);
                break;
        }
    }

    /// <summary>
    /// What a destructuring pattern can match against — <c>TOAST-0053</c>.
    /// </summary>
    /// <param name="TypeName">The name the pattern has to spell to match this value.</param>
    /// <param name="Positional">Declared fields in order, empty when the shape has no order.</param>
    /// <param name="Named">Every field a pattern may name, including inherited ones.</param>
    /// <param name="Kind">"variant", "record", "struct" or "class", for diagnostics.</param>
    /// <remarks>
    /// A union variant, a record, a struct and a class all answer the same three questions, so
    /// the matcher asks them here rather than switching on the instance type in four places.
    /// A class has an empty <paramref name="Positional"/> deliberately: its properties may be
    /// inherited, reordered or added without changing what the class means, so there is no
    /// order a positional pattern could rely on. Naming the fields is the only safe spelling,
    /// and the matcher says so rather than binding against an order that is not a contract.
    /// </remarks>
    internal readonly record struct PatternSubject(
        string TypeName,
        IReadOnlyList<string> Positional,
        IReadOnlyList<string> Named,
        string Kind);

    /// <summary>Describes a value a pattern may destructure, or fails for anything else.</summary>
    internal static bool TryDescribePatternSubject(object? value, out PatternSubject subject)
    {
        switch (value)
        {
            case ToshUnionVariantInstance variant:
                {
                    var fields = VariantFieldNames(variant);
                    subject = new PatternSubject(variant.VariantName, fields, fields, "variant");
                    return true;
                }

            case ToshRecordInstance record:
                {
                    var fields = record.Definition.Fields.Select(field => field.Name).ToArray();
                    subject = new PatternSubject(record.Definition.Name, fields, fields, "record");
                    return true;
                }

            case ToshStructInstance structure:
                {
                    var fields = structure.Definition.Fields.Select(field => field.Name).ToArray();
                    var named = fields
                        .Concat(structure.Definition.Properties.Select(property => property.Name))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    subject = new PatternSubject(structure.Definition.Name, fields, named, "struct");
                    return true;
                }

            case ToshClassInstance instance:
                {
                    var named = new List<string>();
                    for (var definition = instance.Definition; definition is not null; definition = definition.BaseClass)
                    {
                        foreach (var property in definition.Properties)
                        {
                            if (!named.Contains(property.Name, StringComparer.Ordinal))
                            {
                                named.Add(property.Name);
                            }
                        }
                    }

                    subject = new PatternSubject(
                        instance.Definition.Name, Array.Empty<string>(), named, "class");
                    return true;
                }

            default:
                subject = default;
                return false;
        }
    }

    /// <summary>
    /// Reads a value as a sequence a list pattern can walk, or refuses — <c>TOAST-0053</c>.
    /// </summary>
    /// <remarks>
    /// A string is deliberately not one. .NET makes it an <c>IEnumerable&lt;char&gt;</c>, so
    /// without this check `[a, b]` would match "hi" and bind two characters.
    /// </remarks>
    internal static bool TryReadPatternSequence(object? value, out IReadOnlyList<object?> items)
    {
        switch (value)
        {
            case null or string:
                items = Array.Empty<object?>();
                return false;

            case IReadOnlyList<object?> list:
                items = list;
                return true;

            case System.Collections.IEnumerable sequence:
                var collected = new List<object?>();
                foreach (var item in sequence) { collected.Add(item); }
                items = collected;
                return true;

            default:
                items = Array.Empty<object?>();
                return false;
        }
    }

    /// <summary>Reads one member from any value a pattern can destructure.</summary>
    internal static bool TryReadPatternMember(object? value, string name, out object? member)
    {
        switch (value)
        {
            case ToshUnionVariantInstance variant: return variant.TryGetMember(name, out member);
            case ToshRecordInstance record: return record.TryGetMember(name, out member);
            case ToshStructInstance structure: return structure.TryGetMember(name, out member);
            case ToshClassInstance instance: return instance.TryGetMember(name, out member);
            default: member = null; return false;
        }
    }

    /// <summary>The variant's declared fields, in declaration order — <c>TOAST-0053</c>.</summary>
    /// <remarks>
    /// Read from the declaration rather than from <c>GetMembers</c>, which prepends the
    /// <c>Variant</c> tag: binding against that list made `Ok(v)` bind `v` to the string
    /// "Ok", and every pattern was one place out without ever failing to match.
    /// </remarks>
    internal static IReadOnlyList<string> VariantFieldNames(ToshUnionVariantInstance instance)
    {
        foreach (var variant in instance.UnionDefinition.Variants)
        {
            if (string.Equals(variant.Name, instance.VariantName, StringComparison.Ordinal))
            {
                return variant.FieldNames;
            }
        }

        return Array.Empty<string>();
    }

    private async IAsyncEnumerable<object?> ExecuteMatchArmAsync(
        string sourceName,
        string sourceText,
        MatchArmSyntax arm,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        switch (arm.Body)
        {
            case MatchArmBlockBodySyntax blockBody:
                await foreach (var value in ExecuteBlockAsync(sourceName, sourceText, blockBody.Block, cancellationToken)
                                   .WithCancellation(cancellationToken))
                {
                    yield return value;
                }
                yield break;

            case MatchArmPipelineBodySyntax pipelineBody:
                await foreach (var value in EvaluatePipelineWithRedirectionAsync(sourceName, sourceText, pipelineBody.Pipeline, cancellationToken)
                                   .WithCancellation(cancellationToken))
                {
                    yield return value;
                }
                yield break;

            default:
                throw new InvalidOperationException($"Unsupported match arm body syntax: {arm.Body.GetType().Name}.");
        }
    }

    private void UpdateLastResultIfAny(IReadOnlyList<object?> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        LanguageRuntime.ExecutionObserver.SetLastResult(
            values.Count == 1 ? values[0] : values.ToArray());
    }

    /// <summary>
    /// True when a statement's results can be streamed rather than drained
    /// (<c>TS-P1-45</c>).
    /// </summary>
    /// <remarks>
    /// The one thing draining is still required for is <em>suppression</em>, which needs
    /// every value before it can decide to emit none. It applies only to the shape
    /// <see cref="ShouldSuppressStatementResults"/> tests for — a single-stage expression
    /// pipeline with no redirections — so every other statement is free to stream.
    /// Deliberately written against the same shape rather than a second description of
    /// it, because two descriptions of one rule is how this codebase's drift starts.
    /// </remarks>
    private static bool CanStreamStatementResults(StatementSyntax statement) =>
        statement is not PipelineStatementSyntax
        {
            Pipeline:
            {
                Stages.Count: 1,
                Redirections: null or { Count: 0 },
                Stages: [ExpressionPipelineStageSyntax]
            }
        };

    private static bool ShouldSuppressStatementResults(StatementSyntax statement, IReadOnlyList<object?> values)
    {
        if (values.Count == 0 || values.Any(value => value is not null))
        {
            return false;
        }

        return statement is PipelineStatementSyntax
        {
            Pipeline:
            {
                Stages.Count: 1,
                Redirections: null or { Count: 0 },
                Stages: [ExpressionPipelineStageSyntax]
            }
        };
    }

    private async IAsyncEnumerable<object?> ExecuteBlockAsync(
        string sourceName,
        string sourceText,
        BlockSyntax block,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? locals = null,
        IAsyncEnumerable<object?>? initialInput = null,
        IReadOnlyList<object?>? firstCommandArguments = null,
        bool pushNewScope = true)
    {
        using var _ = pushNewScope
            ? PushScope(locals ?? new Dictionary<string, object?>(StringComparer.Ordinal))
            : ScopeFrames.Empty;
        var pendingInput = initialInput;
        var pendingFirstCommandArguments = firstCommandArguments;

        var hasDeferStatements = false;

        foreach (var statement in block.Statements)
        {
            if (statement is DeferStatementSyntax)
            {
                hasDeferStatements = true;
                break;
            }
        }

        if (hasDeferStatements)
        {
            var deferredBlocks = new List<BlockSyntax>();
            var outputValues = new List<object?>();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? pendingException = null;
            var deferFailures = new ToshDeferFailureState();

            try
            {
                await foreach (var value in ExecuteBlockStatementsAsync(
                    sourceName, sourceText, block, cancellationToken,
                    pendingInput, pendingFirstCommandArguments, deferredBlocks)
                    .WithCancellation(cancellationToken))
                {
                    outputValues.Add(value);
                }
            }
            catch (ShellControlFlowException ex)
            {
                // Return/break/continue are pending exits, not body failures.
                // A cleanup failure still supersedes the pending jump below.
                pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
            catch (Exception ex)
            {
                pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                ToshDeferFailures.AttachSourceContext(ex, sourceName, sourceText);
                deferFailures.CaptureBodyFailure(ex);
            }

            await RunDeferredBlocksAsync(sourceName, sourceText, deferredBlocks, deferFailures);

            // Match the ordinary streaming path: values produced before an
            // exit remain visible, even though defer requires buffering them
            // until cleanup has run.
            foreach (var value in outputValues)
            {
                yield return value;
            }

            // Cleanup failures supersede a pending return/break/continue. If
            // there is also a body failure, the helper throws the documented
            // ordered aggregate (or preserves cancellation as cancellation).
            deferFailures.ThrowIfCleanupFailed();
            pendingException?.Throw();
        }
        else
        {
            await foreach (var value in ExecuteBlockStatementsAsync(
                sourceName, sourceText, block, cancellationToken,
                pendingInput, pendingFirstCommandArguments, deferredBlocks: null)
                .WithCancellation(cancellationToken))
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// Runs a scope's deferred blocks, last registered first.
    /// </summary>
    /// <remarks>
    /// Shared by block scopes and by the top-level script scope (`TS-P2-89`),
    /// because every rule here is one somebody would otherwise have to reproduce:
    /// cleanup is shielded from the token that ended the enclosing scope so each
    /// reached <c>defer</c> gets its one chance to run; a control-flow signal from
    /// inside a deferred block is suppressed rather than allowed to redirect the
    /// exit; and a failing cleanup does not stop the earlier ones, with the
    /// original exceptions kept in LIFO order for the aggregate.
    /// </remarks>
    private async Task RunDeferredBlocksAsync(
        string sourceName,
        string sourceText,
        List<BlockSyntax> deferredBlocks,
        ToshDeferFailureState deferFailures)
    {
        _deferredCleanupDepth++;

        try
        {
        for (var i = deferredBlocks.Count - 1; i >= 0; i--)
        {
            try
            {
                await foreach (var value in ExecuteBlockAsync(
                    sourceName, sourceText, deferredBlocks[i], CancellationToken.None)
                    .WithCancellation(CancellationToken.None))
                {
                    // Deferred blocks execute for side effects only; output is discarded.
                }
            }
            catch (ShellControlFlowException)
            {
            }
            catch (Exception cleanupFailure)
            {
                ToshDeferFailures.AttachSourceContext(cleanupFailure, sourceName, sourceText);
                deferFailures.CaptureCleanupFailure(cleanupFailure);
            }
        }
        }
        finally
        {
            _deferredCleanupDepth--;
        }
    }

    private async IAsyncEnumerable<object?> ExecuteBlockStatementsAsync(
        string sourceName,
        string sourceText,
        BlockSyntax block,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        IAsyncEnumerable<object?>? pendingInput,
        IReadOnlyList<object?>? pendingFirstCommandArguments,
        List<BlockSyntax>? deferredBlocks)
    {
        foreach (var statement in block.Statements)
        {
            // `exit` stops the work, not just the session. It recorded an exit code and set a
            // flag that only the REPL loop ever read, so a script ran on regardless: `echo one`,
            // `exit 0`, `echo two` printed both lines, and any script using `exit` for an early
            // return silently carried on doing what it meant to skip.
            //
            // Checked here because every body — a script, a function, a loop, a branch — runs
            // through this loop, so one check ends them all rather than each remembering to.
            //
            // `TS-P2-115`. The exemption for deferred blocks was stated here and not
            // implemented: cleanup runs through this same loop, with `ExitRequested`
            // still true, so the guard stopped each deferred block at its first
            // statement and every `defer` was a no-op on the way out. A lock stayed
            // held, a temp directory stayed behind, a terminal mode stayed changed —
            // on the one exit route where cleanup matters most, and silently, because
            // `throw` unwinds correctly and is the path people test.
            //
            // Shielded by depth for the same reason cleanup is shielded from the
            // cancellation token: the event that ended the scope must not also
            // prevent the scope from tidying up after itself.
            if (Host.ExitRequested && _deferredCleanupDepth == 0)
            {
                break;
            }

            if (statement is DeferStatementSyntax deferStatement)
            {
                deferredBlocks?.Add(deferStatement.Body);
                continue;
            }

            // Debug hook / script trace: fire before each statement executes.
            if (DebugHook is not null || LanguageRuntime.Options.ScriptTrace)
            {
                var action = await InvokeDebugHookAsync(sourceName, sourceText, statement, cancellationToken);
                if (action == DebugAction.Abort)
                {
                    throw new DebugAbortException { Span = statement.Span };
                }
            }

            if (statement is ReturnStatementSyntax returnStatement)
            {
                IReadOnlyList<object?> returnValues;

                if (returnStatement.Value is null)
                {
                    returnValues = Array.Empty<object?>();
                }
                else if (returnStatement.Value.Stages.Count == 1 &&
                         returnStatement.Value.Stages[0] is ExpressionPipelineStageSyntax expressionStage)
                {
                    returnValues = [await EvaluateArgumentAsync(sourceName, sourceText, expressionStage.Expression, cancellationToken)];
                }
                else
                {
                    returnValues = await AsyncEnumerableExtensions.ToListAsync(
                        EvaluatePipelineAsync(sourceName, sourceText, returnStatement.Value, cancellationToken, pendingInput, outputIsCaptured: true),
                        cancellationToken);
                }

                UpdateLastResultIfAny(returnValues);
                throw new ReturnSignalException(returnStatement.Span, returnValues);
            }

            if (statement is YieldStatementSyntax yieldStatement)
            {
                if (yieldStatement.Value is not null)
                {
                    await foreach (var yieldValue in EvaluatePipelineAsync(
                        sourceName, sourceText, yieldStatement.Value, cancellationToken, pendingInput)
                        .WithCancellation(cancellationToken))
                    {
                        yield return yieldValue;
                    }
                }

                pendingInput = null;
                continue;
            }

            if (statement is BreakStatementSyntax breakStatement)
            {
                throw new BreakSignalException(breakStatement.Span);
            }

            if (statement is ContinueStatementSyntax continueStatement)
            {
                throw new ContinueSignalException(continueStatement.Span);
            }

            // Fast path: variable assignments never produce output. Run via a Task-returning
            // method to avoid the IAsyncEnumerable state machine + ToListAsync overhead.
            if (statement is VariableAssignmentStatementSyntax varAssign)
            {
                await EvaluateVariableAssignmentCoreAsync(sourceName, sourceText, varAssign, cancellationToken);
                UpdateLastResultIfAny(Array.Empty<object?>());
                pendingInput = null;
                continue;
            }

            var statementResults = statement switch
            {
                PipelineStatementSyntax pipelineStatement => EvaluatePipelineWithRedirectionAsync(
                    sourceName,
                    sourceText,
                    pipelineStatement.Pipeline,
                    cancellationToken,
                    pendingInput,
                    pendingFirstCommandArguments),
                _ => EvaluateStatementAsync(sourceName, sourceText, statement, cancellationToken),
            };

            // A statement that yields is generator output, so stream it instead of draining it
            // to a list. Draining is what made an infinite generator hang: `while (true) { yield
            // 7 }` is a single statement, and materializing every value it will ever produce
            // never finishes. A bare `yield` already streams through its own branch above; this
            // extends the same rule to a yield nested in a loop, `if`, `try`, or `switch`.
            //
            // Neither step below is skipped in spirit. Suppression cannot apply — it fires only
            // for a single-stage pipeline statement, which never contains a yield — and the last
            // result is already maintained by the body's own block execution, so leaving it alone
            // here matches what a bare `yield` does.
            if (StatementStreamsOutput(statement))
            {
                await foreach (var value in statementResults.WithCancellation(cancellationToken))
                {
                    yield return value;
                }

                pendingInput = null;
                continue;
            }

            // `TS-P1-45`. Everything except a single-stage expression pipeline streams,
            // retaining values only until the `$tosh.Last.Result` budget is spent.
            //
            // Draining used to be unconditional, so a bare command inside a block was
            // materialized whole: `func g() { yes } | first` never terminated, and
            // `func g() { seq 1 N } | first` scaled linearly — 1.45s at five million,
            // 4.86s at twenty — while the same loop written as `for i in 1..N { $i }`
            // short-circuited in 0.28s.
            //
            // Two things depended on the drain and only one of them survives as a
            // constraint. Suppression does not: `ShouldSuppressStatementResults` fires
            // only for a single-stage *expression* pipeline whose values are all null, so
            // it can never apply to the statements streamed here — the same reasoning the
            // yield branch above already uses. `$tosh.Last.Result` does: it holds the
            // whole array when a statement produced several, which cannot be known
            // without keeping them.
            //
            // So values are kept up to `LastResultRetentionLimit` and the last result is
            // set from them afterwards. Past the limit the retained copy is dropped and
            // the last result is cleared rather than left stale — reading a *previous*
            // statement's output would be worse than reading nothing. The limit is set
            // far above any statement whose output a person would go on to inspect, so
            // in practice nothing observable changes; what changes is that the
            // pathological case streams instead of hanging.
            if (CanStreamStatementResults(statement))
            {
                List<object?>? retained = new();

                await foreach (var value in statementResults.WithCancellation(cancellationToken))
                {
                    if (retained is not null)
                    {
                        if (retained.Count < LastResultRetentionLimit)
                        {
                            retained.Add(value);
                        }
                        else
                        {
                            retained = null;
                        }
                    }

                    yield return value;
                }

                if (retained is null)
                {
                    LanguageRuntime.ExecutionObserver.SetLastResult(null);
                }
                else
                {
                    UpdateLastResultIfAny(retained);
                }

                pendingInput = null;

                if (statement is PipelineStatementSyntax && pendingFirstCommandArguments is not null)
                {
                    pendingFirstCommandArguments = null;
                }

                continue;
            }

            IReadOnlyList<object?> values = await AsyncEnumerableExtensions.ToListAsync(statementResults, cancellationToken);

            if (ShouldSuppressStatementResults(statement, values))
            {
                values = Array.Empty<object?>();
            }

            UpdateLastResultIfAny(values);

            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
            }

            pendingInput = null;

            if (statement is PipelineStatementSyntax && pendingFirstCommandArguments is not null)
            {
                pendingFirstCommandArguments = null;
            }
        }
    }
}
