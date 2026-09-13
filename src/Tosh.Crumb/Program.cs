using Tosh.Crumb.Commands;

namespace Tosh.Crumb;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }
        if (args[0] is "-V" or "--version" or "version")
        {
            Console.WriteLine("crumb 0.1.0");
            return 0;
        }

        // crumb leans on $HOME for the AUR clone cache, devel tracker,
        // and review history. Validate up-front so a missing $HOME
        // surfaces as a one-line error instead of a stack trace from
        // deep inside an AurBuilder property getter.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOME"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_CACHE_HOME")))
        {
            Console.Error.WriteLine("crumb: $HOME is not set (and no XDG_CACHE_HOME fallback)");
            Console.Error.WriteLine("       crumb needs a home directory for its build/devel cache.");
            return 2;
        }

        string subcommand;
        string[] rest;

        try
        {
            var expansion = PacmanFlags.TryExpand(args[0]);
            if (expansion is not null)
            {
                subcommand = expansion.Subcommand;
                rest = expansion.InjectedFlags.Concat(args.Skip(1)).ToArray();
            }
            else
            {
                subcommand = args[0];
                rest = args.Skip(1).ToArray();
            }

            var opt = CrumbOptions.Parse(rest);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            return subcommand switch
            {
                "search" or "s" => await CrumbCommands.SearchAsync(opt, cts.Token),
                "info" or "show" or "i" => await CrumbCommands.InfoAsync(opt, cts.Token),
                "list" or "ls" or "l" => CrumbCommands.List(opt),
                "files" or "fl" => CrumbCommands.Files(opt),
                "owns" or "own" or "o" => CrumbCommands.Owns(opt),
                "install" or "in" or "add" => await CrumbCommands.InstallAsync(opt, cts.Token),
                "install-file" or "file-install" or "local-install" => await CrumbCommands.InstallFileAsync(opt, cts.Token),
                "remove" or "rm" or "uninstall" => await CrumbCommands.RemoveAsync(opt, cts.Token),
                "sync" or "sy" or "refresh" => await CrumbCommands.SyncAsync(opt, cts.Token),
                "update" or "up" or "upgrade" => await CrumbCommands.UpdateAsync(opt, cts.Token),
                "clean" or "purge" => await CrumbCommands.CleanAsync(opt, cts.Token),
                "logs" or "log" => await CrumbCommands.LogsAsync(opt, cts.Token),
                "gendb" => await CrumbCommands.GenDbAsync(opt, cts.Token),
                "news" => await NewsCommand.RunAsync(opt, cts.Token),
                _ => UnknownCommand(subcommand),
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"crumb: {ex.Message}");
            return 2;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }

    private static int UnknownCommand(string name)
    {
        Console.Error.WriteLine($"crumb: unknown subcommand '{name}'");
        Console.Error.WriteLine("       run `crumb help` for the list.");
        return 2;
    }

    /// <summary>
    /// Crumb's help, drawn by the shell's own renderer from a <see cref="Tosh.Runtime.HelpTopic"/>.
    /// </summary>
    /// <remarks>
    /// This was forty hand-aligned `Console.WriteLine` calls — a second help layout to
    /// keep in step, which looked like a different program from everything else in the
    /// shell. The description lives in <see cref="Output.CrumbHelp"/> as data now, so
    /// the drawing is not Crumb's job and anything else that wants the description can
    /// have it.
    /// </remarks>
    private static void PrintHelp()
        => Console.WriteLine(Tosh.Runtime.HelpTopicSummaryRenderer.Render(Output.CrumbHelp.Topic()));
}
