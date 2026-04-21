using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tosh.Core;
using Tosh.Language;
using Tosh.LanguageServices;

namespace Tosh.Mcp;

public sealed class ToshMcpServer
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ToshLanguageFeatures _features = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly TimeSpan DefaultSnippetTimeout = TimeSpan.FromSeconds(10);

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
            object result = toolName switch
            {
                "lsp_diagnostics" => ExecuteDiagnostics(arguments),
                "lsp_completions" => ExecuteCompletions(arguments),
                "lsp_hover" => ExecuteHover(arguments),
                "lsp_signature_help" => ExecuteSignatureHelp(arguments),
                "lsp_definitions" => ExecuteDefinitions(arguments),
                "lsp_document_symbols" => ExecuteDocumentSymbols(arguments),
                "command_metadata" => ExecuteCommandMetadata(arguments),
                "operator_metadata" => ExecuteOperatorMetadata(),
                "run_snippet" => await ExecuteRunSnippetAsync(arguments, cancellationToken),
                "explain_error" => ExecuteExplainError(arguments),
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

    private object ExecuteCommandMetadata(JsonElement arguments)
    {
        var nameFilter = arguments.ValueKind != JsonValueKind.Undefined &&
                         arguments.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : null;

        var categoryFilter = arguments.ValueKind != JsonValueKind.Undefined &&
                             arguments.TryGetProperty("category", out var catElement)
            ? catElement.GetString()
            : null;

        var allMetadata = _features.GetAllCommandMetadata();

        IEnumerable<CommandMetadata> filtered = allMetadata;

        if (nameFilter is not null)
        {
            filtered = filtered.Where(m => string.Equals(m.Name, nameFilter, StringComparison.OrdinalIgnoreCase)
                || m.Aliases.Contains(nameFilter, StringComparer.OrdinalIgnoreCase));
        }

        if (categoryFilter is not null)
        {
            filtered = filtered.Where(m => string.Equals(m.Category, categoryFilter, StringComparison.OrdinalIgnoreCase));
        }

        var result = filtered.ToList();
        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, JsonOptions) } }
        };
    }

    private static object ExecuteOperatorMetadata()
    {
        var operators = new object[]
        {
            // Unary
            new { kind = "unary", name = "not", description = "Logical negation. Returns `true` if the operand is falsy.", example = "not true" },

            // Arithmetic
            new { kind = "binary", name = "+", description = "Addition. Also concatenates strings and appends arrays.", example = "1 + 2" },
            new { kind = "binary", name = "-", description = "Subtraction.", example = "5 - 3" },
            new { kind = "binary", name = "*", description = "Multiplication. Also repeats strings: `\"ha\" * 3` → `\"hahaha\"`.", example = "3 * 4" },
            new { kind = "binary", name = "/", description = "Division.", example = "10 / 2" },
            new { kind = "binary", name = "%", description = "Modulo (remainder).", example = "7 % 3" },
            new { kind = "binary", name = "**", description = "Exponentiation.", example = "2 ** 8" },

            // Equality / regex
            new { kind = "binary", name = "==", description = "Equality. Case-insensitive for strings.", example = "\"Hello\" == \"hello\"" },
            new { kind = "binary", name = "!=", description = "Inequality.", example = "1 != 2" },
            new { kind = "binary", name = "=~", description = "Regex match. Returns `true` if the left string matches the regex pattern on the right.", example = "\"hello\" =~ \"^h\"" },
            new { kind = "binary", name = "!~", description = "Negated regex match.", example = "\"world\" !~ \"^h\"" },

            // Comparison
            new { kind = "binary", name = ">",  description = "Greater-than comparison.", example = "5 > 3" },
            new { kind = "binary", name = ">=", description = "Greater-than-or-equal comparison.", example = "5 >= 5" },
            new { kind = "binary", name = "<",  description = "Less-than comparison.", example = "3 < 5" },
            new { kind = "binary", name = "<=", description = "Less-than-or-equal comparison.", example = "3 <= 3" },

            // Membership
            new { kind = "binary", name = "in",       description = "Membership operator. Returns `true` if the value is found in the collection or substring is found in string.", example = "3 in [1,2,3]" },
            new { kind = "binary", name = "not-in",   description = "Negated membership operator.", example = "4 not-in [1,2,3]" },
            new { kind = "binary", name = "is in",    description = "Membership form of `is`. Write as two words. Equivalent to `in`.", example = "3 is in [1,2,3]" },
            new { kind = "binary", name = "is not in", description = "Negated membership form of `is not`. Write as three words.", example = "4 is not in [1,2,3]" },

            // String operators
            new { kind = "binary", name = "contains",    description = "Returns `true` if the string contains the substring, or if the collection contains the value.", example = "\"hello\" contains \"ell\"" },
            new { kind = "binary", name = "starts-with", description = "Returns `true` if the string starts with the given prefix.", example = "\"hello\" starts-with \"he\"" },
            new { kind = "binary", name = "ends-with",   description = "Returns `true` if the string ends with the given suffix.", example = "\"hello\" ends-with \"lo\"" },

            // Type operators
            new { kind = "binary", name = "is",     description = "Type-check operator. Returns `true` if the value matches the named type. Use `is not` (two words) or `is-not` as the negated form.", example = "5 is int" },
            new { kind = "binary", name = "is-not", description = "Negated type-check. The two-word form `is not` is also accepted.", example = "5 is-not string" },
            new { kind = "binary", name = "as",     description = "Type-cast operator. Converts the value to the named type. Also used as an import alias keyword in `using` / `require` / `bind`.", example = "5 as float" },

            // Logical
            new { kind = "binary", name = "and", description = "Short-circuit logical AND.", example = "true and false" },
            new { kind = "binary", name = "or",  description = "Short-circuit logical OR.",  example = "false or true" },
        };

        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(operators, JsonOptions) } }
        };
    }

    private async Task<object> ExecuteRunSnippetAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var code = arguments.GetProperty("code").GetString() ?? string.Empty;

        var timeoutMs = arguments.ValueKind != JsonValueKind.Undefined &&
                        arguments.TryGetProperty("timeout_ms", out var timeoutElement) &&
                        timeoutElement.ValueKind == JsonValueKind.Number
            ? timeoutElement.GetInt32()
            : (int)DefaultSnippetTimeout.TotalMilliseconds;

        timeoutMs = Math.Clamp(timeoutMs, 100, 30_000);

        var outputWriter = new StringWriter();
        var errorWriter = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(outputWriter, errorWriter);
        var engine = new ToshEngine(runtime);

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var results = new List<object?>();
        try
        {
            await foreach (var value in engine.EvaluateAsync(code, "<mcp-snippet>", linkedCts.Token)
                               .WithCancellation(linkedCts.Token))
            {
                results.Add(value);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            errorWriter.Write("[Execution timed out]");
        }
        catch (ToshDiagnosticException) when (timeoutCts.IsCancellationRequested)
        {
            errorWriter.Write("[Execution timed out]");
        }
        catch (ToshDiagnosticException ex)
        {
            foreach (var diag in ex.Diagnostics)
            {
                errorWriter.Write($"{diag.Code}: {diag.Title}");
                if (diag.Help is not null)
                    errorWriter.Write($" — {diag.Help}");
                errorWriter.WriteLine();
            }
        }

        var stdout = outputWriter.ToString();
        var stderr = errorWriter.ToString();
        var formatted = new List<string>();

        foreach (var item in results)
        {
            formatted.Add(runtime.Formatter.Format(item));
        }

        var response = new
        {
            results = formatted,
            stdout = stdout.Length > 0 ? stdout : null,
            stderr = stderr.Length > 0 ? stderr : null
        };

        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(response, JsonOptions) } }
        };
    }

    private object ExecuteExplainError(JsonElement arguments)
    {
        var text = arguments.GetProperty("text").GetString() ?? string.Empty;

        var errorFilter = arguments.ValueKind != JsonValueKind.Undefined &&
                          arguments.TryGetProperty("error", out var errorElement)
            ? errorElement.GetString()
            : null;

        // Phase 1: collect parse diagnostics via LSP layer
        var parseDiagnostics = _features.GetDiagnostics(text, "<mcp-explain>");

        // Phase 2: if no parse errors, try executing to catch runtime errors
        var runtimeErrors = new List<(string Code, string Message, string? Help)>();
        if (parseDiagnostics.Count == 0)
        {
            try
            {
                var runtime = ToshRuntime.CreateDefault();
                var engine = new ToshEngine(runtime);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                engine.ExecuteToListAsync(text, "<mcp-explain>", cts.Token).GetAwaiter().GetResult();
            }
            catch (ToshDiagnosticException ex)
            {
                foreach (var d in ex.Diagnostics)
                    runtimeErrors.Add((d.Code, d.Title, d.Help));
            }
            catch (OperationCanceledException)
            {
                // Timed out — not an error to explain
            }
            catch (Exception ex)
            {
                runtimeErrors.Add(("tosh::runtime::exception", ex.Message, null));
            }
        }

        var explanations = new List<object>();
        var sourceLines = text.Split('\n');

        // Explain parse diagnostics (LspDiagnostic has line/character positions)
        foreach (var diag in parseDiagnostics)
        {
            var lineNum = diag.Range.Start.Line + 1; // convert 0-based to 1-based
            string? context = lineNum >= 1 && lineNum <= sourceLines.Length
                ? sourceLines[lineNum - 1].Trim()
                : null;

            explanations.Add(new
            {
                code = diag.Code,
                message = diag.Message,
                explanation = ClassifyDiagnosticCode(diag.Code, diag.Message),
                line = (int?)lineNum,
                context,
                suggestion = (string?)null
            });
        }

        // Explain runtime errors
        foreach (var (code, message, help) in runtimeErrors)
        {
            explanations.Add(new
            {
                code,
                message,
                explanation = ClassifyDiagnosticCode(code, message),
                line = (int?)null,
                context = (string?)null,
                suggestion = help
            });
        }

        // If a specific error string was provided but we have no diagnostics, explain it generically
        if (explanations.Count == 0 && errorFilter is not null)
        {
            explanations.Add(new
            {
                code = "user-provided",
                message = errorFilter,
                explanation = ClassifyErrorMessage(errorFilter),
                line = (int?)null,
                context = (string?)null,
                suggestion = (string?)null
            });
        }
        else if (explanations.Count == 0)
        {
            explanations.Add(new
            {
                code = "none",
                message = "No errors found.",
                explanation = "The provided code parses and executes without errors.",
                line = (int?)null,
                context = (string?)null,
                suggestion = (string?)null
            });
        }

        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(explanations, JsonOptions) } }
        };
    }

    private static string ClassifyDiagnosticCode(string code, string title)
    {
        if (code.Contains("unexpected_token"))
            return $"The parser encountered a token it did not expect. {title} Check for missing operators, unmatched braces, or incorrect syntax near this location.";

        if (code.Contains("unknown_symbol") || code.Contains("undefined"))
            return $"An identifier was referenced that has not been declared. {title} Ensure the variable or function is defined before use, and check for typos.";

        if (code.Contains("unterminated_string"))
            return $"A string literal was not properly closed. {title} Add the matching quote character.";

        if (code.Contains("expected_expression"))
            return $"An expression was expected but not found. {title} This often means an operator is missing its right-hand operand.";

        if (code.Contains("expected_identifier"))
            return $"An identifier (variable or function name) was expected. {title} Check that you are using a valid name after 'var', 'func', etc.";

        if (code.Contains("duplicate"))
            return $"A duplicate declaration was detected. {title} Rename one of the conflicting items or remove the duplicate.";

        if (code.Contains("type_mismatch") || code.Contains("invalid_cast"))
            return $"A type error occurred. {title} Verify that the value types are compatible with the operation.";

        if (code.Contains("runtime::exception"))
            return $"A runtime exception was thrown during execution: {title}";

        return title;
    }

    private static string ClassifyErrorMessage(string error)
    {
        if (error.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("undefined", StringComparison.OrdinalIgnoreCase))
            return $"The error indicates something was not found or is undefined: '{error}'. Check that all referenced commands, variables, and files exist.";

        if (error.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("access denied", StringComparison.OrdinalIgnoreCase))
            return $"A permissions error occurred: '{error}'. Verify file/directory permissions and run with appropriate privileges.";

        if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return $"An operation timed out: '{error}'. Check for infinite loops or slow network/IO operations.";

        if (error.Contains("syntax", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("parse", StringComparison.OrdinalIgnoreCase))
            return $"A syntax or parse error: '{error}'. Review the code for syntax mistakes.";

        return $"Error: '{error}'. Please review the code and consult the TōSh documentation for details.";
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
        },
        new
        {
            name = "command_metadata",
            description = "Get structured metadata for TōSh built-in commands. Returns name, description, longDescription, usage, category, aliases, arguments (with kind: expression/bareword/string/path/block), options, examples, canonicalExamples (input/output pairs), pipeline input/output shape, output type and member names, output mode (structured/text/mixed/none), side effects (readsFiles, writesFiles, network, spawnsProcess), tags, seeAlso, permissions, errorConditions, sinceVersion, deprecatedVersion, removedVersion, and isExperimental. Call with no arguments to get all commands, or filter by name or category.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", description = "Filter by command name or alias. Omit to return all commands." },
                    category = new { type = "string", description = "Filter by category (e.g. 'Filesystem', 'Data', 'System'). Omit to return all categories." }
                },
                required = Array.Empty<string>()
            }
        },
        new
        {
            name = "operator_metadata",
            description = "Get documentation for all TōSh infix and unary operators. Returns each operator's kind (unary/binary), name, description, and a usage example. Covers arithmetic (+, -, *, /, %, **), equality (==, !=, =~, !~), comparison (>, >=, <, <=), membership (in, not-in, 'is in', 'is not in'), string operators (contains, starts-with, ends-with), type operators (is, is-not, as), and logical operators (and, or, not). Note: 'is in' and 'is not in' are multi-word forms and must be written with spaces.",
            inputSchema = new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }
        },
        new
        {
            name = "run_snippet",
            description = "Execute a ToSh code snippet and return the results. Runs in an isolated engine instance with a timeout. Returns collected pipeline results, stdout, and stderr.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    code = new { type = "string", description = "The ToSh code to execute." },
                    timeout_ms = new { type = "integer", description = "Maximum execution time in milliseconds (100..30000, default 10000)." }
                },
                required = new[] { "code" }
            }
        },
        new
        {
            name = "explain_error",
            description = "Analyze ToSh code for errors and return structured explanations. Parses the code and, if no parse errors are found, executes it to catch runtime errors. Each explanation includes the error code, message, human-readable explanation, line number, source context, and fix suggestion.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    text = new { type = "string", description = "The ToSh source code to analyze for errors." },
                    error = new { type = "string", description = "An optional error message to explain if no errors are found in the code itself." }
                },
                required = new[] { "text" }
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
