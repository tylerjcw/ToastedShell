using Tosh.Stdlib.Clr;
using Tosh.Stdlib.Concurrency;
using Tosh.Stdlib.Data;
using Tosh.Stdlib.Display;
using Tosh.Stdlib.Filesystem;
using Tosh.Stdlib.Functional;
using Tosh.Stdlib.Maths;
using Tosh.Stdlib.Net;
using Tosh.Stdlib.Pipeline;
using Tosh.Stdlib.Processes;
using Tosh.Stdlib.Scripting;
using Tosh.Stdlib.Shell;
using Tosh.Stdlib.Sys;
using Tosh.Stdlib.Text;
using Tosh.Stdlib.Time;
using Tosh.Runtime.Formats;

using Tosh.Runtime;

namespace Tosh.Stdlib;

public static class BuiltInCommands
{
    /// <summary>
    /// Registers every built-in command: the language's, then the shell's — <c>TOAST-0007</c>.
    /// </summary>
    /// <remarks>
    /// The composition of the two registrars below, in the order the shell has always used, so
    /// that an existing host sees no change. The split is what is new; this is the compatibility
    /// surface over it.
    /// </remarks>
    public static void RegisterDefaults(ShellCommandRegistry commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var formats = CreateDefaultFormats();
        RegisterLanguageDefaults(commands, formats);
        RegisterShellDefaults(commands, formats);
    }

    /// <inheritdoc cref="LanguageCommands.CreateDefaultFormats"/>
    public static DataFormatRegistry CreateDefaultFormats() => LanguageCommands.CreateDefaultFormats();

    /// <inheritdoc cref="LanguageCommands.RegisterDefaults"/>
    /// <remarks>
    /// `TOAST-0007` moved the registrations themselves into `Toast.Stdlib`; this spelling is
    /// kept so that existing hosts and tests do not have to move with them.
    /// </remarks>
    public static void RegisterLanguageDefaults(ShellCommandRegistry commands, DataFormatRegistry formats)
        => LanguageCommands.RegisterDefaults(commands, formats);

    /// <summary>
    /// The commands that are part of the shell — <c>TOAST-0007</c>.
    /// </summary>
    /// <remarks>
    /// Everything that needs a TōSh session: the REPL surface, job control, service control,
    /// the filesystem verbs that resolve against the shell's working directory, and the
    /// network. Registering these into a bare <c>ToastRuntime</c> is legal — they simply fail
    /// when run, naming the capability the host does not provide.
    /// </remarks>
    public static void RegisterShellDefaults(ShellCommandRegistry commands, DataFormatRegistry formats)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(formats);

        // ── Shell (REPL meta, prompts, history, help) ──
        commands.Register(new HelpCommand());
        commands.Register(new AproposCommand());
        commands.Register(new ExitCommand());
        commands.RegisterAlias("logout", "exit");
        commands.Register(new ExecCommand());
        commands.Register(new EditCommand());
        commands.Register(new UmaskCommand());
        commands.Register(new UlimitCommand());
        commands.Register(new ClearCommand());
        commands.Register(new HistoryCommand());
        commands.Register(new HistorySearchCommand());
        commands.Register(new ConfigCommand());
        commands.Register(new ViewCommand());
        commands.Register(new BackCommand());
        commands.Register(new ForwardCommand());
        commands.Register(new DirsCommand());
        commands.Register(new EventsCommand());
        commands.Register(new WhichCommand());
        commands.RegisterAlias("whence", "which");
        commands.Register(new HushCommand());
        commands.Register(new ReadLineCommand());
        commands.Register(new TuiCommand());
        commands.Register(new PromptCommand());
        commands.Register(new PromptTimeCommand());
        commands.Register(new PromptDirCommand());
        commands.Register(new PromptGitCommand());
        commands.Register(new PromptUserHostCommand());
        commands.Register(new PromptHistoryCommand());
        commands.Register(new PromptJobsCommand());
        commands.Register(new PromptDurationCommand());
        commands.Register(new PromptExitCodeCommand());
        commands.Register(new PromptTextCommand());
        commands.Register(new PromptNewlineCommand());

        // ── Sys (system info, env vars, service control) ──
        commands.Register(new UnameCommand());
        commands.Register(new HostnameCommand());
        commands.Register(new WhoAmICommand());
        commands.Register(new IdCommand());
        commands.Register(new FreeCommand());
        commands.Register(new UptimeCommand());
        commands.Register(new LscpuCommand());
        commands.Register(new LsipcCommand());
        commands.Register(new SystemctlCommand());
        commands.Register(new JournalctlCommand());
        commands.Register(new LoginctlCommand());
        commands.Register(new HostnamectlCommand());
        commands.Register(new NetworkctlCommand());
        commands.Register(new EnvironmentCommand());
        commands.Register(new VarsCommand());
        commands.Register(new ExportCommand());
        commands.Register(new ForgetCommand());
        commands.RegisterAlias("unset", "forget");
        commands.Register(new SeqCommand());
        commands.Register(new GuidCommand());

        // ── Filesystem (paths, files, directories, IO handles) ──
        commands.Register(new PrintWorkingDirectoryCommand());
        commands.Register(new ChangeDirectoryCommand());
        commands.Register(new ListDirectoryCommand());
        commands.Register(new DfCommand());
        commands.RegisterAlias("mounts", "df");
        commands.Register(new DuCommand());
        commands.RegisterAlias("usage", "du");
        commands.RegisterAlias("disk-usage", "du");
        commands.Register(new StatCommand());
        commands.Register(new FindmntCommand());
        commands.Register(new FindCommand());
        commands.Register(new GlobCommand());
        commands.Register(new TreeCommand());
        commands.Register(new LsblkCommand());
        commands.Register(new TouchCommand());
        commands.Register(new RemoveItemCommand());
        commands.Register(new CopyItemCommand());
        commands.Register(new MoveItemCommand());
        commands.Register(new ChmodCommand());
        commands.Register(new ChownCommand());
        commands.Register(new LinkCommand());
        commands.Register(new CatCommand());

        // ── Text (line/char manipulation, regex, templating) ──
        commands.Register(new WordCountCommand());

        // ── Pipeline (collection ops, aggregation, projection) ──
        commands.Register(new InspectCommand());
        // Aggregation

        // ── Concurrency (spawn, channels, async) ──
        commands.Register(new SpawnCommand());
        commands.Register(new ScopeCommand());

        // ── Processes (jobs, signals, process listing) ──
        commands.Register(new ProcessListCommand());
        commands.Register(new JobsCommand());
        commands.Register(new WaitForCommand());
        commands.Register(new KillCommand());
        commands.Register(new SignalCommand());
        commands.Register(new ForegroundCommand());
        commands.Register(new BackgroundResumeCommand());
        commands.Register(new LsfdCommand());

        // ── Net (HTTP, ICMP, IP) ──
        commands.Register(new PingCommand());
        commands.Register(new HttpCommand(formats));
        commands.Register(new IpCommand());

        // ── Scripting ──
        //
        // `source`, `eval`, `debug` and `format` joined this list in `TOAST-0006`. All four
        // reach the shell host, and `source` and `eval` live in `Shell/` — so the Scripting
        // section splits like the others, with `assert` the only language-level member.
        commands.Register(new SourceCommand());
        commands.Register(new EvalCommand());
        commands.Register(new DebugCommand());
        commands.Register(new FormatCommand());
        commands.Register(new RaiseCommand());
        commands.Register(new UndefCommand());

        // ── Display (TUI styling) ──
        commands.Register(new StyledCommand());
    }
}
