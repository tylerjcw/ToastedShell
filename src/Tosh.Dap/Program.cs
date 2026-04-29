using Tosh.Dap;

var server = new ToshDapServer(Console.OpenStandardInput(), Console.OpenStandardOutput());
await server.RunAsync();
