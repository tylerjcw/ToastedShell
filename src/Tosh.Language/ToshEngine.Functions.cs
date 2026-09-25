using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using Tosh.Runtime;
using Tosh.Language.Binding;
using Tosh.Language.Bridge;
using Tosh.Language.Debugging;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed partial class ToshEngine
{
    private async IAsyncEnumerable<object?> EvaluateFunctionDefinitionAsync(
        string sourceName,
        string sourceText,
        FunctionDefinitionStatementSyntax function,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, function.Name, function.Span, "reserved runtime namespace");
        var definition = CreateFunctionDefinition(
            function.Name,
            function.Parameters,
            function.ReturnTypeName,
            function.Body,
            function.IsCommandWrapper,
            sourceName,
            sourceText,
            function.Span,
            function.DocComment,
            typeParameters: function.TypeParameters,
            typeParameterConstraints: function.TypeParameterConstraints);

        var functionCommand = new FunctionCommand(this, definition);
        DeclareCommand(functionCommand, function.Modifier);

        if (function.HandlesEvent is not null)
        {
            RegisterEventHandler(functionCommand, function.HandlesEvent, function.HandlerPriority, function.IsOnceHandler, function.WhenGuard, sourceName, sourceText);
        }

        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateRuneDefinitionAsync(
        string sourceName,
        string sourceText,
        RuneDefinitionStatementSyntax rune,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, rune.Name, rune.Span, "reserved runtime namespace");

        var duplicateParameters = rune.Parameters
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicateParameters is not null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.duplicate_rune_parameter",
                Title: $"Rune '{rune.Name}' defines parameter '{duplicateParameters.Key}' more than once.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: duplicateParameters.First().Span,
                Label: $"'{duplicateParameters.Key}' is declared multiple times"));
        }

        var definition = new RuneDefinition(
            rune.Name,
            rune.Parameters.Select(p => new RuneParameterDefinition(p.Name, p.Span)).ToArray(),
            rune.Body,
            rune.IsSealed,
            rune.IsFixed,
            sourceName,
            sourceText,
            rune.Span,
            CaptureVisibleScopes(),
            rune.DocComment is not null ? DocComment.Parse(new[] { new SyntaxToken(SyntaxTokenKind.DocComment, 0, rune.DocComment.ToString() ?? "") }) : null);

        var runeCommand = new RuneCommand(definition);
        DeclareCommand(runeCommand, rune.Modifier);

        yield break;
    }

    private FunctionDefinition CreateFunctionDefinition(
        string name,
        IReadOnlyList<FunctionParameterSyntax> parameters,
        string? returnTypeName,
        BlockSyntax body,
        bool isCommandWrapper,
        string sourceName,
        string sourceText,
        TextSpan span,
        DocComment? docComment = null,
        IReadOnlyList<string>? typeParameters = null,
        IReadOnlyList<TypeParameterConstraintSyntax>? typeParameterConstraints = null)
    {
        var duplicateParameters = parameters
            .GroupBy(parameter => parameter.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateParameters is not null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.duplicate_function_parameter",
                Title: $"Function '{name}' defines parameter '{duplicateParameters.Key}' more than once.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: duplicateParameters.First().Span,
                Label: $"'{duplicateParameters.Key}' is declared multiple times"));
        }

        foreach (var parameter in parameters)
        {
            EnsureBindingNameIsNotReserved(sourceName, sourceText, parameter.Name, parameter.Span, "reserved runtime namespace");
        }

        return new FunctionDefinition(
            name,
            parameters
                .Select(parameter => CreateParameterDefinition(parameter, sourceName, sourceText, typeParameters))
                .ToArray(),
            EraseTypeParameter(returnTypeName, typeParameters),
            body,
            isCommandWrapper,
            sourceName,
            sourceText,
            span,
            CaptureVisibleScopes(),
            docComment,
            IsGenerator: ContainsYieldStatement(body),
            TypeParameters: typeParameters,
            RawReturnTypeName: returnTypeName,
            TypeParameterConstraints: typeParameterConstraints?
                .Select(c => new ToshTypeParameterConstraint(c.TypeParameter, c.ConstraintNames))
                .ToArray());
    }

    private static string? EraseTypeParameter(string? typeName, IReadOnlyList<string>? typeParameters)
    {
        if (typeName is null || typeParameters is not { Count: > 0 }) return typeName;
        if (typeParameters.Contains(typeName, StringComparer.Ordinal)) return null;

        // Generic type whose arg list mentions a type parameter — strip
        // arguments that are themselves type-parameter names so the
        // outer constructor can still be resolved (e.g. `list<T>`
        // becomes `list`). Recursively erase nested generic args.
        var lt = typeName.IndexOf('<');
        var gt = typeName.LastIndexOf('>');
        if (lt > 0 && gt == typeName.Length - 1)
        {
            var head = typeName.Substring(0, lt);
            var inner = typeName.Substring(lt + 1, gt - lt - 1);
            var args = SplitTopLevelCommas(inner);
            var anyParam = false;
            var rebuilt = new List<string>(args.Count);
            foreach (var arg in args)
            {
                var trimmed = arg.Trim();
                if (typeParameters.Contains(trimmed, StringComparer.Ordinal))
                {
                    anyParam = true;
                    continue;
                }
                var erased = EraseTypeParameter(trimmed, typeParameters);
                if (erased is null)
                {
                    anyParam = true;
                    continue;
                }
                if (!ReferenceEquals(erased, trimmed)) anyParam = true;
                rebuilt.Add(erased);
            }
            if (anyParam)
            {
                return rebuilt.Count == 0 ? head : $"{head}<{string.Join(", ", rebuilt)}>";
            }
        }
        return typeName;
    }

    private static IReadOnlyList<string> SplitTopLevelCommas(string s)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        if (start <= s.Length) result.Add(s.Substring(start));
        return result;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StatementSyntax, System.Runtime.CompilerServices.StrongBox<bool>> YieldingStatements = new();

    private void RegisterEventHandler(
        FunctionCommand functionCommand,
        string eventName,
        int? priority,
        bool once,
        BlockSyntax? whenGuard,
        string sourceName,
        string sourceText)
    {
        var capturedScopes = CaptureVisibleScopes();

        var handler = new ShellEventHandler(
            eventName,
            functionCommand.Name,
            async (shellEvent, cancellationToken) =>
            {
                try
                {
                    if (whenGuard is not null)
                    {
                        var guardResult = await EvaluateWhenGuardAsync(
                            sourceName, sourceText, whenGuard, shellEvent, capturedScopes, cancellationToken);

                        if (!guardResult)
                        {
                            return null;
                        }
                    }

                    object? result = null;
                    var context = new CommandContext(
                        LanguageRuntime,
                        EmptyAsyncEnumerable(),
                        new object?[] { shellEvent },
                        cancellationToken,
                        BlockExecutor: _ownBlockExecutor,
                        ScopedCommands: CreateScopedCommandView(),
                        ShellTypes: this);

                    await foreach (var value in functionCommand.ExecuteAsync(context))
                    {
                        result = value;
                    }

                    return result;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await LanguageRuntime.Error.WriteTextLineAsync(
                        $"Event handler '{functionCommand.Name}' for '{eventName}' failed: {ex.Message}",
                        CancellationToken.None);
                    return null;
                }
            },
            priority,
            once,
            capturedScopes?.Cast<object>().ToArray());

        LanguageRuntime.Events.Register(handler);
    }

    private async Task<bool> EvaluateWhenGuardAsync(
        string sourceName,
        string sourceText,
        BlockSyntax guard,
        ShellEvent shellEvent,
        IReadOnlyList<LexicalScope>? capturedScopes,
        CancellationToken cancellationToken)
    {
        if (capturedScopes is not null)
        {
            foreach (var scope in capturedScopes)
            {
                _scopes.Push(scope);
            }
        }

        _scopes.Push(new LexicalScope());
        _scopes.Peek().Variables["_"] = new VariableBinding(shellEvent, ReplayAsPipeline: false, IsAllocatedOnly: false);

        try
        {
            object? lastValue = null;

            await foreach (var value in ExecuteBlockAsync(sourceName, sourceText, guard, cancellationToken, pushNewScope: false))
            {
                lastValue = value;
            }

            return ToshTruthiness.IsTruthy(lastValue);
        }
        finally
        {
            _scopes.Pop();

            if (capturedScopes is not null)
            {
                for (var index = 0; index < capturedScopes.Count; index++)
                {
                    _scopes.Pop();
                }
            }
        }
    }

    private static async IAsyncEnumerable<object?> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }

    private async IAsyncEnumerable<object?> ExpandRuneAsync(
        RuneDefinition rune,
        IReadOnlyList<ArgumentSyntax> arguments,
        string callerSourceName,
        string callerSourceText,
        IAsyncEnumerable<object?> input,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // `TOAST-0069`. A rune that calls itself expands forever, and expansion is not one of
        // the paths the depth guard already covered — a recursive *function* reported
        // `tosh.runtime.recursion_limit_exceeded`, while a recursive rune overflowed the stack
        // and took the process with it.
        using var expansionFrame = ToshExecutionDepthGuard.Enter(
            LanguageRuntime.Options.MaxRecursionDepth,
            $"rune {rune.Name}",
            callerSourceName,
            callerSourceText,
            rune.Span);

        // Bind arguments to parameters as thunks (unevaluated)
        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);

        for (int i = 0; i < rune.Parameters.Count; i++)
        {
            var paramName = rune.Parameters[i].Name;

            if (i < arguments.Count)
            {
                // Store the argument as a RuneThunk — it will be lazily evaluated
                // when the rune body references this parameter
                locals[paramName] = new RuneThunk(
                    arguments[i],
                    callerSourceName,
                    callerSourceText,
                    rune.IsSealed ? CaptureVisibleScopes() : null,
                    rune.IsSealed);
            }
        }

        // Validate argument count
        if (arguments.Count < rune.Parameters.Count)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                "RUNE001",
                $"Rune '{rune.Name}' expects {rune.Parameters.Count} argument(s) but received {arguments.Count}.",
                callerSourceName, callerSourceText, rune.Span));
        }

        // Provide pipeline input as $_ inside the rune body
        locals["_input"] = input;

        if (rune.IsSealed)
        {
            // Hygienic: push captured scopes from definition site, then a new scope
            using var captured = PushCapturedScopes(rune.CapturedScopes);
            await foreach (var item in ExecuteBlockAsync(
                rune.SourceName, rune.SourceText, rune.Body, cancellationToken,
                locals, initialInput: input))
            {
                yield return item;
            }
        }
        else
        {
            // Leaky: execute in the caller's binding store without a new scope so that
            // variables declared inside the rune become visible after invocation.
            // We restore only temporary parameter bindings afterward.
            if (_scopes.Count > 0)
            {
                var targetVariables = _scopes.Peek().Variables;
                var previousBindings = new Dictionary<string, object?>(StringComparer.Ordinal);

                foreach (var (key, value) in locals)
                {
                    previousBindings[key] = targetVariables.TryGetValue(key, out var existing) ? existing : null;
                    targetVariables[key] = new VariableBinding(value, ReplayAsPipeline: false, IsAllocatedOnly: false);
                }

                try
                {
                    await foreach (var item in ExecuteBlockAsync(
                        rune.SourceName, rune.SourceText, rune.Body, cancellationToken,
                        pushNewScope: false, initialInput: input))
                    {
                        yield return item;
                    }
                }
                finally
                {
                    foreach (var (key, previous) in previousBindings)
                    {
                        if (previous is null)
                        {
                            targetVariables.Remove(key);
                        }
                        else
                        {
                            targetVariables[key] = previous;
                        }
                    }
                }
            }
            else
            {
                var targetVariables = LanguageRuntime.Variables;
                var previousBindings = new Dictionary<string, object?>(StringComparer.Ordinal);

                foreach (var (key, value) in locals)
                {
                    previousBindings[key] = targetVariables.TryGetValue(key, out var existing) ? existing : null;
                    targetVariables[key] = value;
                }

                try
                {
                    await foreach (var item in ExecuteBlockAsync(
                        rune.SourceName, rune.SourceText, rune.Body, cancellationToken,
                        pushNewScope: false, initialInput: input))
                    {
                        yield return item;
                    }
                }
                finally
                {
                    foreach (var (key, previous) in previousBindings)
                    {
                        if (previous is null)
                        {
                            targetVariables.Remove(key);
                        }
                        else
                        {
                            targetVariables[key] = previous;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Evaluates a RuneThunk — executes the captured argument expression
    /// in the appropriate scope (caller's scope for sealed, current scope for leaky).
    /// </summary>
    internal async Task<object?> EvaluateRuneThunkAsync(
        RuneThunk thunk,
        CancellationToken cancellationToken)
    {
        if (thunk.Syntax is BlockArgumentSyntax blockArg)
        {
            // Block arguments: evaluate the block and collect results.
            //
            // A block runs where it was *written*, exactly as an expression argument does.
            // This path used to ignore `CallerScopes` and run in whatever scope was current,
            // which inside an expansion is the rune's own parameter scope — so a block
            // forwarded through a second rune saw that rune's parameters instead of the ones
            // it was written against. `rune f(b) { t { t $b } }` made `$b` mean `t`'s `b`
            // rather than `f`'s, and re-entered until the recursion limit stopped it.
            var pushNewScope = !IsInsideLeakyRune();
            var results = new List<object?>();

            using (thunk.IsSealed ? UseScopes(thunk.CallerScopes) : null)
            {
                await foreach (var item in ExecuteBlockAsync(
                    thunk.SourceName, thunk.SourceText, blockArg.Block, cancellationToken,
                    pushNewScope: pushNewScope))
                {
                    results.Add(item);
                }
            }

            return results.Count switch
            {
                0 => null,
                1 => results[0],
                _ => results.ToArray(),
            };
        }

        // Expression arguments: evaluate and return the single value
        if (thunk.IsSealed)
        {
            // Sealed: evaluate in the caller's scope — *as* that stack, not layered over the
            // current one. Layering leaves the rune's own parameter scope underneath, and an
            // argument that names the parameter it is bound to then resolves to itself.
            using var caller = UseScopes(thunk.CallerScopes);
            return await EvaluateArgumentAsync(
                thunk.SourceName, thunk.SourceText, thunk.Syntax, cancellationToken);
        }

        // Leaky: evaluate in the current scope
        return await EvaluateArgumentAsync(
            thunk.SourceName, thunk.SourceText, thunk.Syntax, cancellationToken);
    }

    private bool IsInsideLeakyRune()
    {
        // Check if we're inside a leaky rune by looking for RuneThunk values in scope
        foreach (var scope in _scopes)
        {
            foreach (var (_, value) in scope.Variables)
            {
                if (value is RuneThunk thunk && !thunk.IsSealed)
                    return true;
            }
        }
        return false;
    }

    private async ValueTask<(
        bool Success,
        object? Converted,
        ToshDiagnosticException? Failure)> TryConvertParameterValueAsync(
        FunctionParameterDefinition parameter,
        object? value,
        CancellationToken cancellationToken)
    {
        object? converted = value;

        if (parameter.TypeName is not null)
        {
            var typeConversion = await TryConvertAnnotatedValueAsync(
                parameter.TypeName,
                value,
                cancellationToken);
            converted = typeConversion.Converted;
            if (!typeConversion.Success)
            {
                return (false, converted, DescribeAnnotationFailure(converted));
            }
        }

        var refinement = await TryApplyRefinementWithOptionalCoercionAsync(
            parameter.Refinement,
            converted,
            cancellationToken);
        return (refinement.Success, refinement.RefinedValue, refinement.Failure);
    }

    internal async ValueTask<(
        bool Success,
        Dictionary<string, object?> Locals,
        int Score,
        List<FunctionParameterDefinition>? PendingDefaults)> TryBindCallableParametersAsync(
        IReadOnlyList<FunctionParameterDefinition> parameters,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        static (bool, Dictionary<string, object?>, int, List<FunctionParameterDefinition>?) NoMatch()
            => (false, new Dictionary<string, object?>(StringComparer.Ordinal), 0, null);

        if (PlanCallableParameterBinding(parameters, arguments) is not { } steps)
        {
            return NoMatch();
        }

        var locals = new Dictionary<string, object?>(StringComparer.Ordinal) { ["args"] = arguments.ToArray() };
        var score = 0;
        List<FunctionParameterDefinition>? pendingDefaults = null;
        List<object?>? restArguments = null;

        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (step.IsMissing)
            {
                ApplyMissingArgumentStep(step, locals, ref score, ref pendingDefaults);
                continue;
            }

            var conversion = await TryConvertParameterValueAsync(step.Parameter, step.Value, cancellationToken);

            if (!conversion.Success)
            {
                if (conversion.Failure is not null)
                {
                    throw conversion.Failure;
                }

                return NoMatch();
            }

            if (step.IsRest)
            {
                (restArguments ??= []).Add(conversion.Converted);
                continue;
            }

            if (!ReferenceEquals(conversion.Converted, step.Value))
            {
                score++;
            }

            locals[step.Parameter.Name] = conversion.Converted;
        }

        if (parameters.Count > 0 && parameters[^1].IsRest)
        {
            locals[parameters[^1].Name] = restArguments ?? [];
        }

        return (true, locals, score, pendingDefaults);
    }

    /// <summary>
    /// Keeps only the lowest-scoring matches seen so far, collecting ties.
    /// </summary>
    /// <remarks>
    /// The rule — a strictly better score replaces everything, an equal score joins the tie — is
    /// what decides which overload wins and which calls are reported ambiguous. It was written
    /// out once per surface, and the two copies had to agree exactly for compiled and interpreted
    /// dispatch to pick the same overload (<c>TS-P1-24</c>).
    /// </remarks>
    private static void AccumulateBestMatch<TCandidate>(
        List<CallableBindingMatch<TCandidate>> bestMatches,
        ref int bestScore,
        CallableBindingMatch<TCandidate> match)
    {
        if (match.Score < bestScore)
        {
            bestMatches.Clear();
            bestMatches.Add(match);
            bestScore = match.Score;
            return;
        }

        if (match.Score == bestScore)
        {
            bestMatches.Add(match);
        }
    }

    internal IReadOnlyList<CallableBindingMatch<TCandidate>> SelectBestCallableMatches<TCandidate>(
        IEnumerable<TCandidate> candidates,
        Func<TCandidate, IReadOnlyList<FunctionParameterDefinition>> parameterSelector,
        IReadOnlyList<object?> arguments)
    {
        ValidateNamedArgumentUniqueness(arguments, "this call");
        var bestMatches = new List<CallableBindingMatch<TCandidate>>();
        var bestScore = int.MaxValue;
        var candidateParameters = new List<IReadOnlyList<FunctionParameterDefinition>>();

        foreach (var candidate in candidates)
        {
            var parameters = parameterSelector(candidate);
            candidateParameters.Add(parameters);

            if (!TryBindCallableParameters(
                    parameters,
                    arguments,
                    out var locals,
                    out var score,
                    out var pendingDefaults))
            {
                continue;
            }

            AccumulateBestMatch(
                bestMatches,
                ref bestScore,
                new CallableBindingMatch<TCandidate>(candidate, locals, score, pendingDefaults));
        }

        if (bestMatches.Count == 0)
        {
            ThrowIfNamedArgumentMatchesNoCandidate(candidateParameters, arguments);
        }

        return bestMatches.ToArray();
    }

    internal async ValueTask<IReadOnlyList<CallableBindingMatch<TCandidate>>> SelectBestCallableMatchesAsync<TCandidate>(
        IEnumerable<TCandidate> candidates,
        Func<TCandidate, IReadOnlyList<FunctionParameterDefinition>> parameterSelector,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        ValidateNamedArgumentUniqueness(arguments, "this call");
        var bestMatches = new List<CallableBindingMatch<TCandidate>>();
        var bestScore = int.MaxValue;
        var candidateParameters = new List<IReadOnlyList<FunctionParameterDefinition>>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = parameterSelector(candidate);
            candidateParameters.Add(parameters);
            var binding = await TryBindCallableParametersAsync(
                parameters,
                arguments,
                cancellationToken);
            if (!binding.Success)
            {
                continue;
            }

            AccumulateBestMatch(
                bestMatches,
                ref bestScore,
                new CallableBindingMatch<TCandidate>(
                    candidate,
                    binding.Locals,
                    binding.Score,
                    binding.PendingDefaults));
        }

        if (bestMatches.Count == 0)
        {
            ThrowIfNamedArgumentMatchesNoCandidate(candidateParameters, arguments);
        }

        return bestMatches.ToArray();
    }

    /// <summary>
    /// Evaluates the pending parameter defaults recorded by the callable
    /// binder for a winning overload (TS-P1-05). Defaults run at call
    /// time in the callable's lexical environment, left-to-right, with
    /// earlier bound parameters visible; later parameters are not in
    /// scope. Losing overload candidates never evaluate their defaults.
    /// The evaluated value passes through the same annotation/refinement
    /// conversion as an explicitly supplied argument.
    /// </summary>
    /// <summary>
    /// Whether this parameter's default still has to be evaluated, seeding the visible scope with
    /// already-bound values as it goes.
    /// </summary>
    /// <remarks>
    /// Three rules in one, and all three were written out once per surface: a rest parameter never
    /// takes a default; a parameter that was actually supplied contributes its value to the scope
    /// the *later* defaults are evaluated in, which is what makes `func f(a, b = $a * 2)` work;
    /// and only the pending ones are evaluated, so a losing overload candidate never runs a
    /// default's side effects (<c>TS-P1-24</c>).
    /// </remarks>
    private static bool NeedsPendingDefault(
        FunctionParameterDefinition parameter,
        HashSet<string> pendingNames,
        Dictionary<string, object?> locals,
        Dictionary<string, object?> visible)
    {
        if (parameter.IsRest)
        {
            return false;
        }

        if (pendingNames.Contains(parameter.Name))
        {
            return true;
        }

        if (locals.TryGetValue(parameter.Name, out var boundValue))
        {
            visible[parameter.Name] = boundValue;
        }

        return false;
    }

    /// <summary>
    /// True when a default failed purely because it referenced `$this`
    /// or `$super` where no instance is in scope. Matched on the
    /// unknown-variable diagnostic the evaluator raises; the
    /// constructor-default regression test locks this coupling so a
    /// change to that diagnostic surfaces loudly.
    /// </summary>
    private static bool ReferencesUnavailableSelf(ToshDiagnosticException failure)
    {
        if (failure.Diagnostics.Count == 0)
        {
            return false;
        }

        var diagnostic = failure.Diagnostics[0];
        if (!string.Equals(diagnostic.Code, "tosh.runtime.unknown_variable", StringComparison.Ordinal))
        {
            return false;
        }

        return diagnostic.Title.Contains("'this'", StringComparison.Ordinal)
            || diagnostic.Title.Contains("'super'", StringComparison.Ordinal);
    }

    private static HashSet<string> CollectPendingDefaultNames(
        IReadOnlyList<FunctionParameterDefinition> pendingDefaults)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pending in pendingDefaults)
        {
            names.Add(pending.Name);
        }

        return names;
    }

    private static IReadOnlyList<string> SplitTopLevelTypeArguments(string inner)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                result.Add(inner[start..i].Trim());
                start = i + 1;
            }
        }
        if (start <= inner.Length)
        {
            var tail = inner[start..].Trim();
            if (tail.Length > 0)
            {
                result.Add(tail);
            }
        }
        return result;
    }

    private async ValueTask<(
        bool Success,
        object? RefinedValue,
        ToshDiagnosticException? Failure)> TryApplyRefinementWithOptionalCoercionAsync(
        RefinementAnnotation? refinement,
        object? value,
        CancellationToken cancellationToken,
        string? baseTypeName = null)
    {
        if (refinement is null)
        {
            return (true, value, null);
        }

        object? currentValue = value;
        var guarded = await TryApplyGuardedRefinementCoercionAsync(
            refinement,
            currentValue,
            cancellationToken);
        if (!guarded.Success)
        {
            return (false, guarded.RefinedValue, guarded.Failure);
        }

        currentValue = guarded.RefinedValue;
        var predicate = await TryEvaluateRefinementPredicateAsync(
            refinement,
            currentValue,
            cancellationToken);
        if (!predicate.Completed)
        {
            return (false, currentValue, predicate.Failure);
        }

        if (predicate.Satisfied)
        {
            return (true, currentValue, null);
        }

        var fallbackClause = refinement.Clauses
            .OfType<RefinementCoerceClause>()
            .FirstOrDefault(static clause => clause.Guard is null);
        if (fallbackClause is null)
        {
            return (false, currentValue, null);
        }

        object? coerced;
        try
        {
            coerced = await EvaluateRefinementCoercerAsync(
                refinement,
                fallbackClause,
                currentValue,
                cancellationToken);
        }
        catch (ToshDiagnosticException exception)
        {
            return (false, currentValue, exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (
                false,
                currentValue,
                CreateExpressionDiagnostic(
                    refinement.SourceName,
                    refinement.SourceText,
                    fallbackClause.Coercer,
                    exception));
        }

        // `TOAST-0068`. The coercer's result is converted back to the refinement's base type
        // before the predicate is asked again.
        //
        // Without this a coercer can put another type in a refined slot and the predicate
        // will not notice, because a predicate tests the *value* and says nothing about its
        // type: `type TimeoutMs = int where (_ > 0 and _ <= 300000) coerce Math.Clamp(_, 0,
        // 300000)` accepted 999999 and left a `System.Double` in a slot declared `int`,
        // while the uncoerced path held an `Int32`. Two values, one annotation, two types.
        //
        // Only for a *named* refinement type, which is the case that has a declared base to
        // convert to. An inline `where` on a variable has already been converted against its
        // own annotation before reaching here.
        if (baseTypeName is not null)
        {
            if (!TryConvertAnnotatedValue(baseTypeName, coerced, out var reconverted))
            {
                return (false, coerced, null);
            }

            coerced = reconverted;
        }

        predicate = await TryEvaluateRefinementPredicateAsync(
            refinement,
            coerced,
            cancellationToken);
        if (!predicate.Completed)
        {
            return (false, coerced, predicate.Failure);
        }

        return (predicate.Satisfied, coerced, null);
    }

    private async ValueTask<(
        bool Success,
        object? RefinedValue,
        ToshDiagnosticException? Failure)> TryApplyGuardedRefinementCoercionAsync(
        RefinementAnnotation refinement,
        object? value,
        CancellationToken cancellationToken)
    {
        object? refinedValue = value;

        // Run every guarded `if … coerce` clause in order, threading the coerced
        // value forward so subsequent guards see the result of earlier coercions.
        // For example, given:
        //
        //   if (not (_ is int)) coerce ((round (Double.Parse(_)) 0) as int)
        //   if (_ < 0)          coerce (Math.Abs(_))
        //
        // an input of "-4.25" becomes -4 (first clause), then 4 (second clause).
        // (Stopping after the first match would skip the negativity fix-up.)
        foreach (var clause in refinement.Clauses
                     .OfType<RefinementCoerceClause>()
                     .Where(static clause => clause.Guard is not null))
        {
            try
            {
                if (!await EvaluateRefinementBooleanExpressionAsync(
                        refinement,
                        clause.Guard!,
                        refinedValue,
                        clause.Span,
                        "Refinement coercion guards",
                        cancellationToken,
                        useTruthiness: true))
                {
                    continue;
                }
            }
            catch (ToshDiagnosticException exception)
            {
                return (false, refinedValue, exception);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return (
                    false,
                    refinedValue,
                    CreateExpressionDiagnostic(
                        refinement.SourceName,
                        refinement.SourceText,
                        clause.Guard!,
                        exception));
            }

            try
            {
                refinedValue = await EvaluateRefinementCoercerAsync(
                    refinement,
                    clause,
                    refinedValue,
                    cancellationToken);
            }
            catch (ToshDiagnosticException exception)
            {
                return (false, refinedValue, exception);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return (
                    false,
                    refinedValue,
                    CreateExpressionDiagnostic(
                        refinement.SourceName,
                        refinement.SourceText,
                        clause.Coercer,
                        exception));
            }
        }

        return (true, refinedValue, null);
    }

    private async ValueTask<(
        bool Completed,
        bool Satisfied,
        ToshDiagnosticException? Failure)> TryEvaluateRefinementPredicateAsync(
        RefinementAnnotation refinement,
        object? value,
        CancellationToken cancellationToken)
    {
        try
        {
            return (
                true,
                await EvaluateRefinementPredicateAsync(
                    refinement,
                    value,
                    cancellationToken),
                null);
        }
        catch (ToshDiagnosticException exception)
        {
            return (false, false, exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (
                false,
                false,
                CreateExpressionDiagnostic(
                    refinement.SourceName,
                    refinement.SourceText,
                    GetPrimaryRefinementPredicate(refinement),
                    exception));
        }
    }

    private static string SubstituteTypeParametersInTypeName(string typeName, IReadOnlyDictionary<string, string> substitutions)
    {
        var result = typeName;
        foreach (var (parameter, replacement) in substitutions)
        {
            result = Regex.Replace(result, $@"\b{Regex.Escape(parameter)}\b", replacement);
        }

        return result;
    }

    /// <summary>
    /// Returns the bare type name without any '&lt;...&gt;' generic argument
    /// suffix. Used by interface/trait/base-class lookup paths so that
    /// references like 'IPoint&lt;int&gt;' resolve to the registered
    /// 'IPoint' definition.
    /// </summary>
    private static string StripGenericTypeArguments(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return typeName;
        }
        var lt = typeName.IndexOf('<');
        return lt < 0 ? typeName : typeName.Substring(0, lt);
    }

    private static string SubstituteTypeParametersInText(string text, IReadOnlyDictionary<string, string> substitutions)
    {
        var result = text;
        foreach (var (parameter, replacement) in substitutions)
        {
            result = Regex.Replace(result, $@"\b{Regex.Escape(parameter)}\b", replacement);
        }

        return result;
    }

    private static bool TrySplitGenericTypeName(
        string typeName,
        out string genericName,
        out IReadOnlyList<string> typeArguments)
    {
        genericName = string.Empty;
        typeArguments = Array.Empty<string>();

        var openIndex = typeName.IndexOf('<');
        if (openIndex <= 0 || !typeName.EndsWith('>'))
        {
            return false;
        }

        genericName = typeName[..openIndex].Trim();
        var argsText = typeName.Substring(openIndex + 1, typeName.Length - openIndex - 2);
        var arguments = SplitGenericTypeArguments(argsText);
        if (arguments.Count == 0)
        {
            return false;
        }

        typeArguments = arguments;
        return true;
    }

    private static IReadOnlyList<string> SplitGenericTypeArguments(string text)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = 0;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            switch (character)
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    arguments.Add(text[start..index].Trim());
                    start = index + 1;
                    break;
            }
        }

        var last = text[start..].Trim();
        if (last.Length > 0)
        {
            arguments.Add(last);
        }

        return arguments;
    }

    internal async IAsyncEnumerable<object?> ExecuteFunctionAsync(
        FunctionDefinition definition,
        CommandContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        using var executionFrame = ToshExecutionDepthGuard.Enter(
            LanguageRuntime.Options.MaxRecursionDepth,
            definition.Name,
            context.Invocation?.SourceName ?? definition.SourceName,
            context.Invocation?.SourceText ?? definition.SourceText,
            context.Invocation?.CommandSpan ?? definition.Span);
        using var capturedScopes = PushCapturedScopes(definition.CapturedScopes);
        var inputItems = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        var (locals, typeBindings) = BindFunctionParameters(definition, context, inputItems);

        // Collect parameters whose defaults must be evaluated because the
        // caller supplied neither a named nor a positional argument, then
        // apply them through the shared callable default binder so free
        // functions, methods, and constructors share one protocol
        // (TS-P1-05): call time, lexical scope, left-to-right, earlier
        // bound parameters visible.
        var namedArgNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positionalArgCount = 0;
        foreach (var arg in context.Arguments)
        {
            if (arg is NamedArgument named)
                namedArgNames.Add(named.Name);
            else
                positionalArgCount++;
        }

        List<FunctionParameterDefinition>? pendingDefaults = null;
        var posIdx = 0;
        for (var i = 0; i < definition.Parameters.Count; i++)
        {
            var param = definition.Parameters[i];
            if (param.IsRest) continue;

            var wasProvidedByName = namedArgNames.Contains(param.Name);
            var wasProvidedByPosition = !wasProvidedByName && posIdx < positionalArgCount;
            if (!wasProvidedByName) posIdx++;

            if (param.DefaultValue is not null && !wasProvidedByName && !wasProvidedByPosition)
            {
                (pendingDefaults ??= new List<FunctionParameterDefinition>()).Add(param);
            }
        }

        // The definition's captured scopes are already pushed above, so
        // the defaults see the same lexical environment the body will.
        await ApplyPendingParameterDefaultsAsync(
            definition.Parameters,
            locals,
            pendingDefaults,
            definition.SourceName,
            definition.SourceText,
            capturedScopes: null,
            callName: definition.Name,
            context.CancellationToken);

        // Only seed the function body with an "initial input" enumerable when
        // the caller actually piped data in (or the call site is inside a
        // pipeline). Otherwise EvaluatePipelineAsync would treat every first
        // statement in the body as pipelined (because initialInput != null),
        // which forces external commands into RedirectStandardInput=true mode
        // and breaks interactive children like `sudo pacman -Syu` that need a
        // real TTY for password / confirmation prompts.
        IAsyncEnumerable<object?>? initialInput = (inputItems.Count > 0 || context.IsPipelined)
            ? AsyncEnumerableExtensions.FromEnumerable(inputItems)
            : null;
        var firstCommandArguments = definition.IsCommandWrapper && definition.Parameters.Count == 0
            ? context.Arguments
            : null;

        _functionCallStack.Push(definition.Name);
        _functionArgumentsStack.Push(context.Arguments.ToArray());
        _functionInputStack.Push(inputItems.Count switch
        {
            0 => null,
            1 => inputItems[0],
            _ => inputItems.ToArray(),
        });

        // `TS-P1-07`. Every function streams, generator or not. The buffering branch
        // that used to sit here was labelled "Non-generator functions buffer all output
        // before yielding" and existed to work around a C# restriction rather than to
        // express a semantic: `return` raises a signal that has to be caught around the
        // whole enumeration, and `yield return` is not allowed inside a try-with-catch.
        // The generator branch had already solved that with a manual enumerator, so both
        // now share it.
        //
        // Measured before and after with an unbounded producer: `gen | first` used to run
        // the loop forever — 800,000 values in ten seconds without ever short-circuiting —
        // while `seq 1 20000000 | first` and `yes | first` both returned in 0.25s. A
        // user-defined function was the only thing in a pipeline that could not be
        // short-circuited.
        // `TOAST-0096`. Set for the duration of the body, so a `return` inside it can seed a
        // generic construction from the signature.
        //
        // **Before the iterator is built, not after.** An async iterator captures the ambient
        // `ExecutionContext`, and an `AsyncLocal` assigned after that capture is invisible to
        // the body the moment anything inside it awaits — so the annotation survived a `return`
        // at the top of a function and vanished after a statement like `$list.Add(x)`, which was
        // as arbitrary as it sounds to debug.
        var previousReturnAnnotation = _currentReturnAnnotation;
        _currentReturnAnnotation = definition.RawReturnTypeName;

        // `TOAST-0118`. The same bindings that convert the return value are what
        // `new A<T>` inside the body needs to resolve `T`.
        var previousTypeParameters = _currentTypeParameterBindings;
        _currentTypeParameterBindings = typeBindings is { Count: > 0 }
            ? typeBindings
            : previousTypeParameters;

        // Generator functions stream values as they are produced.
        // C# does not allow yield inside try-with-catch, so we use a manual enumerator.
        var enumerator = ExecuteBlockAsync(
            definition.SourceName,
            definition.SourceText,
            definition.Body,
            context.CancellationToken,
            locals,
            initialInput,
            firstCommandArguments)
            .GetAsyncEnumerator(context.CancellationToken);

        Exception? pendingException = null;
        IReadOnlyList<object?>? returnValues = null;

        try
        {
            while (true)
            {
                object? current;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        break;
                    current = enumerator.Current;
                }
                catch (ReturnSignalException signal)
                {
                    returnValues = signal.Values;
                    break;
                }
                catch (BreakSignalException signal)
                {
                    pendingException = CreateLoopControlDiagnostic(
                        definition.SourceName,
                        definition.SourceText,
                        signal.Span,
                        keyword: "break",
                        code: "tosh.runtime.break_outside_loop",
                        title: "'break' can only be used inside 'for', 'while', or 'each' blocks.");
                    break;
                }
                catch (ContinueSignalException signal)
                {
                    pendingException = CreateLoopControlDiagnostic(
                        definition.SourceName,
                        definition.SourceText,
                        signal.Span,
                        keyword: "continue",
                        code: "tosh.runtime.continue_outside_loop",
                        title: "'continue' can only be used inside 'for', 'while', or 'each' blocks.");
                    break;
                }

                // `TOSH-0010`. A return annotation describes the returned value. When the
                // function has a `return` of its own, these are the values it *emitted* on the
                // way there — output, not the result — and checking them against the
                // annotation refuses perfectly good functions that happen to log.
                //
                // A generator is the exception: its yielded values *are* its result, so they
                // are still checked.
                yield return definition.ReturnsExplicitly && !definition.IsGenerator
                    ? current
                    : ConvertFunctionReturnValue(definition, context, current, typeBindings);
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
            _functionInputStack.Pop();
            _functionArgumentsStack.Pop();
            _functionCallStack.Pop();
            _currentReturnAnnotation = previousReturnAnnotation;
            _currentTypeParameterBindings = previousTypeParameters;
        }

        if (pendingException is not null)
            throw pendingException;

        if (returnValues is not null)
        {
            foreach (var value in returnValues)
            {
                yield return ConvertFunctionReturnValue(definition, context, value, typeBindings);
            }
        }
    }

    /// <summary>
    /// <summary>
    /// Lightweight descriptor used by the generic-inference helpers
    /// so they don't depend on <see cref="FunctionDefinition"/>
    /// directly. Both free-function calls and class-method calls
    /// build one of these per call to share the same nested-shape
    /// unification, constraint validation, and diagnostic codes.
    /// </summary>
    internal sealed record GenericInferenceTarget(
        string OwnerLabel,
        IReadOnlyList<string> TypeParameters,
        IReadOnlyList<ToshTypeParameterConstraint>? TypeParameterConstraints);

    /// <summary>
    /// Infers / validates type-parameter bindings for one parameter.
    /// Walks the raw annotation tree alongside the runtime value's
    /// shape so nested forms (<c>list&lt;T&gt;</c>, <c>dict&lt;K,V&gt;</c>,
    /// <c>T[]</c>) contribute to inference, not just bare <c>T</c>.
    /// </summary>
    private void ApplyGenericBinding(
        FunctionDefinition definition,
        FunctionParameterDefinition parameter,
        object? value,
        CommandContext context,
        int argumentIndex,
        Dictionary<string, Type>? typeBindings)
    {
        if (typeBindings is null) return;
        if (definition.TypeParameters is not { Count: > 0 } typeParams) return;
        var raw = parameter.RawTypeName;
        if (raw is null) return;
        if (value is null) return;

        var target = new GenericInferenceTarget(
            definition.Name,
            typeParams,
            definition.TypeParameterConstraints);
        UnifyAnnotationWithValue(
            target,
            parameter.Name,
            raw,
            value,
            context,
            argumentIndex,
            typeBindings);
    }

    /// <summary>
    /// Method-level type-parameter inference for class methods.
    /// Mirrors <see cref="ApplyGenericBinding"/> but driven by a
    /// <see cref="ToshClassMethodDefinition"/> instead of a free
    /// function. Returns the populated binding table so callers can
    /// strict-validate parameter values and the return type against
    /// the inferred substitutions.
    /// </summary>
    internal Dictionary<string, Type>? InferMethodTypeBindings(
        ToshClassMethodDefinition method,
        IReadOnlyList<object?> argumentValues,
        CommandContext context,
        string ownerLabel)
    {
        if (method.TypeParameters is not { Count: > 0 } typeParams) return null;

        var typeBindings = new Dictionary<string, Type>(StringComparer.Ordinal);
        var target = new GenericInferenceTarget(
            ownerLabel,
            typeParams,
            method.TypeParameterConstraints);

        var count = Math.Min(method.Parameters.Count, argumentValues.Count);
        for (var i = 0; i < count; i++)
        {
            var parameter = method.Parameters[i];
            var raw = parameter.RawTypeName;
            if (raw is null) continue;
            var value = argumentValues[i];
            if (value is null) continue;

            UnifyAnnotationWithValue(
                target,
                parameter.Name,
                raw,
                value,
                context,
                argumentIndex: i,
                typeBindings);
        }

        return typeBindings.Count == 0 ? null : typeBindings;
    }

    /// <summary>
    /// Unify an annotation argument with either a concrete CLR type
    /// (when the runtime value's shape was reflected) or a sample
    /// element value (when we only saw it through enumeration).
    /// </summary>
    private void UnifyShapeArg(
        GenericInferenceTarget target,
        string parameterName,
        string annotation,
        object? sample,
        Type? clrType,
        CommandContext context,
        int argumentIndex,
        Dictionary<string, Type> typeBindings)
    {
        if (target.TypeParameters.Contains(annotation, StringComparer.Ordinal))
        {
            if (clrType is not null)
            {
                BindOrValidateBoundType(target, parameterName, annotation, clrType, context, argumentIndex, typeBindings);
            }
            else if (sample is not null)
            {
                BindOrValidateTypeParameter(target, parameterName, annotation, sample, context, argumentIndex, typeBindings);
            }
            return;
        }

        if (sample is not null)
        {
            UnifyAnnotationWithValue(target, parameterName, annotation, sample, context, argumentIndex, typeBindings);
        }
    }

    /// <summary>
    /// Tries to peek at an enumerable's element type and the first
    /// sample value (used for further nested unification).
    /// </summary>
    private static bool TryGetElementType(object? value, out Type elementType, out object? sample)
    {
        elementType = typeof(object);
        sample = null;
        if (value is null) return false;

        var clrType = value.GetType();
        if (clrType.IsArray)
        {
            elementType = clrType.GetElementType() ?? typeof(object);
            if (value is System.Collections.IEnumerable enumerable)
            {
                foreach (var first in enumerable) { sample = first; break; }
            }
            return true;
        }

        // IEnumerable<T> on the runtime type.
        foreach (var iface in clrType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = iface.GetGenericArguments()[0];
                if (value is System.Collections.IEnumerable enumerable)
                {
                    foreach (var first in enumerable) { sample = first; break; }
                }
                return true;
            }
        }

        // Loose enumerable (e.g. ArrayList, IList) — peek at the
        // first element to derive a runtime type.
        if (value is System.Collections.IEnumerable loose)
        {
            foreach (var first in loose) { sample = first; break; }
            if (sample is not null)
            {
                elementType = sample.GetType();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tries to peek at a dictionary's key/value types (and one
    /// sample of each, when available).
    /// </summary>
    private static bool TryGetDictionaryKVTypes(
        object? value,
        out Type keyType,
        out Type valueType,
        out object? keySample,
        out object? valueSample)
    {
        keyType = typeof(object);
        valueType = typeof(object);
        keySample = null;
        valueSample = null;
        if (value is null) return false;

        foreach (var iface in value.GetType().GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            {
                var args = iface.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
                if (value is System.Collections.IDictionary loose)
                {
                    foreach (System.Collections.DictionaryEntry e in loose)
                    {
                        keySample = e.Key;
                        valueSample = e.Value;
                        break;
                    }
                }
                return true;
            }
        }

        if (value is System.Collections.IDictionary plain)
        {
            foreach (System.Collections.DictionaryEntry e in plain)
            {
                keySample = e.Key;
                valueSample = e.Value;
                if (keySample is not null) keyType = keySample.GetType();
                if (valueSample is not null) valueType = valueSample.GetType();
                return true;
            }
            return true; // empty dict — leave types as object
        }

        return false;
    }

    /// <summary>
    /// First-bind / strict-validate path keyed on a runtime value
    /// (uses <c>value.GetType()</c> as the inferred CLR type).
    /// </summary>
    private void BindOrValidateTypeParameter(
        GenericInferenceTarget target,
        string parameterName,
        string typeParameterName,
        object value,
        CommandContext context,
        int argumentIndex,
        Dictionary<string, Type> typeBindings)
    {
        BindOrValidateBoundType(
            target, parameterName, typeParameterName,
            value.GetType(), context, argumentIndex, typeBindings,
            mismatchValue: value);
    }

    /// <summary>
    /// First-bind / strict-validate path keyed on a CLR type
    /// (used when the value's element type was reflected, not
    /// observed directly).
    /// </summary>
    private void BindOrValidateBoundType(
        GenericInferenceTarget target,
        string parameterName,
        string typeParameterName,
        Type clrType,
        CommandContext context,
        int argumentIndex,
        Dictionary<string, Type> typeBindings,
        object? mismatchValue = null)
    {
        if (typeBindings.TryGetValue(typeParameterName, out var bound))
        {
            var ok = mismatchValue is not null
                ? bound.IsInstanceOfType(mismatchValue)
                : bound.IsAssignableFrom(clrType);
            if (!ok)
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.generic_argument_type_mismatch",
                    title: $"'{target.OwnerLabel}' inferred type parameter '{typeParameterName}' as '{bound.Name}', but argument '{parameterName}' is '{clrType.Name}'.",
                    argumentIndex: argumentIndex,
                    label: $"'{parameterName}' must be a {bound.Name} ({typeParameterName} was bound earlier in this call)");
            }
            return;
        }

        // First binding — verify any `where` constraints declared
        // for this type parameter.
        EnforceTypeParameterConstraints(
            target,
            typeParameterName,
            clrType,
            typeBindings,
            context,
            argumentIndex,
            subject: $"'{parameterName}' (CLR {clrType.Name})");

        typeBindings[typeParameterName] = clrType;
    }

    /// <summary>
    /// Checks every <c>where</c> clause declared for one type parameter against the type it
    /// is being bound to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `TOAST-0055`. Lifted out of the inference path because that is not the only way a type
    /// parameter gets a binding: an explicit call-site type argument — <c>F&lt;string&gt;(…)</c>
    /// — seeds it directly, and seeding skipped straight past this. Inference reached it, so
    /// <c>F("a")</c> was refused while <c>F&lt;string&gt;("a")</c> was not, for the same
    /// function and the same constraint.
    /// </para>
    /// <para>
    /// <paramref name="subject"/> is what the label blames, because the two callers blame
    /// different things: an argument whose type implied the binding, or the type argument the
    /// caller wrote.
    /// </para>
    /// </remarks>
    private void EnforceTypeParameterConstraints(
        GenericInferenceTarget target,
        string typeParameterName,
        Type clrType,
        Dictionary<string, Type> typeBindings,
        CommandContext context,
        int? argumentIndex,
        string subject)
    {
        if (target.TypeParameterConstraints is not { Count: > 0 } constraints)
        {
            return;
        }

        foreach (var clause in constraints)
        {
            if (!string.Equals(clause.TypeParameter, typeParameterName, StringComparison.Ordinal)) continue;
            foreach (var constraintName in clause.ConstraintNames)
            {
                if (ToshTypeParameterConstraintRegistry.TryGet(constraintName, out var predicate))
                {
                    if (predicate(clrType)) continue;
                    throw context.CreateDiagnostic(
                        code: "tosh.runtime.generic_constraint_failed",
                        title: $"'{target.OwnerLabel}' requires '{typeParameterName}' to satisfy '{constraintName}', but '{clrType.Name}' does not.",
                        argumentIndex: argumentIndex,
                        label: $"{subject} does not satisfy '{constraintName}'");
                }

                // Phase 4.5 — substitute type-parameter references
                // in the constraint name (e.g. `IComparable<T>`
                // becomes `IComparable<Int32>` once T binds to int).
                var resolvedConstraintName = SubstituteTypeParametersInAnnotation(
                    constraintName, target, typeBindings, typeParameterName, clrType);
                var constraintType = TryResolveTypeName(resolvedConstraintName);
                if (constraintType is not null)
                {
                    if (!constraintType.IsAssignableFrom(clrType))
                    {
                        throw context.CreateDiagnostic(
                            code: "tosh.runtime.generic_constraint_failed",
                            title: $"'{target.OwnerLabel}' requires '{typeParameterName}' to satisfy '{constraintName}', but '{clrType.Name}' does not.",
                            argumentIndex: argumentIndex,
                            label: $"{subject} is not assignable to '{constraintName}'");
                    }
                    continue;
                }

                // `TOAST-0055`. Recognised-but-unresolvable stays conservative; a name
                // that resolves to nothing is a typo, and silently dropping the
                // constraint is the one outcome the author cannot see.
                if (!IsRecognisedConstraintName(constraintName))
                {
                    throw context.CreateDiagnostic(
                        code: "tosh.runtime.unknown_type_constraint",
                        title: $"'{target.OwnerLabel}' constrains '{typeParameterName}' to "
                            + $"'{constraintName}', which is not a known constraint.",
                        argumentIndex: argumentIndex,
                        label: $"'{constraintName}' names nothing");
                }
            }
        }
    }

    private static void EnsureReservedBindingName(string name)
    {
        if (RuntimeNamespaceUtilities.IsReservedRuntimeNamespaceName(name))
        {
            throw new InvalidOperationException($"'{name}' is a reserved runtime namespace.");
        }
    }

    private object? ConvertFunctionReturnValue(
        FunctionDefinition definition,
        CommandContext context,
        object? value)
    {
        return ConvertFunctionReturnValue(definition, context, value, typeBindings: null);
    }

    private object? ConvertFunctionReturnValue(
        FunctionDefinition definition,
        CommandContext context,
        object? value,
        Dictionary<string, Type>? typeBindings)
    {
        // Generic return type bound at call site: validate against the
        // inferred CLR type rather than the (erased) annotation.
        if (typeBindings is { Count: > 0 } &&
            definition.RawReturnTypeName is { } rawReturn &&
            definition.TypeParameters is { Count: > 0 } typeParams &&
            typeParams.Contains(rawReturn, StringComparer.Ordinal))
        {
            if (typeBindings.TryGetValue(rawReturn, out var bound) && value is not null && !bound.IsInstanceOfType(value))
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.generic_return_type_mismatch",
                    title: $"Function '{definition.Name}' inferred '{rawReturn}' as '{bound.Name}', but returned a '{value.GetType().Name}'.",
                    label: $"return value must be a {bound.Name} (T was bound from the arguments)",
                    span: definition.Span);
            }
            return value;
        }

        if (definition.ReturnTypeName is null)
        {
            return value;
        }

        // `TOAST-0046`. `void` and `nothing` are the same bound type and now behave the
        // same, which they did not: `-> void` tried to convert the value to the CLR's
        // `System.Void` and failed with "could not be converted to 'void'", while
        // `-> nothing` was not a type the runtime resolver had heard of at all.
        //
        // Neither is a conversion question. A void function declares that it produces
        // nothing, so the only thing to check is whether it did.
        if (IsNothingAnnotation(definition.ReturnTypeName))
        {
            if (value is null)
            {
                return null;
            }

            throw context.CreateDiagnostic(
                code: "tosh.runtime.void_function_produced_value",
                title: $"Function '{definition.Name}' returns 'void' but produced a value.",
                label: $"'{definition.Name}' declares that it produces nothing",
                span: definition.Span);
        }

        try
        {
            return ConvertAnnotatedValue(
                definition.ReturnTypeName,
                refinement: null,
                value,
                definition.Span,
                definition.SourceName,
                definition.SourceText,
                $"{definition.Name} return");
        }
        catch (ToshDiagnosticException exception)
        {
            if (!exception.Diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code, "tosh.runtime.annotation_conversion_failed", StringComparison.Ordinal)))
            {
                throw;
            }
        }

        throw context.CreateDiagnostic(
            code: "tosh.runtime.return_type_conversion_failed",
            title: ToastMessages.FunctionReturnConversionFailure(
                definition.Name,
                definition.ReturnTypeName),
            label: ToastMessages.FunctionReturnConversionLabel(definition.ReturnTypeName),
            span: definition.Span);
    }


}
