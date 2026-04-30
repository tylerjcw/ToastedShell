using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class ExtensionCommandTests
{
    [Fact]
    public async Task Var_alias_and_get_projection_commands_work()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var variableResults = await engine.ExecuteToListAsync("var greeting = \"hello\"\necho $greeting");
        var projectionResults = await engine.ExecuteToListAsync(
            "echo \"{\\\"name\\\":\\\"alpha\\\",\\\"size\\\":1}\" | from json | get { name, size } | rename name Name size Bytes");

        Assert.Collection(variableResults, item => Assert.Equal("hello", item));

        var projection = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(projectionResults));
        Assert.True(projection.TryGetValue("Name", out var name));
        Assert.True(projection.TryGetValue("Bytes", out var bytes));
        Assert.Equal("alpha", name);
        Assert.Equal(1L, bytes);
    }

    [Fact]
    public async Task Take_while_skip_while_and_tee_can_shape_and_capture_pipelines()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var takeResults = await engine.ExecuteToListAsync("echo 1 2 3 1 | take-while (_ < 3) and not (_ == 0)");
        var skipResults = await engine.ExecuteToListAsync("echo 1 2 3 1 | skip-while (_ < 3) or (_ == 0)");
        var teeResults = await engine.ExecuteToListAsync("echo one two | tee -v saved");

        Assert.Equal(["1", "2"], takeResults.Select(item => item?.ToString() ?? string.Empty).ToArray());
        Assert.Equal(["3", "1"], skipResults.Select(item => item?.ToString() ?? string.Empty).ToArray());
        Assert.Equal(["one", "two"], teeResults.Select(item => item?.ToString() ?? string.Empty).ToArray());

        var captured = Assert.IsType<object[]>(runtime.Variables["saved"]);
        Assert.Equal(["one", "two"], captured.Select(item => item?.ToString() ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task Text_shaping_commands_work_together()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var splitResults = await engine.ExecuteToListAsync("echo \"alpha,beta,gamma\" | split \",\"");
        var replaceResults = await engine.ExecuteToListAsync("echo alpha-beta | replace beta BETA");
        var joinedResults = await engine.ExecuteToListAsync("echo one two | join-lines \", \"");
        var matchResults = await engine.ExecuteToListAsync("echo \"PID=42\" | match \"PID=(?<Pid>[0-9]+)\" | get Pid");
        var templateResults = await engine.ExecuteToListAsync("echo \"{\\\"name\\\":\\\"toast\\\",\\\"size\\\":2}\" | from json | template \"{{ name }}: {{ size }}\"");
        var hashResults = await engine.ExecuteToListAsync("echo hello | hash sha256");

        Assert.Equal(["alpha", "beta", "gamma"], splitResults.Cast<ShellTextLine>().Select(item => item.Text).ToArray());
        Assert.Equal("alpha-BETA", Assert.IsType<ShellTextLine>(Assert.Single(replaceResults)).Text);
        Assert.Equal("one, two", Assert.IsType<ShellTextLine>(Assert.Single(joinedResults)).Text);
        Assert.Equal("42", Assert.Single(matchResults));
        Assert.Equal("toast: 2", Assert.IsType<ShellTextLine>(Assert.Single(templateResults)).Text);

        var hashProjection = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(hashResults));
        Assert.True(hashProjection.TryGetValue("Algorithm", out var algorithm));
        Assert.True(hashProjection.TryGetValue("Hash", out var hash));
        Assert.Equal("SHA256", algorithm);
        Assert.Equal(64, Assert.IsType<string>(hash).Length);
    }

    [Fact]
    public async Task Text_regex_commands_support_regex_mode_and_regex_objects()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var splitResults = await engine.ExecuteToListAsync("echo \"alpha,beta;gamma\" | split -r \"[,;]\"");
        var replaceResults = await engine.ExecuteToListAsync("echo \"A1 B2\" | replace -r \"[0-9]\" \"#\"");
        var matchResults = await engine.ExecuteToListAsync("echo \"PID=42\" | match (new regex(\"PID=(?<Pid>[0-9]+)\")) | get Pid");

        Assert.Equal(["alpha", "beta", "gamma"], splitResults.Cast<ShellTextLine>().Select(item => item.Text).ToArray());
        Assert.Equal("A# B#", Assert.IsType<ShellTextLine>(Assert.Single(replaceResults)).Text);
        Assert.Equal(["42"], matchResults.Cast<string>().ToArray());
    }

    [Fact]
    public async Task File_system_helper_commands_work()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var filePath = Path.Combine(temporaryDirectory.Path, "alpha.txt");
        await File.WriteAllTextAsync(filePath, "alpha");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var existsResults = await engine.ExecuteToListAsync($"exists {Quote(filePath)}");
        var fileResults = await engine.ExecuteToListAsync($"is-file {Quote(filePath)}");
        var dirResults = await engine.ExecuteToListAsync($"is-dir {Quote(temporaryDirectory.Path)}");
        var tempDirectoryResults = await engine.ExecuteToListAsync("mkdir-temp test-shell");
        var tempFileResults = await engine.ExecuteToListAsync("tempfile test-shell txt");
        var materializedTextResults = await engine.ExecuteToListAsync("echo alpha beta | as-file text");
        var materializedJsonResults = await engine.ExecuteToListAsync("echo \"{\\\"name\\\":\\\"toast\\\"}\" | from json | as-file json");
        var materializedCsvResults = await engine.ExecuteToListAsync("echo \"{\\\"name\\\":\\\"toast\\\",\\\"size\\\":2}\" | from json | as-file csv");

        Assert.Equal(true, Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(existsResults))["Exists"]);
        Assert.Equal(true, Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(fileResults))["IsFile"]);
        Assert.Equal(true, Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(dirResults))["IsDirectory"]);
        Assert.IsType<FileSystemEntry>(Assert.Single(tempDirectoryResults));
        Assert.IsType<FileSystemEntry>(Assert.Single(tempFileResults));
        Assert.Equal(".txt", Assert.IsType<FileSystemEntry>(Assert.Single(materializedTextResults)).Extension);
        Assert.Equal(".json", Assert.IsType<FileSystemEntry>(Assert.Single(materializedJsonResults)).Extension);
        Assert.Equal(".csv", Assert.IsType<FileSystemEntry>(Assert.Single(materializedCsvResults)).Extension);
    }

    [Fact]
    public async Task Clr_cast_and_reflection_commands_work()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var castResults = await engine.ExecuteToListAsync("echo 42 | cast int | type-of");
        var typeResults = await engine.ExecuteToListAsync("describe-type string | get Name");
        var memberResults = await engine.ExecuteToListAsync("members string | where _.Name == Length | first | get Kind");
        var methodResults = await engine.ExecuteToListAsync("methods string | where _.Name == Contains | first | get ReturnType");
        var constructorResults = await engine.ExecuteToListAsync("constructors System.Text.StringBuilder | count");
        var catalogResults = await engine.ExecuteToListAsync("types System.String | where _.FullName == System.String | first | get Name");

        Assert.Collection(castResults, item => Assert.Equal(typeof(int), item));
        Assert.Collection(typeResults, item => Assert.Equal("String", item));
        Assert.Collection(memberResults, item => Assert.Equal("Property", item));
        Assert.Collection(methodResults, item => Assert.Equal("System.Boolean", item));
        Assert.Collection(constructorResults, item => Assert.True(Convert.ToInt32(item) > 0));
        Assert.Collection(catalogResults, item => Assert.Equal("String", item));
    }

    [Fact]
    public async Task Load_assembly_accepts_pipeline_paths()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var dllPath = Path.Combine(projectRoot, "src", "Tosh.Cli", "bin", "Debug", "net10.0", "Tosh.Cli.dll");

        var results = await engine.ExecuteToListAsync($"echo {Quote(dllPath)} | load-assembly | get Name");

        Assert.Collection(results, item => Assert.Equal("Tosh.Cli", item));
    }

    [Fact]
    public async Task Shell_state_commands_manage_functions_environment_and_history()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime) { IsInteractiveSession = true };
        var variableName = $"TOSH_TEST_{Guid.NewGuid():N}";

        await engine.ExecuteToListAsync("func tosh_wrapper => echo hi");
        await engine.ExecuteToListAsync("func tosh_func() { echo hi }");

        var forgetWrapperResults = await engine.ExecuteToListAsync("forget tosh_wrapper");
        var unsetFunctionResults = await engine.ExecuteToListAsync("forget tosh_func");
        await engine.ExecuteToListAsync($"export {variableName} = toast");
        var unsetResults = await engine.ExecuteToListAsync($"forget {variableName}");

        runtime.RecordHistory("ls -la");
        runtime.RecordHistory("echo hello");
        var historyResults = await engine.ExecuteToListAsync("history-search echo | get Text");

        Assert.Equal(true, Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(forgetWrapperResults))["RemovedCommand"]);
        Assert.Equal("Function", Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(forgetWrapperResults))["CommandKind"]);
        Assert.Equal(true, Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(unsetFunctionResults))["RemovedCommand"]);
        Assert.Equal("Function", Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(unsetFunctionResults))["CommandKind"]);
        Assert.Null(Environment.GetEnvironmentVariable(variableName));
        Assert.Equal(true, Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(unsetResults))["RemovedEnvironment"]);
        Assert.Collection(historyResults, item => Assert.Equal("echo hello", item));

        var localForgetResults = await engine.ExecuteToListAsync("""
            func clear_local() {
                var temp = "value"
                forget temp
                try { echo $temp } catch { echo missing }
            }
            clear_local
            """);

        Assert.IsAssignableFrom<IDictionary<string, object?>>(localForgetResults[0]);
        Assert.Equal("missing", localForgetResults[1]);
    }

    [Fact]
    public async Task Discovery_commands_accept_pipeline_input()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime) { IsInteractiveSession = true };

        runtime.RecordHistory("echo hello world");
        runtime.RecordHistory("ls -la");

        var whichResults = await engine.ExecuteToListAsync("echo help clear | which | where _.Kind == BuiltIn | get Name");
        var envResults = await engine.ExecuteToListAsync("echo PATH | env | get Name");
        var typeResults = await engine.ExecuteToListAsync("echo map | types | where _.Name == dict | first | get Name");
        var historyResults = await engine.ExecuteToListAsync("echo hello | history-search | get Text");

        Assert.Equal(["help", "clear"], whichResults.Cast<string>().ToArray());
        Assert.Collection(envResults, item => Assert.Equal("PATH", item));
        Assert.Collection(typeResults, item => Assert.Equal("dict", item));
        Assert.Collection(historyResults, item => Assert.Equal("echo hello world", item));
    }

    private static string Quote(string path)
    {
        return "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-extension-tests-{Guid.NewGuid():N}");
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
