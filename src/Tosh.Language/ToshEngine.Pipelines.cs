using System.Collections;
using System.Text;
using Tosh.Runtime;
using Tosh.Language.Binding;
using Tosh.Language.Parsing;

namespace Tosh.Language;

/// <summary>
/// Pipelines: running the stages, applying redirection, backgrounding, and settling the
/// exit code once a pipeline finishes.
///
/// Moved out of ToshEngine.cs by `TOAST-0005`. Every member moved **verbatim**.
///
/// `RedirectionIncludesError` lives here rather than with the diagnostics, which is
/// where its name would have put it. It asks whether a redirection covers the error
/// stream — plumbing, not diagnosis — and it sits next to `RedirectionIncludesOutput`,
/// which nothing would have mistaken for diagnostic code.
/// </summary>
public sealed partial class ToshEngine
{

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

        // Resolve input redirection for background pipelines.
        if (statement.Pipeline.InputRedirection is { } bgInputRedirection)
        {
            var inputTarget = await EvaluateArgumentAsync(sourceName, sourceText, bgInputRedirection.Source, cancellationToken);
            var inputPath = ResolveInputRedirectionPath(sourceName, sourceText, bgInputRedirection, inputTarget);
            initialInput = await AsyncEnumerableExtensions.ToListAsync(ReadLinesAsync(inputPath, cancellationToken), cancellationToken);
        }

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
                Code: "tosh.runtime.background_pipeline_requires_command",
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
                    Code: "tosh.runtime.background_pipeline_not_supported",
                    Title: "Background jobs currently support an optional input expression followed by external command stages only.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: stage.Span,
                    Label: "this stage is not an external command"));
            }

            var command = ResolveCommand(sourceName, sourceText, commandSyntax);

            if (command is not IExternalProcessCommand externalCommand)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.background_command_must_be_external",
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
                arguments = ExpandCommandArguments(command, evaluatedArguments, sourceName, sourceText);
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
                LanguageRuntime.CurrentDirectory,
                processStages,
                initialInput,
                redirections));

        LanguageRuntime.ExecutionObserver.SetLastResult(job.ToInfo());
        LanguageRuntime.ExecutionObserver.SetLastExitCode(0);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluatePipelineWithRedirectionAsync(
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        IAsyncEnumerable<object?>? initialInput = null,
        IReadOnlyList<object?>? firstCommandArguments = null,
        bool outputIsCaptured = false)
    {
        // Resolve input redirection (in< / i<) before executing the pipeline.
        if (pipeline.InputRedirection is { } inputRedirection)
        {
            var inputTarget = await EvaluateArgumentAsync(sourceName, sourceText, inputRedirection.Source, cancellationToken);
            var inputPath = ResolveInputRedirectionPath(sourceName, sourceText, inputRedirection, inputTarget);
            initialInput = ReadLinesAsync(inputPath, cancellationToken);
        }

        if (pipeline.Redirections is null or { Count: 0 })
        {
            await foreach (var value in EvaluatePipelineAsync(sourceName, sourceText, pipeline, cancellationToken, initialInput, firstCommandArguments, outputIsCaptured: outputIsCaptured)
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
        IToastStream? originalOutput = null;
        IToastStream? originalError = null;
        TextWriter? originalSessionOutput = null;
        TextWriter? originalSessionError = null;

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
                        Code: "tosh.runtime.redirection_target_unavailable",
                        Title: $"Cannot open '{redirection.Path}' for redirection: {exception.Message}",
                        SourceName: null,
                        SourceText: null,
                        Span: null,
                        Label: "this redirection target could not be opened for writing",
                        Help: "check that the directory exists and is writable. Redirection creates the "
                            + "file but not the directories above it."));
                }

                var writer = TextWriter.Synchronized(new StreamWriter(stream, RedirectionEncoding));
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

            // `TOAST-0015`. The destination is a Tōast stream on the *language* runtime,
            // not the shell session's `TextWriter`. A host with no session redirects the
            // same way, because there is nothing shell-shaped left in the path — the
            // session's writer is one destination among files, pipes and buffers rather
            // than the thing being replaced.
            if (outputTargets.Count > 0)
            {
                originalOutput = LanguageRuntime.Output;
                LanguageRuntime.Output = ToastStreams.Composite(
                    outputTargets.Select(ToastStreams.FromWriter).ToArray());

                // The session's writer is moved too, and that half is a **shell**
                // mechanism rather than a language one: TōSh commands write diagnostics
                // and passthrough text to `Runtime.Error`/`Runtime.Output`, and an
                // external process inherits handles decided from them. The language no
                // longer reads either — it writes to the stream above — so a host with no
                // session redirects with this whole branch inert.
                originalSessionOutput = Runtime.Output;
                Runtime.Output = CreateCompositeWriter(outputTargets);
            }

            if (errorTargets.Count > 0)
            {
                originalError = LanguageRuntime.Error;
                LanguageRuntime.Error = ToastStreams.Composite(
                    errorTargets.Select(ToastStreams.FromWriter).ToArray());

                originalSessionError = Runtime.Error;
                Runtime.Error = CreateCompositeWriter(errorTargets);
            }

            var hasOutputRedirection = outputTargets.Count > 0;

            // Deliberately NOT captured: this is the top-level display path, and terminal
            // passthrough is exactly what it is for. Redirection is handled below from the
            // values the pipeline yields (TS-P1-30).
            await foreach (var value in EvaluatePipelineAsync(sourceName, sourceText, pipeline, cancellationToken, initialInput, firstCommandArguments, outputIsCaptured: outputIsCaptured)
                               .WithCancellation(cancellationToken))
            {
                if (hasOutputRedirection)
                {
                    var text = value switch
                    {
                        ShellTextLine line => line.Text,
                        // Same contract as an interpolation hole (`TOAST-0014`, Appendix B
                        // question 3). Decided by measuring what this wrote: a nested list
                        // put a CLR type name and *newlines* into the file, and an enum put
                        // seven lines of its own implementation there. That is not a
                        // serialisation format worth preserving, and multi-line values
                        // corrupt every line-oriented reader downstream.
                        _ => ToastRenderer.Render(value),
                    };

                    await LanguageRuntime.Output.WriteTextLineAsync(text, cancellationToken);
                    await LanguageRuntime.Output.FlushAsync(cancellationToken);
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
            // Session first, language second, and the order matters: assigning
            // `Runtime.Output` re-derives `Language.Output` from the writer, so restoring
            // the language destination first would leave an equivalent-but-different
            // adapter in place instead of the exact one that was saved.
            if (originalSessionOutput is not null)
            {
                Runtime.Output = originalSessionOutput;
            }

            if (originalSessionError is not null)
            {
                Runtime.Error = originalSessionError;
            }

            if (originalOutput is not null)
            {
                LanguageRuntime.Output = originalOutput;
            }

            if (originalError is not null)
            {
                LanguageRuntime.Error = originalError;
            }

            await FlushBufferedPipelineRedirectionsAsync(bufferedPlans.Values, cancellationToken);

            foreach (var writer in disposableWriters)
            {
                await writer.DisposeAsync();
            }
        }
    }

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
                        Code: "tosh.runtime.redirection_target_unavailable",
                        Title: $"Cannot open '{redirection.Path}' for redirection: {exception.Message}",
                        SourceName: null,
                        SourceText: null,
                        Span: null,
                        Label: "this redirection target could not be opened for writing",
                        Help: "check that the directory exists and is writable. Redirection creates the "
                            + "file but not the directories above it."));
                }

                await using var writer = new StreamWriter(stream, RedirectionEncoding);

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
                Code: "tosh.runtime.redirection_target_null",
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
            string text => ShellPathArguments.Expand(LanguageRuntime.CurrentDirectory, text),
            _ => [PathUtilities.ResolvePath(LanguageRuntime.CurrentDirectory, targetPath.ToString() ?? string.Empty)],
        };

        if (resolvedPaths.Count == 1)
        {
            return resolvedPaths[0];
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.redirection_target_not_single_path",
            Title: "Redirection targets must resolve to exactly one path.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: redirection.Span,
            Label: "this target resolved to multiple paths",
            Help: "use a single file path or quote the pattern if you meant a literal name."));
    }

    private string ResolveInputRedirectionPath(
        string sourceName,
        string sourceText,
        InputRedirectionSyntax redirection,
        object? sourcePath)
    {
        if (sourcePath is null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.input_redirection_source_null",
                Title: "Input redirection source cannot be null.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: redirection.Span,
                Label: "this input redirection source evaluated to null"));
        }

        var resolved = sourcePath switch
        {
            FileSystemInfo fileSystemInfo => fileSystemInfo.FullName,
            FileSystemEntry entry => entry.FullName,
            string text => PathUtilities.ResolvePath(LanguageRuntime.CurrentDirectory, text),
            _ => PathUtilities.ResolvePath(LanguageRuntime.CurrentDirectory, sourcePath.ToString() ?? string.Empty),
        };

        if (!File.Exists(resolved))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.input_redirection_source_not_found",
                Title: $"Input redirection source '{resolved}' does not exist.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: redirection.Span,
                Label: "this file does not exist"));
        }

        return resolved;
    }

    private IAsyncEnumerable<object?> EvaluatePipelineAsync(
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        CancellationToken cancellationToken,
        IAsyncEnumerable<object?>? initialInput = null,
        IReadOnlyList<object?>? firstCommandArguments = null,
        PipelineExitStatusTracker? pipelineExitStatusTracker = null,
        bool outputIsCaptured = false)
    {
        var ownsTracker = pipelineExitStatusTracker is null;
        pipelineExitStatusTracker ??= new PipelineExitStatusTracker(LanguageRuntime.Options.Pipefail);
        IAsyncEnumerable<object?> current = initialInput ?? AsyncEnumerableExtensions.Empty<object?>();
        IReadOnlyList<object?>? pendingFirstCommandArguments = firstCommandArguments;
        var isPipelined = pipeline.Stages.Count > 1 || initialInput is not null;

        // If lowering recognised a fusable trailing pattern (e.g.
        // `... | sort | first N`), execute the upstream stages normally
        // and replace the trailing stages with a specialised iterator.
        var fusion = pipeline.Fusion;
        var stageCount = pipeline.Stages.Count;
        var stagesToRun = fusion is null ? stageCount : stageCount - GetStagesConsumed(fusion);

        for (int i = 0; i < stagesToRun; i++)
        {
            var stage = pipeline.Stages[i];
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
                    cancellationToken,
                    outputIsCaptured: outputIsCaptured),
                PipeForwardStageSyntax pipeForward => ExecutePipeForwardStageAsync(
                    sourceName,
                    sourceText,
                    pipeForward,
                    current,
                    pipelineExitStatusTracker,
                    cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported pipeline stage syntax: {stage.GetType().Name}."),
            };

            if (stage is CommandSyntax && pendingFirstCommandArguments is not null)
            {
                pendingFirstCommandArguments = null;
            }
        }

        if (fusion is SortFirstFusion sortFirst)
        {
            current = ExecuteSortFirstFusionAsync(current, sortFirst, cancellationToken);
        }

        // `TS-P2-113`. The finaliser is another iterator, so wrapping erases the
        // `PreExpandedSequence` type that says this stream has already had its
        // collection enumerated into it. Re-applied here, or every consumer of a
        // whole pipeline — `for` among them — expands it a second time.
        var finalized = FinalizePipelineExitCodeAsync(current, pipelineExitStatusTracker, ownsTracker, cancellationToken);

        return ShellIterationUtilities.CarryShapeMarker(current, finalized);
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
            // Once `exit` has spoken, its code is the answer. The tracker records the status of
            // the pipeline that just ran — and `exit 3` is itself a command that succeeds — so
            // letting it write here overwrote the 3 with a 0 and every script exited cleanly no
            // matter what it asked for.
            if (ownsTracker && tracker.HasExitCodes && !Host.ExitRequested)
            {
                var exitCode = tracker.GetFinalExitCode();
                LanguageRuntime.ExecutionObserver.SetLastExitCode(exitCode);

                if (exitCode != 0 && LanguageRuntime.Options.ExitOnError)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.nonzero_exit_code",
                        Title: $"Command exited with code {exitCode}.",
                        Help: "A command in the pipeline returned a non-zero exit code while Shell.ExitOnError is enabled. " +
                              "Set $tosh.Config.Shell.ExitOnError = false to disable this behavior."));
                }
            }
        }
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

    /// <summary>
    /// Returns the textual span covering a pipeline's stages, used to narrow
    /// runtime diagnostics so the underline points at the offending value
    /// rather than the entire <c>var</c>/assignment statement.
    /// </summary>
    private static TextSpan? GetPipelineSpan(PipelineSyntax? pipeline)
    {
        if (pipeline is null || pipeline.Stages.Count == 0)
        {
            return null;
        }

        var first = pipeline.Stages[0].Span;
        var last = pipeline.Stages[^1].Span;
        return TextSpan.FromBounds(first.Start, last.End);
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
}
