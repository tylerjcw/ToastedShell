using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class ExternalCommandTests
{
    [Fact]
    public async Task Can_execute_external_command_by_explicit_relative_path()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "hello",
            unixBody:
            """
            printf 'alpha\nbeta\n'
            """,
            windowsBody:
            """
            @echo alpha
            @echo beta
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var results = await engine.ExecuteToListAsync("./" + commandName);

        Assert.Equal(["alpha", "beta"], results.Select(item => item?.ToString()!).ToArray());
        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public async Task Can_execute_external_command_from_path()
    {
        using var tempDirectory = new TemporaryDirectory();
        CreateScript(
            tempDirectory.Path,
            "path-hello",
            unixBody:
            """
            printf 'from-path\n'
            """,
            windowsBody:
            """
            @echo from-path
            """);

        using var _ = new TemporaryPathScope(tempDirectory.Path);
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        var results = await engine.ExecuteToListAsync("path-hello");

        Assert.Equal(["from-path"], results.Select(item => item?.ToString()!).ToArray());
        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public async Task External_commands_receive_pipeline_input_on_stdin()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "stdin-copy",
            unixBody:
            """
            cat
            """,
            windowsBody:
            """
            @more
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var results = await engine.ExecuteToListAsync("echo alpha beta | ./" + commandName);

        Assert.Equal(["alpha", "beta"], results.Select(item => item?.ToString()!).ToArray());
        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public async Task External_commands_require_executable_permissions()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandPath = Path.Combine(tempDirectory.Path, "not-executable");

        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(commandPath, "@echo nope" + Environment.NewLine);
        }
        else
        {
            await File.WriteAllTextAsync(commandPath, "#!/usr/bin/env sh\nprintf 'nope\\n'\n");
            File.SetUnixFileMode(commandPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(async () => await engine.ExecuteToListAsync("./not-executable"));
        var diagnostic = Assert.Single(exception.Diagnostics);

        Assert.Equal("tosh.runtime.external_command_not_executable", diagnostic.Code);
    }

    [Fact]
    public async Task Which_only_returns_executable_external_commands()
    {
        using var tempDirectory = new TemporaryDirectory();
        CreateScript(
            tempDirectory.Path,
            "visible-command",
            unixBody:
            """
            printf 'ok\n'
            """,
            windowsBody:
            """
            @echo ok
            """);

        var hiddenCommandPath = Path.Combine(tempDirectory.Path, "hidden-command");

        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(hiddenCommandPath, "@echo hidden" + Environment.NewLine);
        }
        else
        {
            await File.WriteAllTextAsync(hiddenCommandPath, "#!/usr/bin/env sh\nprintf 'hidden\\n'\n");
            File.SetUnixFileMode(hiddenCommandPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using var _ = new TemporaryPathScope(tempDirectory.Path);
        var engine = ShellEngine.CreateFullShell();

        var visibleResults = await engine.ExecuteToListAsync("which visible-command");
        var hiddenResults = await engine.ExecuteToListAsync("which hidden-command");

        Assert.Contains(visibleResults, item => Assert.IsType<CommandResolution>(item).Kind == CommandResolutionKind.External);
        Assert.Empty(hiddenResults);
    }

    [Fact]
    public async Task External_commands_update_last_exit_code_when_they_fail()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "fail-status",
            unixBody:
            """
            exit 7
            """,
            windowsBody:
            """
            @exit /b 7
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var results = await engine.ExecuteToListAsync("./" + commandName);

        Assert.Empty(results);
        Assert.Equal(7, runtime.LastExitCode);

        var exitCodeResults = await engine.ExecuteToListAsync("echo $tosh.Last.ExitCode");
        Assert.Equal(7, Assert.Single(exitCodeResults));
    }

    [Fact]
    public async Task External_text_lines_behave_like_strings_in_pipelines()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "emit-hello",
            unixBody:
            """
            printf 'hello\n'
            """,
            windowsBody:
            """
            @echo hello
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var equalityResults = await engine.ExecuteToListAsync("./" + commandName + " | where _ == \"hello\"");
        var methodResults = await engine.ExecuteToListAsync("./" + commandName + " | each { _.ToUpper() }");
        var memberResults = await engine.ExecuteToListAsync("./" + commandName + " | get Length");

        Assert.Single(equalityResults);
        Assert.Equal(["HELLO"], methodResults);
        Assert.Equal([5], memberResults);
    }

    [Fact]
    public async Task External_commands_accept_materialized_file_objects_as_path_arguments()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "path-cat",
            unixBody:
            """
            cat "$1"
            """,
            windowsBody:
            """
            @type %~1
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync("var file = (echo alpha beta | as-file text)");
        var results = await engine.ExecuteToListAsync("./" + commandName + " $file");

        Assert.Equal(["alpha", "beta"], results.Select(item => item?.ToString()!).ToArray());
        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public async Task External_commands_accept_input_process_substitution()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "path-cat",
            unixBody:
            """
            cat "$1"
            """,
            windowsBody:
            """
            @type %~1
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var results = await engine.ExecuteToListAsync("./" + commandName + " <(echo alpha beta)");

        Assert.Equal(["alpha", "beta"], results.Select(item => item?.ToString()!).ToArray());
        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public async Task External_commands_accept_splatted_argument_collections()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "argv-copy",
            unixBody:
            """
            printf '%s\n' "$@"
            """,
            windowsBody:
            """
            @setlocal EnableExtensions
            :loop
            @if "%~1"=="" goto end
            @echo %~1
            @shift
            @goto loop
            :end
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var results = await engine.ExecuteToListAsync(
            """
            var values = ["alpha", "beta", "gamma"];
            ./argv-copy ...$values
            """);

        Assert.Equal(["alpha", "beta", "gamma"], results.Select(item => item?.ToString()!).ToArray());
        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public async Task Here_string_can_feed_external_commands()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "stdin-copy",
            unixBody:
            """
            cat
            """,
            windowsBody:
            """
            @more
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var results = await engine.ExecuteToListAsync("<<< \"alpha\\nbeta\" | ./" + commandName);

        Assert.Equal(["alpha", "beta"], results.Select(item => item?.ToString()!).ToArray());
        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public async Task Background_external_jobs_can_start_from_here_string_input()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "stdin-copy",
            unixBody:
            """
            cat
            """,
            windowsBody:
            """
            @more
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var started = await engine.ExecuteToListAsync("<<< \"alpha\\nbeta\" | ./" + commandName + " &");
        Assert.Empty(started);
        var startedInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);

        var waited = await engine.ExecuteToListAsync($"wait-for {startedInfo.Id}");
        var completion = Assert.IsType<ShellJobCompletion>(Assert.Single(waited));

        Assert.Equal(ShellJobStatus.Completed, completion.Status);
        Assert.Equal(["alpha", "beta"], completion.Output.Select(item => item?.ToString()!).ToArray());
    }

    [Fact]
    public async Task External_commands_can_redirect_to_path_objects_and_append()
    {
        using var tempDirectory = new TemporaryDirectory();
        var emitBoth = CreateScript(
            tempDirectory.Path,
            "emit-both",
            unixBody:
            """
            printf 'out\n'
            printf 'err\n' >&2
            """,
            windowsBody:
            """
            @echo out
            @>&2 echo err
            """);
        var emitOne = CreateScript(
            tempDirectory.Path,
            "emit-one",
            unixBody:
            """
            printf 'one\n'
            """,
            windowsBody:
            """
            @echo one
            """);
        var emitTwo = CreateScript(
            tempDirectory.Path,
            "emit-two",
            unixBody:
            """
            printf 'two\n'
            """,
            windowsBody:
            """
            @echo two
            """);
        var pathCat = CreateScript(
            tempDirectory.Path,
            "path-cat",
            unixBody:
            """
            cat "$1"
            """,
            windowsBody:
            """
            @type %~1
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync("var stdout = (tempfile stdout txt)\nvar stderr = (tempfile stderr txt)\nvar combined = (tempfile combined txt)");
        await engine.ExecuteToListAsync($"./{emitBoth} out> $stdout err> $stderr");
        await engine.ExecuteToListAsync($"./{emitOne} out>> $combined");
        await engine.ExecuteToListAsync($"./{emitTwo} out>> $combined");
        var stdoutResults = await engine.ExecuteToListAsync($"./{pathCat} $stdout");
        var stderrResults = await engine.ExecuteToListAsync($"./{pathCat} $stderr");
        var combinedResults = await engine.ExecuteToListAsync($"./{pathCat} $combined");
        var results = stdoutResults.Concat(stderrResults).Concat(combinedResults).ToList();

        Assert.Equal(["out", "err", "one", "two"], results.Select(item => item?.ToString()!).ToArray());
    }

    [Fact]
    public async Task External_commands_can_combine_output_and_error_into_one_file()
    {
        using var tempDirectory = new TemporaryDirectory();
        var emitBoth = CreateScript(
            tempDirectory.Path,
            "emit-both",
            unixBody:
            """
            printf 'out\n'
            printf 'err\n' >&2
            """,
            windowsBody:
            """
            @echo out
            @>&2 echo err
            """);
        var emitTailWarn = CreateScript(
            tempDirectory.Path,
            "emit-tailwarn",
            unixBody:
            """
            printf 'tail\n'
            printf 'warn\n' >&2
            """,
            windowsBody:
            """
            @echo tail
            @>&2 echo warn
            """);
        var pathCat = CreateScript(
            tempDirectory.Path,
            "path-cat",
            unixBody:
            """
            cat "$1"
            """,
            windowsBody:
            """
            @type %~1
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync("var combined = (tempfile combined txt)");
        await engine.ExecuteToListAsync($"./{emitBoth} o+e> $combined");
        await engine.ExecuteToListAsync($"./{emitTailWarn} e+o>> $combined");
        var results = await engine.ExecuteToListAsync($"./{pathCat} $combined");

        Assert.Equal(["out", "err", "tail", "warn"], results.Select(item => item?.ToString()!).ToArray());
    }

    [Fact]
    public async Task External_commands_can_redirect_output_and_error_to_the_same_file_consistently()
    {
        using var tempDirectory = new TemporaryDirectory();
        var emitBoth = CreateScript(
            tempDirectory.Path,
            "emit-both",
            unixBody:
            """
            printf 'out\n'
            printf 'err\n' >&2
            """,
            windowsBody:
            """
            @echo out
            @>&2 echo err
            """);
        var pathCat = CreateScript(
            tempDirectory.Path,
            "path-cat",
            unixBody:
            """
            cat "$1"
            """,
            windowsBody:
            """
            @type %~1
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync("var combined = (tempfile combined txt)");
        await engine.ExecuteToListAsync($"./{emitBoth} out> $combined err>> $combined");
        var results = await engine.ExecuteToListAsync($"./{pathCat} $combined");

        Assert.Equal(["out", "err"], results.Select(item => item?.ToString()!).ToArray());
    }

    [Fact]
    public async Task Background_external_jobs_honor_explicit_redirections()
    {
        using var tempDirectory = new TemporaryDirectory();
        var emitBoth = CreateScript(
            tempDirectory.Path,
            "emit-both",
            unixBody:
            """
            printf 'out\n'
            printf 'err\n' >&2
            """,
            windowsBody:
            """
            @echo out
            @>&2 echo err
            """);
        var pathCat = CreateScript(
            tempDirectory.Path,
            "path-cat",
            unixBody:
            """
            cat "$1"
            """,
            windowsBody:
            """
            @type %~1
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync("var stdout = (tempfile stdout txt)\nvar stderr = (tempfile stderr txt)");
        var started = await engine.ExecuteToListAsync($"./{emitBoth} out> $stdout err> $stderr &");
        Assert.Empty(started);
        var startedInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);

        var completionResults = await engine.ExecuteToListAsync($"wait-for {startedInfo.Id}");
        var completion = Assert.IsType<ShellJobCompletion>(Assert.Single(completionResults));
        var stdoutResults = await engine.ExecuteToListAsync($"./{pathCat} $stdout");
        var stderrResults = await engine.ExecuteToListAsync($"./{pathCat} $stderr");
        var fileResults = stdoutResults.Concat(stderrResults).ToList();

        Assert.Equal(["out", "err"], fileResults.Select(item => item?.ToString()!).ToArray());
        Assert.Empty(completion.Output);
        Assert.Empty(completion.ErrorLines);
        Assert.Equal(ShellJobStatus.Completed, completion.Status);
    }

    [Fact]
    public async Task Background_external_jobs_can_combine_output_and_error_into_one_file()
    {
        using var tempDirectory = new TemporaryDirectory();
        var emitBoth = CreateScript(
            tempDirectory.Path,
            "emit-both",
            unixBody:
            """
            printf 'out\n'
            printf 'err\n' >&2
            """,
            windowsBody:
            """
            @echo out
            @>&2 echo err
            """);
        var pathCat = CreateScript(
            tempDirectory.Path,
            "path-cat",
            unixBody:
            """
            cat "$1"
            """,
            windowsBody:
            """
            @type %~1
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync("var combined = (tempfile combined txt)");
        var started = await engine.ExecuteToListAsync($"./{emitBoth} o+e> $combined &");
        Assert.Empty(started);
        var startedInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);

        var completionResults = await engine.ExecuteToListAsync($"wait-for {startedInfo.Id}");
        var completion = Assert.IsType<ShellJobCompletion>(Assert.Single(completionResults));
        var fileResults = await engine.ExecuteToListAsync($"./{pathCat} $combined");

        Assert.Equal(["out", "err"], fileResults.Select(item => item?.ToString()!).ToArray());
        Assert.Empty(completion.Output);
        Assert.Empty(completion.ErrorLines);
    }

    [Fact]
    public async Task Pipeline_exit_code_uses_last_stage_by_default_and_pipefail_when_enabled()
    {
        using var tempDirectory = new TemporaryDirectory();
        var failCommand = CreateScript(
            tempDirectory.Path,
            "fail7",
            unixBody:
            """
            exit 7
            """,
            windowsBody:
            """
            @exit /b 7
            """);
        var successCommand = CreateScript(
            tempDirectory.Path,
            "ok0",
            unixBody:
            """
            exit 0
            """,
            windowsBody:
            """
            @exit /b 0
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync($"./{failCommand} | ./{successCommand}");
        var defaultExitCode = runtime.LastExitCode;

        await engine.ExecuteToListAsync("$tosh.Config.Shell.Pipefail = true");
        await engine.ExecuteToListAsync($"./{failCommand} | ./{successCommand}");
        var pipefailExitCode = runtime.LastExitCode;

        Assert.Equal(0, defaultExitCode);
        Assert.Equal(7, pipefailExitCode);
    }

    [Fact]
    public async Task Background_external_jobs_can_be_listed_and_waited_for()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "background-hello",
            unixBody:
            """
            sleep 0.2
            printf 'ready\n'
            """,
            windowsBody:
            """
            @ping -n 3 127.0.0.1 > nul
            @echo ready
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var started = await engine.ExecuteToListAsync("./" + commandName + " &");
        Assert.Empty(started);
        var startedInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);

        var listed = await engine.ExecuteToListAsync("jobs");
        Assert.Contains(listed, item => Assert.IsType<ShellJobInfo>(item).Id == startedInfo.Id);

        var waited = await engine.ExecuteToListAsync($"wait-for {startedInfo.Id}");
        var completion = Assert.IsType<ShellJobCompletion>(Assert.Single(waited));

        Assert.Equal(startedInfo.Id, completion.Id);
        Assert.Equal(ShellJobStatus.Completed, completion.Status);
        Assert.Equal(["ready"], completion.Output.Select(item => item?.ToString()!).ToArray());
    }

    [Fact]
    public async Task Background_external_jobs_can_be_killed()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "background-sleep",
            unixBody:
            """
            sleep 5
            printf 'too-late\n'
            """,
            windowsBody:
            """
            @ping -n 31 127.0.0.1 > nul
            @echo too-late
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var started = await engine.ExecuteToListAsync("./" + commandName + " &");
        Assert.Empty(started);
        var startedInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);

        var killResults = await engine.ExecuteToListAsync($"kill {startedInfo.Id}");
        var killResult = Assert.IsType<JobControlResult>(Assert.Single(killResults));
        Assert.True(killResult.IsSuccess);

        var waited = await engine.ExecuteToListAsync($"wait-for {startedInfo.Id}");
        var completion = Assert.IsType<ShellJobCompletion>(Assert.Single(waited));
        Assert.Equal(ShellJobStatus.Cancelled, completion.Status);
    }

    [Fact]
    public async Task Background_external_pipelines_can_be_waited_for()
    {
        using var tempDirectory = new TemporaryDirectory();
        var producer = CreateScript(
            tempDirectory.Path,
            "producer",
            unixBody:
            """
            printf 'alpha\nbeta\n'
            """,
            windowsBody:
            """
            @echo alpha
            @echo beta
            """);
        var consumer = CreateScript(
            tempDirectory.Path,
            "consumer",
            unixBody:
            """
            tr '[:lower:]' '[:upper:]'
            """,
            windowsBody:
            """
            @powershell -NoProfile -Command "$input | ForEach-Object { $_.ToUpperInvariant() }"
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var started = await engine.ExecuteToListAsync($"./{producer} | ./{consumer} &");
        Assert.Empty(started);
        var startedInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);

        var waited = await engine.ExecuteToListAsync($"wait-for {startedInfo.Id}");
        var completion = Assert.IsType<ShellJobCompletion>(Assert.Single(waited));

        Assert.Equal(ShellJobStatus.Completed, completion.Status);
        Assert.Equal(["ALPHA", "BETA"], completion.Output.Select(item => item?.ToString()!).ToArray());
    }

    [Fact]
    public async Task Signal_command_can_terminate_background_jobs()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "linger",
            unixBody:
            """
            sleep 5
            """,
            windowsBody:
            """
            @ping -n 31 127.0.0.1 > nul
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var started = await engine.ExecuteToListAsync("./" + commandName + " &");
        Assert.Empty(started);
        var startedInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);

        var signalResults = await engine.ExecuteToListAsync($"signal TERM {startedInfo.Id}");
        var signalResult = Assert.IsType<JobControlResult>(Assert.Single(signalResults));
        Assert.True(signalResult.IsSuccess);

        var waited = await engine.ExecuteToListAsync($"wait-for {startedInfo.Id}");
        var completion = Assert.IsType<ShellJobCompletion>(Assert.Single(waited));
        Assert.Equal(ShellJobStatus.Failed, completion.Status);
        Assert.NotEqual(0, completion.ExitCode);
    }

    [Fact]
    public async Task Signal_command_can_suspend_and_resume_background_jobs()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "linger",
            unixBody:
            """
            sleep 30
            """,
            windowsBody:
            """
            @ping -n 31 127.0.0.1 > nul
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var started = await engine.ExecuteToListAsync("./" + commandName + " &");
        Assert.Empty(started);
        var startedInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);

        var stopped = await engine.ExecuteToListAsync($"signal STOP {startedInfo.Id}");
        var stopResult = Assert.IsType<JobControlResult>(Assert.Single(stopped));
        Assert.True(stopResult.IsSuccess);
        Assert.True(runtime.TryGetJob(startedInfo.Id, out var job));
        Assert.Equal(ShellJobStatus.Suspended, job.Status);

        var resumed = await engine.ExecuteToListAsync($"bg {startedInfo.Id}");
        var resumeResult = Assert.IsType<JobControlResult>(Assert.Single(resumed));
        Assert.True(resumeResult.IsSuccess);
        Assert.Equal(ShellJobStatus.Running, job.Status);

        var killed = await engine.ExecuteToListAsync($"kill {startedInfo.Id}");
        var killResult = Assert.IsType<JobControlResult>(Assert.Single(killed));
        Assert.True(killResult.IsSuccess);

        var waited = await engine.ExecuteToListAsync($"wait-for {startedInfo.Id}");
        var completion = Assert.IsType<ShellJobCompletion>(Assert.Single(waited));
        Assert.Equal(ShellJobStatus.Cancelled, completion.Status);
    }

    [Fact]
    public async Task Command_substitution_can_be_assigned_and_interpolated()
    {
        using var tempDirectory = new TemporaryDirectory();
        var firstLine = CreateScript(
            tempDirectory.Path,
            "first-line",
            unixBody:
            """
            head -n 1 "$1"
            """,
            windowsBody:
            """
            @for /f "usebackq delims=" %%A in ("%~1") do (
            @  echo %%A
            @  goto :eof
            @)
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var results = await engine.ExecuteToListAsync(
            "var file = (echo Bread Coffee | as-file text)\n" +
            "var firstName = $(./" + firstLine + " $file)\n" +
            "echo $\"First sorted item: {$firstName}\"");

        Assert.Equal("First sorted item: Bread", Assert.Single(results));
    }

    [Fact]
    public async Task External_commands_expand_bareword_globs_and_preserve_quoted_or_unmatched_literals()
    {
        using var tempDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(tempDirectory.Path, "alpha.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(tempDirectory.Path, "beta.txt"), "beta");
        var commandName = CreateScript(
            tempDirectory.Path,
            "show-args",
            unixBody:
            """
            for arg in "$@"; do
                printf '%s\n' "$arg"
            done
            """,
            windowsBody:
            """
            @setlocal EnableExtensions
            :loop
            @if "%~1"=="" goto end
            @echo %~1
            @shift
            @goto loop
            :end
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        var expanded = await engine.ExecuteToListAsync("./" + commandName + " *.txt");
        var quoted = await engine.ExecuteToListAsync("./" + commandName + " \"*.txt\"");
        var unmatched = await engine.ExecuteToListAsync("./" + commandName + " *.missing");

        Assert.Equal(["alpha.txt", "beta.txt"], expanded.Select(item => item?.ToString()!).ToArray());
        Assert.Equal(["*.txt"], quoted.Select(item => item?.ToString()!).ToArray());
        Assert.Equal(["*.missing"], unmatched.Select(item => item?.ToString()!).ToArray());
    }

    [Fact]
    public async Task ExitOnError_throws_when_command_exits_nonzero()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "fail42",
            unixBody:
            """
            exit 42
            """,
            windowsBody:
            """
            @exit /b 42
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        runtime.Config.Shell.ExitOnError = true;

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("./" + commandName));
        Assert.Contains("42", exception.Diagnostics[0].Title);
    }

    [Fact]
    public async Task ExitOnError_does_not_throw_on_zero_exit()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "ok0",
            unixBody:
            """
            exit 0
            """,
            windowsBody:
            """
            @exit /b 0
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        runtime.Config.Shell.ExitOnError = true;

        var results = await engine.ExecuteToListAsync("./" + commandName);
        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public async Task ExitOnError_with_pipefail_throws_on_first_stage_failure()
    {
        using var tempDirectory = new TemporaryDirectory();
        var failCommand = CreateScript(
            tempDirectory.Path,
            "fail3",
            unixBody:
            """
            exit 3
            """,
            windowsBody:
            """
            @exit /b 3
            """);
        var consumerCommand = CreateScript(
            tempDirectory.Path,
            "stdin-copy",
            unixBody:
            """
            cat
            """,
            windowsBody:
            """
            @more
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        runtime.Config.Shell.ExitOnError = true;
        runtime.Config.Shell.Pipefail = true;

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync($"./{failCommand} | ./{consumerCommand}"));
        Assert.Contains("3", exception.Diagnostics[0].Title);
    }

    [Fact]
    public async Task Trace_writes_command_name_and_arguments_to_stderr()
    {
        var runtime = ToshRuntime.CreateDefault();
        var errorWriter = new StringWriter();
        runtime.Error = errorWriter;
        var engine = new ToshEngine(runtime.Language);

        runtime.Config.Shell.Trace = true;

        await engine.ExecuteToListAsync("echo hello world");
        var traceOutput = errorWriter.ToString();

        Assert.Contains("+ echo hello world", traceOutput);
    }

    private static string CreateScript(string directory, string name, string unixBody, string windowsBody)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(directory, name + ".cmd");
            File.WriteAllText(path, $"@echo off{Environment.NewLine}{windowsBody.Trim().Replace("\n", Environment.NewLine, StringComparison.Ordinal)}{Environment.NewLine}");
            return Path.GetFileName(path);
        }

        var scriptPath = Path.Combine(directory, name);
        File.WriteAllText(scriptPath, $"#!/usr/bin/env sh\n{unixBody.Trim()}\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return Path.GetFileName(scriptPath);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-external-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class TemporaryPathScope : IDisposable
    {
        private readonly string? _previousPath;

        public TemporaryPathScope(string prependDirectory)
        {
            _previousPath = Environment.GetEnvironmentVariable("PATH");
            var updatedPath = string.IsNullOrWhiteSpace(_previousPath)
                ? prependDirectory
                : prependDirectory + Path.PathSeparator + _previousPath;
            Environment.SetEnvironmentVariable("PATH", updatedPath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _previousPath);
        }
    }
}
