using Tosh.Runtime;
using Tosh.Stdlib.Shell;
using Tosh.Language;
using Tosh.Language.Parsing;
using System.Numerics;

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
        var result = ToshParser.Parse("where _.Modified < (date now | date sub (timespan 2d))");

        Assert.Empty(result.Diagnostics);

        // where now wraps expressions in a block argument
        var blockArg = Assert.IsType<BlockArgumentSyntax>(Assert.Single(result.Pipeline.Commands[0].Arguments));
        var statement = Assert.IsType<PipelineStatementSyntax>(Assert.Single(blockArg.Block.Statements));
        var stage = Assert.IsType<ExpressionPipelineStageSyntax>(Assert.Single(statement.Pipeline.Stages));
        var operatorExpr = Assert.IsType<OperatorArgumentSyntax>(stage.Expression);
        Assert.Equal("<", operatorExpr.Operator);
        var subexpr = Assert.IsType<SubexpressionArgumentSyntax>(operatorExpr.Right);
        Assert.Equal(2, subexpr.Pipeline.Commands.Count);
        Assert.Equal("date", subexpr.Pipeline.Commands[0].Name);
        Assert.Equal("date", subexpr.Pipeline.Commands[1].Name);
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
    public void Parser_supports_command_substitution_as_an_argument()
    {
        var result = ToshParser.Parse("echo $(ping -c 1 www.google.com)");

        Assert.Empty(result.Diagnostics);
        var command = result.Pipeline.Commands[0];
        var substitution = Assert.IsType<CommandSubstitutionArgumentSyntax>(Assert.Single(command.Arguments));
        var innerCommand = Assert.IsType<CommandSyntax>(Assert.Single(substitution.Pipeline.Stages));

        Assert.Equal("ping", innerCommand.Name);
    }

    [Fact]
    public void Parser_supports_input_process_substitution_as_an_argument()
    {
        var result = ToshParser.Parse("/bin/cat <(echo alpha beta)");

        Assert.Empty(result.Diagnostics);
        var command = result.Pipeline.Commands[0];
        var substitution = Assert.IsType<InputProcessSubstitutionArgumentSyntax>(Assert.Single(command.Arguments));
        var innerCommand = Assert.IsType<CommandSyntax>(Assert.Single(substitution.Pipeline.Stages));

        Assert.Equal("echo", innerCommand.Name);
    }

    [Fact]
    public void Parser_supports_splat_arguments()
    {
        var result = ToshParser.Parse("echo ...$tosh.Script.Args");

        Assert.Empty(result.Diagnostics);
        var command = result.Pipeline.Commands[0];
        var splat = Assert.IsType<SplatArgumentSyntax>(Assert.Single(command.Arguments));
        var member = Assert.IsType<MemberAccessArgumentSyntax>(splat.Value);
        Assert.Equal("Args", member.MemberPath);
    }

    [Fact]
    public void Parser_supports_here_string_as_an_input_stage()
    {
        var result = ToshParser.Parse("<<< \"alpha\" | /bin/cat");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Pipeline.Stages.Count);
        Assert.IsType<ExpressionPipelineStageSyntax>(result.Pipeline.Stages[0]);
        Assert.IsType<CommandSyntax>(result.Pipeline.Stages[1]);
    }

    [Fact]
    public void Parser_supports_background_pipelines()
    {
        var result = ToshParser.Parse("ping -c 1 localhost &");

        Assert.Empty(result.Diagnostics);
        Assert.True(result.Pipeline.IsBackground);
    }

    [Fact]
    public void Parser_treats_ampersand_as_a_statement_boundary()
    {
        var result = ToshParser.Parse("echo first & echo second");

        Assert.Empty(result.Diagnostics);
        var script = Assert.IsType<ScriptStatementSyntax>(result.Statement);
        Assert.Collection(
            script.Statements,
            statement => Assert.True(Assert.IsType<PipelineStatementSyntax>(statement).Pipeline.IsBackground),
            statement => Assert.False(Assert.IsType<PipelineStatementSyntax>(statement).Pipeline.IsBackground));
    }

    [Fact]
    public void Parser_supports_variable_declarations_and_csharp_style_object_construction()
    {
        var result = ToshParser.Parse("var builder = new System.Text.StringBuilder(\"hello\") | call Append \" world\"");

        Assert.Empty(result.Diagnostics);

        var statement = Assert.IsType<VariableDeclarationStatementSyntax>(result.Statement);
        Assert.Equal("builder", statement.Name);
        var value = statement.Value;
        Assert.NotNull(value);
        Assert.Equal(2, value.Stages.Count);

        var expressionStage = Assert.IsType<ExpressionPipelineStageSyntax>(value.Stages[0]);
        var newObject = Assert.IsType<NewObjectArgumentSyntax>(expressionStage.Expression);
        Assert.Equal("System.Text.StringBuilder", newObject.TypeName);
        Assert.Collection(
            newObject.Arguments,
            argument => Assert.Equal("hello", Assert.IsType<LiteralArgumentSyntax>(argument).Value));

        Assert.Equal("call", Assert.IsType<CommandSyntax>(value.Stages[1]).Name);
    }

    [Fact]
    public void Parser_supports_alloc_statements()
    {
        var result = ToshParser.Parse("alloc buffer = Tosh.Tests.NativePoint");

        Assert.Empty(result.Diagnostics);

        var statement = Assert.IsType<AllocStatementSyntax>(result.Statement);
        Assert.Equal("buffer", statement.Name);
        var stage = Assert.IsType<ExpressionPipelineStageSyntax>(Assert.Single(statement.Value.Stages));
        Assert.Equal("Tosh.Tests.NativePoint", Assert.IsType<StaticMemberAccessArgumentSyntax>(stage.Expression).Path);
    }

    [Fact]
    public void Parser_supports_out_and_ref_native_binding_parameters()
    {
        var result = ToshParser.Parse(
            """
            bind LibC {
                func gettimeofday(out tv: Tosh.Tests.NativeTimeVal, ref tz: nint) -> int
            }
            """);

        Assert.Empty(result.Diagnostics);

        var bind = Assert.IsType<BindStatementSyntax>(result.Statement);
        var function = Assert.Single(bind.Functions);
        Assert.Collection(
            function.Parameters,
            parameter =>
            {
                Assert.Equal("tv", parameter.Name);
                Assert.Equal("Tosh.Tests.NativeTimeVal", parameter.TypeName);
                Assert.Equal(NativeParameterPassingMode.Out, parameter.PassingMode);
            },
            parameter =>
            {
                Assert.Equal("tz", parameter.Name);
                Assert.Equal("nint", parameter.TypeName);
                Assert.Equal(NativeParameterPassingMode.Ref, parameter.PassingMode);
            });
    }

    [Fact]
    public void Parser_supports_wrapper_and_typed_function_definitions()
    {
        var wrapperResult = ToshParser.Parse("func ll => ls -la");
        var functionResult = ToshParser.Parse("func recent(days: TimeSpan) -> FileSystemEntry { ls -la | where _.Modified > ((date now) - $days) }");

        Assert.Empty(wrapperResult.Diagnostics);
        var wrapper = Assert.IsType<FunctionDefinitionStatementSyntax>(wrapperResult.Statement);
        Assert.Equal("ll", wrapper.Name);
        Assert.Empty(wrapper.Parameters);

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
    public void Parser_supports_anonymous_function_arguments()
    {
        var result = ToshParser.Parse("invoke func(x) => ($x * 2) 21");

        Assert.Empty(result.Diagnostics);

        var command = result.Pipeline.Commands[0];
        Assert.Equal("invoke", command.Name);

        var anonymous = Assert.IsType<AnonymousFunctionArgumentSyntax>(command.Arguments[0]);
        var parameter = Assert.Single(anonymous.Parameters);
        Assert.Equal("x", parameter.Name);

        var statement = Assert.IsType<PipelineStatementSyntax>(Assert.Single(anonymous.Body.Statements));
        var stage = Assert.IsType<ExpressionPipelineStageSyntax>(Assert.Single(statement.Pipeline.Stages));
        var operation = Assert.IsType<OperatorArgumentSyntax>(stage.Expression);
        Assert.Equal("*", operation.Operator);
    }

    [Fact]
    public void Parser_supports_class_definitions_with_properties_constructors_and_methods()
    {
        var result = ToshParser.Parse(
            """
            export class Item {
                prop Name: string? = null
                prop IsLowStock: bool => $this.is_low_stock()
                prop ClassName: string? {
                    get => $this.internal_name
                    set => $this.internal_name = $value
                }
                shy prop internal_name => $"item_{$this.Name}"
                Item() { }
                Item(name: string) { $this.Name = $name }
                static func named(name: string) -> Item { return new Item($name) }
                shy func is_low_stock() -> bool { return false }
            }
            """);

        Assert.Empty(result.Diagnostics);

        var @class = Assert.IsType<ClassDefinitionStatementSyntax>(result.Statement);
        Assert.Equal(DeclarationModifier.Export, @class.Modifier);
        Assert.Equal("Item", @class.Name);

        var propertyMembers = @class.Members.OfType<ClassPropertyMemberSyntax>().ToArray();
        Assert.Contains(propertyMembers, member => member.Name == "Name" && member.TypeName == "string?");
        Assert.Contains(propertyMembers, member => member.Name == "IsLowStock" && member.GetterBody is not null && member.SetterBody is null);
        Assert.Contains(propertyMembers, member => member.Name == "ClassName" && member.GetterBody is not null && member.SetterBody is not null);
        Assert.Contains(propertyMembers, member => member.Name == "internal_name" && member.IsShy);

        Assert.Equal(2, @class.Members.OfType<ClassConstructorMemberSyntax>().Count());
        Assert.Contains(@class.Members.OfType<ClassMethodMemberSyntax>(), member => member.Method.Name == "named" && member.IsStatic);
        Assert.Contains(@class.Members.OfType<ClassMethodMemberSyntax>(), member => member.Method.Name == "is_low_stock" && member.IsShy);
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
    public void Parser_supports_break_continue_using_and_require_statements()
    {
        var usingResult = ToshParser.Parse("using System.IO = IO");
        var requireResult = ToshParser.Parse("require ./defs.tosh");
        var selectiveRequireResult = ToshParser.Parse("require Inventory from ./defs.tosh as Inv");
        var nativeRequireResult = ToshParser.Parse("require native libc as LibC");
        var breakResult = ToshParser.Parse("break");
        var continueResult = ToshParser.Parse("continue");

        Assert.Empty(usingResult.Diagnostics);
        var usingStatement = Assert.IsType<UsingStatementSyntax>(usingResult.Statement);
        Assert.Equal("System.IO", usingStatement.Target);
        Assert.Equal("IO", usingStatement.Alias);

        Assert.Empty(requireResult.Diagnostics);
        var requireStatement = Assert.IsType<RequireStatementSyntax>(requireResult.Statement);
        Assert.Equal("./defs.tosh", requireStatement.Target);
        Assert.Empty(requireStatement.Imports);

        Assert.Empty(selectiveRequireResult.Diagnostics);
        var selectiveRequire = Assert.IsType<RequireStatementSyntax>(selectiveRequireResult.Statement);
        Assert.Equal("./defs.tosh", selectiveRequire.Target);
        Assert.Single(selectiveRequire.Imports);
        Assert.Equal("Inventory", selectiveRequire.Imports[0].Name);
        Assert.Equal("Inv", selectiveRequire.Imports[0].Alias);

        Assert.Empty(nativeRequireResult.Diagnostics);
        var nativeRequire = Assert.IsType<RequireStatementSyntax>(nativeRequireResult.Statement);
        Assert.True(nativeRequire.IsNative);
        Assert.Equal("libc", nativeRequire.Target);
        Assert.Equal("LibC", nativeRequire.Alias);

        Assert.Empty(breakResult.Diagnostics);
        Assert.IsType<BreakStatementSyntax>(breakResult.Statement);

        Assert.Empty(continueResult.Diagnostics);
        Assert.IsType<ContinueStatementSyntax>(continueResult.Statement);
    }

    [Fact]
    public void Parser_supports_declaration_modifiers()
    {
        var variableResult = ToshParser.Parse("export var answer = 42");
        var wrapperResult = ToshParser.Parse("shy func ll => ls -la");
        var functionResult = ToshParser.Parse("global func greet() { echo hi }");
        var usingResult = ToshParser.Parse("global using System.IO");
        var requireResult = ToshParser.Parse("shy require ./defs.tosh");

        Assert.Empty(variableResult.Diagnostics);
        Assert.Equal(DeclarationModifier.Export, Assert.IsType<VariableDeclarationStatementSyntax>(variableResult.Statement).Modifier);

        Assert.Empty(wrapperResult.Diagnostics);
        Assert.Equal(DeclarationModifier.Shy, Assert.IsType<FunctionDefinitionStatementSyntax>(wrapperResult.Statement).Modifier);

        Assert.Empty(functionResult.Diagnostics);
        Assert.Equal(DeclarationModifier.Global, Assert.IsType<FunctionDefinitionStatementSyntax>(functionResult.Statement).Modifier);

        Assert.Empty(usingResult.Diagnostics);
        Assert.Equal(DeclarationModifier.Global, Assert.IsType<UsingStatementSyntax>(usingResult.Statement).Modifier);

        Assert.Empty(requireResult.Diagnostics);
        Assert.Equal(DeclarationModifier.Shy, Assert.IsType<RequireStatementSyntax>(requireResult.Statement).Modifier);
    }

    [Fact]
    public void Parser_supports_native_bind_blocks()
    {
        var result = ToshParser.Parse(
            """
            bind LibC {
                func abs(int) -> int
                func myAbs(value: int) -> int as "abs"
            }
            """);

        Assert.Empty(result.Diagnostics);
        var bind = Assert.IsType<BindStatementSyntax>(result.Statement);
        Assert.Equal("LibC", bind.ModuleName);
        Assert.Null(bind.NativeTarget);
        Assert.Equal(2, bind.Functions.Count);
        Assert.Equal("abs", bind.Functions[0].Name);
        Assert.Equal("arg1", bind.Functions[0].Parameters[0].Name);
        Assert.Equal("int", bind.Functions[0].Parameters[0].TypeName);
        Assert.Null(bind.Functions[0].CallingConventionName);
        Assert.Equal("myAbs", bind.Functions[1].Name);
        Assert.Equal("abs", bind.Functions[1].SymbolName);
        Assert.Equal("value", bind.Functions[1].Parameters[0].Name);
    }

    [Fact]
    public void Parser_supports_inline_native_bind_blocks()
    {
        var result = ToshParser.Parse(
            """
            bind native "libc.so.6" as LibC {
                func abs(int) -> int
            }
            """);

        Assert.Empty(result.Diagnostics);
        var bind = Assert.IsType<BindStatementSyntax>(result.Statement);
        Assert.Equal("LibC", bind.ModuleName);
        Assert.Equal("libc.so.6", bind.NativeTarget);
        Assert.Single(bind.Functions);
        Assert.Equal("abs", bind.Functions[0].Name);
    }

    [Fact]
    public void Parser_supports_native_binding_calling_conventions()
    {
        var result = ToshParser.Parse(
            """
            bind User32 {
                func MessageBoxW(nint, string, string, uint) -> int callconv stdcall
            }
            """);

        Assert.Empty(result.Diagnostics);
        var bind = Assert.IsType<BindStatementSyntax>(result.Statement);
        Assert.Equal("stdcall", bind.Functions[0].CallingConventionName);
    }

    [Fact]
    public void Parser_supports_module_enum_and_record_definitions()
    {
        var moduleResult = ToshParser.Parse("export module Inventory { export func load() { echo ok } }");
        var enumResult = ToshParser.Parse("enum StockState: int { Unknown = 0, Low = 1, Ok = 2 }");
        var recordResult = ToshParser.Parse("record Item(name: string, quantity: int, category?: string = \"Food\")");

        Assert.Empty(moduleResult.Diagnostics);
        var module = Assert.IsType<ModuleDefinitionStatementSyntax>(moduleResult.Statement);
        Assert.Equal("Inventory", module.Name);
        Assert.Equal(DeclarationModifier.Export, module.Modifier);

        Assert.Empty(enumResult.Diagnostics);
        var @enum = Assert.IsType<EnumDefinitionStatementSyntax>(enumResult.Statement);
        Assert.Equal("StockState", @enum.Name);
        Assert.Equal("int", @enum.UnderlyingTypeName);
        Assert.Equal(3, @enum.Members.Count);

        Assert.Empty(recordResult.Diagnostics);
        var record = Assert.IsType<RecordDefinitionStatementSyntax>(recordResult.Statement);
        Assert.Equal("Item", record.Name);
        Assert.Equal(3, record.Fields.Count);
        Assert.Equal("string", record.Fields[0].TypeName);
        Assert.Equal("int", record.Fields[1].TypeName);
        Assert.True(record.Fields[2].IsOptional);
    }

    [Fact]
    public void Parser_desugars_dotted_module_names_into_nested_partial_modules()
    {
        var result = ToshParser.Parse("module Foo.Bar.Baz { var x = 1 }");

        Assert.Empty(result.Diagnostics);
        var outer = Assert.IsType<ModuleDefinitionStatementSyntax>(result.Statement);
        Assert.Equal("Foo", outer.Name);
        Assert.True(outer.IsPartial, "outer wrapper of dotted module should be partial");
        Assert.Single(outer.Body.Statements);

        var middle = Assert.IsType<ModuleDefinitionStatementSyntax>(outer.Body.Statements[0]);
        Assert.Equal("Bar", middle.Name);
        Assert.True(middle.IsPartial);
        Assert.Single(middle.Body.Statements);

        var inner = Assert.IsType<ModuleDefinitionStatementSyntax>(middle.Body.Statements[0]);
        Assert.Equal("Baz", inner.Name);
        Assert.False(inner.IsPartial, "innermost module inherits the user's `partial` keyword (none here)");
    }

    [Fact]
    public void Parser_supports_partial_module_keyword()
    {
        var result = ToshParser.Parse("partial module Lib { var a = 1 }");

        Assert.Empty(result.Diagnostics);
        var module = Assert.IsType<ModuleDefinitionStatementSyntax>(result.Statement);
        Assert.Equal("Lib", module.Name);
        Assert.True(module.IsPartial);
    }

    [Fact]
    public async Task Partial_modules_merge_within_the_same_scope()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            """
            partial module Lib { var a = 1 }
            partial module Lib { var b = 2 }
            echo Lib.a
            echo Lib.b
            """);

        Assert.Equal(2, results.Count);
        Assert.Equal(1, Convert.ToInt32(results[0]));
        Assert.Equal(2, Convert.ToInt32(results[1]));
    }

    [Fact]
    public async Task Dotted_module_names_create_nested_modules()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            """
            module App.Math { var pi = 3 }
            module App.Text { var greeting = "hi" }
            echo App.Math.pi
            echo App.Text.greeting
            """);

        Assert.Equal(2, results.Count);
        Assert.Equal(3, Convert.ToInt32(results[0]));
        Assert.Equal("hi", results[1]);
    }

    [Fact]
    public void Parser_supports_newline_separated_top_level_statements()
    {
        var result = ToshParser.Parse("func ll => ls -la\nfunc recent(days: TimeSpan) -> FileSystemEntry { ls -la | where _.Modified > ((date now) - $days) }");

        Assert.Empty(result.Diagnostics);
        var script = Assert.IsType<ScriptStatementSyntax>(result.Statement);
        Assert.Collection(
            script.Statements,
            statement => Assert.IsType<FunctionDefinitionStatementSyntax>(statement),
            statement => Assert.IsType<FunctionDefinitionStatementSyntax>(statement));
    }

    [Fact]
    public void Parser_preserves_command_arguments_after_a_newline_statement_boundary()
    {
        var result = ToshParser.Parse("func ll => ls -la\nwhich ll | get Kind");

        Assert.Empty(result.Diagnostics);
        var script = Assert.IsType<ScriptStatementSyntax>(result.Statement);
        var pipeline = Assert.IsType<PipelineStatementSyntax>(script.Statements[1]);
        var getCommand = Assert.IsType<CommandSyntax>(pipeline.Pipeline.Stages[1]);
        var kindArgument = Assert.IsType<BarewordArgumentSyntax>(Assert.Single(getCommand.Arguments));

        Assert.Equal("get", getCommand.Name);
        Assert.Equal("Kind", kindArgument.Value);
    }

    [Fact]
    public void Parser_supports_function_wrappers_and_new_control_flow_forms()
    {
        var wrapperResult = ToshParser.Parse("func llf => ls -la");
        var untilResult = ToshParser.Parse("until (($done == true)) { echo waiting }");
        var throwResult = ToshParser.Parse("throw \"boom\"");
        var tryResult = ToshParser.Parse("try { throw \"boom\" } catch (err) { echo $err } finally { echo done }");
        var switchResult = ToshParser.Parse("switch ($kind) { case file { echo file } default { echo other } }");
        var matchResult = ToshParser.Parse("var label = match ($kind) { file => \"file\"; default => \"other\" }");

        Assert.Empty(wrapperResult.Diagnostics);
        var wrapper = Assert.IsType<FunctionDefinitionStatementSyntax>(wrapperResult.Statement);
        Assert.Empty(wrapper.Parameters);

        Assert.Empty(untilResult.Diagnostics);
        Assert.IsType<UntilStatementSyntax>(untilResult.Statement);

        Assert.Empty(throwResult.Diagnostics);
        Assert.IsType<ThrowStatementSyntax>(throwResult.Statement);

        Assert.Empty(tryResult.Diagnostics);
        Assert.IsType<TryStatementSyntax>(tryResult.Statement);

        Assert.Empty(switchResult.Diagnostics);
        Assert.IsType<SwitchStatementSyntax>(switchResult.Statement);

        Assert.Empty(matchResult.Diagnostics);
        var matchDeclaration = Assert.IsType<VariableDeclarationStatementSyntax>(matchResult.Statement);
        var matchValue = Assert.IsType<ExpressionPipelineStageSyntax>(Assert.Single(matchDeclaration.Value!.Stages)).Expression;
        Assert.IsType<MatchArgumentSyntax>(matchValue);
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
    public void Parser_supports_callable_invocation_postfix_on_variables()
    {
        var result = ToshParser.Parse("echo $double(21)");

        Assert.Empty(result.Diagnostics);

        var command = Assert.Single(result.Pipeline.Commands);
        var invocation = Assert.IsType<CallableInvocationArgumentSyntax>(Assert.Single(command.Arguments));
        Assert.Equal("double", Assert.IsType<VariableReferenceArgumentSyntax>(invocation.Target).Name);
        Assert.Collection(
            invocation.Arguments,
            argument => Assert.Equal(21, Assert.IsType<LiteralArgumentSyntax>(argument).Value));
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

        var list = Assert.IsType<ArrayLiteralArgumentSyntax>(staticCall.Arguments[1]);
        Assert.Collection(
            list.Items,
            item => Assert.Equal("Hello", Assert.IsType<LiteralArgumentSyntax>(item).Value),
            item => Assert.Equal("World", Assert.IsType<LiteralArgumentSyntax>(item).Value));
    }

    [Fact]
    public void Parser_requires_new_for_constructor_style_type_invocation_expressions()
    {
        var result = ToshParser.Parse("var pt = new Point(2, 2)");

        Assert.Empty(result.Diagnostics);

        var declaration = Assert.IsType<VariableDeclarationStatementSyntax>(result.Statement);
        var expressionStage = Assert.IsType<ExpressionPipelineStageSyntax>(Assert.Single(declaration.Value!.Stages));
        var typeInvocation = Assert.IsType<NewObjectArgumentSyntax>(expressionStage.Expression);

        Assert.Equal("Point", typeInvocation.TypeName);
        Assert.Collection(
            typeInvocation.Arguments,
            argument => Assert.Equal(2, Assert.IsType<LiteralArgumentSyntax>(argument).Value),
            argument => Assert.Equal(2, Assert.IsType<LiteralArgumentSyntax>(argument).Value));
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
        var result = ToshParser.Parse("ls | where _.Name.ToString().ToLower().Contains(\".x\") | get Name");

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
        Assert.Equal("_", Assert.IsType<VariableReferenceArgumentSyntax>(nameAccess.Target).Name);
    }

    [Fact]
    public void Parser_keeps_simple_where_comparison_values_as_literals()
    {
        var result = ToshParser.Parse("ls | where Type == file | get Name");

        Assert.Empty(result.Diagnostics);

        var whereCommand = result.Pipeline.Commands[1];
        var predicate = Assert.IsType<BlockArgumentSyntax>(Assert.Single(whereCommand.Arguments));
        var statement = Assert.IsType<PipelineStatementSyntax>(Assert.Single(predicate.Block.Statements));
        var stage = Assert.IsType<ExpressionPipelineStageSyntax>(Assert.Single(statement.Pipeline.Stages));
        var comparison = Assert.IsType<OperatorArgumentSyntax>(stage.Expression);

        Assert.Equal("==", comparison.Operator);

        var left = Assert.IsType<MemberAccessArgumentSyntax>(comparison.Left);
        Assert.Equal("Type", left.MemberPath);
        Assert.Equal("_", Assert.IsType<VariableReferenceArgumentSyntax>(left.Target).Name);

        var right = Assert.IsType<BarewordArgumentSyntax>(comparison.Right);
        Assert.Equal("file", right.Value);
    }

    [Fact]
    public void Parser_supports_advanced_where_boolean_expressions_and_array_literals()
    {
        var result = ToshParser.Parse("ls | where { (_.Type == file) and not (_.Type == dir) and (_.Owner == [root, komrad]) } | get Name");

        Assert.Empty(result.Diagnostics);

        var whereCommand = result.Pipeline.Commands[1];
        var predicate = Assert.IsType<BlockArgumentSyntax>(Assert.Single(whereCommand.Arguments));
        var statement = Assert.IsType<PipelineStatementSyntax>(Assert.Single(predicate.Block.Statements));
        var stage = Assert.IsType<ExpressionPipelineStageSyntax>(Assert.Single(statement.Pipeline.Stages));
        var topLevelAnd = Assert.IsType<OperatorArgumentSyntax>(stage.Expression);

        Assert.Equal("and", topLevelAnd.Operator);
        Assert.IsType<ArrayLiteralArgumentSyntax>(Assert.IsType<OperatorArgumentSyntax>(topLevelAnd.Right).Right);
    }

    [Fact]
    public void Parser_supports_each_blocks_with_expression_statements()
    {
        var result = ToshParser.Parse("ls | each { _.Name; _.Extension }");

        Assert.Empty(result.Diagnostics);

        var eachCommand = result.Pipeline.Commands[1];
        Assert.Equal("each", eachCommand.Name);
        var block = Assert.IsType<BlockArgumentSyntax>(Assert.Single(eachCommand.Arguments));
        Assert.Equal(2, block.Block.Statements.Count);

        var firstStatement = Assert.IsType<PipelineStatementSyntax>(block.Block.Statements[0]);
        var firstStage = Assert.IsType<ExpressionPipelineStageSyntax>(Assert.Single(firstStatement.Pipeline.Stages));
        var memberAccess = Assert.IsType<MemberAccessArgumentSyntax>(firstStage.Expression);
        Assert.Equal("Name", memberAccess.MemberPath);
        Assert.Equal("_", Assert.IsType<VariableReferenceArgumentSyntax>(memberAccess.Target).Name);
    }

    [Fact]
    public void Parser_supports_index_access_expressions()
    {
        var result = ToshParser.Parse("echo $x[3]");

        Assert.Empty(result.Diagnostics);

        var command = Assert.Single(result.Pipeline.Commands);
        var indexAccess = Assert.IsType<IndexAccessArgumentSyntax>(Assert.Single(command.Arguments));
        Assert.Equal(IndexLookupKind.Default, indexAccess.LookupKind);
        Assert.Equal("x", Assert.IsType<VariableReferenceArgumentSyntax>(indexAccess.Target).Name);
        Assert.Equal(3, Assert.IsType<LiteralArgumentSyntax>(indexAccess.Index).Value);
    }

    [Fact]
    public void Parser_supports_chained_index_access_and_member_access()
    {
        var result = ToshParser.Parse("echo $rows[0].Name");

        Assert.Empty(result.Diagnostics);

        var command = Assert.Single(result.Pipeline.Commands);
        var memberAccess = Assert.IsType<MemberAccessArgumentSyntax>(Assert.Single(command.Arguments));
        Assert.Equal("Name", memberAccess.MemberPath);

        var indexAccess = Assert.IsType<IndexAccessArgumentSyntax>(memberAccess.Target);
        Assert.Equal(IndexLookupKind.Default, indexAccess.LookupKind);
        Assert.Equal("rows", Assert.IsType<VariableReferenceArgumentSyntax>(indexAccess.Target).Name);
        Assert.Equal(0, Assert.IsType<LiteralArgumentSyntax>(indexAccess.Index).Value);
    }

    [Fact]
    public void Parser_supports_explicit_key_and_value_lookup_index_syntax()
    {
        var keyResult = ToshParser.Parse("echo $map[Name,]");
        var valueResult = ToshParser.Parse("echo $map[,42]");

        Assert.Empty(keyResult.Diagnostics);
        Assert.Empty(valueResult.Diagnostics);

        var keyIndex = Assert.IsType<IndexAccessArgumentSyntax>(Assert.Single(Assert.Single(keyResult.Pipeline.Commands).Arguments));
        Assert.Equal(IndexLookupKind.ByKey, keyIndex.LookupKind);

        var valueIndex = Assert.IsType<IndexAccessArgumentSyntax>(Assert.Single(Assert.Single(valueResult.Pipeline.Commands).Arguments));
        Assert.Equal(IndexLookupKind.ByValue, valueIndex.LookupKind);
    }

    [Fact]
    public void Parser_supports_newline_separated_block_statements()
    {
        var result = ToshParser.Parse(
            """
            ls | each {
                _.Name
                _.Extension
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
    public void Parser_supports_unquoted_comma_joined_command_arguments()
    {
        var result = ToshParser.Parse("ls --show Name,FullName");

        Assert.Empty(result.Diagnostics);

        var command = Assert.Single(result.Pipeline.Commands);
        Assert.Collection(
            command.Arguments,
            argument => Assert.Equal("--show", Assert.IsType<BarewordArgumentSyntax>(argument).Value),
            argument => Assert.Equal("Name,FullName", Assert.IsType<BarewordArgumentSyntax>(argument).Value));
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
        var result = ToshParser.Parse("while (($count < 3)) { echo $count; $count = ($count + 1) }");

        Assert.Empty(result.Diagnostics);

        var statement = Assert.IsType<WhileStatementSyntax>(result.Statement);
        Assert.IsType<OperatorArgumentSyntax>(statement.Condition);
        Assert.Equal(2, statement.Body.Statements.Count);
    }

    [Fact]
    public async Task Type_of_returns_the_runtime_type_for_each_object()
    {
        var engine = ShellEngine.CreateFullShell();

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
        var engine = ShellEngine.CreateFullShell();

        var dateResults = await engine.ExecuteToListAsync("date parse 2026-03-23T14:05:00Z | type-of");
        var spanResults = await engine.ExecuteToListAsync("timespan 1w2d4h5m23s | type-of");
        var calendarResults = await engine.ExecuteToListAsync("timespan 1y2mo | type-of");

        Assert.Collection(dateResults, item => Assert.Equal(typeof(DateTimeOffset), item));
        Assert.Collection(spanResults, item => Assert.Equal(typeof(TimeSpan), item));
        Assert.Collection(calendarResults, item => Assert.Equal(typeof(TemporalAmount), item));
    }

    [Fact]
    public async Task Date_command_and_cast_can_extract_dateonly_and_timeonly_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var dateOnlyCommandResults = await engine.ExecuteToListAsync("date date-only 2026-03-23T14:05:00Z | type-of");
        var timeOnlyCommandResults = await engine.ExecuteToListAsync("date time-only 2026-03-23T14:05:00Z | type-of");
        var dateOnlyCastResults = await engine.ExecuteToListAsync("date parse 2026-03-23T14:05:00Z | cast dateonly | type-of");
        var timeOnlyCastResults = await engine.ExecuteToListAsync("date parse 2026-03-23T14:05:00Z | cast timeonly | type-of");
        var projectedResults = await engine.ExecuteToListAsync("date parse 2026-03-23T14:05:00Z -d -t | type-of");

        Assert.Collection(dateOnlyCommandResults, item => Assert.Equal(typeof(DateOnly), item));
        Assert.Collection(timeOnlyCommandResults, item => Assert.Equal(typeof(TimeOnly), item));
        Assert.Collection(dateOnlyCastResults, item => Assert.Equal(typeof(DateOnly), item));
        Assert.Collection(timeOnlyCastResults, item => Assert.Equal(typeof(TimeOnly), item));
        Assert.Collection(
            projectedResults,
            item => Assert.Equal(typeof(DateOnly), item),
            item => Assert.Equal(typeof(TimeOnly), item));
    }

    [Fact]
    public async Task Operator_expressions_can_produce_typed_date_results()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo ((date now) - (timespan 2d)) | type-of");

        Assert.Collection(results, item => Assert.Equal(typeof(DateTimeOffset), item));
    }

    [Fact]
    public async Task Bare_temporal_literals_can_bind_typed_expression_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var spanResults = await engine.ExecuteToListAsync("var span = 2d\necho $span | type-of");
        var dateResults = await engine.ExecuteToListAsync("var dt = 2026-03-25\necho $dt | type-of");
        var amountResults = await engine.ExecuteToListAsync("var lease = 1y2mo\necho $lease | type-of");

        Assert.Collection(spanResults, item => Assert.Equal(typeof(TimeSpan), item));
        Assert.Collection(dateResults, item => Assert.Equal(typeof(DateTimeOffset), item));
        Assert.Collection(amountResults, item => Assert.Equal(typeof(TemporalAmount), item));
    }

    [Fact]
    public async Task Bare_ip_literals_can_bind_typed_expression_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var ipv4Results = await engine.ExecuteToListAsync("var loopback = 127.0.0.1\necho $loopback | type-of");
        var ipv6Results = await engine.ExecuteToListAsync("var loopback6 = ::1\necho $loopback6 | type-of");

        Assert.Collection(ipv4Results, item => Assert.Equal(typeof(System.Net.IPAddress), item));
        Assert.Collection(ipv6Results, item => Assert.Equal(typeof(System.Net.IPAddress), item));
    }

    [Fact]
    public async Task Typed_ipaddress_parameters_accept_string_arguments()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            "func family(address: ipaddress) { echo $address }\nfamily \"127.0.0.1\" | type-of");

        Assert.Collection(results, item => Assert.Equal(typeof(System.Net.IPAddress), item));
    }

    [Fact]
    public async Task Date_command_accepts_direct_iso_values_and_calendar_arithmetic()
    {
        var engine = ShellEngine.CreateFullShell();

        var parsedResults = await engine.ExecuteToListAsync("date 2026-03-25T14:05:00Z | type-of");
        var shiftedResults = await engine.ExecuteToListAsync("var next = ((date 2026-01-31) + 1mo)\necho $next.Month $next.Day");

        Assert.Collection(parsedResults, item => Assert.Equal(typeof(DateTimeOffset), item));
        Assert.Equal(new object?[] { 2, 28 }, shiftedResults.ToArray());
    }

    [Fact]
    public async Task Can_construct_and_invoke_dotnet_objects()
    {
        var engine = ShellEngine.CreateFullShell();

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
        var assignmentResults = await engine.ExecuteToListAsync("$answer = 2");
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

        await engine.ExecuteToListAsync("var files = ls | where _.Type == file");
        var results = await engine.ExecuteToListAsync("$files | get Name");

        Assert.Equal(
            new[] { "keep.txt", "skip.log" },
            results.Cast<string>().OrderBy(name => name).ToArray());
    }

    [Fact]
    public async Task Result_is_available_between_successful_script_statements()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("echo toast\necho $tosh.Last.Result");

        Assert.Equal(["toast", "toast"], results.Cast<string>().ToArray());
        Assert.Equal("toast", runtime.LastResult);
    }

    [Fact]
    public async Task Result_stores_multi_value_results_as_a_collection_object()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("echo one two");
        var result = Assert.IsType<object[]>(runtime.LastResult);
        var flattened = await engine.ExecuteToListAsync("$tosh.Last.Result | flatten");

        Assert.Equal(["one", "two"], result.Cast<string>().ToArray());
        Assert.Equal(["one", "two"], flattened.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Result_is_available_inside_function_bodies_between_statements()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            func remember() {
                echo one two
                return $tosh.Last.Result.Length
            }
            """);

        var results = await engine.ExecuteToListAsync("remember");

        Assert.Equal(3, results.Count);
        Assert.Equal("one", results[0]);
        Assert.Equal("two", results[1]);
        Assert.Equal(2, results[2]);
        Assert.IsType<object[]>(runtime.LastResult);
    }

    [Fact]
    public async Task Failed_commands_do_not_overwrite_result()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("echo toast");
        await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("definitely_not_a_tosh_command"));
        var results = await engine.ExecuteToListAsync("echo $tosh.Last.Result");

        Assert.Collection(results, item => Assert.Equal("toast", item));
        Assert.Equal("toast", runtime.LastResult);
    }

    [Fact]
    public async Task Commands_are_case_sensitive()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("LS"));
        await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("ECHO toast"));
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
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo new System.Text.StringBuilder(\"hello\").Append(\" world\").Length");

        Assert.Collection(results, item => Assert.Equal(11, item));
    }

    [Fact]
    public async Task Literal_method_call_expressions_can_transform_strings()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo \"Hello\".ToLower()");

        Assert.Collection(results, item => Assert.Equal("hello", item));
    }

    [Fact]
    public async Task Static_method_call_expressions_can_use_list_literals()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo String.Join(\" \", [\"Hello\", \"World\"]).ToLower()");

        Assert.Collection(results, item => Assert.Equal("hello world", item));
    }

    [Fact]
    public async Task Static_member_access_expressions_can_resolve_dotnet_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var nowTypeResults = await engine.ExecuteToListAsync("echo DateTime.Now | type-of");
        var emptyLengthResults = await engine.ExecuteToListAsync("echo String.Empty.Length");

        Assert.Collection(nowTypeResults, item => Assert.Equal(typeof(DateTime), item));
        Assert.Collection(emptyLengthResults, item => Assert.Equal(0, item));
    }

    [Fact]
    public async Task Static_member_access_can_chain_into_instance_method_calls()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo DateTime.Now.AddDays(-2) | type-of");

        Assert.Collection(results, item => Assert.Equal(typeof(DateTime), item));
    }

    [Fact]
    public async Task Static_method_call_expressions_support_params_array_overloads()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo String.Join(\" \", \"Hello\", \"World\")");

        Assert.Collection(results, item => Assert.Equal("Hello World", item));
    }

    [Fact]
    public async Task Get_can_project_multiple_members_into_queryable_shell_records()
    {
        var engine = ShellEngine.CreateFullShell();

        var projectedResults = await engine.ExecuteToListAsync(
            "echo new Tosh.Runtime.ProcessInfo(2, \"large\", false, null, null, 4096, null, null) new Tosh.Runtime.ProcessInfo(1, \"small\", false, null, null, 1024, null, null) | get { Name, PID, Memory }");
        var sortedNameResults = await engine.ExecuteToListAsync(
            "echo new Tosh.Runtime.ProcessInfo(2, \"large\", false, null, null, 4096, null, null) new Tosh.Runtime.ProcessInfo(1, \"small\", false, null, null, 1024, null, null) | get { Name, PID, Memory } | sort PID | get Name");

        var firstProjection = Assert.IsAssignableFrom<IDictionary<string, object?>>(projectedResults[0]);
        Assert.True(firstProjection.TryGetValue("Name", out var projectedName));
        Assert.True(firstProjection.TryGetValue("PID", out var projectedPid));
        Assert.True(firstProjection.TryGetValue("Memory", out var projectedMemory));
        Assert.Equal("large", projectedName);
        Assert.Equal(2, projectedPid);
        Assert.IsType<StorageSize>(projectedMemory);
        Assert.Equal(new[] { "small", "large" }, sortedNameResults.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Member_assignment_can_update_expando_members()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("using System.Dynamic; var person = new ExpandoObject(); $person.Name = \"toast\"; $person.Age = 7");
        var results = await engine.ExecuteToListAsync("echo $person | get { Name, Age }");

        var record = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(results));
        Assert.Equal("toast", record["Name"]);
        Assert.Equal(7, record["Age"]);
    }

    [Fact]
    public async Task Bare_var_declaration_can_materialize_expando_on_first_member_assignment()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("var person\n$person.Name = \"komrad\"\n$person.Uid = 1000");
        var results = await engine.ExecuteToListAsync("echo $person | get { Name, Uid }");

        var record = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(results));
        Assert.Equal("komrad", record["Name"]);
        Assert.Equal(1000, record["Uid"]);
    }

    [Fact]
    public async Task Allocated_variables_can_be_assigned_with_dollar_prefixed_syntax()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("var address\n$address = \"123 Somewhere St.\"");
        var results = await engine.ExecuteToListAsync("echo $address");

        Assert.Collection(results, item => Assert.Equal("123 Somewhere St.", item));
    }

    [Fact]
    public async Task Member_assignment_can_materialize_nested_expando_paths()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("var person\n$person.Name.First = \"Komrad\"\n$person.Name.Last = \"Toast\"");
        var results = await engine.ExecuteToListAsync("echo $person | get { Name.First, Name.Last }");

        var record = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(results));
        Assert.Equal("Komrad", record["Name.First"]);
        Assert.Equal("Toast", record["Name.Last"]);
    }

    [Fact]
    public async Task Record_literals_create_expando_records()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("var kitty = {| Name = \"Loki\", Age = \"1y2m\" |}");
        var results = await engine.ExecuteToListAsync("echo $kitty | get { Name, Age }");

        var record = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(results));
        Assert.Equal("Loki", record["Name"]);
        Assert.Equal("1y2m", record["Age"]);
    }

    [Fact]
    public async Task Array_literals_create_dotnet_arrays()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("var items = [\"one\", \"two\"]");
        var results = await engine.ExecuteToListAsync("echo $items.Length\ntype-of $items | get Name");

        Assert.Equal(2, results[0]);
        Assert.Equal("array<string>", results[1]);
    }

    [Fact]
    public async Task Built_in_collection_type_aliases_construct_shell_friendly_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var values = new list(1, 2, 3)
            var items = new array(1, 2, 3)
            var tags = new set(one, two, two)
            var meta = new dict(Name, "Toast", Uid, 1000)
            var mapped = new map({| Name = "Toast", Uid = 2000 |})
            var hash = new hashtable(Name, "Toast", Uid, 3000)
            var table = new table(Name, "Toast", Uid, 1000)
            var pair = new tuple(alpha, 42)
            type-of $values | get Name
            type-of $items | get Name
            type-of $tags | get Name
            type-of $meta | get Name
            type-of $mapped | get Name
            type-of $hash | get Name
            type-of $table | get Name
            type-of $pair | get Name
            echo $pair.Item2
            echo $meta.Uid
            echo $mapped.Uid
            echo $hash.Uid
            echo $table.Name
            """);

        Assert.Equal("list<int>", results[0]);
        Assert.Equal("array<int>", results[1]);
        Assert.Equal("set<string>", results[2]);
        Assert.Equal("dict", results[3]);
        Assert.Equal("dict", results[4]);
        Assert.Equal("hashtable", results[5]);

        // `new table(...)` above still constructs — `table` is a retained alias —
        // but the type answers `record` since TS-P3-11.
        Assert.Equal("record", results[6]);
        Assert.Equal("tuple", results[7]);
        Assert.Equal(42, results[8]);
        Assert.Equal(1000, results[9]);
        Assert.Equal(2000, results[10]);
        Assert.Equal(3000, results[11]);
        Assert.Equal("Toast", results[12]);
    }

    [Fact]
    public async Task Generic_shell_collection_aliases_support_angle_bracket_construction_and_casting()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var items = new list<String>("one", "two")
            var array = new array<int>(1, 2, 3)
            var meta = new dict<string, int>(One, 1, Two, 2)
            type-of $items | get Name
            echo $items.Count
            type-of $array | get Name
            echo $array.Length
            type-of $meta | get Name
            echo $meta.Two
            echo [1, 2, 3] | cast list<int> | type-of | get Name
            constructors list<int> | first | get Type
            """);

        Assert.Equal("list<string>", results[0]);
        Assert.Equal(2, results[1]);
        Assert.Equal("array<int>", results[2]);
        Assert.Equal(3, results[3]);
        Assert.Equal("dict<string, int>", results[4]);
        Assert.Equal(2, results[5]);
        Assert.Equal("list<int>", results[6]);
        Assert.Equal("list<int>", results[7]);
    }

    [Fact]
    public async Task Command_style_new_and_types_surface_shell_collection_types()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var words = new list<string> one two
            type-of $words | get Name
            types map | where _.Namespace == ToSh | first | get Name
            """);

        Assert.Equal("list<string>", results[0]);
        Assert.Equal("dict", results[1]);
    }

    [Fact]
    public async Task Command_style_new_supports_unquoted_generic_clr_type_names()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var tuple = new System.Tuple<int,string,bool> 1 beta true
            echo $tuple.Item1 $tuple.Item2 $tuple.Item3
            """);

        Assert.Equal(1, results[0]);
        Assert.Equal("beta", results[1]);
        Assert.Equal(true, results[2]);
    }

    [Fact]
    public async Task Constructors_command_surfaces_implicit_default_constructor_for_value_types()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            constructors System.Drawing.Color | first | get Signature
            """);

        Assert.Equal("System.Drawing.Color()", results[0]);
    }

    [Fact]
    public async Task Ternary_operator_selects_branches_lazily_and_binds_looser_than_null_coalescing()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var user = null
            var label = (($user != null) ? $user.Name : "guest")
            var state = (null ?? true) ? "yes" : "no"
            echo $label
            echo $state
            """);

        Assert.Equal("guest", results[0]);
        Assert.Equal("yes", results[1]);
    }

    [Fact]
    public async Task Method_calls_can_materialize_missing_record_list_members()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("var person\nvar kitty = {| Name = \"Loki\" |}\n$person.Pets.Add($kitty)");
        var countResults = await engine.ExecuteToListAsync("echo $person.Pets.Count");
        var nameResults = await engine.ExecuteToListAsync("echo $person.Pets | flatten | get Name");

        Assert.Collection(countResults, item => Assert.Equal(1, item));
        Assert.Collection(nameResults, item => Assert.Equal("Loki", item));
    }

    [Fact]
    public async Task Member_assignment_can_update_settable_clr_properties()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("var builder = new System.Text.StringBuilder(\"toast\"); $builder.Length = 2");
        var results = await engine.ExecuteToListAsync("echo $builder.ToString()");

        Assert.Collection(results, item => Assert.Equal("to", item));
    }

    [Fact]
    public async Task Runtime_requires_dollar_prefixed_member_assignments_after_declaration()
    {
        // `TS-P2-51` moved this from the parser to the engine. `person.Name = "x"` and
        // `B.S = 5` are one shape, and only the engine can say which is which — so the hint is
        // raised where the *read* of the same spelling has always raised it. See
        // StaticMemberAssignmentTests for the pair.
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("var person = {| Name = \"ada\" |}");
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("person.Name = \"toast\""));
        var diagnostic = Assert.Single(exception.Diagnostics);

        Assert.Equal("tosh.runtime.variable_reference_requires_dollar", diagnostic.Code);
        Assert.Contains("$person.Name", diagnostic.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_suggests_dollar_prefixed_variable_references_when_a_variable_name_is_used_as_a_command()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("var person = {| Name = \"toast\" |}");
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("person.Name"));
        var diagnostic = Assert.Single(exception.Diagnostics);

        Assert.Equal("tosh.runtime.variable_reference_requires_dollar", diagnostic.Code);
        Assert.Contains("$person.Name", diagnostic.Label, StringComparison.Ordinal);
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
    public async Task Wrapper_functions_can_forward_all_call_arguments_to_the_wrapped_command()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("""
            func test1(a, b, c) {
                echo String.Join(":", [$a, $b, $c])
            }
            func t1 => test1
            """);
        var results = await engine.ExecuteToListAsync("t1 one two three");

        Assert.Collection(results, item => Assert.Equal("one:two:three", item));
    }

    [Fact]
    public async Task Wrapper_functions_can_expand_commands_and_forward_call_arguments()
    {
        using var tempDirectory = new TemporaryDirectory();
        var nestedPath = System.IO.Path.Combine(tempDirectory.Path, "nested");
        Directory.CreateDirectory(nestedPath);
        File.WriteAllText(System.IO.Path.Combine(nestedPath, "keep.txt"), "keep");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("func ll => ls -la");
        var results = await engine.ExecuteToListAsync("ll nested | get Name");

        Assert.Collection(results, item => Assert.Equal("keep.txt", item));
    }

    [Fact]
    public async Task Wrapper_functions_can_bind_implicit_positional_parameters_from_the_arrow_body()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("""
            func test1(a, b, c) {
                echo String.Join(":", [$a, $b, $c])
            }
            func t1 => test1 $1 "Jim" $2
            """);
        var results = await engine.ExecuteToListAsync("t1 one two");

        Assert.Collection(results, item => Assert.Equal("one:Jim:two", item));
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

        await engine.ExecuteToListAsync("func bigger(size: StorageSize) { where _.Size >= $size }");
        var results = await engine.ExecuteToListAsync("ls -la | bigger 1kb | get Name");

        Assert.Collection(results, item => Assert.Equal("big.txt", item));
    }

    [Fact]
    public async Task Functions_can_convert_emitted_values_to_a_declared_return_type()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("func stringifyCount() -> String { count }");
        var results = await engine.ExecuteToListAsync("echo 1 2 3 | stringifyCount");

        Assert.Collection(results, item => Assert.Equal("3", item));
    }

    [Fact]
    public async Task Return_exits_functions_early_with_a_value()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("func choose() { return \"done\"; echo never }");
        var results = await engine.ExecuteToListAsync("choose");

        Assert.Collection(results, item => Assert.Equal("done", item));
    }

    [Fact]
    public async Task Return_can_forward_pipeline_values_from_the_current_function_input()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("func names() { return get Name }");
        var results = await engine.ExecuteToListAsync("ls -la | first 2 | names");

        Assert.Equal(2, results.Count);
        Assert.All(results, item => Assert.IsType<string>(item));
    }

    [Fact]
    public async Task Return_without_a_value_stops_function_execution()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("func stop() { return; echo never }");
        var results = await engine.ExecuteToListAsync("stop");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Each_executes_a_block_for_each_input_object()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo Hello World | each { _.ToLower() }");

        Assert.Collection(
            results,
            item => Assert.Equal("hello", item),
            item => Assert.Equal("world", item));
    }

    [Fact]
    public async Task Return_inside_each_blocks_exits_the_enclosing_function()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("func firstLower() { echo Hello World | each { return _.ToLower() }; echo never }");
        var results = await engine.ExecuteToListAsync("firstLower");

        Assert.Collection(results, item => Assert.Equal("hello", item));
    }

    [Fact]
    public async Task Each_blocks_support_continue()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo one skip two | each { if ((_ == skip)) { continue }; echo _ }");

        Assert.Equal(new[] { "one", "two" }, results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Each_blocks_support_break()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo one two three | each { echo _; break }");

        Assert.Collection(results, item => Assert.Equal("one", item));
    }

    [Fact]
    public async Task If_statements_execute_the_true_branch_when_the_condition_is_true()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("if (\"Hello\".Contains(\"H\")) { echo yes }");

        Assert.Collection(results, item => Assert.Equal("yes", item));
    }

    [Fact]
    public async Task If_statements_execute_else_if_and_else_branches()
    {
        var engine = ShellEngine.CreateFullShell();

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
    public async Task Command_substitution_captures_shell_text_output()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo $(/bin/echo hello)");

        Assert.Equal("hello", Assert.Single(results));
    }

    [Fact]
    public async Task Command_substitution_joins_multiple_values_with_newlines()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var text = $(echo one two three)
            echo $text
            """);

        Assert.Equal($"one{Environment.NewLine}two{Environment.NewLine}three", Assert.Single(results));
    }

    [Fact]
    public async Task Command_substitution_works_inside_interpolated_strings()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo $\"value: {$(/bin/echo hello)}\"");

        Assert.Equal("value: hello", Assert.Single(results));
    }

    [Fact]
    public async Task Input_process_substitution_materializes_a_temp_file()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("var path = <(echo alpha beta)\n$path | type-of");

        var type = Assert.IsAssignableFrom<Type>(Assert.Single(results));
        Assert.Equal(typeof(FileSystemEntry), type);
    }

    [Fact]
    public async Task For_loops_iterate_pipeline_sources_and_bind_loop_variables()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("for item in (echo one two three | first 2) { echo $item }");

        Assert.Equal(new[] { "one", "two" }, results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task For_loops_support_continue_and_break()
    {
        var engine = ShellEngine.CreateFullShell();

        var continueResults = await engine.ExecuteToListAsync(
            "for item in (echo one skip two) { if (($item == skip)) { continue }; echo $item }");
        var breakResults = await engine.ExecuteToListAsync(
            "for item in (echo one two three) { echo $item; break; echo never }");

        Assert.Equal(new[] { "one", "two" }, continueResults.Cast<string>().ToArray());
        Assert.Collection(breakResults, item => Assert.Equal("one", item));
    }

    [Fact]
    public async Task For_loops_enumerate_single_collection_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("""
            var items = new list("one", "two", "three")
            for item in ($items) { echo $item }
            """);

        Assert.Equal(new[] { "one", "two", "three" }, results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task For_loops_use_tosh_class_enumeration_hooks()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("""
            class Basket {
                prop Items: object? = new list("Bread", "Coffee")

                shy func enumerate() {
                    return $this.Items
                }
            }

            var basket = new Basket()
            for item in ($basket) { echo $item }
            """);

        Assert.Equal(new[] { "Bread", "Coffee" }, results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task While_loops_re_evaluate_conditions_and_allow_assignments()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("var count = 0");
        var results = await engine.ExecuteToListAsync("while (($count < 3)) { echo $count; $count = ($count + 1) }");
        var finalValue = await engine.ExecuteToListAsync("echo $count");

        Assert.Equal(new int[] { 0, 1, 2 }, results.Select(item => Convert.ToInt32(item)).ToArray());
        Assert.Collection(finalValue, item => Assert.Equal(3, item));
    }

    [Fact]
    public async Task Until_loops_re_evaluate_conditions_and_support_compound_assignment()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("var count = 0");
        var results = await engine.ExecuteToListAsync("until (($count >= 3)) { echo $count; $count += 1 }");
        var finalValue = await engine.ExecuteToListAsync("echo $count");

        Assert.Equal(new int[] { 0, 1, 2 }, results.Select(item => Convert.ToInt32(item)).ToArray());
        Assert.Collection(finalValue, item => Assert.Equal(3, item));
    }

    [Fact]
    public async Task Try_catch_finally_and_throw_work_together()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("try { throw \"boom\" } catch (err) { echo $err } finally { echo done }");

        Assert.Equal(new[] { "boom", "done" }, results.Select(item => item?.ToString() ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task User_class_extending_Error_can_be_thrown_and_caught_with_pattern_match()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("""
            class HttpError(status, url) extends Error {
                prop Status = status
                prop Url = url
            }
            try {
                throw (new HttpError(503, "https://example.test"))
            } catch (err) {
                if ($err is HttpError) {
                    echo $"http {$err.Status} {$err.Url}"
                } else {
                    echo "other"
                }
            }
            """);

        Assert.Collection(results, item => Assert.Equal("http 503 https://example.test", item));
    }

    [Fact]
    public async Task Uncaught_user_error_surfaces_through_diagnostic_renderer()
    {
        var engine = ShellEngine.CreateFullShell();

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("""
            class HttpError(status) extends Error {
                prop Status = $status
            }
            throw (new HttpError(500))
            """));

        var diagnostic = ex.Diagnostics[0];
        Assert.Equal("HttpError", diagnostic.Code);
        Assert.Equal("an error escaped here", diagnostic.Label);
    }

    [Fact]
    public async Task Uncaught_user_error_maps_diagnostic_footer_properties()
    {
        var engine = ShellEngine.CreateFullShell();

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("""
            class ArgumentError(message: string) extends Error {
                prop Code: string = "point.argument"
                prop Title: string = "short label"
                prop Message: string = $message
                prop Label: string = $this.Title
                prop Help: string = "Use another point value or a Numeric scalar."
                prop Information: string => $"ArgumentError: {$this.Message}"
            }

            throw (new ArgumentError("Unsupported operand type: string"))
            """));

        var diagnostic = ex.Diagnostics[0];
        Assert.Equal("point.argument", diagnostic.Code);
        Assert.Equal("Unsupported operand type: string", diagnostic.Title);
        Assert.Equal("short label", diagnostic.Label);
        Assert.Equal("Use another point value or a Numeric scalar.", diagnostic.Help);
        Assert.Equal("ArgumentError: Unsupported operand type: string", diagnostic.Info);
    }

    [Fact]
    public async Task Plain_string_throws_still_produce_runtime_throw_diagnostic()
    {
        var engine = ShellEngine.CreateFullShell();

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("throw \"boom\""));

        var diagnostic = ex.Diagnostics[0];
        Assert.Equal("tosh.runtime.throw", diagnostic.Code);
        Assert.Equal("boom", diagnostic.Title);
    }

    [Fact]
    public async Task User_thrown_ToshError_subclass_can_be_caught_by_concrete_type_from_csharp()
    {
        var engine = ShellEngine.CreateFullShell();

        // Define the type in tosh, then trigger a throw and observe the
        // raw CLR exception type — it must be the user's class, not a
        // ThrowSignalException wrapper.
        await engine.ExecuteToListAsync("""
            class HttpError(status) extends Error {
                prop Status = status
            }
            """);

        var caught = await Record.ExceptionAsync(() =>
            engine.ExecuteToListAsync("throw (new HttpError(404))"));

        Assert.NotNull(caught);
        // The top-level engine boundary wraps any user throw into a
        // diagnostic for pretty-printing, but the InnerException chain
        // (or Diagnostic Title / Code) must preserve the user's type
        // identity.
        var diag = Assert.IsType<ToshDiagnosticException>(caught);
        Assert.Equal("HttpError", diag.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Switch_statements_match_the_first_equal_case()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("var kind = \"file\"");
        var fileResults = await engine.ExecuteToListAsync("switch ($kind) { case file { echo file } case dir { echo dir } default { echo other } }");
        await engine.ExecuteToListAsync("var kind = \"link\"");
        var otherResults = await engine.ExecuteToListAsync("switch ($kind) { case file { echo file } default { echo other } }");

        Assert.Collection(fileResults, item => Assert.Equal("file", item));
        Assert.Collection(otherResults, item => Assert.Equal("other", item));
    }

    [Fact]
    public async Task Match_expressions_select_ordered_arms_support_guards_and_require_default_for_exhaustiveness()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("var kind = \"file\"");
        var fileResults = await engine.ExecuteToListAsync("echo (match ($kind) { file => \"file\"; dir => \"dir\"; default => \"other\" })");
        var guardResults = await engine.ExecuteToListAsync("echo (match (3) { 3 if ((false)) => \"no\"; 3 if ((true)) => \"yes\"; default => \"other\" })");
        var blockResults = await engine.ExecuteToListAsync("echo (match ($kind) { file => { echo chosen }; default => { echo fallback } })");
        var topLevelResults = await engine.ExecuteToListAsync("match (link) { file => \"file\"; default => \"other\" }");
        var defaultKeywordDiagnostic = ToshParser.Parse("match ($kind) { _ => \"other\" }");
        var diagnostic = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("match (link) { file => \"file\" }"));

        Assert.Collection(fileResults, item => Assert.Equal("file", item));
        Assert.Collection(guardResults, item => Assert.Equal("yes", item));
        Assert.Collection(blockResults, item => Assert.Equal("chosen", item));
        Assert.Collection(topLevelResults, item => Assert.Equal("other", item));
        Assert.Contains(defaultKeywordDiagnostic.Diagnostics, entry => entry.Code.Contains("match_default_keyword_required", StringComparison.Ordinal));
        Assert.Contains("non_exhaustive_match", diagnostic.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Operators_support_modulo_membership_and_regex_matching()
    {
        var engine = ShellEngine.CreateFullShell();

        var moduloResults = await engine.ExecuteToListAsync("echo (10 % 3)");
        var inResults = await engine.ExecuteToListAsync("echo (komrad in [root, komrad])");
        var notInResults = await engine.ExecuteToListAsync("echo (komrad not in [root, toast])");
        var regexResults = await engine.ExecuteToListAsync("echo (README.md =~ \"(?i)\\\\.md$\")");
        var notRegexResults = await engine.ExecuteToListAsync("echo (README.txt !~ \"(?i)\\\\.md$\")");
        var compiledRegexResults = await engine.ExecuteToListAsync("echo (README.md =~ (new regex(\"\\\\.md$\", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))");

        Assert.Collection(moduloResults, item => Assert.Equal(1, item));
        Assert.Collection(inResults, item => Assert.Equal(true, item));
        Assert.Collection(notInResults, item => Assert.Equal(true, item));
        Assert.Collection(regexResults, item => Assert.Equal(true, item));
        Assert.Collection(notRegexResults, item => Assert.Equal(true, item));
        Assert.Collection(compiledRegexResults, item => Assert.Equal(true, item));
    }

    [Fact]
    public async Task Break_and_continue_outside_loops_raise_diagnostics()
    {
        var engine = ShellEngine.CreateFullShell();

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
        var blockResults = await engine.ExecuteToListAsync("echo Hello | each { var item = _.ToLower(); $item }");
        var globalResults = await engine.ExecuteToListAsync("echo $item");

        Assert.Collection(blockResults, item => Assert.Equal("hello", item));
        Assert.Collection(globalResults, item => Assert.Equal("GLOBAL", item));
    }

    [Fact]
    public async Task Untouched_dotnet_types_can_be_constructed_and_invoked_fluently()
    {
        var engine = ShellEngine.CreateFullShell();

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
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("using System.IO = IO\necho IO.Path.DirectorySeparatorChar");

        Assert.Collection(results, item => Assert.Equal(Path.DirectorySeparatorChar, Assert.IsType<char>(item)));
    }

    [Fact]
    public async Task Using_imports_enable_static_method_access_for_framework_types_from_runtime_assemblies()
    {
        var engine = ShellEngine.CreateFullShell();

        var importedResults = await engine.ExecuteToListAsync("using System.IO\necho DriveInfo.GetDrives() | type-of");
        var qualifiedResults = await engine.ExecuteToListAsync("echo System.IO.DriveInfo.GetDrives() | type-of");
        var rawPipelineResults = await engine.ExecuteToListAsync("using System.IO\nDriveInfo.GetDrives() | type-of");
        var flattenedResults = await engine.ExecuteToListAsync("using System.IO\nDriveInfo.GetDrives() | each { _ } | type-of");

        Assert.Collection(importedResults, item => Assert.Equal("array<System.IO.DriveInfo>", engine.LanguageRuntime.ObjectAccessor.GetValue(item, "Name")));
        Assert.Collection(qualifiedResults, item => Assert.Equal("array<System.IO.DriveInfo>", engine.LanguageRuntime.ObjectAccessor.GetValue(item, "Name")));
        Assert.Collection(rawPipelineResults, item => Assert.Equal("array<System.IO.DriveInfo>", engine.LanguageRuntime.ObjectAccessor.GetValue(item, "Name")));
        Assert.NotEmpty(flattenedResults);
        Assert.All(flattenedResults, item => Assert.Equal(typeof(DriveInfo), item));
    }

    [Fact]
    public async Task Variable_assignments_preserve_raw_clr_collection_results()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("using System.IO\nvar di = DriveInfo.GetDrives()\necho $di | type-of");

        Assert.Collection(results, item => Assert.Equal("array<System.IO.DriveInfo>", engine.LanguageRuntime.ObjectAccessor.GetValue(item, "Name")));
    }

    [Fact]
    public async Task Subexpressions_preserve_raw_clr_collection_results_for_member_access()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("using System.IO\necho (DriveInfo.GetDrives()).Length");

        Assert.Collection(
            results,
            item => Assert.True(Convert.ToInt32(item) >= 0));
    }

    [Fact]
    public async Task Require_can_export_public_module_names_without_leaking_private_ones()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(
            System.IO.Path.Combine(tempDirectory.Path, "defs.tosh"),
            "var prefix = \"hello\"\nfunc helper() { echo $prefix }\nexport func ll => ls -la\nexport func names() { helper }\nfunc private_names() { helper }");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("require defs.tosh\nrequire defs.tosh\nwhich ll names | get Kind\nnames\ntry { private_names } catch { echo missing }");

        Assert.Equal(CommandResolutionKind.Function, results[0]);
        Assert.Equal(CommandResolutionKind.Function, results[1]);
        Assert.Equal("hello", results[2]);
        Assert.Equal("missing", results[3]);
    }

    [Fact]
    public async Task Using_is_lexical_by_default_inside_functions()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            func make_point() {
                using System.Drawing = Drawing
                echo new Drawing.Point(2, 3).X
            }

            make_point

            try {
                echo new Drawing.Point(1, 1).X
            } catch {
                echo missing
            }
            """);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, results[0]);
        Assert.Equal("missing", results[1]);
    }

    [Fact]
    public async Task Require_is_lexical_by_default_inside_functions()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(
            System.IO.Path.Combine(tempDirectory.Path, "defs.tosh"),
            "export func lexical_greet_test() { echo hello }");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            """
            func run_local() {
                require ./defs.tosh
                lexical_greet_test
            }

            run_local

            try {
                lexical_greet_test
            } catch {
                echo missing
            }
            """);

        Assert.Equal("hello", results[0]);
        Assert.Equal("missing", results[1]);
    }

    [Fact]
    public async Task Require_can_load_dll_targets_for_following_using_statements()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var projectRoot = GetProjectRoot();
        var dllPath = ToshCli.AssemblyPath;

        var results = await engine.ExecuteToListAsync($"require {dllPath}\nusing Tosh.Cli\ndescribe-type ReplInputClassifier | get FullName");

        Assert.Collection(results, item => Assert.Equal("Tosh.Cli.ReplInputClassifier", item));
    }

    [Fact]
    public async Task Require_can_load_project_targets_for_following_using_statements()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var projectRoot = GetProjectRoot();
        var projectPath = System.IO.Path.Combine(projectRoot, "src", "Tosh.Cli", "Tosh.Cli.csproj");

        var results = await engine.ExecuteToListAsync($"require {projectPath}\nusing Tosh.Cli\ndescribe-type ReplInputClassifier | get FullName");

        Assert.Collection(results, item => Assert.Equal("Tosh.Cli.ReplInputClassifier", item));
    }

    [Fact]
    public async Task Global_declarations_can_escape_local_scopes()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("""
            var item = "GLOBAL"
            func scope_test() {
                var item = "LOCAL"
                global var escaped = "OUTSIDE"
                echo $item
            }
            scope_test
            echo $item
            echo $escaped
            """);

        Assert.Equal(["LOCAL", "GLOBAL", "OUTSIDE"], results.Select(item => Assert.IsType<string>(item)).ToArray());
    }

    [Fact]
    public async Task First_returns_the_first_object_or_first_n_objects()
    {
        var engine = ShellEngine.CreateFullShell();

        var single = await engine.ExecuteToListAsync("echo one two three | first");
        var many = await engine.ExecuteToListAsync("echo one two three | first 2");

        Assert.Collection(single, item => Assert.Equal("one", item));
        Assert.Equal(new[] { "one", "two" }, many.Cast<string>().ToArray());
    }

    [Fact]
    public async Task First_reads_a_spread_collection_as_pipeline_items()
    {
        var engine = ShellEngine.CreateFullShell();

        var singleArray = await engine.ExecuteToListAsync("echo ...[1, 2, 3] | first");
        var nestedArray = await engine.ExecuteToListAsync("echo ...[[1, 2], [3, 4]] | first");
        var multipleRows = await engine.ExecuteToListAsync("echo [1, 2] [3, 4] | first");

        Assert.Collection(singleArray, item => Assert.Equal(1, item));

        var firstNested = Assert.Single(nestedArray);
        Assert.IsAssignableFrom<Array>(firstNested);
        Assert.Equal(new object?[] { 1, 2 }, ((Array)firstNested!).Cast<object?>().ToArray());

        var firstPipelineRow = Assert.Single(multipleRows);
        Assert.IsAssignableFrom<Array>(firstPipelineRow);
        Assert.Equal(new object?[] { 1, 2 }, ((Array)firstPipelineRow!).Cast<object?>().ToArray());
    }

    [Fact]
    public async Task Get_index_reads_a_spread_collection_as_pipeline_items()
    {
        var engine = ShellEngine.CreateFullShell();

        var indexResult = await engine.ExecuteToListAsync("echo ...[1, 2, 3] | get 0");
        var firstPipelineRow = await engine.ExecuteToListAsync("echo [1, 2] [3, 4] | get 0");

        Assert.Collection(indexResult, item => Assert.Equal(1, item));

        var firstRow = Assert.Single(firstPipelineRow);
        Assert.IsAssignableFrom<Array>(firstRow);
        Assert.Equal(new object?[] { 1, 2 }, ((Array)firstRow!).Cast<object?>().ToArray());
    }

    [Fact]
    public async Task Row_oriented_commands_read_a_spread_collection_as_pipeline_items()
    {
        var engine = ShellEngine.CreateFullShell();

        var filtered = await engine.ExecuteToListAsync("echo ...[1, 2, 3] | where { _ > 1 }");
        var sorted = await engine.ExecuteToListAsync("echo ...[3, 1, 2] | sort");
        var counted = await engine.ExecuteToListAsync("echo ...[1, 2, 3] | count");

        Assert.Equal(new object?[] { 2, 3 }, filtered.ToArray());
        Assert.Equal(new object?[] { 1, 2, 3 }, sorted.ToArray());
        Assert.Collection(counted, item => Assert.Equal(3, item));
    }

    [Fact]
    public async Task Last_returns_the_last_object_or_last_n_objects()
    {
        var engine = ShellEngine.CreateFullShell();

        var single = await engine.ExecuteToListAsync("echo one two three | last");
        var many = await engine.ExecuteToListAsync("echo one two three | last 2");

        Assert.Collection(single, item => Assert.Equal("three", item));
        Assert.Equal(new[] { "two", "three" }, many.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Skip_skips_the_first_object_or_first_n_objects()
    {
        var engine = ShellEngine.CreateFullShell();

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
        var engine = ShellEngine.CreateFullShell();

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
        var engine = ShellEngine.CreateFullShell();

        var typeResults = await engine.ExecuteToListAsync("ps | first | type-of");
        var idResults = await engine.ExecuteToListAsync("ps | first | get Id | type-of");

        Assert.Collection(typeResults, item => Assert.Equal(typeof(ProcessInfo), item));
        Assert.Collection(idResults, item => Assert.Equal(typeof(int), item));
    }

    [Fact]
    public async Task Process_info_exposes_memory_as_a_queryable_member()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo new Tosh.Runtime.ProcessInfo(1, \"proc\", false, null, null, 2048, null, null) | get Memory");

        Assert.Collection(results, item => Assert.Equal(StorageSize.FromBytes(2048), Assert.IsType<StorageSize>(item)));
    }

    [Fact]
    public async Task Env_returns_environment_variable_objects()
    {
        var engine = ShellEngine.CreateFullShell();

        var nameResults = await engine.ExecuteToListAsync("env PATH | get Name");
        var setResults = await engine.ExecuteToListAsync("env PATH | get IsSet");

        Assert.Collection(nameResults, item => Assert.Equal("PATH", item));
        Assert.Collection(setResults, item => Assert.Equal(true, item));
    }

    [Fact]
    public async Task Env_runtime_namespace_returns_environment_variable_values()
    {
        const string variableName = "TOSH_ENV_NAMESPACE_TEST";
        const string variableValue = "toast-value";

        Environment.SetEnvironmentVariable(variableName, variableValue);

        try
        {
            var engine = ShellEngine.CreateFullShell();

            var results = await engine.ExecuteToListAsync($"echo $env.{variableName}");

            Assert.Collection(results, item => Assert.Equal(variableValue, item));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task Missing_env_runtime_namespace_members_resolve_to_null()
    {
        const string variableName = "TOSH_ENV_NAMESPACE_MISSING_TEST";
        Environment.SetEnvironmentVariable(variableName, null);

        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync($"echo $env.{variableName}");

        Assert.Collection(results, item => Assert.Null(item));
    }

    [Fact]
    public async Task Which_returns_builtin_command_resolutions()
    {
        var engine = ShellEngine.CreateFullShell();

        var kindResults = await engine.ExecuteToListAsync("which help | get Kind");
        var usageResults = await engine.ExecuteToListAsync("whence help | get Usage");

        Assert.Contains(CommandResolutionKind.BuiltIn, kindResults.Cast<CommandResolutionKind>());
        Assert.Contains("help [--cli] [topic ... | browse [query] | search <query> | related <topic> | categories]", usageResults.Cast<string>());
    }

    [Fact]
    public async Task Which_can_resolve_wrapper_and_block_functions()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("func ll => ls -la");
        await engine.ExecuteToListAsync("func recent(days: TimeSpan) { ls -la | where _.Modified > ((date now) - $days) }");
        var kindResults = await engine.ExecuteToListAsync("which ll recent | get Kind");

        Assert.Equal(2, kindResults.Cast<CommandResolutionKind>().Count(kind => kind == CommandResolutionKind.Function));
        Assert.Contains(CommandResolutionKind.Function, kindResults.Cast<CommandResolutionKind>());
    }

    [Fact]
    public async Task Source_executes_script_files_in_the_current_session()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(
            System.IO.Path.Combine(tempDirectory.Path, "defs.tosh"),
            "func ll => ls -la\nfunc stringifyCount() -> String { count }");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var sourceResults = await engine.ExecuteToListAsync("source defs.tosh");
        var functionKinds = await engine.ExecuteToListAsync("which ll | get Kind");
        var functionResults = await engine.ExecuteToListAsync("echo 1 2 3 | stringifyCount");

        Assert.Empty(sourceResults);
        Assert.Contains(CommandResolutionKind.Function, functionKinds.Cast<CommandResolutionKind>());
        Assert.Collection(functionResults, item => Assert.Equal("3", item));
    }

    [Fact]
    public async Task Source_passes_script_arguments_through_tosh_script_args()
    {
        using var tempDirectory = new TemporaryDirectory();
        var scriptPath = System.IO.Path.Combine(tempDirectory.Path, "argv.tosh");
        await File.WriteAllTextAsync(
            scriptPath,
            """
            $tosh.Script.Args | count
            $tosh.Script.Args | first
            $tosh.Script.Args | last
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("source argv.tosh --trimmed ./out");

        Assert.Equal([2, "--trimmed", "./out"], results);
    }

    [Fact]
    public async Task Auto_sourced_tosh_scripts_pass_arguments_through_tosh_script_args()
    {
        using var tempDirectory = new TemporaryDirectory();
        var scriptPath = System.IO.Path.Combine(tempDirectory.Path, "argv.tosh");
        await File.WriteAllTextAsync(
            scriptPath,
            """
            $tosh.Script.Args | count
            $tosh.Script.Args | first
            $tosh.Script.Args | last
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("./argv.tosh --trimmed ./out");

        Assert.Equal([2, "--trimmed", "./out"], results);
    }

    [Fact]
    public async Task Auto_sourced_tosh_shebang_scripts_without_extension_pass_arguments_through_tosh_script_args()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDirectory = new TemporaryDirectory();
        var scriptPath = System.IO.Path.Combine(tempDirectory.Path, "argv");
        await File.WriteAllTextAsync(
            scriptPath,
            """
            #!/usr/bin/env tosh
            $tosh.Script.Args | count
            $tosh.Script.Args | first
            $tosh.Script.Args | last
            """);
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("./argv --trimmed ./out");

        Assert.Equal([2, "--trimmed", "./out"], results);
    }

    [Fact]
    public async Task Auto_sourced_tosh_shebang_scripts_without_extension_can_be_used_in_subexpressions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDirectory = new TemporaryDirectory();
        var scriptPath = System.IO.Path.Combine(tempDirectory.Path, "value");
        await File.WriteAllTextAsync(
            scriptPath,
            """
            #!/usr/bin/env tosh
            return 42
            """);
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("var value = (./value)\n$value");

        Assert.Equal([42], results);
    }

    [Fact]
    public async Task Command_arguments_support_splatting()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var values = ["alpha", "beta", "gamma"]
            echo ...$values
            """);

        Assert.Equal(["alpha", "beta", "gamma"], results.Select(item => item?.ToString() ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task Auto_sourced_tosh_scripts_do_not_leak_default_scope_declarations()
    {
        using var tempDirectory = new TemporaryDirectory();
        var scriptPath = System.IO.Path.Combine(tempDirectory.Path, "vars.tosh");
        await File.WriteAllTextAsync(
            scriptPath,
            """
            var inner = "hidden"
            echo $inner
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("./vars.tosh");
        Assert.Equal(["hidden"], results);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("echo $inner"));
        Assert.Contains("Variable 'inner' was not found.", exception.Message);
    }

    [Fact]
    public async Task Source_restores_outer_script_args_after_script_execution()
    {
        using var tempDirectory = new TemporaryDirectory();
        var scriptPath = System.IO.Path.Combine(tempDirectory.Path, "argv.tosh");
        await File.WriteAllTextAsync(
            scriptPath,
            """
            $tosh.Script.Args | first
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        runtime.InvocationArguments = ["outer"];
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("source argv.tosh inner\n$tosh.Script.Args | first");

        Assert.Equal(["inner", "outer"], results);
    }

    [Fact]
    public async Task Flat_args_variable_is_no_longer_available()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("echo $args"));
        var diagnostic = Assert.Single(exception.Diagnostics);

        Assert.Equal("tosh.runtime.unknown_variable", diagnostic.Code);
    }

    [Fact]
    public async Task Top_level_invocation_args_are_available_under_tosh_script_args()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.InvocationArguments = ["alpha", "beta"];
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("$tosh.Script.Args | first\n$tosh.Script.Args | last");

        Assert.Equal(["alpha", "beta"], results);
    }

    [Fact]
    public async Task Newline_separated_top_level_statements_can_chain_into_get_after_an_earlier_statement()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("func ll => ls -la\nwhich ll | get Kind");

        Assert.Contains(CommandResolutionKind.Function, results.Cast<CommandResolutionKind>());
    }

    [Fact]
    public async Task Newline_separated_block_statements_execute_inside_each_blocks()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            echo Hello | each {
                var lower = _.ToLower()
                $lower
            }
            """);

        Assert.Collection(results, item => Assert.Equal("hello", item));
    }

    [Fact]
    public async Task Return_exits_top_level_scripts_early()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo before\nreturn \"done\"\necho after");

        Assert.Equal(new[] { "before", "done" }, results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Sort_by_alias_can_sort_objects_by_visible_process_members()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            "echo new Tosh.Runtime.ProcessInfo(1, \"large\", false, null, null, 4096, null, null) new Tosh.Runtime.ProcessInfo(2, \"small\", false, null, null, 1024, null, null) | sort-by Memory | get Name");

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

        var results = await engine.ExecuteToListAsync("ls | where _.Extension == .txt | get Name");

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
        var lastWriteTimeResults = await engine.ExecuteToListAsync("ls | get LastWriteTime");
        var modeResults = await engine.ExecuteToListAsync("ls | get Mode");
        var readonlyResults = await engine.ExecuteToListAsync("ls -la | get Readonly");
        var createdResults = await engine.ExecuteToListAsync("ls -la | get Created");
        var accessedResults = await engine.ExecuteToListAsync("ls -la | get Accessed");
        var creationTimeResults = await engine.ExecuteToListAsync("ls -la | get CreationTime");
        var lastAccessTimeResults = await engine.ExecuteToListAsync("ls -la | get LastAccessTime");
        var inodeResults = await engine.ExecuteToListAsync("ls -la | get Inode");
        var ownerResults = await engine.ExecuteToListAsync("ls -la | get Owner");
        var groupResults = await engine.ExecuteToListAsync("ls -la | get Group");

        Assert.Collection(sizeResults, item => Assert.Equal(StorageSize.FromBytes(4), Assert.IsType<StorageSize>(item)));
        Assert.Collection(typeResults, item => Assert.Equal(FileSystemEntryType.File, item));
        Assert.Collection(modifiedResults, item => Assert.IsType<DateTimeOffset>(item));
        Assert.Collection(lastWriteTimeResults, item => Assert.IsType<DateTime>(item));
        Assert.Collection(readonlyResults, item => Assert.IsType<bool>(item));
        Assert.Collection(createdResults, item => Assert.IsType<DateTimeOffset>(item));
        Assert.Collection(accessedResults, item => Assert.IsType<DateTimeOffset>(item));
        Assert.Collection(creationTimeResults, item => Assert.IsType<DateTime>(item));
        Assert.Collection(lastAccessTimeResults, item => Assert.IsType<DateTime>(item));

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

        var targetResults = await engine.ExecuteToListAsync("ls -la | where _.Name == keep-link.txt | get Target");

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

        var results = await engine.ExecuteToListAsync("ls | where _.Type == file | get Name");

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

        var results = await engine.ExecuteToListAsync("ls -la | where _.Size? > 1000 | get Name");

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

        var numericResults = await engine.ExecuteToListAsync("ls -la | where _.Size? > 1000 | get Name");
        var unitResults = await engine.ExecuteToListAsync("ls -la | where _.Size? > 1kb | get Name");

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

        var results = await engine.ExecuteToListAsync("ls -la | where _.Size >= 1kb | get Name");

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

        var results = await engine.ExecuteToListAsync("ls -la | where _.Type == file | where _.Modified < (date now | date sub (timespan 2d)) | get Name");

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

        var results = await engine.ExecuteToListAsync("ls -la | where _.Name.ToString().ToLower().Contains(\".x\") | get Name");

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
        var results = await engine.ExecuteToListAsync("ls -la | where _.Name.ToLower().EndsWith($suffix) | get Name");

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

        var results = await engine.ExecuteToListAsync("ls -la | where { _.Type == file; _.Size >= 1kb; _.Modified < ((date now) - (timespan 2d)); } | get Name");

        Assert.Collection(results, item => Assert.Equal("old.txt", item));
    }

    [Fact]
    public async Task Where_supports_unified_boolean_predicates_with_shorthand_operators()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "alpha.txt"), new string('a', 2048));
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "beta.txt"), new string('b', 256));
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "gamma.log"), new string('g', 4096));
        Directory.CreateDirectory(System.IO.Path.Combine(tempDirectory.Path, "nested"));

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            "ls -la | where { (_.Size >= 1kb) and ((_.Name == [alpha.txt, beta.txt]) or (_.Modified > ((date now) - 2d))) and not (_.Type == dir) } | get Name");

        Assert.Equal(
            new[] { "alpha.txt", "gamma.log" },
            results.Cast<string>().OrderBy(name => name).ToArray());
    }

    [Fact]
    public async Task New_expression_works_for_imported_and_fully_qualified_clr_types()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var importedResults = await engine.ExecuteToListAsync("using System.Drawing\nvar pt = new Point(2, 2)\necho $pt.X $pt.Y");
        var qualifiedResults = await engine.ExecuteToListAsync("var size = new System.Drawing.Size(3, 4)\necho $size.Width $size.Height");

        Assert.Equal(new object?[] { 2, 2 }, importedResults.ToArray());
        Assert.Equal(new object?[] { 3, 4 }, qualifiedResults.ToArray());
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
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("new System.Text.StringBuilder hello | inspect");

        var inspection = Assert.IsType<ObjectInspection>(Assert.Single(results));
        Assert.Equal("System.Text.StringBuilder", inspection.TypeName);
        Assert.Contains(inspection.Members, member => member.Name == "Length");
        // The preview is the value's rendering — a StringBuilder's own `ToString` is its
        // contents. Its *type* is asserted above, from `TypeName`, which is where it
        // belongs.
        Assert.Contains("hello", inspection.Display, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Class_primary_constructor_initializes_properties_and_renders_public_members()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            class Item(name: string, quantity: int, category: string) {
                prop Name: string = name
                prop Quantity: int = quantity
                prop Category: string = category
            }

            var item = new Item("Bread", 2, "Food")
            $item
            """);

        var instance = Assert.IsType<ToshClassInstance>(Assert.Single(results));
        Assert.Equal("Bread", engine.LanguageRuntime.ObjectAccessor.GetValue(instance, "Name"));
        Assert.Equal(2, engine.LanguageRuntime.ObjectAccessor.GetValue(instance, "Quantity"));
        Assert.Equal("Food", engine.LanguageRuntime.ObjectAccessor.GetValue(instance, "Category"));

        var rendered = engine.Runtime.Display.RenderMany(results);
        Assert.Contains("Name", rendered, StringComparison.Ordinal);
        Assert.Contains("Bread", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellTypeName", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_expression_can_construct_tosh_classes()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            class Item {
                prop Name: string? = null

                Item(name: string) {
                    $this.Name = $name
                }
            }

            var item = new Item("Bread")
            $item.Name
            """);

        Assert.Equal("Bread", Assert.Single(results));
    }

    [Fact]
    public async Task Typed_constructor_parameters_do_not_silently_round_fractional_values()
    {
        var engine = ShellEngine.CreateFullShell();

        await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync(
                """
                class Point(x: int, y: int) {
                    prop X: double = x
                    prop Y: double = y
                }

                new Point(12.3, 45.6)
                """));
    }

    [Fact]
    public async Task Bare_type_invocation_requires_new_for_clr_and_tosh_types()
    {
        var engine = ShellEngine.CreateFullShell();

        var clrException = await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync(
                """
                using System.Drawing
                var pt = Point(2, 2)
                """));

        var toshException = await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync(
                """
                class Item {
                    Item() { }
                }

                var item = Item()
                """));

        Assert.Contains("expression_failed", clrException.Diagnostics[0].Code, StringComparison.Ordinal);
        Assert.Contains("new Point(...)", clrException.Diagnostics[0].Title, StringComparison.Ordinal);

        Assert.Contains("expression_failed", toshException.Diagnostics[0].Code, StringComparison.Ordinal);
        Assert.Contains("new Item(...)", toshException.Diagnostics[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Class_static_methods_support_custom_return_types_and_instance_methods()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            class Item(name: string, quantity: int, category: string) {
                prop Name: string = name
                prop Quantity: int = quantity
                prop Category: string = category

                static func named(name: string) -> Item {
                    return new Item($name, 1, "misc")
                }

                func describe() -> string {
                    return $"[{$this.Category}] {$this.Name} x{$this.Quantity}"
                }
            }

            var item = Item.named("Bread")
            $item.describe()
            """);

        Assert.Equal("[misc] Bread x1", Assert.Single(results));
    }

    [Fact]
    public async Task Type_of_returns_tosh_class_descriptors_for_user_classes()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            class Item(name: string, quantity: int, category: string) {
                prop Name: string = name
                prop Quantity: int = quantity
                prop Category: string = category
            }

            var item = new Item("Bread", 2, "Food")
            $item | type-of
            """);

        var descriptor = Assert.IsAssignableFrom<IShellTypeDescriptor>(Assert.Single(results));
        Assert.Equal("Item", descriptor.ShellTypeName);
        Assert.Equal("ToSh", descriptor.ShellAssemblyName);
        Assert.True(descriptor.ShellIsClass);
    }

    [Fact]
    public async Task Describe_type_members_methods_and_constructors_understand_tosh_classes()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync(
            """
            class Item(name: string, quantity: int, category: string) {
                prop Name: string = name
                prop Quantity: int = quantity
                prop Category: string = category
                prop IsLowStock: bool => $this.is_low_stock()
                shy prop InternalName: string? = null

                Item() { }

                static func named(name: string) -> Item {
                    return new Item($name, 1, "misc")
                }

                func describe() -> string {
                    return $"[{$this.Category}] {$this.Name} x{$this.Quantity}"
                }

                shy func is_low_stock() -> bool {
                    return ($this.Quantity < 5)
                }
            }
            """);

        var describeResults = await engine.ExecuteToListAsync("describe-type Item");
        var memberResults = await engine.ExecuteToListAsync("members Item");
        var methodResults = await engine.ExecuteToListAsync("methods Item");
        var constructorResults = await engine.ExecuteToListAsync("constructors Item");

        Assert.Equal("Item", engine.LanguageRuntime.ObjectAccessor.GetValue(Assert.Single(describeResults), "Name"));
        Assert.Equal("ToSh", engine.LanguageRuntime.ObjectAccessor.GetValue(describeResults[0], "Assembly"));

        Assert.Contains(memberResults, item => Equals(engine.LanguageRuntime.ObjectAccessor.GetValue(item, "Name"), "Name"));
        Assert.Contains(memberResults, item => Equals(engine.LanguageRuntime.ObjectAccessor.GetValue(item, "Name"), "IsLowStock"));
        Assert.DoesNotContain(memberResults, item => Equals(engine.LanguageRuntime.ObjectAccessor.GetValue(item, "Name"), "InternalName"));

        Assert.Contains(methodResults, item => Equals(engine.LanguageRuntime.ObjectAccessor.GetValue(item, "Name"), "named"));
        Assert.Contains(methodResults, item => Equals(engine.LanguageRuntime.ObjectAccessor.GetValue(item, "Name"), "describe"));
        Assert.DoesNotContain(methodResults, item => Equals(engine.LanguageRuntime.ObjectAccessor.GetValue(item, "Name"), "is_low_stock"));

        Assert.Equal(2, constructorResults.Count);
        Assert.Contains(constructorResults, item => Equals(engine.LanguageRuntime.ObjectAccessor.GetValue(item, "Signature"), "Item()"));
        Assert.Contains(constructorResults, item => Equals(engine.LanguageRuntime.ObjectAccessor.GetValue(item, "Signature"), "Item(name: string, quantity: int, category: string)"));
    }

    [Fact]
    public async Task Helper_introspection_commands_understand_tosh_class_instances_and_descriptors()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync(
            """
            class Item(name: string, quantity: int, category: string) {
                prop Name: string = name
                prop Quantity: int = quantity
                prop Category: string = category
                shy prop InternalName: string? = null

                Item() { }

                static func named(name: string) -> Item {
                    return new Item($name, 1, "misc")
                }

                func describe() -> string {
                    return $"[{$this.Category}] {$this.Name} x{$this.Quantity}"
                }

                shy func is_low_stock() -> bool {
                    return ($this.Quantity < 5)
                }
            }

            var item = Item.named("Bread")
            """);

        var propResults = await engine.ExecuteToListAsync("get-props $item");
        var instanceMethodResults = await engine.ExecuteToListAsync("get-methods $item");
        var staticMethodResults = await engine.ExecuteToListAsync("$item | type-of | get-methods");
        var hasInstanceMethodResults = await engine.ExecuteToListAsync("$item | has-method describe");
        var hasHiddenInstanceMethodResults = await engine.ExecuteToListAsync("$item | has-method is_low_stock");
        var hasStaticMethodResults = await engine.ExecuteToListAsync("$item | type-of | has-method named");
        var constructorResults = await engine.ExecuteToListAsync("$item | constructors");

        Assert.Contains("Name", propResults.Select(item => item?.ToString()));
        Assert.Contains("Quantity", propResults.Select(item => item?.ToString()));
        Assert.DoesNotContain("InternalName", propResults.Select(item => item?.ToString()));

        Assert.Contains("describe", instanceMethodResults.Select(item => item?.ToString()));
        Assert.DoesNotContain("named", instanceMethodResults.Select(item => item?.ToString()));
        Assert.DoesNotContain("is_low_stock", instanceMethodResults.Select(item => item?.ToString()));

        Assert.Contains("named", staticMethodResults.Select(item => item?.ToString()));
        Assert.DoesNotContain("describe", staticMethodResults.Select(item => item?.ToString()));

        Assert.Equal(true, Assert.Single(hasInstanceMethodResults));
        Assert.Equal(false, Assert.Single(hasHiddenInstanceMethodResults));
        Assert.Equal(true, Assert.Single(hasStaticMethodResults));

        Assert.Contains(constructorResults, item => Equals(engine.LanguageRuntime.ObjectAccessor.GetValue(item, "Signature"), "Item()"));
        Assert.Contains(constructorResults, item => Equals(engine.LanguageRuntime.ObjectAccessor.GetValue(item, "Signature"), "Item(name: string, quantity: int, category: string)"));
    }

    [Fact]
    public async Task Functions_and_command_wrappers_can_consume_pipeline_input()
    {
        var engine = ShellEngine.CreateFullShell();

        var functionResults = await engine.ExecuteToListAsync(
            """
            func upper_input() {
                $tosh.Function.Input | each { _.ToUpper() }
            }

            echo hello world | upper_input
            """);

        var wrapperResults = await engine.ExecuteToListAsync(
            """
            func top_two => first 2
            echo one two three | top_two
            """);

        var wrapperArgumentResults = await engine.ExecuteToListAsync(
            """
            func add_suffix(suffix) => each { String.Join("", [_, $suffix]) }
            echo alpha beta | add_suffix "!"
            """);

        Assert.Equal(["HELLO", "WORLD"], functionResults.Select(item => item?.ToString() ?? string.Empty).ToArray());
        Assert.Equal(["one", "two"], wrapperResults.Select(item => item?.ToString() ?? string.Empty).ToArray());
        Assert.Equal(["alpha!", "beta!"], wrapperArgumentResults.Select(item => item?.ToString() ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task Flat_input_variable_is_no_longer_available()
    {
        var engine = ShellEngine.CreateFullShell();

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync(
            """
            func upper_input() {
                $input
            }
            echo hello | upper_input
            """));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.runtime.unknown_variable", diagnostic.Code);
    }

    [Fact]
    public async Task Reserved_runtime_namespace_name_cannot_be_used_for_functions_or_modules()
    {
        var engine = ShellEngine.CreateFullShell();

        var functionException = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("func tosh() { }"));
        var moduleException = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("module tosh { }"));

        Assert.Equal("tosh.runtime.reserved_variable_name", Assert.Single(functionException.Diagnostics).Code);
        Assert.Equal("tosh.runtime.reserved_variable_name", Assert.Single(moduleException.Diagnostics).Code);
    }

    [Fact]
    public async Task Reserved_runtime_namespace_name_cannot_be_used_for_parameters_or_loop_variables()
    {
        var engine = ShellEngine.CreateFullShell();

        var parameterException = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("func demo(tosh) { echo $tosh }"));
        var loopException = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("for tosh in (echo 1) { echo $tosh }"));

        Assert.Equal("tosh.runtime.reserved_variable_name", Assert.Single(parameterException.Diagnostics).Code);
        Assert.Equal("tosh.runtime.reserved_variable_name", Assert.Single(loopException.Diagnostics).Code);
    }

    [Fact]
    public async Task Reserved_runtime_namespace_name_cannot_be_used_for_lambda_parameters()
    {
        var engine = ShellEngine.CreateFullShell();

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("var bad = func(env) => $env"));

        Assert.Equal("tosh.runtime.reserved_variable_name", Assert.Single(exception.Diagnostics).Code);
    }

    [Fact]
    public async Task Reserved_env_runtime_namespace_name_cannot_be_used_for_bindings()
    {
        var engine = ShellEngine.CreateFullShell();

        var variableException = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("var env = 1"));
        var functionException = await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("func env() { }"));

        Assert.Equal("tosh.runtime.reserved_variable_name", Assert.Single(variableException.Diagnostics).Code);
        Assert.Equal("tosh.runtime.reserved_variable_name", Assert.Single(functionException.Diagnostics).Code);
    }

    [Fact]
    public async Task Class_computed_properties_and_shy_members_work_through_this()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync(
            """
            class Item {
                prop Name: string? = null
                prop Quantity: int? = null
                prop IsLowStock: bool => $this.is_low_stock()
                prop ClassName: string? {
                    get => $this.InternalName
                    set => $this.InternalName = $value
                }
                shy prop InternalName: string? = null

                Item() { }

                shy func is_low_stock() -> bool {
                    if ($this.Quantity != null) {
                        return ($this.Quantity < 5)
                    }

                    return false
                }
            }

            var item = new Item()
            $item.Name = "Bread"
            $item.Quantity = 2
            $item.ClassName = "inventory_item"
            """);

        var valueResults = await engine.ExecuteToListAsync(
            """
            $item.IsLowStock
            $item.ClassName
            """);

        Assert.Collection(
            valueResults,
            value => Assert.Equal(true, value),
            value => Assert.Equal("inventory_item", value));

        var inspectResults = await engine.ExecuteToListAsync("$item | inspect");
        var inspection = Assert.IsType<ObjectInspection>(Assert.Single(inspectResults));
        Assert.Equal("Item", inspection.TypeName);
        Assert.Contains(inspection.Members, member => member.Name == "ClassName");
        Assert.DoesNotContain(inspection.Members, member => member.Name == "InternalName");
    }

    [Fact]
    public async Task Require_can_export_classes_for_later_use()
    {
        using var tempDirectory = new TemporaryDirectory();
        var modulePath = System.IO.Path.Combine(tempDirectory.Path, "inventory.tosh");

        await File.WriteAllTextAsync(
            modulePath,
            """
            export class Item(name: string, quantity: int, category: string) {
                prop Name: string = name
                prop Quantity: int = quantity
                prop Category: string = category
            }
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            """
            require ./inventory.tosh
            var item = new Item("Bread", 2, "Food")
            $item.Name
            """);

        Assert.Equal("Bread", Assert.Single(results));
    }

    [Fact]
    public async Task Class_special_methods_can_override_string_equality_and_hash_behavior()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            class Item(name: string) {
                prop Name: string = name

                shy func ToString() -> string {
                    return $"Item({$this.Name})"
                }

                shy func Equals(other) -> bool {
                    return ($other.Name == $this.Name)
                }

                shy func GetHashCode() -> int {
                    return $this.Name.Length
                }
            }

            var left = new Item("Bread")
            var right = new Item("Bread")
            echo $"Created: {$left}"
            echo ($left == $right)
            echo (new set($left, $right)).Count
            """);

        Assert.Collection(
            results,
            value => Assert.Equal("Created: Item(Bread)", value),
            value => Assert.Equal(true, value),
            value => Assert.Equal(1, value));
    }

    [Fact]
    public async Task Collections_example_runs_successfully()
    {
        var engine = ShellEngine.CreateFullShell();
        var examplePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../examples/collections.tosh"));
        var source = await File.ReadAllTextAsync(examplePath);

        var results = await engine.ExecuteToListAsync(source, examplePath);

        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task Require_can_import_exported_modules_with_aliases()
    {
        using var tempDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(tempDirectory.Path, "inventory.tosh"),
            """
            module Inventory {
                enum StockState {
                    Unknown
                    Low
                    Ok
                }

                class Item(name: string) {
                    prop Name: string = name

                    static func named(name: string) -> Item {
                        return new Item($name)
                    }
                }
            }
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            """
            require Inventory from ./inventory.tosh as Inv
            var bread = new Inv.Item("Bread")
            $bread.Name
            echo Inv.StockState.Low
            echo Inv.Item.named("Milk").Name
            """);

        Assert.Equal("Bread", results[0]);
        Assert.Equal("Low", results[1]?.ToString());
        Assert.Equal("Milk", results[2]);
    }

    [Fact]
    public async Task Require_resolves_relative_to_the_current_script_file()
    {
        using var tempDirectory = new TemporaryDirectory();
        var scriptsPath = System.IO.Path.Combine(tempDirectory.Path, "examples");
        Directory.CreateDirectory(scriptsPath);

        var libraryPath = System.IO.Path.Combine(scriptsPath, "toastlib.tosh");
        var scriptPath = System.IO.Path.Combine(scriptsPath, "main.tosh");

        await File.WriteAllTextAsync(
            libraryPath,
            """
            module Inventory {
                class Item(name: string) {
                    prop Name: string = name
                }
            }
            """);

        await File.WriteAllTextAsync(
            scriptPath,
            """
            require Inventory from "./toastlib.tosh" as Inv
            echo new Inv.Item("Bread").Name
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var source = await File.ReadAllTextAsync(scriptPath);
        var results = await engine.ExecuteToListAsync(source, scriptPath);

        Assert.Equal("Bread", Assert.Single(results));
    }

    [Fact]
    public async Task Native_libraries_can_be_required_bound_and_invoked()
    {
        var libraryName = GetNativeTestLibraryName();

        if (libraryName is null)
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            $@"require native ""{libraryName}"" as LibC
bind LibC {{
    func abs(int) -> int
    func myAbs(value: int) -> int as ""abs""
}}
LibC.abs(-5)
LibC.myAbs(-9)");

        Assert.Equal([5, 9], results);
    }

    [Fact]
    public async Task Inline_native_bind_blocks_can_load_bind_and_invoke()
    {
        var libraryName = GetNativeTestLibraryName();

        if (libraryName is null)
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            $@"bind native ""{libraryName}"" as LibC {{
    func abs(int) -> int
}}
LibC.abs(-11)");

        Assert.Equal([11], results);
    }

    [Fact]
    public async Task Native_libraries_support_string_and_pointer_sized_types()
    {
        var libraryName = GetNativeTestLibraryName();

        if (libraryName is null)
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var strlenSymbol = OperatingSystem.IsWindows() ? "strlen" : "strlen";

        var results = await engine.ExecuteToListAsync(
            $@"require native ""{libraryName}"" as LibC
bind LibC {{
    func strlen(string) -> nuint as ""{strlenSymbol}""
    func malloc(nuint) -> nint
    func free(nint)
}}
var length = LibC.strlen(""toast"")
var pointer = LibC.malloc(16)
type-of $length | get Name
type-of $pointer | get Name
LibC.free($pointer)
$length");

        Assert.Equal("UIntPtr", Assert.IsType<string>(results[0]));
        Assert.Equal("IntPtr", Assert.IsType<string>(results[1]));
        var length = Assert.IsType<UIntPtr>(results[2]);
        Assert.Equal((ulong)5, length.ToUInt64());
    }

    [Fact]
    public async Task Native_bindings_reject_plain_string_returns()
    {
        var libraryName = GetNativeTestLibraryName();

        if (libraryName is null)
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                $@"require native ""{libraryName}"" as LibC
bind LibC {{
    func getenv(string) -> string
}}"));

        Assert.Contains("Native string returns need an explicit interop string type.", exception.Message);
    }

    [Fact]
    public async Task Native_bindings_reject_byref_string_parameters()
    {
        var libraryName = GetNativeTestLibraryName();

        if (libraryName is null)
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                $@"require native ""{libraryName}"" as LibC
bind LibC {{
    func getenv(out value: string) -> int
}}"));

        Assert.Contains("By-ref native string parameters need an explicit pointer type.", exception.Message);
    }

    [Fact]
    public async Task Native_libraries_support_cstring_returns()
    {
        var libraryName = GetNativeTestLibraryName();

        if (libraryName is null)
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            $@"require native ""{libraryName}"" as LibC
bind LibC {{
    func getenv(string) -> cstring
}}
LibC.getenv(""PATH"")");

        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(Assert.Single(results))));
    }

    [Fact]
    public async Task Native_bindings_accept_explicit_calling_conventions()
    {
        var libraryName = GetNativeTestLibraryName();

        if (libraryName is null)
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            $@"require native ""{libraryName}"" as LibC
bind LibC {{
    func abs(int) -> int callconv cdecl
}}
LibC.abs(-7)");

        Assert.Equal([7], results);
    }

    [Fact]
    public async Task Native_buffer_helpers_support_out_buffer_copy_patterns()
    {
        var libraryName = GetNativeTestLibraryName();

        if (libraryName is null)
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            $@"require native ""{libraryName}"" as LibC
bind LibC {{
    func memcpy(nint, nint, nuint) -> nint
}}
var source = (native-alloc 6)
var dest = (native-alloc 6)
native-write $source ""toast""
var ignored = (LibC.memcpy($dest, $source, 6))
native-read cstring $dest
native-free $source
native-free $dest");

        Assert.Equal("toast", Assert.Single(results));
    }

    [Fact]
    public async Task Short_native_buffer_surface_supports_alloc_write_read_and_forget()
    {
        var libraryName = GetNativeTestLibraryName();

        if (libraryName is null)
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            $@"require native ""{libraryName}"" as LibC
bind LibC {{
    func memcpy(nint, nint, nuint) -> nint
}}
alloc source = 6
alloc dest = 6
write-buffer $source ""toast""
var ignored = (LibC.memcpy($dest, $source, 6))
var output = (read-buffer cstring $dest)
forget $source $dest | ignore
$output");

        Assert.Equal("toast", Assert.Single(results));
    }

    [Fact]
    public async Task Native_buffer_helpers_support_struct_layout_round_trips()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            """
            var buffer = (native-alloc Tosh.Tests.NativePoint)
            var point = new Tosh.Tests.NativePoint(7, 11)
            native-sizeof Tosh.Tests.NativePoint
            native-offsetof Tosh.Tests.NativePoint Y
            native-write $buffer $point
            var roundTrip = (native-read Tosh.Tests.NativePoint $buffer)
            $roundTrip.X
            $roundTrip.Y
            native-free $buffer
            """);

        Assert.Equal([8, 4L, 7, 11], results);
    }

    [Fact]
    public async Task Short_native_buffer_surface_supports_size_of_and_offset_of()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            """
            size-of Tosh.Tests.NativePoint
            offset-of Tosh.Tests.NativePoint.Y
            """);

        Assert.Equal([8, 4L], results);
    }

    [Fact]
    public async Task Alloc_statement_accepts_simple_interop_type_names()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            """
            alloc buffer = long
            read-buffer long $buffer
            forget $buffer | ignore
            """);

        Assert.Equal([0L], results);
    }

    [Fact]
    public async Task Forget_value_form_removes_native_buffer_variables_and_frees_them()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            """
            alloc buffer = 16
            var removal = (forget $buffer | first)
            $removal.RemovedVariable
            $removal.FreedValue
            """);

        Assert.Equal([true, true], results);
    }

    /// <summary>
    /// `out` parameters are engine-allocated and do not appear at the call site,
    /// so `gettimeofday` takes only its (unused) timezone pointer.
    /// </summary>
    [Fact]
    public async Task Native_bindings_support_out_struct_parameters()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            """
            bind native "libc.so.6" as LibC {
                func gettimeofday(out tv: Tosh.Tests.NativeTimeVal, nint) -> int
            }
            var direct = LibC.gettimeofday(0)
            $direct.ReturnValue
            $direct.tv.tv_sec > 0
            """);

        Assert.Equal([0, true], results);
    }

    /// <summary>
    /// Buffer writeback moved to `ref`, which is where it belongs: supplying the
    /// memory means the parameter is not write-only. `out` drops from the call
    /// site precisely because there is nothing for a caller to pass.
    /// </summary>
    [Fact]
    public async Task Native_bindings_write_back_into_ref_buffers()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            """
            bind native "libc.so.6" as LibC {
                func gettimeofday(ref tv: Tosh.Tests.NativeTimeVal, nint) -> int
            }
            alloc buffer = Tosh.Tests.NativeTimeVal
            var buffered = LibC.gettimeofday($buffer, 0)
            var roundTrip = (read-buffer Tosh.Tests.NativeTimeVal $buffer)
            $buffered.ReturnValue
            $roundTrip.tv_sec > 0
            forget $buffer | ignore
            """);

        Assert.Equal([0, true], results);
    }

    [Fact]
    public async Task Flatten_preserves_dynamic_record_values()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var items = [{| Name = "Bread" |}, {| Name = "Coffee" |}]
            $items | flatten | get Name
            """);

        Assert.Equal(["Bread", "Coffee"], results.Select(result => Assert.IsType<string>(result)).ToArray());
    }

    [Fact]
    public async Task Null_expression_statements_do_not_emit_placeholder_results()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            module Utilities {
                func banner(title: string) {
                    writeline $title
                }
            }

            Utilities.banner("demo")
            Utilities.banner("again")
            """);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Enums_support_string_parameter_binding_and_static_members()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            enum StockState {
                Unknown
                Low
                Ok
            }

            func show(state: StockState) {
                echo $state
            }

            show "Low"
            echo StockState.Ok
            """);

        Assert.Equal("Low", results[0]?.ToString());
        Assert.Equal("Ok", results[1]?.ToString());
    }

    [Fact]
    public async Task Enums_expose_synthetic_members_through_runtime_access()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            echo System.DayOfWeek.Friday.NumericValue
            System.DayOfWeek.Friday | get NumericValue
            System.DayOfWeek.Friday | get-prop Names
            System.DayOfWeek.Friday | has-prop NumericValue
            System.DayOfWeek.Friday | get-props
            """);

        Assert.Equal(5, Assert.IsType<int>(results[0]));
        Assert.Equal(5, Assert.IsType<int>(results[1]));
        Assert.Equal(["Friday"], Assert.IsType<string[]>(results[2]));
        Assert.True(Assert.IsType<bool>(results[3]));
        Assert.Contains("Names", results.Skip(4).Select(item => item?.ToString()));
        Assert.Contains("NumericValue", results.Skip(4).Select(item => item?.ToString()));
    }

    [Fact]
    public async Task Members_distinguish_enum_helpers_from_real_clr_members()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            members System.DayOfWeek | where _.Name == NumericValue | first | get Origin
            members System.DayOfWeek | where _.Name == NumericValue | first | get Kind
            members System.DayOfWeek | where _.Name == Friday | first | get Origin
            members System.DayOfWeek | where _.Name == Friday | first | get Kind
            """);

        Assert.Equal("Shell", Assert.IsType<string>(results[0]));
        Assert.Equal("Helper", Assert.IsType<string>(results[1]));
        Assert.Equal("CLR", Assert.IsType<string>(results[2]));
        Assert.Equal("Field", Assert.IsType<string>(results[3]));
    }

    [Fact]
    public async Task Qualified_clr_type_names_resolve_to_type_objects()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            echo System.DayOfWeek | get IsEnum
            echo System.DayOfWeek | get FullName
            """);

        Assert.True(Assert.IsType<bool>(results[0]));
        Assert.Equal("System.DayOfWeek", Assert.IsType<string>(results[1]));
    }

    [Fact]
    public async Task Raw_command_emits_plain_shell_text_lines()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("echo 1317 System.DayOfWeek.Friday | raw");

        Assert.Collection(
            results,
            item => Assert.Equal("1317", Assert.IsType<ShellTextLine>(item).Text),
            item => Assert.Equal("Friday", Assert.IsType<ShellTextLine>(item).Text));
        Assert.Equal($"1317{Environment.NewLine}Friday", runtime.Display.RenderMany(results));
    }

    [Fact]
    public async Task Named_records_support_defaults_and_structural_equality()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            record Item(name: string, quantity: int, category?: string = "Food")
            var bread = new Item("Bread", 2)
            $bread.Category
            echo ((new Item("Bread", 2, "Food")) == (new Item("Bread", 2, "Food")))
            """);

        Assert.Equal("Food", results[0]);
        Assert.Equal(true, results[1]);
    }

    [Fact]
    public async Task Drive_info_size_members_are_exposed_as_storage_size_values()
    {
        var engine = ShellEngine.CreateFullShell();
        var driveRoot = System.IO.Path.GetPathRoot(Environment.CurrentDirectory)
                        ?? throw new InvalidOperationException("Unable to determine the current drive root.");
        var escapedDriveRoot = driveRoot
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        var projectedResults = await engine.ExecuteToListAsync(
            $"echo new System.IO.DriveInfo(\"{escapedDriveRoot}\") | get {{ AvailableFreeSpace, TotalFreeSpace, TotalSize }}");
        var typeResults = await engine.ExecuteToListAsync(
            $"echo new System.IO.DriveInfo(\"{escapedDriveRoot}\") | get TotalSize | type-of");

        var projection = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(projectedResults));
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
        var pipedScalarResults = await engine.ExecuteToListAsync("echo 1317 | writeline");

        Assert.Empty(writeResults);
        Assert.Empty(writeLineResults);
        Assert.Empty(pipedScalarResults);
        Assert.Equal($"hello world{Environment.NewLine}1317{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task View_can_configure_datetime_timespan_and_storage_size_preferences()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var dateResults = await engine.ExecuteToListAsync("view datetime table unix");
        var timeSpanResults = await engine.ExecuteToListAsync("view duration table seconds");
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

        Assert.Equal(DurationDisplayMode.TotalSeconds, runtime.DisplayPreferences.TimeSpan.TableMode);
        Assert.Collection(
            timeSpanResults.Cast<DisplayPreferenceStatus>(),
            item =>
            {
                Assert.Equal("timespan", item.Target);
                Assert.Equal("scalar", item.Scope);
                Assert.Equal("long", item.Mode);
            },
            item =>
            {
                Assert.Equal("timespan", item.Target);
                Assert.Equal("table", item.Scope);
                Assert.Equal("seconds", item.Mode);
            });

        Assert.Equal(StorageSizeDisplayMode.Bytes, runtime.DisplayPreferences.StorageSize.Mode);
        var status = Assert.IsType<DisplayPreferenceStatus>(Assert.Single(sizeResults));
        Assert.Equal("storage-size", status.Target);
        Assert.Equal("bytes", status.Mode);
    }

    [Fact]
    public async Task View_can_configure_dateonly_and_timeonly_preferences()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var dateOnlyResults = await engine.ExecuteToListAsync("view dateonly scalar relative");
        var timeOnlyResults = await engine.ExecuteToListAsync("view timeonly table 24h");

        Assert.Equal(DateOnlyDisplayMode.Relative, runtime.DisplayPreferences.DateOnly.ScalarMode);
        Assert.Collection(
            dateOnlyResults.Cast<DisplayPreferenceStatus>(),
            item =>
            {
                Assert.Equal("dateonly", item.Target);
                Assert.Equal("scalar", item.Scope);
                Assert.Equal("relative", item.Mode);
            },
            item =>
            {
                Assert.Equal("dateonly", item.Target);
                Assert.Equal("table", item.Scope);
                Assert.Equal("iso", item.Mode);
            });

        Assert.Equal(TimeOnlyDisplayMode.TwentyFourHour, runtime.DisplayPreferences.TimeOnly.TableMode);
        Assert.Collection(
            timeOnlyResults.Cast<DisplayPreferenceStatus>(),
            item =>
            {
                Assert.Equal("timeonly", item.Target);
                Assert.Equal("scalar", item.Scope);
                Assert.Equal("12h", item.Mode);
            },
            item =>
            {
                Assert.Equal("timeonly", item.Target);
                Assert.Equal("table", item.Scope);
                Assert.Equal("24h", item.Mode);
            });
    }

    [Fact]
    public async Task View_can_configure_permissions_and_file_attribute_preferences()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var permissionResults = await engine.ExecuteToListAsync("view permissions both");
        var attributeResults = await engine.ExecuteToListAsync("view attributes hex");

        Assert.Equal(UnixFileModeDisplayMode.Both, runtime.DisplayPreferences.UnixFileMode.Mode);
        Assert.Equal(FileAttributesDisplayMode.Hex, runtime.DisplayPreferences.FileAttributes.Mode);

        var permissionStatus = Assert.IsType<DisplayPreferenceStatus>(Assert.Single(permissionResults));
        Assert.Equal("permissions", permissionStatus.Target);
        Assert.Equal("both", permissionStatus.Mode);

        var attributeStatus = Assert.IsType<DisplayPreferenceStatus>(Assert.Single(attributeResults));
        Assert.Equal("file-attributes", attributeStatus.Target);
        Assert.Equal("hex", attributeStatus.Mode);
    }

    [Fact]
    public async Task View_can_configure_type_specific_table_columns()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var setResults = await engine.ExecuteToListAsync("view columns table Kind Name");

        Assert.True(runtime.DisplayPreferences.Profiles.TryGet("table", out var profile));
        Assert.Equal(["Kind", "Name"], profile.TableColumns);

        var setStatus = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(setResults));
        Assert.Equal("table", setStatus["Type"]);
        Assert.Equal("custom", setStatus["Source"]);
        Assert.Equal(["Kind", "Name"], Assert.IsType<string[]>(setStatus["TableColumns"]));

        var resetResults = await engine.ExecuteToListAsync("view columns table default");

        Assert.False(runtime.DisplayPreferences.Profiles.TryGet("table", out _));
        var resetStatus = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(resetResults));
        Assert.Equal("default", resetStatus["Source"]);
        Assert.Equal(Array.Empty<string>(), Assert.IsType<string[]>(resetStatus["TableColumns"]));
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
        var engine = new ToshEngine(runtime) { IsInteractiveSession = true };

        var results = await engine.ExecuteToListAsync("history");

        Assert.Collection(
            results,
            item => Assert.Equal("help", Assert.IsType<CommandHistoryEntry>(item).Text),
            item => Assert.Equal("ls -la", Assert.IsType<CommandHistoryEntry>(item).Text));

        var whenResults = await engine.ExecuteToListAsync("history | get When");
        Assert.All(whenResults, item => Assert.IsType<DateTimeOffset>(item));
    }

    [Fact]
    public async Task History_can_expand_and_run_history_designators()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.RecordHistory("echo alpha");
        runtime.RecordHistory("echo beta gamma");
        var engine = new ToshEngine(runtime) { IsInteractiveSession = true };

        var expandedById = await engine.ExecuteToListAsync("history expand 1");
        Assert.Equal("echo alpha", Assert.IsType<string>(Assert.Single(expandedById)));

        var expandedRelative = await engine.ExecuteToListAsync("history expand -1");
        Assert.Equal("echo beta gamma", Assert.IsType<string>(Assert.Single(expandedRelative)));

        var expandedContains = await engine.ExecuteToListAsync("history expand \"?alp?\"");
        Assert.Equal("echo alpha", Assert.IsType<string>(Assert.Single(expandedContains)));

        var expandedLastWord = await engine.ExecuteToListAsync("history expand \"!$\"");
        Assert.Equal("gamma", Assert.IsType<string>(Assert.Single(expandedLastWord)));

        var expandedFirstArgument = await engine.ExecuteToListAsync("history expand \"!^\"");
        Assert.Equal("beta", Assert.IsType<string>(Assert.Single(expandedFirstArgument)));

        var expandedAllArguments = await engine.ExecuteToListAsync("history expand \"!*\"");
        Assert.Equal("beta gamma", Assert.IsType<string>(Assert.Single(expandedAllArguments)));

        var expandedByIdLastWord = await engine.ExecuteToListAsync("history expand \"1:$\"");
        Assert.Equal("alpha", Assert.IsType<string>(Assert.Single(expandedByIdLastWord)));

        var runResults = await engine.ExecuteToListAsync("history run 2");
        Assert.Collection(
            runResults,
            item => Assert.Equal("beta", Assert.IsType<string>(item)),
            item => Assert.Equal("gamma", Assert.IsType<string>(item)));
    }

    [Fact]
    public async Task History_can_search_and_delete_entries()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.RecordHistory("echo alpha");
        runtime.RecordHistory("git status");
        runtime.RecordHistory("echo beta");
        var engine = new ToshEngine(runtime) { IsInteractiveSession = true };

        var searchResults = await engine.ExecuteToListAsync("history search echo | get Text");
        Assert.Collection(
            searchResults,
            item => Assert.Equal("echo alpha", item),
            item => Assert.Equal("echo beta", item));

        var deleteResults = await engine.ExecuteToListAsync("history delete git");
        var deletion = Assert.IsType<Tosh.Stdlib.Shell.HistoryDeletionResult>(Assert.Single(deleteResults));
        Assert.Equal("git status", deletion.Text);

        Assert.Collection(
            runtime.History,
            entry => Assert.Equal("echo alpha", entry.Text),
            entry => Assert.Equal("echo beta", entry.Text));
    }

    [Fact]
    public async Task History_command_can_report_path_reload_and_clear_persisted_history()
    {
        using var tempDirectory = new TemporaryDirectory();
        var historyPath = Path.Combine(tempDirectory.Path, "history.jsonl");

        var seedRuntime = ToshRuntime.CreateDefault();
        seedRuntime.Config.History.FilePath = historyPath;
        seedRuntime.InitializeHistoryStorage(writeThrough: true);
        seedRuntime.RecordHistory("help");
        seedRuntime.RecordHistory("ls -la");

        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.History.FilePath = historyPath;
        var engine = new ToshEngine(runtime) { IsInteractiveSession = true };

        var pathResults = await engine.ExecuteToListAsync("history path");
        Assert.Equal(historyPath, Assert.IsType<string>(Assert.Single(pathResults)));

        var reloadResults = await engine.ExecuteToListAsync("history reload");
        var reloadStatus = Assert.IsType<Tosh.Stdlib.Shell.HistoryStatusResult>(Assert.Single(reloadResults));
        Assert.Equal("reload", reloadStatus.Action);
        Assert.Equal(2, reloadStatus.EntryCount);
        Assert.Collection(
            runtime.History,
            entry => Assert.Equal("help", entry.Text),
            entry => Assert.Equal("ls -la", entry.Text));

        var clearResults = await engine.ExecuteToListAsync("history clear");
        var clearStatus = Assert.IsType<Tosh.Stdlib.Shell.HistoryStatusResult>(Assert.Single(clearResults));
        Assert.Equal("clear", clearStatus.Action);
        Assert.Empty(runtime.History);
        Assert.Equal(string.Empty, File.ReadAllText(historyPath));
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

    [Fact]
    public async Task Nameof_returns_variable_name()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("var thisIsAVar = 13\necho nameof($thisIsAVar)");

        Assert.Equal("thisIsAVar", Assert.Single(results));
    }

    [Fact]
    public async Task Nameof_works_with_dollar_prefix()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("var greeting = \"hello\"\necho nameof($greeting)");

        Assert.Equal("greeting", Assert.Single(results));
    }

    [Fact]
    public async Task Nameof_works_in_interpolated_string()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("var myVar = 42\necho $\"Name: {nameof($myVar)}\"");

        Assert.Equal("Name: myVar", Assert.Single(results));
    }

    [Fact]
    public async Task Nameof_works_with_bareword_function_name()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo nameof(echo)");

        Assert.Equal("echo", Assert.Single(results));
    }

    [Fact]
    public async Task Nameof_rejects_bare_variable_name_without_dollar()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("var myVar = 1\necho nameof(myVar)"));

        Assert.Contains("nameof require '$'", exception.Message);
    }

    [Fact]
    public async Task Has_prop_returns_true_for_existing_property()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo \"{\\\"name\\\":\\\"toast\\\"}\" | from json | has-prop name");

        Assert.Equal(true, Assert.Single(results));
    }

    [Fact]
    public async Task Has_prop_returns_false_for_missing_property()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo \"{\\\"name\\\":\\\"toast\\\"}\" | from json | has-prop missing");

        Assert.Equal(false, Assert.Single(results));
    }

    [Fact]
    public async Task Has_prop_works_with_direct_argument()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("var obj = {| Name = \"toast\" |}\nhas-prop $obj Name");

        Assert.Equal(true, Assert.Single(results));
    }

    [Fact]
    public async Task Has_method_checks_clr_methods()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo hello | has-method Contains");

        Assert.Equal(true, Assert.Single(results));
    }

    [Fact]
    public async Task Get_props_lists_record_fields()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("var obj = {| Name = \"toast\", Size = 2 |}\nget-props $obj");

        Assert.Equal(["Name", "Size"], results.Select(r => r?.ToString()!).ToArray());
    }

    [Fact]
    public async Task Get_methods_lists_object_methods()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo hello | get-methods");

        Assert.Contains("Contains", results.Select(r => r?.ToString()));
        Assert.Contains("StartsWith", results.Select(r => r?.ToString()));
    }

    [Fact]
    public async Task ThisFunc_returns_current_function_name()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("func greet() { echo $tosh.Function.Name }\ngreet");

        Assert.Equal("greet", Assert.Single(results));
    }

    [Fact]
    public async Task ThisFunc_returns_empty_string_outside_functions()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo $tosh.Function.Name");

        Assert.Equal(string.Empty, Assert.Single(results));
    }

    [Fact]
    public async Task ThisFunc_reflects_innermost_function_in_nested_calls()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            "func inner() { echo $tosh.Function.Name }\nfunc outer() { inner }\nouter");

        Assert.Equal("inner", Assert.Single(results));
    }

    [Fact]
    public async Task ThisScript_returns_source_name()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo $tosh.Script.Path", "my-script.tosh");

        Assert.Equal("my-script.tosh", Assert.Single(results));
    }

    [Fact]
    public async Task ThisScript_returns_input_for_repl()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo $tosh.Script.Path");

        Assert.Equal("<input>", Assert.Single(results));
    }

    [Fact]
    public async Task Get_prop_reads_dynamic_property_by_name()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            "var obj = {| Name = \"toast\", Size = 2 |}\nvar prop = \"Name\"\nget-prop $obj $prop");

        Assert.Equal("toast", Assert.Single(results));
    }

    [Fact]
    public async Task Get_prop_reads_clr_property()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo hello | get-prop Length");

        Assert.Equal(5, Assert.Single(results));
    }

    [Fact]
    public async Task Set_prop_adds_property_to_record()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            "var obj = {| Name = \"toast\" |}\nset-prop $obj Size 42\nget-prop $obj Size");

        Assert.Equal(2, results.Count);
        Assert.Equal("42", results[1]?.ToString());
    }

    [Fact]
    public async Task Set_prop_updates_existing_property()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            "var obj = {| Name = \"toast\" |}\nset-prop $obj Name \"bread\" | get-prop Name");

        Assert.Equal("bread", Assert.Single(results));
    }

    [Fact]
    public async Task Del_prop_removes_property_from_record()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            "var obj = {| Name = \"toast\", Size = 2 |}\ndel-prop $obj Size | has-prop Size");

        Assert.Equal(false, Assert.Single(results));
    }

    [Fact]
    public async Task Call_method_invokes_by_dynamic_name()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo hello | call-method ToUpper");

        Assert.Equal("HELLO", Assert.Single(results));
    }

    [Fact]
    public async Task Call_method_passes_arguments()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo hello | call-method Contains ell");

        Assert.Equal(true, Assert.Single(results));
    }

    [Fact]
    public async Task Clone_creates_independent_copy_of_record()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            "var obj = {| Name = \"toast\" |}\nvar copy = (clone $obj)\nvar ignored = (set-prop $copy Name \"bread\")\nget-prop $obj Name");

        Assert.Equal("toast", Assert.Single(results));
    }

    [Fact]
    public async Task Clone_works_in_pipeline()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            "var obj = {| A = 1, B = 2 |}\n$obj | clone | get-props");

        Assert.Equal(["A", "B"], results.Select(r => r?.ToString()!).ToArray());
    }

    [Fact]
    public async Task String_concatenation_with_plus_in_subexpression()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("var a = \"hello\"\nvar b = \"world\"\necho ($a + \" \" + $b)");

        Assert.Equal("hello world", Assert.Single(results));
    }

    [Fact]
    public async Task String_plus_in_command_argument_subexpression()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Simplest case: echo ($a + $b) where $a and $b are strings
        var results = await engine.ExecuteToListAsync("var a = \"hello\"\nvar b = \"world\"\necho ($a + $b)");

        Assert.Equal("helloworld", Assert.Single(results));
    }

    [Fact]
    public async Task String_plus_with_comma_literal_in_subexpression()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // This is the pattern from inventory.tosh: set-prop $item Tags ($existing + "," + $tag)
        var results = await engine.ExecuteToListAsync("var a = \"hello\"\nvar b = \"world\"\necho ($a + \",\" + $b)");

        Assert.Equal("hello,world", Assert.Single(results));
    }

    [Fact]
    public async Task Set_prop_with_plus_concat_subexpression()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Reproduces the inventory.tosh pattern: set-prop $item Tags ($existing + "," + $tag)
        var results = await engine.ExecuteToListAsync(
            "var obj = {| Name = \"test\" |}\n" +
            "var a = \"hello\"\n" +
            "var b = \"world\"\n" +
            "var ignored = (set-prop $obj Name ($a + \",\" + $b))\n" +
            "get-prop $obj Name");

        Assert.Equal("hello,world", Assert.Single(results));
    }

    [Fact]
    public async Task Cli_host_returns_last_external_exit_code_for_command_invocations()
    {
        var projectRoot = GetProjectRoot();
        var cliPath = ToshCli.AssemblyPath;
        using var configDirectory = new TemporaryDirectory();

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.Environment["TOSH_CONFIG_HOME"] = configDirectory.Path;
        process.StartInfo.ArgumentList.Add(cliPath);
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("/bin/sh -c \"exit 7\"");

        process.Start();
        await process.WaitForExitAsync();

        Assert.Equal(7, process.ExitCode);
    }

    private static string GetProjectRoot()
    {
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }

    [Fact]
    public async Task AutoCd_changes_directory_when_bare_name_matches_subdirectory()
    {
        using var tempDirectory = new TemporaryDirectory();
        var subDir = System.IO.Path.Combine(tempDirectory.Path, "mydir");
        Directory.CreateDirectory(subDir);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        runtime.Config.Shell.AutoCd = true;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("mydir");

        Assert.Equal(subDir, runtime.CurrentDirectory);
        var entry = Assert.IsType<FileSystemEntry>(Assert.Single(results));
        Assert.Equal("mydir", entry.Name);
    }

    [Fact]
    public async Task AutoCd_changes_directory_when_explicit_path_is_directory()
    {
        using var tempDirectory = new TemporaryDirectory();
        var subDir = System.IO.Path.Combine(tempDirectory.Path, "nested");
        Directory.CreateDirectory(subDir);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        runtime.Config.Shell.AutoCd = true;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("./nested");

        Assert.Equal(subDir, runtime.CurrentDirectory);
    }

    [Fact]
    public async Task AutoCd_disabled_throws_for_directory_name()
    {
        using var tempDirectory = new TemporaryDirectory();
        Directory.CreateDirectory(System.IO.Path.Combine(tempDirectory.Path, "mydir"));

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        runtime.Config.Shell.AutoCd = false;
        var engine = new ToshEngine(runtime);

        await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("./mydir"));
    }

    [Fact]
    public async Task Cd_syncs_process_working_directory()
    {
        using var tempDirectory = new TemporaryDirectory();
        var subDir = System.IO.Path.Combine(tempDirectory.Path, "inner");
        Directory.CreateDirectory(subDir);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync($"cd \"{subDir}\"");

        Assert.Equal(subDir, runtime.CurrentDirectory);
    }

    private static string? GetNativeTestLibraryName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "ucrtbase.dll";
        }

        if (OperatingSystem.IsLinux())
        {
            return "libc.so.6";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "/usr/lib/libSystem.B.dylib";
        }

        return null;
    }

    [Fact]
    public void Parser_handles_func_with_handles_and_when_clause_single_line()
    {
        var source = "func onError(evt) handles StatusUpdate when { $evt.Level == \"error\" } { writeline \"error!\" }";
        var result = ToshParser.Parse(source);

        foreach (var d in result.Diagnostics)
        {
            Assert.Fail($"Diagnostic: [{d.Code}] {d.Title} at {d.Span} — {d.Label}");
        }

        var funcStmt = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);
        Assert.Equal("onError", funcStmt.Name);
        Assert.Equal("StatusUpdate", funcStmt.HandlesEvent);
        Assert.NotNull(funcStmt.WhenGuard);
    }

    [Fact]
    public void Parser_handles_func_with_handles_and_when_clause_multi_line()
    {
        var source = """
            func onError(evt)
                handles StatusUpdate
                when { $evt.Level == "error" }
            {
                writeline "error!"
            }
            """;
        var result = ToshParser.Parse(source);

        foreach (var d in result.Diagnostics)
        {
            Assert.Fail($"Diagnostic: [{d.Code}] {d.Title} at {d.Span} — {d.Label}");
        }

        var funcStmt = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);
        Assert.Equal("onError", funcStmt.Name);
        Assert.Equal("StatusUpdate", funcStmt.HandlesEvent);
        Assert.NotNull(funcStmt.WhenGuard);
    }

    [Theory]
    [MemberData(nameof(MultiLineFuncDefinitions))]
    public void Parser_handles_multiline_func_definition_styles(string source)
    {
        var result = ToshParser.Parse(source);

        foreach (var d in result.Diagnostics)
        {
            Assert.Fail($"Source: {source}\nDiagnostic: [{d.Code}] {d.Title} at {d.Span} — {d.Label}");
        }
    }

    public static IEnumerable<object[]> MultiLineFuncDefinitions()
    {
        // Params on separate lines, body on next line
        yield return ["""
            func projectGuard(
                var1: int,
                var2: string,
                var3: bool
            )
            {
                writeline "entering project dir"
            }
            """];

        // Mixed params, some on same line
        yield return ["""
            func projectGuard(
                var1: int,
                var2: string, var3: bool)
            {
                writeline "entering project dir"
            }
            """];

        // Body on same line as opening brace, close paren on own line
        yield return ["""
            func projectGuard(
                var1: int,
                var2: string,
                var3: bool
            )
            { writeline "entering project dir" }
            """];

        // Close paren and body on same line
        yield return ["""
            func projectGuard(
                var1: int,
                var2: string,
                var3: bool
            ) { writeline "entering project dir" }
            """];

        // Close paren with last param and body all on one line
        yield return ["""
            func projectGuard(
                var1: int,
                var2: string,
                var3: bool ) { writeline "entering project dir" }
            """];

        // Inline body
        yield return ["func projectGuard(var1: int, var2: string, var3: bool) { writeline \"entering project dir\" }"];

        // Arrow wrapper (single statement)
        yield return ["func projectGuard(var1: int, var2: string, var3: bool) => writeline \"entering project dir\""];

        // Body opening brace on same line, content + close on next lines
        yield return ["""
            func projectGuard(var1: int, var2: string, var3: bool) {
                writeline "entering project dir" }
            """];

        // Body opening brace on same line, close on own line
        yield return ["""
            func projectGuard(var1: int, var2: string, var3: bool) {
                writeline "entering project dir"
            }
            """];

        // Arrow wrapper with pipe
        yield return ["func someAlias(arg1, arg2) => ls -la $arg1 | where Type == $arg2"];
    }

    [Fact]
    public async Task Rest_parameter_collects_surplus_arguments_with_named_syntax()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("""
            func gather(first, names...) {
                echo $first
                $names | each { echo $_ }
            }
            """);
        var results = await engine.ExecuteToListAsync("gather X Y Z");

        Assert.Collection(results,
            item => Assert.Equal("X", item),
            item => Assert.Equal("Y", item),
            item => Assert.Equal("Z", item));
    }

    [Fact]
    public async Task Rest_parameter_collects_surplus_arguments_with_shorthand_syntax()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("""
            func collect(first, ...) {
                echo $first
                $args | each { echo $_ }
            }
            """);
        var results = await engine.ExecuteToListAsync("collect A B C");

        Assert.Collection(results,
            item => Assert.Equal("A", item),
            item => Assert.Equal("B", item),
            item => Assert.Equal("C", item));
    }

    [Fact]
    public async Task Anonymous_expression_lambda_can_be_invoked()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var double = func(x) => ($x * 2)
            invoke $double 21
            """);

        Assert.Equal(42, Assert.Single(results));
    }

    [Fact]
    public async Task Callable_invocation_postfix_can_invoke_lambda_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var double = func(x) => ($x * 2)
            $double(21)
            """);

        Assert.Equal(42, Assert.Single(results));
    }

    [Fact]
    public async Task Callable_invocation_postfix_supports_currying_chains()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var add3 = func(a, b, c) => ($a + $b + $c)
            (curry $add3)(1)(2)(39)
            """);

        Assert.Equal(42, Assert.Single(results));
    }

    [Fact]
    public async Task Callable_invocation_postfix_requires_callable_targets()
    {
        var engine = ShellEngine.CreateFullShell();

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                var x = 42
                $x(1)
                """));

        Assert.Equal("tosh.runtime.value_not_callable", Assert.Single(exception.Diagnostics).Code);
    }

    [Fact]
    public async Task Anonymous_block_lambda_can_return_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var describe = func(x) {
                if (($x > 10)) {
                    return "big"
                }

                return "small"
            }

            invoke $describe 12
            invoke $describe 2
            """);

        Assert.Collection(
            results,
            item => Assert.Equal("big", item),
            item => Assert.Equal("small", item));
    }

    [Fact]
    public async Task Anonymous_functions_capture_live_scope_bindings()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var factor = 2
            var scale = func(x) => ($x * $factor)
            $factor = 3
            invoke $scale 7
            """);

        Assert.Equal(21, Assert.Single(results));
    }

    [Fact]
    public async Task Top_level_functions_support_overloading_by_arity()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            func greet() { echo noargs }
            func greet(name) { echo hello $name }

            greet
            greet toast
            """);

        Assert.Collection(
            results,
            item => Assert.Equal("noargs", item),
            item => Assert.Equal("hello", item),
            item => Assert.Equal("toast", item));
    }

    [Fact]
    public async Task Top_level_functions_support_overloading_by_type_annotation()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            func kind(value: int) { echo int }
            func kind(value: string) { echo string }

            kind 42
            kind hello
            """);

        Assert.Collection(
            results,
            item => Assert.Equal("int", item),
            item => Assert.Equal("string", item));
    }

    [Fact]
    public async Task Top_level_function_overloads_report_ambiguity_for_nullable_null_matches()
    {
        var engine = ShellEngine.CreateFullShell();

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                func pick(value: int?) { echo int }
                func pick(value: long?) { echo long }

                pick null
                """));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.runtime.function_overload_ambiguous", diagnostic.Code);
        Assert.Contains("Multiple overloads matched function 'pick'", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Top_level_function_overloads_report_ambiguity_for_convertible_numeric_matches()
    {
        var engine = ShellEngine.CreateFullShell();

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                func widen(value: int) { echo int }
                func widen(value: long) { echo long }

                widen (cast short 5)
                """));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.runtime.function_overload_ambiguous", diagnostic.Code);
        Assert.Contains("Multiple overloads matched function 'widen'", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Top_level_function_redefinition_replaces_matching_overload_signature()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            func greet(name: string) { echo old }
            func greet(person: string) { echo new }

            greet toast
            """);

        Assert.Equal("new", Assert.Single(results));
    }

    [Fact]
    public async Task Function_references_invoke_matching_overload()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            func pick(value) { echo one }
            func pick(left, right) { echo two }

            var f = &pick
            invoke $f alpha
            invoke $f alpha beta
            """);

        Assert.Collection(
            results,
            item => Assert.Equal("one", item),
            item => Assert.Equal("two", item));
    }

    [Fact]
    public async Task Class_method_overloads_report_ambiguity_for_nullable_null_matches()
    {
        var engine = ShellEngine.CreateFullShell();

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                class Picker {
                    func pick(value: int?) { echo int }
                    func pick(value: long?) { echo long }
                }

                var picker = new Picker()
                $picker.pick(null)
                """));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Contains("Multiple overloads matched method 'Picker.pick'", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Class_constructor_overloads_report_ambiguity_for_nullable_null_matches()
    {
        var engine = ShellEngine.CreateFullShell();

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                class Picker {
                    Picker(value: int?) { }
                    Picker(value: long?) { }
                }

                new Picker(null)
                """));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Contains("Multiple constructor overloads matched class 'Picker'", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_requires_a_callable_value()
    {
        var engine = ShellEngine.CreateFullShell();

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("invoke 42"));

        Assert.Equal("tosh.runtime.value_not_callable", Assert.Single(exception.Diagnostics).Code);
    }

    [Fact]
    public async Task Map_supports_callable_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            echo 1 2 3 | map func(x) => ($x * 2)
            """);

        Assert.Collection(
            results,
            item => Assert.Equal(2, item),
            item => Assert.Equal(4, item),
            item => Assert.Equal(6, item));
    }

    [Fact]
    public async Task Map_supports_blocks()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            echo hello world | map { _.ToUpper() }
            """);

        Assert.Collection(
            results,
            item => Assert.Equal("HELLO", item),
            item => Assert.Equal("WORLD", item));
    }

    [Fact]
    public async Task Filter_supports_callable_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            echo 1 2 3 4 | filter func(x) => ((($x % 2) == 0))
            """);

        Assert.Collection(
            results,
            item => Assert.Equal(2, item),
            item => Assert.Equal(4, item));
    }

    [Fact]
    public async Task Filter_supports_blocks()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            echo one two three | filter { _.Contains("o") }
            """);

        Assert.Collection(
            results,
            item => Assert.Equal("one", item),
            item => Assert.Equal("two", item));
    }

    [Fact]
    public async Task Reduce_supports_callable_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            echo 1 2 3 4 | reduce 0 func(acc, x) => ($acc + $x)
            """);

        Assert.Equal(10, Assert.Single(results));
    }

    [Fact]
    public async Task Reduce_supports_blocks()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            echo one two three | reduce "" { $acc + _.Substring(0, 1) }
            """);

        Assert.Equal("ott", Assert.Single(results));
    }

    [Fact]
    public async Task Any_all_and_none_support_callable_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var anyResults = await engine.ExecuteToListAsync("echo 1 2 3 | any func(x) => ($x == 2)");
        var allResults = await engine.ExecuteToListAsync("echo 2 4 6 | all func(x) => ((($x % 2) == 0))");
        var noneResults = await engine.ExecuteToListAsync("echo 1 2 3 | none func(x) => ($x > 10)");

        Assert.Equal(true, Assert.Single(anyResults));
        Assert.Equal(true, Assert.Single(allResults));
        Assert.Equal(true, Assert.Single(noneResults));
    }

    [Fact]
    public async Task Existing_pipeline_commands_accept_callable_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var whereResults = await engine.ExecuteToListAsync("echo 1 2 3 4 | where func(x) => ($x > 2)");
        var eachResults = await engine.ExecuteToListAsync("echo one two | each func(x) => ($x.ToUpper())");
        var foreachResults = await engine.ExecuteToListAsync("echo alpha beta | foreach func(x) => ($x + \"!\")");
        var selectResults = await engine.ExecuteToListAsync("echo 1 2 3 | select func(x) => ($x * 10)");
        var takeWhileResults = await engine.ExecuteToListAsync("echo 1 2 3 4 | take-while func(x) => ($x < 3)");
        var skipWhileResults = await engine.ExecuteToListAsync("echo 1 2 3 4 | skip-while func(x) => ($x < 3)");

        Assert.Equal(new object?[] { 3, 4 }, whereResults.ToArray());
        Assert.Equal(new object?[] { "ONE", "TWO" }, eachResults.ToArray());
        Assert.Equal(new object?[] { "alpha!", "beta!" }, foreachResults.ToArray());
        Assert.Equal(new object?[] { 10, 20, 30 }, selectResults.ToArray());
        Assert.Equal(new object?[] { 1, 2 }, takeWhileResults.ToArray());
        Assert.Equal(new object?[] { 3, 4 }, skipWhileResults.ToArray());
    }

    [Fact]
    public async Task Sort_and_group_by_accept_callable_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var sortResults = await engine.ExecuteToListAsync(
            """
            echo pear fig banana kiwi | sort func(x) => ($x.Length)
            """);

        var groupResults = await engine.ExecuteToListAsync(
            """
            echo ant ape bear boar | group-by func(x) => ($x.Substring(0, 1))
            """);

        Assert.Equal(new object?[] { "fig", "pear", "kiwi", "banana" }, sortResults.ToArray());

        var groups = groupResults.Cast<GroupingInfo>().ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Equal("a", groups[0].Key);
        Assert.Equal(["ant", "ape"], groups[0].Items.Cast<string>().ToArray());
        Assert.Equal("b", groups[1].Key);
        Assert.Equal(["bear", "boar"], groups[1].Items.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Partial_binds_leading_arguments()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var add = func(x, y) => ($x + $y)
            var inc = partial $add 1
            invoke $inc 41
            """);

        Assert.Equal(42, Assert.Single(results));
    }

    [Fact]
    public async Task Curry_accumulates_arguments_until_saturated()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var add3 = func(a, b, c) => ($a + $b + $c)
            var curried = curry $add3
            var step1 = invoke $curried 1
            var step2 = invoke $step1 2
            invoke $step2 39
            """);

        Assert.Equal(42, Assert.Single(results));
    }

    [Fact]
    public async Task Curry_rejects_non_fixed_arity_callables()
    {
        var engine = ShellEngine.CreateFullShell();

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                var gather = func(first, rest...) {
                    echo $first
                }

                curry $gather
                """));

        Assert.Equal("tosh.runtime.curry_requires_fixed_arity_callable", Assert.Single(exception.Diagnostics).Code);
    }

    [Fact]
    public async Task Rest_parameter_binds_empty_list_when_no_surplus_arguments()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("""
            func f(a, extras...) {
                echo $a
                echo ($extras | count)
            }
            """);
        var results = await engine.ExecuteToListAsync("f only");

        Assert.Collection(results,
            item => Assert.Equal("only", item),
            item => Assert.Equal(0, item));
    }

    [Fact]
    public async Task Function_without_rest_parameter_still_rejects_extra_arguments()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("func strict(a, b) { echo ok }");

        await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("strict 1 2 3"));
    }

    [Fact]
    public void Parser_rejects_rest_parameter_that_is_not_last()
    {
        var result = ToshParser.Parse("func bad(items..., last) { echo nope }");

        Assert.Contains(result.Diagnostics, d => d.Code == "tosh.parser.rest_parameter_must_be_last");
    }

    [Fact]
    public void Parser_parses_standalone_rest_shorthand()
    {
        var result = ToshParser.Parse("func variadic(...) { echo ok }");

        var function = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);
        var param = Assert.Single(function.Parameters);
        Assert.Equal("args", param.Name);
        Assert.True(param.IsRest);
    }

    [Fact]
    public void Parser_parses_named_rest_parameter()
    {
        var result = ToshParser.Parse("func variadic(first, rest...) { echo ok }");

        var function = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);
        Assert.Equal(2, function.Parameters.Count);
        Assert.False(function.Parameters[0].IsRest);
        Assert.Equal("first", function.Parameters[0].Name);
        Assert.True(function.Parameters[1].IsRest);
        Assert.Equal("rest", function.Parameters[1].Name);
    }

    [Fact]
    public async Task Static_method_with_args_returns_class_instance()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("""
            export class Greeter {
                prop Message = null
                static func Create(msg: string) {
                    var g = new Greeter()
                    $g.Message = $msg
                    return $g
                }
            }
            """);
        var results = await engine.ExecuteToListAsync("Greeter.Create(\"hello\") | get Message");

        Assert.Collection(results, item => Assert.Equal("hello", item));
    }

    [Fact]
    public async Task Static_method_without_args_gives_overload_error()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("""
            export class Greeter {
                prop Message = null
                static func Create(msg: string) {
                    var g = new Greeter()
                    $g.Message = $msg
                    return $g
                }
            }
            """);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("Greeter.Create()"));

        Assert.Contains("argument", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Static_member_access_without_parens_gives_helpful_error()
    {
        var engine = ShellEngine.CreateFullShell();

        await engine.ExecuteToListAsync("""
            export class Greeter {
                prop Message = null
                static func Create(msg: string) {
                    var g = new Greeter()
                    return $g
                }
            }
            """);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("Greeter.Create"));

        Assert.Contains("Greeter.Create(...)", exception.Message);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  CLI stress-test fixes
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Distinct_expands_a_spread_collection()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo ...[3,1,4,1,5,9] | distinct | count");

        Assert.Collection(results, item => Assert.Equal(5, item));
    }

    [Fact]
    public async Task Reverse_expands_a_spread_collection()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo ...[1,2,3] | reverse");

        Assert.Equal(new object[] { 3, 2, 1 }, results.ToArray());
    }

    [Fact]
    public async Task Skip_expands_a_spread_collection()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo ...[10,20,30,40] | skip 2");

        Assert.Equal(new object[] { 30, 40 }, results.ToArray());
    }

    [Fact]
    public async Task Each_agrees_with_count_on_a_spread_collection()
    {
        var engine = ShellEngine.CreateFullShell();

        var eachCount = await engine.ExecuteToListAsync("echo ...[1,2,3] | each { echo ITEM } | count");
        var directCount = await engine.ExecuteToListAsync("echo ...[1,2,3] | count");

        Assert.Collection(eachCount, item => Assert.Equal(3, item));
        Assert.Collection(directCount, item => Assert.Equal(3, item));
    }

    [Fact]
    public async Task Try_catch_preserves_pre_exception_output()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("""try { echo "before"; throw "err" } catch (e) { echo "caught" }""");

        Assert.Equal(new[] { "before", "caught" }, results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Try_without_exception_yields_all_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("""try { echo "a"; echo "b" } catch (e) { echo "nope" }""");

        Assert.Equal(new[] { "a", "b" }, results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Array_plus_operator_concatenates_collections()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("echo ([1,2] + [3,4]) | flatten");

        Assert.Equal(new object[] { 1, 2, 3, 4 }, results.ToArray());
    }

    [Fact]
    public async Task Split_empty_delimiter_splits_into_characters()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("""echo "hello" | split "" | count""");

        Assert.Collection(results, item => Assert.Equal(5, item));
    }

    [Fact]
    public async Task If_expression_returns_then_branch_value()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("""var x = if (true) { echo 1 } else { echo 2 }; echo $x""");

        Assert.Collection(results, item => Assert.Equal(1, item));
    }

    [Fact]
    public async Task If_expression_returns_else_branch_value()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("""var x = if (false) { echo 1 } else { echo 2 }; echo $x""");

        Assert.Collection(results, item => Assert.Equal(2, item));
    }

    [Fact]
    public async Task If_expression_supports_else_if_chains()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("""var x = if (false) { echo 1 } else if (true) { echo 2 } else { echo 3 }; echo $x""");

        Assert.Collection(results, item => Assert.Equal(2, item));
    }

    [Fact]
    public async Task If_expression_returns_null_for_empty_block()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("""var x = if (true) { } else { echo 2 }; echo $x""");

        Assert.Collection(results, item => Assert.Null(item));
    }

    [Fact]
    public async Task If_expression_returns_array_for_multiple_values()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync("""var x = if (true) { echo 1; echo 2 } else { echo 3 }; echo $x | flatten""");

        Assert.Equal(new object[] { 1, 2 }, results.ToArray());
    }

    [Fact]
    public async Task Integral_arithmetic_promotes_to_biginteger_on_overflow()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var x = (9223372036854775807 + 1)
            echo $x
            echo ($x == "9223372036854775808")
            echo ($x > 9223372036854775807)
            echo ($x | type-of)
            """);

        Assert.Collection(
            results,
            item => Assert.Equal("9223372036854775808", item?.ToString()),
            item => Assert.Equal(true, item),
            item => Assert.Equal(true, item),
            item => Assert.Equal(typeof(BigInteger), item));
    }

    [Fact]
    public async Task Fibonacci_sequence_can_grow_past_fixed_width_integral_limits()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            func fibonacci(n) {
                unfold [0, 1] { [$_[0], [$_[1], ($_[0] + $_[1])]] } | first $n
            }

            fibonacci 100 | last | type-of
            fibonacci 100 | last
            """);

        Assert.Collection(
            results,
            item => Assert.Equal(typeof(BigInteger), item),
            item => Assert.Equal("218922995834555169026", item?.ToString()));
    }

    [Fact]
    public void Where_predicate_in_arrow_function_does_not_cause_parse_errors()
    {
        var source = """
            func recent(span: TimeSpan) => ls -la | where _.Modified > ((date now) - $span)
            func other() => echo done
            """;

        var result = ToshParser.Parse(source);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Cd_tilde_alias_resolves_when_alias_is_set_directly()
    {
        using var tempDirectory = new TemporaryDirectory();
        var targetPath = System.IO.Path.Combine(tempDirectory.Path, "target");
        Directory.CreateDirectory(targetPath);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        runtime.Config.Shell.Dirs.TrySetMember("myalias", targetPath);
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("cd ~myalias");

        Assert.Equal(targetPath, runtime.CurrentDirectory);
    }

    [Fact]
    public async Task Cd_tilde_alias_resolves_when_alias_is_set_via_config_assignment()
    {
        using var tempDirectory = new TemporaryDirectory();
        var targetPath = System.IO.Path.Combine(tempDirectory.Path, "target");
        Directory.CreateDirectory(targetPath);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("$tosh.Config.Shell.Dirs = {| myalias = \"" + targetPath.Replace("\\", "\\\\") + "\" |}");
        await engine.ExecuteToListAsync("cd ~myalias");

        Assert.Equal(targetPath, runtime.CurrentDirectory);
    }
}
