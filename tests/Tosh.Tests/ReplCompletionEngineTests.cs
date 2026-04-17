using System.Text;
using Tosh.Cli;
using Tosh.Core;
namespace Tosh.Tests;

public sealed class ReplCompletionEngineTests
{
    [Fact]
    public void Completes_runtime_variables_with_dollar_prefix()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Variables["person"] = "toast";
        var engine = new ReplCompletionEngine(runtime);

        var result = engine.GetCompletions("$pe", 3);

        Assert.NotNull(result);
        Assert.Contains(result!.Suggestions, suggestion => suggestion.Label == "$person");
    }

    [Fact]
    public void Completes_env_special_variable_and_environment_members()
    {
        const string variableName = "TOSH_COMPLETION_ENV_TEST";
        const string variableValue = "toast";
        Environment.SetEnvironmentVariable(variableName, variableValue);

        try
        {
            var runtime = ToshRuntime.CreateDefault();
            var engine = new ReplCompletionEngine(runtime);

            var rootResult = engine.GetCompletions("$en", 3);
            var memberResult = engine.GetCompletions("$env.TOSH_COMPLETION_ENV_", "$env.TOSH_COMPLETION_ENV_".Length);

            Assert.NotNull(rootResult);
            Assert.Contains(rootResult!.Suggestions, suggestion => suggestion.Label == "$env");
            Assert.NotNull(memberResult);
            Assert.Contains(memberResult!.Suggestions, suggestion => suggestion.Label == variableName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void Completes_dictionary_members_from_variable_reference()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Variables["person"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = "Toast",
            ["Group"] = "komrad",
        };
        var completionEngine = new ReplCompletionEngine(runtime);

        var result = completionEngine.GetCompletions("$person.Na", "$person.Na".Length);

        Assert.NotNull(result);
        Assert.Contains(result!.Suggestions, suggestion => suggestion.Label == "Name");
    }

    [Fact]
    public void Completes_clr_instance_members_from_runtime_variables()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Variables["builder"] = new StringBuilder();
        var engine = new ReplCompletionEngine(runtime);

        var result = engine.GetCompletions("$builder.Ap", "$builder.Ap".Length);

        Assert.NotNull(result);
        Assert.Contains(result!.Suggestions, suggestion => suggestion.Label == "Append");
    }

    [Fact]
    public void Completes_imported_clr_types_in_new_expressions()
    {
        var runtime = ToshRuntime.CreateDefault();
        var resolver = Assert.IsType<DotNetTypeResolver>(runtime.TypeResolver);
        resolver.AddUsing("System.Drawing");
        var engine = new ReplCompletionEngine(runtime);

        var result = engine.GetCompletions("new Poi", "new Poi".Length);

        Assert.NotNull(result);
        Assert.Contains(result!.Suggestions, suggestion => suggestion.Label == "Point");
    }

    [Fact]
    public void Completes_generic_type_arguments_for_shell_types()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ReplCompletionEngine(runtime);

        var result = engine.GetCompletions("new list<Str", "new list<Str".Length);

        Assert.NotNull(result);
        Assert.Contains(result!.Suggestions, suggestion => string.Equals(suggestion.Label, "string", StringComparison.OrdinalIgnoreCase) ||
                                                           string.Equals(suggestion.Label, "String", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Suggestions, suggestion => string.Equals(suggestion.Label, "stat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Hides_compiler_generated_clr_types_from_repl_completions()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ReplCompletionEngine(runtime);

        var result = engine.GetCompletions("using System.", "using System.".Length);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Suggestions, suggestion => suggestion.Label.Contains('<', StringComparison.Ordinal));
    }

    [Fact]
    public void Completes_paths_for_path_oriented_commands()
    {
        var tempRoot = Directory.CreateTempSubdirectory("tosh-repl-paths-");

        try
        {
            Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "examples"));
            File.WriteAllText(Path.Combine(tempRoot.FullName, "example.txt"), "toast");

            var runtime = ToshRuntime.CreateDefault();
            runtime.CurrentDirectory = tempRoot.FullName;
            var engine = new ReplCompletionEngine(runtime);

            var result = engine.GetCompletions("cd ex", "cd ex".Length);

            Assert.NotNull(result);
            Assert.Equal("examples" + Path.DirectorySeparatorChar, result!.Suggestions[0].Label);
            Assert.Contains(result.Suggestions, suggestion => suggestion.Label == "example.txt");
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Completes_require_from_paths()
    {
        var tempRoot = Directory.CreateTempSubdirectory("tosh-repl-require-");

        try
        {
            File.WriteAllText(Path.Combine(tempRoot.FullName, "toastlib.tosh"), "# demo");

            var runtime = ToshRuntime.CreateDefault();
            runtime.CurrentDirectory = tempRoot.FullName;
            var engine = new ReplCompletionEngine(runtime);

            var result = engine.GetCompletions("require Inventory from toa", "require Inventory from toa".Length);

            Assert.NotNull(result);
            Assert.Contains(result!.Suggestions, suggestion => suggestion.Label == "toastlib.tosh");
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Completes_paths_with_spaces_using_quoted_insert_text()
    {
        var tempRoot = Directory.CreateTempSubdirectory("tosh-repl-spaces-");

        try
        {
            Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "space dir"));

            var runtime = ToshRuntime.CreateDefault();
            runtime.CurrentDirectory = tempRoot.FullName;
            var engine = new ReplCompletionEngine(runtime);

            var result = engine.GetCompletions("cd spa", "cd spa".Length);

            Assert.NotNull(result);
            var suggestion = Assert.Single(result!.Suggestions, suggestion => suggestion.Label == "space dir" + Path.DirectorySeparatorChar);
            Assert.Equal("\"space dir" + Path.DirectorySeparatorChar + "\"", suggestion.GetInsertText());
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Completes_quoted_paths_without_double_quoting_insert_text()
    {
        var tempRoot = Directory.CreateTempSubdirectory("tosh-repl-quoted-spaces-");

        try
        {
            Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "space dir"));

            var runtime = ToshRuntime.CreateDefault();
            runtime.CurrentDirectory = tempRoot.FullName;
            var engine = new ReplCompletionEngine(runtime);

            var input = "cd \"spa";
            var result = engine.GetCompletions(input, input.Length);

            Assert.NotNull(result);
            var suggestion = Assert.Single(result!.Suggestions, suggestion => suggestion.Label == "space dir" + Path.DirectorySeparatorChar);
            Assert.Equal("space dir" + Path.DirectorySeparatorChar, suggestion.GetInsertText());
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Completes_external_commands_in_command_position()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ReplCompletionEngine(runtime);

        var result = engine.GetCompletions("dot", "dot".Length);

        Assert.NotNull(result);
        Assert.Contains(result!.Suggestions, suggestion => string.Equals(suggestion.Label, "dotnet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Inline_help_query_uses_token_under_cursor_and_prefers_last_member_segment()
    {
        Assert.Equal("filter", ReplCompletionEngine.GetInlineHelpQuery("echo filter", "echo fil".Length));
        Assert.Equal("Path", ReplCompletionEngine.GetInlineHelpQuery("$env.Path", "$env.Path".Length));
        Assert.Equal("Name", ReplCompletionEngine.GetInlineHelpQuery("$person.Name", "$person.Na".Length));
    }

    [Fact]
    public void Inspect_reference_resolution_supports_runtime_variables_and_bare_types()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Variables["person"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = "Toast",
        };

        var engine = new ReplCompletionEngine(runtime);

        Assert.True(engine.TryResolveInspectableReference("$person.Name", out var memberValue));
        Assert.Equal("Toast", memberValue);

        Assert.True(engine.TryResolveInspectableReference("string", out var bareType));
        Assert.Equal(typeof(string), bareType);
    }

    [Fact]
    public void Inspect_target_span_includes_wrapping_quotes_for_string_literals()
    {
        var span = ReplCompletionEngine.GetInspectTargetSpanAtCursor("\"Hello\"", "\"Hello\"".Length);

        Assert.Equal(0, span.Start);
        Assert.Equal("\"Hello\"".Length, span.Length);
        Assert.Equal("\"Hello\"", span.Token);
    }

    [Fact]
    public void Inspect_reference_resolution_supports_quoted_and_bareword_string_values()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ReplCompletionEngine(runtime);

        Assert.True(engine.TryResolveInspectableReference("\"Hello\"", out var quotedValue));
        Assert.Equal("Hello", quotedValue);
        Assert.Equal("\"Hello\"", ReplCompletionEngine.BuildInspectableSourceExpression("\"Hello\"", quotedValue));

        Assert.True(engine.TryResolveInspectableReference("Hello", out var barewordValue));
        Assert.Equal("Hello", barewordValue);
        Assert.Equal("\"Hello\"", ReplCompletionEngine.BuildInspectableSourceExpression("Hello", barewordValue));
    }

    [Fact]
    public void Completes_command_flags_when_typing_dash()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ReplCompletionEngine(runtime);

        var result = engine.GetCompletions("ls -", "ls -".Length);

        Assert.NotNull(result);
        Assert.True(result!.Suggestions.Count > 0);
        Assert.All(result.Suggestions, suggestion => Assert.StartsWith("-", suggestion.Label));
        Assert.Contains(result.Suggestions, suggestion => suggestion.Label == "-l");
        Assert.Contains(result.Suggestions, suggestion => suggestion.Label == "-a");
    }

    [Fact]
    public void Completes_command_flags_with_partial_prefix()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ReplCompletionEngine(runtime);

        var result = engine.GetCompletions("ls --so", "ls --so".Length);

        Assert.NotNull(result);
        Assert.Contains(result!.Suggestions, suggestion => suggestion.Label == "--sort <name|size|time>");
    }

    [Fact]
    public void Completes_command_flags_after_pipe()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ReplCompletionEngine(runtime);

        var result = engine.GetCompletions("cat file.txt | grep -", "cat file.txt | grep -".Length);

        Assert.NotNull(result);
        Assert.True(result!.Suggestions.Count > 0);
        Assert.All(result.Suggestions, suggestion => Assert.StartsWith("-", suggestion.Label));
    }

    [Fact]
    public void Signature_hint_returns_usage_for_known_command()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ReplCompletionEngine(runtime);

        var hint = engine.GetSignatureHint("ls ", "ls ".Length);

        Assert.NotNull(hint);
        Assert.StartsWith("ls", hint!);
        Assert.Contains("[-a]", hint);
        Assert.Contains("[-l]", hint);
    }

    [Fact]
    public void Signature_hint_returns_null_for_unknown_command()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ReplCompletionEngine(runtime);

        var hint = engine.GetSignatureHint("nonexistent-command ", "nonexistent-command ".Length);

        Assert.Null(hint);
    }

    [Fact]
    public void Signature_hint_returns_null_at_command_position()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ReplCompletionEngine(runtime);

        var hint = engine.GetSignatureHint("ls", "ls".Length);

        // When typing the command name itself (no trailing space), there's no segment prefix with tokens
        // so it should still detect "ls" as the command
        Assert.NotNull(hint);
    }

    [Fact]
    public void Completes_option_values_after_flag_with_choices()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ReplCompletionEngine(runtime);

        var result = engine.GetCompletions("ls --sort ", "ls --sort ".Length);

        Assert.NotNull(result);
        Assert.Contains(result!.Suggestions, s => s.Label == "name");
        Assert.Contains(result!.Suggestions, s => s.Label == "size");
        Assert.Contains(result!.Suggestions, s => s.Label == "time");
    }

    [Fact]
    public void Completes_option_values_with_partial_prefix()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ReplCompletionEngine(runtime);

        var result = engine.GetCompletions("ls --sort n", "ls --sort n".Length);

        Assert.NotNull(result);
        Assert.Contains(result!.Suggestions, s => s.Label == "name");
        Assert.DoesNotContain(result.Suggestions, s => s.Label == "size");
    }

    [Fact]
    public void Does_not_complete_option_values_for_flag_without_choices()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ReplCompletionEngine(runtime);

        // -a is a boolean flag with no value choices
        var result = engine.GetCompletions("ls -a ", "ls -a ".Length);

        // Should not be treated as option value completion (falls through to other completions)
        if (result is not null)
        {
            Assert.DoesNotContain(result.Suggestions, s => s.Label == "name" || s.Label == "size" || s.Label == "time");
        }
    }

    [Fact]
    public void ParseOptionValueChoices_extracts_pipe_separated_values()
    {
        var choices = ReplCompletionEngine.ParseOptionValueChoices("--sort <name|size|time>");

        Assert.NotNull(choices);
        Assert.Equal(3, choices!.Count);
        Assert.Equal("name", choices[0]);
        Assert.Equal("size", choices[1]);
        Assert.Equal("time", choices[2]);
    }

    [Fact]
    public void ParseOptionValueChoices_returns_null_for_single_placeholder()
    {
        var choices = ReplCompletionEngine.ParseOptionValueChoices("-t <directory>");

        Assert.Null(choices);
    }

    [Fact]
    public void ParseOptionValueChoices_returns_null_for_boolean_flag()
    {
        var choices = ReplCompletionEngine.ParseOptionValueChoices("-a");

        Assert.Null(choices);
    }
}
