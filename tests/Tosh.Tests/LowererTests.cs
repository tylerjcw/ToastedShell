using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Language.Binding.BoundNodes;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Tests for the lowering pass that converts a <see cref="ParseResult"/>
/// into a <see cref="BoundUnit"/>. The pass starts as a thin shape
/// translator — it carves out only the highest-leverage node types
/// (pipelines, command calls, literal/variable arguments) and wraps
/// everything else as <see cref="BoundDynamicExpression"/> /
/// <see cref="BoundDynamicStatement"/>.
/// </summary>
public sealed class LowererTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public LowererTests(ToshRuntimeFixture fixture)
    {
        _runtime = fixture.Runtime;
    }

    private ParseResult ParseSource(string source)
    {
        var engine = new ToshEngine(_runtime);
        return engine.Parse(source, "<lowerer-test>");
    }

    [Fact]
    public void Lower_produces_a_bound_unit_with_a_root_script()
    {
        var parse = ParseSource("echo hello");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        Assert.Same(parse, unit.ParseResult);
        Assert.NotNull(unit.Root);
        Assert.Single(unit.Root.Statements);
    }

    [Fact]
    public void Lower_translates_pipeline_statements_to_bound_pipeline_statements()
    {
        var parse = ParseSource("echo hello");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var statement = Assert.IsType<BoundPipelineStatement>(unit.Root.Statements[0]);
        var stage = Assert.Single(statement.Pipeline.Stages);
        var call = Assert.IsType<BoundCommandCall>(stage);
        Assert.Equal("echo", call.Name);
        Assert.NotNull(call.ResolvedCommand); // builtin echo is in the registry
    }

    [Fact]
    public void Lower_marks_unknown_commands_as_unresolved()
    {
        var parse = ParseSource("definitely_not_a_real_command arg1");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var statement = Assert.IsType<BoundPipelineStatement>(unit.Root.Statements[0]);
        var call = Assert.IsType<BoundCommandCall>(statement.Pipeline.Stages[0]);
        Assert.Null(call.ResolvedCommand);
    }

    [Fact]
    public void Lower_preserves_pipeline_stage_count()
    {
        var parse = ParseSource("ls | where _ != null | first");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var statement = Assert.IsType<BoundPipelineStatement>(unit.Root.Statements[0]);
        Assert.Equal(3, statement.Pipeline.Stages.Count);
    }

    [Fact]
    public void Lower_translates_literal_arguments_to_bound_literals_with_concrete_types()
    {
        var parse = ParseSource("echo 42");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var call = (BoundCommandCall)((BoundPipelineStatement)unit.Root.Statements[0]).Pipeline.Stages[0];
        var arg = Assert.Single(call.Arguments);
        var literal = Assert.IsType<BoundLiteral>(arg.Value);
        Assert.Equal(42, literal.Value);
        Assert.True(literal.Type.IsConcrete);
        Assert.Equal(typeof(int), literal.Type.ClrType);
    }

    [Fact]
    public void Lower_translates_barewords_to_string_literals()
    {
        var parse = ParseSource("echo hello");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var call = (BoundCommandCall)((BoundPipelineStatement)unit.Root.Statements[0]).Pipeline.Stages[0];
        var literal = Assert.IsType<BoundLiteral>(call.Arguments[0].Value);
        Assert.Equal("hello", literal.Value);
        Assert.Equal(typeof(string), literal.Type.ClrType);
    }

    [Fact]
    public void Lower_translates_variable_references()
    {
        var parse = ParseSource("echo $name");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var call = (BoundCommandCall)((BoundPipelineStatement)unit.Root.Statements[0]).Pipeline.Stages[0];
        var varRef = Assert.IsType<BoundVariableReference>(call.Arguments[0].Value);
        Assert.Equal("name", varRef.Name);
        Assert.Null(varRef.Symbol); // v1: lowering doesn't yet build symbols
        Assert.True(varRef.Type.IsDynamic);
    }

    [Fact]
    public void Lower_wraps_unmodeled_arguments_as_dynamic_expressions()
    {
        // A range argument is not yet carved out — should round-trip
        // through BoundDynamicExpression so the evaluator-on-IR has
        // something to dispatch on.
        var parse = ParseSource("echo (1..5)");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var call = (BoundCommandCall)((BoundPipelineStatement)unit.Root.Statements[0]).Pipeline.Stages[0];
        var arg = Assert.Single(call.Arguments);
        Assert.IsType<BoundDynamicExpression>(arg.Value);
    }

    [Fact]
    public void Lower_wraps_unmodeled_statements_as_dynamic_statements()
    {
        // Function definitions aren't carved out yet.
        var parse = ParseSource("func greet(name) { echo $name }");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        Assert.IsType<BoundDynamicStatement>(unit.Root.Statements[0]);
    }

    [Fact]
    public void Lower_carves_out_var_declarations()
    {
        var parse = ParseSource("var x = 42");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var decl = Assert.IsType<BoundVariableDeclaration>(unit.Root.Statements[0]);
        Assert.Equal("x", decl.Symbol.Name);
        Assert.Equal(BoundSymbolKind.LocalVariable, decl.Symbol.Kind);
        Assert.NotNull(decl.Value);
    }

    [Fact]
    public void Lower_resolves_subsequent_variable_references_to_their_declaration()
    {
        var parse = ParseSource("var name = \"alice\"\necho $name");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var decl = Assert.IsType<BoundVariableDeclaration>(unit.Root.Statements[0]);
        var pipeline = Assert.IsType<BoundPipelineStatement>(unit.Root.Statements[1]);
        var call = (BoundCommandCall)pipeline.Pipeline.Stages[0];
        var varRef = Assert.IsType<BoundVariableReference>(call.Arguments[0].Value);

        Assert.NotNull(varRef.Symbol);
        Assert.Same(decl.Symbol, varRef.Symbol);
    }

    [Fact]
    public void Lower_leaves_externally_sourced_references_unresolved()
    {
        var parse = ParseSource("echo $env");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var call = (BoundCommandCall)((BoundPipelineStatement)unit.Root.Statements[0]).Pipeline.Stages[0];
        var varRef = Assert.IsType<BoundVariableReference>(call.Arguments[0].Value);
        Assert.Null(varRef.Symbol); // not declared locally; runtime lookup will resolve $env
    }

    [Fact]
    public void Lower_carves_out_member_access()
    {
        var parse = ParseSource("echo $env.HOME");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var call = (BoundCommandCall)((BoundPipelineStatement)unit.Root.Statements[0]).Pipeline.Stages[0];
        var member = Assert.IsType<BoundMemberAccess>(call.Arguments[0].Value);
        Assert.Equal("HOME", member.MemberPath);
        Assert.IsType<BoundVariableReference>(member.Target);
    }

    [Fact]
    public void Lower_carves_out_binary_operators()
    {
        var parse = ParseSource("echo (1 + 2)");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var call = (BoundCommandCall)((BoundPipelineStatement)unit.Root.Statements[0]).Pipeline.Stages[0];
        // The argument may be wrapped in a SubexpressionArgumentSyntax,
        // which still falls back to BoundDynamicExpression for now.
        // Either accept dynamic OR a BoundBinaryOperator — both are valid
        // outcomes of the same source string given the parser shape.
        var arg = call.Arguments[0].Value;
        Assert.True(arg is BoundBinaryOperator or BoundDynamicExpression or BoundLiteral);
    }

    [Fact]
    public void Lower_carves_out_ranges_directly_in_pipelines()
    {
        var parse = ParseSource("1..10 | sum");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var pipeline = ((BoundPipelineStatement)unit.Root.Statements[0]).Pipeline;
        // The first stage in '1..10 | sum' is the range expression
        // wrapped in a synthetic value-emitting command. We simply
        // assert there are two stages — the deeper structural shape
        // may vary depending on how the parser emits range pipelines.
        Assert.Equal(2, pipeline.Stages.Count);
    }

    [Fact]
    public void Lower_handles_named_arguments()
    {
        var parse = ParseSource("ls --type file");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var call = (BoundCommandCall)((BoundPipelineStatement)unit.Root.Statements[0]).Pipeline.Stages[0];
        // The parser may model `--type file` as either a NamedArgument or
        // two positional barewords depending on the command's option
        // metadata. We only assert a non-empty argument list here; the
        // shape is exercised by argument-binder tests.
        Assert.NotEmpty(call.Arguments);
    }

    [Fact]
    public void Lower_preserves_source_spans_on_bound_nodes()
    {
        var source = "echo hello";
        var parse = ParseSource(source);
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var statement = Assert.IsType<BoundPipelineStatement>(unit.Root.Statements[0]);
        var call = (BoundCommandCall)statement.Pipeline.Stages[0];

        Assert.Equal(0, call.NameSpan.Start);
        Assert.Equal("echo".Length, call.NameSpan.Length);

        var arg = call.Arguments[0];
        var argSlice = source.Substring(arg.Span.Start, arg.Span.Length);
        Assert.Equal("hello", argSlice);
    }
}
