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
        var backgroundJobs = LanguageRuntime.BackgroundJobs;
        if (backgroundJobs is null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.background_jobs_not_supported",
                Title: "This host does not support background jobs.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: "background execution requires a host with job control"));
        }

        IReadOnlyList<object?>? initialInput = null;
        string? inputPath = null;
        var processStages = new List<ToastBackgroundProcessSpec>();
        var redirections = new List<ToastBackgroundRedirectionSpec>();
        var stages = statement.Pipeline.Stages;
        var stageIndex = 0;

        // Resolve input redirection for background pipelines. Every stage here is a program,
        // so the file goes to the first one as bytes (`TOSH-0012`).
        if (statement.Pipeline.InputRedirection is { } bgInputRedirection)
        {
            var inputTarget = await EvaluateArgumentAsync(sourceName, sourceText, bgInputRedirection.Source, cancellationToken);
            inputPath = ResolveInputRedirectionPath(sourceName, sourceText, bgInputRedirection, inputTarget);
        }

        if (stages.Count > 0 && stages[0] is ExpressionPipelineStageSyntax initialExpression)
        {
            // An input expression has always taken precedence over `in<`; it still does.
            inputPath = null;
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

            processStages.Add(new ToastBackgroundProcessSpec(externalCommand.ResolvedPath, arguments));
        }

        if (statement.Pipeline.Redirections is { Count: > 0 })
        {
            foreach (var redirection in statement.Pipeline.Redirections)
            {
                var targetPath = await EvaluateArgumentAsync(sourceName, sourceText, redirection.Target, cancellationToken);
                var path = ResolveRedirectionTargetPath(sourceName, sourceText, redirection, targetPath);
                redirections.Add(new ToastBackgroundRedirectionSpec(
                    path,
                    redirection.Stream switch
                    {
                        RedirectionStream.Output => ToastBackgroundRedirectionStream.Output,
                        RedirectionStream.Error => ToastBackgroundRedirectionStream.Error,
                        RedirectionStream.OutputThenError => ToastBackgroundRedirectionStream.OutputThenError,
                        _ => ToastBackgroundRedirectionStream.ErrorThenOutput,
                    },
                    redirection.Mode == RedirectionMode.Append
                        ? ToastBackgroundRedirectionMode.Append
                        : ToastBackgroundRedirectionMode.Truncate));
            }
        }

        var commandText = ExtractSourceSnippet(sourceText, statement.Span);
        var jobInfo = backgroundJobs.StartExternalPipeline(new ToastBackgroundPipelineRequest(
            commandText,
            LanguageRuntime.CurrentDirectory,
            processStages,
            initialInput,
            redirections,
            inputPath));

        LanguageRuntime.ExecutionObserver.SetLastResult(jobInfo);
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
        RawByteHandoff? inputBytes = null;

        // Resolve input redirection (in< / i<) before executing the pipeline.
        if (pipeline.InputRedirection is { } inputRedirection)
        {
            var inputTarget = await EvaluateArgumentAsync(sourceName, sourceText, inputRedirection.Source, cancellationToken);
            var inputPath = ResolveInputRedirectionPath(sourceName, sourceText, inputRedirection, inputTarget);
            inputBytes = new RawByteHandoff();
            initialInput = ReadInputRedirectionAsync(inputPath, inputBytes, cancellationToken);
        }

        if (pipeline.Redirections is null or { Count: 0 })
        {
            await foreach (var value in EvaluatePipelineAsync(sourceName, sourceText, pipeline, cancellationToken, initialInput, firstCommandArguments, outputIsCaptured: outputIsCaptured, initialRawInput: inputBytes)
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
        IToastStream? redirectedOutput = null;
        IToastStream? redirectedError = null;
        IDisposable? sessionRedirection = null;
        RawByteHandoff? outputBytes = null;
        RedirectedFileByteStream? outputFile = null;

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
                    outputFile = new RedirectedFileByteStream(writer, stream);
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
                redirectedOutput = ToastStreams.Composite(
                    outputTargets.Select(ToastStreams.FromWriter).ToArray());
                LanguageRuntime.Output = redirectedOutput;
            }

            if (errorTargets.Count > 0)
            {
                originalError = LanguageRuntime.Error;
                redirectedError = ToastStreams.Composite(
                    errorTargets.Select(ToastStreams.FromWriter).ToArray());
                LanguageRuntime.Error = redirectedError;
            }

            // TōSh commands and external-process plumbing still write through the
            // shell session. Mirror the language destinations through an optional host
            // capability; an embedded Tōast runtime receives an inert scope and never
            // needs a ToshRuntime (`TOAST-0006`).
            sessionRedirection = LanguageRuntime.SessionRedirection.Begin(
                redirectedOutput,
                redirectedError);

            var hasOutputRedirection = outputTargets.Count > 0;

            // `TOSH-0012`. When the output goes to exactly one file, a program in the last
            // stage writes its bytes there itself instead of yielding lines for the loop below
            // to re-encode. Not with several targets, and not for a buffered plan: those
            // receive text, and bytes that are not text have no faithful form there.
            if (outputTargets.Count == 1 && outputFile is not null)
            {
                outputBytes = new RawByteHandoff();
                outputBytes.Offer(new RawByteDestination(outputFile, ReaderCanLeave: false));
            }

            // Deliberately NOT captured: this is the top-level display path, and terminal
            // passthrough is exactly what it is for. Redirection is handled below from the
            // values the pipeline yields (TS-P1-30).
            await foreach (var value in EvaluatePipelineAsync(sourceName, sourceText, pipeline, cancellationToken, initialInput, firstCommandArguments, outputIsCaptured: outputIsCaptured, initialRawInput: inputBytes, finalRawOutput: outputBytes)
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
            outputBytes?.Withdraw();
            sessionRedirection?.Dispose();

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
        bool outputIsCaptured = false,
        RawByteHandoff? initialRawInput = null,
        RawByteHandoff? finalRawOutput = null)
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

        // `TOSH-0012`. A byte path runs only between two command stages, the only stages that
        // can be programs: an expression stage ignores its input and a pipe-forward stage turns
        // it into arguments. The last stage gets the caller's path — a redirected file — unless
        // fusion replaced it, in which case its output is not the pipeline's.
        var pendingRawInput = initialRawInput;

        for (int i = 0; i < stagesToRun; i++)
        {
            var stage = pipeline.Stages[i];
            var stageRawInput = stage is CommandSyntax ? pendingRawInput : null;
            RawByteHandoff? stageRawOutput = null;

            if (stage is CommandSyntax)
            {
                if (i + 1 < stagesToRun)
                {
                    stageRawOutput = pipeline.Stages[i + 1] is CommandSyntax ? new RawByteHandoff() : null;
                }
                else if (fusion is null)
                {
                    stageRawOutput = finalRawOutput;
                }
            }

            pendingRawInput = stageRawOutput;

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
                    outputIsCaptured: outputIsCaptured,
                    hasUpstream: i > 0 || initialInput is not null,
                    rawInput: stageRawInput,
                    rawOutput: stageRawOutput),
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

        // The same shorthand written as an expression rather than a bare name.
        // `prop Points = $vertices` parses as an expression stage, so it missed the
        // branch above and ran as a pipeline — which *enumerates* a collection and
        // then re-collects it by count. Reading a value silently changed its shape:
        // an empty array became null, and a one-element array became that element.
        // Reading a local cannot produce a stream, so there is nothing to collect.
        if (pipeline.Stages[0] is ExpressionPipelineStageSyntax expressionStage &&
            expressionStage.Expression is VariableReferenceArgumentSyntax variable &&
            locals.TryGetValue(variable.Name, out value))
        {
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Pulls one item without letting an exception cross an async-iterator catch boundary.
    /// Callers can therefore yield <see cref="CapturedEnumeratorMove.Value"/> immediately and
    /// rethrow a captured control-flow signal only after that value has left the iterator.
    /// </summary>
    private static async ValueTask<CapturedEnumeratorMove> MoveNextCapturingFailureAsync(
        IAsyncEnumerator<object?> enumerator)
    {
        try
        {
            if (!await enumerator.MoveNextAsync())
                return default;

            return new CapturedEnumeratorMove(
                HasValue: true,
                enumerator.Current,
                Failure: null);
        }
        catch (Exception failure)
        {
            return new CapturedEnumeratorMove(
                HasValue: false,
                Value: null,
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure));
        }
    }

    private static async IAsyncEnumerable<object?> SingleItemAsync(object? item)
    {
        await Task.CompletedTask;
        yield return item;
    }

    private async ValueTask<bool> IsInAsync(
        object? value,
        object? candidates,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (candidates is null)
        {
            return false;
        }

        if (candidates is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await AreEqualAsync(value, entry.Key, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }

        if (candidates is IShellEnumerableObject { HasShellItems: true } shellEnumerable)
        {
            await foreach (var candidate in shellEnumerable
                               .EnumerateShellItemsAsync(cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                if (await AreEqualAsync(value, candidate, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }

        if (candidates is IEnumerable enumerable && candidates is not string)
        {
            foreach (var candidate in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await AreEqualAsync(value, candidate, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }

        if (candidates is string text)
        {
            return text.Contains(
                await ToOperatorStringAsync(value, cancellationToken),
                StringComparison.Ordinal);
        }

        return await AreEqualAsync(value, candidates, cancellationToken);
    }

    private async ValueTask<bool> ContainsAsync(
        object? actual,
        object? expected,
        CancellationToken cancellationToken)
    {
        if (actual is null)
        {
            return false;
        }

        if (actual is string text)
        {
            // `TOAST-0018`. A string does not contain nothing. `null` rendered as the
            // empty string, and every string contains that, so `"abc" contains null` was
            // true. Collection membership is unaffected: `[1, null] contains null` asks a
            // different question and still answers true.
            if (expected is null)
            {
                return false;
            }

            return text.Contains(
                await ToOperatorStringAsync(expected, cancellationToken),
                StringComparison.Ordinal);
        }

        if (actual is IDictionary ||
            actual is IShellEnumerableObject { HasShellItems: true } ||
            actual is IEnumerable)
        {
            return await IsInAsync(expected, actual, cancellationToken);
        }

        return false;
    }

    private async ValueTask<bool> StartsWithAsync(
        object? actual,
        object? expected,
        CancellationToken cancellationToken)
    {
        if (actual is null)
        {
            return false;
        }

        return (await ToOperatorStringAsync(actual, cancellationToken)).StartsWith(
            await ToOperatorStringAsync(expected, cancellationToken),
            StringComparison.Ordinal);
    }

    private async ValueTask<bool> EndsWithAsync(
        object? actual,
        object? expected,
        CancellationToken cancellationToken)
    {
        if (actual is null)
        {
            return false;
        }

        return (await ToOperatorStringAsync(actual, cancellationToken)).EndsWith(
            await ToOperatorStringAsync(expected, cancellationToken),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// What <c>in&lt;</c> feeds the first stage: the file's bytes when that stage is a program
    /// that offered its stdin, and the file's lines otherwise (<c>TOSH-0012</c>).
    /// </summary>
    private static async IAsyncEnumerable<object?> ReadInputRedirectionAsync(
        string path,
        RawByteHandoff bytes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (bytes.TryClaim(out var destination))
        {
            await using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            // A program that stops reading early — `head -c 16 in< big.bin` — has had what it
            // wanted; that is not an error, so the result is not checked.
            await destination.CopyFromAsync(file, ReadOnlyMemory<byte>.Empty, cancellationToken);
            yield break;
        }

        await foreach (var line in ReadLinesAsync(path, cancellationToken))
        {
            yield return line;
        }
    }

    /// <summary>
    /// The bytes a program writes to a redirected file, sent past the text writer the rest of
    /// the redirection uses. Anything already written as text is flushed first, so the file
    /// keeps the order things happened in.
    /// </summary>
    private sealed class RedirectedFileByteStream(TextWriter writer, Stream file) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            writer.Flush();
            file.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            writer.Flush();
            await file.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
            writer.Flush();
            file.Flush();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            writer.Flush();
            await file.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static async IAsyncEnumerable<object?> ReadLinesAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        string? line;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
        }
    }

    private static int GetStagesConsumed(Tosh.Language.Binding.PipelineFusion fusion) => fusion switch
    {
        SortFirstFusion sortFirst => sortFirst.StagesConsumed,
        _ => 0,
    };

    /// <summary>
    /// Specialised executor for <c>... | sort [-r] | first N</c>. Uses a
    /// bounded <see cref="PriorityQueue{TElement, TPriority}"/> of size N
    /// to retain only the items we need, then emits them in sort order.
    /// Memory: O(N) instead of O(M); time: O(M log N).
    /// </summary>
    private static async IAsyncEnumerable<object?> ExecuteSortFirstFusionAsync(
        IAsyncEnumerable<object?> source,
        SortFirstFusion fusion,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (fusion.Count == 0)
        {
            yield break;
        }

        // Comparator mirrors SortCommand's default (no -n, no -h, no key).
        // Forward direction: ascending; reverse: descending.
        // `TOAST-0018`. The shared comparer, not a local copy of it. The copy this
        // replaced compared only values of an identical type and otherwise ordered by
        // type *name*, so `[1, "a", 2.5] | sort` answered `1, 2.5, "a"` while
        // `| sort | first 3` answered `2.5, 1, "a"` — a fused pipeline disagreeing with
        // the unfused one it is supposed to be indistinguishable from.
        IComparer<object?> ascending = ShellSortComparer.Ordinal;
        IComparer<object?> descending = new ReverseComparer(ascending);
        var comparer = fusion.Reverse ? descending : ascending;

        // Heap orders by the OPPOSITE direction so its top is the
        // candidate to evict. For ascending top-N (N smallest), we keep
        // a max-heap; for reverse (N largest), a min-heap.
        var evictionComparer = fusion.Reverse ? ascending : descending;
        var heap = new PriorityQueue<object?, object?>(fusion.Count, evictionComparer);

        // `TOAST-0025`. The stages this fusion replaced each expanded their input, and
        // the fusion did not — so a pipeline head yielding a lone collection reached the
        // heap as **one item**, and `[3,1,2] | sort | first` answered `3, 1, 2`: the
        // whole array, unsorted, with no error. `TS-P2-74` is why the head yields one
        // value ("it is each stage that decides whether a collection means itself or its
        // elements"), and this stands in for two stages that had both decided.
        //
        // The same helper `FirstCommand` calls, rather than a bare expansion: it honours
        // `PreExpandedSequence` (`TS-P2-113`), so a replayed variable is not expanded a
        // second time, and it expands only a *lone* collection, so a stream of several
        // collections keeps them as items. Both cases are pinned in the corpus.
        var expanded = ShellIterationUtilities.ReplaySingleInputCollectionAsync(source, cancellationToken);

        await foreach (var item in expanded.WithCancellation(cancellationToken))
        {
            if (heap.Count < fusion.Count)
            {
                heap.Enqueue(item, item);
                continue;
            }

            // EnqueueDequeue replaces the top if the new item is "better"
            // (smaller for ascending top-N, larger for reverse).
            heap.EnqueueDequeue(item, item);
        }

        // Drain to a buffer, then sort in the requested direction.
        var buffer = new List<object?>(heap.Count);
        while (heap.Count > 0)
        {
            buffer.Add(heap.Dequeue());
        }

        buffer.Sort(comparer);

        foreach (var item in buffer)
        {
            yield return item;
        }
    }

    /// <summary>
    /// Runs a pipeline stage that is an expression.
    /// </summary>
    /// <remarks>
    /// Not an iterator itself, so the variable-replay branch can return a
    /// <see cref="PreExpandedSequence"/> — a stream that has already had its
    /// collection enumerated into it, and must not be expanded again downstream
    /// (`TS-P2-113`). The rest of the work stays in the iterator below.
    /// </remarks>
    private IAsyncEnumerable<object?> ExecuteExpressionStageAsync(
        string sourceName,
        string sourceText,
        ExpressionPipelineStageSyntax expressionStage,
        CancellationToken cancellationToken)
    {
        if (expressionStage.Expression is VariableReferenceArgumentSyntax variableReference &&
            TryGetVariableBinding(variableReference.Name, out var binding) &&
            binding.ReplayAsPipeline &&
            binding.Value is IEnumerable enumerable &&
            binding.Value is not string)
        {
            return new PreExpandedSequence(ReplayBindingAsync(enumerable, cancellationToken));
        }

        // `TOAST-0028`. An expression head is where a collection *literal* — or a variable
        // holding one, or a range — reaches a pipeline, and those are sequences: spreading
        // them is what `[1, 2, 3] | where { … }` has always meant. Marking says so at the
        // producer, which is the point of the change; downstream no longer has to guess it
        // from how many items happen to arrive.
        var core = ExecuteExpressionStageCoreAsync(sourceName, sourceText, expressionStage, cancellationToken);

        return HeadIsACall(expressionStage.Expression) ? core : new SpreadableSequence(core);
    }

    /// <summary>
    /// Whether a pipeline head <em>calls</em> something — `TOAST-0039`.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `TOAST-0028` marked every expression head as a sequence, which made the rule
    /// syntactic in a way authors could not see. A function returning a collection answered
    /// 1 because a bare name parses as a command; a method returning the identical
    /// collection answered 3 because `$c.m()` parses as an expression. Nothing about the
    /// author's intent differed.
    /// </para>
    /// <para>
    /// The rule is now one sentence: a collection <em>written</em> as an expression is a
    /// sequence, and a collection <em>returned by a call</em> is a value. A property read
    /// stays a sequence, because `$obj.Items` <em>is</em> the collection in the same way a
    /// variable is — it is the calling that produces one.
    /// </para>
    /// </remarks>
    private static bool HeadIsACall(ArgumentSyntax expression) => expression switch
    {
        MethodCallArgumentSyntax => true,
        StaticMethodCallArgumentSyntax => true,
        CallableInvocationArgumentSyntax => true,
        // `new` is deliberately **not** a call here. It constructs a value the way a
        // literal writes one, and treating it as a call would make `new array(1, 2, 3)`
        // answer 1 while the identical `[1, 2, 3]` answers 3 — the same defect this item
        // exists to remove, reintroduced one spelling over.
        _ => false,
    };

    private static async IAsyncEnumerable<object?> ReplayBindingAsync(
        IEnumerable source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private async IAsyncEnumerable<object?> ExecuteExpressionStageCoreAsync(
        string sourceName,
        string sourceText,
        ExpressionPipelineStageSyntax expressionStage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {

        // `TS-P2-73`. A ternary arm could not invoke a multi-value command, because the
        // parentheses it *requires* are the same parentheses that impose single-value
        // collapse: unparenthesised arms are a parse error, and a parenthesised arm is a
        // subexpression, which `EvaluateArgumentAsync` reduces to one value or rejects.
        // So `func svc(a, s) => ($a == journal) ? (sudo journalctl -u $s) : (...)` failed
        // with "this subexpression produced 20 values" while the identical `if`/`else`
        // block streamed all twenty.
        //
        // Parentheses mean two things here — grouping and collapse — and an arm needs
        // only the first. The rule from `TS-P1-20` is unchanged and still applies
        // wherever a single value is genuinely required; this is a *pipeline stage*, so
        // the surrounding context streams, exactly as `for x in (pipeline)` already does
        // under rule 3 of that same list. An argument list is untouched: `echo ($a ? $b :
        // $c)` still reaches `EvaluateArgumentAsync` and still collapses.
        var effective = expressionStage.Expression;

        while (effective is ConditionalArgumentSyntax conditional)
        {
            var condition = await EvaluateArgumentAsync(sourceName, sourceText, conditional.Condition, cancellationToken);
            effective = OperatorEvaluator.ToBoolean(condition) ? conditional.WhenTrue : conditional.WhenFalse;
        }

        if (!ReferenceEquals(effective, expressionStage.Expression) &&
            effective is SubexpressionArgumentSyntax chosenSubexpression)
        {
            await foreach (var item in EvaluatePipelineAsync(
                sourceName, sourceText, chosenSubexpression.Pipeline, cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        // `TOAST-0032`. `...$xs` sends the collection's elements, one item each, and says
        // so at the point it is written. Everything else here decides shape by inspecting
        // the value; this is the one form where the author has already said what they
        // meant, so nothing is inferred.
        if (effective is SpreadElementArgumentSyntax spread)
        {
            var spreadValue = await EvaluateArgumentAsync(sourceName, sourceText, spread.Value, cancellationToken);

            foreach (var item in ShellIterationUtilities.ExpandIterationItems(spreadValue))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            yield break;
        }

        object? value;

        try
        {
            value = await EvaluateArgumentPreservingEmptyAsync(sourceName, sourceText, effective, cancellationToken);
        }
        catch (ToshDiagnosticException)
        {
            throw;
        }
        catch (Tosh.Runtime.ShellControlFlowException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsToshThrown(exception))
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateExpressionDiagnostic(sourceName, sourceText, expressionStage.Expression, exception);
        }

        // `TS-P2-74`: this gate stays. Spreading every list-valued expression head was
        // tried and is wrong — `[] | to json` must serialize the empty array rather than
        // send nothing downstream, and eight tests said so, across `to json`, format
        // round-trips and comprehensions. A pipeline head yields one value; it is each
        // stage that decides whether a collection means itself or its elements.
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

        // `TOAST-0123`. A call whose body produced no values contributes no items. It used
        // to contribute one null, so `$win.Events.Drain() | where Kind == …` failed on
        // every idle frame — at the `where`, naming the caller rather than the method that
        // yielded nothing. A value position still reads null; only a pipeline, which asked
        // for items, is told there were none.
        if (ReferenceEquals(value, ToshEmptyCallResult.Instance))
        {
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

    private async IAsyncEnumerable<object?> ExecutePipeForwardStageAsync(
        string sourceName,
        string sourceText,
        PipeForwardStageSyntax pipeForward,
        IAsyncEnumerable<object?> input,
        PipelineExitStatusTracker? pipelineExitStatusTracker,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Collect all items from the previous stage.
        var items = new List<object?>();
        await foreach (var item in input.WithCancellation(cancellationToken))
        {
            items.Add(item);
        }

        // Collapse: single item → unwrap, multiple → list, zero → null.
        object? collectedValue = items.Count switch
        {
            0 => null,
            1 => items[0],
            _ => items,
        };

        // Execute the command with the collected value prepended as first argument.
        var prependedArgs = new List<object?> { collectedValue };
        await foreach (var result in ExecuteCommandSyntaxAsync(
            sourceName,
            sourceText,
            pipeForward.Command,
            AsyncEnumerableExtensions.Empty<object?>(),
            additionalArguments: null,
            isPipelined: false,
            pipelineExitStatusTracker,
            cancellationToken,
            prependedArguments: prependedArgs))
        {
            yield return result;
        }
    }

}
