using System.Text;
using System.Text.Json;
using Tosh.Client;
using Tosh.Dap;
using Tosh.Lsp;
using Tosh.Mcp;
using Tosh.Stdlib.Tssp;

namespace Tosh.Tests;

/// <summary>
/// The protocol surfaces start and answer — <c>TS-P2-42</c>.
/// </summary>
/// <remarks>
/// <para>
/// A July 30 survey found the LSP server, the MCP server, the DAP server, and the TSSP client
/// library — about 2,500 lines between them — with **no test referencing them at all**. They are also the
/// surfaces where breakage is quietest: nothing in the suite notices when a server stops
/// answering `initialize`, because nothing in the suite ever asks it to.
/// </para>
/// <para>
/// The DAP server was the sharpest case: its `.csproj` was in no solution, so an ordinary
/// build had not compiled it since April — while the VS Code extension contributes a `tosh`
/// debugger that builds it on demand and launches it. A user pressing F5 was the only thing
/// checking whether it still compiled. It did, but that was luck rather than a guarantee.
/// </para>
/// <para>
/// These are deliberately smoke tests and not protocol conformance suites. Each asserts that
/// the thing starts, parses a real request, and returns a well-formed response — enough that a
/// breakage is loud, cheap enough that it stays green. Conformance, if it is ever wanted,
/// belongs in its own file per surface.
/// </para>
/// <para>
/// All three run **in-process** over <see cref="MemoryStream"/>, because each server takes its
/// input and output streams as constructor arguments. No process spawning, so these cost
/// milliseconds and cannot leave strays behind. Each server's loop ends at end-of-stream, so
/// a MemoryStream primed with requests runs to completion on its own.
/// </para>
/// </remarks>
public sealed class ProtocolSmokeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    // ── LSP: Content-Length framing ────────────────────────────────────────────

    private static byte[] LspFrame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        return [.. header, .. payload];
    }

    [Fact]
    public async Task The_language_server_answers_initialize()
    {
        var input = new MemoryStream([
            .. LspFrame("""
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}
                """),
            .. LspFrame("""{"jsonrpc":"2.0","id":2,"method":"shutdown"}"""),
        ]);

        var output = new MemoryStream();
        using var cts = new CancellationTokenSource(Budget);

        await new ToshLanguageServer(input, output).RunAsync(cts.Token);

        var response = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("Content-Length:", response, StringComparison.Ordinal);
        // The point of `initialize` is the capability advertisement: an editor that gets a
        // response without it silently loses every feature rather than failing.
        Assert.Contains("capabilities", response, StringComparison.Ordinal);
        Assert.Contains("\"id\":1", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_language_server_answers_a_document_request()
    {
        // `initialize` alone would pass even if every feature were unwired, since the
        // capability list is a constant. This drives one real request through the analysis
        // backend and asserts a result comes back for it.
        var input = new MemoryStream([
            .. LspFrame("""
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}
                """),
            .. LspFrame("""
                {"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{
                "uri":"file:///smoke.tosh","languageId":"tosh","version":1,
                "text":"func greet(name) { return $name }\n"}}}
                """),
            .. LspFrame("""
                {"jsonrpc":"2.0","id":2,"method":"textDocument/documentSymbol","params":{
                "textDocument":{"uri":"file:///smoke.tosh"}}}
                """),
        ]);

        var output = new MemoryStream();
        using var cts = new CancellationTokenSource(Budget);

        await new ToshLanguageServer(input, output).RunAsync(cts.Token);

        var response = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("\"id\":2", response, StringComparison.Ordinal);
        Assert.Contains("greet", response, StringComparison.Ordinal);
    }

    // ── DAP: Content-Length framing, same as LSP ──────────────────────────────

    [Fact]
    public async Task The_debug_adapter_answers_initialize()
    {
        var input = new MemoryStream([
            .. LspFrame("""
                {"seq":1,"type":"request","command":"initialize","arguments":{
                "adapterID":"tosh","clientID":"smoke","linesStartAt1":true}}
                """),
            .. LspFrame("""{"seq":2,"type":"request","command":"threads"}"""),
        ]);

        var output = new MemoryStream();
        using var cts = new CancellationTokenSource(Budget);

        await new ToshDapServer(input, output).RunAsync(cts.Token);

        var response = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("Content-Length:", response, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"response\"", response, StringComparison.Ordinal);
        Assert.Contains("initialize", response, StringComparison.Ordinal);
        // `success:false` on initialize means the editor drops the session immediately.
        Assert.DoesNotContain("\"success\":false", response, StringComparison.Ordinal);
    }

    // ── MCP: newline-delimited JSON ────────────────────────────────────────────

    private static async Task<string> RunMcpAsync(params string[] requests)
    {
        var input = new MemoryStream(
            Encoding.UTF8.GetBytes(string.Join("\n", requests) + "\n"));
        var output = new MemoryStream();
        using var cts = new CancellationTokenSource(Budget);

        await new ToshMcpServer(input, output).RunAsync(cts.Token);

        return Encoding.UTF8.GetString(output.ToArray());
    }

    [Fact]
    public async Task The_mcp_server_answers_initialize_and_ping()
    {
        var response = await RunMcpAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            """{"jsonrpc":"2.0","id":2,"method":"ping","params":{}}""");

        Assert.Contains("\"id\":1", response, StringComparison.Ordinal);
        Assert.Contains("\"id\":2", response, StringComparison.Ordinal);
        Assert.DoesNotContain("\"error\"", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_mcp_server_lists_tools_and_every_entry_is_well_formed()
    {
        var response = await RunMcpAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");

        // Parse rather than substring-match: an agent host rejects the whole listing if one
        // entry lacks a name or an input schema, and a substring assertion would not notice.
        var listing = response
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .Single(document => document.RootElement.TryGetProperty("id", out var id)
                                && id.GetInt32() == 2);

        var tools = listing.RootElement.GetProperty("result").GetProperty("tools");

        Assert.NotEmpty(tools.EnumerateArray());

        foreach (var tool in tools.EnumerateArray())
        {
            Assert.False(
                string.IsNullOrWhiteSpace(tool.GetProperty("name").GetString()),
                "every advertised tool needs a name");
            Assert.True(
                tool.TryGetProperty("inputSchema", out _),
                $"tool '{tool.GetProperty("name").GetString()}' advertises no input schema");
        }
    }

    // ── TSSP: the writer and the parser are two halves of one protocol ─────────

    [Fact]
    public async Task A_tssp_stream_written_by_the_client_parses_in_the_shell()
    {
        // Tosh.Client writes frames; Tosh.Stdlib's TsspParser reads them. They are separate
        // assemblies with no shared code — Tosh.Client deliberately depends on nothing else
        // in the tree — so the wire format is the only thing keeping them in agreement, and
        // nothing was checking it. This is the drift-guard shape applied to a protocol.
        var stream = new MemoryStream();

        using (var writer = new ToshFrameWriter(stream, leaveOpen: true))
        {
            writer.WriteHeader(schema: "smoke", modes: ["table"], renderer: "test");
            writer.WriteRecord(new { Name = "widget", Count = 3 });
            writer.WriteError("something went wrong");
            writer.Flush();
        }

        stream.Position = 0;
        var parser = new TsspParser(stream);

        var header = await parser.TryReadHeaderAsync(CancellationToken.None);

        Assert.NotNull(header);
        Assert.Equal(TsspVersion.Current, header!.Version);
        Assert.Equal("smoke", header.Schema);
        Assert.Equal("test", header.Renderer);
        Assert.Contains("table", header.Modes);

        var frames = new List<TsspFrame>();

        await foreach (var frame in parser.ReadFramesAsync(CancellationToken.None))
        {
            frames.Add(frame);
        }

        Assert.Contains(frames, frame => frame.Kind == "rec");
        Assert.Contains(frames, frame => frame.Kind == "err");

        var record = Encoding.UTF8.GetString(
            frames.First(frame => frame.Kind == "rec").Payload.Span);

        Assert.Contains("widget", record, StringComparison.Ordinal);
    }

    [Fact]
    public void A_frame_written_before_the_header_is_rejected()
    {
        // The writer's own invariant. Asserted because a stream missing its header is the
        // failure a consumer diagnoses worst — it looks like corrupt data rather than a
        // producer bug.
        using var writer = new ToshFrameWriter(new MemoryStream(), leaveOpen: true);

        Assert.Throws<InvalidOperationException>(() => writer.WriteError("no header yet"));
    }
}
