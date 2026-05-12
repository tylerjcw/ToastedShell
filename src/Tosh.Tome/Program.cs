namespace Tosh.Tome;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            PrintHelp();
            return 0;
        }

        if (args.Any(a => a is "-v" or "--version"))
        {
            Console.WriteLine("tome 0.1.0");
            return 0;
        }

        string? path = null;
        var initialText = string.Empty;

        if (args.Length >= 1)
        {
            path = Path.GetFullPath(args[0]);
            if (File.Exists(path))
            {
                try
                {
                    initialText = File.ReadAllText(path);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"tome: cannot read {path}: {ex.Message}");
                    return 1;
                }
            }
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("tome: requires an interactive terminal");
            return 1;
        }

        using var terminal = new TerminalDriver();
        var app = new TomeApp(terminal, path, initialText);
        app.Run();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("tome — Tōme, the TōSh terminal editor");
        Console.WriteLine();
        Console.WriteLine("usage: tome [file]");
        Console.WriteLine();
        Console.WriteLine("keys:");
        Console.WriteLine("  Ctrl+S    save");
        Console.WriteLine("  Ctrl+Q    quit");
        Console.WriteLine("  Ctrl+Z    undo");
        Console.WriteLine("  Ctrl+Y    redo");
        Console.WriteLine("  Ctrl+A    line start");
        Console.WriteLine("  Ctrl+E    line end");
        Console.WriteLine("  arrows    navigate");
        Console.WriteLine("  PgUp/PgDn page");
    }
}
