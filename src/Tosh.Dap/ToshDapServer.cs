using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tosh.Runtime;
using Tosh.Language;
using Tosh.Language.Debugging;

namespace Tosh.Dap;

/// <summary>
/// Debug Adapter Protocol server for ToSh. Speaks DAP over stdin/stdout.
/// VS Code spawns one instance per debug session.
///
/// <para>
/// <b>Dormant by intent, not abandoned.</b> Nothing in the shipped product launches
/// this; the only caller is <c>ProtocolSmokeTests</c>, which starts a server and
/// drives the initialize handshake, so it is built and exercised on every run rather
/// than merely compiling. A debug adapter is the natural companion to the language
/// server, and the protocol handling is the part that is tedious to write twice —
/// which is why it is kept rather than deleted. Recorded here so the next reader does
/// not have to work that out from the absence of callers.
/// </para>
/// </summary>
public sealed class ToshDapServer
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Session state
    private ToshDebugSession? _session;
    private ToshEngine? _engine;
    private Task? _engineTask;
    private CancellationTokenSource _engineCts = new();
    private bool _stopOnEntry;
    private int _nextVarRef = 1000;

    // Variable reference table for the Variables request
    private readonly Dictionary<int, IReadOnlyList<(string Name, string Value, string Type)>> _varRefs = new();

    public ToshDapServer(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReadMessageAsync(cancellationToken);
            if (message is null) break;

            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "request") continue;

                var seq = root.TryGetProperty("seq", out var seqEl) ? seqEl.GetInt32() : 0;
                var command = root.TryGetProperty("command", out var cmdEl) ? cmdEl.GetString() ?? "" : "";
                root.TryGetProperty("arguments", out var args);

                await HandleRequestAsync(seq, command, args, cancellationToken);
            }
            catch (Exception ex)
            {
                await SendOutputAsync("stderr", $"[Tosh.Dap] Unhandled error: {ex.Message}\n");
            }
        }
    }

    // ── Request Dispatch ──────────────────────────────────────────────────────

    private async Task HandleRequestAsync(int seq, string command, JsonElement args, CancellationToken ct)
    {
        switch (command)
        {
            case "initialize":
                await HandleInitializeAsync(seq, args, ct);
                break;
            case "launch":
                await HandleLaunchAsync(seq, args, ct);
                break;
            case "configurationDone":
                await SendResponseAsync(seq, "configurationDone", null, ct);
                break;
            case "setBreakpoints":
                await HandleSetBreakpointsAsync(seq, args, ct);
                break;
            case "setExceptionBreakpoints":
                // Accept all filters without actually filtering; we surface all errors.
                await SendResponseAsync(seq, "setExceptionBreakpoints", new { breakpoints = Array.Empty<object>() }, ct);
                break;
            case "threads":
                await SendResponseAsync(seq, "threads", new { threads = new[] { new { id = 1, name = "main" } } }, ct);
                break;
            case "stackTrace":
                await HandleStackTraceAsync(seq, args, ct);
                break;
            case "scopes":
                await HandleScopesAsync(seq, args, ct);
                break;
            case "variables":
                await HandleVariablesAsync(seq, args, ct);
                break;
            case "evaluate":
                await HandleEvaluateAsync(seq, args, ct);
                break;
            case "continue":
                _session?.Continue();
                await SendResponseAsync(seq, "continue", new { allThreadsContinued = true }, ct);
                await SendEventAsync("continued", new { threadId = 1, allThreadsContinued = true }, ct);
                break;
            case "next":
                _session?.StepOver();
                await SendResponseAsync(seq, "next", null, ct);
                break;
            case "stepIn":
                _session?.StepNext();
                await SendResponseAsync(seq, "stepIn", null, ct);
                break;
            case "stepOut":
                _session?.StepOut();
                await SendResponseAsync(seq, "stepOut", null, ct);
                break;
            case "pause":
                // Force a pause on the next statement
                if (_session != null) _session.StepNext();
                await SendResponseAsync(seq, "pause", null, ct);
                break;
            case "disconnect":
            case "terminate":
                _session?.Abort();
                _engineCts.Cancel();
                await SendResponseAsync(seq, command, null, ct);
                await SendEventAsync("terminated", new { }, ct);
                break;
            default:
                await SendErrorResponseAsync(seq, command, $"Unsupported request: {command}", ct);
                break;
        }
    }

    // ── initialize ────────────────────────────────────────────────────────────

    private async Task HandleInitializeAsync(int seq, JsonElement args, CancellationToken ct)
    {
        await SendResponseAsync(seq, "initialize", new
        {
            supportsConfigurationDoneRequest = true,
            supportsTerminateRequest = true,
            supportsSetBreakpointsRequest = true,
            supportsStepBack = false,
            supportsRestartRequest = false,
            supportsConditionalBreakpoints = false,
            supportsEvaluateForHovers = true,
            supportsDelayedStackTraceLoading = false
        }, ct);

        await SendEventAsync("initialized", new { }, ct);
    }

    // ── launch ────────────────────────────────────────────────────────────────

    private async Task HandleLaunchAsync(int seq, JsonElement args, CancellationToken ct)
    {
        var program = args.TryGetProperty("program", out var p) ? p.GetString() ?? "" : "";
        _stopOnEntry = args.TryGetProperty("stopOnEntry", out var soe) && soe.GetBoolean();

        string[] scriptArgs = Array.Empty<string>();
        if (args.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
            scriptArgs = argsEl.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();

        if (!File.Exists(program))
        {
            await SendErrorResponseAsync(seq, "launch", $"Script not found: {program}", ct);
            return;
        }

        await SendResponseAsync(seq, "launch", null, ct);

        // Start engine on a background task
        _engineCts = new CancellationTokenSource();
        _engineTask = Task.Run(() => RunEngineAsync(program, scriptArgs, _engineCts.Token), ct);
    }

    private async Task RunEngineAsync(string program, string[] scriptArgs, CancellationToken ct)
    {
        var outputWriter = new StringWriter();
        var errorWriter = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(outputWriter, errorWriter);
        _engine = new ToshEngine(runtime);

        _session = new ToshDebugSession(_engine, stopOnEntry: _stopOnEntry || true);
        _session.Paused += async (ctx, depth) =>
        {
            _varRefs.Clear();
            _nextVarRef = 1000;

            var reason = IsBreakpointHit(ctx) ? "breakpoint" : "step";
            await SendEventAsync("stopped", new
            {
                reason,
                threadId = 1,
                allThreadsStopped = true,
                description = $"Paused at {ctx.SourceName}:{ctx.Line}"
            }, ct);
        };

        // Flush writers to the DAP output channel periodically
        runtime.Output.Flush();
        runtime.Error.Flush();

        // Intercept output by hooking a timer-based flush (simple approach for V1)
        var flushTimer = new System.Timers.Timer(100);
        flushTimer.Elapsed += async (_, _) =>
        {
            var outText = outputWriter.ToString();
            var errText = errorWriter.ToString();
            if (outText.Length > 0)
            {
                outputWriter.GetStringBuilder().Clear();
                await SendOutputAsync("stdout", outText);
            }
            if (errText.Length > 0)
            {
                errorWriter.GetStringBuilder().Clear();
                await SendOutputAsync("stderr", errText);
            }
        };
        flushTimer.Start();

        try
        {
            await foreach (var _ in _engine.ExecuteScriptFileAsync(program, scriptArgs, ct)) { }

            // Final flush
            flushTimer.Stop();
            var outText = outputWriter.ToString();
            var errText = errorWriter.ToString();
            if (outText.Length > 0) await SendOutputAsync("stdout", outText);
            if (errText.Length > 0) await SendOutputAsync("stderr", errText);
        }
        catch (DebugAbortException)
        {
            await SendOutputAsync("console", "[debug] Script aborted.\n");
        }
        catch (OperationCanceledException)
        {
            // normal disconnect
        }
        catch (Exception ex)
        {
            await SendOutputAsync("stderr", $"Error: {ex.Message}\n");
        }
        finally
        {
            flushTimer.Stop();
            _session?.Dispose();
            _session = null;
            await SendEventAsync("exited", new { exitCode = 0 }, default);
            await SendEventAsync("terminated", new { }, default);
        }
    }

    private bool IsBreakpointHit(DebugStepContext ctx) =>
        ctx.Line.HasValue && _session != null;

    // ── setBreakpoints ────────────────────────────────────────────────────────

    private async Task HandleSetBreakpointsAsync(int seq, JsonElement args, CancellationToken ct)
    {
        var source = args.TryGetProperty("source", out var srcEl)
            ? (srcEl.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "" : "")
            : "";

        var requestedLines = Array.Empty<int>();
        if (args.TryGetProperty("breakpoints", out var bpsEl) && bpsEl.ValueKind == JsonValueKind.Array)
        {
            requestedLines = bpsEl.EnumerateArray()
                .Select(bp => bp.TryGetProperty("line", out var lineEl) ? lineEl.GetInt32() : 0)
                .Where(l => l > 0)
                .ToArray();
        }
        else if (args.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
        {
            requestedLines = linesEl.EnumerateArray()
                .Select(l => l.GetInt32())
                .Where(l => l > 0)
                .ToArray();
        }

        var verified = _session?.SetBreakpoints(source, requestedLines) ?? new HashSet<int>(requestedLines);
        var responseBreakpoints = requestedLines
            .Select(l => new { verified = true, line = l })
            .ToArray();

        await SendResponseAsync(seq, "setBreakpoints", new { breakpoints = responseBreakpoints }, ct);
    }

    // ── stackTrace ────────────────────────────────────────────────────────────

    private async Task HandleStackTraceAsync(int seq, JsonElement args, CancellationToken ct)
    {
        if (_session?.PausedContext is not { } ctx)
        {
            await SendResponseAsync(seq, "stackTrace", new { stackFrames = Array.Empty<object>(), totalFrames = 0 }, ct);
            return;
        }

        var frameNames = _session.GetCallStackNames();
        var frames = new List<object>();

        // Frame 0: current statement
        frames.Add(new
        {
            id = 0,
            name = frameNames.Count > 0 ? frameNames[0] : Path.GetFileName(ctx.SourceName),
            source = new { name = Path.GetFileName(ctx.SourceName), path = ctx.SourceName },
            line = ctx.Line ?? 1,
            column = 1
        });

        // Parent frames (names only, no line info for V1)
        for (var i = 1; i < frameNames.Count; i++)
        {
            frames.Add(new
            {
                id = i,
                name = frameNames[i],
                source = new { name = Path.GetFileName(ctx.SourceName), path = ctx.SourceName },
                line = 1,
                column = 1
            });
        }

        await SendResponseAsync(seq, "stackTrace", new { stackFrames = frames, totalFrames = frames.Count }, ct);
    }

    // ── scopes ────────────────────────────────────────────────────────────────

    private async Task HandleScopesAsync(int seq, JsonElement args, CancellationToken ct)
    {
        if (_session is null)
        {
            await SendResponseAsync(seq, "scopes", new { scopes = Array.Empty<object>() }, ct);
            return;
        }

        var vars = _session.GetFormattedVariables();
        var localsRef = _nextVarRef++;
        _varRefs[localsRef] = vars;

        await SendResponseAsync(seq, "scopes", new
        {
            scopes = new[]
            {
                new { name = "Variables", variablesReference = localsRef, expensive = false }
            }
        }, ct);
    }

    // ── variables ────────────────────────────────────────────────────────────

    private async Task HandleVariablesAsync(int seq, JsonElement args, CancellationToken ct)
    {
        var varRef = args.TryGetProperty("variablesReference", out var vrEl) ? vrEl.GetInt32() : -1;

        if (!_varRefs.TryGetValue(varRef, out var vars))
        {
            await SendResponseAsync(seq, "variables", new { variables = Array.Empty<object>() }, ct);
            return;
        }

        var result = vars.Select(v => new
        {
            name = $"${v.Name}",
            value = v.Value,
            type = v.Type,
            variablesReference = 0  // no drill-down in V1
        }).ToArray();

        await SendResponseAsync(seq, "variables", new { variables = result }, ct);
    }

    // ── evaluate ─────────────────────────────────────────────────────────────

    private async Task HandleEvaluateAsync(int seq, JsonElement args, CancellationToken ct)
    {
        var expression = args.TryGetProperty("expression", out var exEl) ? exEl.GetString() ?? "" : "";

        // For hover/watch: look up variable names in current scope
        if (_session is not null)
        {
            var varName = expression.TrimStart('$');
            var vars = _session.GetFormattedVariables();
            var match = vars.FirstOrDefault(v => string.Equals(v.Name, varName, StringComparison.Ordinal));
            if (match.Name is not null)
            {
                await SendResponseAsync(seq, "evaluate", new { result = match.Value, type = match.Type, variablesReference = 0 }, ct);
                return;
            }
        }

        await SendResponseAsync(seq, "evaluate", new { result = "(not in scope)", variablesReference = 0 }, ct);
    }

    // ── Protocol I/O ──────────────────────────────────────────────────────────

    private async Task<string?> ReadMessageAsync(CancellationToken ct)
    {
        var headerBuilder = new StringBuilder();

        // Read headers line by line
        int contentLength = -1;
        while (true)
        {
            var line = await ReadLineAsync(ct);
            if (line is null) return null;
            if (line.Length == 0) break; // blank line = end of headers

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["Content-Length:".Length..].Trim();
                if (int.TryParse(value, out var len))
                    contentLength = len;
            }
        }

        if (contentLength <= 0) return null;

        var buffer = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = await _input.ReadAsync(buffer.AsMemory(read, contentLength - read), ct);
            if (n == 0) return null;
            read += n;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var n = await _input.ReadAsync(buf.AsMemory(0, 1), ct);
            if (n == 0) return sb.Length > 0 ? sb.ToString() : null;
            var ch = (char)buf[0];
            if (ch == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append(ch);
        }
    }

    private async Task SendResponseAsync(int requestSeq, string command, object? body, CancellationToken ct)
    {
        var msg = new
        {
            type = "response",
            request_seq = requestSeq,
            success = true,
            command,
            body
        };
        await WriteMessageAsync(JsonSerializer.Serialize(msg, JsonOpts), ct);
    }

    private async Task SendErrorResponseAsync(int requestSeq, string command, string message, CancellationToken ct)
    {
        var msg = new
        {
            type = "response",
            request_seq = requestSeq,
            success = false,
            command,
            message,
            body = new { error = new { id = 1, format = message } }
        };
        await WriteMessageAsync(JsonSerializer.Serialize(msg, JsonOpts), ct);
    }

    private async Task SendEventAsync(string eventName, object body, CancellationToken ct = default)
    {
        var msg = new { type = "event", @event = eventName, body };
        await WriteMessageAsync(JsonSerializer.Serialize(msg, JsonOpts), ct);
    }

    private async Task SendOutputAsync(string category, string output, CancellationToken ct = default)
    {
        await SendEventAsync("output", new { category, output }, ct);
    }

    private async Task WriteMessageAsync(string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.UTF8.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await _writeLock.WaitAsync(ct);
        try
        {
            await _output.WriteAsync(header, ct);
            await _output.WriteAsync(body, ct);
            await _output.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
