using System.Reflection;
using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// TS-P1-20 — a parenthesized pipeline used where a single value is
/// expected must collapse the same way in compiled and interpreted
/// execution: nothing yielded becomes null, one item becomes the item,
/// and more than one is the same structured failure. Iteration sources
/// keep every item. Each case asserts the compiled result against the
/// interpreter's result for the same source rather than against a
/// hand-written expectation.
/// </summary>
[Collection(ConsoleSerialCollection.Name)]
public sealed class CompiledPipelineValueTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public CompiledPipelineValueTests(ToshRuntimeFixture fixture)
    {
        _runtime = fixture.Runtime;
    }

    [Theory]
    [InlineData("var n = ([1, 2, 3] | count)\necho $n", "3")]
    [InlineData("var xs = [1, 2, 3]\nvar n = ($xs | count)\necho $n", "3")]
    [InlineData("var n = ([1, 2, 3] | count)\necho (type-of $n)", "System.Int32")]
    [InlineData("var n = ([1, 2, 3] | where { _ > 99 })\necho ($n == null ? \"NULL\" : \"NOTNULL\")", "NULL")]
    [InlineData("for x in ([1, 2, 3] | each { _ }) { echo $x }", "1\n2\n3")]
    public void Compiled_value_pipeline_matches_expected_collapse(string source, string expected)
    {
        var execution = CompileAndRun(source);

        Assert.Null(execution.Failure);
        Assert.Equal(expected, execution.Output);
    }

    [Fact]
    public void Single_item_value_pipeline_does_not_produce_a_list()
    {
        // The pre-fix emitter returned List<object> here, so the value
        // stringified as "System.Collections.Generic.List`1[System.Object]".
        var execution = CompileAndRun(
            """
            var xs = [1, 2, 3]
            var n = ($xs | count)
            echo $"{$n}"
            """);

        Assert.Null(execution.Failure);
        Assert.Equal("3", execution.Output);
        Assert.DoesNotContain("List", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_item_value_pipeline_fails_with_the_interpreter_diagnostic()
    {
        var execution = CompileAndRun("var n = ([1, 2, 3] | each { _ })");

        var diagnostic = Assert.IsType<ToshDiagnosticException>(execution.Failure);
        Assert.Equal(
            "tosh.runtime.subexpression_requires_single_value",
            diagnostic.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Interpreted_and_compiled_agree_on_the_single_value_rule()
    {
        const string source =
            """
            var xs = [10, 20, 30]
            var n = ($xs | count)
            echo $n
            """;

        var engine = new ToshEngine(_runtime);
        await engine.ExecuteToListAsync(source);
        Assert.True(engine.TryGetVariableValue("n", out var interpreted));

        var execution = CompileAndRun(source);
        Assert.Null(execution.Failure);

        Assert.Equal(3, interpreted);
        Assert.Equal(interpreted?.ToString(), execution.Output);
    }

    private CompiledExecution CompileAndRun(string source)
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse(source, "<compiled-pipeline-value-test>");
        Assert.True(
            parse.Diagnostics.Count == 0,
            $"parse errors: {string.Join(", ", parse.Diagnostics)}");

        var unit = Lowerer.Lower(parse, _runtime.Commands);
        var assemblyName = $"ToshCompiledPipelineValue_{Guid.NewGuid():N}";
        using var stream = new MemoryStream();
        var emit = BoundUnitEmitter.Emit(unit, assemblyName, stream);
        Assert.True(
            emit.IsClean,
            $"unexpected diagnostics: {string.Join(", ", emit.UnsupportedShapes)}");

        var assembly = Assembly.Load(stream.ToArray());
        var main = assembly
            .GetType($"{assemblyName}.Program")!
            .GetMethod("Main", BindingFlags.Public | BindingFlags.Static)!;

        var originalOut = Console.Out;
        var capture = new StringWriter();
        Exception? failure = null;
        try
        {
            Console.SetOut(capture);
            main.Invoke(null, new object?[] { Array.Empty<string>() });
        }
        catch (TargetInvocationException exception)
        {
            failure = exception.InnerException ?? exception;
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return new CompiledExecution(
            capture.ToString().ReplaceLineEndings("\n").TrimEnd('\n'),
            failure);
    }

    private sealed record CompiledExecution(string Output, Exception? Failure);
}
