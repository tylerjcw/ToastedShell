using Tosh.Cli;

namespace Tosh.Tests;

public sealed class CliInvocationResolverTests
{
    [Fact]
    public void Resolver_recognizes_command_flag()
    {
        var plan = CliInvocationResolver.Resolve(["-c", "echo hello", "one", "two"], Environment.CurrentDirectory);

        Assert.Equal(CliInvocationKind.Command, plan.Kind);
        Assert.Equal("echo hello", plan.ScriptOrCommand);
        Assert.Equal(["one", "two"], plan.Arguments);
        Assert.True(plan.LoadStartup);
    }

    [Fact]
    public void Resolver_allows_no_startup_for_repl_and_commands()
    {
        var replPlan = CliInvocationResolver.Resolve(["--no-startup"], Environment.CurrentDirectory);
        var commandPlan = CliInvocationResolver.Resolve(["--no-startup", "-c", "echo hello"], Environment.CurrentDirectory);

        Assert.Equal(CliInvocationKind.Repl, replPlan.Kind);
        Assert.False(replPlan.LoadStartup);
        Assert.Equal(CliInvocationKind.Command, commandPlan.Kind);
        Assert.False(commandPlan.LoadStartup);
        Assert.Equal("echo hello", commandPlan.ScriptOrCommand);
    }

    [Fact]
    public void Resolver_recognizes_dot_tosh_script_without_shebang()
    {
        using var tempDirectory = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDirectory.Path, "demo.tosh");
        File.WriteAllText(scriptPath, "echo ok");

        var plan = CliInvocationResolver.Resolve([scriptPath, "alpha"], tempDirectory.Path);

        Assert.Equal(CliInvocationKind.ToshScript, plan.Kind);
        Assert.Equal(Path.GetFullPath(scriptPath), plan.ScriptOrCommand);
        Assert.Equal(["alpha"], plan.Arguments);
        Assert.True(plan.LoadStartup);
    }

    [Fact]
    public void Resolver_applies_no_startup_to_tosh_scripts()
    {
        using var tempDirectory = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDirectory.Path, "demo.tosh");
        File.WriteAllText(scriptPath, "echo ok");

        var plan = CliInvocationResolver.Resolve(["--no-startup", scriptPath, "alpha"], tempDirectory.Path);

        Assert.Equal(CliInvocationKind.ToshScript, plan.Kind);
        Assert.False(plan.LoadStartup);
        Assert.Equal(Path.GetFullPath(scriptPath), plan.ScriptOrCommand);
        Assert.Equal(["alpha"], plan.Arguments);
    }

    [Fact]
    public void Resolver_recognizes_tosh_shebang_without_extension()
    {
        using var tempDirectory = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDirectory.Path, "demo");
        File.WriteAllText(scriptPath, "#!/usr/bin/env tosh\necho ok\n");

        var plan = CliInvocationResolver.Resolve([scriptPath, "alpha"], tempDirectory.Path);

        Assert.Equal(CliInvocationKind.ToshScript, plan.Kind);
        Assert.Equal(Path.GetFullPath(scriptPath), plan.ScriptOrCommand);
        Assert.Equal(["alpha"], plan.Arguments);
    }

    [Fact]
    public void Resolver_recognizes_foreign_shebang_and_builds_external_invocation()
    {
        using var tempDirectory = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDirectory.Path, "demo");
        File.WriteAllText(scriptPath, "#!/usr/bin/env sh\nprintf 'ok\\n'\n");

        var plan = CliInvocationResolver.Resolve([scriptPath, "alpha"], tempDirectory.Path);

        Assert.Equal(CliInvocationKind.ExternalScript, plan.Kind);
        Assert.Equal(Path.GetFullPath(scriptPath), plan.ScriptOrCommand);
        Assert.Equal("sh", plan.Arguments[0]);
        Assert.Equal(Path.GetFullPath(scriptPath), plan.Arguments[1]);
        Assert.Equal("alpha", plan.Arguments[2]);
    }

    [Fact]
    public void Resolver_uses_double_dash_to_allow_leading_dash_command()
    {
        var plan = CliInvocationResolver.Resolve(["--", "-custom-command"], Environment.CurrentDirectory);

        Assert.Equal(CliInvocationKind.Command, plan.Kind);
        Assert.Equal("-custom-command", plan.ScriptOrCommand);
    }

    [Fact]
    public void Resolver_rejects_unknown_leading_option_without_double_dash()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CliInvocationResolver.Resolve(["-custom-command"], Environment.CurrentDirectory));

        Assert.Contains("Unknown option '-custom-command'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_recognizes_no_profile_flag()
    {
        var replPlan = CliInvocationResolver.Resolve(["--no-profile"], Environment.CurrentDirectory);
        var commandPlan = CliInvocationResolver.Resolve(["--no-profile", "-c", "echo hello"], Environment.CurrentDirectory);

        Assert.Equal(CliInvocationKind.Repl, replPlan.Kind);
        Assert.True(replPlan.LoadStartup);
        Assert.True(replPlan.SkipProfile);

        Assert.Equal(CliInvocationKind.Command, commandPlan.Kind);
        Assert.True(commandPlan.SkipProfile);
        Assert.Equal("echo hello", commandPlan.ScriptOrCommand);
    }

    [Fact]
    public void Resolver_recognizes_login_flag()
    {
        var longPlan = CliInvocationResolver.Resolve(["--login"], Environment.CurrentDirectory);
        var shortPlan = CliInvocationResolver.Resolve(["-l"], Environment.CurrentDirectory);
        var combinedPlan = CliInvocationResolver.Resolve(["--login", "-c", "echo hello"], Environment.CurrentDirectory);

        Assert.Equal(CliInvocationKind.Repl, longPlan.Kind);
        Assert.True(longPlan.IsLoginShell);
        Assert.True(longPlan.LoadStartup);

        Assert.Equal(CliInvocationKind.Repl, shortPlan.Kind);
        Assert.True(shortPlan.IsLoginShell);

        Assert.Equal(CliInvocationKind.Command, combinedPlan.Kind);
        Assert.True(combinedPlan.IsLoginShell);
        Assert.Equal("echo hello", combinedPlan.ScriptOrCommand);
    }

    [Fact]
    public void Resolver_combines_no_profile_and_login_flags()
    {
        var plan = CliInvocationResolver.Resolve(["--no-profile", "--login", "-c", "echo hi"], Environment.CurrentDirectory);

        Assert.Equal(CliInvocationKind.Command, plan.Kind);
        Assert.True(plan.SkipProfile);
        Assert.True(plan.IsLoginShell);
        Assert.True(plan.LoadStartup);
        Assert.Equal("echo hi", plan.ScriptOrCommand);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-cli-tests-{Guid.NewGuid():N}");
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
}
