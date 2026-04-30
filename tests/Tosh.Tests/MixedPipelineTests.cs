using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class MixedPipelineTests
{
    [Fact]
    public async Task Null_values_serialize_to_empty_string_for_external_commands()
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
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("./" + commandName + " <(echo null)");

        Assert.Single(results);
        Assert.Equal(string.Empty, results[0]?.ToString());
    }

    [Fact]
    public async Task Mixed_types_serialize_through_external_command_pipeline()
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
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("echo 42 true hello | ./" + commandName);

        Assert.Equal(["42", "true", "hello"], results.Select(r => r?.ToString()!).ToArray());
    }

    [Fact]
    public async Task Shell_to_shell_pipeline_preserves_typed_objects()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        // echo 42 → type-of → get Name should show Int32 (the object stays typed in shell-to-shell pipes)
        var results = await engine.ExecuteToListAsync("echo 42 | type-of | get Name");

        Assert.Equal("Int32", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task External_to_shell_pipeline_converts_text_lines()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "emit-lines",
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
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("./" + commandName + " | type-of");
        var typeNames = results.Select(r => r?.ToString()!).ToArray();

        Assert.All(typeNames, name => Assert.Equal("Tosh.Runtime.ShellTextLine", name));
    }

    [Fact]
    public async Task Shell_to_external_to_shell_pipeline_round_trips()
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
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("echo hello world | ./" + commandName + " | type-of");
        var typeNames = results.Select(r => r?.ToString()!).ToArray();

        Assert.Equal(2, typeNames.Length);
        Assert.All(typeNames, name => Assert.Equal("Tosh.Runtime.ShellTextLine", name));
    }

    [Fact]
    public async Task Multiple_external_commands_in_pipeline()
    {
        using var tempDirectory = new TemporaryDirectory();
        var producer = CreateScript(
            tempDirectory.Path,
            "producer",
            unixBody:
            """
            printf 'beta\nalpha\n'
            """,
            windowsBody:
            """
            @echo beta
            @echo alpha
            """);
        var sorter = CreateScript(
            tempDirectory.Path,
            "sorter",
            unixBody:
            """
            sort
            """,
            windowsBody:
            """
            @powershell -NoProfile -Command "$input | Sort-Object"
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync($"./{producer} | ./{sorter}");

        Assert.Equal(["alpha", "beta"], results.Select(r => r?.ToString()!).ToArray());
    }

    [Fact]
    public async Task Empty_pipeline_input_to_external_command()
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
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("echo 1 2 3 | where $_ > 100 | ./" + commandName);
        Assert.Empty(results);
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-mixed-pipeline-tests-{Guid.NewGuid():N}");
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
