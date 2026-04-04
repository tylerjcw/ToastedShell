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
}
