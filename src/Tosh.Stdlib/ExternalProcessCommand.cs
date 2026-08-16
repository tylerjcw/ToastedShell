using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

using Tosh.Runtime;
using Tosh.Stdlib.Tssp;

namespace Tosh.Stdlib;

public sealed class ExternalProcessCommand : IExternalProcessCommand, ICommandResolutionMetadata, IImplicitGlobCommand
{
    private readonly string _resolvedPath;

    public ExternalProcessCommand(string name, string resolvedPath)
    {
        Name = name;
        _resolvedPath = resolvedPath;
    }

    public string Name { get; }

    public string ResolvedPath => _resolvedPath;

    public string Description => $"Executes the external program at '{_resolvedPath}'.";

    public string Usage => $"{Name} [arg ...]";

    public CommandResolutionKind ResolutionKind => CommandResolutionKind.External;

    public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var mode = DetermineSpawnMode(context);

        if (mode == SpawnMode.TerminalPassthrough)
        {
            await ExecuteWithTerminalPassthroughAsync(context);
            yield break;
        }

        if (mode == SpawnMode.Hybrid)
        {
            await foreach (var item in ExecuteWithHybridAsync(context))
            {
                yield return item;
            }
            yield break;
        }

        await foreach (var item in ExecuteWithPipesAsync(context))
        {
            yield return item;
        }
    }

    private enum SpawnMode
    {
        TerminalPassthrough,
        Hybrid,
        Piped,
    }

    private SpawnMode DetermineSpawnMode(CommandContext context)
    {
        var atTerminal = !Console.IsInputRedirected
                      && !Console.IsOutputRedirected
                      && ReferenceEquals(context.Runtime.Output, Console.Out)
                      && ReferenceEquals(context.Runtime.Error, Console.Error);

        // A consumer is waiting for this value, so stdout has to be piped even at a
        // terminal — but stdin and stderr stay with the terminal, so an interactive child
        // still prompts and still shows progress. That is precisely what Hybrid does.
        //
        // This used to be decided by `IsPipelined` alone, which is true only when a
        // *downstream stage* exists. So `git … | collect` captured while
        // `var x = git …`, `(git …)`, `$(git …)` and `$"{git …}"` all printed to the
        // terminal and yielded null — they consume the value without being pipelined
        // (TS-P1-30).
        if (atTerminal && context.OutputIsCaptured)
        {
            return SpawnMode.Hybrid;
        }

        var hasTerminal = atTerminal && !context.IsPipelined;

        if (hasTerminal && IsHybridConsumer(context))
        {
            return SpawnMode.Hybrid;
        }

        if (hasTerminal && !IsKnownStructuredOutputCommand(_resolvedPath))
        {
            return SpawnMode.TerminalPassthrough;
        }

        return SpawnMode.Piped;
    }

    private bool IsHybridConsumer(CommandContext context)
    {
        if (string.IsNullOrEmpty(_resolvedPath)) return false;
        var name = Path.GetFileNameWithoutExtension(_resolvedPath);
        if (string.IsNullOrEmpty(name)) return false;
        return context.Runtime.Config.External.IsHybridConsumer(name);
    }

    // Binaries shipped with TōSh that always emit TSSP when
    // TOSH_STRUCTURED_STDOUT is negotiated. Routing them through the piped
    // path even at a bare TTY lets DisplayEngine render their output instead
    // of letting the child's text-fallback path bypass the shell.
    //
    // NOTE: Currently empty. Forcing the piped path also forces stdin
    // redirection and skips the foreground-group handoff, which breaks
    // interactive prompts (the child opens /dev/tty but ToSh still owns
    // the controlling terminal). Until we land a hybrid passthrough mode
    // — pipe stdout only, inherit stdin/stderr, hand off the foreground
    // group — interactive children must take the full passthrough path.
    private static readonly HashSet<string> KnownStructuredCommands =
        new(StringComparer.Ordinal);

    private static bool IsKnownStructuredOutputCommand(string resolvedPath)
    {
        if (string.IsNullOrEmpty(resolvedPath)) return false;
        var name = Path.GetFileNameWithoutExtension(resolvedPath);
        return !string.IsNullOrEmpty(name) && KnownStructuredCommands.Contains(name);
    }

    private async Task ExecuteWithTerminalPassthroughAsync(CommandContext context)
    {
        var terminal = context.Runtime.Terminal;
        var processOwnershipTransferred = false;

        var startInfo = new ProcessStartInfo
        {
            FileName = _resolvedPath,
            WorkingDirectory = context.Runtime.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        foreach (var argument in context.Arguments)
        {
            startInfo.ArgumentList.Add(ExternalTextSerializer.SerializeArgument(argument));
        }

        ApplyTsspEnvironment(startInfo, context, consumer: "terminal", stdioMode: "passthrough");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        using var cancellationRegistration = context.CancellationToken.Register(() => TryKill(process));

        // Save terminal state before the child can modify it.
        terminal.SaveTerminalState();

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start external command '{Name}'.");
        }

        // On Unix, try to place the child in its own process group and, only
        // if that succeeds, transfer terminal foreground to that group.
        var childOwnsGroup = TrySetChildProcessGroup(process);

        try
        {
            if (childOwnsGroup)
            {
                terminal.TrySetForegroundGroup(process.Id, out _);
            }

            // Use waitpid(WUNTRACED) to detect Ctrl+Z suspension.
            var waitResult = terminal.WaitForForegroundChild(process.Id);

            switch (waitResult.Outcome)
            {
                case ForegroundWaitOutcome.Exited:
                    context.Runtime.SetLastExitCode(waitResult.StatusOrSignal);
                    context.PipelineExitStatusTracker?.Record(waitResult.StatusOrSignal);
                    break;

                case ForegroundWaitOutcome.Stopped:
                    // Child was suspended (Ctrl+Z). Snapshot its terminal state
                    // so fg can restore it before sending SIGCONT.
                    Termios? childTermios = null;
                    if (PosixTerminalInterop.TryGetTerminalAttributes(out var t, out _))
                    {
                        childTermios = t;
                    }

                    var commandText = BuildCommandText(context);
                    var job = ShellJob.CreateSuspended(
                        context.Runtime.AllocateJobId(),
                        commandText,
                        process,
                        childOwnsGroup,
                        childTermios);
                    processOwnershipTransferred = true;
                    context.Runtime.RegisterJob(job);
                    context.Runtime.SetLastExitCode(148); // 128 + SIGTSTP(20)
                    await context.Runtime.Error.WriteLineAsync(
                        $"\n[{job.Id}]  Stopped                 {commandText}");
                    break;

                default:
                    // Fallback: non-interactive or error — use managed wait.
                    await process.WaitForExitAsync(context.CancellationToken);
                    context.Runtime.SetLastExitCode(process.ExitCode);
                    context.PipelineExitStatusTracker?.Record(process.ExitCode);
                    break;
            }
        }
        finally
        {
            if (childOwnsGroup)
            {
                terminal.ReclaimForeground();
            }

            terminal.RestoreTerminalState();

            if (!processOwnershipTransferred)
            {
                process.Dispose();
            }
        }
    }

    private string BuildCommandText(CommandContext context)
    {
        var parts = new List<string> { Name };

        foreach (var arg in context.Arguments)
        {
            parts.Add(ExternalTextSerializer.SerializeArgument(arg));
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// On Unix, place the newly spawned child into its own process group
    /// (using its PID as the PGID).  Returns <c>true</c> when the child
    /// is now a group leader, <c>false</c> otherwise (e.g. the child
    /// already exec'd, already exited, or we are on Windows).
    /// </summary>
    private static bool TrySetChildProcessGroup(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return PosixTerminalInterop.TrySetProcessGroupId(process.Id, process.Id, out _);
        }
        catch
        {
            // Race: child may have already exited.
            return false;
        }
    }

    private static int s_unframedWarningEmitted;

    /// <summary>
    /// Hybrid consumers routinely interleave unframed bytes (interactive
    /// sudo prompts, embedded pacman/makepkg output, etc.) — that is the
    /// whole point of the hybrid mode. The diagnostic is opt-in via
    /// <c>TOSH_DIAG=1</c> so it does not clutter normal sessions.
    /// </summary>
    private static void EmitUnframedWarningOnce(CommandContext context)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TOSH_DIAG"), "1", StringComparison.Ordinal))
            return;
        if (Interlocked.Exchange(ref s_unframedWarningEmitted, 1) != 0) return;
        try
        {
            context.Runtime.Error.WriteLine(
                "tosh: tssp.unframed_output: hybrid consumer emitted non-TSSP bytes on stdout; forwarding verbatim.");
        }
        catch { }
    }

    private static int s_lastProgressLineWidth;

    /// <summary>
    /// Emit a final newline if a progress line was previously written, so
    /// the next output starts on a clean row. Idempotent.
    /// </summary>
    private static void FinalizeProgressLine(CommandContext context)
    {
        var prev = Interlocked.Exchange(ref s_lastProgressLineWidth, 0);
        if (prev > 0)
        {
            try { context.Runtime.Error.WriteLine(string.Empty); } catch { }
        }
    }

    /// <summary>
    /// Render a TSSP <c>progress</c> frame as a transient single-line status
    /// on stderr. Payload is JSON with optional fields:
    /// <c>{message?: string, current?: number, total?: number, percent?: number}</c>.
    /// Lines overwrite in place via CR; a final newline is emitted when the
    /// next non-progress frame arrives (handled implicitly by other writers
    /// to stderr).
    /// </summary>
    private static void RenderProgressFrame(ReadOnlyMemory<byte> payload, CommandContext context)
    {
        try
        {
            string? message = null;
            double? current = null;
            double? total = null;
            double? percent = null;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (root.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String)
                        message = m.GetString();
                    if (root.TryGetProperty("current", out var c) && c.TryGetDouble(out var cv)) current = cv;
                    if (root.TryGetProperty("total", out var t) && t.TryGetDouble(out var tv)) total = tv;
                    if (root.TryGetProperty("percent", out var p) && p.TryGetDouble(out var pv)) percent = pv;
                }
                else if (root.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    message = root.GetString();
                }
            }
            catch
            {
                // Malformed payload — best-effort render of the raw bytes.
                message = Encoding.UTF8.GetString(payload.Span);
            }

            if (percent is null && current is not null && total is > 0)
            {
                percent = (current.Value / total.Value) * 100.0;
            }

            var sb = new StringBuilder();
            if (percent is not null) sb.Append($"[{percent.Value,6:0.0}%] ");
            else if (current is not null && total is not null) sb.Append($"[{current.Value:0}/{total.Value:0}] ");
            if (!string.IsNullOrEmpty(message)) sb.Append(message);
            var line = sb.ToString();
            if (line.Length == 0) return;

            // Overwrite the previous line: pad to the width we last wrote.
            var prevWidth = Interlocked.Exchange(ref s_lastProgressLineWidth, line.Length);
            if (line.Length < prevWidth) line = line.PadRight(prevWidth);

            context.Runtime.Error.Write("\r" + line);
        }
        catch
        {
            // Progress rendering must never disrupt the stream.
        }
    }

    private async IAsyncEnumerable<object?> ExecuteWithHybridAsync(
        CommandContext context,
        [EnumeratorCancellation] CancellationToken enumeratorCt = default)
    {
        var terminal = context.Runtime.Terminal;
        var processOwnershipTransferred = false;

        var startInfo = new ProcessStartInfo
        {
            FileName = _resolvedPath,
            WorkingDirectory = context.Runtime.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = false,    // inherit /dev/tty for interactive prompts
            RedirectStandardOutput = true,    // pipe → TSSP parser
            RedirectStandardError = false,    // inherit /dev/tty for status messages
        };

        foreach (var argument in context.Arguments)
        {
            startInfo.ArgumentList.Add(ExternalTextSerializer.SerializeArgument(argument));
        }

        ApplyTsspEnvironment(startInfo, context, consumer: "terminal", stdioMode: "hybrid");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        using var cancellationRegistration = context.CancellationToken.Register(() => TryKill(process));

        terminal.SaveTerminalState();

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start external command '{Name}'.");
        }

        var childOwnsGroup = TrySetChildProcessGroup(process);
        if (childOwnsGroup)
        {
            terminal.TrySetForegroundGroup(process.Id, out _);
        }

        var stdoutStream = process.StandardOutput.BaseStream;
        var parser = new TsspParser(stdoutStream);

        var channel = Channel.CreateUnbounded<object?>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        // Wait for the child on a dedicated thread so we can detect Ctrl+Z.
        var waitTcs = new TaskCompletionSource<ForegroundWaitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitThread = new Thread(() =>
        {
            try { waitTcs.SetResult(terminal.WaitForForegroundChild(process.Id)); }
            catch (Exception ex) { waitTcs.SetException(ex); }
        })
        { IsBackground = true, Name = $"tosh-fg-wait:{process.Id}" };
        waitThread.Start();

        // Pump stdout: read header, then frames OR plain bytes-as-text into the channel.
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                TsspHeader? header = null;
                try { header = await parser.TryReadHeaderAsync(context.CancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch { header = null; }

                if (header is not null && header.Version <= TsspVersion.Current)
                {
                    await foreach (var item in ConsumeTsspFramesAsync(parser, header, context)
                                       .WithCancellation(context.CancellationToken))
                    {
                        await channel.Writer.WriteAsync(item, context.CancellationToken);
                    }
                }
                else
                {
                    // Hybrid consumer emitted non-TSSP bytes — forward verbatim,
                    // but line-buffered so each ShellTextLine is exactly one logical line.
                    EmitUnframedWarningOnce(context);

                    Stream combined = parser.SniffedBytes.Length > 0
                        ? new PrefixedStream(parser.SniffedBytes, stdoutStream)
                        : stdoutStream;

                    using var reader = new StreamReader(combined, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                    string? line;
                    while ((line = await reader.ReadLineAsync(context.CancellationToken)) is not null)
                    {
                        await channel.Writer.WriteAsync(new ShellTextLine(line), context.CancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { /* stream/process teardown races */ }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, context.CancellationToken);

        ForegroundWaitResult waitResult = ForegroundWaitResult.FallbackToManagedWait;
        Exception? deferred = null;
        try
        {
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(enumeratorCt))
                {
                    yield return item;
                }
            }
            finally
            {
                try { await pumpTask; } catch (Exception ex) { deferred ??= ex; }
                try { waitResult = await waitTcs.Task; } catch (Exception ex) { deferred ??= ex; }
            }
        }
        finally
        {
            switch (waitResult.Outcome)
            {
                case ForegroundWaitOutcome.Exited:
                    context.Runtime.SetLastExitCode(waitResult.StatusOrSignal);
                    context.PipelineExitStatusTracker?.Record(waitResult.StatusOrSignal);
                    break;

                case ForegroundWaitOutcome.Stopped:
                    Termios? childTermios = null;
                    if (PosixTerminalInterop.TryGetTerminalAttributes(out var t, out _))
                    {
                        childTermios = t;
                    }

                    var commandText = BuildCommandText(context);
                    var job = ShellJob.CreateSuspended(
                        context.Runtime.AllocateJobId(),
                        commandText,
                        process,
                        childOwnsGroup,
                        childTermios);
                    processOwnershipTransferred = true;
                    context.Runtime.RegisterJob(job);
                    context.Runtime.SetLastExitCode(148);
                    try
                    {
                        context.Runtime.Error.WriteLine(
                            $"\n[{job.Id}]  Stopped                 {commandText}");
                    }
                    catch { }
                    break;

                default:
                    try { process.WaitForExit(); } catch { }
                    try
                    {
                        context.Runtime.SetLastExitCode(process.ExitCode);
                        context.PipelineExitStatusTracker?.Record(process.ExitCode);
                    }
                    catch { }
                    break;
            }

            if (childOwnsGroup)
            {
                terminal.ReclaimForeground();
            }

            terminal.RestoreTerminalState();

            if (!processOwnershipTransferred)
            {
                process.Dispose();
            }
        }
    }

    private async IAsyncEnumerable<object?> ExecuteWithPipesAsync(
        CommandContext context)
    {
        using var process = CreatePipedProcess(context);
        using var cancellationRegistration = context.CancellationToken.Register(() => TryKill(process));

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start external command '{Name}'.");
        }

        var stderrTask = PumpStandardErrorAsync(process, context.Runtime.Error, context.CancellationToken);
        var stdinTask = PumpStandardInputAsync(process, context.Input, context.CancellationToken);

        var stdoutStream = process.StandardOutput.BaseStream;
        var parser = new TsspParser(stdoutStream);
        TsspHeader? header = null;
        try
        {
            header = await parser.TryReadHeaderAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            header = null;
        }

        if (header is not null && header.Version <= TsspVersion.Current)
        {
            await foreach (var item in ConsumeTsspFramesAsync(parser, header, context))
                yield return item;
        }
        else
        {
            // Either no TSSP header, or unsupported version. Fall back to plain text.
            await foreach (var item in ConsumePlainStdoutAsync(stdoutStream, parser.SniffedBytes, context.CancellationToken))
                yield return item;
        }

        await AwaitAndIgnoreClosedPipeAsync(stdinTask);
        await AwaitAndIgnoreClosedPipeAsync(stderrTask);
        await process.WaitForExitAsync(CancellationToken.None);
        context.Runtime.SetLastExitCode(process.ExitCode);
        context.PipelineExitStatusTracker?.Record(process.ExitCode);
    }

    private static async IAsyncEnumerable<object?> ConsumeTsspFramesAsync(
        TsspParser parser,
        TsspHeader header,
        CommandContext context)
    {
        // Resolve an optional renderer once up front. Header.Renderer wins;
        // otherwise we key off the schema name. Only IShellCallable values
        // are honoured — anything else falls back to raw record streaming.
        IShellCallable? renderer = null;
        var renderers = context.Runtime.Config.Renderers;
        var rendererKey = header.Renderer ?? header.Schema;
        if (rendererKey is not null && renderers[rendererKey] is IShellCallable callable)
            renderer = callable;

        IAsyncEnumerator<TsspFrame>? enumerator = null;
        try
        {
            enumerator = parser.ReadFramesAsync(context.CancellationToken).GetAsyncEnumerator(context.CancellationToken);
            while (true)
            {
                bool moved;
                TsspFrame? frame = null;
                try
                {
                    moved = await enumerator.MoveNextAsync();
                    if (moved) frame = enumerator.Current;
                }
                catch (TsspProtocolException ex)
                {
                    await context.Runtime.Error.WriteLineAsync(
                        $"tosh: tssp.frame_error: {ex.Message}");
                    yield break;
                }
                if (!moved) yield break;
                if (frame is null) continue;

                switch (frame.Kind)
                {
                    case "rec":
                        FinalizeProgressLine(context);
                        if (frame.Record is null) break;
                        var record = ApplySchemaFieldOrder(frame.Record, header.Schema, context);
                        if (renderer is null)
                        {
                            yield return record;
                        }
                        else
                        {
                            await foreach (var v in InvokeRendererAsync(renderer, record, context))
                                yield return v;
                        }
                        break;
                    case "err":
                        FinalizeProgressLine(context);
                        var msg = Encoding.UTF8.GetString(frame.Payload.Span);
                        await context.Runtime.Error.WriteLineAsync(msg);
                        break;
                    case "meta":
                        RegisterMetaSchema(context, header, frame.Payload);
                        break;
                    case "pres":
                    case "pres-end":
                        FinalizeProgressLine(context);
                        // Ignore in v1 piped/capture consumer routing.
                        break;
                    case "progress":
                        RenderProgressFrame(frame.Payload, context);
                        break;
                    default:
                        // Unknown frame kind — ignore (forward-compat).
                        break;
                }
            }
        }
        finally
        {
            if (enumerator is not null) await enumerator.DisposeAsync();
            FinalizeProgressLine(context);
        }
    }

    private static async IAsyncEnumerable<object?> InvokeRendererAsync(
        IShellCallable renderer,
        object? record,
        CommandContext context)
    {
        var inner = context with
        {
            Arguments = new object?[] { record },
            Input = AsyncEnumerableExtensions.Empty<object?>(),
            IsPipelined = false,
        };
        await foreach (var v in renderer.InvokeAsync(inner).WithCancellation(context.CancellationToken))
            yield return v;
    }

    /// <summary>Reorder a record's fields to match the registered schema's
    /// <c>fields</c> declaration so that downstream column rendering uses the
    /// canonical author-intended order, even when record JSON property order
    /// diverges or omits optional fields.</summary>
    private static object? ApplySchemaFieldOrder(object? record, string? schemaName, CommandContext context)
    {
        if (record is null || schemaName is null) return record;
        if (!ShellRecordUtilities.TryGetFields(record, out var fields)) return record;

        var schemas = context.Runtime.Config.Schemas;
        if (schemas[schemaName] is not object schemaObj) return record;
        if (!ShellRecordUtilities.TryGetFields(schemaObj, out var schemaFields)) return record;

        IReadOnlyList<KeyValuePair<string, object?>>? declaredFields = null;
        string? titleTemplate = null;
        foreach (var (k, v) in schemaFields)
        {
            if (string.Equals(k, "fields", StringComparison.Ordinal)
                && v is object fv
                && ShellRecordUtilities.TryGetFields(fv, out var fieldList))
            {
                declaredFields = fieldList;
            }
            else if (string.Equals(k, "title", StringComparison.Ordinal) && v is string ts)
            {
                titleTemplate = ts;
            }
        }
        if (declaredFields is null && titleTemplate is null) return record;

        var sourceMap = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in fields) sourceMap[k] = v;

        var ordered = new List<KeyValuePair<string, object?>>(sourceMap.Count + 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (declaredFields is not null)
        {
            foreach (var (k, _) in declaredFields)
            {
                ordered.Add(new KeyValuePair<string, object?>(k, sourceMap.TryGetValue(k, out var val) ? val : null));
                seen.Add(k);
            }
        }
        foreach (var (k, v) in fields)
            if (!seen.Contains(k)) ordered.Add(new KeyValuePair<string, object?>(k, v));

        if (titleTemplate is not null)
        {
            var title = ExpandTitleTemplate(titleTemplate, sourceMap);
            if (!string.IsNullOrWhiteSpace(title))
            {
                var meta = ShellRecordUtilities.CreateExpando(
                    new[] { new KeyValuePair<string, object?>("title", title) });
                ordered.Add(new KeyValuePair<string, object?>(ShellRecordUtilities.TsspMetaKey, meta));
            }
        }

        return ShellRecordUtilities.CreateExpando(ordered);
    }

    /// <summary>Substitutes <c>{Field}</c> placeholders in a schema's
    /// <c>title</c> template with field values from a record. Missing fields
    /// expand to empty strings; literal braces are not currently escaped.</summary>
    private static string ExpandTitleTemplate(string template, Dictionary<string, object?> values)
    {
        var sb = new StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{')
            {
                var end = template.IndexOf('}', i + 1);
                if (end > i)
                {
                    var key = template.Substring(i + 1, end - i - 1);
                    if (values.TryGetValue(key, out var v) && v is not null)
                        sb.Append(v);
                    i = end + 1;
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Parse a <c>meta</c> frame and merge it into the runtime's schema registry.</summary>
    private static void RegisterMetaSchema(CommandContext context, TsspHeader header, ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var meta = JsonValueConverter.Convert(doc.RootElement);
            // Header schema name overrides the inline one when both are present —
            // the header is authoritative.
            var name = header.Schema;
            if (name is null && meta is IReadOnlyDictionary<string, object?> dict
                && dict.TryGetValue("schema", out var nm) && nm is string s) name = s;
            if (name is null) return;
            context.Runtime.Config.Schemas[name] = meta;
        }
        catch (System.Text.Json.JsonException) { /* malformed meta — ignore */ }
    }

    private static async IAsyncEnumerable<object?> ConsumePlainStdoutAsync(
        Stream stdoutStream,
        ReadOnlyMemory<byte> prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Stream combined = prefix.Length > 0
            ? new PrefixedStream(prefix, stdoutStream)
            : stdoutStream;

        using var reader = new StreamReader(combined, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            yield return new ShellTextLine(line);
        }
    }

    /// <summary>Stream wrapper that yields a fixed byte prefix before delegating reads.</summary>
    private sealed class PrefixedStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _prefix;
        private int _offset;
        private readonly Stream _inner;
        public PrefixedStream(ReadOnlyMemory<byte> prefix, Stream inner) { _prefix = prefix; _inner = inner; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset < _prefix.Length)
            {
                var take = Math.Min(count, _prefix.Length - _offset);
                _prefix.Span.Slice(_offset, take).CopyTo(buffer.AsSpan(offset, take));
                _offset += take;
                return take;
            }
            return _inner.Read(buffer, offset, count);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_offset < _prefix.Length)
            {
                var take = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.Span.Slice(_offset, take).CopyTo(buffer.Span.Slice(0, take));
                _offset += take;
                return take;
            }
            return await _inner.ReadAsync(buffer, ct);
        }
    }

    private Process CreatePipedProcess(CommandContext context)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _resolvedPath,
            WorkingDirectory = context.Runtime.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in context.Arguments)
        {
            startInfo.ArgumentList.Add(ExternalTextSerializer.SerializeArgument(argument));
        }

        var consumer = context.IsPipelined ? "pipe" : "capture";
        ApplyTsspEnvironment(startInfo, context, consumer, stdioMode: "pipe");

        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
    }

    private static void ApplyTsspEnvironment(ProcessStartInfo startInfo, CommandContext context, string consumer, string stdioMode = "pipe")
    {
        var env = startInfo.Environment;
        env["TOSH_STRUCTURED_STDOUT"] = "1";
        env["TOSH_TSSP_VERSION"] = TsspVersion.Current.ToString();
        env["TOSH_STDOUT_CONSUMER"] = consumer;
        env["TOSH_STDIN_ACCEPTS"] = "records,text";
        env["TOSH_STDIO_MODE"] = stdioMode;

        var tty = TryResolveControllingTty();
        if (!string.IsNullOrEmpty(tty)) env["TOSH_TTY"] = tty;

        int width = 80, height = 24;
        try { width = Console.WindowWidth; } catch { }
        try { height = Console.WindowHeight; } catch { }
        if (width <= 0) width = 80;
        if (height <= 0) height = 24;
        env["TOSH_TERM_WIDTH"] = width.ToString();
        env["TOSH_TERM_HEIGHT"] = height.ToString();

        env["TOSH_COLOR"] = (consumer == "terminal" || stdioMode == "hybrid")
            ? DetectColorCapability()
            : "none";
    }

    private static string? TryResolveControllingTty()
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            // ttyname(0/1/2) is the canonical lookup, but we don't have a
            // P/Invoke for it. /proc/self/fd/0 is good enough on Linux —
            // when stdin is a tty, it resolves to /dev/pts/N or /dev/tty*.
            for (var fd = 0; fd <= 2; fd++)
            {
                var link = $"/proc/self/fd/{fd}";
                if (!File.Exists(link) && !Directory.Exists(link)) continue;
                try
                {
                    var fi = new FileInfo(link);
                    if (fi.LinkTarget is { } target
                        && (target.StartsWith("/dev/pts/", StringComparison.Ordinal)
                            || target.StartsWith("/dev/tty", StringComparison.Ordinal)))
                    {
                        return target;
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static string DetectColorCapability()
    {
        if (Console.IsOutputRedirected) return "none";
        var colorterm = Environment.GetEnvironmentVariable("COLORTERM");
        if (!string.IsNullOrEmpty(colorterm) &&
            (colorterm.Contains("truecolor", StringComparison.OrdinalIgnoreCase) ||
             colorterm.Contains("24bit", StringComparison.OrdinalIgnoreCase)))
            return "truecolor";
        var term = Environment.GetEnvironmentVariable("TERM") ?? string.Empty;
        if (term.Contains("256color", StringComparison.OrdinalIgnoreCase)) return "256";
        if (term == "dumb" || string.IsNullOrEmpty(term)) return "none";
        return "256";
    }

    private static async Task PumpStandardErrorAsync(Process process, TextWriter errorWriter, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken);

            if (line is null)
            {
                break;
            }

            await errorWriter.WriteLineAsync(line);
        }
    }

    private static async Task PumpStandardInputAsync(Process process, IAsyncEnumerable<object?> input, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in input.WithCancellation(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await process.StandardInput.WriteLineAsync(ExternalTextSerializer.Serialize(item));
            }
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }
        }
    }

    private static async Task AwaitAndIgnoreClosedPipeAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            try
            {
                Console.Error.WriteLine($"tosh: warning: failed to kill process {process.Id}: {ex.Message}");
            }
            catch
            {
                Console.Error.WriteLine($"tosh: warning: failed to kill process: {ex.Message}");
            }
        }
        catch
        {
        }
    }
}
