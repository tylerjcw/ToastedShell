using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class MixedPipelineTests
{
    [Fact]
    public async Task Null_values_serialize_to_empty_string_for_external_commands()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        // echo null pipes null to /bin/cat which serializes it as empty string
        var results = await engine.ExecuteToListAsync("echo null | /bin/cat");

        // null becomes empty string via ExternalTextSerializer → shows up as an empty line
        Assert.Single(results);
        Assert.Equal(string.Empty, results[0]?.ToString());
    }

    [Fact]
    public async Task Mixed_types_serialize_through_external_command_pipeline()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("echo 42 true hello | /bin/cat");

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
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("/bin/sh -c \"printf 'alpha\\nbeta\\n'\" | type-of");
        var typeNames = results.Select(r => r?.ToString()!).ToArray();

        // Each output line from the external command becomes a ShellTextLine, and type-of reports its type
        Assert.All(typeNames, name => Assert.Equal("Tosh.Core.ShellTextLine", name));
    }

    [Fact]
    public async Task Shell_to_external_to_shell_pipeline_round_trips()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        // echo → /bin/cat (object→text) → type-of (text→type info)
        var results = await engine.ExecuteToListAsync("echo hello world | /bin/cat | type-of");
        var typeNames = results.Select(r => r?.ToString()!).ToArray();

        Assert.Equal(2, typeNames.Length);
        Assert.All(typeNames, name => Assert.Equal("Tosh.Core.ShellTextLine", name));
    }

    [Fact]
    public async Task Multiple_external_commands_in_pipeline()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("/bin/sh -c \"printf 'beta\\nalpha\\n'\" | /usr/bin/sort");

        Assert.Equal(["alpha", "beta"], results.Select(r => r?.ToString()!).ToArray());
    }

    [Fact]
    public async Task Empty_pipeline_input_to_external_command()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        // where (empty filter) | /bin/cat — nothing to pipe
        var results = await engine.ExecuteToListAsync("echo 1 2 3 | where $_ > 100 | /bin/cat");
        Assert.Empty(results);
    }
}
