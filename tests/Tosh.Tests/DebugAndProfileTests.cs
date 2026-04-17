using System.Text;
using Tosh.Cli;
using Tosh.Core;
using Tosh.Language;
using Tosh.Language.Debugging;

namespace Tosh.Tests;

public sealed class StartupProfileTests
{
    [Fact]
    public void CliResolver_parses_profile_startup_flag()
    {
        var plan = CliInvocationResolver.Resolve(["--profile-startup"], Environment.CurrentDirectory);

        Assert.Equal(CliInvocationKind.Repl, plan.Kind);
        Assert.True(plan.ProfileStartup);
    }

    [Fact]
    public void CliResolver_profile_startup_defaults_to_false()
    {
        var plan = CliInvocationResolver.Resolve([], Environment.CurrentDirectory);

        Assert.Equal(CliInvocationKind.Repl, plan.Kind);
        Assert.False(plan.ProfileStartup);
    }

    [Fact]
    public async Task LoadAsync_populates_startup_profile_when_enabled()
    {
        using var tempDir = new TemporaryDirectory();
        var configPath = Path.Combine(tempDir.Path, "config.tosh");
        File.WriteAllText(configPath, "# empty config");

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await ToshStartupLoader.LoadAsync(engine, tempDir.Path, skipProfile: false, errorWriter: TextWriter.Null, profileStartup: true);

        Assert.NotNull(runtime.StartupProfile);
        Assert.True(runtime.StartupProfile.Total > TimeSpan.Zero);
        Assert.True(runtime.StartupProfile.Files.Count > 0);
    }

    [Fact]
    public async Task LoadAsync_does_not_populate_profile_when_disabled()
    {
        using var tempDir = new TemporaryDirectory();

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await ToshStartupLoader.LoadAsync(engine, tempDir.Path, skipProfile: false, errorWriter: TextWriter.Null, profileStartup: false);

        Assert.Null(runtime.StartupProfile);
    }

    [Fact]
    public void StartupProfileData_implements_IShellRecordObject()
    {
        var profile = new StartupProfileData
        {
            Total = TimeSpan.FromMilliseconds(100),
            Config = TimeSpan.FromMilliseconds(30),
            Profile = TimeSpan.FromMilliseconds(40),
            Autoload = TimeSpan.FromMilliseconds(20),
            History = TimeSpan.FromMilliseconds(10),
        };

        Assert.Equal("StartupProfile", profile.ShellTypeName);
        Assert.True(profile.TryGetMember("Total", out var total));
        Assert.Equal(TimeSpan.FromMilliseconds(100), total);

        var members = profile.GetMembers();
        Assert.Equal(6, members.Count);
    }

    [Fact]
    public void StartupFileProfile_implements_IShellRecordObject()
    {
        var fileProfile = new StartupFileProfile
        {
            Path = "/config/test.tosh",
            Duration = TimeSpan.FromMilliseconds(42),
        };

        Assert.Equal("StartupFileProfile", fileProfile.ShellTypeName);
        Assert.True(fileProfile.TryGetMember("Path", out var path));
        Assert.Equal("/config/test.tosh", path);
        Assert.True(fileProfile.TryGetMember("Duration", out var duration));
        Assert.Equal(TimeSpan.FromMilliseconds(42), duration);
        Assert.False(fileProfile.TrySetMember("Path", "other"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-profile-tests-{Guid.NewGuid():N}");
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

public sealed class DebugHookTests
{
    [Fact]
    public async Task DebugHook_fires_for_each_statement_in_block()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var statementsHit = new List<string>();

        engine.DebugHook = context =>
        {
            statementsHit.Add(context.StatementText ?? "<null>");
            return Task.FromResult(DebugAction.Continue);
        };

        await engine.ExecuteToListAsync(
            "func test() {\n" +
            "  var x = 1\n" +
            "  var y = 2\n" +
            "  echo $x\n" +
            "}\n" +
            "test");

        // Should have hit at least the var declarations and echo inside the function,
        // plus the test call and function definition at top level.
        Assert.True(statementsHit.Count >= 3, $"Expected at least 3 statements hit, got {statementsHit.Count}: [{string.Join(", ", statementsHit)}]");
    }

    [Fact]
    public async Task DebugHook_abort_stops_execution()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var hitCount = 0;

        engine.DebugHook = context =>
        {
            hitCount++;
            // Abort on the second statement
            return Task.FromResult(hitCount >= 2 ? DebugAction.Abort : DebugAction.StepNext);
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            engine.ExecuteToListAsync(
                "func run() {\n" +
                "  var x = 1\n" +
                "  var y = 2\n" +
                "  var z = 3\n" +
                "}\n" +
                "run"));
    }

    [Fact]
    public async Task DebugHook_provides_line_numbers()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var lines = new List<int?>();

        engine.DebugHook = context =>
        {
            lines.Add(context.Line);
            return Task.FromResult(DebugAction.Continue);
        };

        await engine.ExecuteToListAsync(
            "func traced() {\n" +
            "  echo hello\n" +
            "  echo world\n" +
            "}\n" +
            "traced");

        // Both statements should have line numbers.
        Assert.All(lines, line => Assert.NotNull(line));
        Assert.Contains(2, lines.Select(l => l!.Value));
        Assert.Contains(3, lines.Select(l => l!.Value));
    }

    [Fact]
    public async Task ScriptTrace_emits_to_error_stream()
    {
        var errorOutput = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, errorOutput);
        var engine = new ToshEngine(runtime);

        runtime.Config.Shell.ScriptTrace = true;

        await engine.ExecuteToListAsync(
            "func traced() {\n" +
            "  var x = 42\n" +
            "  echo $x\n" +
            "}\n" +
            "traced");

        var output = errorOutput.ToString();
        // Script trace should emit lines with "+" prefix
        Assert.Contains("+", output);
        // Should mention the variable declaration or echo
        Assert.True(output.Contains("var x") || output.Contains("echo"), $"Expected trace lines, got: {output}");
    }

    [Fact]
    public async Task ScriptTrace_disabled_produces_no_trace_output()
    {
        var errorOutput = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, errorOutput);
        var engine = new ToshEngine(runtime);

        runtime.Config.Shell.ScriptTrace = false;

        await engine.ExecuteToListAsync(
            "func quiet() {\n" +
            "  var x = 42\n" +
            "  echo $x\n" +
            "}\n" +
            "quiet");

        var output = errorOutput.ToString();
        Assert.DoesNotContain("+", output);
    }
}
