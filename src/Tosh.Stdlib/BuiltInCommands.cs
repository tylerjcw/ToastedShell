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
    public static void RegisterDefaults(ShellCommandRegistry commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var formats = new DataFormatRegistry();
        formats.Register(new JsonDataFormat());
        formats.Register(new DelimitedDataFormat("csv", ','));
        formats.Register(new DelimitedDataFormat("tsv", '\t'));
        formats.Register(new DelimitedDataFormat("delimited", ',', ["delim"]));
        formats.Register(new XmlDataFormat());
        formats.Register(new TomlDataFormat());

        // ── Shell (REPL meta, prompts, history, help) ──
        commands.Register(new HelpCommand());
        commands.Register(new AproposCommand());
        commands.Register(new ExitCommand());
        commands.RegisterAlias("logout", "exit");
        commands.Register(new ExecCommand());
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
        commands.Register(new ReadLinkCommand());
        commands.Register(new RealPathCommand());
        commands.Register(new DirNameCommand());
        commands.Register(new BaseNameCommand());
        commands.Register(new MakeDirectoryCommand());
        commands.Register(new TouchCommand());
        commands.Register(new RemoveItemCommand());
        commands.Register(new CopyItemCommand());
        commands.Register(new MoveItemCommand());
        commands.Register(new ChmodCommand());
        commands.Register(new ChownCommand());
        commands.Register(new LinkCommand());
        commands.Register(new MakeTempDirectoryCommand());
        commands.Register(new TemporaryFileCommand());
        commands.Register(new CatCommand());
        commands.Register(new ReadFileCommand());
        commands.Register(new ReadLinesCommand());
        commands.Register(new WriteFileCommand());
        commands.Register(new AppendFileCommand());
        commands.Register(new ReadBytesCommand());
        commands.Register(new WriteBytesCommand());
        commands.Register(new OpenFileCommand());
        commands.Register(new CloseCommand());
        commands.Register(new ReadFromCommand());
        commands.Register(new ReadLineFromCommand());
        commands.Register(new ReadToEndCommand());
        commands.Register(new WriteToCommand());
        commands.Register(new WriteLineToCommand());
        commands.Register(new FlushCommand());
        commands.Register(new SeekCommand());
        commands.Register(new PositionCommand());
        commands.Register(new LengthCommand());
        commands.Register(new CopyToCommand());
        commands.Register(new PathPredicateCommand("exists", "Checks whether each path exists.", "Exists", path => File.Exists(path) || Directory.Exists(path)));
        commands.Register(new PathPredicateCommand("is-file", "Checks whether each path is a file.", "IsFile", File.Exists));
        commands.Register(new PathPredicateCommand("is-dir", "Checks whether each path is a directory.", "IsDirectory", Directory.Exists));
        commands.Register(new PathPredicateCommand("is-link", "Checks whether each path is a symbolic link.", "IsLink", path => File.Exists(path) ? new FileInfo(path).LinkTarget is not null : Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is not null));

        // ── Text (line/char manipulation, regex, templating) ──
        commands.Register(new EchoCommand());
        commands.Register(new RawCommand());
        commands.Register(new WriteCommand());
        commands.Register(new WriteLineCommand());
        commands.Register(new LinesCommand());
        commands.Register(new HeadCommand());
        commands.Register(new TailCommand());
        commands.Register(new WordCountCommand());
        commands.Register(new UniqueCommand());
        commands.Register(new CutCommand());
        commands.Register(new TranslateCommand());
        commands.Register(new GrepCommand());
        commands.Register(new SplitCommand());
        commands.Register(new JoinLinesCommand());
        commands.Register(new JoinCommand());
        commands.Register(new ReplaceCommand());
        commands.Register(new MatchCommand());
        commands.Register(new TemplateCommand());

        // ── Data (parse/format, hashes, structured types) ──
        commands.Register(new FromCommand(formats));
        commands.Register(new ToCommand(formats));
        commands.Register(new ParseCommand());
        commands.Register(new HashCommand());
        commands.Register(new AsFileCommand());
        commands.Register(new VectorCommand());
        commands.Register(new MatrixCommand());
        commands.Register(new ComplexCommand());

        // ── Pipeline (collection ops, aggregation, projection) ──
        commands.Register(new GetCommand());
        commands.RegisterAlias("select", "get");
        commands.RegisterAlias("pick", "get");
        commands.Register(new RowCommand());
        commands.Register(new RenameCommand());
        commands.Register(new InspectCommand());
        commands.Register(new WhereCommand());
        commands.Register(new EachCommand());
        commands.RegisterAlias("foreach", "each");
        commands.Register(new ParallelCommand());
        commands.Register(new MapCommand());
        commands.Register(new FilterCommand());
        commands.Register(new ReduceCommand());
        commands.Register(new ScanCommand());
        commands.Register(new FlatMapCommand());
        commands.Register(new ZipCommand());
        commands.Register(new QuantifierCommand("any", "Returns true if any pipeline value matches the predicate.", QuantifierCommand.QuantifierKind.Any));
        commands.Register(new QuantifierCommand("all", "Returns true if every pipeline value matches the predicate.", QuantifierCommand.QuantifierKind.All));
        commands.Register(new QuantifierCommand("none", "Returns true if no pipeline values match the predicate.", QuantifierCommand.QuantifierKind.None));
        commands.Register(new FirstCommand());
        commands.Register(new LastCommand());
        commands.Register(new SkipCommand());
        commands.Register(new SortCommand());
        commands.RegisterAlias("sort-by", "sort");
        commands.Register(new ReverseCommand());
        commands.Register(new CountCommand());
        commands.Register(new CollectCommand());
        commands.Register(new FlattenCommand());
        commands.Register(new DistinctCommand());
        commands.Register(new GroupByCommand());
        commands.Register(new TakeWhileCommand());
        commands.Register(new SkipWhileCommand());
        commands.Register(new TakeUntilCommand());
        commands.Register(new SkipUntilCommand());
        commands.Register(new PartitionCommand());
        commands.Register(new FindIndexCommand());
        commands.Register(new TeeCommand());
        commands.Register(new ChunkCommand());
        commands.Register(new WindowCommand());
        commands.Register(new GroupWhileCommand());
        commands.Register(new FrequenciesCommand());
        commands.Register(new TransposeCommand());
        commands.Register(new InterleaveCommand());
        commands.Register(new EnumerateCommand());
        commands.Register(new DedupCommand());
        commands.Register(new IntersperseCommand());
        commands.Register(new StepByCommand());
        commands.Register(new ChainCommand());
        commands.Register(new CartesianProductCommand());
        commands.Register(new CombinationsCommand());
        commands.Register(new PermutationsCommand());
        commands.Register(new XargsCommand());
        commands.Register(new IgnoreCommand());
        // Aggregation
        commands.Register(new SumCommand());
        commands.Register(new AverageCommand());
        commands.RegisterAlias("avg", "average");
        commands.Register(new MinCommand());
        commands.Register(new MaxCommand());
        commands.Register(new MedianCommand());
        commands.Register(new StdevCommand());
        commands.RegisterAlias("stddev", "stdev");
        commands.Register(new VarianceCommand());
        commands.Register(new PercentileCommand());
        commands.Register(new DescribeCommand());
        commands.Register(new SummarizeCommand());
        commands.RegisterAlias("summary", "summarize");

        // ── Functional (combinators, iteration, lambdas) ──
        commands.Register(new InvokeCommand());
        commands.Register(new PartialCommand());
        commands.Register(new CurryCommand());
        commands.Register(new ComposeCommand());
        commands.Register(new UnfoldCommand());
        commands.Register(new IterateCommand());
        commands.Register(new RecurCommand());
        commands.Register(new ConvergeCommand());
        commands.Register(new CycleCommand());
        commands.Register(new RepeatCommand());
        commands.Register(new RepeatedlyCommand());

        // ── Concurrency (spawn, channels, async) ──
        commands.Register(new SpawnCommand());
        commands.Register(new ScopeCommand());
        commands.Register(new RaceCommand());
        commands.Register(new SettleCommand());
        commands.Register(new TimeoutCommand());
        commands.Register(new AsyncCommand());
        commands.Register(new AwaitCommand());
        commands.Register(new ChannelCommand());
        commands.Register(new ChannelSendCommand());
        commands.Register(new ChannelRecvCommand());
        commands.Register(new ChannelCloseCommand());
        commands.Register(new ChannelSelectCommand());

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

        // ── Time (clocks, durations, sleep) ──
        commands.Register(new SleepCommand());
        commands.Register(new TimeCommand());
        commands.Register(new DateCommand());
        commands.Register(new TimeSpanCommand());

        // ── Maths (numeric helpers) ──
        commands.Register(new RoundCommand());

        // ── Clr (reflection, interop, native memory) ──
        // Structured-introspection canonical surface.
        commands.Register(new MembersCommand());
        commands.Register(new MethodsCommand());
        commands.Register(new PropsCommand());
        commands.Register(new FuncsCommand());
        commands.Register(new CloneCommand());
        commands.Register(new TypeOfCommand());
        commands.Register(new DescribeTypeCommand());
        commands.Register(new ConstructorsCommand());
        commands.Register(new TypesCommand());
        commands.Register(new LoadAssemblyCommand());
        commands.Register(new CastCommand());
        commands.Register(new NewObjectCommand());
        // Verb-form commands deprecated 2026-05-10. Replacements:
        //   call / call-method  → $obj.Method($args) or $callable($args)
        //   get-prop / get-props → $obj.Prop, members props
        //   set-prop             → $obj.Prop = value
        //   del-prop             → $obj.Prop = null  (or `forget` for dict keys)
        //   has-prop / has-method → members has Name, methods has Name
        //   get-methods          → methods, or members methods
        commands.Register(new HasPropCommand());
        commands.Register(new HasMethodCommand());
        commands.Register(new GetPropsCommand());
        commands.Register(new GetMethodsCommand());
        commands.Register(new GetPropCommand());
        commands.Register(new SetPropCommand());
        commands.Register(new DelPropCommand());
        commands.Register(new CallMethodCommand());
        commands.Register(new CallCommand());
        commands.Register(new NativeAllocCommand());
        commands.RegisterAlias("alloc", "native-alloc");
        commands.Register(new NativeFreeCommand());
        commands.Register(new NativeReadCommand());
        commands.RegisterAlias("read-buffer", "native-read");
        commands.Register(new NativeWriteCommand());
        commands.RegisterAlias("write-buffer", "native-write");
        commands.Register(new NativeSizeOfCommand());
        commands.RegisterAlias("size-of", "native-sizeof");
        commands.Register(new NativeOffsetOfCommand());
        commands.RegisterAlias("offset-of", "native-offsetof");

        // ── Scripting (assert, raise, undef) ──
        commands.Register(new AssertCommand());
        commands.Register(new RaiseCommand());
        commands.Register(new UndefCommand());

        // ── Display (TUI styling) ──
        commands.Register(new StyledCommand());
    }
}
