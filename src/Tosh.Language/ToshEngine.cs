using System.Collections;
using Tosh.Core;
using Tosh.Core.Commands;
using Tosh.Language.Commands;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed class ToshEngine : IShellEvaluator
{
    private readonly Stack<Dictionary<string, object?>> _localScopes = new();

    public ToshEngine(ToshRuntime? runtime = null)
    {
        Runtime = runtime ?? ToshRuntime.CreateDefault();
        Runtime.BlockExecutor = new EngineBlockExecutor(this);
        Runtime.Evaluator = this;

        if (!Runtime.Commands.TryGet("source", out _))
        {
            Runtime.Commands.Register(new SourceCommand(this));
        }
    }

    public ToshRuntime Runtime { get; }

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

    private IAsyncEnumerable<object?> EvaluateAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return EvaluateParseResultAsync(parseResult, cancellationToken);
    }

    private async IAsyncEnumerable<object?> EvaluateParseResultAsync(
        ParseResult parseResult,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var values = new List<object?>();

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
            PipelineStatementSyntax pipeline => EvaluatePipelineAsync(sourceName, sourceText, pipeline.Pipeline, cancellationToken),
            VariableDeclarationStatementSyntax declaration => EvaluateVariableDeclarationAsync(sourceName, sourceText, declaration, cancellationToken),
            AliasStatementSyntax alias => EvaluateAliasDefinitionAsync(sourceName, sourceText, alias, cancellationToken),
            UsingStatementSyntax @using => EvaluateUsingStatementAsync(sourceName, sourceText, @using, cancellationToken),
            ReturnStatementSyntax @return => EvaluateReturnStatementAsync(sourceName, sourceText, @return, cancellationToken),
            BreakStatementSyntax @break => EvaluateBreakStatementAsync(@break),
            ContinueStatementSyntax @continue => EvaluateContinueStatementAsync(@continue),
            VariableAssignmentStatementSyntax assignment => EvaluateVariableAssignmentAsync(sourceName, sourceText, assignment, cancellationToken),
            FunctionDefinitionStatementSyntax function => EvaluateFunctionDefinitionAsync(sourceName, sourceText, function, cancellationToken),
            IfStatementSyntax @if => EvaluateIfStatementAsync(sourceName, sourceText, @if, cancellationToken),
            ForStatementSyntax @for => EvaluateForStatementAsync(sourceName, sourceText, @for, cancellationToken),
            WhileStatementSyntax @while => EvaluateWhileStatementAsync(sourceName, sourceText, @while, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported statement syntax: {statement.GetType().Name}."),
        };
    }

    private async IAsyncEnumerable<object?> EvaluateScriptStatementAsync(
        string sourceName,
        string sourceText,
        ScriptStatementSyntax script,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var statement in script.Statements)
        {
            await foreach (var value in EvaluateStatementAsync(sourceName, sourceText, statement, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return value;
            }
        }
    }

    private async IAsyncEnumerable<object?> EvaluateVariableDeclarationAsync(
        string sourceName,
        string sourceText,
        VariableDeclarationStatementSyntax declaration,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DeclareVariable(
            declaration.Name,
            await EvaluateVariableBindingAsync(sourceName, sourceText, declaration.Value, cancellationToken));
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateAliasDefinitionAsync(
        string sourceName,
        string sourceText,
        AliasStatementSyntax alias,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!alias.Value.Stages.OfType<CommandSyntax>().Any())
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::alias_requires_command",
                Title: "Aliases must contain at least one command stage.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: alias.Span,
                Label: "this alias does not contain a command to invoke",
                Help: "use 'def' for more complex expression-only or statement-oriented definitions."));
        }

        var definition = new AliasDefinition(
            alias.Name,
            alias.Value,
            ExtractSourceText(sourceText, alias.Value.Stages.First().Span.Start, alias.Value.Stages.Last().Span.End),
            sourceName,
            sourceText,
            alias.Span);
        Runtime.Commands.RegisterOrReplace(new AliasCommand(this, definition));
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateUsingStatementAsync(
        string sourceName,
        string sourceText,
        UsingStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (statement.IsFileImport)
        {
            if (statement.Alias is not null)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh::runtime::module_alias_not_supported",
                    Title: "Aliasing imported script files is not supported yet.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: statement.Span,
                    Label: "remove the alias from this file import",
                    Help: "use 'using \"./module.tosh\"' to load the file for now."));
            }

            var path = ResolveModulePath(statement.Target, Runtime.CurrentDirectory);

            if (!Runtime.LoadedModules.Add(path))
            {
                yield break;
            }

            var moduleSource = await File.ReadAllTextAsync(path, cancellationToken);

            await foreach (var _ in EvaluateAsync(moduleSource, path, cancellationToken).WithCancellation(cancellationToken))
            {
            }

            yield break;
        }

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

    private async IAsyncEnumerable<object?> EvaluateVariableAssignmentAsync(
        string sourceName,
        string sourceText,
        VariableAssignmentStatementSyntax assignment,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var value = await EvaluateVariableBindingAsync(sourceName, sourceText, assignment.Value, cancellationToken);

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

    private async IAsyncEnumerable<object?> EvaluateFunctionDefinitionAsync(
        string sourceName,
        string sourceText,
        FunctionDefinitionStatementSyntax function,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var duplicateParameters = function.Parameters
            .GroupBy(parameter => parameter.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateParameters is not null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh::runtime::duplicate_function_parameter",
                Title: $"Function '{function.Name}' defines parameter '{duplicateParameters.Key}' more than once.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: duplicateParameters.First().Span,
                Label: $"'{duplicateParameters.Key}' is declared multiple times"));
        }

        var definition = new FunctionDefinition(
            function.Name,
            function.Parameters
                .Select(parameter => new FunctionParameterDefinition(parameter.Name, parameter.TypeName, parameter.Span))
                .ToArray(),
            function.ReturnTypeName,
            function.Body,
            sourceName,
            sourceText,
            function.Span);
        Runtime.Commands.RegisterOrReplace(new FunctionCommand(this, definition));
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
        await foreach (var item in EvaluatePipelineAsync(sourceName, sourceText, statement.Source, cancellationToken)
                           .WithCancellation(cancellationToken))
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
                                       [statement.VariableName] = item,
                                       ["it"] = item,
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
            return new VariableBinding(raw.Value, ReplayAsPipeline: false);
        }

        var values = await AsyncEnumerableExtensions.ToListAsync(
            EvaluatePipelineAsync(sourceName, sourceText, pipeline, cancellationToken),
            cancellationToken);

        return values.Count switch
        {
            0 => new VariableBinding(null, ReplayAsPipeline: false),
            1 => new VariableBinding(values[0], ReplayAsPipeline: false),
            _ => new VariableBinding(values.ToArray(), ReplayAsPipeline: true),
        };
    }

    private IAsyncEnumerable<object?> EvaluatePipelineAsync(
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        CancellationToken cancellationToken,
        IAsyncEnumerable<object?>? initialInput = null,
        IReadOnlyList<object?>? firstCommandArguments = null)
    {
        IAsyncEnumerable<object?> current = initialInput ?? AsyncEnumerableExtensions.Empty<object?>();
        IReadOnlyList<object?>? pendingFirstCommandArguments = firstCommandArguments;

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
                    cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported pipeline stage syntax: {stage.GetType().Name}."),
            };

            if (stage is CommandSyntax && pendingFirstCommandArguments is not null)
            {
                pendingFirstCommandArguments = null;
            }
        }

        return current;
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

        yield return value;
    }

    private async IAsyncEnumerable<object?> ExecuteCommandSyntaxAsync(
        string sourceName,
        string sourceText,
        CommandSyntax commandSyntax,
        IAsyncEnumerable<object?> input,
        IReadOnlyList<object?>? additionalArguments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var command = ResolveCommand(sourceName, sourceText, commandSyntax);

        IReadOnlyList<object?> arguments;

        try
        {
            arguments = await EvaluateArgumentsAsync(sourceName, sourceText, commandSyntax.Arguments, cancellationToken);

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
        var context = new CommandContext(Runtime, input, arguments, cancellationToken, invocation);

        await using var enumerator = command.ExecuteAsync(context).GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            object? item;

            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    yield break;
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
            catch (Exception exception)
            {
                throw CreateCommandDiagnostic(sourceName, sourceText, commandSyntax, exception);
            }

            yield return item;
        }
    }

    private IShellCommand ResolveCommand(
        string sourceName,
        string sourceText,
        CommandSyntax commandSyntax)
    {
        if (Runtime.Commands.TryGet(commandSyntax.Name, out var command))
        {
            return command;
        }

        var external = ExternalCommandResolver.Resolve(Runtime.CurrentDirectory, commandSyntax.Name);

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
            ExternalCommandLookupStatus.IsDirectory =>
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh::runtime::external_command_is_directory",
                    Title: $"'{external.ResolvedPath ?? commandSyntax.Name}' is a directory, not an executable file.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: commandSyntax.Span,
                    Label: $"'{commandSyntax.Name}' does not refer to a runnable program")),
            _ =>
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh::runtime::unknown_command",
                    Title: $"Command '{commandSyntax.Name}' was not found.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: commandSyntax.Span,
                    Label: $"'{commandSyntax.Name}' is not a built-in, function, alias, or executable",
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
        switch (argument)
        {
            case BarewordArgumentSyntax bareword:
                return bareword.Value;

            case LiteralArgumentSyntax literal:
                return literal.Value;

            case VariableReferenceArgumentSyntax variableReference:
            {
                if (TryGetVariableValue(variableReference.Name, out var value))
                {
                    return value;
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
                var type = Runtime.TypeResolver.Resolve(newObject.TypeName)
                           ?? throw new InvalidOperationException($"Unable to resolve type '{newObject.TypeName}'.");
                var constructorArguments = await EvaluateArgumentsAsync(sourceName, sourceText, newObject.Arguments, cancellationToken);
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

            case ListLiteralArgumentSyntax listLiteral:
            {
                var items = await EvaluateArgumentsAsync(sourceName, sourceText, listLiteral.Items, cancellationToken);
                return items.ToArray();
            }

            case BlockArgumentSyntax blockArgument:
            {
                return new ShellBlock(blockArgument.Block, sourceName, sourceText, blockArgument.Span);
            }

            case MemberProjectionArgumentSyntax projection:
            {
                return new ProjectedMemberSelection(projection.MemberPaths);
            }

            case MemberAccessArgumentSyntax memberAccess:
            {
                var target = await EvaluateArgumentAsync(sourceName, sourceText, memberAccess.Target, cancellationToken);
                return Runtime.ObjectAccessor.GetValue(target, memberAccess.MemberPath);
            }

            case MethodCallArgumentSyntax methodCall:
            {
                var target = await EvaluateArgumentAsync(sourceName, sourceText, methodCall.Target, cancellationToken);

                if (target is ShellTextLine textLine)
                {
                    target = textLine.Text;
                }

                if (target is null)
                {
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

            case OperatorArgumentSyntax operation:
            {
                var left = await EvaluateArgumentAsync(sourceName, sourceText, operation.Left, cancellationToken);
                var right = await EvaluateArgumentAsync(sourceName, sourceText, operation.Right, cancellationToken);
                return OperatorEvaluator.EvaluateBinary(left, operation.Operator, right);
            }

            case PredicateBlockArgumentSyntax predicateBlock:
            {
                var clauses = new List<WherePredicateClause>(predicateBlock.Clauses.Count);

                foreach (var clause in predicateBlock.Clauses)
                {
                    clauses.Add(new WherePredicateClause(
                        clause.MemberPath,
                        clause.Operator,
                        await EvaluateArgumentAsync(sourceName, sourceText, clause.Expected, cancellationToken),
                        clause.Span,
                        clause.OperatorSpan));
                }

                return new WherePredicateBlock(clauses, predicateBlock.Span);
            }

            default:
                throw new InvalidOperationException($"Unsupported argument syntax: {argument.GetType().Name}.");
        }
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
        var segments = SplitQualifiedPath(path);

        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var type = Runtime.TypeResolver.Resolve(string.Join('.', segments.Take(prefixLength)));

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
        var segments = SplitQualifiedPath(path);
        matchedType = false;

        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var type = Runtime.TypeResolver.Resolve(string.Join('.', segments.Take(prefixLength)));

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

    private bool TryGetVariableValue(string name, out object? value)
    {
        if (TryGetVariableBinding(name, out var binding))
        {
            value = binding.Value;
            return true;
        }

        value = null;
        return false;
    }

    private bool TryGetVariableBinding(string name, out VariableBinding binding)
    {
        foreach (var scope in _localScopes)
        {
            if (scope.TryGetValue(name, out var rawValue))
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

        binding = new VariableBinding(null, ReplayAsPipeline: false);
        return false;
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

    private void DeclareVariable(string name, VariableBinding binding)
    {
        if (_localScopes.Count > 0)
        {
            _localScopes.Peek()[name] = binding;
            return;
        }

        Runtime.Variables[name] = binding;
    }

    private bool TryAssignVariable(string name, VariableBinding binding)
    {
        foreach (var scope in _localScopes)
        {
            if (!scope.ContainsKey(name))
            {
                continue;
            }

            scope[name] = binding;
            return true;
        }

        if (Runtime.Variables.ContainsKey(name))
        {
            Runtime.Variables[name] = binding;
            return true;
        }

        return false;
    }

    private IDisposable PushScope(IReadOnlyDictionary<string, object?> locals)
    {
        _localScopes.Push(new Dictionary<string, object?>(locals, StringComparer.Ordinal));
        return new ScopeFrame(_localScopes);
    }

    private static VariableBinding ToVariableBinding(object? value)
    {
        return value is VariableBinding binding
            ? binding
            : new VariableBinding(value, ReplayAsPipeline: false);
    }

    internal IAsyncEnumerable<object?> ExecuteAliasAsync(AliasDefinition definition, CommandContext context)
    {
        return EvaluatePipelineAsync(
            definition.SourceName,
            definition.SourceText,
            definition.Pipeline,
            context.CancellationToken,
            context.Input,
            context.Arguments);
    }

    internal async IAsyncEnumerable<object?> ExecuteFunctionAsync(
        FunctionDefinition definition,
        CommandContext context)
    {
        var inputItems = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        var locals = BindFunctionParameters(definition, context, inputItems);
        var initialInput = AsyncEnumerableExtensions.FromEnumerable(inputItems);
        var returnType = ResolveFunctionReturnType(definition, context);
        var values = new List<object?>();

        try
        {
            await foreach (var value in ExecuteBlockAsync(
                               definition.SourceName,
                               definition.SourceText,
                               definition.Body,
                               context.CancellationToken,
                               locals,
                               initialInput)
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

        foreach (var value in values)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return ConvertFunctionReturnValue(definition, context, returnType, value);
        }
    }

    private async IAsyncEnumerable<object?> ExecuteBlockAsync(
        string sourceName,
        string sourceText,
        BlockSyntax block,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? locals = null,
        IAsyncEnumerable<object?>? initialInput = null)
    {
        using var _ = PushScope(locals ?? new Dictionary<string, object?>(StringComparer.Ordinal));
        var pendingInput = initialInput;

        foreach (var statement in block.Statements)
        {
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

            var values = statement is PipelineStatementSyntax pipelineStatement && pendingInput is not null
                ? EvaluatePipelineAsync(sourceName, sourceText, pipelineStatement.Pipeline, cancellationToken, pendingInput)
                : EvaluateStatementAsync(sourceName, sourceText, statement, cancellationToken);

            await foreach (var value in values.WithCancellation(cancellationToken))
            {
                yield return value;
            }

            pendingInput = null;
        }
    }

    private Dictionary<string, object?> BindFunctionParameters(
        FunctionDefinition definition,
        CommandContext context,
        IReadOnlyList<object?> inputItems)
    {
        if (context.Arguments.Count != definition.Parameters.Count)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::function_argument_count_mismatch",
                title: $"Function '{definition.Name}' expects {definition.Parameters.Count} argument(s) but received {context.Arguments.Count}.",
                label: $"'{definition.Name}' requires {definition.Parameters.Count} argument(s)");
        }

        var locals = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["args"] = context.Arguments.ToArray(),
            ["input"] = inputItems.Count switch
            {
                0 => new VariableBinding(null, ReplayAsPipeline: false),
                1 => new VariableBinding(inputItems[0], ReplayAsPipeline: false),
                _ => new VariableBinding(inputItems.ToArray(), ReplayAsPipeline: true),
            },
        };

        for (var index = 0; index < definition.Parameters.Count; index++)
        {
            var parameter = definition.Parameters[index];
            var value = context.Arguments[index];

            if (parameter.TypeName is not null)
            {
                var parameterType = context.Runtime.TypeResolver.Resolve(parameter.TypeName)
                                   ?? throw context.CreateDiagnostic(
                                       code: "tosh::runtime::unknown_parameter_type",
                                       title: $"Parameter type '{parameter.TypeName}' could not be resolved.",
                                       label: $"the type annotation on '{parameter.Name}' could not be resolved",
                                       span: parameter.Span);

                if (!TypeConversion.TryConvert(value, parameterType, out var converted))
                {
                    throw context.CreateDiagnostic(
                        code: "tosh::runtime::parameter_type_conversion_failed",
                        title: $"Argument '{parameter.Name}' could not be converted to {parameterType.FullName ?? parameterType.Name}.",
                        argumentIndex: index,
                        label: $"'{parameter.Name}' expects {parameterType.Name}");
                }

                value = converted;
            }

            locals[parameter.Name] = value;
        }

        return locals;
    }

    private Type? ResolveFunctionReturnType(FunctionDefinition definition, CommandContext context)
    {
        if (definition.ReturnTypeName is null)
        {
            return null;
        }

        return context.Runtime.TypeResolver.Resolve(definition.ReturnTypeName)
               ?? throw context.CreateDiagnostic(
                   code: "tosh::runtime::unknown_return_type",
                   title: $"Return type '{definition.ReturnTypeName}' could not be resolved.",
                   label: $"the return type annotation on '{definition.Name}' could not be resolved",
                   span: definition.Span);
    }

    private object? ConvertFunctionReturnValue(
        FunctionDefinition definition,
        CommandContext context,
        Type? returnType,
        object? value)
    {
        if (returnType is null)
        {
            return value;
        }

        if (TypeConversion.TryConvert(value, returnType, out var converted))
        {
            return converted;
        }

        throw context.CreateDiagnostic(
            code: "tosh::runtime::return_type_conversion_failed",
            title: $"Function '{definition.Name}' returned a value that could not be converted to {returnType.FullName ?? returnType.Name}.",
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

    private static string ResolveModulePath(string target, string currentDirectory)
    {
        var candidate = PathUtilities.ResolvePath(currentDirectory, target);

        if (File.Exists(candidate))
        {
            return candidate;
        }

        if (!Path.HasExtension(candidate))
        {
            var withExtension = candidate + ".tosh";

            if (File.Exists(withExtension))
            {
                return withExtension;
            }
        }

        throw new FileNotFoundException($"Module file '{candidate}' was not found.", candidate);
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
        private readonly Stack<Dictionary<string, object?>> _scopes;

        public ScopeFrame(Stack<Dictionary<string, object?>> scopes)
        {
            _scopes = scopes;
        }

        public void Dispose()
        {
            _scopes.Pop();
        }
    }

    private sealed record VariableBinding(object? Value, bool ReplayAsPipeline);

}
