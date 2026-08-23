using System.Collections;
using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

/// <summary>
/// Variables, scopes and assignment: declaring and binding a name, the lexical scope
/// stack, and every form of assignment — simple, compound, tuple, member and static.
///
/// Moved out of ToshEngine.cs by `TOAST-0005`. Every member moved **verbatim**.
///
/// `CreateScopedTypeResolver` and `CreateScopedCommandView` were left behind despite
/// the name. They scope a *resolver* to the engine, and have nothing to do with the
/// lexical scope stack this file manages.
/// </summary>
public sealed partial class ToshEngine
{

    private async IAsyncEnumerable<object?> EvaluateTupleAssignmentAsync(
        string sourceName,
        string sourceText,
        TupleAssignmentStatementSyntax tupleAssign,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Evaluate the right-hand side
        var values = await AsyncEnumerableExtensions.ToListAsync(
            EvaluatePipelineAsync(sourceName, sourceText, tupleAssign.Value, cancellationToken, outputIsCaptured: true),
            cancellationToken);

        // One value that is itself positional spreads; anything else is taken as the values the
        // pipeline produced.
        IReadOnlyList<object?> unpacked = values.Count == 1 && TryUnpackPositionalValue(values[0]) is { } spread
            ? spread
            : values;

        EnsureTupleArityMatches(
            sourceName,
            sourceText,
            values.Count == 1 ? values[0] : null,
            unpacked.Count,
            tupleAssign.LeftNames.Count,
            tupleAssign.Span);

        // Prepare every target before mutating any of them. Tuple assignment
        // is simultaneous: an unknown/const target or failed annotation
        // conversion must not leave earlier targets partially updated.
        var preparedAssignments = new List<(string Name, VariableBinding Binding)>(tupleAssign.LeftNames.Count);
        var valueSpan = GetPipelineSpan(tupleAssign.Value) ?? tupleAssign.Span;

        for (int i = 0; i < tupleAssign.LeftNames.Count; i++)
        {
            var name = tupleAssign.LeftNames[i];
            var assignedValue = i < unpacked.Count ? unpacked[i] : null;

            EnsureBindingNameIsNotReserved(sourceName, sourceText, name, tupleAssign.Span, "reserved runtime namespace");
            var existingBinding = RequireMutableVariableBinding(
                sourceName,
                sourceText,
                name,
                tupleAssign.Span);
            preparedAssignments.Add((
                name,
                CreateAssignedVariableBinding(
                    sourceName,
                    sourceText,
                    name,
                    valueSpan,
                    existingBinding,
                    CloneStructAssignmentValue(assignedValue))));
        }

        foreach (var (name, binding) in preparedAssignments)
        {
            AssignExistingVariableBinding(sourceName, sourceText, name, tupleAssign.Span, binding);
        }

        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateVariableDeclarationAsync(
        string sourceName,
        string sourceText,
        VariableDeclarationStatementSyntax declaration,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, declaration.Name, declaration.Span, "reserved runtime namespace");

        VariableBinding binding;
        if (declaration.Value is null)
        {
            binding = new VariableBinding(null, ReplayAsPipeline: false, IsAllocatedOnly: true);
        }
        else
        {
            // Phase 3.2 — push the target type annotation so generic
            // calls in the initializer can seed bindings from it.
            var prevTarget = _targetTypeAnnotation.Value;
            _targetTypeAnnotation.Value = declaration.TypeName;
            try
            {
                binding = await EvaluateVariableBindingAsync(sourceName, sourceText, declaration.Value, cancellationToken);
            }
            finally
            {
                _targetTypeAnnotation.Value = prevTarget;
            }
        }

        // Struct copy-on-assign: clone struct instances to enforce value-type semantics
        if (binding.Value is ToshStructInstance structInstance)
        {
            binding = binding with { Value = structInstance.Clone() };
        }

        var declaredRefinement = declaration.TypeName is not null
            ? CreateRefinementAnnotation(sourceName, sourceText, declaration.Refinement)
            : null;

        if (declaration.TypeName is not null)
        {
            if (!binding.IsAllocatedOnly)
            {
                var valueSpan = GetPipelineSpan(declaration.Value) ?? declaration.Span;
                var converted = ConvertAnnotatedValue(
                    declaration.TypeName,
                    declaredRefinement,
                    binding.Value,
                    valueSpan,
                    sourceName,
                    sourceText,
                    declaration.Name);

                binding = binding with { Value = converted };
            }

            binding = binding with
            {
                DeclaredTypeName = declaration.TypeName,
                DeclaredRefinement = declaredRefinement,
            };
        }

        if (declaration.Name == "_" && TryGetVariableBinding("_", out _))
        {
            WriteWarning(
                code: "tosh.naming.shadowed_underscore",
                title: "Redeclaring '_' shadows an existing binding.",
                help: "Use a different name if this value matters, or hush this code: hush tosh.naming.shadowed_underscore",
                category: ToshDiagnosticCategory.Naming,
                sourceName: sourceName,
                line: LineFromOffset(sourceText, declaration.Span.Start));
        }

        if (declaration.IsConst)
        {
            binding = binding with { IsConst = true };
        }

        DeclareVariable(
            declaration.Name,
            binding,
            declaration.Modifier,
            sourceName,
            sourceText,
            declaration.Span);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateVariableAssignmentAsync(
        string sourceName,
        string sourceText,
        VariableAssignmentStatementSyntax assignment,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EvaluateVariableAssignmentCoreAsync(sourceName, sourceText, assignment, cancellationToken);
        yield break;
    }

    private async Task EvaluateVariableAssignmentCoreAsync(
        string sourceName,
        string sourceText,
        VariableAssignmentStatementSyntax assignment,
        CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, assignment.Name, assignment.Span, "reserved runtime namespace");

        VariableBinding existingBinding;
        VariableBinding value;
        if (assignment.Operator == "??=")
        {
            existingBinding = RequireMutableVariableBinding(
                sourceName,
                sourceText,
                assignment.Name,
                assignment.Span);

            if (!existingBinding.IsAllocatedOnly && existingBinding.Value is not null)
            {
                return;
            }

            value = await EvaluateVariableBindingAsync(sourceName, sourceText, assignment.Value, cancellationToken);
        }
        else
        {
            // Preserve the established order for every other assignment:
            // evaluate the RHS before resolving the mutable target.
            value = await EvaluateVariableBindingAsync(sourceName, sourceText, assignment.Value, cancellationToken);
            existingBinding = RequireMutableVariableBinding(
                sourceName,
                sourceText,
                assignment.Name,
                assignment.Span);
        }

        var incomingValue = CloneStructAssignmentValue(value.Value);
        object? assignedValue = incomingValue;

        if (assignment.Operator is not "=" and not "??=")
        {
            if (existingBinding.IsAllocatedOnly)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.compound_assignment_requires_value",
                    Title: $"Variable '{assignment.Name}' does not have a value yet.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: assignment.Span,
                    Label: $"assign '{assignment.Name}' before using '{assignment.Operator}'"));
            }

            assignedValue = await ApplyCompoundAssignmentAsync(
                sourceName,
                sourceText,
                assignment.Span,
                existingBinding.Value,
                assignment.Operator,
                incomingValue,
                cancellationToken);
        }

        var assignedBinding = CreateAssignedVariableBinding(
            sourceName,
            sourceText,
            assignment.Name,
            GetPipelineSpan(assignment.Value) ?? assignment.Span,
            existingBinding,
            assignedValue);
        AssignExistingVariableBinding(
            sourceName,
            sourceText,
            assignment.Name,
            assignment.Span,
            assignedBinding);
    }

    private async IAsyncEnumerable<object?> EvaluateMemberAssignmentAsync(
        string sourceName,
        string sourceText,
        MemberAssignmentStatementSyntax assignment,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Preserve the established RHS-first order for ordinary assignment,
        // but defer a null-coalescing RHS until the existing target value has
        // actually been observed as null.
        VariableBinding? binding = null;
        if (assignment.Operator != "??=")
        {
            binding = await EvaluateVariableBindingAsync(sourceName, sourceText, assignment.Value, cancellationToken);
        }

        // Path 1 — terminal index access: `$root[..].chain[..]["key"] = value`.
        // We evaluate the indexer's own target (everything but the final
        // `[index]`) into an object, then route through SetIndexedValue.
        if (assignment.Target is IndexAccessArgumentSyntax idx)
        {
            var indexedTarget = await EvaluateArgumentAsync(sourceName, sourceText, idx.Target, cancellationToken);
            var indexValue = await EvaluateArgumentAsync(sourceName, sourceText, idx.Index, cancellationToken);

            if (assignment.Operator == "??=")
            {
                try
                {
                    var currentValue = await ShellIndexingUtilities.GetIndexedValueAsync(
                        indexedTarget,
                        indexValue,
                        idx.LookupKind,
                        cancellationToken);
                    if (currentValue is not null)
                    {
                        yield break;
                    }
                }
                catch (Exception exception) when (ShouldWrapAssignmentFailure(exception))
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.index_assignment_failed",
                        Title: exception.Message,
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: assignment.Target.Span,
                        Label: "while reading this index for null-coalescing assignment"));
                }

                var coalescedBinding = await EvaluateVariableBindingAsync(
                    sourceName,
                    sourceText,
                    assignment.Value,
                    cancellationToken);

                try
                {
                    await ShellIndexingUtilities.SetIndexedValueAsync(
                        indexedTarget,
                        indexValue,
                        coalescedBinding.Value,
                        idx.LookupKind,
                        cancellationToken);
                }
                catch (Exception exception) when (ShouldWrapAssignmentFailure(exception))
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.index_assignment_failed",
                        Title: exception.Message,
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: assignment.Target.Span,
                        Label: "while assigning to this index"));
                }

                yield break;
            }

            var newValue = binding!.Value;
            try
            {
                if (assignment.Operator != "=")
                {
                    var currentValue = await ShellIndexingUtilities.GetIndexedValueAsync(
                        indexedTarget,
                        indexValue,
                        idx.LookupKind,
                        cancellationToken);
                    newValue = await ApplyCompoundAssignmentAsync(
                        sourceName,
                        sourceText,
                        assignment.Span,
                        currentValue,
                        assignment.Operator,
                        binding.Value,
                        cancellationToken);
                }

                await ShellIndexingUtilities.SetIndexedValueAsync(
                    indexedTarget,
                    indexValue,
                    newValue,
                    idx.LookupKind,
                    cancellationToken);
            }
            catch (Exception exception) when (ShouldWrapAssignmentFailure(exception))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.index_assignment_failed",
                    Title: exception.Message,
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: assignment.Target.Span,
                    Label: "while assigning to this index"));
            }

            yield break;
        }

        object? target;
        string memberPath;

        // `TS-P2-51`. A `$`-less dotted target — `B.S`, `Outer.Inner.V`, `System.Console.Title`
        // — is a static member path. The parser hands the whole path over unresolved because
        // only the engine can say where the type ends and the members begin.
        if (TryDecomposeStaticAssignmentTarget(assignment.Target, out var staticPath))
        {
            (target, memberPath) = ResolveStaticAssignmentTarget(
                sourceName,
                sourceText,
                staticPath,
                assignment.Target.Span);
        }
        else if (TryDecomposeMemberAssignmentTarget(assignment.Target, out var rootExpression, out memberPath))
        {
            target = await EvaluateOrMaterializeRootTargetAsync(sourceName, sourceText, rootExpression, cancellationToken);
        }
        else
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.invalid_member_assignment_target",
                Title: "Assignments to members require a member path target.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: assignment.Target.Span,
                Label: "use a target like '$person.Name'"));
        }

        if (assignment.Operator == "??=")
        {
            try
            {
                var currentValue = await Runtime.ObjectAccessor.GetValueAsync(
                    target,
                    memberPath,
                    cancellationToken);
                if (currentValue is not null)
                {
                    yield break;
                }
            }
            catch (Exception exception) when (ShouldWrapAssignmentFailure(exception))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.member_assignment_failed",
                    Title: exception.Message,
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: assignment.Target.Span,
                    Label: "while reading this member for null-coalescing assignment"));
            }

            var coalescedBinding = await EvaluateVariableBindingAsync(
                sourceName,
                sourceText,
                assignment.Value,
                cancellationToken);

            try
            {
                await Runtime.ObjectAccessor.SetValueAsync(
                    target,
                    memberPath,
                    coalescedBinding.Value,
                    cancellationToken);
            }
            catch (Exception exception) when (ShouldWrapAssignmentFailure(exception))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.member_assignment_failed",
                    Title: exception.Message,
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: assignment.Target.Span,
                    Label: "while assigning to this member"));
            }

            yield break;
        }

        var valueToAssign = binding!.Value;
        try
        {
            if (assignment.Operator != "=")
            {
                var currentValue = await Runtime.ObjectAccessor.GetValueAsync(
                    target,
                    memberPath,
                    cancellationToken);
                valueToAssign = await ApplyCompoundAssignmentAsync(
                    sourceName,
                    sourceText,
                    assignment.Span,
                    currentValue,
                    assignment.Operator,
                    binding.Value,
                    cancellationToken);
            }

            await Runtime.ObjectAccessor.SetValueAsync(
                target,
                memberPath,
                valueToAssign,
                cancellationToken);
        }
        catch (Exception exception) when (ShouldWrapAssignmentFailure(exception))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.member_assignment_failed",
                Title: exception.Message,
                SourceName: sourceName,
                SourceText: sourceText,
                Span: assignment.Target.Span,
                Label: "while assigning to this member"));
        }

        yield break;
    }

    private async Task<VariableBinding> EvaluateVariableBindingAsync(
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        CancellationToken cancellationToken)
    {
        if (pipeline.Stages.Count == 1 &&
            pipeline.Stages[0] is ExpressionPipelineStageSyntax
            {
                Expression: VariableReferenceArgumentSyntax variableReference,
            } &&
            TryGetVariableBinding(variableReference.Name, out var existingBinding))
        {
            return existingBinding;
        }

        if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, pipeline, cancellationToken) is { Matched: true } raw)
        {
            return new VariableBinding(raw.Value,
                ReplayAsPipeline: ShouldReplayAsPipeline(raw.Value),
                IsAllocatedOnly: false);
        }

        var values = await AsyncEnumerableExtensions.ToListAsync(
            EvaluatePipelineAsync(sourceName, sourceText, pipeline, cancellationToken, outputIsCaptured: true),
            cancellationToken);

        return values.Count switch
        {
            0 => new VariableBinding(null, ReplayAsPipeline: false, IsAllocatedOnly: false),
            1 => new VariableBinding(values[0],
                ReplayAsPipeline: ShouldReplayAsPipeline(values[0]),
                IsAllocatedOnly: false),
            _ => new VariableBinding(values.ToArray(), ReplayAsPipeline: true, IsAllocatedOnly: false),
        };
    }

    public bool TryGetVariableValue(string name, out object? value)
    {
        if (TryGetVariableBinding(name, out var binding))
        {
            value = binding.Value;
            return true;
        }

        value = null;
        return false;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetVisibleVariables()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<KeyValuePair<string, object?>>();

        foreach (var scope in _scopes)
        {
            foreach (var (name, rawValue) in scope.Variables)
            {
                if (seen.Add(name))
                {
                    var value = rawValue is VariableBinding binding ? binding.Value : rawValue;
                    result.Add(new KeyValuePair<string, object?>(name, value));
                }
            }
        }

        foreach (var (name, rawValue) in Runtime.Variables)
        {
            if (seen.Add(name))
            {
                var value = rawValue is VariableBinding binding ? binding.Value : rawValue;
                result.Add(new KeyValuePair<string, object?>(name, value));
            }
        }

        return result;
    }

    private bool TryGetVariableBinding(string name, out VariableBinding binding)
    {
        if (string.Equals(name, "tosh", StringComparison.Ordinal))
        {
            binding = new VariableBinding(_toshNamespace, ReplayAsPipeline: false, IsAllocatedOnly: false);
            return true;
        }

        if (string.Equals(name, "env", StringComparison.Ordinal))
        {
            binding = new VariableBinding(_environmentNamespace, ReplayAsPipeline: false, IsAllocatedOnly: false);
            return true;
        }

        foreach (var scope in _scopes)
        {
            if (scope.Variables.TryGetValue(name, out var rawValue))
            {
                binding = ToVariableBinding(rawValue);
                return true;
            }
        }

        if (Runtime.Variables.TryGetValue(name, out var globalValue))
        {
            binding = ToVariableBinding(globalValue);
            return true;
        }

        binding = new VariableBinding(null, ReplayAsPipeline: false, IsAllocatedOnly: false);
        return false;
    }

    private VariableBinding RequireMutableVariableBinding(
        string sourceName,
        string sourceText,
        string name,
        TextSpan span)
    {
        if (!TryGetVariableBinding(name, out var binding))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.unknown_variable",
                Title: $"Variable '{name}' has not been declared.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: $"declare '{name}' with 'var' before assigning to it",
                Help: $"try 'var {name} = ...' the first time you bind this variable."));
        }

        if (binding.IsConst)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.const_reassignment",
                Title: $"Cannot reassign constant '{name}'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: $"'{name}' was declared with 'const' and cannot be modified",
                Help: "use 'var' instead of 'const' if you need to reassign this variable."));
        }

        return binding;
    }

    private VariableBinding CreateAssignedVariableBinding(
        string sourceName,
        string sourceText,
        string name,
        TextSpan valueSpan,
        VariableBinding existingBinding,
        object? assignedValue)
    {
        if (existingBinding.DeclaredTypeName is not null)
        {
            assignedValue = ConvertAnnotatedValue(
                existingBinding.DeclaredTypeName,
                existingBinding.DeclaredRefinement,
                assignedValue,
                valueSpan,
                sourceName,
                sourceText,
                name);
        }

        return existingBinding with
        {
            Value = assignedValue,
            ReplayAsPipeline = ShouldReplayAsPipeline(assignedValue),
            IsAllocatedOnly = false,
        };
    }

    private static object? CloneStructAssignmentValue(object? value) =>
        value is ToshStructInstance structInstance
            ? structInstance.Clone()
            : value;

    private void AssignExistingVariableBinding(
        string sourceName,
        string sourceText,
        string name,
        TextSpan span,
        VariableBinding binding)
    {
        if (TryAssignVariable(name, binding))
        {
            return;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.unknown_variable",
            Title: $"Variable '{name}' has not been declared.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"declare '{name}' with 'var' before assigning to it",
            Help: $"try 'var {name} = ...' the first time you bind this variable."));
    }

    private bool TryBuildVariableReferenceHint(string commandName, out string suggestedReference, out string variableName)
    {
        suggestedReference = string.Empty;
        variableName = string.Empty;

        if (string.IsNullOrWhiteSpace(commandName) ||
            commandName[0] == '$' ||
            commandName == "_" ||
            commandName.StartsWith("_.", StringComparison.Ordinal))
        {
            return false;
        }

        var separatorIndex = commandName.IndexOf('.');
        var rootName = separatorIndex >= 0 ? commandName[..separatorIndex] : commandName;

        if (!IsIdentifier(rootName) || !TryGetVariableBinding(rootName, out _))
        {
            return false;
        }

        suggestedReference = "$" + commandName;
        variableName = rootName;
        return true;
    }

    private ValueTask<object?> ApplyCompoundAssignmentAsync(
        string sourceName,
        string sourceText,
        TextSpan span,
        object? currentValue,
        string assignmentOperator,
        object? incomingValue,
        CancellationToken cancellationToken)
    {
        var binaryOperator = assignmentOperator switch
        {
            "+=" => "+",
            "-=" => "-",
            "*=" => "*",
            "**=" => "**",
            "/=" => "/",
            "//=" => "//",
            "%=" => "%",
            _ => throw new InvalidOperationException($"Unsupported assignment operator '{assignmentOperator}'."),
        };

        return EvaluateBinaryOperatorAsync(
            sourceName,
            sourceText,
            span,
            currentValue,
            binaryOperator,
            incomingValue,
            cancellationToken);
    }

    private static bool ShouldWrapAssignmentFailure(Exception exception) =>
        exception is not ToshDiagnosticException and
        not OperationCanceledException and
        not ShellControlFlowException &&
        !IsToshThrown(exception);

    private (bool FreedValue, string FreedValueKind) TryDisposeForgottenVariableValue(object? value)
    {
        if (value is NativeBuffer buffer)
        {
            if (buffer.IsFreed || VariableValueStillReferenced(buffer))
            {
                return (false, string.Empty);
            }

            buffer.Dispose();
            return (true, nameof(NativeBuffer));
        }

        if (value is ManagedFileHandle handle)
        {
            if (!handle.IsOpen || VariableValueStillReferenced(handle))
            {
                return (false, string.Empty);
            }

            handle.Dispose();
            return (true, nameof(ManagedFileHandle));
        }

        return (false, string.Empty);
    }

    private bool VariableValueStillReferenced(object? value)
    {
        foreach (var scope in _scopes)
        {
            foreach (var scopedValue in scope.Variables.Values)
            {
                if (ValuesReferToSameObject(ToVariableBinding(scopedValue).Value, value))
                {
                    return true;
                }
            }
        }

        foreach (var runtimeValue in Runtime.Variables.Values)
        {
            if (ValuesReferToSameObject(ToVariableBinding(runtimeValue).Value, value))
            {
                return true;
            }
        }

        foreach (var handler in Runtime.Events.GetHandlers())
        {
            if (handler.CapturedScopes is not { } scopes)
            {
                continue;
            }

            foreach (var scopeObj in scopes)
            {
                if (scopeObj is LexicalScope scope)
                {
                    foreach (var scopedValue in scope.Variables.Values)
                    {
                        if (ValuesReferToSameObject(ToVariableBinding(scopedValue).Value, value))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private IReadOnlyList<LexicalScope>? CaptureVisibleScopes()
    {
        if (_scopes.Count == 0)
        {
            return null;
        }

        return _scopes.Reverse().ToArray();
    }

    /// <summary>
    /// Runs with <paramref name="scopes"/> as the entire visible stack, restoring it after.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="PushCapturedScopes"/>, which layers scopes *over* what is
    /// already visible. That is right for a definition site, and wrong for a rune's argument:
    /// what is already visible includes the rune's parameters, and an empty capture leaves
    /// them exposed rather than shadowed.
    /// </remarks>
    private IDisposable UseScopes(IReadOnlyList<LexicalScope>? scopes)
    {
        var saved = _scopes.ToArray();
        _scopes.Clear();

        if (scopes is not null)
        {
            foreach (var scope in scopes)
            {
                _scopes.Push(scope);
            }
        }

        return new RestoredScopes(_scopes, saved);
    }

    /// <summary>Puts back the stack <see cref="UseScopes"/> set aside.</summary>
    private sealed class RestoredScopes(Stack<LexicalScope> scopes, LexicalScope[] saved) : IDisposable
    {
        public void Dispose()
        {
            scopes.Clear();

            // `Stack{T}.ToArray` yields top-first, so it is refilled from the bottom up.
            for (var index = saved.Length - 1; index >= 0; index--)
            {
                scopes.Push(saved[index]);
            }
        }
    }

    private IDisposable PushCapturedScopes(IReadOnlyList<LexicalScope>? scopes)
    {
        if (scopes is null || scopes.Count == 0)
        {
            return ScopeFrames.Empty;
        }

        var disposables = new List<IDisposable>(scopes.Count);

        foreach (var scope in scopes)
        {
            disposables.Add(PushScope(scope));
        }

        return new ScopeFrames(disposables);
    }

    private void DeclareVariable(
        string name,
        VariableBinding binding,
        DeclarationModifier modifier,
        string? sourceName = null,
        string? sourceText = null,
        TextSpan? span = null)
    {
        EnsureReservedBindingName(name);
        EnsureConstantIsNotRedeclared(name, modifier, sourceName, sourceText, span);

        if (modifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek() is { IsModuleScope: true, ExportDeclarationsByDefault: true } moduleScope)
        {
            moduleScope.Variables[name] = binding;
            moduleScope.Exports!.Variables[name] = binding.Value;
            return;
        }

        if (modifier == DeclarationModifier.Export && TryGetNearestModuleScope(out var exportScope))
        {
            exportScope.Variables[name] = binding;
            exportScope.Exports!.Variables[name] = binding.Value;
            return;
        }

        if (modifier == DeclarationModifier.Shy)
        {
            if (_scopes.Count == 0)
            {
                throw new InvalidOperationException("Shy declarations require a function, block, or module scope.");
            }

            _scopes.Peek().Variables[name] = binding;
            return;
        }

        if (modifier is DeclarationModifier.Global or DeclarationModifier.Export)
        {
            if (modifier == DeclarationModifier.Global && TryGetNearestModuleScope(out var globalModuleScope))
            {
                globalModuleScope.Variables[name] = binding;
                globalModuleScope.Exports!.Variables[name] = binding.Value;
                return;
            }

            Runtime.Variables[name] = binding;
            Runtime.SyncExportedEnvironmentVariable(name, binding.Value);
            return;
        }

        if (_scopes.Count > 0)
        {
            _scopes.Peek().Variables[name] = binding;
            return;
        }

        Runtime.Variables[name] = binding;
        Runtime.SyncExportedEnvironmentVariable(name, binding.Value);
    }

    private bool TryAssignVariable(string name, VariableBinding binding)
    {
        EnsureReservedBindingName(name);

        foreach (var scope in _scopes)
        {
            if (!scope.Variables.ContainsKey(name))
            {
                continue;
            }

            scope.Variables[name] = binding;
            return true;
        }

        if (Runtime.Variables.ContainsKey(name))
        {
            Runtime.Variables[name] = binding;
            Runtime.SyncExportedEnvironmentVariable(name, binding.Value);
            return true;
        }

        return false;
    }

    private static string DescribeUnknownVariable(string name)
    {
        foreach (var (spelling, suggestion) in ShellNamespaceSuggestions)
        {
            if (string.Equals(name, spelling, StringComparison.OrdinalIgnoreCase))
            {
                return $"did you mean '{suggestion}'? {spelling} is not a shell variable in TōSh.";
            }
        }

        return $"declare it first with 'var {name} = ...'.";
    }

    private IDisposable PushScope(IReadOnlyDictionary<string, object?> locals, bool isModuleScope = false)
    {
        _scopes.Push(new LexicalScope(locals, isModuleScope));
        return new ScopeFrame(_scopes, Runtime.Events);
    }

    /// <summary>
    /// Pushes a scope in which a class's nested types are reachable by their own names, for the
    /// duration of code that belongs to that class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Outside the class a nested type is reached through it — <c>Reactor.Fuel</c> — but inside,
    /// the qualification is noise: the code is already in <c>Reactor</c>. Without this a class
    /// could declare an enum and then not name it, so <c>prop Loaded = Fuel.Mox</c> read
    /// <c>Fuel.Mox</c> as a bareword and an annotation of <c>Fuel</c> failed outright.
    /// </para>
    /// <para>
    /// The names go into an ordinary lexical scope rather than into type resolution as a special
    /// case, so they are found by the walk that already looks for types and disappear when the
    /// scope pops.
    /// </para>
    /// </remarks>
    internal IDisposable? PushNestedTypeScope(ToshClassDefinition definition)
    {
        var nested = definition.NestedTypesForScope();
        if (nested.Count == 0)
        {
            return null;
        }

        var scope = new LexicalScope();
        foreach (var (name, type) in nested)
        {
            scope.Classes[name] = type;
        }

        return PushScope(scope);
    }

    private IDisposable PushScope(LexicalScope scope)
    {
        _scopes.Push(scope);
        return new ScopeFrame(_scopes, Runtime.Events);
    }

    private static VariableBinding ToVariableBinding(object? value)
    {
        return value is VariableBinding binding
            ? binding
            : new VariableBinding(value, ReplayAsPipeline: ShouldReplayAsPipeline(value), IsAllocatedOnly: false);
    }

    // ================================================================
    // Rune (macro) expansion
    // ================================================================

    /// <summary>
    /// Starts a default-value scope. Ambient bindings — `this` and
    /// `super` for an instance method — are visible to every default
    /// (TS-P1-21); parameters are added as they bind.
    /// </summary>
    private static Dictionary<string, object?> SeedDefaultScope(
        IReadOnlyDictionary<string, object?>? ambient)
    {
        var scope = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (ambient is not null)
        {
            foreach (var (name, value) in ambient)
            {
                scope[name] = value;
            }
        }

        return scope;
    }

    private static bool TryDecomposeMemberAssignmentTarget(
        ArgumentSyntax target,
        out ArgumentSyntax rootExpression,
        out string memberPath)
    {
        var segments = new Stack<string>();
        var current = target;

        while (current is MemberAccessArgumentSyntax memberAccess)
        {
            segments.Push(memberAccess.MemberPath);
            current = memberAccess.Target;
        }

        if (segments.Count == 0)
        {
            rootExpression = target;
            memberPath = string.Empty;
            return false;
        }

        rootExpression = current;
        memberPath = string.Join(".", segments);
        return true;
    }

    /// <summary>
    /// Joins a <c>$</c>-less assignment target back into one dotted path.
    /// </summary>
    /// <remarks>
    /// The parser leaves the head as a <see cref="StaticMemberAccessArgumentSyntax"/> holding
    /// whatever it lexed as a single token, and any further <c>.Member</c> tokens arrive as
    /// wrappers around it. Rejoining them means <c>ResolveStaticAssignmentTarget</c> sees one
    /// path however the source happened to be tokenized.
    /// </remarks>
    private static bool TryDecomposeStaticAssignmentTarget(ArgumentSyntax target, out string path)
    {
        var segments = new Stack<string>();
        var current = target;

        while (current is MemberAccessArgumentSyntax memberAccess)
        {
            segments.Push(memberAccess.MemberPath);
            current = memberAccess.Target;
        }

        if (current is not StaticMemberAccessArgumentSyntax staticAccess)
        {
            path = string.Empty;
            return false;
        }

        segments.Push(staticAccess.Path);
        path = string.Join(".", segments);
        return true;
    }

    /// <summary>
    /// Splits a static assignment path into the type it names and the member path to assign
    /// from there, or explains why it names no type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Longest prefix first, matching <c>TryResolveQualifiedAccess</c>: <c>System.Console.Title</c>
    /// must find the type <c>System.Console</c> rather than stopping at the namespace, while
    /// <c>B.S.Length</c> must find the class <c>B</c> and leave <c>S.Length</c> as the path to
    /// walk. One rule serves both, and it is the rule reads already use.
    /// </para>
    /// <para>
    /// A variable of that name is asked about first, and wins. This is the forgotten-<c>$</c>
    /// case the parser used to reject before it could tell the two apart, and the hint is
    /// rebuilt here, where the variable table exists, so <c>person.Name = "x"</c> still names
    /// the real problem.
    /// </para>
    /// </remarks>
    private (object Target, string MemberPath) ResolveStaticAssignmentTarget(
        string sourceName,
        string sourceText,
        string path,
        TextSpan span)
    {
        // A variable of that name wins, and is asked first. Type resolution matches simple
        // names across every loaded assembly and ignores case, so `var person = …` followed by
        // `person.Name = "x"` found an unrelated CLR `Person` and wrote a static instead of
        // naming the forgotten `$` — measured, not imagined: it is what the full suite hit once
        // a compiler test had emitted a type by that name. A wrong hint costs a message; a
        // wrong static write mutates shared state, so the safe reading goes first.
        if (TryBuildVariableReferenceHint(path, out var suggestedReference, out var variableName))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.variable_reference_requires_dollar",
                Title: $"Variable '{variableName}' exists, but variable references must start with '$'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: $"did you mean '{suggestedReference} = ...'?",
                Help: "declare variables with 'var name', then assign through them as '$name.Member = value'."));
        }

        var segments = SplitQualifiedPath(path);

        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var prefix = string.Join('.', segments.Take(prefixLength));
            var remainder = string.Join('.', segments.Skip(prefixLength));

            if (TryGetNamedType(prefix, out var namedType))
            {
                return (namedType, remainder);
            }

            if (ResolveTypeName(prefix) is { } clrType)
            {
                return (clrType, remainder);
            }
        }

        var head = segments.Length > 0 ? segments[0] : path;

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.unknown_static_assignment_target",
            Title: $"'{path}' does not name a static member, because '{head}' names no type.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"'{head}' is not a class, enum, module, or CLR type in scope",
            Help: "assign a static member as 'TypeName.Member = value', or a variable's member as '$name.Member = value'."));
    }

    private sealed record VariableBinding(
        object? Value,
        bool ReplayAsPipeline,
        bool IsAllocatedOnly,
        bool IsConst = false,
        string? DeclaredTypeName = null,
        RefinementAnnotation? DeclaredRefinement = null);
}
