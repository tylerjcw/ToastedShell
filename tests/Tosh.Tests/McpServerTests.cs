using System.Text;
using System.Text.Json;
using Tosh.Mcp;

namespace Tosh.Tests;

public sealed class McpServerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Initialize_returns_protocol_version_and_capabilities()
    {
        var result = await SendRequestAsync(1, "initialize", new { capabilities = new { } });

        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.True(result.TryGetProperty("capabilities", out var capabilities));
        Assert.True(capabilities.TryGetProperty("tools", out _));
        Assert.Equal("tosh-language-services", result.GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Tools_list_returns_ten_tools()
    {
        var result = await SendRequestAsync(1, "tools/list");

        var tools = result.GetProperty("tools");
        Assert.Equal(10, tools.GetArrayLength());

        var names = Enumerable.Range(0, tools.GetArrayLength())
            .Select(i => tools[i].GetProperty("name").GetString())
            .ToArray();

        Assert.Contains("lsp_diagnostics", names);
        Assert.Contains("lsp_completions", names);
        Assert.Contains("lsp_hover", names);
        Assert.Contains("lsp_signature_help", names);
        Assert.Contains("lsp_definitions", names);
        Assert.Contains("lsp_document_symbols", names);
        Assert.Contains("command_metadata", names);
        Assert.Contains("run_snippet", names);
        Assert.Contains("explain_error", names);
        Assert.Contains("operator_metadata", names);
    }

    [Fact]
    public async Task Diagnostics_tool_returns_parser_errors()
    {
        var content = await CallToolAsync("lsp_diagnostics", new { text = "var = 1", uri = "file:///test.tosh" });

        using var doc = JsonDocument.Parse(content);
        var diagnostics = doc.RootElement;
        Assert.True(diagnostics.GetArrayLength() > 0);
        Assert.StartsWith("tosh::parser::", diagnostics[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Completions_tool_returns_builtin_commands()
    {
        var content = await CallToolAsync("lsp_completions", new { text = "ech", uri = "file:///test.tosh", line = 0, character = 3 });

        using var doc = JsonDocument.Parse(content);
        var items = doc.RootElement;
        var labels = Enumerable.Range(0, items.GetArrayLength())
            .Select(i => items[i].GetProperty("label").GetString())
            .ToArray();

        Assert.Contains("echo", labels);
    }

    [Fact]
    public async Task Hover_tool_returns_command_description()
    {
        var content = await CallToolAsync("lsp_hover", new { text = "echo hello", uri = "file:///test.tosh", line = 0, character = 2 });

        using var doc = JsonDocument.Parse(content);
        var hover = doc.RootElement;
        var markdown = hover.GetProperty("contents").GetProperty("value").GetString()!;

        Assert.Contains("echo", markdown);
        Assert.Contains("pipeline objects", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hover_tool_returns_null_for_unknown_symbol()
    {
        var content = await CallToolAsync("lsp_hover", new { text = "xyznotacommand", uri = "file:///test.tosh", line = 0, character = 5 });

        Assert.Equal("null", content);
    }

    [Fact]
    public async Task Signature_help_tool_returns_parameters()
    {
        var content = await CallToolAsync("lsp_signature_help", new { text = "echo hello", uri = "file:///test.tosh", line = 0, character = 8 });

        using var doc = JsonDocument.Parse(content);
        var help = doc.RootElement;
        Assert.True(help.GetProperty("signatures").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Definitions_tool_returns_locations()
    {
        const string script = "func greet(name) {\n  echo $name\n}\ngreet toast";
        var content = await CallToolAsync("lsp_definitions", new { text = script, uri = "file:///test.tosh", line = 3, character = 2 });

        using var doc = JsonDocument.Parse(content);
        var definitions = doc.RootElement;
        Assert.True(definitions.GetArrayLength() > 0);
        Assert.Equal("file:///test.tosh", definitions[0].GetProperty("uri").GetString());
    }

    [Fact]
    public async Task Document_symbols_tool_returns_declarations()
    {
        const string script = "var name = \"toast\"\nfunc greet() { echo $name }";
        var content = await CallToolAsync("lsp_document_symbols", new { text = script, uri = "file:///test.tosh" });

        using var doc = JsonDocument.Parse(content);
        var symbols = doc.RootElement;
        var names = Enumerable.Range(0, symbols.GetArrayLength())
            .Select(i => symbols[i].GetProperty("name").GetString())
            .ToArray();

        Assert.Contains("name", names);
        Assert.Contains("greet", names);
    }

    [Fact]
    public async Task Unknown_tool_returns_error()
    {
        var result = await SendRequestAsync(1, "tools/call", new { name = "nonexistent_tool", arguments = new { } });

        var content = result.GetProperty("content");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("Unknown tool", content[0].GetProperty("text").GetString());
    }

    // --- run_snippet tests ---

    [Fact]
    public async Task RunSnippet_returns_pipeline_results()
    {
        var content = await CallToolAsync("run_snippet", new { code = "1 + 2" });

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var results = root.GetProperty("results");
        Assert.True(results.GetArrayLength() > 0);
        Assert.Equal("3", results[0].GetString());
    }

    [Fact]
    public async Task RunSnippet_captures_stdout()
    {
        var content = await CallToolAsync("run_snippet", new { code = "writeline \"hello mcp\"" });

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var stdout = root.GetProperty("stdout").GetString()!;
        Assert.Contains("hello mcp", stdout);
    }

    [Fact]
    public async Task RunSnippet_reports_parse_errors_in_stderr()
    {
        var content = await CallToolAsync("run_snippet", new { code = "var = oops" });

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var stderr = root.GetProperty("stderr").GetString()!;
        Assert.Contains("tosh::parser::", stderr);
    }

    [Fact]
    public async Task RunSnippet_respects_timeout()
    {
        // Use a function with an infinite loop — valid syntax that will run forever
        var content = await CallToolAsync("run_snippet", new { code = "func loop() {\n  var i = 0\n  while ($i >= 0) {\n    $i = $i + 1\n  }\n}\nloop", timeout_ms = 500 });

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var stderr = root.GetProperty("stderr").GetString()!;
        Assert.Contains("timed out", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunSnippet_returns_multiple_results()
    {
        var content = await CallToolAsync("run_snippet", new { code = "echo 1\necho 2\necho 3" });

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var results = root.GetProperty("results");
        Assert.Equal(3, results.GetArrayLength());
    }

    // --- explain_error tests ---

    [Fact]
    public async Task ExplainError_returns_parse_error_explanation()
    {
        var content = await CallToolAsync("explain_error", new { text = "var = 1" });

        using var doc = JsonDocument.Parse(content);
        var explanations = doc.RootElement;
        Assert.True(explanations.GetArrayLength() > 0);

        var first = explanations[0];
        Assert.StartsWith("tosh::parser::", first.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("explanation").GetString()));
        Assert.True(first.GetProperty("line").ValueKind == JsonValueKind.Number);
    }

    [Fact]
    public async Task ExplainError_returns_no_errors_for_valid_code()
    {
        var content = await CallToolAsync("explain_error", new { text = "var x = 42" });

        using var doc = JsonDocument.Parse(content);
        var explanations = doc.RootElement;
        Assert.Equal(1, explanations.GetArrayLength());
        Assert.Equal("none", explanations[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExplainError_explains_user_provided_error()
    {
        var content = await CallToolAsync("explain_error", new { text = "echo hello", error = "command not found: foo" });

        using var doc = JsonDocument.Parse(content);
        var explanations = doc.RootElement;
        // Valid code + user error string should explain the provided error
        var hasUserProvided = false;
        for (int i = 0; i < explanations.GetArrayLength(); i++)
        {
            if (explanations[i].GetProperty("code").GetString() == "user-provided")
            {
                hasUserProvided = true;
                Assert.Contains("not found", explanations[i].GetProperty("explanation").GetString(), StringComparison.OrdinalIgnoreCase);
            }
        }
        Assert.True(hasUserProvided);
    }

    [Fact]
    public async Task ExplainError_provides_context_line_for_parse_error()
    {
        var content = await CallToolAsync("explain_error", new { text = "var x = 1\nvar = bad\nvar y = 2" });

        using var doc = JsonDocument.Parse(content);
        var explanations = doc.RootElement;
        Assert.True(explanations.GetArrayLength() > 0);

        var first = explanations[0];
        Assert.True(first.GetProperty("context").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task Ping_returns_empty_result()
    {
        var result = await SendRequestAsync(1, "ping");

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }

    // --- Test Helpers ---

    private static async Task<string> CallToolAsync(string toolName, object arguments)
    {
        var result = await SendRequestAsync(1, "tools/call", new { name = toolName, arguments });

        var content = result.GetProperty("content");
        return content[0].GetProperty("text").GetString()!;
    }

    private static async Task<JsonElement> SendRequestAsync(int id, string method, object? parameters = null)
    {
        var input = new MemoryStream();
        var output = new MemoryStream();

        WriteMessage(input, new { jsonrpc = "2.0", id, method, @params = parameters ?? new { } });
        input.Position = 0;

        var server = new ToshMcpServer(input, output);
        await server.RunAsync();

        output.Position = 0;
        return ReadFirstResult(output);
    }

    private static void WriteMessage(Stream stream, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var line = Encoding.UTF8.GetBytes(json + "\n");
        stream.Write(line);
    }

    private static JsonElement ReadFirstResult(Stream stream)
    {
        var raw = Encoding.UTF8.GetString(((MemoryStream)stream).ToArray());
        var line = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("result").Clone();
    }
}
