using Tosh.Core;
using Tosh.Language;
using Tosh.Language.Parsing;

namespace Tosh.Tests;

public sealed class SubcommandTests
{
    [Fact]
    public void Parser_parses_subcommand_with_name_and_block()
    {
        var result = ToshParser.Parse(
            """
            subcommand greet {
                arg name: string
                writeline "hi"
            }
            """);

        Assert.Empty(result.Diagnostics);
        var script = Assert.IsType<ScriptStatementSyntax>(result.Statement);
        var sub = Assert.IsType<SubcommandStatementSyntax>(Assert.Single(script.Statements));
        Assert.Equal("greet", sub.Name);
        Assert.Equal(SubcommandModifier.None, sub.Modifiers);
        Assert.Equal(2, sub.Body.Statements.Count);
    }

    [Fact]
    public void Parser_accepts_subcmd_alias()
    {
        var result = ToshParser.Parse("subcmd x { writeline \"y\" }");
        Assert.Empty(result.Diagnostics);
        var script = Assert.IsType<ScriptStatementSyntax>(result.Statement);
        var sub = Assert.IsType<SubcommandStatementSyntax>(Assert.Single(script.Statements));
        Assert.Equal("x", sub.Name);
    }

    [Fact]
    public void Parser_accepts_modifier_stack()
    {
        var result = ToshParser.Parse("eager hidden vital subcommand x { }");
        Assert.Empty(result.Diagnostics);
        var script = Assert.IsType<ScriptStatementSyntax>(result.Statement);
        var sub = Assert.IsType<SubcommandStatementSyntax>(Assert.Single(script.Statements));
        Assert.Equal(
            SubcommandModifier.Eager | SubcommandModifier.Hidden | SubcommandModifier.Vital,
            sub.Modifiers);
    }

    [Fact]
    public void Parser_rejects_eager_plus_hollow()
    {
        var result = ToshParser.Parse("eager hollow subcommand x { }");
        Assert.Contains(result.Diagnostics, d => d.Code == "tosh.parser.incompatible_subcommand_modifiers");
    }

    [Fact]
    public void Parser_rejects_hollow_body_with_non_subcommand_statements()
    {
        var result = ToshParser.Parse(
            """
            hollow subcommand ns {
                writeline "nope"
                subcommand ok { }
            }
            """);
        Assert.Contains(result.Diagnostics, d => d.Code == "tosh.parser.hollow_subcommand_must_be_empty");
    }

