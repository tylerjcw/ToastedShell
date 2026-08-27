using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Tosh.Runtime.Formats;

using Tosh.Runtime;

namespace Tosh.Stdlib.Net;

[CommandCategory("Network")]
[CommandArgument("<get|post|put|patch|delete|head|options> <url>", "Send an HTTP request immediately.", Required = false)]
[CommandArgument("request <method> <url>", "Build an immutable HTTP request definition without sending it yet.", Required = false)]
[CommandArgument("send [request]", "Send an HttpRequestDefinition or HttpRequestMessage from an argument or the pipeline.", Required = false)]
[CommandArgument("serve|host <dir>", "Start a temporary HTTP file server rooted at a directory and return a live server handle.", Required = false)]
[CommandArgument("servers", "List open HTTP file server handles.", Required = false)]
[CommandArgument("stop [handle|id ...]", "Stop one or more HTTP file servers, or all of them when no target is provided.", Required = false)]
[CommandOption("--header <name> <value>", "Add a request header. Repeatable.")]
[CommandOption("--json <value>", "Serialize a value as JSON request content.")]
[CommandOption("--body <text>", "Send plain text request content.")]
[CommandOption("--file <path>", "Send request content from a file.")]
[CommandOption("--form <record>", "Send application/x-www-form-urlencoded content from a record-like value.")]
[CommandOption("--content-type <value>", "Override the request content type.")]
[CommandOption("--timeout <duration>", "Set the per-request timeout.")]
[CommandOption("--bearer <token>", "Add a Bearer authorization header.")]
[CommandOption("--auth basic <user> <pass>", "Add a Basic authorization header.")]
[CommandOption("--follow | --no-follow", "Control redirect following for the request.")]
[CommandOption("--as <response|json|text|bytes|lines>", "Choose how the response should be materialized.", Default = "response")]
[CommandOption("--out <path>", "Write the raw response body bytes to a file.")]
[CommandOption("--fail", "Turn HTTP non-success status codes into diagnostics instead of returning a response object/body.")]
[CommandOption("--browse", "For `http serve`, render a lightweight browser page with directory listings and share metadata.")]
[CommandOption("--upload", "For `http serve`, accept uploads. Browser uploads and raw PUT/POST uploads are both supported.")]
[CommandOption("--once", "For `http serve`, close the server after the first handled request.")]
[CommandOption("--bind <address>", "Bind `http serve` to a specific address.", Default = "127.0.0.1")]
[CommandOption("--lan", "Bind `http serve` to all interfaces and advertise LAN-friendly share URLs for other devices.")]
[CommandOption("--port <port>", "Bind `http serve` to a specific port. Use `0` to request an ephemeral port.")]
[CommandOption("--index <file>", "Serve this index file for directories before directory listings.", Default = "index.html")]
[CommandOption("--token <token>", "Protect `http serve` with a fixed token. The returned ShareUrl includes it automatically.")]
[CommandOption("--generate-token", "Generate a random token for `http serve` and return it on the server handle.")]
[CommandExample("http get https://example.com --as text", Title = "Fetch a text response")]
[CommandExample("http post https://example.com/api --json {| Name = \"Toast\" |} --as response", Title = "Send JSON and keep the structured response")]
[CommandExample("http request GET https://example.com | http send --as response", Title = "Build then send a request object")]
[CommandExample("http serve ./share --browse", Title = "Start a temporary file server with a lightweight share page")]
[CommandExample("http serve ./share --browse --lan", Title = "Share a directory with other devices on the same network")]
[CommandExample("http serve ./dropbox --upload --generate-token", Title = "Start a temporary upload server with a generated access token")]
[CommandExample("http servers | get { Id, ShareUrl, Upload }", Title = "Inspect open temporary file servers")]
[CommandNote("The native `http` builtin is object-first and backed by .NET HttpClient. Use `--as response` for a structured response object, or `--as json|text|bytes|lines` to project the body directly. `http serve <dir>` starts a temporary file server and returns a live server handle; `--browse`, `--upload`, `--token`, `--generate-token`, and `--lan` turn it into a lightweight cross-platform sharing tool. Use `http servers`, `http stop`, or `close` to manage it.")]
[CommandOutput("Returns either a structured HttpResponseInfo object or a decoded body, depending on `--as`. `http serve` returns live HttpFileServerHandle objects.", TypeName = "HttpResponseInfo", Members = "StatusCode, ReasonPhrase, Method, RequestUri, ContentType, Body, Duration", Mode = "mixed")]
[CommandSideEffects(Network = true)]
[PipelineInput(AcceptsScalar = true, Description = "`http send` accepts a single HttpRequestDefinition or HttpRequestMessage from the pipeline.")]
public sealed class HttpCommand : ShellCommand
{
    private static readonly string[] FriendlyTokenAdjectives =
    [
        "amber",
        "ancient",
        "bold",
        "brisk",
        "calm",
        "clever",
        "cool",
        "cosmic",
        "dapper",
        "eager",
        "fancy",
        "gentle",
        "golden",
        "grand",
        "happy",
        "honest",
        "kind",
        "lively",
        "lucky",
        "mellow",
        "minty",
        "nimble",
        "plucky",
        "quiet",
        "rapid",
        "silver",
        "steady",
        "sunny",
        "swift",
        "tidy",
        "vivid",
        "zesty",
    ];

