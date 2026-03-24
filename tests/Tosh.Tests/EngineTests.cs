using Tosh.Core;
using Tosh.Language;
using Tosh.Language.Parsing;

namespace Tosh.Tests;

public sealed class EngineTests
{
    [Fact]
    public void Parser_builds_a_pipeline_of_commands()
    {
        var result = ToshParser.Parse("echo hello | type-of");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Pipeline.Commands.Count);
        Assert.Equal("echo", result.Pipeline.Commands[0].Name);
        Assert.Equal("type-of", result.Pipeline.Commands[1].Name);

        var argument = Assert.IsType<BarewordArgumentSyntax>(result.Pipeline.Commands[0].Arguments[0]);
        Assert.Equal("hello", argument.Value);
    }

    [Fact]
    public void Parser_supports_parenthesized_subexpressions_as_arguments()
    {
        var result = ToshParser.Parse("where Modified < (date now | date sub (timespan 2d))");

        Assert.Empty(result.Diagnostics);
        var argument = Assert.IsType<SubexpressionArgumentSyntax>(result.Pipeline.Commands[0].Arguments[2]);
        Assert.Equal(2, argument.Pipeline.Commands.Count);
        Assert.Equal("date", argument.Pipeline.Commands[0].Name);
        Assert.Equal("date", argument.Pipeline.Commands[1].Name);
        var nestedArgument = Assert.IsType<SubexpressionArgumentSyntax>(argument.Pipeline.Commands[1].Arguments[1]);
        Assert.Single(nestedArgument.Pipeline.Commands);
        Assert.Equal("timespan", nestedArgument.Pipeline.Commands[0].Name);
    }

    [Fact]
    public void Parser_supports_operator_expressions_in_parentheses()
    {
        var result = ToshParser.Parse("echo ((date now) - (timespan 2d))");

        Assert.Empty(result.Diagnostics);
        var argument = Assert.IsType<OperatorArgumentSyntax>(result.Pipeline.Commands[0].Arguments[0]);
        Assert.Equal("-", argument.Operator);
        Assert.IsType<SubexpressionArgumentSyntax>(argument.Left);
        Assert.IsType<SubexpressionArgumentSyntax>(argument.Right);
    }

    [Fact]
    public void Parser_supports_variable_declarations_and_csharp_style_object_construction()
    {
        var result = ToshParser.Parse("var builder = new System.Text.StringBuilder(\"hello\") | call Append \" world\"");

        Assert.Empty(result.Diagnostics);

        var statement = Assert.IsType<VariableDeclarationStatementSyntax>(result.Statement);
        Assert.Equal("builder", statement.Name);
        Assert.Equal(2, statement.Value.Stages.Count);

        var expressionStage = Assert.IsType<ExpressionPipelineStageSyntax>(statement.Value.Stages[0]);
        var newObject = Assert.IsType<NewObjectArgumentSyntax>(expressionStage.Expression);
        Assert.Equal("System.Text.StringBuilder", newObject.TypeName);
        Assert.Collection(
            newObject.Arguments,
            argument => Assert.Equal("hello", Assert.IsType<LiteralArgumentSyntax>(argument).Value));

        Assert.Equal("call", Assert.IsType<CommandSyntax>(statement.Value.Stages[1]).Name);
    }

    [Fact]
    public void Parser_supports_alias_and_typed_function_definitions()
    {
        var aliasResult = ToshParser.Parse("alias ll = ls -la");
        var functionResult = ToshParser.Parse("def recent(days: TimeSpan) -> FileSystemEntry { ls -la | where Modified > ((date now) - $days) }");

        Assert.Empty(aliasResult.Diagnostics);
        var alias = Assert.IsType<AliasStatementSyntax>(aliasResult.Statement);
        Assert.Equal("ll", alias.Name);
        Assert.Single(alias.Value.Commands);

        Assert.Empty(functionResult.Diagnostics);
        var function = Assert.IsType<FunctionDefinitionStatementSyntax>(functionResult.Statement);
        Assert.Equal("recent", function.Name);
        Assert.Equal("FileSystemEntry", function.ReturnTypeName);
        Assert.Collection(
            function.Parameters,
            parameter =>
            {
                Assert.Equal("days", parameter.Name);
                Assert.Equal("TimeSpan", parameter.TypeName);
            });
    }

    [Fact]
    public void Parser_supports_return_statements_with_optional_values()
    {
        var bareResult = ToshParser.Parse("return");
        var valueResult = ToshParser.Parse("return String.Join(\" \", [\"Hello\", \"World\"])");

        Assert.Empty(bareResult.Diagnostics);
        var bareReturn = Assert.IsType<ReturnStatementSyntax>(bareResult.Statement);
        Assert.Null(bareReturn.Value);

        Assert.Empty(valueResult.Diagnostics);
        var valueReturn = Assert.IsType<ReturnStatementSyntax>(valueResult.Statement);
        Assert.NotNull(valueReturn.Value);
        Assert.Single(valueReturn.Value!.Stages);
    }

    [Fact]
    public void Parser_supports_break_continue_and_using_statements()
    {
        var usingResult = ToshParser.Parse("using System.IO = IO");
        var breakResult = ToshParser.Parse("break");
        var continueResult = ToshParser.Parse("continue");

        Assert.Empty(usingResult.Diagnostics);
        var usingStatement = Assert.IsType<UsingStatementSyntax>(usingResult.Statement);
        Assert.Equal("System.IO", usingStatement.Target);
        Assert.Equal("IO", usingStatement.Alias);

        Assert.Empty(breakResult.Diagnostics);
        Assert.IsType<BreakStatementSyntax>(breakResult.Statement);

        Assert.Empty(continueResult.Diagnostics);
        Assert.IsType<ContinueStatementSyntax>(continueResult.Statement);
    }

    [Fact]
    public void Parser_supports_newline_separated_top_level_statements()
    {
        var result = ToshParser.Parse("alias ll = ls -la\ndef recent(days: TimeSpan) -> FileSystemEntry { ls -la | where Modified > ((date now) - $days) }");

        Assert.Empty(result.Diagnostics);
        var script = Assert.IsType<ScriptStatementSyntax>(result.Statement);
        Assert.Collection(
            script.Statements,
            statement => Assert.IsType<AliasStatementSyntax>(statement),
            statement => Assert.IsType<FunctionDefinitionStatementSyntax>(statement));
    }

    [Fact]
    public void Parser_preserves_command_arguments_after_a_newline_statement_boundary()
    {
        var result = ToshParser.Parse("alias ll = ls -la\nwhich ll | get Kind");

        Assert.Empty(result.Diagnostics);
        var script = Assert.IsType<ScriptStatementSyntax>(result.Statement);
        var pipeline = Assert.IsType<PipelineStatementSyntax>(script.Statements[1]);
        var getCommand = Assert.IsType<CommandSyntax>(pipeline.Pipeline.Stages[1]);
        var kindArgument = Assert.IsType<BarewordArgumentSyntax>(Assert.Single(getCommand.Arguments));

        Assert.Equal("get", getCommand.Name);
        Assert.Equal("Kind", kindArgument.Value);
    }

    [Fact]
    public void Parser_supports_member_access_and_fluent_method_call_expressions()
    {
        var result = ToshParser.Parse("echo $builder.Append(\" world\").ToString()");

        Assert.Empty(result.Diagnostics);

        var command = Assert.Single(result.Pipeline.Commands);
        var toStringCall = Assert.IsType<MethodCallArgumentSyntax>(Assert.Single(command.Arguments));
        Assert.Equal("ToString", toStringCall.MethodName);
        Assert.Empty(toStringCall.Arguments);

        var appendCall = Assert.IsType<MethodCallArgumentSyntax>(toStringCall.Target);
        Assert.Equal("Append", appendCall.MethodName);
        Assert.Collection(
            appendCall.Arguments,
            argument => Assert.Equal(" world", Assert.IsType<LiteralArgumentSyntax>(argument).Value));

        Assert.Equal("builder", Assert.IsType<VariableReferenceArgumentSyntax>(appendCall.Target).Name);
    }

    [Fact]
    public void Parser_supports_static_method_calls_with_list_literals()
    {
        var result = ToshParser.Parse("echo String.Join(\" \", [\"Hello\", \"World\"])");

        Assert.Empty(result.Diagnostics);

        var command = Assert.Single(result.Pipeline.Commands);
        var staticCall = Assert.IsType<StaticMethodCallArgumentSyntax>(Assert.Single(command.Arguments));
        Assert.Equal("String.Join", staticCall.Path);
        Assert.Equal(" ", Assert.IsType<LiteralArgumentSyntax>(staticCall.Arguments[0]).Value);

        var list = Assert.IsType<ListLiteralArgumentSyntax>(staticCall.Arguments[1]);
        Assert.Collection(
            list.Items,
            item => Assert.Equal("Hello", Assert.IsType<LiteralArgumentSyntax>(item).Value),
            item => Assert.Equal("World", Assert.IsType<LiteralArgumentSyntax>(item).Value));
    }

    [Fact]
    public void Parser_supports_static_member_access_expressions()
    {
        var result = ToshParser.Parse("echo DateTime.Now");

        Assert.Empty(result.Diagnostics);

        var command = Assert.Single(result.Pipeline.Commands);
        var staticAccess = Assert.IsType<StaticMemberAccessArgumentSyntax>(Assert.Single(command.Arguments));
        Assert.Equal("DateTime.Now", staticAccess.Path);
    }

    [Fact]
    public void Parser_supports_where_predicate_expressions_with_implicit_current_item_access()
    {
        var result = ToshParser.Parse("ls | where Name.ToString().ToLower().Contains(\".x\") | get Name");

        Assert.Empty(result.Diagnostics);

        var whereCommand = result.Pipeline.Commands[1];
        var predicate = Assert.IsType<BlockArgumentSyntax>(Assert.Single(whereCommand.Arguments));
        var statement = Assert.IsType<PipelineStatementSyntax>(Assert.Single(predicate.Block.Statements));
        var stage = Assert.IsType<ExpressionPipelineStageSyntax>(Assert.Single(statement.Pipeline.Stages));
        var containsCall = Assert.IsType<MethodCallArgumentSyntax>(stage.Expression);
        Assert.Equal("Contains", containsCall.MethodName);

        var lowerCall = Assert.IsType<MethodCallArgumentSyntax>(containsCall.Target);
        Assert.Equal("ToLower", lowerCall.MethodName);

        var toStringCall = Assert.IsType<MethodCallArgumentSyntax>(lowerCall.Target);
        Assert.Equal("ToString", toStringCall.MethodName);

        var nameAccess = Assert.IsType<MemberAccessArgumentSyntax>(toStringCall.Target);
        Assert.Equal("Name", nameAccess.MemberPath);
        Assert.Equal("it", Assert.IsType<VariableReferenceArgumentSyntax>(nameAccess.Target).Name);
    }

    [Fact]
    public void Parser_supports_each_blocks_with_expression_statements()
    {
        var result = ToshParser.Parse("ls | each { $it.Name; $it.Extension }");

        Assert.Empty(result.Diagnostics);

        var eachCommand = result.Pipeline.Commands[1];
        Assert.Equal("each", eachCommand.Name);
        var block = Assert.IsType<BlockArgumentSyntax>(Assert.Single(eachCommand.Arguments));
        Assert.Equal(2, block.Block.Statements.Count);

        var firstStatement = Assert.IsType<PipelineStatementSyntax>(block.Block.Statements[0]);
        var firstStage = Assert.IsType<ExpressionPipelineStageSyntax>(Assert.Single(firstStatement.Pipeline.Stages));
        var memberAccess = Assert.IsType<MemberAccessArgumentSyntax>(firstStage.Expression);
        Assert.Equal("Name", memberAccess.MemberPath);
        Assert.Equal("it", Assert.IsType<VariableReferenceArgumentSyntax>(memberAccess.Target).Name);
    }

    [Fact]
    public void Parser_supports_newline_separated_block_statements()
    {
        var result = ToshParser.Parse(
            """
            ls | each {
                $it.Name
                $it.Extension
            }
            """);

        Assert.Empty(result.Diagnostics);

        var eachCommand = result.Pipeline.Commands[1];
        var block = Assert.IsType<BlockArgumentSyntax>(Assert.Single(eachCommand.Arguments));
        Assert.Equal(2, block.Block.Statements.Count);
    }

    [Fact]
    public void Parser_supports_get_member_projection_blocks()
    {
        var result = ToshParser.Parse("ps | get { Name, PID, Memory }");

        Assert.Empty(result.Diagnostics);

        var getCommand = result.Pipeline.Commands[1];
        var projection = Assert.IsType<MemberProjectionArgumentSyntax>(Assert.Single(getCommand.Arguments));
        Assert.Equal(new[] { "Name", "PID", "Memory" }, projection.MemberPaths.ToArray());
    }

    [Fact]
    public void Parser_supports_if_statements_with_else_if_and_else_blocks()
    {
        var result = ToshParser.Parse("if ($flag) { echo yes } else if (false) { echo maybe } else { echo no }");

        Assert.Empty(result.Diagnostics);

        var statement = Assert.IsType<IfStatementSyntax>(result.Statement);
        Assert.IsType<VariableReferenceArgumentSyntax>(statement.Condition);
        Assert.Single(statement.ThenBlock.Statements);

        var elseBlock = Assert.IsType<BlockSyntax>(statement.ElseBlock);
        var nestedIf = Assert.IsType<IfStatementSyntax>(Assert.Single(elseBlock.Statements));
        Assert.NotNull(nestedIf.ElseBlock);
    }

    [Fact]
    public void Parser_supports_for_loops_with_parenthesized_pipeline_sources()
    {
        var result = ToshParser.Parse("for file in (ls | first 2) { echo $file.Name }");

        Assert.Empty(result.Diagnostics);

        var statement = Assert.IsType<ForStatementSyntax>(result.Statement);
        Assert.Equal("file", statement.VariableName);
        Assert.Equal(2, statement.Source.Stages.Count);
        Assert.Single(statement.Body.Statements);
    }

    [Fact]
    public void Parser_supports_while_loops_with_blocks()
    {
        var result = ToshParser.Parse("while (($count < 3)) { echo $count; count = ($count + 1) }");

        Assert.Empty(result.Diagnostics);

        var statement = Assert.IsType<WhileStatementSyntax>(result.Statement);
        Assert.IsType<OperatorArgumentSyntax>(statement.Condition);
        Assert.Equal(2, statement.Body.Statements.Count);
    }

    [Fact]
    public async Task Type_of_returns_the_runtime_type_for_each_object()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo 42 true hello | type-of");

        Assert.Collection(
            results,
            item => Assert.Equal(typeof(int), item),
            item => Assert.Equal(typeof(bool), item),
            item => Assert.Equal(typeof(string), item));
    }

    [Fact]
    public async Task Date_and_timespan_commands_return_typed_temporal_objects()
    {
        var engine = new ToshEngine();

        var dateResults = await engine.ExecuteToListAsync("date parse 2026-03-23T14:05:00Z | type-of");
        var spanResults = await engine.ExecuteToListAsync("timespan 1w2d4h5m23s | type-of");

        Assert.Collection(dateResults, item => Assert.Equal(typeof(DateTimeOffset), item));
        Assert.Collection(spanResults, item => Assert.Equal(typeof(TimeSpan), item));
    }

    [Fact]
    public async Task Operator_expressions_can_produce_typed_date_results()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo ((date now) - (timespan 2d)) | type-of");

        Assert.Collection(results, item => Assert.Equal(typeof(DateTimeOffset), item));
    }

    [Fact]
    public async Task Can_construct_and_invoke_dotnet_objects()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("new System.Text.StringBuilder hello | call Append world | call ToString");

        Assert.Collection(results, item => Assert.Equal("helloworld", item));
    }

    [Fact]
    public async Task Variables_can_bind_objects_from_csharp_style_new_expressions()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var declarationResults = await engine.ExecuteToListAsync("var builder = new System.Text.StringBuilder(\"hello\")");
        var results = await engine.ExecuteToListAsync("$builder | call Append \" world\" | call ToString");

        Assert.Empty(declarationResults);
        Assert.Collection(results, item => Assert.Equal("hello world", item));
    }

    [Fact]
    public async Task Variables_can_be_reassigned_after_declaration()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("var answer = 1");
        var assignmentResults = await engine.ExecuteToListAsync("answer = 2");
        var results = await engine.ExecuteToListAsync("echo $answer");

        Assert.Empty(assignmentResults);
        Assert.Collection(results, item => Assert.Equal(2, item));
    }

    [Fact]
    public async Task Variables_can_capture_pipeline_output_and_replay_it_as_pipeline_input()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "keep.txt"), "keep");
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "skip.log"), "skip");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("var files = ls | where Type == file");
        var results = await engine.ExecuteToListAsync("$files | get Name");

        Assert.Equal(
            new[] { "keep.txt", "skip.log" },
            results.Cast<string>().OrderBy(name => name).ToArray());
    }

    [Fact]
    public async Task Variable_member_access_can_project_properties_inside_expressions()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("var builder = new System.Text.StringBuilder(\"hello\")");
        var results = await engine.ExecuteToListAsync("echo $builder.Length");

        Assert.Collection(results, item => Assert.Equal(5, item));
    }

    [Fact]
    public async Task Fluent_method_call_expressions_can_chain_over_objects()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("var builder = new System.Text.StringBuilder(\"hello\")");
        var results = await engine.ExecuteToListAsync("echo $builder.Append(\" world\").ToString()");

        Assert.Collection(results, item => Assert.Equal("hello world", item));
    }

    [Fact]
    public async Task Fluent_expressions_can_start_with_new_object_construction()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo new System.Text.StringBuilder(\"hello\").Append(\" world\").Length");

        Assert.Collection(results, item => Assert.Equal(11, item));
    }

    [Fact]
    public async Task Literal_method_call_expressions_can_transform_strings()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo \"Hello\".ToLower()");

        Assert.Collection(results, item => Assert.Equal("hello", item));
    }

    [Fact]
    public async Task Static_method_call_expressions_can_use_list_literals()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo String.Join(\" \", [\"Hello\", \"World\"]).ToLower()");

        Assert.Collection(results, item => Assert.Equal("hello world", item));
    }

    [Fact]
    public async Task Static_member_access_expressions_can_resolve_dotnet_values()
    {
        var engine = new ToshEngine();

        var nowTypeResults = await engine.ExecuteToListAsync("echo DateTime.Now | type-of");
        var emptyLengthResults = await engine.ExecuteToListAsync("echo String.Empty.Length");

        Assert.Collection(nowTypeResults, item => Assert.Equal(typeof(DateTime), item));
        Assert.Collection(emptyLengthResults, item => Assert.Equal(0, item));
    }

    [Fact]
    public async Task Static_member_access_can_chain_into_instance_method_calls()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo DateTime.Now.AddDays(-2) | type-of");

        Assert.Collection(results, item => Assert.Equal(typeof(DateTime), item));
    }

    [Fact]
    public async Task Static_method_call_expressions_support_params_array_overloads()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo String.Join(\" \", \"Hello\", \"World\")");

        Assert.Collection(results, item => Assert.Equal("Hello World", item));
    }

    [Fact]
    public async Task Get_can_project_multiple_members_into_queryable_shell_records()
    {
        var engine = new ToshEngine();

        var projectedResults = await engine.ExecuteToListAsync(
            "echo new Tosh.Core.ProcessInfo(2, \"large\", false, null, null, 4096, null, null) new Tosh.Core.ProcessInfo(1, \"small\", false, null, null, 1024, null, null) | get { Name, PID, Memory }");
        var sortedNameResults = await engine.ExecuteToListAsync(
            "echo new Tosh.Core.ProcessInfo(2, \"large\", false, null, null, 4096, null, null) new Tosh.Core.ProcessInfo(1, \"small\", false, null, null, 1024, null, null) | get { Name, PID, Memory } | sort PID | get Name");

        var firstProjection = Assert.IsType<ProjectedObject>(projectedResults[0]);
        Assert.True(firstProjection.TryGetValue("Name", out var projectedName));
        Assert.True(firstProjection.TryGetValue("PID", out var projectedPid));
        Assert.True(firstProjection.TryGetValue("Memory", out var projectedMemory));
        Assert.Equal("large", projectedName);
        Assert.Equal(2, projectedPid);
        Assert.IsType<StorageSize>(projectedMemory);
        Assert.Equal(new[] { "small", "large" }, sortedNameResults.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Variables_can_store_results_of_static_method_calls()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("var someString = String.Join(\" \", [\"Hello\", \"World\"])");
        var results = await engine.ExecuteToListAsync("echo $someString");

        Assert.Collection(results, item => Assert.Equal("Hello World", item));
    }

    [Fact]
    public async Task Aliases_can_expand_commands_and_forward_call_arguments()
    {
        using var tempDirectory = new TemporaryDirectory();
        var nestedPath = System.IO.Path.Combine(tempDirectory.Path, "nested");
        Directory.CreateDirectory(nestedPath);
        File.WriteAllText(System.IO.Path.Combine(nestedPath, "keep.txt"), "keep");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("alias ll = ls -la");
        var results = await engine.ExecuteToListAsync("ll nested | get Name");

        Assert.Collection(results, item => Assert.Equal("keep.txt", item));
    }

    [Fact]
    public async Task Functions_can_bind_typed_parameters_and_process_pipeline_input()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "small.txt"), "tiny");
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "big.txt"), new string('x', 1501));

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("def bigger(size: StorageSize) { where Size >= $size }");
        var results = await engine.ExecuteToListAsync("ls -la | bigger 1kb | get Name");

        Assert.Collection(results, item => Assert.Equal("big.txt", item));
    }

    [Fact]
    public async Task Functions_can_convert_emitted_values_to_a_declared_return_type()
    {
        var engine = new ToshEngine();

        await engine.ExecuteToListAsync("def stringifyCount() -> String { count }");
        var results = await engine.ExecuteToListAsync("echo 1 2 3 | stringifyCount");

        Assert.Collection(results, item => Assert.Equal("3", item));
    }

    [Fact]
    public async Task Return_exits_functions_early_with_a_value()
    {
        var engine = new ToshEngine();

        await engine.ExecuteToListAsync("def choose() { return \"done\"; echo never }");
        var results = await engine.ExecuteToListAsync("choose");

        Assert.Collection(results, item => Assert.Equal("done", item));
    }

    [Fact]
    public async Task Return_can_forward_pipeline_values_from_the_current_function_input()
    {
        var engine = new ToshEngine();

        await engine.ExecuteToListAsync("def names() { return get Name }");
        var results = await engine.ExecuteToListAsync("ls -la | first 2 | names");

        Assert.Equal(2, results.Count);
        Assert.All(results, item => Assert.IsType<string>(item));
    }

    [Fact]
    public async Task Return_without_a_value_stops_function_execution()
    {
        var engine = new ToshEngine();

        await engine.ExecuteToListAsync("def stop() { return; echo never }");
        var results = await engine.ExecuteToListAsync("stop");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Each_executes_a_block_for_each_input_object()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo Hello World | each { $it.ToLower() }");

        Assert.Collection(
            results,
            item => Assert.Equal("hello", item),
            item => Assert.Equal("world", item));
    }

    [Fact]
    public async Task Return_inside_each_blocks_exits_the_enclosing_function()
    {
        var engine = new ToshEngine();

        await engine.ExecuteToListAsync("def firstLower() { echo Hello World | each { return $it.ToLower() }; echo never }");
        var results = await engine.ExecuteToListAsync("firstLower");

        Assert.Collection(results, item => Assert.Equal("hello", item));
    }

    [Fact]
    public async Task Each_blocks_support_continue()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo one skip two | each { if (($it == skip)) { continue }; echo $it }");

        Assert.Equal(new[] { "one", "two" }, results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Each_blocks_support_break()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo one two three | each { echo $it; break }");

        Assert.Collection(results, item => Assert.Equal("one", item));
    }

    [Fact]
    public async Task If_statements_execute_the_true_branch_when_the_condition_is_true()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("if (\"Hello\".Contains(\"H\")) { echo yes }");

        Assert.Collection(results, item => Assert.Equal("yes", item));
    }

    [Fact]
    public async Task If_statements_execute_else_if_and_else_branches()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("if (false) { echo no } else if (true) { echo yes } else { echo never }");

        Assert.Collection(results, item => Assert.Equal("yes", item));
    }

    [Fact]
    public async Task If_conditions_can_use_subexpressions_that_return_single_values()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "keep.txt"), "keep");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("if ((ls | first | get Name) == \"keep.txt\") { echo matched }");

        Assert.Collection(results, item => Assert.Equal("matched", item));
    }

    [Fact]
    public async Task For_loops_iterate_pipeline_sources_and_bind_loop_variables()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("for item in (echo one two three | first 2) { echo $item }");

        Assert.Equal(new[] { "one", "two" }, results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task For_loops_support_continue_and_break()
    {
        var engine = new ToshEngine();

        var continueResults = await engine.ExecuteToListAsync(
            "for item in (echo one skip two) { if (($item == skip)) { continue }; echo $item }");
        var breakResults = await engine.ExecuteToListAsync(
            "for item in (echo one two three) { echo $item; break; echo never }");

        Assert.Equal(new[] { "one", "two" }, continueResults.Cast<string>().ToArray());
        Assert.Collection(breakResults, item => Assert.Equal("one", item));
    }

    [Fact]
    public async Task While_loops_re_evaluate_conditions_and_allow_assignments()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("var count = 0");
        var results = await engine.ExecuteToListAsync("while (($count < 3)) { echo $count; count = ($count + 1) }");
        var finalValue = await engine.ExecuteToListAsync("echo $count");

        Assert.Equal(new long[] { 0, 1, 2 }, results.Select(item => Convert.ToInt64(item)).ToArray());
        Assert.Collection(finalValue, item => Assert.Equal(3L, item));
    }

    [Fact]
    public async Task Break_and_continue_outside_loops_raise_diagnostics()
    {
        var engine = new ToshEngine();

        var breakException = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("break"));
        var continueException = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("continue"));

        Assert.Contains("break_outside_loop", breakException.Diagnostics[0].Code);
        Assert.Contains("continue_outside_loop", continueException.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Block_locals_shadow_globals_without_leaking_back_out()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("var item = \"GLOBAL\"");
        var blockResults = await engine.ExecuteToListAsync("echo Hello | each { var item = $it.ToLower(); $item }");
        var globalResults = await engine.ExecuteToListAsync("echo $item");

        Assert.Collection(blockResults, item => Assert.Equal("hello", item));
        Assert.Collection(globalResults, item => Assert.Equal("GLOBAL", item));
    }

    [Fact]
    public async Task Untouched_dotnet_types_can_be_constructed_and_invoked_fluently()
    {
        var engine = new ToshEngine();

        var typeResults = await engine.ExecuteToListAsync("echo new System.Random().Next() | type-of");
        var valueResults = await engine.ExecuteToListAsync("echo new System.Random().Next(1, 10)");

        Assert.Collection(typeResults, item => Assert.Equal(typeof(int), item));
        Assert.Collection(
            valueResults,
            item =>
            {
                var value = Assert.IsType<int>(item);
                Assert.InRange(value, 1, 9);
            });
    }

    [Fact]
    public async Task Using_aliases_can_resolve_namespace_prefixed_dotnet_types()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("using System.IO = IO\necho IO.Path.DirectorySeparatorChar");

        Assert.Collection(results, item => Assert.Equal(Path.DirectorySeparatorChar, Assert.IsType<char>(item)));
    }

    [Fact]
    public async Task Using_imports_enable_static_method_access_for_framework_types_from_runtime_assemblies()
    {
        var engine = new ToshEngine();

        var importedResults = await engine.ExecuteToListAsync("using System.IO\necho DriveInfo.GetDrives() | type-of");
        var qualifiedResults = await engine.ExecuteToListAsync("echo System.IO.DriveInfo.GetDrives() | type-of");
        var rawPipelineResults = await engine.ExecuteToListAsync("using System.IO\nDriveInfo.GetDrives() | type-of");
        var flattenedResults = await engine.ExecuteToListAsync("using System.IO\nDriveInfo.GetDrives() | each { $it } | type-of");

        Assert.Collection(importedResults, item => Assert.Equal(typeof(DriveInfo[]), item));
        Assert.Collection(qualifiedResults, item => Assert.Equal(typeof(DriveInfo[]), item));
        Assert.Collection(rawPipelineResults, item => Assert.Equal(typeof(DriveInfo[]), item));
        Assert.NotEmpty(flattenedResults);
        Assert.All(flattenedResults, item => Assert.Equal(typeof(DriveInfo), item));
    }

    [Fact]
    public async Task Variable_assignments_preserve_raw_clr_collection_results()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("using System.IO\nvar di = DriveInfo.GetDrives()\necho $di | type-of");

        Assert.Collection(results, item => Assert.Equal(typeof(DriveInfo[]), item));
    }

    [Fact]
    public async Task Subexpressions_preserve_raw_clr_collection_results_for_member_access()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("using System.IO\necho (DriveInfo.GetDrives()).Length");

        Assert.Collection(
            results,
            item => Assert.True(Convert.ToInt32(item) >= 0));
    }

    [Fact]
    public async Task Using_can_import_script_files_once_without_emitting_their_output()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(
            System.IO.Path.Combine(tempDirectory.Path, "defs.tosh"),
            "echo loaded\nalias ll = ls -la\ndef names() { ls | get Name }");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("using defs.tosh\nusing defs.tosh\nwhich ll names | get Kind");

        Assert.Equal(
            new[] { CommandResolutionKind.Alias, CommandResolutionKind.Function },
            results.Cast<CommandResolutionKind>().ToArray());
    }

    [Fact]
    public async Task First_returns_the_first_object_or_first_n_objects()
    {
        var engine = new ToshEngine();

        var single = await engine.ExecuteToListAsync("echo one two three | first");
        var many = await engine.ExecuteToListAsync("echo one two three | first 2");

        Assert.Collection(single, item => Assert.Equal("one", item));
        Assert.Equal(new[] { "one", "two" }, many.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Last_returns_the_last_object_or_last_n_objects()
    {
        var engine = new ToshEngine();

        var single = await engine.ExecuteToListAsync("echo one two three | last");
        var many = await engine.ExecuteToListAsync("echo one two three | last 2");

        Assert.Collection(single, item => Assert.Equal("three", item));
        Assert.Equal(new[] { "two", "three" }, many.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Skip_skips_the_first_object_or_first_n_objects()
    {
        var engine = new ToshEngine();

        var single = await engine.ExecuteToListAsync("echo one two three | skip");
        var many = await engine.ExecuteToListAsync("echo one two three | skip 2");

        Assert.Equal(new[] { "two", "three" }, single.Cast<string>().ToArray());
        Assert.Collection(many, item => Assert.Equal("three", item));
    }

    [Fact]
    public async Task Sort_orders_scalars_and_member_paths()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "b.txt"), "b");
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "a.txt"), "a");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var scalarResults = await engine.ExecuteToListAsync("echo 3 1 2 | sort");
        var objectResults = await engine.ExecuteToListAsync("ls -la | reverse | sort Name | get Name");

        Assert.Equal(new[] { 1, 2, 3 }, scalarResults.Cast<int>().ToArray());
        Assert.Equal(new[] { "a.txt", "b.txt" }, objectResults.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Reverse_reorders_arbitrary_pipeline_objects()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo one two three | reverse");

        Assert.Equal(new[] { "three", "two", "one" }, results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Count_returns_the_number_of_pipeline_objects()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "alpha.txt"), "alpha");
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "beta.txt"), "beta");
        Directory.CreateDirectory(System.IO.Path.Combine(tempDirectory.Path, "nested"));

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var echoCount = await engine.ExecuteToListAsync("echo 1 2 3 | count");
        var lsCount = await engine.ExecuteToListAsync("ls -la | count");

        Assert.Collection(echoCount, item => Assert.Equal(3, item));
        Assert.Collection(lsCount, item => Assert.Equal(3, item));
    }

    [Fact]
    public async Task Ps_returns_tosh_process_objects()
    {
        var engine = new ToshEngine();

        var typeResults = await engine.ExecuteToListAsync("ps | first | type-of");
        var idResults = await engine.ExecuteToListAsync("ps | first | get Id | type-of");

        Assert.Collection(typeResults, item => Assert.Equal(typeof(ProcessInfo), item));
        Assert.Collection(idResults, item => Assert.Equal(typeof(int), item));
    }

    [Fact]
    public async Task Process_info_exposes_memory_as_a_queryable_member()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo new Tosh.Core.ProcessInfo(1, \"proc\", false, null, null, 2048, null, null) | get Memory");

        Assert.Collection(results, item => Assert.Equal(StorageSize.FromBytes(2048), Assert.IsType<StorageSize>(item)));
    }

    [Fact]
    public async Task Env_returns_environment_variable_objects()
    {
        var engine = new ToshEngine();

        var nameResults = await engine.ExecuteToListAsync("env PATH | get Name");
        var setResults = await engine.ExecuteToListAsync("env PATH | get IsSet");

        Assert.Collection(nameResults, item => Assert.Equal("PATH", item));
        Assert.Collection(setResults, item => Assert.Equal(true, item));
    }

    [Fact]
    public async Task Which_returns_builtin_command_resolutions()
    {
        var engine = new ToshEngine();

        var kindResults = await engine.ExecuteToListAsync("which help | get Kind");
        var usageResults = await engine.ExecuteToListAsync("whence help | get Usage");

        Assert.Contains(CommandResolutionKind.BuiltIn, kindResults.Cast<CommandResolutionKind>());
        Assert.Contains("help [topic | search <query> | related <topic> | categories]", usageResults.Cast<string>());
    }

    [Fact]
    public async Task Which_can_resolve_aliases_and_functions()
    {
        var engine = new ToshEngine();

        await engine.ExecuteToListAsync("alias ll = ls -la");
        await engine.ExecuteToListAsync("def recent(days: TimeSpan) { ls -la | where Modified > ((date now) - $days) }");
        var kindResults = await engine.ExecuteToListAsync("which ll recent | get Kind");

        Assert.Contains(CommandResolutionKind.Alias, kindResults.Cast<CommandResolutionKind>());
        Assert.Contains(CommandResolutionKind.Function, kindResults.Cast<CommandResolutionKind>());
    }

    [Fact]
    public async Task Source_executes_script_files_in_the_current_session()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(
            System.IO.Path.Combine(tempDirectory.Path, "defs.tosh"),
            "alias ll = ls -la\ndef stringifyCount() -> String { count }");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var sourceResults = await engine.ExecuteToListAsync("source defs.tosh");
        var aliasKinds = await engine.ExecuteToListAsync("which ll | get Kind");
        var functionResults = await engine.ExecuteToListAsync("echo 1 2 3 | stringifyCount");

        Assert.Empty(sourceResults);
        Assert.Contains(CommandResolutionKind.Alias, aliasKinds.Cast<CommandResolutionKind>());
        Assert.Collection(functionResults, item => Assert.Equal("3", item));
    }

    [Fact]
    public async Task Newline_separated_top_level_statements_can_chain_into_get_after_an_earlier_statement()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("alias ll = ls -la\nwhich ll | get Kind");

        Assert.Contains(CommandResolutionKind.Alias, results.Cast<CommandResolutionKind>());
    }

    [Fact]
    public async Task Newline_separated_block_statements_execute_inside_each_blocks()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync(
            """
            echo Hello | each {
                var lower = $it.ToLower()
                $lower
            }
            """);

        Assert.Collection(results, item => Assert.Equal("hello", item));
    }

    [Fact]
    public async Task Return_exits_top_level_scripts_early()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo before\nreturn \"done\"\necho after");

        Assert.Equal(new[] { "before", "done" }, results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Sort_by_alias_can_sort_objects_by_visible_process_members()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync(
            "echo new Tosh.Core.ProcessInfo(1, \"large\", false, null, null, 4096, null, null) new Tosh.Core.ProcessInfo(2, \"small\", false, null, null, 1024, null, null) | sort-by Memory | get Name");

        Assert.Equal(new[] { "small", "large" }, results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Can_filter_filesystem_objects()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "keep.txt"), "keep");
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "skip.log"), "skip");
        Directory.CreateDirectory(System.IO.Path.Combine(tempDirectory.Path, "nested"));

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls | where Extension == .txt | get Name");

        Assert.Collection(results, item => Assert.Equal("keep.txt", item));
    }

    [Fact]
    public async Task Displayed_filesystem_columns_are_queryable_members()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "keep.txt"), "keep");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var sizeResults = await engine.ExecuteToListAsync("ls | get Size");
        var typeResults = await engine.ExecuteToListAsync("ls | get Type");
        var modifiedResults = await engine.ExecuteToListAsync("ls | get Modified");
        var modeResults = await engine.ExecuteToListAsync("ls | get Mode");
        var readonlyResults = await engine.ExecuteToListAsync("ls -la | get Readonly");
        var createdResults = await engine.ExecuteToListAsync("ls -la | get Created");
        var accessedResults = await engine.ExecuteToListAsync("ls -la | get Accessed");
        var inodeResults = await engine.ExecuteToListAsync("ls -la | get Inode");
        var ownerResults = await engine.ExecuteToListAsync("ls -la | get Owner");
        var groupResults = await engine.ExecuteToListAsync("ls -la | get Group");

        Assert.Collection(sizeResults, item => Assert.Equal(StorageSize.FromBytes(4), Assert.IsType<StorageSize>(item)));
        Assert.Collection(typeResults, item => Assert.Equal(FileSystemEntryType.File, item));
        Assert.Collection(modifiedResults, item => Assert.IsType<DateTime>(item));
        Assert.Collection(readonlyResults, item => Assert.IsType<bool>(item));
        Assert.Collection(createdResults, item => Assert.IsType<DateTime>(item));
        Assert.Collection(accessedResults, item => Assert.IsType<DateTime>(item));

        if (OperatingSystem.IsWindows())
        {
            Assert.Collection(modeResults, item => Assert.Null(item));
            Assert.Collection(inodeResults, item => Assert.Null(item));
            Assert.Collection(ownerResults, item => Assert.Null(item));
            Assert.Collection(groupResults, item => Assert.Null(item));
        }
        else
        {
            Assert.Collection(modeResults, item => Assert.IsType<UnixFileMode>(item));
            Assert.Collection(inodeResults, item => Assert.IsType<long>(item));
            Assert.Collection(ownerResults, item => Assert.IsType<FileSystemPrincipalInfo>(item));
            Assert.Collection(groupResults, item => Assert.IsType<FileSystemPrincipalInfo>(item));
        }
    }

    [Fact]
    public async Task Filesystem_entries_surface_link_targets_when_listing_symlinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDirectory = new TemporaryDirectory();
        var filePath = System.IO.Path.Combine(tempDirectory.Path, "keep.txt");
        var linkPath = System.IO.Path.Combine(tempDirectory.Path, "keep-link.txt");
        File.WriteAllText(filePath, "keep");
        File.CreateSymbolicLink(linkPath, filePath);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var targetResults = await engine.ExecuteToListAsync("ls -la | where Name == keep-link.txt | get Target");

        Assert.Collection(targetResults, item => Assert.NotNull(item));
    }

    [Fact]
    public async Task Typed_filesystem_entry_kind_remains_filterable_from_strings()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "keep.txt"), "keep");
        Directory.CreateDirectory(System.IO.Path.Combine(tempDirectory.Path, "nested"));

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls | where Type == file | get Name");

        Assert.Collection(results, item => Assert.Equal("keep.txt", item));
    }

    [Fact]
    public async Task Nullable_member_path_allows_ordered_comparisons_to_skip_nulls()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "small.txt"), "tiny");
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "big.txt"), new string('x', 1501));
        Directory.CreateDirectory(System.IO.Path.Combine(tempDirectory.Path, "nested"));

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls -la | where Size? > 1000 | get Name");

        Assert.Collection(results, item => Assert.Equal("big.txt", item));
    }

    [Fact]
    public async Task Typed_storage_sizes_can_be_compared_against_numbers_and_units()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "small.txt"), "tiny");
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "big.txt"), new string('x', 1501));

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var numericResults = await engine.ExecuteToListAsync("ls -la | where Size? > 1000 | get Name");
        var unitResults = await engine.ExecuteToListAsync("ls -la | where Size? > 1kb | get Name");

        Assert.Collection(numericResults, item => Assert.Equal("big.txt", item));
        Assert.Collection(unitResults, item => Assert.Equal("big.txt", item));
    }

    [Fact]
    public async Task Ordered_comparisons_auto_skip_nulls_for_statically_nullable_members()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "small.txt"), "tiny");
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "big.txt"), new string('x', 1501));
        Directory.CreateDirectory(System.IO.Path.Combine(tempDirectory.Path, "nested"));

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls -la | where Size >= 1kb | get Name");

        Assert.Collection(results, item => Assert.Equal("big.txt", item));
    }

    [Fact]
    public async Task Modified_dates_can_be_compared_against_subexpression_dates()
    {
        using var tempDirectory = new TemporaryDirectory();
        var oldFilePath = System.IO.Path.Combine(tempDirectory.Path, "old.txt");
        var recentFilePath = System.IO.Path.Combine(tempDirectory.Path, "recent.txt");
        File.WriteAllText(oldFilePath, "old");
        File.WriteAllText(recentFilePath, "recent");
        File.SetLastWriteTime(oldFilePath, DateTime.Now.AddDays(-5));
        File.SetLastWriteTime(recentFilePath, DateTime.Now.AddHours(-6));

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls -la | where Type == file | where Modified < (date now | date sub (timespan 2d)) | get Name");

        Assert.Collection(results, item => Assert.Equal("old.txt", item));
    }

    [Fact]
    public async Task Where_can_evaluate_fluent_predicate_expressions_against_the_current_item()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "alpha.x"), "alpha");
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "beta.txt"), "beta");
        Directory.CreateDirectory(System.IO.Path.Combine(tempDirectory.Path, "nested.x"));

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls -la | where Name.ToString().ToLower().Contains(\".x\") | get Name");

        Assert.Equal(
            new[] { "alpha.x", "nested.x" },
            results.Cast<string>().OrderBy(name => name).ToArray());
    }

    [Fact]
    public async Task Where_predicate_expressions_can_mix_current_item_access_with_variables()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "alpha.x"), "alpha");
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "beta.txt"), "beta");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("var suffix = \".x\"");
        var results = await engine.ExecuteToListAsync("ls -la | where Name.ToLower().EndsWith($suffix) | get Name");

        Assert.Collection(results, item => Assert.Equal("alpha.x", item));
    }

    [Fact]
    public async Task Where_supports_predicate_blocks_with_multiple_clauses()
    {
        using var tempDirectory = new TemporaryDirectory();
        var oldFilePath = System.IO.Path.Combine(tempDirectory.Path, "old.txt");
        var recentFilePath = System.IO.Path.Combine(tempDirectory.Path, "recent.txt");
        File.WriteAllText(oldFilePath, new string('x', 2048));
        File.WriteAllText(recentFilePath, new string('x', 2048));
        File.SetLastWriteTime(oldFilePath, DateTime.Now.AddDays(-5));
        File.SetLastWriteTime(recentFilePath, DateTime.Now.AddHours(-6));

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls -la | where { Type == file; Size >= 1kb; Modified < ((date now) - (timespan 2d)); } | get Name");

        Assert.Collection(results, item => Assert.Equal("old.txt", item));
    }

    [Fact]
    public async Task Ls_hides_dotfiles_unless_all_flag_is_used()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, ".secret"), "secret");
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "visible.txt"), "visible");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var defaultResults = await engine.ExecuteToListAsync("ls");
        var allResults = await engine.ExecuteToListAsync("ls -a");

        Assert.Collection(
            defaultResults,
            item => Assert.Equal("visible.txt", Assert.IsType<FileSystemEntry>(item).Name));

        Assert.Equal(
            new[] { ".secret", "visible.txt" },
            allResults.Select(item => Assert.IsType<FileSystemEntry>(item).Name).OrderBy(name => name).ToArray());
    }

    [Fact]
    public async Task Ls_long_format_marks_entries_for_long_display()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "visible.txt"), "visible");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls -la");

        var entry = Assert.IsType<FileSystemEntry>(Assert.Single(results));
        Assert.True(entry.PreferLongDisplay);
        Assert.Equal("visible.txt", entry.Name);
    }

    [Fact]
    public async Task Can_create_touch_copy_move_and_remove_filesystem_items()
    {
        using var tempDirectory = new TemporaryDirectory();
        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("mkdir -p nested");
        await engine.ExecuteToListAsync("touch nested/original.txt");
        await engine.ExecuteToListAsync("cp nested/original.txt nested/copied.txt");
        await engine.ExecuteToListAsync("mv nested/copied.txt nested/moved.txt");
        await engine.ExecuteToListAsync("rm nested/moved.txt");

        Assert.True(Directory.Exists(System.IO.Path.Combine(tempDirectory.Path, "nested")));
        Assert.True(File.Exists(System.IO.Path.Combine(tempDirectory.Path, "nested", "original.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(tempDirectory.Path, "nested", "copied.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(tempDirectory.Path, "nested", "moved.txt")));
    }

    [Fact]
    public async Task Rm_recursive_removes_directories()
    {
        using var tempDirectory = new TemporaryDirectory();
        var nestedPath = System.IO.Path.Combine(tempDirectory.Path, "nested");
        Directory.CreateDirectory(nestedPath);
        File.WriteAllText(System.IO.Path.Combine(nestedPath, "file.txt"), "contents");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("rm -r nested");

        Assert.False(Directory.Exists(nestedPath));
    }

    [Fact]
    public async Task Inspect_returns_object_inspection_for_piped_values()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("new System.Text.StringBuilder hello | inspect");

        var inspection = Assert.IsType<ObjectInspection>(Assert.Single(results));
        Assert.Equal("System.Text.StringBuilder", inspection.TypeName);
        Assert.Contains(inspection.Members, member => member.Name == "Length");
        Assert.Contains("StringBuilder", inspection.Display, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drive_info_size_members_are_exposed_as_storage_size_values()
    {
        var engine = new ToshEngine();
        var driveRoot = System.IO.Path.GetPathRoot(Environment.CurrentDirectory)
                        ?? throw new InvalidOperationException("Unable to determine the current drive root.");
        var escapedDriveRoot = driveRoot
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        var projectedResults = await engine.ExecuteToListAsync(
            $"echo new System.IO.DriveInfo(\"{escapedDriveRoot}\") | get {{ AvailableFreeSpace, TotalFreeSpace, TotalSize }}");
        var typeResults = await engine.ExecuteToListAsync(
            $"echo new System.IO.DriveInfo(\"{escapedDriveRoot}\") | get TotalSize | type-of");

        var projection = Assert.IsType<ProjectedObject>(Assert.Single(projectedResults));
        Assert.True(projection.TryGetValue("AvailableFreeSpace", out var availableFreeSpace));
        Assert.True(projection.TryGetValue("TotalFreeSpace", out var totalFreeSpace));
        Assert.True(projection.TryGetValue("TotalSize", out var totalSize));
        Assert.IsType<StorageSize>(availableFreeSpace);
        Assert.IsType<StorageSize>(totalFreeSpace);
        Assert.IsType<StorageSize>(totalSize);
        Assert.Collection(typeResults, item => Assert.Equal(typeof(StorageSize), item));
    }

    [Fact]
    public async Task View_changes_formatter_style_through_a_normal_command()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("view detail");

        Assert.Equal(ObjectRenderStyle.Detail, runtime.Formatter.Style);
        var status = Assert.IsType<FormatterStatus>(Assert.Single(results));
        Assert.Equal(ObjectRenderStyle.Detail, status.Style);
    }

    [Fact]
    public async Task Write_and_writeline_write_text_without_emitting_pipeline_objects()
    {
        using var output = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(output, TextWriter.Null);
        var engine = new ToshEngine(runtime);

        var writeResults = await engine.ExecuteToListAsync("write \"hello\"");
        var writeLineResults = await engine.ExecuteToListAsync("writeline \" world\"");

        Assert.Empty(writeResults);
        Assert.Empty(writeLineResults);
        Assert.Equal($"hello world{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task View_can_configure_datetime_and_storage_size_preferences()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var dateResults = await engine.ExecuteToListAsync("view datetime table unix");
        var sizeResults = await engine.ExecuteToListAsync("view size bytes");

        Assert.Equal(TemporalDisplayMode.Unix, runtime.DisplayPreferences.DateTime.TableMode);
        Assert.Collection(
            dateResults.Cast<DisplayPreferenceStatus>(),
            item =>
            {
                Assert.Equal("datetime", item.Target);
                Assert.Equal("scalar", item.Scope);
                Assert.Equal("iso", item.Mode);
            },
            item =>
            {
                Assert.Equal("datetime", item.Target);
                Assert.Equal("table", item.Scope);
                Assert.Equal("unix", item.Mode);
            });

        Assert.Equal(StorageSizeDisplayMode.Bytes, runtime.DisplayPreferences.StorageSize.Mode);
        var status = Assert.IsType<DisplayPreferenceStatus>(Assert.Single(sizeResults));
        Assert.Equal("storage-size", status.Target);
        Assert.Equal("bytes", status.Mode);
    }

    [Fact]
    public async Task Exit_requests_shell_exit_through_a_normal_command()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("exit");

        Assert.True(runtime.ExitRequested);
        Assert.Empty(results);
    }

    [Fact]
    public async Task History_returns_runtime_history_entries()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.RecordHistory("help");
        runtime.RecordHistory("ls -la");
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("history");

        Assert.Collection(
            results,
            item => Assert.Equal("help", Assert.IsType<CommandHistoryEntry>(item).Text),
            item => Assert.Equal("ls -la", Assert.IsType<CommandHistoryEntry>(item).Text));

        var whenResults = await engine.ExecuteToListAsync("history | get When");
        Assert.All(whenResults, item => Assert.IsType<DateTimeOffset>(item));
    }

    [Fact]
    public async Task Uname_returns_kernel_information()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("uname");

        var info = Assert.IsType<UnameInfo>(Assert.Single(results));
        Assert.False(string.IsNullOrWhiteSpace(info.SystemName));
        Assert.False(string.IsNullOrWhiteSpace(info.NodeName));
    }

    [Fact]
    public async Task Hostname_and_whoami_return_identity_scalars()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var hostResults = await engine.ExecuteToListAsync("hostname");
        var userResults = await engine.ExecuteToListAsync("whoami");

        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(Assert.Single(hostResults))));
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<FileSystemPrincipalInfo>(Assert.Single(userResults)).DisplayName));
    }

    [Fact]
    public async Task Id_returns_current_identity_information()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("id");

        var identity = Assert.IsType<UserIdentityInfo>(Assert.Single(results));
        Assert.False(string.IsNullOrWhiteSpace(identity.User.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(identity.Group.DisplayName));
        Assert.NotEmpty(identity.Groups);
    }

    [Fact]
    public async Task Df_returns_file_system_usage_objects()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("df | first");

        var usage = Assert.IsType<FileSystemUsageInfo>(Assert.Single(results));
        Assert.False(string.IsNullOrWhiteSpace(usage.MountedOn));
    }

    [Fact]
    public async Task Ping_returns_typed_reply_objects()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ping -c 1 127.0.0.1");

        var reply = Assert.IsType<PingReplyInfo>(Assert.Single(results));
        Assert.Equal(1, reply.Sequence);
        Assert.Equal("127.0.0.1", reply.Address?.ToString());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-tests-{Guid.NewGuid():N}");
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
