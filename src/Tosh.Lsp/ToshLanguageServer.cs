using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tosh.LanguageServices;

namespace Tosh.Lsp;

public sealed class ToshLanguageServer
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ToshLanguageFeatures _features = new();
    private readonly Dictionary<string, string> _documents = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _supportsHierarchicalDocumentSymbols;
    private bool _shutdownRequested;

    public ToshLanguageServer(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var payload = await ReadMessageAsync(cancellationToken);

            if (payload is null)
            {
                break;
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.TryGetProperty("method", out var methodElement))
            {
                var method = methodElement.GetString() ?? string.Empty;
                root.TryGetProperty("id", out var id);
                root.TryGetProperty("params", out var parameters);

                if (id.ValueKind != JsonValueKind.Undefined)
                {
                    await HandleRequestAsync(id, method, parameters, cancellationToken);
                }
                else
                {
                    await HandleNotificationAsync(method, parameters, cancellationToken);
                }
            }
            else if (_shutdownRequested)
            {
                break;
            }
        }
    }

    private async Task HandleRequestAsync(JsonElement id, string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                _supportsHierarchicalDocumentSymbols =
                    parameters.ValueKind == JsonValueKind.Object &&
                    parameters.TryGetProperty("capabilities", out var capabilities) &&
                    capabilities.TryGetProperty("textDocument", out var textDocument) &&
                    textDocument.TryGetProperty("documentSymbol", out var documentSymbol) &&
                    documentSymbol.TryGetProperty("hierarchicalDocumentSymbolSupport", out var hierarchicalSupport) &&
                    hierarchicalSupport.ValueKind == JsonValueKind.True;

                await WriteResponseAsync(id, new
                {
                    capabilities = new
                    {
                        textDocumentSync = new
                        {
                            openClose = true,
                            change = 1
                        },
                        completionProvider = new
                        {
                            resolveProvider = false,
                            triggerCharacters = new[] { "$", "." }
                        },
                        signatureHelpProvider = new
                        {
                            triggerCharacters = new[] { "(", "," },
                            retriggerCharacters = new[] { "," }
                        },
                        hoverProvider = true,
                        definitionProvider = true,
                        documentSymbolProvider = true
                    },
                    serverInfo = new
                    {
                        name = "ToSh Language Server",
                        version = "0.1.0"
                    }
                }, cancellationToken);
                break;

            case "shutdown":
                _shutdownRequested = true;
                await WriteResponseAsync(id, null, cancellationToken);
                break;

            case "textDocument/completion":
                {
                    var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
                    var position = parameters.GetProperty("position").Deserialize<LspPosition>(_jsonOptions) ?? new LspPosition(0, 0);
                    _documents.TryGetValue(uri, out var text);
                    var items = _features.GetCompletionItems(text ?? string.Empty, position, uri);
                    await WriteResponseAsync(id, items, cancellationToken);
                    break;
                }

            case "textDocument/signatureHelp":
                {
                    var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
                    var position = parameters.GetProperty("position").Deserialize<LspPosition>(_jsonOptions) ?? new LspPosition(0, 0);
                    _documents.TryGetValue(uri, out var text);
                    var signatureHelp = _features.GetSignatureHelp(text ?? string.Empty, uri, position);
                    await WriteResponseAsync(id, signatureHelp, cancellationToken);
                    break;
                }

            case "textDocument/hover":
                {
                    var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
                    var position = parameters.GetProperty("position").Deserialize<LspPosition>(_jsonOptions) ?? new LspPosition(0, 0);
                    _documents.TryGetValue(uri, out var text);
                    var hover = _features.GetHover(text ?? string.Empty, uri, position);
                    await WriteResponseAsync(id, hover, cancellationToken);
                    break;
                }

            case "textDocument/definition":
                {
                    var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
                    var position = parameters.GetProperty("position").Deserialize<LspPosition>(_jsonOptions) ?? new LspPosition(0, 0);
                    _documents.TryGetValue(uri, out var text);
                    var definitions = _features.GetDefinitions(text ?? string.Empty, uri, position);
                    await WriteResponseAsync(id, definitions.Count == 0 ? null : definitions, cancellationToken);
                    break;
                }

            case "textDocument/documentSymbol":
                {
                    var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
                    _documents.TryGetValue(uri, out var text);
                    object symbols = _supportsHierarchicalDocumentSymbols
                        ? _features.GetDocumentSymbols(text ?? string.Empty, uri)
                        : _features.GetSymbolInformations(text ?? string.Empty, uri);
                    await WriteResponseAsync(id, symbols, cancellationToken);
                    break;
                }

            default:
                await WriteErrorAsync(id, -32601, $"Method '{method}' is not supported.", cancellationToken);
                break;
        }
    }

    private async Task HandleNotificationAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialized":
                return;

            case "exit":
                Environment.Exit(_shutdownRequested ? 0 : 1);
                return;

            case "textDocument/didOpen":
                {
                    var document = parameters.GetProperty("textDocument");
                    var uri = document.GetProperty("uri").GetString() ?? string.Empty;
                    var text = document.GetProperty("text").GetString() ?? string.Empty;
                    _documents[uri] = text;
                    await PublishDiagnosticsAsync(uri, text, cancellationToken);
                    return;
                }

            case "textDocument/didChange":
                {
                    var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
                    var changes = parameters.GetProperty("contentChanges");

                    if (changes.GetArrayLength() > 0)
                    {
                        var text = changes[0].GetProperty("text").GetString() ?? string.Empty;
                        _documents[uri] = text;
                        await PublishDiagnosticsAsync(uri, text, cancellationToken);
                    }

                    return;
                }

            case "textDocument/didClose":
                {
                    var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString() ?? string.Empty;
                    _documents.Remove(uri);
                    await WriteNotificationAsync("textDocument/publishDiagnostics", new
                    {
                        uri,
                        diagnostics = Array.Empty<LspDiagnostic>()
                    }, cancellationToken);
                    return;
                }
        }
    }

    private async Task PublishDiagnosticsAsync(string uri, string text, CancellationToken cancellationToken)
    {
        var diagnostics = _features.GetDiagnostics(text, uri);
        await WriteNotificationAsync("textDocument/publishDiagnostics", new
        {
            uri,
            diagnostics
        }, cancellationToken);
    }

    private async Task<string?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellationToken);

            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                break;
            }

            var separator = line.IndexOf(':');

            if (separator < 0)
            {
                continue;
            }

            headers[line[..separator]] = line[(separator + 1)..].Trim();
        }

        if (!headers.TryGetValue("Content-Length", out var lengthText) ||
            !int.TryParse(lengthText, out var length) ||
            length < 0)
        {
            return null;
        }

        var buffer = new byte[length];
        var totalRead = 0;

        while (totalRead < length)
        {
            var read = await _input.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), cancellationToken);

            if (read == 0)
            {
                return null;
            }

            totalRead += read;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();

        while (true)
        {
            var buffer = new byte[1];
            var read = await _input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);

            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            }

            if (buffer[0] == (byte)'\n')
            {
                return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            }

            bytes.Add(buffer[0]);
        }
    }

    private Task WriteResponseAsync(JsonElement id, object? result, CancellationToken cancellationToken)
    {
        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            id = JsonNode.Parse(id.GetRawText()),
            result
        }, cancellationToken);
    }

    private Task WriteErrorAsync(JsonElement id, int code, string message, CancellationToken cancellationToken)
    {
        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            id = JsonNode.Parse(id.GetRawText()),
            error = new
            {
                code,
                message
            }
        }, cancellationToken);
    }

    private Task WriteNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        }, cancellationToken);
    }

    private async Task WriteMessageAsync(object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteAsync(header, cancellationToken);
            await _output.WriteAsync(bytes, cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
