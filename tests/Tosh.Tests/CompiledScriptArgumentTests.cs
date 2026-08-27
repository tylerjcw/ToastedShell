using System.Reflection;
using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A compiled script converts its arguments to their declared types — `TOAST-0042`.
/// </summary>
/// <remarks>
/// <para>
/// The compiled host converted `int`, `long`, `bool` and `string` by literal name and
/// returned everything else untouched, so a declared `double`, an enum or a refinement
/// alias arrived as the raw `string` the command line supplied.
/// </para>
/// <para>
/// The failure never mentions arguments. `examples/mandelbrot.tosh` declares
/// `arg frames: PosInt` and later divides by it, so the compiled program reported
/// "Operator operands 'System.Int32' and 'System.String' are not compatible" from a line of
/// arithmetic — while the interpreted program ran, because the interpreter converts through
/// the annotation machinery that resolves the alias and applies its `coerce` clause.
/// </para>
/// <para>
/// These run the compiled program with real `argv`, which the differential corpus cannot:
/// it invokes `Main` with an empty array, so no case there can reach argument binding.
/// </para>
/// </remarks>
[Collection(ConsoleSerialCollection.Name)]
public sealed class CompiledScriptArgumentTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public CompiledScriptArgumentTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    /// <summary>A declared type is honoured, whichever kind of type it is.</summary>
    [Theory]
    // The four the hand-written switch already covered, as controls.
    [InlineData("arg n: int = 1\necho $\"{($n + 1)}\"", "41", "42")]
    [InlineData("arg n: long = 1\necho $\"{($n + 1)}\"", "41", "42")]
    [InlineData("arg b: bool = false\necho $\"{$b}\"", "true", "true")]
    [InlineData("arg s: string = \"\"\necho $\"{$s}!\"", "hi", "hi!")]
    // And the ones that fell through untouched.
    [InlineData("arg d: double = 0.0\necho $\"{($d * 2)}\"", "1.5", "3")]
    [InlineData("arg d: decimal = 0.0\necho $\"{($d * 2)}\"", "1.5", "3.0")]
    // A refinement alias — the shape that made a real program fail.
    [InlineData("type PosInt = int where _ > 0\narg n: PosInt = 1\necho $\"{($n * 2)}\"", "21", "42")]
    // And one whose `coerce` clause has to run for the value to be valid at all.
    [InlineData("type PosInt = int where _ > 0 coerce (_ == 0 ? 1 : Math.abs(_))\n"
                + "arg n: PosInt = 1\necho $\"{($n * 2)}\"", "-21", "42")]
    public void A_declared_argument_type_is_converted(string source, string argument, string expected)
    {
        Assert.Equal(expected, RunCompiled(source, argument));
        Assert.Equal(expected, RunInterpreted(source, argument));
    }

    /// <remarks>
    /// Its own runtime, because `InvocationArguments` is what `arg` binds from and the
    /// fixture's runtime is shared with every other test in the collection.
    /// </remarks>
    private static string RunInterpreted(string source, string argument)
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.InvocationArguments = new object?[] { argument };
        var engine = new ToshEngine(runtime.Language);

        var results = engine
            .ExecuteToListAsync(source, "<args>", CancellationToken.None)
            .GetAwaiter().GetResult();

        // Rendered rather than `ToString`d, and compared against the compiled side's
        // captured stdout — the same reduction `DifferentialExecutionTests` uses, because
        // the two backends have no shared output surface. `echo` rather than `writeline`
        // for the same reason: `writeline` writes through a writer bound at initialisation,
        // which `Console.SetOut` does not reach.
        return string.Join("\n", results.Select(v => ToastRenderer.Render(v)?.Trim())).Trim();
    }

    private string RunCompiled(string source, string argument)
    {
        var engine = new ToshEngine(_runtime.Language);
        var parse = engine.Parse(source, "<args>");
        Assert.True(parse.Diagnostics.Count == 0, $"parse: {string.Join(", ", parse.Diagnostics)}");

        var unit = Lowerer.Lower(parse, _runtime.Commands);
        var assemblyName = $"ToshArgs_{Guid.NewGuid():N}";
        using var stream = new MemoryStream();
        var result = BoundUnitEmitter.Emit(unit, assemblyName, stream);
        Assert.True(result.IsClean, $"emit: {string.Join(", ", result.UnsupportedShapes)}");

        var main = Assembly.Load(stream.ToArray()).GetType($"{assemblyName}.Program")!
            .GetMethod("Main", BindingFlags.Public | BindingFlags.Static)!;

        var originalOut = Console.Out;
        var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            main.Invoke(null, new object?[] { new[] { argument } });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return capture.ToString().Replace("\r", "").Trim();
    }
}
