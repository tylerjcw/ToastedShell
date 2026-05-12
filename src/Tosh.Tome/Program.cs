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
        string? workspacePath = null;
        string? workspaceDirectory = null;
        var initialText = string.Empty;

        if (args.Length >= 1)
        {
            var resolved = Path.GetFullPath(args[0]);
            // Directory argument — open it as an ad-hoc single-folder workspace.
            if (Directory.Exists(resolved))
            {
                workspaceDirectory = resolved;
            }
            // .tome file — load as workspace manifest.
            else if (string.Equals(Path.GetExtension(resolved), ".tome", StringComparison.OrdinalIgnoreCase)
                && File.Exists(resolved))
            {
                workspacePath = resolved;
            }
            else
            {
                path = resolved;
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
        }
        else
        {
            // No args: auto-pick a sole .tome manifest in the cwd if one exists.
            // Ambiguous (zero or many) → silent fall-through to an empty buffer.
            try
            {
                var candidates = Directory.GetFiles(Environment.CurrentDirectory, "*.tome",
                    SearchOption.TopDirectoryOnly);
                if (candidates.Length == 1) workspacePath = candidates[0];
            }
            catch { /* unreadable cwd is fine */ }
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("tome: requires an interactive terminal");
            return 1;
        }

        using var terminal = new TerminalDriver();
        var app = new TomeApp(terminal, path, initialText);
        if (workspacePath is not null) app.OpenWorkspaceAtStartup(workspacePath);
        else if (workspaceDirectory is not null) app.OpenDirectoryAsWorkspace(workspaceDirectory);
        app.Run();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("tome — Tōme, the TōSh terminal editor");
        Console.WriteLine();
        Console.WriteLine("usage: tome [file|workspace.tome|directory]");
        Console.WriteLine();
        Console.WriteLine("  A .tome file is loaded as a workspace (folders + restored tabs).");
        Console.WriteLine("  A directory is opened as a single-folder workspace.");
        Console.WriteLine("  With no args, a sole *.tome manifest in the cwd is auto-loaded.");
        Console.WriteLine("  Anything else is opened as a plain text buffer.");
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