    private static readonly string[] FriendlyTokenNouns =
    [
        "aardvark",
        "acorn",
        "badger",
        "beacon",
        "breeze",
        "cedar",
        "cloud",
        "comet",
        "falcon",
        "forest",
        "fox",
        "harbor",
        "hazel",
        "heron",
        "lantern",
        "maple",
        "meadow",
        "otter",
        "owl",
        "pine",
        "raven",
        "river",
        "robin",
        "shadow",
        "sparrow",
        "spruce",
        "starling",
        "sunrise",
        "thunder",
        "valley",
        "willow",
        "zephyr",
    ];

    private readonly DataFormatRegistry _formats;

    public HttpCommand(DataFormatRegistry formats)
        : base(
            "http",
            "Sends HTTP requests and returns structured response objects or decoded bodies.",
            "http <get|post|put|patch|delete|head|options|request|send|serve|host|servers|stop> ...")
    {
        _formats = formats;
    }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException(
                "http requires a subcommand: get, post, put, patch, delete, head, options, request, send, serve, host, servers, or stop.");
        }

        var subcommand = CommandArguments.RequireString(context.Arguments, 0, "subcommand");

        switch (subcommand.ToLowerInvariant())
        {
            case "serve":
            case "host":
                yield return ExecuteServe(context);
                yield break;

            case "servers":
                await foreach (var item in ExecuteServersAsync(context))
                {
                    yield return item;
                }

                yield break;

            case "stop":
                await foreach (var item in ExecuteStopAsync(context))
                {
                    yield return item;
                }

                yield break;

            case "request":
                yield return await BuildRequestFromArgumentsAsync(context);
                yield break;

            case "send":
                await foreach (var item in ExecuteSendAsync(context))
                {
                    yield return item;
                }

                yield break;

            case "get":
            case "post":
            case "put":
            case "patch":
            case "delete":
            case "head":
            case "options":
                await foreach (var item in ExecuteVerbAsync(context, subcommand))
                {
                    yield return item;
                }

                yield break;
        }

        throw new InvalidOperationException(
            "http subcommand must be get, post, put, patch, delete, head, options, request, send, serve, host, servers, or stop.");
    }

    private static HttpFileServerHandle ExecuteServe(CommandContext context)
    {
        var options = ParseServeOptions(context.Arguments, 1, context.Shell().CurrentDirectory);

        return HttpFileServerHandle.Start(
            options.RootPath!,
            options.BindAddress,
            options.Port,
            options.DirectoryBrowsingEnabled,
            options.UploadEnabled,
            options.ServeOnce,
            options.IndexFileName,
            options.AccessToken);
    }

    private static async IAsyncEnumerable<object?> ExecuteServersAsync(CommandContext context)
    {
        foreach (var handle in HttpFileServerHandle.GetOpenHandles())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return handle;
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<object?> ExecuteStopAsync(CommandContext context)
    {
        var handles = await ResolveServerTargetsAsync(context, 1);

        foreach (var handle in handles)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            handle.Close();
            yield return handle;
        }
    }

    private async IAsyncEnumerable<object?> ExecuteVerbAsync(CommandContext context, string method)
    {
        var url = RequireUri(context.Arguments, 1, "url");
        var options = await ParseOptionsAsync(context, 2, allowOutputOptions: true);
        var request = await BuildRequestAsync(method, url, options);

        await foreach (var item in SendAsync(context, request, options))
        {
            yield return item;
        }
    }

    private async Task<HttpRequestDefinition> BuildRequestFromArgumentsAsync(CommandContext context)
    {
        var method = CommandArguments.RequireString(context.Arguments, 1, "method");
        var url = RequireUri(context.Arguments, 2, "url");
        var options = await ParseOptionsAsync(context, 3, allowOutputOptions: false);
        return await BuildRequestAsync(method, url, options);
    }

    private async IAsyncEnumerable<object?> ExecuteSendAsync(CommandContext context)
    {
        var hasExplicitRequestArgument = context.Arguments.Count > 1 &&
            (context.Arguments[1] is not string option || !option.StartsWith("-", StringComparison.Ordinal));

        var requestSource = hasExplicitRequestArgument
            ? context.Arguments[1]
            : await ReadSinglePipelineValueAsync(context, "http send expects an HttpRequestDefinition or HttpRequestMessage.");

        var request = await NormalizeRequestAsync(requestSource);
        var options = await ParseOptionsAsync(context, hasExplicitRequestArgument ? 2 : 1, allowOutputOptions: true);

        if (options.HasRequestMutation)
        {
            request = await ApplyRequestOverridesAsync(request, options);
        }

        await foreach (var item in SendAsync(context, request, options))
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<object?> SendAsync(CommandContext context, HttpRequestDefinition request, HttpCommandOptions options)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = request.FollowRedirects,
        };
        using var client = new HttpClient(handler);

        if (request.Timeout is { } timeout)
        {
            client.Timeout = timeout;
        }

        using var message = CreateRequestMessage(request);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            context.Shell().SetLastExitCode(1);
            context.PipelineExitStatusTracker?.Record(1);
            throw context.CreateDiagnostic(
                code: "tosh.runtime.http_request_failed",
                title: $"The HTTP request failed. {exception.Message}");
        }

        using (response)
        {
            var bodyBytes = response.Content is null
                ? Array.Empty<byte>()
                : await response.Content.ReadAsByteArrayAsync(context.CancellationToken);

            stopwatch.Stop();

            var statusCode = (int)response.StatusCode;
            context.Shell().SetLastExitCode(response.IsSuccessStatusCode ? 0 : statusCode);
            context.PipelineExitStatusTracker?.Record(response.IsSuccessStatusCode ? 0 : statusCode);

            string? savedTo = null;

            if (!string.IsNullOrWhiteSpace(options.OutputPath))
            {
                savedTo = PathUtilities.ResolvePath(context.Shell().CurrentDirectory, options.OutputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(savedTo) ?? context.Shell().CurrentDirectory);
                await File.WriteAllBytesAsync(savedTo, bodyBytes, context.CancellationToken);
            }

            if (options.Fail && !response.IsSuccessStatusCode)
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.http_status_failed",
                    title: $"HTTP request returned {(int)response.StatusCode} {response.ReasonPhrase ?? string.Empty}".Trim(),
                    help: "Remove --fail to receive the response object/body even for non-success status codes.");
            }

            await foreach (var item in MaterializeResponseAsync(context, request, response, bodyBytes, stopwatch.Elapsed, savedTo, options.AsMode))
            {
                yield return item;
            }
        }
    }

    private async IAsyncEnumerable<object?> MaterializeResponseAsync(
        CommandContext context,
        HttpRequestDefinition request,
        HttpResponseMessage response,
        byte[] bodyBytes,
        TimeSpan duration,
        string? savedTo,
        HttpResponseBodyMode mode)
    {
        switch (mode)
        {
            case HttpResponseBodyMode.Json:
                {
                    foreach (var item in await DeserializeJsonBodyAsync(bodyBytes))
                    {
                        yield return item;
                    }

                    yield break;
                }
            case HttpResponseBodyMode.Text:
                yield return new ShellTextLine(DecodeResponseText(bodyBytes, response.Content?.Headers.ContentType?.CharSet));
                yield break;

            case HttpResponseBodyMode.Lines:
                foreach (var line in TextInputUtilities.SplitLines(DecodeResponseText(bodyBytes, response.Content?.Headers.ContentType?.CharSet)))
                {
                    yield return new ShellTextLine(line);
                }

                yield break;

            case HttpResponseBodyMode.Bytes:
                yield return bodyBytes;
                yield break;

            case HttpResponseBodyMode.Response:
            default:
                yield return await CreateResponseInfoAsync(request, response, bodyBytes, duration, savedTo);
                yield break;
        }
    }

    private async Task<HttpResponseInfo> CreateResponseInfoAsync(
        HttpRequestDefinition request,
        HttpResponseMessage response,
        byte[] bodyBytes,
        TimeSpan duration,
        string? savedTo)
    {
        var body = await DecodeResponseBodyForResponseModeAsync(response, bodyBytes);

        return new HttpResponseInfo(
            (int)response.StatusCode,
            response.ReasonPhrase,
            response.IsSuccessStatusCode,
            request.Method,
            request.RequestUri,
            response.RequestMessage?.RequestUri,
            response.Version.ToString(),
            ToHeaderDictionary(response.Headers),
            ToHeaderDictionary(response.Content?.Headers),
            response.Content?.Headers.ContentType?.ToString(),
            response.Content?.Headers.ContentLength,
            duration,
            body,
            savedTo);
    }

    private async Task<object?> DecodeResponseBodyForResponseModeAsync(HttpResponseMessage response, byte[] bodyBytes)
    {
        if (bodyBytes.Length == 0)
        {
            return null;
        }

        var contentType = response.Content?.Headers.ContentType?.MediaType;

        if (LooksLikeJson(contentType))
        {
            try
            {
                var values = await DeserializeJsonBodyAsync(bodyBytes);
                return values.Count switch
                {
                    0 => null,
                    1 => values[0],
                    _ => values.ToArray(),
                };
            }
            catch
            {
                // Fall back to plain text if the content type advertised JSON but the body was malformed.
            }
        }

        if (LooksLikeText(contentType))
        {
            return DecodeResponseText(bodyBytes, response.Content?.Headers.ContentType?.CharSet);
        }

        return bodyBytes;
    }

    private async Task<IReadOnlyList<object?>> DeserializeJsonBodyAsync(byte[] bodyBytes)
    {
        if (bodyBytes.Length == 0)
        {
            return Array.Empty<object?>();
        }

        var text = Encoding.UTF8.GetString(bodyBytes);
        var format = _formats.Resolve("json");
        var values = new List<object?>();

        await foreach (var item in format.DeserializeAsync(text, Array.Empty<object?>()))
        {
            values.Add(item);
        }

        return values;
    }

    private static string DecodeResponseText(byte[] bodyBytes, string? charset)
    {
        if (bodyBytes.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
            {
                return Encoding.GetEncoding(charset).GetString(bodyBytes);
            }
        }
        catch (ArgumentException)
        {
        }

        return Encoding.UTF8.GetString(bodyBytes);
    }

    private async Task<HttpRequestDefinition> BuildRequestAsync(string method, Uri url, HttpCommandOptions options)
    {
        var body = await ResolveBodyAsync(options);
        var headers = CloneHeaders(options.Headers);
        var contentType = options.ContentType ?? body.ContentType;

        if (options.BearerToken is not null)
        {
            AddHeader(headers, "Authorization", $"Bearer {options.BearerToken}");
        }

        if (options.BasicAuthUser is not null)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.BasicAuthUser}:{options.BasicAuthPassword ?? string.Empty}"));
            AddHeader(headers, "Authorization", $"Basic {encoded}");
        }

        return new HttpRequestDefinition(
            method,
            url,
            headers.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<string>)entry.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase),
            body.Bytes,
            contentType,
            options.Timeout,
            options.FollowRedirects,
            body.Kind,
            body.Preview);
    }

    private async Task<HttpRequestDefinition> ApplyRequestOverridesAsync(HttpRequestDefinition request, HttpCommandOptions options)
    {
        var mergedHeaders = CloneHeaders(request.Headers);

        foreach (var header in options.Headers)
        {
            mergedHeaders[header.Key] = new List<string>(header.Value);
        }

        if (options.BearerToken is not null)
        {
            mergedHeaders["Authorization"] = [$"Bearer {options.BearerToken}"];
        }

        if (options.BasicAuthUser is not null)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.BasicAuthUser}:{options.BasicAuthPassword ?? string.Empty}"));
            mergedHeaders["Authorization"] = [$"Basic {encoded}"];
        }

        var body = options.HasExplicitBody
            ? await ResolveBodyAsync(options)
            : new ResolvedHttpBody(request.BodyBytes, request.ContentType, request.BodyKind, request.BodyPreview);

        return new HttpRequestDefinition(
            request.Method,
            request.RequestUri,
            mergedHeaders.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<string>)entry.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase),
            body.Bytes,
            options.ContentType ?? body.ContentType,
            options.Timeout ?? request.Timeout,
            options.FollowRedirectsExplicitlySet ? options.FollowRedirects : request.FollowRedirects,
            body.Kind,
            body.Preview);
    }

    private async Task<ResolvedHttpBody> ResolveBodyAsync(HttpCommandOptions options)
    {
        if (!options.HasExplicitBody)
        {
            return ResolvedHttpBody.None;
        }

        if (options.JsonBody is not null)
        {
            var json = await SerializeJsonAsync(options.JsonBody);
            return new ResolvedHttpBody(
                Encoding.UTF8.GetBytes(json),
                options.ContentType ?? "application/json; charset=utf-8",
                "json",
                Preview(json));
        }

        if (options.TextBody is not null)
        {
            var text = ExternalTextSerializer.Serialize(options.TextBody);
            return new ResolvedHttpBody(
                Encoding.UTF8.GetBytes(text),
                options.ContentType ?? "text/plain; charset=utf-8",
                "text",
                Preview(text));
        }

        if (options.FileBodyPath is not null)
        {
            var path = PathUtilities.ResolvePath(options.CurrentDirectory ?? Environment.CurrentDirectory, options.FileBodyPath);

            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"File '{path}' does not exist.");
            }

            var bytes = await File.ReadAllBytesAsync(path);
            return new ResolvedHttpBody(
                bytes,
                options.ContentType ?? GuessContentType(path),
                "file",
                Path.GetFileName(path));
        }

        if (options.FormBody is not null)
        {
            var pairs = NormalizeFormBody(options.FormBody);
            var text = string.Join("&", pairs.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

            return new ResolvedHttpBody(
                Encoding.UTF8.GetBytes(text),
                options.ContentType ?? "application/x-www-form-urlencoded; charset=utf-8",
                "form",
                Preview(text));
        }

        return ResolvedHttpBody.None;
    }

    private async Task<string> SerializeJsonAsync(object? value)
    {
        var format = _formats.Resolve("json");
        var parts = new List<string>();

        await foreach (var item in format.SerializeAsync([value], ["--compact"]))
        {
            parts.Add(ExternalTextSerializer.Serialize(item));
        }

        return string.Concat(parts);
    }

    private static IReadOnlyList<KeyValuePair<string, string>> NormalizeFormBody(object body)
    {
        if (!ShellRecordUtilities.TryGetFields(body, out var fields))
        {
            throw new InvalidOperationException("--form expects a record or dictionary-like value.");
        }

        return fields
            .Select(field => new KeyValuePair<string, string>(field.Key, ExternalTextSerializer.Serialize(field.Value)))
            .ToArray();
    }

    private static HttpRequestMessage CreateRequestMessage(HttpRequestDefinition request)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.Method), request.RequestUri);

        if (request.BodyBytes is not null)
        {
            message.Content = new ByteArrayContent(request.BodyBytes);

            if (!string.IsNullOrWhiteSpace(request.ContentType))
            {
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
            }
        }

        foreach (var header in request.Headers)
        {
            foreach (var value in header.Value)
            {
                if (message.Headers.TryAddWithoutValidation(header.Key, value))
                {
                    continue;
                }

                if (message.Content is not null && message.Content.Headers.TryAddWithoutValidation(header.Key, value))
                {
                    continue;
                }

                throw new InvalidOperationException($"HTTP header '{header.Key}' is not valid for this request.");
            }
        }

        return message;
    }

    private async Task<HttpRequestDefinition> NormalizeRequestAsync(object? requestSource)
    {
        switch (requestSource)
        {
            case HttpRequestDefinition request:
                return request;
            case HttpRequestMessage message:
                return await FromHttpRequestMessageAsync(message);
            default:
                throw new InvalidOperationException("http send expects an HttpRequestDefinition or HttpRequestMessage.");
        }
    }

    private static async Task<HttpRequestDefinition> FromHttpRequestMessageAsync(HttpRequestMessage message)
    {
        byte[]? bodyBytes = null;
        string? bodyPreview = null;
        string? bodyKind = null;
        string? contentType = null;

        if (message.Content is not null)
        {
            bodyBytes = await message.Content.ReadAsByteArrayAsync();
            contentType = message.Content.Headers.ContentType?.ToString();
            bodyPreview = bodyBytes.Length == 0 ? null : Preview(DecodeContentPreview(message.Content.Headers.ContentType?.CharSet, bodyBytes));
            bodyKind = contentType is not null && LooksLikeJson(message.Content.Headers.ContentType?.MediaType)
                ? "json"
                : "text";
        }

        var headers = ToMutableHeaderDictionary(message.Headers);

        if (message.Content is not null)
        {
            foreach (var header in message.Content.Headers)
            {
                if (!headers.TryGetValue(header.Key, out var values))
                {
                    values = [];
                    headers[header.Key] = values;
                }

                values.AddRange(header.Value);
            }
        }

        return new HttpRequestDefinition(
            message.Method.Method,
            message.RequestUri ?? throw new InvalidOperationException("HttpRequestMessage.RequestUri is required."),
            headers.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<string>)entry.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
            bodyBytes,
            contentType,
            null,
            true,
            bodyKind,
            bodyPreview);
    }

    private async Task<HttpCommandOptions> ParseOptionsAsync(CommandContext context, int startIndex, bool allowOutputOptions)
    {
        var options = new HttpCommandOptions
        {
            CurrentDirectory = context.Shell().CurrentDirectory,
        };

        for (var index = startIndex; index < context.Arguments.Count; index++)
        {
            if (context.Arguments[index] is not string option || !option.StartsWith("-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected argument '{context.Arguments[index]}'.");
            }

            switch (option)
            {
                case "--header":
                    {
                        var name = CommandArguments.RequireString(context.Arguments, ++index, "header name");
                        var value = ExternalTextSerializer.Serialize(RequireArgument(context.Arguments, ++index, "header value"));
                        AddHeader(options.Headers, name, value);
                        break;
                    }
                case "--json":
                    EnsureNoBodyConflict(options, "--json");
                    options.JsonBody = RequireArgument(context.Arguments, ++index, "json value");
                    break;
                case "--body":
                    EnsureNoBodyConflict(options, "--body");
                    options.TextBody = RequireArgument(context.Arguments, ++index, "body");
                    break;
                case "--file":
                    EnsureNoBodyConflict(options, "--file");
                    options.FileBodyPath = CommandArguments.RequireString(context.Arguments, ++index, "path");
                    break;
                case "--form":
                    EnsureNoBodyConflict(options, "--form");
                    options.FormBody = RequireArgument(context.Arguments, ++index, "form value");
                    break;
                case "--content-type":
                    options.ContentType = CommandArguments.RequireString(context.Arguments, ++index, "content type");
                    break;
                case "--timeout":
                    options.Timeout = CommandArguments.RequireConverted<TimeSpan>(context.Arguments, ++index, "timeout");
                    break;
                case "--bearer":
                    options.BearerToken = ExternalTextSerializer.Serialize(RequireArgument(context.Arguments, ++index, "token"));
                    break;
                case "--auth":
                    {
                        var scheme = CommandArguments.RequireString(context.Arguments, ++index, "auth scheme");

                        if (!string.Equals(scheme, "basic", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("http --auth currently supports only 'basic'.");
                        }

                        options.BasicAuthUser = ExternalTextSerializer.Serialize(RequireArgument(context.Arguments, ++index, "username"));
                        options.BasicAuthPassword = ExternalTextSerializer.Serialize(RequireArgument(context.Arguments, ++index, "password"));
                        break;
                    }
                case "--follow":
                    options.FollowRedirects = true;
                    options.FollowRedirectsExplicitlySet = true;
                    break;
                case "--no-follow":
                    options.FollowRedirects = false;
                    options.FollowRedirectsExplicitlySet = true;
                    break;
                case "--as":
                    EnsureOutputOptionsAllowed(allowOutputOptions, option);
                    options.AsMode = ParseBodyMode(CommandArguments.RequireString(context.Arguments, ++index, "mode"));
                    break;
                case "--out":
                    EnsureOutputOptionsAllowed(allowOutputOptions, option);
                    options.OutputPath = CommandArguments.RequireString(context.Arguments, ++index, "path");
                    break;
                case "--fail":
                    EnsureOutputOptionsAllowed(allowOutputOptions, option);
                    options.Fail = true;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown http option '{option}'.");
            }
        }

        return options;
    }

    private static void EnsureOutputOptionsAllowed(bool allowOutputOptions, string option)
    {
        if (!allowOutputOptions)
        {
            throw new InvalidOperationException($"The '{option}' option is only valid for HTTP requests that are being sent.");
        }
    }

    private static void EnsureNoBodyConflict(HttpCommandOptions options, string optionName)
    {
        if (options.HasExplicitBody)
        {
            throw new InvalidOperationException($"Only one of --json, --body, --file, or --form may be used. '{optionName}' conflicts with another body option.");
        }
    }

    private static Uri RequireUri(IReadOnlyList<object?> arguments, int index, string label)
    {
        if (index >= arguments.Count)
        {
            throw new InvalidOperationException($"Missing required argument: {label}.");
        }

        if (arguments[index] is Uri uri)
        {
            return uri;
        }

        var text = ExternalTextSerializer.Serialize(arguments[index]);

        if (Uri.TryCreate(text, UriKind.Absolute, out var parsedUri))
        {
            return parsedUri;
        }

        throw new InvalidOperationException($"Argument '{label}' must be an absolute URI.");
    }

    private static object? RequireArgument(IReadOnlyList<object?> arguments, int index, string label)
    {
        if (index >= arguments.Count)
        {
            throw new InvalidOperationException($"Missing required argument: {label}.");
        }

        return arguments[index];
    }

    private static async Task<object?> ReadSinglePipelineValueAsync(CommandContext context, string missingMessage)
    {
        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);

        if (!await enumerator.MoveNextAsync())
        {
            throw new InvalidOperationException(missingMessage);
        }

        var first = enumerator.Current;

        if (await enumerator.MoveNextAsync())
        {
            throw new InvalidOperationException("http send expects exactly one request object from the pipeline.");
        }

        return first;
    }

    private static Dictionary<string, List<string>> CloneHeaders<TValue>(IEnumerable<KeyValuePair<string, TValue>> source)
        where TValue : IEnumerable<string>
    {
        var cloned = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in source)
        {
            cloned[entry.Key] = [.. entry.Value];
        }

        return cloned;
    }

    private static Dictionary<string, List<string>> ToMutableHeaderDictionary(HttpHeaders headers)
    {
        return headers.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ToHeaderDictionary(HttpHeaders? headers)
    {
        if (headers is null)
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }

        return headers.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AddHeader(Dictionary<string, List<string>> headers, string name, string value)
    {
        if (!headers.TryGetValue(name, out var values))
        {
            values = [];
            headers[name] = values;
        }

        values.Add(value);
    }

    private static HttpServeOptions ParseServeOptions(
        IReadOnlyList<object?> arguments,
        int startIndex,
        string currentDirectory)
    {
        var options = new HttpServeOptions();

        for (var index = startIndex; index < arguments.Count; index++)
        {
            if (arguments[index] is string option && option.StartsWith("-", StringComparison.Ordinal))
            {
                switch (option)
                {
                    case "--bind":
                        options.BindAddress = CommandArguments.RequireString(arguments, ++index, "bind address");
                        continue;
                    case "--lan":
                        options.BindAddress = "0.0.0.0";
                        continue;
                    case "--port":
                        options.Port = CommandArguments.RequireConverted<int>(arguments, ++index, "port");
                        continue;
                    case "--browse":
                        options.DirectoryBrowsingEnabled = true;
                        continue;
                    case "--upload":
                        options.UploadEnabled = true;
                        continue;
                    case "--once":
                        options.ServeOnce = true;
                        continue;
                    case "--index":
                        options.IndexFileName = CommandArguments.RequireString(arguments, ++index, "index file");
                        continue;
                    case "--token":
                        options.AccessToken = CommandArguments.RequireString(arguments, ++index, "token");
                        continue;
                    case "--generate-token":
                        options.GenerateToken = true;
                        continue;
                    default:
                        throw new InvalidOperationException($"Unknown http serve option '{option}'.");
                }
            }

            if (options.RootPath is not null)
            {
                throw new InvalidOperationException("http serve accepts exactly one root path.");
            }

            options.RootPath = PathUtilities.ResolvePath(currentDirectory, ExternalTextSerializer.Serialize(arguments[index]));
        }

        if (options.RootPath is null)
        {
            throw new InvalidOperationException("http serve requires a root directory path.");
        }

        if (options.GenerateToken)
        {
            if (!string.IsNullOrWhiteSpace(options.AccessToken))
            {
                throw new InvalidOperationException("http serve cannot use --token and --generate-token together.");
            }

            options.AccessToken = GenerateFriendlyAccessToken();
        }

        return options;
    }

    private static async Task<IReadOnlyList<HttpFileServerHandle>> ResolveServerTargetsAsync(CommandContext context, int startIndex)
    {
        var handles = new List<HttpFileServerHandle>();
        var seen = new HashSet<int>();

        void AddHandle(HttpFileServerHandle handle)
        {
            if (seen.Add(handle.Id))
            {
                handles.Add(handle);
            }
        }

        for (var index = startIndex; index < context.Arguments.Count; index++)
        {
            AddHandle(ResolveServerTarget(context.Arguments[index]));
        }

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            AddHandle(ResolveServerTarget(item));
        }

        if (handles.Count == 0)
        {
            foreach (var handle in HttpFileServerHandle.GetOpenHandles())
            {
                AddHandle(handle);
            }
        }

        return handles;
    }

    private static HttpFileServerHandle ResolveServerTarget(object? value)
    {
        switch (value)
        {
            case HttpFileServerHandle handle:
                return handle;
            case int id when HttpFileServerHandle.TryGetOpenHandle(id, out var handleByInt):
                return handleByInt;
            case long longId when longId is >= int.MinValue and <= int.MaxValue && HttpFileServerHandle.TryGetOpenHandle((int)longId, out var handleByLong):
                return handleByLong;
            case string text when int.TryParse(text, out var parsedId) && HttpFileServerHandle.TryGetOpenHandle(parsedId, out var handleByString):
                return handleByString;
            default:
                throw new InvalidOperationException($"'{value}' is not a valid HTTP server handle or id.");
        }
    }

    private static HttpResponseBodyMode ParseBodyMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "response" => HttpResponseBodyMode.Response,
            "json" => HttpResponseBodyMode.Json,
            "text" => HttpResponseBodyMode.Text,
            "bytes" => HttpResponseBodyMode.Bytes,
            "lines" => HttpResponseBodyMode.Lines,
            _ => throw new InvalidOperationException("http --as mode must be response, json, text, bytes, or lines."),
        };
    }

    private static bool LooksLikeJson(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        return mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
               mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeText(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return true;
        }

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("csv", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeContentPreview(string? charset, byte[] bodyBytes)
    {
        if (bodyBytes.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
            {
                return Encoding.GetEncoding(charset).GetString(bodyBytes);
            }
        }
        catch (ArgumentException)
        {
        }

        return Encoding.UTF8.GetString(bodyBytes);
    }

    private static string GuessContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => "application/json; charset=utf-8",
            ".txt" or ".log" or ".md" => "text/plain; charset=utf-8",
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".xml" => "application/xml; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            ".tsv" => "text/tab-separated-values; charset=utf-8",
            _ => "application/octet-stream",
        };
    }

    private static string? Preview(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= 120 ? value : value[..117] + "...";
    }

    private static string GenerateFriendlyAccessToken()
    {
        var adjective = FriendlyTokenAdjectives[Random.Shared.Next(FriendlyTokenAdjectives.Length)];
        var noun = FriendlyTokenNouns[Random.Shared.Next(FriendlyTokenNouns.Length)];
        return $"{adjective}-{noun}";
    }

    private sealed class HttpCommandOptions
    {
        public Dictionary<string, List<string>> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public object? JsonBody { get; set; }

        public object? TextBody { get; set; }

        public string? FileBodyPath { get; set; }

        public object? FormBody { get; set; }

        public string? ContentType { get; set; }

        public TimeSpan? Timeout { get; set; }

        public string? BearerToken { get; set; }

        public string? BasicAuthUser { get; set; }

        public string? BasicAuthPassword { get; set; }

        public bool FollowRedirects { get; set; } = true;

        public bool FollowRedirectsExplicitlySet { get; set; }

        public HttpResponseBodyMode AsMode { get; set; } = HttpResponseBodyMode.Response;

        public string? OutputPath { get; set; }

        public bool Fail { get; set; }

        public string? CurrentDirectory { get; set; }

        public bool HasExplicitBody =>
            JsonBody is not null || TextBody is not null || FileBodyPath is not null || FormBody is not null;

        public bool HasRequestMutation =>
            Headers.Count > 0 ||
            HasExplicitBody ||
            ContentType is not null ||
            Timeout is not null ||
            BearerToken is not null ||
            BasicAuthUser is not null ||
            FollowRedirectsExplicitlySet;
    }

    private readonly record struct ResolvedHttpBody(byte[]? Bytes, string? ContentType, string? Kind, string? Preview)
    {
        public static readonly ResolvedHttpBody None = new(null, null, null, null);
    }

    private sealed class HttpServeOptions
    {
        public string? RootPath { get; set; }

        public string BindAddress { get; set; } = "127.0.0.1";

        public int Port { get; set; }

        public bool DirectoryBrowsingEnabled { get; set; }

        public bool UploadEnabled { get; set; }

        public bool ServeOnce { get; set; }

        public string IndexFileName { get; set; } = "index.html";

        public string? AccessToken { get; set; }

        public bool GenerateToken { get; set; }
    }

    private enum HttpResponseBodyMode
    {
        Response,
        Json,
        Text,
        Bytes,
        Lines,
    }
}
