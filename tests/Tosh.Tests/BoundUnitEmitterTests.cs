using System.Reflection;
using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// End-to-end tests for the IL emitter: lowers a tosh source string,
/// emits a .NET assembly into a <see cref="MemoryStream"/>, loads it
/// in-process via <see cref="Assembly.Load(byte[])"/>, redirects
/// stdout, and invokes the entry point.
/// </summary>
public sealed class BoundUnitEmitterTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public BoundUnitEmitterTests(ToshRuntimeFixture fixture)
    {
        _runtime = fixture.Runtime;
    }

    [Theory]
    [InlineData("echo 42", "42")]
    [InlineData("echo hello", "hello")]
    [InlineData("echo true", "True")]
    [InlineData("echo \"hi\" \"world\"", "hi world")]
    [InlineData("echo 1 2 3", "1 2 3")]
    [InlineData("var x = 42\necho $x", "42")]
    [InlineData("var x = 5\nvar y = 10\necho ($x + $y)", "15")]
    [InlineData("echo (3 * 7)", "21")]
    [InlineData("echo (10 - 4)", "6")]
    [InlineData("echo (20 / 5)", "4")]
    [InlineData("echo (10 % 3)", "1")]
    [InlineData("echo (1 + 2 + 3)", "6")]
    [InlineData("echo (-7)", "-7")]
    [InlineData("echo (1 == 1)", "True")]
    [InlineData("echo (1 < 2)", "True")]
    [InlineData("echo (\"a\" + \"b\")", "ab")]
    [InlineData("echo $\"plain text\"", "plain text")]
    [InlineData("var n = 7\necho $\"value={$n}\"", "value=7")]
    [InlineData("var n = 7\necho $\"x={$n + 1}\"", "x=8")]
    [InlineData("var a = 3\nvar b = 4\necho $\"sum={$a + $b}\"", "sum=7")]
    // if / else
    [InlineData("if (1 < 2) { echo yes } else { echo no }", "yes")]
    [InlineData("if (1 > 2) { echo yes } else { echo no }", "no")]
    [InlineData("var x = 5\nif ($x == 5) { echo five }", "five")]
    [InlineData("if (false) { echo a }", "")]
    // while
    [InlineData("var i = 0\nwhile ($i < 3) { echo $i\n$i = $i + 1 }", "0\n1\n2")]
    [InlineData("var i = 3\nwhile ($i > 0) { $i = $i - 1 }\necho $i", "0")]
    public void Compiles_and_runs(string source, string expected)
    {
        var output = CompileAndRun(source);
        Assert.Equal(expected, output.Trim());
    }

    [Fact]
    public void Reports_unsupported_command()
    {
        var (output, result) = CompileAndRunWithDiagnostics("ls");
        Assert.False(result.IsClean);
        Assert.Contains(result.UnsupportedShapes, d => d.Contains("ls", StringComparison.Ordinal));
        // The emitted method still ran (with no echo), so output is empty.
        Assert.Equal(string.Empty, output.Trim());
    }

    private string CompileAndRun(string source)
    {
        var (output, result) = CompileAndRunWithDiagnostics(source);
        Assert.True(result.IsClean,
            $"unexpected diagnostics: {string.Join(", ", result.UnsupportedShapes)}");
        return output;
    }

    private (string Output, EmitResult Result) CompileAndRunWithDiagnostics(string source)
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse(source, "<emit-test>");
        Assert.True(parse.Diagnostics.Count == 0,
            $"parse errors: {string.Join(", ", parse.Diagnostics)}");

        var unit = Lowerer.Lower(parse, _runtime.Commands);

        // Use a unique assembly name to avoid AssemblyLoadContext
        // caching between test cases.
        var assemblyName = $"ToshTest_{Guid.NewGuid():N}";
        using var stream = new MemoryStream();
        var result = BoundUnitEmitter.Emit(unit, assemblyName, stream);

        // Capture stdout while invoking.
        var asm = Assembly.Load(stream.ToArray());
        var program = asm.GetType($"{assemblyName}.Program");
        Assert.NotNull(program);
        var main = program!.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(main);

        var originalOut = Console.Out;
        var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            main!.Invoke(null, new object?[] { Array.Empty<string>() });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return (capture.ToString(), result);
    }
}
