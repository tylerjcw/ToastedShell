using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tosh.LanguageServices;

namespace Tosh.Mcp;

public sealed class ToshMcpServer
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ToshLanguageFeatures _features = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ToshMcpServer(Stream input, Stream output)
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

            if (!root.TryGetProperty("method", out var methodElement))
            {
                continue;
            }

            var method = methodElement.GetString() ?? string.Empty;
            root.TryGetProperty("id", out var id);
            root.TryGetProperty("params", out var parameters);

            if (id.ValueKind != JsonValueKind.Undefined)
            {
                await HandleRequestAsync(id, method, parameters, cancellationToken);
            }
        }
    }

    private async Task HandleRequestAsync(JsonElement id, string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                await WriteResponseAsync(id, new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { } },
                    serverInfo = new
                    {
                        name = "tosh-language-services",
                        version = "0.1.0"
                    }
                }, cancellationToken);
                break;

            case "tools/list":
                await WriteResponseAsync(id, new { tools = GetToolDefinitions() }, cancellationToken);
                break;

            case "tools/call":
                await HandleToolCallAsync(id, parameters, cancellationToken);
                break;

            case "ping":
                await WriteResponseAsync(id, new { }, cancellationToken);
                break;

            default:
                await WriteErrorAsync(id, -32601, $"Method '{method}' is not supported.", cancellationToken);
                break;
        }
    }

    private async Task HandleToolCallAsync(JsonElement id, JsonElement parameters, CancellationToken cancellationToken)
    {
        var toolName = parameters.GetProperty("name").GetString() ?? string.Empty;
        var arguments = parameters.TryGetProperty("arguments", out var args) ? args : default;

        try
        {
            var result = toolName switch
            {
                "lsp_diagnostics" => ExecuteDiagnostics(arguments),
                "lsp_completions" => ExecuteCompletions(arguments),
                "lsp_hover" => ExecuteHover(arguments),
                "lsp_signature_help" => ExecuteSignatureHelp(arguments),
                "lsp_definitions" => ExecuteDefinitions(arguments),
                "lsp_document_symbols" => ExecuteDocumentSymbols(arguments),
                _ => throw new InvalidOperationException($"Unknown tool '{toolName}'.")
            };

            await WriteResponseAsync(id, result, cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteResponseAsync(id, new
            {
                content = new[] { new { type = "text", text = $"Error: {ex.Message}" } },
                isError = true
            }, cancellationToken);
        }
    }

    private object ExecuteDiagnostics(JsonElement arguments)
    {
        var text = arguments.GetProperty("text").GetString() ?? string.Empty;
        var uri = arguments.GetProperty("uri").GetString() ?? "mcp://input";
        var diagnostics = _features.GetDiagnostics(text, uri);
        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(diagnostics, JsonOptions) } }
        };
    }

    private object ExecuteCompletions(JsonElement arguments)
    {
        var text = arguments.GetProperty("text").GetString() ?? string.Empty;
        var uri = arguments.GetProperty("uri").GetString() ?? "mcp://input";
        var line = arguments.GetProperty("line").GetInt32();
        var character = arguments.GetProperty("character").GetInt32();
        var items = _features.GetCompletionItems(text, new LspPosition(line, character), uri);
        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(items, JsonOptions) } }
        };
    }

    private object ExecuteHover(JsonElement arguments)
    {
        var text = arguments.GetProperty("text").GetString() ?? string.Empty;
        var uri = arguments.GetProperty("uri").GetString() ?? "mcp://input";
        var line = arguments.GetProperty("line").GetInt32();
        var character = arguments.GetProperty("character").GetInt32();
        var hover = _features.GetHover(text, uri, new LspPosition(line, character));

        if (hover is null)
        {
            return new { content = new[] { new { type = "text", text = "null" } } };
        }

        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(hover, JsonOptions) } }
        };
    }

    private object ExecuteSignatureHelp(JsonElement arguments)
    {
        var text = arguments.GetProperty("text").GetString() ?? string.Empty;
        var uri = arguments.GetProperty("uri").GetString() ?? "mcp://input";
        var line = arguments.GetProperty("line").GetInt32();
        var character = arguments.GetProperty("character").GetInt32();
        var help = _features.GetSignatureHelp(text, uri, new LspPosition(line, character));

        if (help is null)
        {
            return new { content = new[] { new { type = "text", text = "null" } } };
        }

        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(help, JsonOptions) } }
        };
    }

    private object ExecuteDefinitions(JsonElement arguments)
    {
        var text = arguments.GetProperty("text").GetString() ?? string.Empty;
        var uri = arguments.GetProperty("uri").GetString() ?? "mcp://input";
        var line = arguments.GetProperty("line").GetInt32();
        var character = arguments.GetProperty("character").GetInt32();
        var definitions = _features.GetDefinitions(text, uri, new LspPosition(line, character));
        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(definitions, JsonOptions) } }
        };
    }

    private object ExecuteDocumentSymbols(JsonElement arguments)
    {
        var text = arguments.GetProperty("text").GetString() ?? string.Empty;
        var uri = arguments.GetProperty("uri").GetString() ?? "mcp://input";
        var symbols = _features.GetDocumentSymbols(text, uri);
        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(symbols, JsonOptions) } }
        };
    }

    private static object[] GetToolDefinitions() =>
    [
        new
        {
            name = "lsp_diagnostics",
            description = "Get parser diagnostics (errors and warnings) for a ToSh script.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    text = new { type = "string", description = "The full text content of the ToSh script to analyze." },
                    uri = new { type = "string", description = "The document URI (used as source name for diagnostics)." }
                },
                required = new[] { "text", "uri" }
            }
        },
        new
        {
            name = "lsp_completions",
            description = "Get completion items at a specific position in a ToSh script. Returns variables, keywords, built-in commands, functions, CLR types, members, and command flags.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    text = new { type = "string", description = "The full text content of the ToSh script." },
                    uri = new { type = "string", description = "The document URI." },
                    line = new { type = "integer", description = "Zero-based line number of the cursor position." },
                    character = new { type = "integer", description = "Zero-based character offset within the line." }
                },
                required = new[] { "text", "uri", "line", "character" }
            }
        },
        new
        {
            name = "lsp_hover",
            description = "Get hover information for the symbol at a specific position in a ToSh script. Returns markdown descriptions for keywords, variables, built-in commands, CLR types, functions, and classes.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    text = new { type = "string", description = "The full text content of the ToSh script." },
                    uri = new { type = "string", description = "The document URI." },
                    line = new { type = "integer", description = "Zero-based line number of the cursor position." },
                    character = new { type = "integer", description = "Zero-based character offset within the line." }
                },
                required = new[] { "text", "uri", "line", "character" }
            }
        },
        new
        {
            name = "lsp_signature_help",
            description = "Get signature help for a function or command call at a specific position. Returns parameter information, active parameter index, and overloads.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    text = new { type = "string", description = "The full text content of the ToSh script." },
                    uri = new { type = "string", description = "The document URI." },
                    line = new { type = "integer", description = "Zero-based line number of the cursor position." },
                    character = new { type = "integer", description = "Zero-based character offset within the line." }
                },
                required = new[] { "text", "uri", "line", "character" }
            }
        },
        new
        {
            name = "lsp_definitions",
            description = "Get go-to-definition locations for the symbol at a specific position. Resolves variables, functions (including all overloads), and class declarations.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    text = new { type = "string", description = "The full text content of the ToSh script." },
                    uri = new { type = "string", description = "The document URI." },
                    line = new { type = "integer", description = "Zero-based line number of the cursor position." },
                    character = new { type = "integer", description = "Zero-based character offset within the line." }
                },
                required = new[] { "text", "uri", "line", "character" }
            }
        },
        new
        {
            name = "lsp_document_symbols",
            description = "Get all symbols (functions, variables, classes) declared in a ToSh script. Returns hierarchical document symbols with name, kind, and range.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    text = new { type = "string", description = "The full text content of the ToSh script." },
                    uri = new { type = "string", description = "The document URI." }
                },
                required = new[] { "text", "uri" }
            }
        }
    ];

    // --- JSON-RPC Transport ---

    private async Task WriteResponseAsync(JsonElement id, object? result, CancellationToken cancellationToken)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText())
        };

        if (result is not null)
        {
            response["result"] = JsonSerializer.SerializeToNode(result, JsonOptions);
        }
        else
        {
            response["result"] = null;
        }

        await WriteMessageAsync(response.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }), cancellationToken);
    }

    private async Task WriteErrorAsync(JsonElement id, int code, string message, CancellationToken cancellationToken)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };

        await WriteMessageAsync(response.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }), cancellationToken);
    }

    private async Task WriteMessageAsync(string json, CancellationToken cancellationToken)
    {
        var line = Encoding.UTF8.GetBytes(json + "\n");

        await _writeLock.WaitAsync(cancellationToken);

        try
        {
            await _output.WriteAsync(line, cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<string?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        while (true)
        {
            var buffer = new byte[1];
            var read = await _input.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                return sb.Length > 0 ? sb.ToString() : null;
            }

            var character = (char)buffer[0];

            if (character == '\n')
            {
                var line = sb.ToString().Trim();

                if (line.Length > 0)
                {
                    return line;
                }

                sb.Clear();
                continue;
            }

            sb.Append(character);
        }
    }
}
