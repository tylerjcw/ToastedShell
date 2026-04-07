using Tosh.Mcp;

var useStdio = args.Any(argument => string.Equals(argument, "--stdio", StringComparison.OrdinalIgnoreCase));

if (!useStdio)
{
    await Console.Error.WriteLineAsync("ToSh MCP Server expects '--stdio'.");
    return;
}

Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = System.Text.Encoding.UTF8;

var server = new ToshMcpServer(Console.OpenStandardInput(), Console.OpenStandardOutput());
await server.RunAsync();
