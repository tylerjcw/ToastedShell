using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using Tosh.Core;
using Tosh.Core.Commands;
using Tosh.Language.Commands;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed class ToshEngine : IShellEvaluator
{
    private readonly record struct EvaluatedCommandArgument(ArgumentSyntax Syntax, object? Value);

    private readonly Stack<LexicalScope> _scopes = new();
    private readonly Stack<string> _functionCallStack = new();
    private readonly Stack<string> _scriptNameStack = new();
    private readonly Stack<IReadOnlyList<object?>> _scriptArgumentsStack = new();
    private readonly Stack<IReadOnlyList<object?>> _functionArgumentsStack = new();
    private readonly Stack<object?> _functionInputStack = new();
    private readonly Dictionary<string, ToshRequiredScriptArtifact> _requiredScripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _currentlyRequiring = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NativeLibraryBinding> _requiredNativeLibraries = new(StringComparer.OrdinalIgnoreCase);
    private int _commandEventDepth;
    private readonly ToshRuntimeNamespace _toshNamespace;
    private readonly ShellEnvironmentNamespace _environmentNamespace;

    public ToshEngine(ToshRuntime? runtime = null)
    {
        Runtime = runtime ?? ToshRuntime.CreateDefault();
        Runtime.BlockExecutor = new EngineBlockExecutor(this);
        Runtime.Evaluator = this;
        Runtime.EventSenderFactory = CreateEventSender;
        _toshNamespace = new ToshRuntimeNamespace(this);
        _environmentNamespace = new ShellEnvironmentNamespace();
        if (!Runtime.Commands.TryGet("source", out _))
        {
            Runtime.Commands.Register(new SourceCommand(this));
        }
    }

    public ToshRuntime Runtime { get; }

    internal ShellEventSender CreateEventSender()
    {
        var function = _functionCallStack.Count > 0 ? _functionCallStack.Peek() : null;
        var script = _scriptNameStack.Count > 0 ? _scriptNameStack.Peek() : null;
        return new ShellEventSender(function, script, Line: null);
    }

    internal string GetCurrentScriptPath() => _scriptNameStack.Count > 0 ? _scriptNameStack.Peek() : string.Empty;

    internal IReadOnlyList<object?> GetCurrentScriptArguments() => _scriptArgumentsStack.Count > 0 ? _scriptArgumentsStack.Peek() : Runtime.InvocationArguments;

    internal string GetCurrentFunctionName() => _functionCallStack.Count > 0 ? _functionCallStack.Peek() : string.Empty;

    internal IReadOnlyList<object?> GetCurrentFunctionArguments() => _functionArgumentsStack.Count > 0 ? _functionArgumentsStack.Peek() : Array.Empty<object?>();

    internal object? GetCurrentFunctionInput() => _functionInputStack.Count > 0 ? _functionInputStack.Peek() : null;

    internal ITypeResolver CreateScopedTypeResolver()
    {
        if (_scopes.Count == 0)
        {
            return Runtime.TypeResolver;
        }

        return new ScopedTypeResolver(Runtime.TypeResolver, _scopes.ToArray());
    }

    public ParseResult Parse(string source, string sourceName = "<input>") => ToshParser.Parse(source, sourceName);

    public IAsyncEnumerable<object?> EvaluateAsync(string source, CancellationToken cancellationToken = default)
    {
        return EvaluateAsync(source, "<input>", cancellationToken);
    }

    public IAsyncEnumerable<object?> EvaluateAsync(string source, string sourceName, CancellationToken cancellationToken = default)
    {
        var parseResult = Parse(source, sourceName);

        if (parseResult.Diagnostics.Count > 0)
        {
            throw new ToshDiagnosticException(parseResult.Diagnostics
                .Select(diagnostic => new ToshDiagnostic(
                    Code: diagnostic.Code,
                    Title: diagnostic.Title,
                    SourceName: parseResult.SourceName,
                    SourceText: parseResult.SourceText,
                    Span: diagnostic.Span,
                    Label: diagnostic.Label,
                    Help: diagnostic.Help))
                .ToArray());
        }

        return EvaluateAsync(parseResult, cancellationToken);
    }

    public async Task<IReadOnlyList<object?>> ExecuteToListAsync(string source, CancellationToken cancellationToken = default)
    {
        return await ExecuteToListAsync(source, "<input>", cancellationToken);
    }

    public async Task<IReadOnlyList<object?>> ExecuteToListAsync(string source, string sourceName, CancellationToken cancellationToken = default)
    {
        return await AsyncEnumerableExtensions.ToListAsync(EvaluateAsync(source, sourceName, cancellationToken), cancellationToken);
    }

    public IAsyncEnumerable<object?> ExecuteScriptFileAsync(
        string path,
        IReadOnlyList<object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteScriptFileAsync(path, arguments, isolateScope: true, cancellationToken);
    }

    internal IAsyncEnumerable<object?> ExecuteScriptFileAsync(
        string path,
        IReadOnlyList<object?>? arguments,
        bool isolateScope,
        CancellationToken cancellationToken)
    {
        return ExecuteScriptFileCoreAsync(path, arguments ?? Array.Empty<object?>(), isolateScope, cancellationToken);
    }

    private async IAsyncEnumerable<object?> ExecuteScriptFileCoreAsync(
        string path,
        IReadOnlyList<object?> arguments,
        bool isolateScope,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resolvedPath = PathUtilities.ResolvePath(Runtime.CurrentDirectory, path);

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"Script file '{resolvedPath}' was not found.", resolvedPath);
        }

        var source = await File.ReadAllTextAsync(resolvedPath, cancellationToken);

        await foreach (var value in ExecuteScriptAsync(source, resolvedPath, arguments, isolateScope, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return value;
        }
    }

    private async IAsyncEnumerable<object?> ExecuteScriptAsync(
        string source,
        string sourceName,
        IReadOnlyList<object?> arguments,
        bool isolateScope,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var scriptArgs = arguments.ToArray();
        IDisposable scopeFrame = ScopeFrames.Empty;

        if (isolateScope)
        {
            scopeFrame = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal));
        }

        try
        {
            _scriptArgumentsStack.Push(scriptArgs);
            await foreach (var value in EvaluateAsync(source, sourceName, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return value;
            }
        }
        finally
        {
            _scriptArgumentsStack.Pop();
            scopeFrame.Dispose();
        }
    }

    private IAsyncEnumerable<object?> EvaluateAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return EvaluateParseResultAsync(parseResult, cancellationToken);
    }

    private async IAsyncEnumerable<object?> EvaluateParseResultAsync(
        ParseResult parseResult,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var isTopLevel = _commandEventDepth == 0;
        var values = new List<object?>();
        var stopwatch = isTopLevel ? System.Diagnostics.Stopwatch.StartNew() : null;

        // Raise CommandStarting for top-level user input only
        if (isTopLevel && Runtime.Events.GetHandlers(BuiltInEventNames.CommandStarting).Count > 0)
        {
            _commandEventDepth++;
            try
            {
                var sender = Runtime.EventSenderFactory?.Invoke()
                    ?? new ShellEventSender(Function: null, Script: null, Line: null);
                var inputText = parseResult.SourceText.Trim();
                var startingEvent = new CommandStartingEvent(
                    inputText, [], inputText, sender);
                await Runtime.Events.RaiseAsync(startingEvent, cancellationToken);

                if (startingEvent.Cancelled)
                {
                    yield break;
                }
            }
            finally
            {
                _commandEventDepth--;
            }
        }

        _commandEventDepth++;
        _scriptNameStack.Push(parseResult.SourceName);
        var exitCode = 0;
        try
        {
            await foreach (var value in EvaluateStatementAsync(
                               parseResult.SourceName,
                               parseResult.SourceText,
                               parseResult.Statement,
                               cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                values.Add(value);
            }
        }
        catch (ReturnSignalException signal)
        {
            values.AddRange(signal.Values);
            UpdateLastResultIfAny(signal.Values);
        }
        catch (BreakSignalException signal)
        {
            throw CreateLoopControlDiagnostic(
                parseResult.SourceName,
                parseResult.SourceText,
                signal.Span,
                keyword: "break",
                code: "tosh::runtime::break_outside_loop",
                title: "'break' can only be used inside 'for', 'while', or 'each' blocks.");
        }
        catch (ContinueSignalException signal)
        {
            throw CreateLoopControlDiagnostic(
                parseResult.SourceName,
                parseResult.SourceText,
                signal.Span,
                keyword: "continue",
                code: "tosh::runtime::continue_outside_loop",
                title: "'continue' can only be used inside 'for', 'while', or 'each' blocks.");
        }
        catch (ThrowSignalException signal)
        {
            exitCode = 1;
            throw CreateThrownValueDiagnostic(parseResult.SourceName, parseResult.SourceText, signal);
        }
        catch (Exception) when (exitCode == 0)
        {
            exitCode = 1;
            throw;
        }
        finally
        {
            _scriptNameStack.Pop();
            _commandEventDepth--;

            // Raise CommandCompleted for top-level user input only
            if (isTopLevel && Runtime.Events.GetHandlers(BuiltInEventNames.CommandCompleted).Count > 0)
            {
                stopwatch?.Stop();
                _commandEventDepth++;
                try
                {
                    var sender = Runtime.EventSenderFactory?.Invoke()
                        ?? new ShellEventSender(Function: null, Script: null, Line: null);
                    var inputText = parseResult.SourceText.Trim();
                    var completedEvent = new CommandCompletedEvent(
                        inputText, exitCode, stopwatch?.Elapsed ?? TimeSpan.Zero,
                        values.Count > 0 ? values[^1] : null, sender);
                    await Runtime.Events.RaiseAsync(completedEvent, cancellationToken);
                }
                finally
                {
                    _commandEventDepth--;
                }
            }
        }

        if (parseResult.Statement is not ScriptStatementSyntax)
        {
            UpdateLastResultIfAny(values);
        }

        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }
    }

    private IAsyncEnumerable<object?> EvaluateStatementAsync(
        string sourceName,
        string sourceText,
        StatementSyntax statement,
        CancellationToken cancellationToken)
    {
        return statement switch
        {
            ScriptStatementSyntax script => EvaluateScriptStatementAsync(sourceName, sourceText, script, cancellationToken),
            PipelineStatementSyntax pipeline => pipeline.Pipeline.IsBackground
                ? EvaluateBackgroundPipelineAsync(sourceName, sourceText, pipeline, cancellationToken)
                : EvaluatePipelineWithRedirectionAsync(sourceName, sourceText, pipeline.Pipeline, cancellationToken),
            VariableDeclarationStatementSyntax declaration => EvaluateVariableDeclarationAsync(sourceName, sourceText, declaration, cancellationToken),
            DestructuringDeclarationStatementSyntax destructuring => EvaluateDestructuringDeclarationAsync(sourceName, sourceText, destructuring, cancellationToken),
            AllocStatementSyntax alloc => EvaluateAllocStatementAsync(sourceName, sourceText, alloc, cancellationToken),
            UsingStatementSyntax @using => EvaluateUsingStatementAsync(sourceName, sourceText, @using, cancellationToken),
            RequireStatementSyntax require => EvaluateRequireStatementAsync(sourceName, sourceText, require, cancellationToken),
            BindStatementSyntax bind => EvaluateBindStatementAsync(sourceName, sourceText, bind, cancellationToken),
            ReturnStatementSyntax @return => EvaluateReturnStatementAsync(sourceName, sourceText, @return, cancellationToken),
            ThrowStatementSyntax @throw => EvaluateThrowStatementAsync(sourceName, sourceText, @throw, cancellationToken),
            BreakStatementSyntax @break => EvaluateBreakStatementAsync(@break),
            ContinueStatementSyntax @continue => EvaluateContinueStatementAsync(@continue),
            VariableAssignmentStatementSyntax assignment => EvaluateVariableAssignmentAsync(sourceName, sourceText, assignment, cancellationToken),
            MemberAssignmentStatementSyntax assignment => EvaluateMemberAssignmentAsync(sourceName, sourceText, assignment, cancellationToken),
            FunctionDefinitionStatementSyntax function => EvaluateFunctionDefinitionAsync(sourceName, sourceText, function, cancellationToken),
            ClassDefinitionStatementSyntax @class => EvaluateClassDefinitionAsync(sourceName, sourceText, @class, cancellationToken),
            ModuleDefinitionStatementSyntax module => EvaluateModuleDefinitionAsync(sourceName, sourceText, module, cancellationToken),
            EnumDefinitionStatementSyntax @enum => EvaluateEnumDefinitionAsync(sourceName, sourceText, @enum, cancellationToken),
            RecordDefinitionStatementSyntax record => EvaluateRecordDefinitionAsync(sourceName, sourceText, record, cancellationToken),
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

    private async IAsyncEnumerable<object?> EvaluateScriptStatementAsync(
        string sourceName,
        string sourceText,
        ScriptStatementSyntax script,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        PreRegisterTypeDefinitions(script.Statements);

        foreach (var statement in script.Statements)
        {
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

    private async IAsyncEnumerable<object?> EvaluateBackgroundPipelineAsync(
        string sourceName,
        string sourceText,
        PipelineStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<object?>? initialInput = null;
        var processStages = new List<ShellJobProcessSpec>();
        var redirections = new List<ShellJobRedirectionSpec>();
        var stages = statement.Pipeline.Stages;
        var stageIndex = 0;

        if (stages.Count > 0 && stages[0] is ExpressionPipelineStageSyntax initialExpression)
        {
            initialInput = await AsyncEnumerableExtensions.ToListAsync(
                ExecuteExpressionStageAsync(sourceName, sourceText, initialExpression, cancellationToken),
                cancellationToken);
            stageIndex = 1;
        }

        if (stageIndex >= stages.Count)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::background_pipeline_requires_command",
                Title: "Background pipelines require at least one external command stage.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: "add an external command after the input expression"));
        }

        for (; stageIndex < stages.Count; stageIndex++)
        {
            var stage = stages[stageIndex];

            if (stage is not CommandSyntax commandSyntax)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh::runtime::background_pipeline_not_supported",
                    Title: "Background jobs currently support an optional input expression followed by external command stages only.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: stage.Span,
                    Label: "this stage is not an external command"));
            }

            var command = ResolveCommand(sourceName, sourceText, commandSyntax);

            if (command is not ExternalProcessCommand externalCommand)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh::runtime::background_command_must_be_external",
                    Title: "Background jobs currently require external command stages.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: commandSyntax.Span,
                    Label: $"'{commandSyntax.Name}' is not being launched as a native process"));
            }

            IReadOnlyList<object?> arguments;

            try
            {
                var evaluatedArguments = await EvaluateCommandArgumentsAsync(sourceName, sourceText, command, commandSyntax, cancellationToken);
                arguments = ExpandImplicitGlobArguments(command, evaluatedArguments);
            }
            catch (ToshDiagnosticException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw CreateCommandDiagnostic(sourceName, sourceText, commandSyntax, exception);
            }

            processStages.Add(new ShellJobProcessSpec(externalCommand.ResolvedPath, arguments));
        }

        if (statement.Pipeline.Redirections is { Count: > 0 })
        {
            foreach (var redirection in statement.Pipeline.Redirections)
            {
                var targetPath = await EvaluateArgumentAsync(sourceName, sourceText, redirection.Target, cancellationToken);
                var path = ResolveRedirectionTargetPath(sourceName, sourceText, redirection, targetPath);
                redirections.Add(new ShellJobRedirectionSpec(
                    path,
                    redirection.Stream switch
                    {
                        RedirectionStream.Output => ShellJobRedirectionStream.Output,
                        RedirectionStream.Error => ShellJobRedirectionStream.Error,
                        RedirectionStream.OutputThenError => ShellJobRedirectionStream.OutputThenError,
                        _ => ShellJobRedirectionStream.ErrorThenOutput,
                    },
                    redirection.Mode == RedirectionMode.Append
                        ? ShellJobRedirectionMode.Append
                        : ShellJobRedirectionMode.Truncate));
            }
        }

        var commandText = ExtractSourceSnippet(sourceText, statement.Span);
        var job = Runtime.RegisterJob(
            ShellJob.StartExternalPipeline(
                Runtime.AllocateJobId(),
                commandText,
                Runtime.CurrentDirectory,
                processStages,
                initialInput,
                redirections));

        Runtime.SetLastResult(job.ToInfo());
        Runtime.SetLastExitCode(0);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateVariableDeclarationAsync(
        string sourceName,
        string sourceText,
        VariableDeclarationStatementSyntax declaration,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, declaration.Name, declaration.Span, "reserved runtime namespace");

        var binding = declaration.Value is null
            ? new VariableBinding(null, ReplayAsPipeline: false, IsAllocatedOnly: true)
            : await EvaluateVariableBindingAsync(sourceName, sourceText, declaration.Value, cancellationToken);

        if (declaration.TypeName is not null)
        {
            var value = binding.Value;
            if (!binding.IsAllocatedOnly)
            {
                if (!TryConvertAnnotatedValue(declaration.TypeName, value, out var converted))
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh::runtime::variable_type_mismatch",
                        Title: $"Cannot assign {(value?.GetType().Name ?? "null")} to variable '{declaration.Name}' of type {declaration.TypeName}.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: declaration.Span,
                        Label: $"expected {declaration.TypeName}, got {(value?.GetType().Name ?? "null")}",
                        Help: "ensure the value matches the declared ToSh or CLR type."));
                }

                binding = binding with { Value = converted };
            }
        }

        if (declaration.Name == "_" && TryGetVariableBinding("_", out _))
        {
            Runtime.Error.WriteLine("Warning: redeclaring '_' shadows an existing binding. Use a different name if this value matters.");
        }

        DeclareVariable(declaration.Name, binding, declaration.Modifier);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateDestructuringDeclarationAsync(
        string sourceName,
        string sourceText,
        DestructuringDeclarationStatementSyntax destructuring,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var binding = await EvaluateVariableBindingAsync(sourceName, sourceText, destructuring.Value, cancellationToken);
        var value = binding.Value;

        switch (destructuring.Pattern)
        {
            case ArrayDestructuringPatternSyntax arrayPattern:
                {
                    object?[]? array = value switch
                    {
                        object?[] a => a,
                        IReadOnlyList<object?> list => list.ToArray(),
                        IEnumerable enumerable when value is not string => enumerable.Cast<object?>().ToArray(),
                        _ => null,
                    };

                    if (array is null)
                    {
                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh::runtime::destructuring_requires_array",
                            Title: "Array destructuring requires an array or list value.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: destructuring.Span,
                            Label: $"got {(value?.GetType().Name ?? "null")} instead of an array"));
                    }

                    for (var i = 0; i < arrayPattern.Names.Count; i++)
                    {
                        var name = arrayPattern.Names[i];
                        var elementValue = i < array.Length ? array[i] : null;
                        DeclareVariable(name, new VariableBinding(elementValue, ReplayAsPipeline: false, IsAllocatedOnly: false), destructuring.Modifier);
                    }

                    break;
                }

            case RecordDestructuringPatternSyntax recordPattern:
                {
                    IDictionary<string, object?>? dict = value switch
                    {
                        IDictionary<string, object?> d => d,
                        IShellRecordObject record => record.GetMembers().ToDictionary(m => m.Key, m => m.Value, StringComparer.OrdinalIgnoreCase),
                        _ => null,
                    };

                    if (dict is null)
                    {
                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh::runtime::destructuring_requires_record",
                            Title: "Record destructuring requires a record or dictionary value.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: destructuring.Span,
                            Label: $"got {(value?.GetType().Name ?? "null")} instead of a record"));
                    }

                    foreach (var name in recordPattern.Names)
                    {
                        dict.TryGetValue(name, out var memberValue);
                        DeclareVariable(name, new VariableBinding(memberValue, ReplayAsPipeline: false, IsAllocatedOnly: false), destructuring.Modifier);
                    }

                    break;
                }
        }

        yield break;
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
                EvaluatePipelineAsync(sourceName, sourceText, statement.Value, cancellationToken),
                cancellationToken);

            if (values.Count != 1)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh::runtime::alloc_requires_single_value",
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

        var context = new CommandContext(Runtime, AsyncEnumerableExtensions.Empty<object?>(), [allocationSpecification], cancellationToken, ScopedTypeResolver: CreateScopedTypeResolver());
        var size = NativeCommandUtilities.ResolveAllocationSize(context, allocationSpecification, 0);

        if (size < 0)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::alloc_negative_size",
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
                Code: "tosh::runtime::using_export_not_supported",
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
            if (Runtime.TypeResolver is not IImportingTypeResolver importingResolver)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh::runtime::using_not_supported",
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
                Code: "tosh::runtime::shy_using_requires_scope",
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

    private async IAsyncEnumerable<object?> EvaluateRequireStatementAsync(
        string sourceName,
        string sourceText,
        RequireStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            if (statement.IsNative)
            {
                if (statement.Imports.Count > 0)
                {
                    throw new InvalidOperationException("Selective require imports are not supported for native libraries.");
                }

                var moduleName = statement.Alias ?? GetDefaultNativeModuleName(statement.Target);
                EnsureNativeModuleAvailable(sourceName, statement.Target, moduleName, statement.Modifier);
            }
            else
            {
                var requirement = ResolveRequirement(statement.Target, GetExecutionDirectory(sourceName));

                switch (requirement.Kind)
                {
                    case RequireTargetKind.Script:
                        {
                            if (!_requiredScripts.TryGetValue(requirement.CacheKey, out var artifact))
                            {
                                if (!_currentlyRequiring.Add(requirement.CacheKey))
                                {
                                    throw new InvalidOperationException(
                                        $"Circular require detected: '{requirement.CacheKey}' is already being loaded.");
                                }

                                try
                                {
                                    var moduleSource = await File.ReadAllTextAsync(requirement.ResolvedPath, cancellationToken);
                                    artifact = await ExecuteRequiredScriptAsync(moduleSource, requirement.ResolvedPath, cancellationToken);
                                    _requiredScripts[requirement.CacheKey] = artifact;
                                }
                                finally
                                {
                                    _currentlyRequiring.Remove(requirement.CacheKey);
                                }
                            }

                            ImportRequiredArtifact(artifact, statement);
                            break;
                        }

                    case RequireTargetKind.Assembly:
                        {
                            if (statement.Imports.Count > 0)
                            {
                                throw new InvalidOperationException("Selective require imports are only supported for .tosh files.");
                            }

                            if (!Runtime.LoadedModules.Add(requirement.CacheKey))
                            {
                                break;
                            }

                            AssemblyLoadContext.Default.LoadFromAssemblyPath(requirement.ResolvedPath);
                            break;
                        }

                    case RequireTargetKind.Project:
                        {
                            if (statement.Imports.Count > 0)
                            {
                                throw new InvalidOperationException("Selective require imports are only supported for .tosh files.");
                            }

                            if (!Runtime.LoadedModules.Add(requirement.CacheKey))
                            {
                                break;
                            }

                            var assemblyPath = await BuildProjectAndResolveAssemblyPathAsync(requirement.ResolvedPath, cancellationToken);
                            AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
                            break;
                        }

                    default:
                        throw new InvalidOperationException($"Unsupported require target kind '{requirement.Kind}'.");
                }
            }
        }
        catch (ToshDiagnosticException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::require_failed",
                Title: exception.Message,
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: $"while requiring '{statement.Target}'"));
        }

        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateBindStatementAsync(
        string sourceName,
        string sourceText,
        BindStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (statement.NativeTarget is not null)
        {
            EnsureNativeModuleAvailable(sourceName, statement.NativeTarget, statement.ModuleName, DeclarationModifier.Default);
        }

        if (!TryGetModule(statement.ModuleName, out var module) ||
            module.NativeLibraryBinding is null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::bind_target_not_native_module",
                Title: $"'{statement.ModuleName}' is not a native library module.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: $"load a native library with 'require native ... as {statement.ModuleName}' first"));
        }

        foreach (var function in statement.Functions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parameters = function.Parameters
                .Select(parameter => new NativeFunctionParameterDefinition(
                    parameter.Name,
                    parameter.TypeName ?? string.Empty,
                    ResolveNativeInteropParameterType(parameter.TypeName, parameter.PassingMode, sourceName, sourceText, parameter.Span, $"parameter '{parameter.Name}'"),
                    parameter.PassingMode))
                .ToArray();
            var returnType = ResolveNativeInteropReturnType(function.ReturnTypeName, sourceName, sourceText, function.Span);
            var callingConvention = ResolveNativeCallingConvention(function.CallingConventionName, sourceName, sourceText, function.Span);
            var command = new NativeFunctionCommand(
                statement.ModuleName,
                function.Name,
                function.SymbolName,
                module.NativeLibraryBinding,
                parameters,
                returnType,
                callingConvention);
            module.SetCommand(command);
        }

        yield break;
    }

    private void EnsureNativeModuleAvailable(
        string sourceName,
        string nativeTarget,
        string moduleName,
        DeclarationModifier modifier)
    {
        var requirement = ResolveNativeRequirement(nativeTarget, GetExecutionDirectory(sourceName));

        if (!_requiredNativeLibraries.TryGetValue(requirement.CacheKey, out var binding))
        {
            var handle = NativeLibrary.Load(requirement.ResolvedPath);
            binding = new NativeLibraryBinding(
                requirement.ResolvedPath,
                requirement.CacheKey,
                handle,
                new ModuleExportTable());
            _requiredNativeLibraries[requirement.CacheKey] = binding;
        }

        var module = new ToshModuleObject(this, moduleName, binding.Exports)
        {
            NativeLibraryBinding = binding,
        };
        DeclareModule(moduleName, module, modifier);
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
                EvaluatePipelineAsync(sourceName, sourceText, statement.Value, cancellationToken),
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
                EvaluatePipelineAsync(sourceName, sourceText, statement.Value, cancellationToken),
                cancellationToken);
            value = values.Count switch
            {
                0 => new CommandFailure("An error was thrown."),
                1 => values[0],
                _ => values.ToArray(),
            };
        }

        throw new ThrowSignalException(statement.Span, value);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private async IAsyncEnumerable<object?> EvaluateVariableAssignmentAsync(
        string sourceName,
        string sourceText,
        VariableAssignmentStatementSyntax assignment,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, assignment.Name, assignment.Span, "reserved runtime namespace");

        var value = await EvaluateVariableBindingAsync(sourceName, sourceText, assignment.Value, cancellationToken);

        if (!TryGetVariableBinding(assignment.Name, out var existingBinding))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::unknown_variable",
                Title: $"Variable '{assignment.Name}' has not been declared.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: assignment.Span,
                Label: $"declare '{assignment.Name}' with 'var' before assigning to it",
                Help: $"try 'var {assignment.Name} = ...' the first time you bind this variable."));
        }

        if (assignment.Operator == "??=")
        {
            if (!existingBinding.IsAllocatedOnly && existingBinding.Value is not null)
            {
                yield break;
            }
        }
        else if (assignment.Operator != "=")
        {
            if (existingBinding.IsAllocatedOnly)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh::runtime::compound_assignment_requires_value",
                    Title: $"Variable '{assignment.Name}' does not have a value yet.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: assignment.Span,
                    Label: $"assign '{assignment.Name}' before using '{assignment.Operator}'"));
            }

            var newValue = ApplyCompoundAssignment(existingBinding.Value, assignment.Operator, value.Value);
            value = new VariableBinding(
                newValue,
                ReplayAsPipeline: ShouldReplayAsPipeline(newValue),
                IsAllocatedOnly: false);
        }

        if (!TryAssignVariable(assignment.Name, value))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::unknown_variable",
                Title: $"Variable '{assignment.Name}' has not been declared.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: assignment.Span,
                Label: $"declare '{assignment.Name}' with 'var' before assigning to it",
                    Help: $"try 'var {assignment.Name} = ...' the first time you bind this variable."));
        }
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateMemberAssignmentAsync(
        string sourceName,
        string sourceText,
        MemberAssignmentStatementSyntax assignment,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var binding = await EvaluateVariableBindingAsync(sourceName, sourceText, assignment.Value, cancellationToken);

        if (!TryDecomposeMemberAssignmentTarget(assignment.Target, out var rootExpression, out var memberPath))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::invalid_member_assignment_target",
                Title: "Assignments to members require a member path target.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: assignment.Target.Span,
                Label: "use a target like '$person.Name'"));
        }

        var target = await EvaluateOrMaterializeRootTargetAsync(sourceName, sourceText, rootExpression, cancellationToken);
        var valueToAssign = binding.Value;

        try
        {
            if (assignment.Operator == "??=")
            {
                var currentValue = Runtime.ObjectAccessor.GetValue(target, memberPath);
                if (currentValue is not null)
                {
                    yield break;
                }
            }
            else if (assignment.Operator != "=")
            {
                var currentValue = Runtime.ObjectAccessor.GetValue(target, memberPath);
                valueToAssign = ApplyCompoundAssignment(currentValue, assignment.Operator, binding.Value);
            }

            Runtime.ObjectAccessor.SetValue(target, memberPath, valueToAssign);
        }
        catch (Exception exception) when (exception is not ToshDiagnosticException)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::member_assignment_failed",
                Title: exception.Message,
                SourceName: sourceName,
                SourceText: sourceText,
                Span: assignment.Target.Span,
                Label: "while assigning to this member"));
        }

        yield break;
    }

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
            function.Span);

        var functionCommand = new FunctionCommand(this, definition);
        DeclareCommand(functionCommand, function.Modifier);

        if (function.HandlesEvent is not null)
        {
            RegisterEventHandler(functionCommand, function.HandlesEvent, function.HandlerPriority, function.IsOnceHandler, function.WhenGuard, sourceName, sourceText);
        }

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
        TextSpan span)
    {
        var duplicateParameters = parameters
            .GroupBy(parameter => parameter.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateParameters is not null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::duplicate_function_parameter",
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
                .Select(parameter => new FunctionParameterDefinition(parameter.Name, parameter.TypeName, parameter.IsOptional, parameter.IsRest, parameter.Span))
                .ToArray(),
            returnTypeName,
            body,
            isCommandWrapper,
            sourceName,
            sourceText,
            span,
            CaptureVisibleScopes());
    }

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
                        Runtime,
                        EmptyAsyncEnumerable(),
                        new object?[] { shellEvent },
                        cancellationToken);

                    await foreach (var value in functionCommand.ExecuteAsync(context))
                    {
                        result = value;
                    }

                    return result;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Runtime.Error.WriteLineAsync(
                        $"Event handler '{functionCommand.Name}' for '{eventName}' failed: {ex.Message}");
                    return null;
                }
            },
            priority,
            once,
            capturedScopes?.Cast<object>().ToArray());

        Runtime.Events.Register(handler);
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

            return IsTruthyValue(lastValue);
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

    private static bool IsTruthyValue(object? value)
    {
        if (value is null)
        {
            return false;
        }

        if (TypeConversion.TryConvert(value, typeof(bool), out var converted) && converted is bool boolean)
        {
            return boolean;
        }

        return true;
    }

    private static async IAsyncEnumerable<object?> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateClassDefinitionAsync(
        string sourceName,
        string sourceText,
        ClassDefinitionStatementSyntax @class,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, @class.Name, @class.Span, "reserved runtime namespace");

        var duplicateProperties = @class.Members
            .OfType<ClassPropertyMemberSyntax>()
            .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateProperties is not null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::duplicate_class_property",
                Title: $"Class '{@class.Name}' defines property '{duplicateProperties.Key}' more than once.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: duplicateProperties.First().Span,
                Label: $"'{duplicateProperties.Key}' is declared multiple times"));
        }

        foreach (var parameter in @class.PrimaryConstructorParameters)
        {
            EnsureBindingNameIsNotReserved(sourceName, sourceText, parameter.Name, parameter.Span, "reserved runtime namespace");
        }

        foreach (var constructorParameter in @class.Members
                     .OfType<ClassConstructorMemberSyntax>()
                     .SelectMany(member => member.Parameters))
        {
            EnsureBindingNameIsNotReserved(sourceName, sourceText, constructorParameter.Name, constructorParameter.Span, "reserved runtime namespace");
        }

        foreach (var methodParameter in @class.Members
                     .OfType<ClassMethodMemberSyntax>()
                     .SelectMany(member => member.Method.Parameters))
        {
            EnsureBindingNameIsNotReserved(sourceName, sourceText, methodParameter.Name, methodParameter.Span, "reserved runtime namespace");
        }

        var runtimeProperties = @class.Members
            .OfType<ClassPropertyMemberSyntax>()
            .Select(property => new ToshClassPropertyDefinition(
                property.Name,
                property.TypeName,
                property.Initializer,
                property.GetterBody,
                property.SetterBody,
                property.IsShy,
                property.Span))
            .ToArray();

        var runtimeMethods = @class.Members
            .OfType<ClassMethodMemberSyntax>()
            .Select(method => new ToshClassMethodDefinition(
                method.Method.Name,
                method.Method.Parameters
                    .Select(parameter => new FunctionParameterDefinition(parameter.Name, parameter.TypeName, parameter.IsOptional, parameter.IsRest, parameter.Span))
                    .ToArray(),
                method.Method.ReturnTypeName,
                method.Method.Body,
                method.IsStatic,
                method.IsShy,
                sourceName,
                sourceText,
                method.Span,
                CaptureVisibleScopes()))
            .ToArray();

        var runtimeConstructors = @class.Members
            .OfType<ClassConstructorMemberSyntax>()
            .Select(constructor => new ToshClassConstructorDefinition(
                constructor.Parameters
                    .Select(parameter => new FunctionParameterDefinition(parameter.Name, parameter.TypeName, parameter.IsOptional, parameter.IsRest, parameter.Span))
                    .ToArray(),
                constructor.Body,
                sourceName,
                sourceText,
                constructor.Span,
                CaptureVisibleScopes()))
            .ToArray();

        var definition = new ToshClassDefinition(
            this,
            @class.Name,
            @class.PrimaryConstructorParameters
                .Select(parameter => new FunctionParameterDefinition(parameter.Name, parameter.TypeName, parameter.IsOptional, parameter.IsRest, parameter.Span))
                .ToArray(),
            runtimeProperties,
            runtimeMethods,
            runtimeConstructors,
            sourceName,
            sourceText,
            @class.Span,
            CaptureVisibleScopes());

        DeclareType(@class.Name, definition, @class.Modifier);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateModuleDefinitionAsync(
        string sourceName,
        string sourceText,
        ModuleDefinitionStatementSyntax module,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, module.Name, module.Span, "reserved runtime namespace");

        var moduleScope = new LexicalScope(
            new Dictionary<string, object?>(StringComparer.Ordinal),
            isModuleScope: true,
            exportDeclarationsByDefault: true);

        using (PushScope(moduleScope))
        {
            await foreach (var _ in ExecuteBlockAsync(sourceName, sourceText, module.Body, cancellationToken, pushNewScope: false)
                               .WithCancellation(cancellationToken))
            {
            }
        }

        var moduleObject = new ToshModuleObject(this, module.Name, moduleScope.Exports ?? new ModuleExportTable());
        var effectiveModifier = module.Modifier;

        if (effectiveModifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek().IsModuleScope)
        {
            effectiveModifier = DeclarationModifier.Export;
        }

        DeclareModule(module.Name, moduleObject, effectiveModifier);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateEnumDefinitionAsync(
        string sourceName,
        string sourceText,
        EnumDefinitionStatementSyntax @enum,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, @enum.Name, @enum.Span, "reserved runtime namespace");

        var underlyingType = string.IsNullOrWhiteSpace(@enum.UnderlyingTypeName)
            ? typeof(int)
            : ResolveTypeName(@enum.UnderlyingTypeName!)
                ?? throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh::runtime::unknown_enum_underlying_type",
                    Title: $"Enum '{@enum.Name}' uses unknown underlying type '{@enum.UnderlyingTypeName}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @enum.Span,
                    Label: $"the type '{@enum.UnderlyingTypeName}' could not be resolved"));

        var members = new List<ToshEnumValue>();
        long nextNumericValue = 0;
        var canAutoIncrement = IsNumericEnumUnderlyingType(underlyingType);

        foreach (var member in @enum.Members)
        {
            object? rawValue;

            if (member.Value is null)
            {
                if (!canAutoIncrement)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh::runtime::enum_member_value_required",
                        Title: $"Enum member '{@enum.Name}.{member.Name}' requires an explicit value.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: member.Span,
                        Label: $"'{underlyingType.Name}' values cannot be auto-incremented"));
                }

                rawValue = Convert.ChangeType(nextNumericValue, underlyingType);
            }
            else if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, member.Value, cancellationToken) is { Matched: true } raw)
            {
                rawValue = raw.Value;
            }
            else
            {
                var values = await AsyncEnumerableExtensions.ToListAsync(
                    EvaluatePipelineAsync(sourceName, sourceText, member.Value, cancellationToken),
                    cancellationToken);
                rawValue = values.Count switch
                {
                    0 => null,
                    1 => values[0],
                    _ => throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh::runtime::enum_member_requires_single_value",
                        Title: $"Enum member '{@enum.Name}.{member.Name}' must resolve to exactly one value.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: member.Span,
                        Label: "this enum member initializer produced multiple values")),
                };
            }

            if (!TypeConversion.TryConvert(rawValue, underlyingType, out var converted))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh::runtime::enum_member_conversion_failed",
                    Title: $"Enum member '{@enum.Name}.{member.Name}' could not be converted to '{underlyingType.Name}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: member.Span,
                    Label: $"the value does not match '{underlyingType.Name}'"));
            }

            members.Add(new ToshEnumValue(default!, member.Name, converted));

            if (canAutoIncrement)
            {
                nextNumericValue = Convert.ToInt64(converted, System.Globalization.CultureInfo.InvariantCulture) + 1;
            }
        }

        var definition = new ToshEnumDefinition(
            @enum.Name,
            @enum.UnderlyingTypeName,
            underlyingType,
            members,
            sourceName,
            sourceText,
            @enum.Span);

        var fixedMembers = definition.Members
            .Select(member => new ToshEnumValue(definition, member.Name, member.UnderlyingValue))
            .ToArray();
        definition = new ToshEnumDefinition(
            @enum.Name,
            @enum.UnderlyingTypeName,
            underlyingType,
            fixedMembers,
            sourceName,
            sourceText,
            @enum.Span);

        DeclareType(@enum.Name, definition, @enum.Modifier);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateRecordDefinitionAsync(
        string sourceName,
        string sourceText,
        RecordDefinitionStatementSyntax record,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, record.Name, record.Span, "reserved runtime namespace");

        var duplicateFields = record.Fields
            .GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateFields is not null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::duplicate_record_field",
                Title: $"Record '{record.Name}' defines field '{duplicateFields.Key}' more than once.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: duplicateFields.First().Span,
                Label: $"'{duplicateFields.Key}' is declared multiple times"));
        }

        var definition = new ToshRecordDefinition(
            this,
            record.Name,
            record.Fields
                .Select(field => new ToshRecordFieldDefinition(field.Name, field.TypeName, field.DefaultValue, field.IsOptional, field.Span))
                .ToArray(),
            sourceName,
            sourceText,
            record.Span,
            CaptureVisibleScopes());

        DeclareType(record.Name, definition, record.Modifier);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateEventDefinitionAsync(
        string sourceName,
        string sourceText,
        EventDefinitionStatementSyntax @event,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, @event.Name, @event.Span, "reserved runtime namespace");

        var definition = new ToshEventDefinition(
            this,
            @event.Name,
            @event.Fields
                .Select(field => new ToshEventFieldDefinition(field.Name, field.TypeName, field.DefaultValue, field.Span))
                .ToArray(),
            @event.IsRequired,
            @event.IsLocal,
            sourceName,
            sourceText,
            @event.Span,
            CaptureVisibleScopes());

        if (definition.IsRequired)
        {
            Runtime.Events.MarkRequired(definition.Name);
        }

        if (definition.IsLocal && _scopes.Count > 0)
        {
            _scopes.Peek().LocalEventNames.Add(definition.Name);
        }

        DeclareVariable(definition.Name, new VariableBinding(definition, ReplayAsPipeline: false, IsAllocatedOnly: false), @event.Modifier);
        yield break;
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

        await foreach (var item in EvaluatePipelineAsync(sourceName, sourceText, statement.Source, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            foreach (var current in ShellIterationUtilities.ExpandIterationItems(item))
            {
                var iterationValues = new List<object?>();
                var shouldBreak = false;
                var shouldContinue = false;

                try
                {
                    await foreach (var value in ExecuteBlockAsync(
                                       sourceName,
                                       sourceText,
                                       statement.Body,
                                       cancellationToken,
                                       new Dictionary<string, object?>(StringComparer.Ordinal)
                                       {
                                           [statement.VariableName] = current,
                                           ["_"] = current,
                                       })
                                       .WithCancellation(cancellationToken))
                    {
                        iterationValues.Add(value);
                    }
                }
                catch (ContinueSignalException)
                {
                    shouldContinue = true;
                }
                catch (BreakSignalException)
                {
                    shouldBreak = true;
                }

                foreach (var value in iterationValues)
                {
                    yield return value;
                }

                if (shouldBreak)
                {
                    yield break;
                }

                if (shouldContinue)
                {
                    continue;
                }
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
            var iterationValues = new List<object?>();
            var shouldBreak = false;
            var shouldContinue = false;

            try
            {
                await foreach (var value in ExecuteBlockAsync(sourceName, sourceText, statement.Body, cancellationToken)
                                   .WithCancellation(cancellationToken))
                {
                    iterationValues.Add(value);
                }
            }
            catch (ContinueSignalException)
            {
                shouldContinue = true;
            }
            catch (BreakSignalException)
            {
                shouldBreak = true;
            }

            foreach (var value in iterationValues)
            {
                yield return value;
            }

            if (shouldBreak)
            {
                yield break;
            }

            if (shouldContinue)
            {
                continue;
            }
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
            var iterationValues = new List<object?>();
            var shouldBreak = false;
            var shouldContinue = false;

            try
            {
                await foreach (var value in ExecuteBlockAsync(sourceName, sourceText, statement.Body, cancellationToken)
                                   .WithCancellation(cancellationToken))
                {
                    iterationValues.Add(value);
                }
            }
            catch (ContinueSignalException)
            {
                shouldContinue = true;
            }
            catch (BreakSignalException)
            {
                shouldBreak = true;
            }

            foreach (var value in iterationValues)
            {
                yield return value;
            }

            if (shouldBreak)
            {
                yield break;
            }

            if (shouldContinue)
            {
                continue;
            }
        }
    }

    private async IAsyncEnumerable<object?> EvaluateTryStatementAsync(
        string sourceName,
        string sourceText,
        TryStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tryValues = new List<object?>();
        var catchValues = new List<object?>();
        var finallyValues = new List<object?>();
        ShellControlFlowException? pendingControlFlow = null;
        Exception? pendingFailure = null;
        var caughtException = false;

        try
        {
            await foreach (var value in ExecuteBlockAsync(sourceName, sourceText, statement.TryBlock, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                tryValues.Add(value);
            }
        }
        catch (ReturnSignalException signal)
        {
            pendingControlFlow = signal;
        }
        catch (BreakSignalException signal)
        {
            pendingControlFlow = signal;
        }
        catch (ContinueSignalException signal)
        {
            pendingControlFlow = signal;
        }
        catch (Exception exception) when (statement.CatchClause is not null)
        {
            caughtException = true;
            var catchLocals = new Dictionary<string, object?>(StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(statement.CatchClause.VariableName))
            {
                EnsureBindingNameIsNotReserved(sourceName, sourceText, statement.CatchClause.VariableName!, statement.CatchClause.Span, "reserved runtime namespace");
                catchLocals[statement.CatchClause.VariableName!] = CreateCaughtErrorValue(exception);
            }

            await foreach (var value in ExecuteBlockAsync(
                               sourceName,
                               sourceText,
                               statement.CatchClause.Body,
                               cancellationToken,
                               catchLocals)
                               .WithCancellation(cancellationToken))
            {
                catchValues.Add(value);
            }
        }
        catch (Exception exception)
        {
            pendingFailure = exception;
        }
        finally
        {
            if (statement.FinallyBlock is not null)
            {
                await foreach (var value in ExecuteBlockAsync(sourceName, sourceText, statement.FinallyBlock, cancellationToken)
                                   .WithCancellation(cancellationToken))
                {
                    finallyValues.Add(value);
                }
            }
        }

        if (pendingControlFlow is not null)
        {
            throw pendingControlFlow;
        }

        if (pendingFailure is not null)
        {
            throw pendingFailure;
        }

        if (caughtException)
        {
            foreach (var value in tryValues)
            {
                yield return value;
            }

            foreach (var value in catchValues)
            {
                yield return value;
            }
        }
        else
        {
            foreach (var value in tryValues)
            {
                yield return value;
            }
        }

        foreach (var value in finallyValues)
        {
            yield return value;
        }
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
            var matchValue = await EvaluateArgumentAsync(sourceName, sourceText, @case.MatchExpression, cancellationToken);

            if (OperatorEvaluator.AreEqual(switchValue, matchValue))
            {
                blockToExecute = @case.Body;
                break;
            }
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
            EvaluatePipelineAsync(sourceName, sourceText, pipeline, cancellationToken),
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

    private async IAsyncEnumerable<object?> EvaluatePipelineWithRedirectionAsync(
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        IAsyncEnumerable<object?>? initialInput = null,
        IReadOnlyList<object?>? firstCommandArguments = null)
    {
        if (pipeline.Redirections is null or { Count: 0 })
        {
            await foreach (var value in EvaluatePipelineAsync(sourceName, sourceText, pipeline, cancellationToken, initialInput, firstCommandArguments)
                               .WithCancellation(cancellationToken))
            {
                yield return value;
            }

            yield break;
        }

        var resolvedRedirections = new List<ResolvedPipelineRedirection>();

        foreach (var redirection in pipeline.Redirections)
        {
            var targetPath = await EvaluateArgumentAsync(sourceName, sourceText, redirection.Target, cancellationToken);
            var path = ResolveRedirectionTargetPath(sourceName, sourceText, redirection, targetPath);
            resolvedRedirections.Add(new ResolvedPipelineRedirection(path, redirection.Stream, redirection.Mode));
        }

        var bufferedPlans = CreateBufferedPipelineRedirectionPlans(resolvedRedirections);
        var disposableWriters = new List<TextWriter>();
        var outputTargets = new List<TextWriter>();
        var errorTargets = new List<TextWriter>();
        TextWriter? originalOutput = null;
        TextWriter? originalError = null;

        try
        {
            foreach (var plan in bufferedPlans.Values)
            {
                if (plan.HasOutput)
                {
                    outputTargets.Add(plan.OutputWriter);
                }

                if (plan.HasError)
                {
                    errorTargets.Add(plan.ErrorWriter);
                }
            }

            foreach (var redirection in resolvedRedirections)
            {
                if (bufferedPlans.ContainsKey(redirection.Path))
                {
                    continue;
                }

                var mode = redirection.Mode == RedirectionMode.Append ? FileMode.Append : FileMode.Create;

                FileStream stream;
                try
                {
                    stream = File.Open(redirection.Path, mode, FileAccess.Write, FileShare.Read);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        "TOSH400",
                        $"Cannot open '{redirection.Path}' for redirection: {exception.Message}"));
                }

                var writer = TextWriter.Synchronized(new StreamWriter(stream, Encoding.UTF8));
                disposableWriters.Add(writer);

                if (RedirectionIncludesOutput(redirection.Stream))
                {
                    outputTargets.Add(writer);
                }

                if (RedirectionIncludesError(redirection.Stream))
                {
                    errorTargets.Add(writer);
                }
            }

            if (outputTargets.Count > 0)
            {
                originalOutput = Runtime.Output;
                Runtime.Output = CreateCompositeWriter(outputTargets);
            }

            if (errorTargets.Count > 0)
            {
                originalError = Runtime.Error;
                Runtime.Error = CreateCompositeWriter(errorTargets);
            }

            var hasOutputRedirection = outputTargets.Count > 0;

            await foreach (var value in EvaluatePipelineAsync(sourceName, sourceText, pipeline, cancellationToken, initialInput, firstCommandArguments)
                               .WithCancellation(cancellationToken))
            {
                if (hasOutputRedirection)
                {
                    var text = value switch
                    {
                        ShellTextLine line => line.Text,
                        _ => Runtime.Formatter.Format(value),
                    };

                    await Runtime.Output.WriteLineAsync(text);
                    await Runtime.Output.FlushAsync(cancellationToken);
                }
                else
                {
                    // No stdout redirection — pass values through (e.g., only stderr was redirected)
                    yield return value;
                }
            }
        }
        finally
        {
            if (originalOutput is not null)
            {
                Runtime.Output = originalOutput;
            }

            if (originalError is not null)
            {
                Runtime.Error = originalError;
            }

            await FlushBufferedPipelineRedirectionsAsync(bufferedPlans.Values, cancellationToken);

            foreach (var writer in disposableWriters)
            {
                await writer.DisposeAsync();
            }
        }
    }

    private static TextWriter CreateCompositeWriter(IReadOnlyList<TextWriter> writers)
        => writers.Count == 1 ? writers[0] : new CompositeTextWriter(writers);

    private static bool RedirectionIncludesOutput(RedirectionStream stream)
        => stream is RedirectionStream.Output or RedirectionStream.OutputThenError or RedirectionStream.ErrorThenOutput;

    private static bool RedirectionIncludesError(RedirectionStream stream)
        => stream is RedirectionStream.Error or RedirectionStream.OutputThenError or RedirectionStream.ErrorThenOutput;

    private static Dictionary<string, BufferedPipelineRedirectionPlan> CreateBufferedPipelineRedirectionPlans(
        IReadOnlyList<ResolvedPipelineRedirection> redirections)
    {
        return redirections
            .GroupBy(static redirection => redirection.Path, StringComparer.OrdinalIgnoreCase)
            .Where(static group =>
                group.Count() > 1 ||
                group.Any(static redirection => redirection.Stream is RedirectionStream.OutputThenError or RedirectionStream.ErrorThenOutput))
            .ToDictionary(
                static group => group.Key,
                static group => new BufferedPipelineRedirectionPlan(group.Key, group.ToArray()),
                StringComparer.OrdinalIgnoreCase);
    }

    private static async Task FlushBufferedPipelineRedirectionsAsync(
        IEnumerable<BufferedPipelineRedirectionPlan> plans,
        CancellationToken cancellationToken)
    {
        foreach (var plan in plans)
        {
            var outputText = plan.OutputBuffer.ToString();
            var errorText = plan.ErrorBuffer.ToString();

            foreach (var redirection in plan.Redirections)
            {
                var text = GetRedirectionContent(redirection.Stream, outputText, errorText);
                var fileMode = redirection.Mode == RedirectionMode.Append ? FileMode.Append : FileMode.Create;

                FileStream stream;
                try
                {
                    stream = File.Open(redirection.Path, fileMode, FileAccess.Write, FileShare.Read);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        "TOSH400",
                        $"Cannot open '{redirection.Path}' for redirection: {exception.Message}"));
                }

                await using var writer = new StreamWriter(stream, Encoding.UTF8);

                if (text.Length > 0)
                {
                    await writer.WriteAsync(text.AsMemory(), cancellationToken);
                }

                await writer.FlushAsync(cancellationToken);
            }
        }
    }

    private static string GetRedirectionContent(
        RedirectionStream stream,
        string outputText,
        string errorText)
        => stream switch
        {
            RedirectionStream.Output => outputText,
            RedirectionStream.Error => errorText,
            RedirectionStream.OutputThenError => outputText + errorText,
            RedirectionStream.ErrorThenOutput => outputText + errorText,
            _ => string.Empty,
        };

    private sealed record ResolvedPipelineRedirection(
        string Path,
        RedirectionStream Stream,
        RedirectionMode Mode);

    private sealed class BufferedPipelineRedirectionPlan
    {
        public BufferedPipelineRedirectionPlan(
            string path,
            IReadOnlyList<ResolvedPipelineRedirection> redirections)
        {
            Path = path;
            Redirections = redirections;
            OutputWriter = TextWriter.Synchronized(new StringWriter(OutputBuffer));
            ErrorWriter = TextWriter.Synchronized(new StringWriter(ErrorBuffer));
            HasOutput = redirections.Any(static redirection => RedirectionIncludesOutput(redirection.Stream));
            HasError = redirections.Any(static redirection => RedirectionIncludesError(redirection.Stream));
        }

        public string Path { get; }

        public IReadOnlyList<ResolvedPipelineRedirection> Redirections { get; }

        public StringBuilder OutputBuffer { get; } = new();

        public StringBuilder ErrorBuffer { get; } = new();

        public TextWriter OutputWriter { get; }

        public TextWriter ErrorWriter { get; }

        public bool HasOutput { get; }

        public bool HasError { get; }
    }

    private string ResolveRedirectionTargetPath(
        string sourceName,
        string sourceText,
        RedirectionSyntax redirection,
        object? targetPath)
    {
        if (targetPath is null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::redirection_target_null",
                Title: "Redirection target cannot be null.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: redirection.Span,
                Label: "this redirection target evaluated to null"));
        }

        IReadOnlyList<string> resolvedPaths = targetPath switch
        {
            FileSystemInfo fileSystemInfo => [fileSystemInfo.FullName],
            FileSystemEntry entry => [entry.FullName],
            string text => ShellPathArguments.Expand(Runtime.CurrentDirectory, text),
            _ => [PathUtilities.ResolvePath(Runtime.CurrentDirectory, targetPath.ToString() ?? string.Empty)],
        };

        if (resolvedPaths.Count == 1)
        {
            return resolvedPaths[0];
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh::runtime::redirection_target_not_single_path",
            Title: "Redirection targets must resolve to exactly one path.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: redirection.Span,
            Label: "this target resolved to multiple paths",
            Help: "use a single file path or quote the pattern if you meant a literal name."));
    }

    private IAsyncEnumerable<object?> EvaluatePipelineAsync(
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        CancellationToken cancellationToken,
        IAsyncEnumerable<object?>? initialInput = null,
        IReadOnlyList<object?>? firstCommandArguments = null,
        PipelineExitStatusTracker? pipelineExitStatusTracker = null)
    {
        var ownsTracker = pipelineExitStatusTracker is null;
        pipelineExitStatusTracker ??= new PipelineExitStatusTracker(Runtime.Config.Shell.Pipefail);
        IAsyncEnumerable<object?> current = initialInput ?? AsyncEnumerableExtensions.Empty<object?>();
        IReadOnlyList<object?>? pendingFirstCommandArguments = firstCommandArguments;
        var isPipelined = pipeline.Stages.Count > 1 || initialInput is not null;

        foreach (var stage in pipeline.Stages)
        {
            current = stage switch
            {
                ExpressionPipelineStageSyntax expressionStage => ExecuteExpressionStageAsync(
                    sourceName,
                    sourceText,
                    expressionStage,
                    cancellationToken),
                CommandSyntax commandSyntax => ExecuteCommandSyntaxAsync(
                    sourceName,
                    sourceText,
                    commandSyntax,
                    current,
                    pendingFirstCommandArguments,
                    isPipelined,
                    pipelineExitStatusTracker,
                    cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported pipeline stage syntax: {stage.GetType().Name}."),
            };

            if (stage is CommandSyntax && pendingFirstCommandArguments is not null)
            {
                pendingFirstCommandArguments = null;
            }
        }

        return FinalizePipelineExitCodeAsync(current, pipelineExitStatusTracker, ownsTracker, cancellationToken);
    }

    private async IAsyncEnumerable<object?> FinalizePipelineExitCodeAsync(
        IAsyncEnumerable<object?> current,
        PipelineExitStatusTracker tracker,
        bool ownsTracker,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in current.WithCancellation(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            if (ownsTracker && tracker.HasExitCodes)
            {
                var exitCode = tracker.GetFinalExitCode();
                Runtime.SetLastExitCode(exitCode);

                if (exitCode != 0 && Runtime.Config.Shell.ExitOnError)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh::runtime::nonzero_exit_code",
                        Title: $"Command exited with code {exitCode}.",
                        Help: "A command in the pipeline returned a non-zero exit code while Shell.ExitOnError is enabled. " +
                              "Set $tosh.Config.Shell.ExitOnError = false to disable this behavior."));
                }
            }
        }
    }

    private async IAsyncEnumerable<object?> ExecuteExpressionStageAsync(
        string sourceName,
        string sourceText,
        ExpressionPipelineStageSyntax expressionStage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (expressionStage.Expression is VariableReferenceArgumentSyntax variableReference &&
            TryGetVariableBinding(variableReference.Name, out var binding) &&
            binding.ReplayAsPipeline &&
            binding.Value is IEnumerable enumerable &&
            binding.Value is not string)
        {
            foreach (var item in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            yield break;
        }

        object? value;

        try
        {
            value = await EvaluateArgumentAsync(sourceName, sourceText, expressionStage.Expression, cancellationToken);
        }
        catch (ToshDiagnosticException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateExpressionDiagnostic(sourceName, sourceText, expressionStage.Expression, exception);
        }

        if (ShouldReplayRuntimeNamespaceCollectionAccess(expressionStage.Expression) &&
            ShouldReplayAsPipeline(value) &&
            value is IEnumerable replayable &&
            value is not string)
        {
            foreach (var item in replayable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            yield break;
        }

        // Expand ranges into their individual values.
        if (value is ToshRange range)
        {
            foreach (var item in range.Enumerate())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            yield break;
        }

        yield return value;
    }

    private async IAsyncEnumerable<object?> ExecuteCommandSyntaxAsync(
        string sourceName,
        string sourceText,
        CommandSyntax commandSyntax,
        IAsyncEnumerable<object?> input,
        IReadOnlyList<object?>? additionalArguments,
        bool isPipelined,
        PipelineExitStatusTracker? pipelineExitStatusTracker,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var command = ResolveCommand(sourceName, sourceText, commandSyntax);

        IReadOnlyList<object?> arguments;

        try
        {
            var evaluatedArguments = await EvaluateCommandArgumentsAsync(sourceName, sourceText, command, commandSyntax, cancellationToken);
            arguments = ExpandImplicitGlobArguments(command, evaluatedArguments);

            if (additionalArguments is { Count: > 0 })
            {
                arguments = arguments.Concat(additionalArguments).ToArray();
            }
        }
        catch (ToshDiagnosticException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateCommandDiagnostic(sourceName, sourceText, commandSyntax, exception);
        }

        var invocation = new CommandInvocation(
            sourceName,
            sourceText,
            commandSyntax.Name,
            commandSyntax.Span,
            commandSyntax.Arguments.Select(argument => argument.Span).ToArray());
        var context = new CommandContext(Runtime, input, arguments, cancellationToken, invocation, isPipelined, CreateScopedTypeResolver(), pipelineExitStatusTracker);

        if (Runtime.Config.Shell.Trace)
        {
            var traceArgs = string.Join(" ", arguments.Select(FormatTraceArgument));
            var traceLine = string.IsNullOrEmpty(traceArgs)
                ? $"+ {commandSyntax.Name}"
                : $"+ {commandSyntax.Name} {traceArgs}";
            await Runtime.Error.WriteLineAsync(traceLine);
        }

        var exitCodeCountBefore = pipelineExitStatusTracker?.ExitCodeCount ?? 0;

        await using var enumerator = command.ExecuteAsync(context).GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            object? item;

            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                item = enumerator.Current;
            }
            catch (ToshDiagnosticException)
            {
                throw;
            }
            catch (ReturnSignalException)
            {
                throw;
            }
            catch (BreakSignalException)
            {
                throw;
            }
            catch (ContinueSignalException)
            {
                throw;
            }
            catch (ThrowSignalException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw CreateCommandDiagnostic(sourceName, sourceText, commandSyntax, exception);
            }

            yield return item;
        }

        // If the command didn't record its own exit code (shell commands),
        // record 0 so the pipeline tracker has complete stage information.
        if (pipelineExitStatusTracker is not null && pipelineExitStatusTracker.ExitCodeCount == exitCodeCountBefore)
        {
            pipelineExitStatusTracker.Record(0);
        }
    }

    private async Task<IReadOnlyList<EvaluatedCommandArgument>> EvaluateCommandArgumentsAsync(
        string sourceName,
        string sourceText,
        IShellCommand command,
        CommandSyntax commandSyntax,
        CancellationToken cancellationToken)
    {
        if (string.Equals(command.Name, "offset-of", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command.Name, "native-offsetof", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = new List<EvaluatedCommandArgument>(commandSyntax.Arguments.Count);

            for (var index = 0; index < commandSyntax.Arguments.Count; index++)
            {
                if (index == 0 && commandSyntax.Arguments[index] is StaticMemberAccessArgumentSyntax staticAccess)
                {
                    arguments.Add(new EvaluatedCommandArgument(staticAccess, staticAccess.Path));
                    continue;
                }

                await EvaluateCommandArgumentAsync(arguments, sourceName, sourceText, commandSyntax.Arguments[index], cancellationToken);
            }

            return arguments;
        }

        var results = new List<EvaluatedCommandArgument>(commandSyntax.Arguments.Count);

        foreach (var argument in commandSyntax.Arguments)
        {
            if (command is ICurrentItemMemberPathCommand &&
                TryGetCurrentItemMemberPath(argument, out var memberPath))
            {
                results.Add(new EvaluatedCommandArgument(argument, memberPath));
                continue;
            }

            await EvaluateCommandArgumentAsync(results, sourceName, sourceText, argument, cancellationToken);
        }

        return results;
    }

    private IReadOnlyList<object?> ExpandImplicitGlobArguments(
        IShellCommand command,
        IReadOnlyList<EvaluatedCommandArgument> evaluatedArguments)
    {
        if (command is not IImplicitGlobCommand || evaluatedArguments.Count == 0)
        {
            return evaluatedArguments.Select(static argument => argument.Value).ToArray();
        }

        var expanded = new List<object?>(evaluatedArguments.Count);

        for (var index = 0; index < evaluatedArguments.Count; index++)
        {
            var evaluatedArgument = evaluatedArguments[index];

            if (evaluatedArgument.Syntax is BarewordArgumentSyntax or SplatArgumentSyntax &&
                evaluatedArgument.Value is string text &&
                !string.IsNullOrWhiteSpace(text) &&
                !text.StartsWith("-", StringComparison.Ordinal) &&
                PathUtilities.ContainsGlobPattern(text))
            {
                var matches = PathUtilities.ExpandGlob(Runtime.CurrentDirectory, text);

                if (matches.Count > 0)
                {
                    expanded.AddRange(matches.Select(static match => (object?)match.ArgumentText));
                    continue;
                }
            }

            expanded.Add(evaluatedArgument.Value);
        }

        return expanded;
    }

    private async Task EvaluateCommandArgumentAsync(
        ICollection<EvaluatedCommandArgument> arguments,
        string sourceName,
        string sourceText,
        ArgumentSyntax argument,
        CancellationToken cancellationToken)
    {
        if (argument is SplatArgumentSyntax splat)
        {
            var splatValue = await EvaluateArgumentAsync(sourceName, sourceText, splat.Value, cancellationToken);

            foreach (var item in ExpandSplatValues(sourceName, sourceText, splat, splatValue))
            {
                arguments.Add(new EvaluatedCommandArgument(splat, item));
            }

            return;
        }

        arguments.Add(new EvaluatedCommandArgument(
            argument,
            await EvaluateArgumentAsync(sourceName, sourceText, argument, cancellationToken)));
    }

    private IReadOnlyList<object?> ExpandSplatValues(
        string sourceName,
        string sourceText,
        SplatArgumentSyntax splat,
        object? value)
    {
        if (value is null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::splat_requires_collection",
                Title: "Argument splatting requires a non-null collection value.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: splat.Span,
                Label: "this splat target is null",
                Help: "use a list, array, range, or tuple value with '...'."));
        }

        if (value is string || ShellRecordUtilities.IsRecordLike(value))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::splat_requires_collection",
                Title: "Argument splatting requires an array, list, range, tuple, or similar collection.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: splat.Span,
                Label: "this value expands as a single argument, not a collection"));
        }

        if (value is ToshRange range)
        {
            return range.Enumerate().Cast<object?>().ToArray();
        }

        if (value is IEnumerable enumerable)
        {
            return enumerable.Cast<object?>().ToArray();
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh::runtime::splat_requires_collection",
            Title: "Argument splatting requires a collection value.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: splat.Span,
            Label: $"'{value.GetType().Name}' does not expand into multiple arguments",
            Help: "wrap multiple values in an array or list before splatting them."));
    }

    private IShellCommand ResolveCommand(
        string sourceName,
        string sourceText,
        CommandSyntax commandSyntax)
    {
        foreach (var scope in _scopes)
        {
            if (scope.Commands.TryGetValue(commandSyntax.Name, out var scopedCommand))
            {
                return scopedCommand;
            }
        }

        if (Runtime.Commands.TryGet(commandSyntax.Name, out var command))
        {
            return command;
        }

        var external = ExternalCommandResolver.Resolve(Runtime.CurrentDirectory, commandSyntax.Name);

        if (external.Status is not ExternalCommandLookupStatus.Found &&
            TryBuildVariableReferenceHint(commandSyntax.Name, out var suggestedReference, out var variableName))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::variable_reference_requires_dollar",
                Title: $"Variable '{variableName}' exists, but variable references must start with '$'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: commandSyntax.Span,
                Label: $"did you mean '{suggestedReference}'?",
                Help: "declare variables with 'var name', then use '$name' everywhere else in ToSh."));
        }

        // Auto-source Tosh scripts instead of trying to exec them as native processes.
        if (external.Status is ExternalCommandLookupStatus.Found or ExternalCommandLookupStatus.NotExecutable &&
            external.ResolvedPath is not null &&
            ScriptFileDetection.IsToshScript(external.ResolvedPath))
        {
            return new ToshScriptCommand(commandSyntax.Name, external.ResolvedPath, this);
        }

        return external.Status switch
        {
            ExternalCommandLookupStatus.Found when external.ResolvedPath is not null =>
                new ExternalProcessCommand(commandSyntax.Name, external.ResolvedPath),
            ExternalCommandLookupStatus.NotExecutable =>
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh::runtime::external_command_not_executable",
                    Title: $"'{external.ResolvedPath ?? commandSyntax.Name}' is not executable.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: commandSyntax.Span,
                    Label: $"'{commandSyntax.Name}' cannot be launched as a program",
                    Help: external.IsExplicitPath
                        ? $"make it executable, for example with `chmod +x {commandSyntax.Name}`, or run it with an interpreter."
                        : "check the file permissions or invoke it through an interpreter.")),
            ExternalCommandLookupStatus.IsDirectory when Runtime.Config.Shell.AutoCd =>
                new AutoCdCommand(external.ResolvedPath ?? commandSyntax.Name),
            ExternalCommandLookupStatus.IsDirectory =>
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh::runtime::external_command_is_directory",
                    Title: $"'{external.ResolvedPath ?? commandSyntax.Name}' is a directory, not an executable file.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: commandSyntax.Span,
                    Label: $"'{commandSyntax.Name}' does not refer to a runnable program")),
            _ when Runtime.Config.Shell.AutoCd && TryResolveAutoCdDirectory(commandSyntax.Name, out var autoCdPath) =>
                new AutoCdCommand(autoCdPath),
            _ =>
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh::runtime::unknown_command",
                    Title: $"Command '{commandSyntax.Name}' was not found.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: commandSyntax.Span,
                    Label: $"'{commandSyntax.Name}' is not a built-in, function, executable, or $-prefixed variable reference",
                    Help: commandSyntax.Name.Contains(Path.DirectorySeparatorChar) || commandSyntax.Name.Contains(Path.AltDirectorySeparatorChar)
                        ? "check that the path exists and points to an executable file."
                        : $"use 'which {commandSyntax.Name}' to inspect how Tosh resolves this command.")),
        };
    }

    private async Task<IReadOnlyList<object?>> EvaluateArgumentsAsync(
        string sourceName,
        string sourceText,
        IReadOnlyList<ArgumentSyntax> arguments,
        CancellationToken cancellationToken)
    {
        var values = new object?[arguments.Count];

        for (var index = 0; index < arguments.Count; index++)
        {
            values[index] = await EvaluateArgumentAsync(sourceName, sourceText, arguments[index], cancellationToken);
        }

        return values;
    }

    private async Task<object?> EvaluateArgumentAsync(
        string sourceName,
        string sourceText,
        ArgumentSyntax argument,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (argument)
            {
                case BarewordArgumentSyntax bareword:
                    return bareword.Value;

                case LiteralArgumentSyntax literal:
                    return literal.Value;

                case VariableReferenceArgumentSyntax variableReference:
                    {
                        if (TryGetVariableBinding(variableReference.Name, out var binding))
                        {
                            if (binding.IsAllocatedOnly)
                            {
                                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                                    Code: "tosh::runtime::uninitialized_variable",
                                    Title: $"Variable '{variableReference.Name}' has been declared but not assigned yet.",
                                    SourceName: sourceName,
                                    SourceText: sourceText,
                                    Span: variableReference.Span,
                                    Label: $"assign a value to '{variableReference.Name}' before using it",
                                    Help: $"try '${variableReference.Name} = ...' or assign a member like '${variableReference.Name}.Name = ...'."));
                            }

                            return binding.Value;
                        }

                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh::runtime::unknown_variable",
                            Title: $"Variable '{variableReference.Name}' was not found.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: variableReference.Span,
                            Label: $"'{variableReference.Name}' is not defined in this scope",
                            Help: $"declare it first with 'var {variableReference.Name} = ...'."));
                    }

                case NewObjectArgumentSyntax newObject:
                    {
                        var constructorArguments = await EvaluateArgumentsAsync(sourceName, sourceText, newObject.Arguments, cancellationToken);

                        if (TryResolveShellStaticType(newObject.TypeName, out var shellType))
                        {
                            return Runtime.Invoker.CreateInstance(shellType, constructorArguments);
                        }

                        var type = ResolveTypeName(newObject.TypeName)
                                   ?? throw new InvalidOperationException($"Unable to resolve type '{newObject.TypeName}'.");
                        return Runtime.Invoker.CreateInstance(type, constructorArguments);
                    }

                case StaticMethodCallArgumentSyntax staticMethodCall:
                    {
                        var methodArguments = await EvaluateArgumentsAsync(sourceName, sourceText, staticMethodCall.Arguments, cancellationToken);
                        return InvokeQualifiedMethod(staticMethodCall.Path, methodArguments);
                    }

                case StaticMemberAccessArgumentSyntax staticMemberAccess:
                    {
                        return ResolveQualifiedAccessOrFallback(staticMemberAccess.Path);
                    }

                case ArrayLiteralArgumentSyntax listLiteral:
                    {
                        var items = new List<object?>();

                        foreach (var element in listLiteral.Items)
                        {
                            if (element is SpreadElementArgumentSyntax spread)
                            {
                                var spreadValue = await EvaluateArgumentAsync(sourceName, sourceText, spread.Value, cancellationToken);

                                if (spreadValue is string)
                                {
                                    items.Add(spreadValue);
                                }
                                else if (spreadValue is IEnumerable enumerable)
                                {
                                    foreach (var item in enumerable)
                                    {
                                        items.Add(item);
                                    }
                                }
                                else
                                {
                                    items.Add(spreadValue);
                                }
                            }
                            else
                            {
                                items.Add(await EvaluateArgumentAsync(sourceName, sourceText, element, cancellationToken));
                            }
                        }

                        return items.ToArray();
                    }

                case RecordLiteralArgumentSyntax recordLiteral:
                    {
                        IDictionary<string, object?> record = new System.Dynamic.ExpandoObject();

                        foreach (var entry in recordLiteral.Fields)
                        {
                            switch (entry)
                            {
                                case RecordFieldSyntax field:
                                    record[field.Name] = await EvaluateArgumentAsync(sourceName, sourceText, field.Value, cancellationToken);
                                    break;

                                case ComputedRecordFieldSyntax computed:
                                    {
                                        var key = await EvaluateArgumentAsync(sourceName, sourceText, computed.NameExpression, cancellationToken);
                                        var value = await EvaluateArgumentAsync(sourceName, sourceText, computed.Value, cancellationToken);
                                        record[key?.ToString() ?? string.Empty] = value;
                                    }
                                    break;

                                case SpreadRecordEntrySyntax spread:
                                    {
                                        var spreadValue = await EvaluateArgumentAsync(sourceName, sourceText, spread.Value, cancellationToken);

                                        if (spreadValue is IDictionary<string, object?> dict)
                                        {
                                            foreach (var kvp in dict)
                                            {
                                                record[kvp.Key] = kvp.Value;
                                            }
                                        }
                                        else if (spreadValue is IShellRecordObject shellRecord)
                                        {
                                            foreach (var member in shellRecord.GetMembers())
                                            {
                                                record[member.Key] = member.Value;
                                            }
                                        }
                                        else
                                        {
                                            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                                                Code: "tosh::runtime::spread_requires_record",
                                                Title: "Spread in a record literal requires a record or dictionary value.",
                                                SourceName: sourceName,
                                                SourceText: sourceText,
                                                Span: spread.Span,
                                                Label: "this value is not a record"));
                                        }
                                    }
                                    break;
                            }
                        }

                        return record;
                    }

                case BlockArgumentSyntax blockArgument:
                    {
                        return new ShellBlock(blockArgument.Block, sourceName, sourceText, blockArgument.Span);
                    }

                case AnonymousFunctionArgumentSyntax anonymousFunction:
                    {
                        var definition = CreateFunctionDefinition(
                            "<lambda>",
                            anonymousFunction.Parameters,
                            returnTypeName: null,
                            anonymousFunction.Body,
                            isCommandWrapper: false,
                            sourceName,
                            sourceText,
                            anonymousFunction.Span);

                        return new ToshLambda(this, definition);
                    }

                case MemberProjectionArgumentSyntax projection:
                    {
                        return new ProjectedMemberSelection(projection.MemberPaths);
                    }

                case MemberAccessArgumentSyntax memberAccess:
                    {
                        var target = await EvaluateArgumentAsync(sourceName, sourceText, memberAccess.Target, cancellationToken);

                        if (memberAccess.NullSafe && target is null)
                        {
                            return null;
                        }

                        return Runtime.ObjectAccessor.GetValue(target, memberAccess.MemberPath);
                    }

                case IndexAccessArgumentSyntax indexAccess:
                    {
                        var target = await EvaluateArgumentAsync(sourceName, sourceText, indexAccess.Target, cancellationToken);
                        var index = await EvaluateArgumentAsync(sourceName, sourceText, indexAccess.Index, cancellationToken);
                        return ShellIndexingUtilities.GetIndexedValue(target, index, indexAccess.LookupKind);
                    }

                case MethodCallArgumentSyntax methodCall:
                    {
                        var target = await ResolveMethodCallTargetAsync(sourceName, sourceText, methodCall, cancellationToken);

                        if (target is ShellTextLine textLine)
                        {
                            target = textLine.Text;
                        }

                        if (target is null)
                        {
                            if (methodCall.NullSafe)
                            {
                                return null;
                            }

                            throw new InvalidOperationException("Cannot invoke an instance method on null.");
                        }

                        var methodArguments = await EvaluateArgumentsAsync(sourceName, sourceText, methodCall.Arguments, cancellationToken);
                        var invocation = Runtime.Invoker.InvokeInstance(target, methodCall.MethodName, methodArguments);
                        return invocation.ReturnedVoid ? target : invocation.Value;
                    }

                case SubexpressionArgumentSyntax subexpression:
                    {
                        if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, subexpression.Pipeline, cancellationToken) is { Matched: true } raw)
                        {
                            return raw.Value;
                        }

                        var results = await AsyncEnumerableExtensions.ToListAsync(
                            EvaluatePipelineAsync(sourceName, sourceText, subexpression.Pipeline, cancellationToken),
                            cancellationToken);

                        if (results.Count == 1)
                        {
                            return results[0];
                        }

                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh::runtime::subexpression_requires_single_value",
                            Title: "Subexpressions used as arguments must produce exactly one value.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: argument.Span,
                            Label: results.Count == 0
                                ? "this subexpression produced no values"
                                : $"this subexpression produced {results.Count} values",
                            Help: "ensure the parenthesized pipeline returns exactly one object."));
                    }

                case CommandSubstitutionArgumentSyntax commandSubstitution:
                    {
                        IReadOnlyList<object?> results;

                        if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, commandSubstitution.Pipeline, cancellationToken) is { Matched: true } raw)
                        {
                            results = [raw.Value];
                        }
                        else
                        {
                            results = await AsyncEnumerableExtensions.ToListAsync(
                                EvaluatePipelineAsync(sourceName, sourceText, commandSubstitution.Pipeline, cancellationToken),
                                cancellationToken);
                        }

                        return string.Join(Environment.NewLine, results.Select(FormatCommandSubstitutionValue));
                    }

                case InputProcessSubstitutionArgumentSyntax processSubstitution:
                    {
                        IReadOnlyList<object?> results;

                        if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, processSubstitution.Pipeline, cancellationToken) is { Matched: true } raw)
                        {
                            results = [raw.Value];
                        }
                        else
                        {
                            results = await AsyncEnumerableExtensions.ToListAsync(
                                EvaluatePipelineAsync(sourceName, sourceText, processSubstitution.Pipeline, cancellationToken),
                                cancellationToken);
                        }

                        return await PipelineFileMaterializer.MaterializeAsync("text", results, cancellationToken);
                    }

                case OutputProcessSubstitutionArgumentSyntax outputProcessSubstitution:
                    {
                        IReadOnlyList<object?> results;

                        if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, outputProcessSubstitution.Pipeline, cancellationToken) is { Matched: true } rawOutput)
                        {
                            results = [rawOutput.Value];
                        }
                        else
                        {
                            results = await AsyncEnumerableExtensions.ToListAsync(
                                EvaluatePipelineAsync(sourceName, sourceText, outputProcessSubstitution.Pipeline, cancellationToken),
                                cancellationToken);
                        }

                        return await PipelineFileMaterializer.MaterializeAsync("text", results, cancellationToken);
                    }

                case OperatorArgumentSyntax operation:
                    {
                        var left = await EvaluateArgumentAsync(sourceName, sourceText, operation.Left, cancellationToken);

                        // Short-circuit: do not evaluate the right side if unnecessary.
                        if (operation.Operator == "and")
                        {
                            return OperatorEvaluator.ToBoolean(left)
                                && OperatorEvaluator.ToBoolean(await EvaluateArgumentAsync(sourceName, sourceText, operation.Right, cancellationToken));
                        }

                        if (operation.Operator == "or")
                        {
                            return OperatorEvaluator.ToBoolean(left)
                                || OperatorEvaluator.ToBoolean(await EvaluateArgumentAsync(sourceName, sourceText, operation.Right, cancellationToken));
                        }

                        if (operation.Operator == "??")
                        {
                            return left ?? await EvaluateArgumentAsync(sourceName, sourceText, operation.Right, cancellationToken);
                        }

                        var right = await EvaluateArgumentAsync(sourceName, sourceText, operation.Right, cancellationToken);
                        return OperatorEvaluator.EvaluateBinary(left, operation.Operator, right);
                    }

                case ConditionalArgumentSyntax conditional:
                    {
                        var condition = await EvaluateArgumentAsync(sourceName, sourceText, conditional.Condition, cancellationToken);
                        return OperatorEvaluator.ToBoolean(condition)
                            ? await EvaluateArgumentAsync(sourceName, sourceText, conditional.WhenTrue, cancellationToken)
                            : await EvaluateArgumentAsync(sourceName, sourceText, conditional.WhenFalse, cancellationToken);
                    }

                case MatchArgumentSyntax match:
                    {
                        var arm = await ResolveMatchArmAsync(sourceName, sourceText, match, cancellationToken);
                        return await EvaluateMatchArmValueAsync(sourceName, sourceText, arm, cancellationToken);
                    }

                case IfExpressionArgumentSyntax ifExpression:
                    {
                        var condition = await EvaluateConditionAsync(sourceName, sourceText, ifExpression.Condition, cancellationToken);
                        var block = condition ? ifExpression.ThenBlock : ifExpression.ElseBlock;
                        var values = await AsyncEnumerableExtensions.ToListAsync(
                            ExecuteBlockAsync(sourceName, sourceText, block, cancellationToken),
                            cancellationToken);

                        return values.Count switch
                        {
                            0 => null,
                            1 => values[0],
                            _ => values.ToArray(),
                        };
                    }

                case UnaryOperatorArgumentSyntax unaryOperation:
                    {
                        var operand = await EvaluateArgumentAsync(sourceName, sourceText, unaryOperation.Operand, cancellationToken);
                        return OperatorEvaluator.EvaluateUnary(unaryOperation.Operator, operand);
                    }

                case InterpolatedStringArgumentSyntax interpolated:
                    {
                        var builder = new System.Text.StringBuilder();

                        foreach (var part in interpolated.Parts)
                        {
                            switch (part)
                            {
                                case InterpolatedStringLiteralPart literal:
                                    builder.Append(literal.Text);
                                    break;

                                case InterpolatedStringExpressionPart expression:
                                    {
                                        var results = await AsyncEnumerableExtensions.ToListAsync(
                                            EvaluateAsync(expression.Expression, sourceName, cancellationToken),
                                            cancellationToken);

                                        if (results.Count == 1)
                                        {
                                            builder.Append(FormatInterpolatedValue(results[0]));
                                        }
                                        else if (results.Count > 1)
                                        {
                                            builder.Append(string.Join(" ", results.Select(FormatInterpolatedValue)));
                                        }

                                        break;
                                    }
                            }
                        }

                        return builder.ToString();
                    }

                case NameOfArgumentSyntax nameOf:
                    {
                        // If not a $-prefixed variable reference, check if the bare identifier
                        // actually refers to a variable — if so, require '$'.
                        if (!nameOf.IsVariableReference && TryGetVariableBinding(nameOf.Identifier, out _))
                        {
                            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                                Code: "tosh::runtime::nameof_requires_dollar",
                                Title: $"Variable references in nameof require '$'. Use nameof(${nameOf.Identifier}).",
                                SourceName: sourceName,
                                SourceText: sourceText,
                                Span: nameOf.Span,
                                Label: $"did you mean '${nameOf.Identifier}'?",
                                Help: $"try nameof(${nameOf.Identifier}) to get the variable name."));
                        }

                        return nameOf.Identifier;
                    }

                case FunctionReferenceArgumentSyntax funcRef:
                    {
                        // Look up the function/command by name and return it as a callable value.
                        foreach (var scope in _scopes)
                        {
                            if (scope.Commands.TryGetValue(funcRef.Name, out var scopedCommand))
                            {
                                return scopedCommand;
                            }
                        }

                        if (Runtime.Commands.TryGet(funcRef.Name, out var registeredCommand))
                        {
                            return registeredCommand;
                        }

                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh::runtime::unknown_function_reference",
                            Title: $"Function '{funcRef.Name}' was not found.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: funcRef.Span,
                            Label: $"'{funcRef.Name}' is not defined in this scope",
                            Help: "define the function first or check the spelling."));
                    }

                case RangeArgumentSyntax range:
                    {
                        var startValue = await EvaluateArgumentAsync(sourceName, sourceText, range.Start, cancellationToken);
                        var endValue = await EvaluateArgumentAsync(sourceName, sourceText, range.End, cancellationToken);

                        var start = ConvertToInt(startValue, "range start");
                        var end = ConvertToInt(endValue, "range end");

                        int? step = null;
                        if (range.Step is not null)
                        {
                            var stepValue = await EvaluateArgumentAsync(sourceName, sourceText, range.Step, cancellationToken);
                            step = ConvertToInt(stepValue, "range step");
                        }

                        return new ToshRange(start, step, end);
                    }

                default:
                    throw new InvalidOperationException($"Unsupported argument syntax: {argument.GetType().Name}.");
            }
        }
        catch (Exception exception) when (exception is not ToshDiagnosticException && exception is not OperationCanceledException)
        {
            throw CreateExpressionDiagnostic(sourceName, sourceText, argument, exception);
        }
    }

    private string FormatCommandSubstitutionValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            ShellTextLine textLine => textLine.Text,
            string text => text,
            _ => Runtime.Formatter.Format(value),
        };
    }

    private async Task<MatchArmSyntax> ResolveMatchArmAsync(
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
                var patternValue = await EvaluateArgumentAsync(sourceName, sourceText, arm.Pattern, cancellationToken);
                matched = OperatorEvaluator.AreEqual(value, patternValue);
            }

            if (!matched)
            {
                continue;
            }

            if (arm.Guard is not null)
            {
                var guardValue = await EvaluateArgumentAsync(sourceName, sourceText, arm.Guard, cancellationToken);
                if (!OperatorEvaluator.ToBoolean(guardValue))
                {
                    continue;
                }
            }

            return arm;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh::runtime::non_exhaustive_match",
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
        MatchArmSyntax arm,
        CancellationToken cancellationToken)
    {
        var values = await AsyncEnumerableExtensions.ToListAsync(
            ExecuteMatchArmAsync(sourceName, sourceText, arm, cancellationToken),
            cancellationToken);

        return values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => values.ToArray(),
        };
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

    private static int ConvertToInt(object? value, string label)
    {
        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            double d when d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue => (int)d,
            _ => throw new InvalidOperationException($"The {label} of a range must be an integer, got '{value}'.")
        };
    }

    private async Task<object?> ResolveMethodCallTargetAsync(
        string sourceName,
        string sourceText,
        MethodCallArgumentSyntax methodCall,
        CancellationToken cancellationToken)
    {
        if (!ShouldAutoMaterializeListTarget(methodCall.MethodName) ||
            !TryDecomposeMemberAssignmentTarget(methodCall.Target, out var rootExpression, out var memberPath))
        {
            return await EvaluateArgumentAsync(sourceName, sourceText, methodCall.Target, cancellationToken);
        }

        var rootTarget = await EvaluateOrMaterializeRootTargetAsync(sourceName, sourceText, rootExpression, cancellationToken);

        try
        {
            var existingTarget = Runtime.ObjectAccessor.GetValue(rootTarget, memberPath);

            if (existingTarget is not null)
            {
                return existingTarget;
            }
        }
        catch (Exception exception) when (exception is not ToshDiagnosticException)
        {
        }

        var materializedList = new List<object?>();

        try
        {
            Runtime.ObjectAccessor.SetValue(rootTarget, memberPath, materializedList);
            return materializedList;
        }
        catch (Exception exception) when (exception is not ToshDiagnosticException)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::list_materialization_failed",
                Title: exception.Message,
                SourceName: sourceName,
                SourceText: sourceText,
                Span: methodCall.Target.Span,
                Label: $"while preparing '{memberPath}' for '{methodCall.MethodName}'"));
        }
    }

    private async Task<object?> EvaluateOrMaterializeRootTargetAsync(
        string sourceName,
        string sourceText,
        ArgumentSyntax rootExpression,
        CancellationToken cancellationToken)
    {
        if (rootExpression is VariableReferenceArgumentSyntax variableReference &&
            TryGetVariableBinding(variableReference.Name, out var existingBinding) &&
            existingBinding.IsAllocatedOnly)
        {
            var target = new System.Dynamic.ExpandoObject();

            TryAssignVariable(
                variableReference.Name,
                existingBinding with
                {
                    Value = target,
                    ReplayAsPipeline = false,
                    IsAllocatedOnly = false,
                });

            return target;
        }

        return await EvaluateArgumentAsync(sourceName, sourceText, rootExpression, cancellationToken);
    }

    private static ToshDiagnosticException CreateExpressionDiagnostic(
        string sourceName,
        string sourceText,
        ArgumentSyntax argument,
        Exception exception)
    {
        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: exception is InvalidOperationException
                ? "tosh::runtime::expression_failed"
                : "tosh::runtime::unexpected_exception",
            Title: exception.Message,
            SourceName: sourceName,
            SourceText: sourceText,
            Span: argument.Span,
            Label: "while evaluating this expression"));
    }

    private object? ResolveQualifiedAccessOrFallback(string path)
    {
        if (TryResolveQualifiedAccess(path, out var value, out _))
        {
            return value;
        }

        return path;
    }

    private object? InvokeQualifiedMethod(string path, IReadOnlyList<object?> arguments)
    {
        if (TryResolveShellStaticType(path, out _))
        {
            throw new InvalidOperationException($"Construct instances with 'new {path}(...)'.");
        }

        if (TryInvokeShellSymbol(path, arguments, out var shellResult))
        {
            return shellResult;
        }

        var directType = ResolveTypeName(path);

        if (directType is not null)
        {
            throw new InvalidOperationException($"Construct instances with 'new {path}(...)'.");
        }

        var segments = SplitQualifiedPath(path);

        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var type = ResolveTypeName(string.Join('.', segments.Take(prefixLength)));

            if (type is null)
            {
                continue;
            }

            if (prefixLength == segments.Length - 1)
            {
                var invocation = Runtime.Invoker.InvokeStatic(type, segments[^1], arguments);
                return invocation.ReturnedVoid ? null : invocation.Value;
            }

            var target = ResolveQualifiedMemberChain(type, segments[prefixLength..^1]);

            if (target is null)
            {
                throw new InvalidOperationException("Cannot invoke an instance method on null.");
            }

            var instanceInvocation = Runtime.Invoker.InvokeInstance(target, segments[^1], arguments);
            return instanceInvocation.ReturnedVoid ? target : instanceInvocation.Value;
        }

        throw new InvalidOperationException($"Unable to resolve .NET access path '{path}'.");
    }

    private bool TryResolveQualifiedAccess(string path, out object? value, out bool matchedType)
    {
        if (TryResolveShellSymbolAccess(path, out value))
        {
            matchedType = true;
            return true;
        }

        var directType = ResolveTypeName(path);

        if (directType is not null)
        {
            matchedType = true;
            value = directType;
            return true;
        }

        var segments = SplitQualifiedPath(path);
        matchedType = false;

        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var type = ResolveTypeName(string.Join('.', segments.Take(prefixLength)));

            if (type is null)
            {
                continue;
            }

            matchedType = true;
            value = ResolveQualifiedMemberChain(type, segments[prefixLength..]);
            return true;
        }

        value = null;
        return false;
    }

    private bool TryInvokeShellSymbol(string path, IReadOnlyList<object?> arguments, out object? value)
    {
        var segments = SplitQualifiedPath(path);

        if (segments.Length >= 2 &&
            TryGetModule(segments[0], out var module))
        {
            object target = module;

            if (segments.Length > 2)
            {
                target = Runtime.ObjectAccessor.GetValue(module, string.Join('.', segments[1..^1]))
                         ?? throw new InvalidOperationException($"Cannot invoke '{segments[^1]}' on null.");
            }

            var invocation = Runtime.Invoker.InvokeInstance(target, segments[^1], arguments);
            value = invocation.ReturnedVoid ? target : invocation.Value;
            return true;
        }

        if (segments.Length == 2 &&
            TryResolveShellStaticType(segments[0], out var shellType))
        {
            var invocation = Runtime.Invoker.InvokeStatic(shellType, segments[1], arguments);
            value = invocation.ReturnedVoid ? null : invocation.Value;
            return true;
        }

        value = null;
        return false;
    }

    private bool TryResolveShellSymbolAccess(string path, out object? value)
    {
        if (TryGetNamedType(path, out var directType))
        {
            value = directType;
            return true;
        }

        var segments = SplitQualifiedPath(path);

        if (segments.Length >= 1 &&
            TryGetModule(segments[0], out var module))
        {
            value = segments.Length == 1
                ? module
                : Runtime.ObjectAccessor.GetValue(module, string.Join('.', segments[1..]));
            return true;
        }

        if (segments.Length == 2 &&
            TryGetNamedType(segments[0], out var shellType))
        {
            value = Runtime.Invoker.GetStaticMember(shellType, segments[1]);
            return true;
        }

        value = null;
        return false;
    }

    private object? ResolveQualifiedMemberChain(Type type, IReadOnlyList<string> memberSegments)
    {
        if (memberSegments.Count == 0)
        {
            throw new InvalidOperationException($"No member path was provided for type '{type.FullName}'.");
        }

        object? current = Runtime.Invoker.GetStaticMember(type, memberSegments[0]);

        for (var index = 1; index < memberSegments.Count; index++)
        {
            current = Runtime.ObjectAccessor.GetValue(current, memberSegments[index]);
        }

        return current;
    }

    private static string[] SplitQualifiedPath(string path)
    {
        return path
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
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

    private static void EnsureBindingNameIsNotReserved(string sourceName, string sourceText, string name, TextSpan span, string titleSuffix)
    {
        if (!RuntimeNamespaceUtilities.IsReservedRuntimeNamespaceName(name))
        {
            return;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh::runtime::reserved_variable_name",
            Title: $"'{name}' is a {titleSuffix}.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"choose a different name than '{name}'"));
    }

    private string FormatInterpolatedValue(object? value)
    {
        if (value is ToshClassInstance classInstance && classInstance.HasCustomToString())
        {
            return classInstance.ToString();
        }

        return Runtime.Formatter.Format(value);
    }

    private static string FormatTraceArgument(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        var text = value.ToString() ?? string.Empty;

        if (text.Contains(' ') || text.Contains('"') || text.Length == 0)
        {
            return $"\"{text.Replace("\"", "\\\"")}\"";
        }

        return text;
    }

    private void UpdateLastResultIfAny(IReadOnlyList<object?> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        Runtime.SetLastResult(values.Count == 1 ? values[0] : values.ToArray());
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

    private static bool IsIdentifier(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!(char.IsLetter(text[0]) || text[0] == '_'))
        {
            return false;
        }

        for (var index = 1; index < text.Length; index++)
        {
            var character = text[index];

            if (!(char.IsLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<(bool Matched, object? Value)> TryEvaluateRawExpressionPipelineAsync(
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        CancellationToken cancellationToken)
    {
        if (pipeline.Stages.Count == 1 &&
            pipeline.Stages[0] is ExpressionPipelineStageSyntax expressionStage)
        {
            var value = await EvaluateArgumentAsync(sourceName, sourceText, expressionStage.Expression, cancellationToken);
            return (true, value);
        }

        return (false, null);
    }

    private static bool ShouldReplayAsPipeline(object? value)
    {
        // Lists and arrays should enumerate their elements into the pipeline.
        // Strings, dictionaries (ExpandoObject / records), and other single objects should not.
        return value is IList or Array;
    }

    private static bool ShouldReplayRuntimeNamespaceCollectionAccess(ArgumentSyntax expression)
    {
        if (!TryGetRuntimeNamespaceMemberPath(expression, out var memberPath))
        {
            return false;
        }

        return memberPath switch
        {
            "Script.Args" => true,
            "Function.Args" => true,
            "Function.Input" => true,
            _ => false,
        };
    }

    private static bool TryGetRuntimeNamespaceMemberPath(ArgumentSyntax expression, out string memberPath)
    {
        var segments = new Stack<string>();
        var current = expression;

        while (current is MemberAccessArgumentSyntax memberAccess)
        {
            segments.Push(memberAccess.MemberPath);
            current = memberAccess.Target;
        }

        if (current is VariableReferenceArgumentSyntax variableReference &&
            string.Equals(variableReference.Name, "tosh", StringComparison.Ordinal) &&
            segments.Count > 0)
        {
            memberPath = string.Join(".", segments);
            return true;
        }

        memberPath = string.Empty;
        return false;
    }

    private static bool TryGetCurrentItemMemberPath(ArgumentSyntax expression, out string memberPath)
    {
        var segments = new Stack<string>();
        var current = expression;

        while (current is MemberAccessArgumentSyntax memberAccess)
        {
            segments.Push(memberAccess.MemberPath);
            current = memberAccess.Target;
        }

        if (current is VariableReferenceArgumentSyntax variableReference &&
            string.Equals(variableReference.Name, "_", StringComparison.Ordinal))
        {
            memberPath = segments.Count == 0
                ? "_"
                : string.Join(".", segments);
            return true;
        }

        memberPath = string.Empty;
        return false;
    }

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

    private static object? ApplyCompoundAssignment(object? currentValue, string assignmentOperator, object? incomingValue)
    {
        if (currentValue is null)
        {
            throw new InvalidOperationException($"The '{assignmentOperator}' operator requires an existing value.");
        }

        var binaryOperator = assignmentOperator switch
        {
            "+=" => "+",
            "-=" => "-",
            "*=" => "*",
            "/=" => "/",
            "%=" => "%",
            _ => throw new InvalidOperationException($"Unsupported assignment operator '{assignmentOperator}'."),
        };

        return OperatorEvaluator.EvaluateBinary(currentValue, binaryOperator, incomingValue);
    }

    private static object? CreateCaughtErrorValue(Exception exception)
    {
        return exception switch
        {
            ThrowSignalException thrown => thrown.Value,
            ToshDiagnosticException diagnostic => diagnostic,
            _ => exception,
        };
    }

    public ShellNameRemovalResult Forget(string name)
    {
        var removedVariable = false;
        var variableScope = string.Empty;
        VariableBinding? removedVariableBinding = null;

        foreach (var scope in _scopes)
        {
            if (!scope.Variables.TryGetValue(name, out var scopedValue))
            {
                continue;
            }

            scope.Variables.Remove(name);
            removedVariable = true;
            variableScope = scope.IsModuleScope ? "Module" : "Local";
            removedVariableBinding = ToVariableBinding(scopedValue);
            break;
        }

        if (!removedVariable && Runtime.Variables.TryGetValue(name, out var globalValue))
        {
            Runtime.Variables.Remove(name);
            removedVariable = true;
            variableScope = "Global";
            removedVariableBinding = ToVariableBinding(globalValue);
        }

        var removedType = false;

        foreach (var scope in _scopes)
        {
            if (!scope.Classes.Remove(name))
            {
                continue;
            }

            removedType = true;
            break;
        }

        if (!removedType)
        {
            Runtime.Classes.Remove(name);
        }

        var removedModule = false;

        foreach (var scope in _scopes)
        {
            if (!scope.Modules.Remove(name))
            {
                continue;
            }

            removedModule = true;
            break;
        }

        if (!removedModule)
        {
            Runtime.Modules.Remove(name);
        }

        var removedCommand = false;
        var commandKind = string.Empty;
        var commandScope = string.Empty;

        foreach (var scope in _scopes)
        {
            if (!scope.Commands.TryGetValue(name, out var scopedCommand))
            {
                continue;
            }

            if (scopedCommand is ICommandResolutionMetadata scopedMetadata &&
                scopedMetadata.ResolutionKind is CommandResolutionKind.Alias or CommandResolutionKind.Function)
            {
                scope.Commands.Remove(name);
                removedCommand = true;
                commandKind = scopedMetadata.ResolutionKind.ToString();
                commandScope = scope.IsModuleScope ? "Module" : "Local";
                break;
            }
        }

        if (!removedCommand &&
            Runtime.Commands.TryGet(name, out var command) &&
            command is ICommandResolutionMetadata metadata &&
            metadata.ResolutionKind is CommandResolutionKind.Alias or CommandResolutionKind.Function)
        {
            removedCommand = Runtime.Commands.Remove(name);
            commandKind = metadata.ResolutionKind.ToString();
            commandScope = "Global";
        }

        var removedEnvironment = Runtime.ExportedEnvironmentVariables.Contains(name) ||
                                 Environment.GetEnvironmentVariable(name) is not null;
        Runtime.RemoveExportedEnvironmentVariable(name);

        var (freedValue, freedValueKind) = TryDisposeForgottenVariableValue(removedVariableBinding?.Value);

        return new ShellNameRemovalResult(
            name,
            removedVariable,
            variableScope,
            removedCommand,
            commandKind,
            commandScope,
            removedEnvironment,
            freedValue,
            freedValueKind);
    }

    public IReadOnlyList<ShellNameRemovalResult> ForgetValue(object? value)
    {
        var removals = new List<ShellNameRemovalResult>();

        foreach (var scope in _scopes)
        {
            var matches = scope.Variables
                .Where(entry => ValuesReferToSameObject(ToVariableBinding(entry.Value).Value, value))
                .Select(entry => entry.Key)
                .ToArray();

            foreach (var name in matches)
            {
                removals.Add(Forget(name));
            }
        }

        var globalMatches = Runtime.Variables
            .Where(entry => ValuesReferToSameObject(ToVariableBinding(entry.Value).Value, value))
            .Select(entry => entry.Key)
            .ToArray();

        foreach (var name in globalMatches)
        {
            if (removals.Any(removal => string.Equals(removal.Name, name, StringComparison.Ordinal)))
            {
                continue;
            }

            removals.Add(Forget(name));
        }

        if (removals.Count == 0 && value is NativeBuffer buffer)
        {
            var freed = false;

            if (!buffer.IsFreed)
            {
                buffer.Dispose();
                freed = true;
            }

            removals.Add(new ShellNameRemovalResult(
                Name: buffer.ToString(),
                RemovedVariable: false,
                VariableScope: string.Empty,
                RemovedCommand: false,
                CommandKind: string.Empty,
                CommandScope: string.Empty,
                RemovedEnvironment: false,
                FreedValue: freed,
                FreedValueKind: nameof(NativeBuffer)));
        }
        else if (removals.Count == 0 && value is ManagedFileHandle handle)
        {
            var freed = false;

            if (handle.IsOpen)
            {
                handle.Dispose();
                freed = true;
            }

            removals.Add(new ShellNameRemovalResult(
                Name: handle.ToString(),
                RemovedVariable: false,
                VariableScope: string.Empty,
                RemovedCommand: false,
                CommandKind: string.Empty,
                CommandScope: string.Empty,
                RemovedEnvironment: false,
                FreedValue: freed,
                FreedValueKind: nameof(ManagedFileHandle)));
        }

        return removals;
    }

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

    private static bool ValuesReferToSameObject(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return false;
    }

    private void DeclareCommand(IShellCommand command, DeclarationModifier modifier)
    {
        EnsureReservedBindingName(command.Name);

        if (modifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek() is { IsModuleScope: true, ExportDeclarationsByDefault: true } moduleScope)
        {
            var registered = RegisterCommand(moduleScope.Commands, command);
            moduleScope.Exports!.Commands[command.Name] = registered;
            return;
        }

        if (modifier == DeclarationModifier.Export && TryGetNearestModuleScope(out var exportScope))
        {
            var registered = RegisterCommand(exportScope.Commands, command);
            exportScope.Exports!.Commands[command.Name] = registered;
            return;
        }

        if (modifier == DeclarationModifier.Shy)
        {
            if (_scopes.Count == 0)
            {
                throw new InvalidOperationException("Shy aliases and functions require a function, block, or module scope.");
            }

            RegisterCommand(_scopes.Peek().Commands, command);
            return;
        }

        if (modifier is DeclarationModifier.Global or DeclarationModifier.Export)
        {
            WarnIfShadowingBuiltin(command.Name);
            RegisterCommand(Runtime.Commands, command);
            return;
        }

        if (_scopes.Count > 0)
        {
            RegisterCommand(_scopes.Peek().Commands, command);
            return;
        }

        WarnIfShadowingBuiltin(command.Name);
        RegisterCommand(Runtime.Commands, command);
    }

    private IShellCommand RegisterCommand(Dictionary<string, IShellCommand> commands, IShellCommand command)
    {
        if (TryMergeFunctionOverload(commands.TryGetValue(command.Name, out var existing) ? existing : null, command, out var merged))
        {
            commands[command.Name] = merged;
            return merged;
        }

        commands[command.Name] = command;
        return command;
    }

    private IShellCommand RegisterCommand(ShellCommandRegistry commands, IShellCommand command)
    {
        if (commands.TryGet(command.Name, out var existing) &&
            TryMergeFunctionOverload(existing, command, out var merged))
        {
            commands.RegisterOrReplace(merged);
            return merged;
        }

        commands.RegisterOrReplace(command);
        return command;
    }

    private bool TryMergeFunctionOverload(IShellCommand? existing, IShellCommand incoming, out IShellCommand merged)
    {
        merged = incoming;

        if (incoming is not FunctionCommand incomingFunction)
        {
            return false;
        }

        switch (existing)
        {
            case FunctionCommand existingFunction:
                merged = new OverloadedFunctionCommand(this, [existingFunction.Definition, incomingFunction.Definition]);
                return true;
            case OverloadedFunctionCommand overloadGroup:
                overloadGroup.AddOrReplace(incomingFunction.Definition);
                merged = overloadGroup;
                return true;
            default:
                return false;
        }
    }

    private IReadOnlyList<LexicalScope>? CaptureVisibleScopes()
    {
        if (_scopes.Count == 0)
        {
            return null;
        }

        return _scopes.Reverse().ToArray();
    }

    private void WarnIfShadowingBuiltin(string commandName)
    {
        if (Runtime.Commands.TryGet(commandName, out var existing) &&
            existing is not ICommandResolutionMetadata)
        {
            Runtime.Error.WriteLine($"Warning: function '{commandName}' shadows built-in command '{commandName}'.");
        }
    }

    private static string ExtractSourceSnippet(string sourceText, TextSpan span)
    {
        if (span.Start < 0 || span.End <= span.Start || span.End > sourceText.Length)
        {
            return "<background job>";
        }

        return sourceText[span.Start..span.End].Trim();
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

    private void DeclareVariable(string name, VariableBinding binding, DeclarationModifier modifier)
    {
        EnsureReservedBindingName(name);

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

    private void DeclareType(string name, IShellNamedType definition, DeclarationModifier modifier)
    {
        EnsureReservedBindingName(name);

        if (modifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek() is { IsModuleScope: true, ExportDeclarationsByDefault: true } moduleScope)
        {
            moduleScope.Classes[name] = definition;
            moduleScope.Exports!.Types[name] = definition;
            return;
        }

        if (modifier == DeclarationModifier.Export && TryGetNearestModuleScope(out var exportScope))
        {
            exportScope.Classes[name] = definition;
            exportScope.Exports!.Types[name] = definition;
            return;
        }

        if (modifier == DeclarationModifier.Shy)
        {
            if (_scopes.Count == 0)
            {
                throw new InvalidOperationException("Shy class declarations require a function, block, or module scope.");
            }

            _scopes.Peek().Classes[name] = definition;
            return;
        }

        if (modifier is DeclarationModifier.Global or DeclarationModifier.Export)
        {
            Runtime.Classes[name] = definition;
            return;
        }

        if (_scopes.Count > 0)
        {
            _scopes.Peek().Classes[name] = definition;
            return;
        }

        Runtime.Classes[name] = definition;
    }

    private void PreRegisterTypeDefinitions(IReadOnlyList<StatementSyntax> statements)
    {
        foreach (var statement in statements)
        {
            var (name, modifier) = statement switch
            {
                ClassDefinitionStatementSyntax c => (c.Name, c.Modifier),
                RecordDefinitionStatementSyntax r => (r.Name, r.Modifier),
                EnumDefinitionStatementSyntax e => (e.Name, e.Modifier),
                _ => (null, DeclarationModifier.Default),
            };

            if (name is null)
            {
                continue;
            }

            var placeholder = new ForwardTypeReference(name);
            DeclareType(name, placeholder, modifier);
        }
    }

    private void DeclareModule(string name, object module, DeclarationModifier modifier)
    {
        EnsureReservedBindingName(name);

        if (modifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek() is { IsModuleScope: true, ExportDeclarationsByDefault: true } moduleScope)
        {
            moduleScope.Modules[name] = module;
            moduleScope.Exports!.Modules[name] = module;
            return;
        }

        if (modifier == DeclarationModifier.Export && TryGetNearestModuleScope(out var exportScope))
        {
            exportScope.Modules[name] = module;
            exportScope.Exports!.Modules[name] = module;
            return;
        }

        if (modifier == DeclarationModifier.Shy)
        {
            if (_scopes.Count == 0)
            {
                throw new InvalidOperationException("Shy module declarations require a function, block, or module scope.");
            }

            _scopes.Peek().Modules[name] = module;
            return;
        }

        if (modifier is DeclarationModifier.Global or DeclarationModifier.Export)
        {
            Runtime.Modules[name] = module;
            return;
        }

        if (_scopes.Count > 0)
        {
            _scopes.Peek().Modules[name] = module;
            return;
        }

        Runtime.Modules[name] = module;
    }

    private bool TryGetNamedType(string name, out IShellNamedType definition)
    {
        foreach (var scope in _scopes)
        {
            if (scope.Classes.TryGetValue(name, out var scopedDefinition) &&
                scopedDefinition is IShellNamedType shellType)
            {
                definition = shellType;
                return true;
            }
        }

        if (Runtime.Classes.TryGetValue(name, out var rawValue) &&
            rawValue is IShellNamedType runtimeDefinition)
        {
            definition = runtimeDefinition;
            return true;
        }

        definition = null!;
        return false;
    }

    private bool TryResolveShellStaticType(string path, out IShellStaticType definition)
    {
        if (TryGetNamedType(path, out var directType))
        {
            definition = directType;
            return true;
        }

        if (BuiltInShellTypes.TryResolveStaticType(path, CreateScopedTypeResolver(), out var builtInType))
        {
            definition = builtInType;
            return true;
        }

        var segments = SplitQualifiedPath(path);

        if (segments.Length >= 2 &&
            TryGetModule(segments[0], out var module))
        {
            object? current = module;
            try
            {
                foreach (var segment in segments[1..])
                {
                    current = Runtime.ObjectAccessor.GetValue(current, segment);

                    if (current is null)
                    {
                        definition = null!;
                        return false;
                    }
                }
            }
            catch (Exception exception) when (exception is not ToshDiagnosticException)
            {
                definition = null!;
                return false;
            }

            if (current is IShellStaticType staticType)
            {
                definition = staticType;
                return true;
            }
        }

        definition = null!;
        return false;
    }

    private bool TryGetClassDefinition(string name, out ToshClassDefinition definition)
    {
        if (TryGetNamedType(name, out var shellType) && shellType is ToshClassDefinition classDefinition)
        {
            definition = classDefinition;
            return true;
        }

        definition = null!;
        return false;
    }

    private bool TryGetModule(string name, out ToshModuleObject module)
    {
        foreach (var scope in _scopes)
        {
            if (scope.Modules.TryGetValue(name, out var scopedModule) &&
                scopedModule is ToshModuleObject scopedToshModule)
            {
                module = scopedToshModule;
                return true;
            }
        }

        if (Runtime.Modules.TryGetValue(name, out var rawModule) &&
            rawModule is ToshModuleObject runtimeModule)
        {
            module = runtimeModule;
            return true;
        }

        module = null!;
        return false;
    }

    private bool TryGetNearestModuleScope(out LexicalScope moduleScope)
    {
        foreach (var scope in _scopes)
        {
            if (scope.IsModuleScope)
            {
                moduleScope = scope;
                return true;
            }
        }

        moduleScope = null!;
        return false;
    }

    private Type? ResolveTypeName(string name)
    {
        return CreateScopedTypeResolver().Resolve(name);
    }

    private IDisposable PushScope(IReadOnlyDictionary<string, object?> locals, bool isModuleScope = false)
    {
        _scopes.Push(new LexicalScope(locals, isModuleScope));
        return new ScopeFrame(_scopes, Runtime.Events);
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

    internal bool TryBindCallableParameters(
        IReadOnlyList<FunctionParameterDefinition> parameters,
        IReadOnlyList<object?> arguments,
        out Dictionary<string, object?> locals,
        out int score)
    {
        locals = new Dictionary<string, object?>(StringComparer.Ordinal);
        score = 0;

        var hasRestParameter = parameters.Count > 0 && parameters[^1].IsRest;
        var positionalCount = hasRestParameter ? parameters.Count - 1 : parameters.Count;
        var requiredCount = parameters.Count(parameter => !parameter.IsOptional && !parameter.IsRest);

        if (arguments.Count < requiredCount || (!hasRestParameter && arguments.Count > parameters.Count))
        {
            return false;
        }

        locals["args"] = arguments.ToArray();

        for (var index = 0; index < positionalCount; index++)
        {
            var parameter = parameters[index];

            if (index >= arguments.Count)
            {
                locals[parameter.Name] = null;
                score += 4;
                continue;
            }

            var value = arguments[index];

            if (parameter.TypeName is not null)
            {
                if (!TryConvertAnnotatedValue(parameter.TypeName, value, out var converted))
                {
                    locals = new Dictionary<string, object?>(StringComparer.Ordinal);
                    score = 0;
                    return false;
                }

                if (!ReferenceEquals(converted, value))
                {
                    score += 1;
                }

                value = converted;
            }

            locals[parameter.Name] = value;
        }

        if (hasRestParameter)
        {
            var restParam = parameters[^1];
            var restArgs = new List<object?>();
            for (var i = positionalCount; i < arguments.Count; i++)
            {
                restArgs.Add(arguments[i]);
            }
            locals[restParam.Name] = restArgs;
        }

        return true;
    }

    internal IReadOnlyList<CallableBindingMatch<TCandidate>> SelectBestCallableMatches<TCandidate>(
        IEnumerable<TCandidate> candidates,
        Func<TCandidate, IReadOnlyList<FunctionParameterDefinition>> parameterSelector,
        IReadOnlyList<object?> arguments)
    {
        var bestMatches = new List<CallableBindingMatch<TCandidate>>();
        var bestScore = int.MaxValue;

        foreach (var candidate in candidates)
        {
            if (!TryBindCallableParameters(
                    parameterSelector(candidate),
                    arguments,
                    out var locals,
                    out var score))
            {
                continue;
            }

            var match = new CallableBindingMatch<TCandidate>(candidate, locals, score);

            if (score < bestScore)
            {
                bestMatches.Clear();
                bestMatches.Add(match);
                bestScore = score;
            }
            else if (score == bestScore)
            {
                bestMatches.Add(match);
            }
        }

        return bestMatches.ToArray();
    }

    internal bool TryConvertAnnotatedValue(string typeName, object? value, out object? converted)
    {
        var allowsNull = typeName.EndsWith("?", StringComparison.Ordinal);
        var normalizedTypeName = allowsNull ? typeName[..^1] : typeName;

        if (value is ToshClassSelfReference selfReference)
        {
            value = selfReference.Unwrap();
        }

        if (value is null)
        {
            converted = null;
            return allowsNull;
        }

        if (value is IShellTypedObject directTyped &&
            (string.Equals(directTyped.ShellTypeDescriptor.ShellTypeName, normalizedTypeName, StringComparison.Ordinal) ||
             string.Equals(directTyped.ShellTypeDescriptor.ShellFullName, normalizedTypeName, StringComparison.Ordinal)))
        {
            converted = value;
            return true;
        }

        if (TryGetNamedType(normalizedTypeName, out var shellType))
        {
            var shellDescriptor = (IShellTypeDescriptor)shellType;

            if (value is IShellTypedObject typed &&
                (string.Equals(typed.ShellTypeDescriptor.ShellTypeName, shellDescriptor.ShellTypeName, StringComparison.Ordinal) ||
                 string.Equals(typed.ShellTypeDescriptor.ShellFullName, shellDescriptor.ShellFullName, StringComparison.Ordinal)))
            {
                converted = value;
                return true;
            }

            if (shellType is ToshEnumDefinition enumDefinition &&
                enumDefinition.TryConvertValue(value, out var enumValue))
            {
                converted = enumValue;
                return true;
            }
        }

        var resolvedType = ResolveTypeName(normalizedTypeName);

        if (resolvedType is not null)
        {
            return TypeConversion.TryConvert(value, resolvedType, out converted);
        }

        converted = null;
        return false;
    }

    internal object? ConvertAnnotatedValue(
        string typeName,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner)
    {
        if (TryConvertAnnotatedValue(typeName, value, out var converted))
        {
            return converted;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh::runtime::annotation_conversion_failed",
            Title: $"'{owner}' produced a value that could not be converted to '{typeName}'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"the value does not match '{typeName}'"));
    }

    internal IReadOnlyList<object?> ExecuteClassBlockSync(
        string sourceName,
        string sourceText,
        BlockSyntax block,
        IReadOnlyDictionary<string, object?> locals,
        IReadOnlyList<LexicalScope>? capturedScopes,
        string callName)
    {
        using var captured = PushCapturedScopes(capturedScopes);
        _functionCallStack.Push(callName);

        try
        {
            return AsyncEnumerableExtensions.ToListAsync(
                    ExecuteBlockAsync(
                        sourceName,
                        sourceText,
                        block,
                        CancellationToken.None,
                        locals))
                .GetAwaiter()
                .GetResult();
        }
        catch (ReturnSignalException signal)
        {
            return signal.Values;
        }
        finally
        {
            _functionCallStack.Pop();
        }
    }

    internal object? EvaluateClassPipelineValueSync(
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        IReadOnlyDictionary<string, object?> locals,
        IReadOnlyList<LexicalScope>? capturedScopes)
    {
        if (TryEvaluateShorthandLocalPipeline(pipeline, locals, out var shorthandValue))
        {
            return shorthandValue;
        }

        var span = pipeline.Stages.Count == 0
            ? default
            : TextSpan.FromBounds(pipeline.Stages[0].Span.Start, pipeline.Stages[^1].Span.End);
        var block = new BlockSyntax([new PipelineStatementSyntax(pipeline, span)], span);
        var values = ExecuteClassBlockSync(sourceName, sourceText, block, locals, capturedScopes, "<class>");

        return values.Count switch
        {
            0 => null,
            1 => values[0] is ToshClassSelfReference selfReference ? selfReference.Unwrap() : values[0],
            _ => values
                .Select(value => value is ToshClassSelfReference self ? self.Unwrap() : value)
                .ToArray(),
        };
    }

    private static bool TryEvaluateShorthandLocalPipeline(
        PipelineSyntax pipeline,
        IReadOnlyDictionary<string, object?> locals,
        out object? value)
    {
        if (pipeline.Redirections is { Count: > 0 } || pipeline.Stages.Count != 1)
        {
            value = null;
            return false;
        }

        if (pipeline.Stages[0] is CommandSyntax command &&
            command.Arguments.Count == 0 &&
            locals.TryGetValue(command.Name, out value))
        {
            return true;
        }

        value = null;
        return false;
    }


    internal async IAsyncEnumerable<object?> ExecuteFunctionAsync(
        FunctionDefinition definition,
        CommandContext context)
    {
        using var capturedScopes = PushCapturedScopes(definition.CapturedScopes);
        var inputItems = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        var locals = BindFunctionParameters(definition, context, inputItems);
        var initialInput = AsyncEnumerableExtensions.FromEnumerable(inputItems);
        var firstCommandArguments = definition.IsCommandWrapper && definition.Parameters.Count == 0
            ? context.Arguments
            : null;
        var values = new List<object?>();

        _functionCallStack.Push(definition.Name);
        _functionArgumentsStack.Push(context.Arguments.ToArray());
        _functionInputStack.Push(inputItems.Count switch
        {
            0 => null,
            1 => inputItems[0],
            _ => inputItems.ToArray(),
        });
        try
        {
            await foreach (var value in ExecuteBlockAsync(
                               definition.SourceName,
                               definition.SourceText,
                               definition.Body,
                               context.CancellationToken,
                               locals,
                               initialInput,
                               firstCommandArguments)
                               .WithCancellation(context.CancellationToken))
            {
                values.Add(value);
            }
        }
        catch (ReturnSignalException signal)
        {
            values.AddRange(signal.Values);
        }
        catch (BreakSignalException signal)
        {
            throw CreateLoopControlDiagnostic(
                definition.SourceName,
                definition.SourceText,
                signal.Span,
                keyword: "break",
                code: "tosh::runtime::break_outside_loop",
                title: "'break' can only be used inside 'for', 'while', or 'each' blocks.");
        }
        catch (ContinueSignalException signal)
        {
            throw CreateLoopControlDiagnostic(
                definition.SourceName,
                definition.SourceText,
                signal.Span,
                keyword: "continue",
                code: "tosh::runtime::continue_outside_loop",
                title: "'continue' can only be used inside 'for', 'while', or 'each' blocks.");
        }
        finally
        {
            _functionInputStack.Pop();
            _functionArgumentsStack.Pop();
            _functionCallStack.Pop();
        }

        foreach (var value in values)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return ConvertFunctionReturnValue(definition, context, value);
        }
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
            Exception? pendingException = null;

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
            catch (Exception ex)
            {
                pendingException = ex;
            }

            for (var i = deferredBlocks.Count - 1; i >= 0; i--)
            {
                try
                {
                    await foreach (var value in ExecuteBlockAsync(
                        sourceName, sourceText, deferredBlocks[i], cancellationToken)
                        .WithCancellation(cancellationToken))
                    {
                        // Deferred blocks execute for side effects only; output is discarded.
                    }
                }
                catch (ShellControlFlowException)
                {
                    // Control flow signals from deferred blocks are suppressed.
                }
            }

            if (pendingException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pendingException).Throw();
            }

            foreach (var value in outputValues)
            {
                yield return value;
            }
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
            if (statement is DeferStatementSyntax deferStatement)
            {
                deferredBlocks?.Add(deferStatement.Body);
                continue;
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
                        EvaluatePipelineAsync(sourceName, sourceText, returnStatement.Value, cancellationToken, pendingInput),
                        cancellationToken);
                }

                UpdateLastResultIfAny(returnValues);
                throw new ReturnSignalException(returnStatement.Span, returnValues);
            }

            if (statement is BreakStatementSyntax breakStatement)
            {
                throw new BreakSignalException(breakStatement.Span);
            }

            if (statement is ContinueStatementSyntax continueStatement)
            {
                throw new ContinueSignalException(continueStatement.Span);
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

    private Dictionary<string, object?> BindFunctionParameters(
        FunctionDefinition definition,
        CommandContext context,
        IReadOnlyList<object?> inputItems)
    {
        var hasRestParameter = definition.Parameters.Count > 0 && definition.Parameters[^1].IsRest;
        var positionalCount = hasRestParameter ? definition.Parameters.Count - 1 : definition.Parameters.Count;
        var requiredCount = definition.Parameters.Count(p => !p.IsOptional && !p.IsRest);
        var allowsImplicitWrapperArguments = definition.IsCommandWrapper && definition.Parameters.Count == 0;

        if (context.Arguments.Count < requiredCount ||
            (!allowsImplicitWrapperArguments && !hasRestParameter && context.Arguments.Count > definition.Parameters.Count))
        {
            var expected = requiredCount == positionalCount
                ? $"{positionalCount}"
                : $"{requiredCount}-{positionalCount}";
            if (hasRestParameter)
            {
                expected = $"at least {requiredCount}";
            }
            throw context.CreateDiagnostic(
                code: "tosh::runtime::function_argument_count_mismatch",
                title: $"Function '{definition.Name}' expects {expected} argument(s) but received {context.Arguments.Count}.",
                label: $"'{definition.Name}' requires {expected} argument(s)");
        }

        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);

        for (var index = 0; index < positionalCount; index++)
        {
            var parameter = definition.Parameters[index];

            if (index >= context.Arguments.Count)
            {
                // Optional parameter with no argument — bind as null
                locals[parameter.Name] = null;
                continue;
            }

            var value = context.Arguments[index];

            if (parameter.TypeName is not null)
            {
                if (!TryConvertAnnotatedValue(parameter.TypeName, value, out var converted))
                {
                    throw context.CreateDiagnostic(
                        code: "tosh::runtime::parameter_type_conversion_failed",
                        title: $"Argument '{parameter.Name}' could not be converted to '{parameter.TypeName}'.",
                        argumentIndex: index,
                        label: $"'{parameter.Name}' expects {parameter.TypeName}");
                }

                value = converted;
            }

            locals[parameter.Name] = value;
        }

        if (hasRestParameter)
        {
            var restParam = definition.Parameters[^1];
            var restArgs = new List<object?>();
            for (var i = positionalCount; i < context.Arguments.Count; i++)
            {
                restArgs.Add(context.Arguments[i]);
            }
            locals[restParam.Name] = restArgs;
        }

        return locals;
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
        if (definition.ReturnTypeName is null)
        {
            return value;
        }

        if (TryConvertAnnotatedValue(definition.ReturnTypeName, value, out var converted))
        {
            return converted;
        }

        throw context.CreateDiagnostic(
            code: "tosh::runtime::return_type_conversion_failed",
            title: $"Function '{definition.Name}' returned a value that could not be converted to '{definition.ReturnTypeName}'.",
            label: $"the returned value does not match '{definition.ReturnTypeName}'",
            span: definition.Span);
    }

    private async Task<bool> EvaluateConditionAsync(
        string sourceName,
        string sourceText,
        ArgumentSyntax condition,
        CancellationToken cancellationToken)
    {
        object? conditionValue;

        try
        {
            conditionValue = await EvaluateArgumentAsync(sourceName, sourceText, condition, cancellationToken);
        }
        catch (ToshDiagnosticException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateExpressionDiagnostic(sourceName, sourceText, condition, exception);
        }

        if (!TypeConversion.TryConvert(conditionValue, typeof(bool), out var converted) || converted is not bool boolean)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::condition_requires_boolean",
                Title: "Conditions must evaluate to a boolean value.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: condition.Span,
                Label: "this condition did not evaluate to true or false",
                Help: "return a boolean, for example with '==', 'Contains(...)', or another predicate."));
        }

        return boolean;
    }

    private static ToshDiagnosticException CreateCommandDiagnostic(
        string sourceName,
        string sourceText,
        CommandSyntax commandSyntax,
        Exception exception)
    {
        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: exception is InvalidOperationException
                ? "tosh::runtime::command_failed"
                : "tosh::runtime::unexpected_exception",
            Title: exception.Message,
            SourceName: sourceName,
            SourceText: sourceText,
            Span: commandSyntax.Span,
            Label: $"while executing '{commandSyntax.Name}'"));
    }

    private void ImportRequiredArtifact(ToshRequiredScriptArtifact artifact, RequireStatementSyntax statement)
    {
        if (statement.Imports.Count == 0)
        {
            foreach (var (name, value) in artifact.Exports.Variables)
            {
                DeclareVariable(name, ToVariableBinding(value), statement.Modifier);
            }

            foreach (var (name, command) in artifact.Exports.Commands)
            {
                DeclareCommand(command, statement.Modifier);
            }

            foreach (var (name, type) in artifact.Exports.Types)
            {
                DeclareType(name, type, statement.Modifier);
            }

            foreach (var (name, module) in artifact.Exports.Modules)
            {
                if (module is not null)
                {
                    DeclareModule(name, module, statement.Modifier);
                }
            }

            return;
        }

        foreach (var import in statement.Imports)
        {
            var bindingName = import.Alias ?? import.Name;

            if (artifact.Exports.Modules.TryGetValue(import.Name, out var module))
            {
                if (module is null)
                {
                    throw new InvalidOperationException($"Export '{import.Name}' in '{artifact.Path}' was null.");
                }

                DeclareModule(bindingName, module, statement.Modifier);
                continue;
            }

            if (artifact.Exports.Types.TryGetValue(import.Name, out var type))
            {
                DeclareType(bindingName, type, statement.Modifier);
                continue;
            }

            if (artifact.Exports.Commands.TryGetValue(import.Name, out var command))
            {
                DeclareCommand(
                    string.Equals(bindingName, command.Name, StringComparison.Ordinal)
                        ? command
                        : new RenamedCommand(bindingName, command),
                    statement.Modifier);
                continue;
            }

            if (artifact.Exports.Variables.TryGetValue(import.Name, out var value))
            {
                DeclareVariable(bindingName, ToVariableBinding(value), statement.Modifier);
                continue;
            }

            throw new InvalidOperationException($"Export '{import.Name}' was not found in '{artifact.Path}'.");
        }
    }

    private async Task<ToshRequiredScriptArtifact> ExecuteRequiredScriptAsync(
        string source,
        string sourceName,
        CancellationToken cancellationToken)
    {
        var parseResult = Parse(source, sourceName);

        if (parseResult.Diagnostics.Count > 0)
        {
            throw new ToshDiagnosticException(parseResult.Diagnostics
                .Select(diagnostic => new ToshDiagnostic(
                    Code: diagnostic.Code,
                    Title: diagnostic.Title,
                    SourceName: parseResult.SourceName,
                    SourceText: parseResult.SourceText,
                    Span: diagnostic.Span,
                    Label: diagnostic.Label,
                    Help: diagnostic.Help))
                .ToArray());
        }

        var moduleScope = new LexicalScope(new Dictionary<string, object?>(StringComparer.Ordinal), isModuleScope: true);
        _scriptNameStack.Push(parseResult.SourceName);
        using var _ = PushScope(moduleScope);

        try
        {
            await foreach (var __ in EvaluateStatementAsync(
                               parseResult.SourceName,
                               parseResult.SourceText,
                               parseResult.Statement,
                               cancellationToken)
                               .WithCancellation(cancellationToken))
            {
            }
        }
        catch (ReturnSignalException signal)
        {
            UpdateLastResultIfAny(signal.Values);
        }
        catch (BreakSignalException signal)
        {
            throw CreateLoopControlDiagnostic(
                parseResult.SourceName,
                parseResult.SourceText,
                signal.Span,
                keyword: "break",
                code: "tosh::runtime::break_outside_loop",
                title: "'break' can only be used inside 'for', 'while', or 'each' blocks.");
        }
        catch (ContinueSignalException signal)
        {
            throw CreateLoopControlDiagnostic(
                parseResult.SourceName,
                parseResult.SourceText,
                signal.Span,
                keyword: "continue",
                code: "tosh::runtime::continue_outside_loop",
                title: "'continue' can only be used inside 'for', 'while', or 'each' blocks.");
        }
        finally
        {
            _scriptNameStack.Pop();
        }

        return new ToshRequiredScriptArtifact(sourceName, moduleScope.Exports ?? new ModuleExportTable());
    }

    private static bool IsNumericEnumUnderlyingType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong);
    }

    private static RequireTarget ResolveRequirement(string target, string currentDirectory)
    {
        var candidate = PathUtilities.ResolvePath(currentDirectory, target);

        if (!Path.HasExtension(candidate))
        {
            var toshCandidate = candidate + ".tosh";

            if (File.Exists(toshCandidate))
            {
                return new RequireTarget(RequireTargetKind.Script, toshCandidate, toshCandidate);
            }
        }

        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"Required target '{candidate}' was not found.", candidate);
        }

        return Path.GetExtension(candidate).ToLowerInvariant() switch
        {
            ".tosh" => new RequireTarget(RequireTargetKind.Script, candidate, candidate),
            ".dll" => new RequireTarget(RequireTargetKind.Assembly, candidate, candidate),
            ".csproj" => new RequireTarget(RequireTargetKind.Project, candidate, candidate),
            _ => throw new InvalidOperationException($"Unsupported require target '{candidate}'. ToSh currently supports .tosh, .dll, and .csproj targets."),
        };
    }

    private static RequireTarget ResolveNativeRequirement(string target, string currentDirectory)
    {
        if (target.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            target.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            target.StartsWith(".", StringComparison.Ordinal) ||
            target.StartsWith("~", StringComparison.Ordinal))
        {
            var candidate = PathUtilities.ResolvePath(currentDirectory, target);

            if (!File.Exists(candidate))
            {
                throw new FileNotFoundException($"Native library '{candidate}' was not found.", candidate);
            }

            return new RequireTarget(RequireTargetKind.Assembly, candidate, "native:" + candidate);
        }

        return new RequireTarget(RequireTargetKind.Assembly, target, "native:" + target);
    }

    private static string GetDefaultNativeModuleName(string target)
    {
        var fileName = Path.GetFileNameWithoutExtension(target);
        var candidate = string.IsNullOrWhiteSpace(fileName) ? target : fileName;
        var sanitized = new StringBuilder(candidate.Length);

        foreach (var ch in candidate)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                sanitized.Append(ch);
            }
        }

        return sanitized.Length == 0 ? "Native" : sanitized.ToString();
    }

    private Type ResolveNativeInteropParameterType(
        string? typeName,
        NativeParameterPassingMode passingMode,
        string sourceName,
        string sourceText,
        TextSpan span,
        string owner)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::native_binding_requires_type",
                Title: $"Native {owner} requires an explicit CLR type.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: "write a CLR type like 'int', 'double', or 'string'"));
        }

        var normalized = typeName.Trim();

        if (string.Equals(normalized, "cstring", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "cstr", StringComparison.OrdinalIgnoreCase))
        {
            if (passingMode != NativeParameterPassingMode.In)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh::runtime::unsupported_native_byref_string",
                    Title: "By-ref native string parameters need an explicit pointer type.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: span,
                    Label: "use 'nint', 'ptr', or a buffer-backed struct type here",
                    Help: "borrowed `cstring` works for input parameters and returns, but `out`/`ref` string marshalling is not supported yet."));
            }

            return typeof(string);
        }

        var resolved = ResolveTypeName(normalized);

        if (resolved is null || !IsSupportedNativeInteropType(resolved))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::unsupported_native_interop_type",
                Title: $"Native interop does not currently support '{typeName}'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: $"'{typeName}' is not supported here",
                Help: "start with primitive CLR types like int, long, float, double, bool, string, IntPtr, UIntPtr, or a struct with sequential/explicit layout."));
        }

        if (passingMode != NativeParameterPassingMode.In && resolved == typeof(string))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::unsupported_native_byref_string",
                Title: "By-ref native string parameters need an explicit pointer type.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: "use 'nint', 'ptr', or a buffer-backed struct type here",
                Help: "plain `string` is only supported for input parameters today."));
        }

        return resolved;
    }

    private NativeFunctionReturnDefinition ResolveNativeInteropReturnType(
        string? typeName,
        string sourceName,
        string sourceText,
        TextSpan span)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return new NativeFunctionReturnDefinition("void", typeof(void), NativeFunctionReturnKind.Default);
        }

        var normalized = typeName.Trim();

        if (string.Equals(normalized, "cstring", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "cstr", StringComparison.OrdinalIgnoreCase))
        {
            return new NativeFunctionReturnDefinition(normalized, typeof(IntPtr), NativeFunctionReturnKind.CString);
        }

        if (string.Equals(normalized, "string", StringComparison.OrdinalIgnoreCase))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::unsupported_native_string_return",
                Title: "Native string returns need an explicit interop string type.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: "use 'cstring' for a borrowed NUL-terminated C string, or 'nint' for a raw pointer",
                Help: "plain 'string' is supported for native parameters, but return values need explicit ownership semantics."));
        }

        var resolved = ResolveNativeInteropParameterType(typeName, NativeParameterPassingMode.In, sourceName, sourceText, span, "return type");
        return new NativeFunctionReturnDefinition(normalized, resolved, NativeFunctionReturnKind.Default);
    }

    private static CallingConvention ResolveNativeCallingConvention(
        string? name,
        string sourceName,
        string sourceText,
        TextSpan span)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return CallingConvention.Cdecl;
        }

        return name.Trim().ToLowerInvariant() switch
        {
            "cdecl" => CallingConvention.Cdecl,
            "stdcall" => CallingConvention.StdCall,
            "thiscall" => CallingConvention.ThisCall,
            "fastcall" => CallingConvention.FastCall,
            "winapi" => CallingConvention.Winapi,
            _ => throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::unsupported_native_calling_convention",
                Title: $"Native interop does not support calling convention '{name}'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: "use cdecl, stdcall, thiscall, fastcall, or winapi")),
        };
    }

    private static bool IsSupportedNativeInteropType(Type type)
    {
        return NativeInteropUtilities.IsSupportedInteropType(type);
    }

    private string GetExecutionDirectory(string sourceName)
    {
        if (!string.IsNullOrWhiteSpace(sourceName) &&
            !sourceName.StartsWith('<') &&
            !sourceName.StartsWith("repl_entry", StringComparison.OrdinalIgnoreCase))
        {
            var resolvedSource = PathUtilities.ResolvePath(Runtime.CurrentDirectory, sourceName);
            var directory = Path.GetDirectoryName(resolvedSource);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }

        return Runtime.CurrentDirectory;
    }

    private static async Task<string> BuildProjectAndResolveAssemblyPathAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException($"Project '{projectPath}' was not found.", projectPath);
        }

        var targetPath = await RunDotNetForOutputAsync(
            $"msbuild {QuoteArgument(projectPath)} -nologo -getProperty:TargetPath",
            Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory,
            cancellationToken);

        if (!File.Exists(targetPath))
        {
            await RunDotNetAsync(
                $"build {QuoteArgument(projectPath)} -nologo -clp:ErrorsOnly",
                Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory,
                cancellationToken);

            targetPath = await RunDotNetForOutputAsync(
                $"msbuild {QuoteArgument(projectPath)} -nologo -getProperty:TargetPath",
                Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory,
                cancellationToken);
        }

        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException($"Built project '{projectPath}' did not produce a loadable assembly.", targetPath);
        }

        return targetPath;
    }

    private static async Task RunDotNetAsync(string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException((await standardError).Trim().Length > 0
            ? (await standardError).Trim()
            : (await standardOutput).Trim());
    }

    private static async Task<string> RunDotNetForOutputAsync(string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = (await standardOutput).Trim();

        if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
        {
            return output;
        }

        var error = (await standardError).Trim();
        throw new InvalidOperationException(error.Length > 0 ? error : output);
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static ToshDiagnosticException CreateLoopControlDiagnostic(
        string sourceName,
        string sourceText,
        TextSpan span,
        string keyword,
        string code,
        string title)
    {
        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: code,
            Title: title,
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"'{keyword}' does not have an enclosing loop to control"));
    }

    private ToshDiagnosticException CreateThrownValueDiagnostic(
        string sourceName,
        string sourceText,
        ThrowSignalException signal)
    {
        var title = signal.Value switch
        {
            null => "An error was thrown.",
            ICommandResult result => result.Message,
            Exception exception => exception.Message,
            _ => Runtime.Formatter.Format(signal.Value),
        };

        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh::runtime::throw",
            Title: title,
            SourceName: sourceName,
            SourceText: sourceText,
            Span: signal.Span,
            Label: "an unhandled value was thrown here"));
    }

    private static string ExtractSourceText(string sourceText, int start, int end)
    {
        if (string.IsNullOrEmpty(sourceText))
        {
            return string.Empty;
        }

        var boundedStart = Math.Clamp(start, 0, sourceText.Length);
        var boundedEnd = Math.Clamp(end, boundedStart, sourceText.Length);
        return sourceText[boundedStart..boundedEnd];
    }

    private bool TryResolveAutoCdDirectory(string name, out string resolvedPath)
    {
        if (name.StartsWith('~'))
        {
            var expanded = PathUtilities.ResolvePath(Runtime.CurrentDirectory, name);

            if (Directory.Exists(expanded))
            {
                resolvedPath = expanded;
                return true;
            }
        }

        var candidate = Path.Combine(Runtime.CurrentDirectory, name);

        if (Directory.Exists(candidate))
        {
            resolvedPath = Path.GetFullPath(candidate);
            return true;
        }

        resolvedPath = string.Empty;
        return false;
    }

    private sealed class AutoCdCommand : IShellCommand
    {
        private readonly string _resolvedPath;

        public AutoCdCommand(string resolvedPath)
        {
            _resolvedPath = resolvedPath;
        }

        public string Name => "cd";
        public string Description => "Auto-cd into a directory.";
        public string Usage => "cd [path]";

        public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            var directoryInfo = new DirectoryInfo(_resolvedPath);

            if (!directoryInfo.Exists)
            {
                throw new InvalidOperationException($"Directory '{_resolvedPath}' does not exist.");
            }

            context.Runtime.CurrentDirectory = directoryInfo.FullName;
            yield return directoryInfo;
        }
    }

    private sealed class EngineBlockExecutor : IShellBlockExecutor
    {
        private readonly ToshEngine _engine;

        public EngineBlockExecutor(ToshEngine engine)
        {
            _engine = engine;
        }

        public async IAsyncEnumerable<object?> ExecuteAsync(
            ShellBlock block,
            IReadOnlyDictionary<string, object?> locals,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (block.Syntax is not BlockSyntax syntax)
            {
                throw new InvalidOperationException("This runtime cannot execute the provided block.");
            }

            await foreach (var value in _engine.ExecuteBlockAsync(block.SourceName, block.SourceText, syntax, cancellationToken, locals)
                               .WithCancellation(cancellationToken))
            {
                yield return value;
            }
        }
    }

    private sealed class ScopeFrame : IDisposable
    {
        private readonly Stack<LexicalScope> _scopes;
        private readonly ShellEventBus? _eventBus;

        public ScopeFrame(Stack<LexicalScope> scopes, ShellEventBus? eventBus = null)
        {
            _scopes = scopes;
            _eventBus = eventBus;
        }

        public void Dispose()
        {
            var scope = _scopes.Pop();

            if (_eventBus is not null && scope.LocalEventNames.Count > 0)
            {
                foreach (var eventName in scope.LocalEventNames)
                {
                    _eventBus.RemoveAll(eventName);
                }
            }
        }
    }

    private sealed class ScopeFrames : IDisposable
    {
        public static readonly ScopeFrames Empty = new(Array.Empty<IDisposable>());

        private readonly IReadOnlyList<IDisposable> _frames;

        public ScopeFrames(IReadOnlyList<IDisposable> frames)
        {
            _frames = frames;
        }

        public void Dispose()
        {
            for (var index = _frames.Count - 1; index >= 0; index--)
            {
                _frames[index].Dispose();
            }
        }
    }

    private enum RequireTargetKind
    {
        Script,
        Assembly,
        Project,
    }

    private sealed record RequireTarget(RequireTargetKind Kind, string ResolvedPath, string CacheKey);

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

    private static bool ShouldAutoMaterializeListTarget(string methodName)
    {
        return methodName switch
        {
            "Add" => true,
            "AddRange" => true,
            "Insert" => true,
            "InsertRange" => true,
            _ => false,
        };
    }

    private sealed record VariableBinding(object? Value, bool ReplayAsPipeline, bool IsAllocatedOnly);

}