    [Fact]
    public async Task Dispatch_routes_to_matching_subcommand()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        runtime.InvocationArguments = ["greet", "World"];
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            subcommand greet {
                arg name: string
                writeline $"hi-{$name}"
            }
            """);

        Assert.Equal($"hi-World{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task Nested_dispatch_routes_through_parent_to_child()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        runtime.InvocationArguments = ["math", "add", "3", "4"];
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            subcommand math {
                subcommand add {
                    args(a: int, b: int)
                    writeline ($a + $b)
                }
            }
            """);

        Assert.Equal($"7{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task Top_level_setup_runs_before_dispatch()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        runtime.InvocationArguments = ["run"];
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            writeline "setup"
            subcommand run { writeline "leaf" }
            """);

        Assert.Equal(
            $"setup{Environment.NewLine}leaf{Environment.NewLine}",
            output.ToString());
    }

    [Fact]
    public async Task Eager_parent_runs_when_child_is_invoked()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        runtime.InvocationArguments = ["build", "release"];
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            eager subcommand build {
                writeline "build-setup"
                subcommand release { writeline "release" }
            }
            """);

        Assert.Equal(
            $"build-setup{Environment.NewLine}release{Environment.NewLine}",
            output.ToString());
    }

    [Fact]
    public async Task Non_eager_parent_body_is_skipped_when_child_is_invoked()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        runtime.InvocationArguments = ["build", "release"];
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            subcommand build {
                writeline "build-default"
                subcommand release { writeline "release" }
            }
            """);

        Assert.Equal($"release{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task Bare_parent_with_body_runs_its_body_when_no_child_picked()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        runtime.InvocationArguments = ["build"];
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            subcommand build {
                writeline "build-default"
                subcommand release { writeline "release" }
            }
            """);

        Assert.Equal($"build-default{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task Global_flag_bound_before_subcommand_name()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        runtime.InvocationArguments = ["--verbose", "run"];
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            flag verbose: bool = false
            subcommand run { writeline $verbose }
            """);

        Assert.Equal($"true{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task Global_flag_also_accepted_after_subcommand_name()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        runtime.InvocationArguments = ["run", "--verbose"];
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            flag verbose: bool = false
            subcommand run { writeline $verbose }
            """);

        Assert.Equal($"true{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task Local_flag_before_subcommand_name_is_unknown()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.InvocationArguments = ["--loud", "run"];
        var engine = new ToshEngine(runtime);

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
            await engine.ExecuteToListAsync(
                """
                subcommand run {
                    flag loud: bool = false
                    writeline $loud
                }
                """));

        Assert.Equal("tosh.runtime.unknown_script_flag", ex.Diagnostics[0].Code);
        Assert.Null(ex.Diagnostics[0].Span);
        Assert.Contains("choose a subcommand first", ex.Diagnostics[0].Help, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Vital_parent_without_child_throws()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.InvocationArguments = ["math"];
        var engine = new ToshEngine(runtime);

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
            await engine.ExecuteToListAsync(
                """
                vital subcommand math {
                    subcommand add { writeline "add" }
                }
                """));

        Assert.Equal("tosh.runtime.subcommand_required", ex.Diagnostics[0].Code);
        Assert.Null(ex.Diagnostics[0].Span);
        Assert.Contains("usage:", ex.Diagnostics[0].Help, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hidden_children_are_omitted_from_auto_help()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        runtime.InvocationArguments = ["--help"];
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            subcommand visible { writeline "v" }
            hidden subcommand secret { writeline "s" }
            """);

        var text = output.ToString();
        Assert.Contains("visible", text);
        Assert.DoesNotContain("secret", text);
    }

    [Fact]
    public async Task User_declared_help_flag_suppresses_auto_help()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        runtime.InvocationArguments = ["--help"];
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            flag help: bool = false
            subcommand run { writeline "leaf" }
            if ($help) { writeline "USER-HELP"; return }
            """);

        Assert.Contains("USER-HELP", output.ToString());
        Assert.DoesNotContain("Usage:", output.ToString());
    }

    [Fact]
    public async Task Duplicate_subcommand_at_same_level_throws()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.InvocationArguments = ["foo"];
        var engine = new ToshEngine(runtime);

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
            await engine.ExecuteToListAsync(
                """
                subcommand foo { writeline "a" }
                subcommand foo { writeline "b" }
                """));

        Assert.Equal("tosh.runtime.duplicate_subcommand", ex.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Dash_dash_separator_passes_flag_looking_tokens_as_positional()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        runtime.InvocationArguments = ["run", "--", "--not-a-flag"];
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            subcommand run {
                arg value: string
                writeline $value
            }
            """);

        Assert.Equal($"--not-a-flag{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task Arrow_form_with_parameter_list_binds_args_and_runs_pipeline()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        runtime.InvocationArguments = ["double", "5"];
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            subcmd double(n: int) => writeline ($n * 2)
            """);

        Assert.Equal($"10{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task Arrow_form_works_inside_nested_block()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        runtime.InvocationArguments = ["math", "double", "7"];
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            subcmd math {
                subcmd double(n: int) => writeline ($n * 2)
            }
            """);

        Assert.Equal($"14{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Parser_rejects_params_without_arrow_body()
    {
        var result = ToshParser.Parse("subcmd greet(name: string) { writeline $name }");
        Assert.Contains(result.Diagnostics, d => d.Code == "tosh.parser.subcommand_params_require_arrow");
    }

    [Fact]
    public async Task Default_arg_value_is_used_when_missing()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        runtime.InvocationArguments = ["fib"];
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            subcommand fib {
                arg n: int = 7
                writeline $n
            }
            """);

        Assert.Equal($"7{Environment.NewLine}", output.ToString());
    }
}
