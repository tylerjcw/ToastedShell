using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A value is never read as an option — <c>TOSH-0013</c>.
/// </summary>
/// <remarks>
/// <para>
/// Builtins found their options by comparing argument values with option spellings, so any value
/// that started with a dash became one. <c>var name = "-r"; rm $name victim</c> removed
/// <c>victim/</c> recursively and left the file called <c>-r</c> where it was, and a file called
/// <c>-r</c> listed into <c>xargs rm</c> did the same to every directory after it.
/// </para>
/// <para>
/// Each case here builds the same small tree: a file whose name is an option, and a directory
/// with something in it that must survive unless the option was really written.
/// </para>
/// </remarks>
public sealed class ArgumentProvenanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"tosh-argument-provenance-tests-{Guid.NewGuid():N}");

    public ArgumentProvenanceTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "victim"));
        File.WriteAllText(Path.Combine(_directory, "victim", "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(_directory, "-r"), "a file called -r");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private bool VictimSurvived => File.Exists(Path.Combine(_directory, "victim", "keep.txt"));

    private bool DashFileSurvived => File.Exists(Path.Combine(_directory, "-r"));

    [Fact]
    public async Task A_written_option_still_works()
    {
        await RunAsync("rm -r victim");

        Assert.False(Directory.Exists(Path.Combine(_directory, "victim")));
        Assert.True(DashFileSurvived);
    }

    [Theory]
    [InlineData("var name = \"-r\"\nrm $name victim")]
    [InlineData("rm (echo \"-r\") victim")]
    [InlineData("rm $\"-r\" victim")]
    [InlineData("rm \"-r\" victim")]
    [InlineData("var options = [\"-r\"]\nrm ...$options victim")]
    [InlineData("\"-r\" |> rm victim")]
    public async Task A_value_that_starts_with_a_dash_is_an_operand(string source)
    {
        // `-r` is the file to remove, and without the option the directory is refused.
        await Assert.ThrowsAsync<ToshDiagnosticException>(() => RunAsync(source));

        Assert.True(VictimSurvived);
        Assert.False(DashFileSurvived);
    }

    [Fact]
    public async Task A_double_dash_ends_the_options()
    {
        await RunAsync("rm -- -r");

        Assert.False(DashFileSurvived);
        Assert.True(VictimSurvived);
    }

    [Fact]
    public async Task A_glob_match_is_an_operand()
    {
        await Assert.ThrowsAsync<ToshDiagnosticException>(() => RunAsync("rm *"));

        Assert.True(VictimSurvived);
        Assert.False(DashFileSurvived);
    }

    [Fact]
    public async Task A_wrapper_function_forwards_written_options_and_values_as_they_were()
    {
        // `func del => rm` hands its own arguments to `rm`. What the caller wrote as an option
        // must stay one, and what the caller passed as a value must stay a value.
        await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync("func del => rm\nvar name = \"-r\"\ndel $name victim"));

        Assert.True(VictimSurvived);

        await RunAsync("func del => rm\ndel -r victim");

        Assert.False(Directory.Exists(Path.Combine(_directory, "victim")));
    }

    [Fact]
    public async Task Invoke_hands_on_what_is_known_about_its_arguments()
    {
        // `invoke` passes everything after the callable along. A slice that forgot where its
        // items came from would turn the `-r` written here into a value, and a lost option is
        // how this would fail.
        await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync("func del => rm\nvar name = \"-r\"\ninvoke &del $name victim"));

        Assert.True(VictimSurvived);

        await RunAsync("func del => rm\ninvoke &del -r victim");

        Assert.False(Directory.Exists(Path.Combine(_directory, "victim")));
    }

    [Fact]
    public async Task A_line_of_input_to_xargs_is_an_operand()
    {
        // The command line `xargs` builds is parsed again; a line of input used to go into it
        // bare, where `-r` is an option like any other.
        await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync("ls | get Name | xargs rm"));

        Assert.True(VictimSurvived);
        Assert.False(DashFileSurvived);

        await RunAsync("echo victim | xargs rm -r");

        Assert.False(Directory.Exists(Path.Combine(_directory, "victim")));
    }

    [Fact]
    public async Task Mv_moves_a_file_whose_name_is_an_option()
    {
        File.WriteAllText(Path.Combine(_directory, "-n"), "moved");
        File.WriteAllText(Path.Combine(_directory, "target.txt"), "replaced");

        // Read as `-n`, this was "no clobber" with a single operand, and nothing moved.
        await RunAsync("var name = \"-n\"\nmv $name target.txt");

        Assert.False(File.Exists(Path.Combine(_directory, "-n")));
        Assert.Equal("moved", File.ReadAllText(Path.Combine(_directory, "target.txt")));
    }

    [Fact]
    public async Task Cp_does_not_recurse_on_a_value()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "destination"));

        await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync("var name = \"-r\"\ncp $name victim destination"));

        Assert.True(File.Exists(Path.Combine(_directory, "destination", "-r")));
        Assert.False(Directory.Exists(Path.Combine(_directory, "destination", "victim")));
    }

    [Fact]
    public async Task Ln_does_not_force_on_a_value()
    {
        // `ln -f` removes an existing destination — a directory recursively — before linking.
        File.WriteAllText(Path.Combine(_directory, "-f"), "a file called -f");
        File.WriteAllText(Path.Combine(_directory, "target.txt"), "target");

        await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync("var name = \"-f\"\nln $name target.txt victim"));

        Assert.True(VictimSurvived);
    }

    [Fact]
    public async Task Chmod_does_not_recurse_on_a_value()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var inner = Path.Combine(_directory, "victim", "keep.txt");
        File.SetUnixFileMode(inner, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(Path.Combine(_directory, "-R"), "a file called -R");

        await RunAsync("var name = \"-R\"\nchmod 700 $name victim");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(inner));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.Combine(_directory, "-R")));

        await RunAsync("chmod -R 700 victim");

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(inner));
    }

    [Fact]
    public async Task Chmod_takes_a_symbolic_mode_that_starts_with_a_dash_from_a_value()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var file = Path.Combine(_directory, "victim", "keep.txt");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // Written literally, `-w` is still read as an option; from a value it is the mode.
        await RunAsync($"var mode = \"-w\"\nchmod $mode {file}");

        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(file));
    }

    [Fact]
    public async Task Chown_does_not_recurse_on_a_value()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(Path.Combine(_directory, "-R"), "a file called -R");

        // Changing the owner to the current owner needs no privilege, and each path the
        // command touched comes back as an entry — so the entries say whether it recursed.
        var entries = await RunAsync($"var name = \"-R\"\nchown {Environment.UserName} $name victim");

        var names = entries.Select(entry => Path.GetFileName(Assert.IsType<FileSystemEntry>(entry).FullName)).ToArray();
        Assert.Equal(["-R", "victim"], names);
    }

    [Fact]
    public async Task Kill_refuses_a_process_id_of_zero_or_less()
    {
        // `kill` reads no options, so a computed `-1` is a target rather than a signal. Before the
        // guard, `kill -1` killed every process the user owned: Process.GetProcessById(-1) checks
        // existence with kill(-1, 0), which succeeds, and Process.Kill then sends kill(-1, SIGKILL).
        //
        // So this test must never pass -1 or 0 itself — run with the guard missing, it would do
        // exactly that to the machine running the suite, and it did, to a container. It passes a
        // pid the same `<= 0` branch refuses, whose process group cannot exist: unguarded, kill(2)
        // answers ESRCH and the test fails on the message instead.
        var results = await RunAsync("var target = -2147483647\nkill $target");

        var result = Assert.IsType<JobControlResult>(Assert.Single(results));
        Assert.False(result.IsSuccess);
        Assert.Contains("is not a process id", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_signal_sender_refuses_pids_that_name_groups()
    {
        // This is where `signal TERM $pid` meets kill(2), and it is tested here rather than
        // through the command on purpose. Signal 0 checks permission and delivers nothing, so a
        // regression makes this fail instead of signalling every process the test may reach.
        // Through the command it cannot be done safely: `signal` refuses 0, and every real signal
        // sent to -1 — even SIGWINCH, which processes ignore by default — reached every process
        // in the container the suite ran in, and took the session down with it.
        Assert.False(ProcessSignalSender.TrySend(-1, 0, out var error));
        Assert.Contains("not a process id", error, StringComparison.Ordinal);
        Assert.False(ProcessSignalSender.TrySend(0, 0, out _));
        Assert.False(ProcessSignalSender.TrySendToGroup(1, 0, out _));
        Assert.False(ProcessSignalSender.TrySendToGroup(0, 0, out _));
    }

    [Fact]
    public void An_argument_list_the_engine_did_not_build_has_no_options()
    {
        var runtime = ToshRuntime.CreateDefault();
        var tracked = new CommandArgumentList(["-r", "-r"], [true, false]);
        var context = new CommandContext(runtime.Language, AsyncEnumerableExtensions.Empty<object?>(), tracked, CancellationToken.None);

        Assert.True(context.MayBeOption(0));
        Assert.False(context.MayBeOption(1));
        Assert.False(context.MayBeOption(2));

        // A list a command made for itself says nothing about where its items came from.
        var untracked = context with { Arguments = ["-r"] };
        Assert.False(untracked.MayBeOption(0));

        var parsed = ParsedCommandArguments.Parse(context);
        Assert.True(parsed.HasFlag("r"));
        Assert.Equal(new object?[] { "-r" }, parsed.Positionals);
        Assert.Empty(ParsedCommandArguments.Parse(untracked).Flags);
    }

    [Fact]
    public void Slicing_and_joining_keep_each_arguments_origin()
    {
        var first = new CommandArgumentList(["rm", "-r", "x"], [true, true, false]);
        var second = CommandArgumentList.Values(["-f"]);

        var sliced = Assert.IsType<CommandArgumentList>(CommandArguments.Slice(first, 1));
        var joined = sliced.Concat(second);

        Assert.Equal(new object?[] { "-r", "x", "-f" }, joined.ToArray());
        Assert.True(joined.IsWord(0));
        Assert.False(joined.IsWord(1));
        Assert.False(joined.IsWord(2));
        Assert.Same(first, CommandArgumentList.From(first));
        Assert.False(CommandArgumentList.From(["-r"]).IsWord(0));
    }

    private async Task<IReadOnlyList<object?>> RunAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = _directory;
        var engine = new ToshEngine(runtime.Language);

        return await engine.ExecuteToListAsync(source).WaitAsync(TimeSpan.FromSeconds(30));
    }
}
