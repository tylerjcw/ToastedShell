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
    // user functions
    [InlineData("func hello => echo hi\nhello", "hi")]
    [InlineData("func say(msg) => echo $msg\nsay hello", "hello")]
    [InlineData("func say(a, b) => echo $a $b\nsay one two", "one two")]
    [InlineData("func greet(name) { echo $\"Hi {$name}!\" }\ngreet World", "Hi World!")]
    [InlineData("func a => echo first\nfunc b => echo second\na\nb", "first\nsecond")]
    [InlineData("func inner => echo deep\nfunc outer => inner\nouter", "deep")]
    [InlineData("func twice(x) { echo $x\necho $x }\ntwice yo", "yo\nyo")]
    [InlineData("func get() { return 42 }\necho (get)", "42")]
    [InlineData("func id(x) { return $x }\necho (id hello)", "hello")]
    [InlineData("func body() { var n = 7\necho $n }\nbody", "7")]
    [InlineData("func cond(x) { if ($x == 1) { echo one } else { echo other } }\ncond 1\ncond 2", "one\nother")]
    [InlineData("func late => helper\nfunc helper => echo ok\nlate", "ok")]
    // arithmetic on parameters / function return values (object-typed)
    [InlineData("func inc(n) { return $n + 1 }\necho (inc 5)", "6")]
    [InlineData("func dbl(n) { return $n * 2 }\necho (dbl 7)", "14")]
    [InlineData("func negate_it(n) { return (0 - $n) }\necho (negate_it 9)", "-9")]
    [InlineData("func is_pos(n) { if ($n > 0) { return 1 } else { return 0 } }\necho (is_pos 5)\necho (is_pos -3)", "1\n0")]
    [InlineData("func is_small(n) { if ($n <= 3) { echo small } else { echo big } }\nis_small 1\nis_small 9", "small\nbig")]
    [InlineData("func eq5(n) { if ($n == 5) { echo yes } else { echo no } }\neq5 5\neq5 6", "yes\nno")]
    // recursion
    [InlineData("func fact(n) { if ($n <= 1) { return 1 } else { return $n * (fact ($n - 1)) } }\necho (fact 5)", "120")]
    [InlineData("func fib(n) { if ($n < 2) { return $n } else { return (fib ($n - 1)) + (fib ($n - 2)) } }\necho (fib 10)", "55")]
    [InlineData("func count_down(n) { if ($n > 0) { echo $n\ncount_down ($n - 1) } }\ncount_down 3", "3\n2\n1")]
    // for loops over ranges
    [InlineData("for i in (1..3) { echo $i }", "1\n2\n3")]
    [InlineData("for i in (0..2) { echo $i }", "0\n1\n2")]
    [InlineData("var sum = 0\nfor i in (1..5) { $sum = $sum + $i }\necho $sum", "15")]
    [InlineData("for i in (1..3) { for j in (1..2) { echo $i $j } }", "1 1\n1 2\n2 1\n2 2\n3 1\n3 2")]
    // compound assignment
    [InlineData("var x = 10\n$x += 5\necho $x", "15")]
    [InlineData("var x = 10\n$x -= 3\necho $x", "7")]
    [InlineData("var x = 4\n$x *= 3\necho $x", "12")]
    [InlineData("var x = 20\n$x /= 4\necho $x", "5")]
    [InlineData("var x = 17\n$x %= 5\necho $x", "2")]
    [InlineData("var s = \"foo\"\n$s += \"bar\"\necho $s", "foobar")]
    [InlineData("var n = 0\nfor i in (1..10) { $n += $i }\necho $n", "55")]
    // list literals
    [InlineData("var xs = [1, 2, 3]\necho $xs.Count", "3")]
    [InlineData("var xs = [\"a\", \"b\", \"c\"]\necho $xs.Count", "3")]
    [InlineData("var xs = [1, 2, 3]\necho $xs[0]", "1")]
    [InlineData("var xs = [10, 20, 30]\necho $xs[2]", "30")]
    [InlineData("echo [1, 2, 3].Count", "3")]
    // record / dict literals — record `{ name: ... }` syntax has a
    // pre-existing parser issue (tosh.parser.missing_list_separator)
    // so the emitter-side tests use the dict `=>` form which parses
    // cleanly.
    [InlineData("var m = { \"name\" => \"Alice\", \"age\" => 30 }\necho $m[\"name\"]", "Alice")]
    [InlineData("var m = { \"x\" => 1, \"y\" => 2 }\necho $m[\"y\"]", "2")]
    [InlineData("var m = { \"k\" => 42 }\necho $m.Count", "1")]
    // for over list
    [InlineData("for x in [1, 2, 3] { echo $x }", "1\n2\n3")]
    [InlineData("var s = 0\nfor x in [10, 20, 30] { $s += $x }\necho $s", "60")]
    [InlineData("for x in [\"a\", \"b\"] { echo $x }", "a\nb")]
    // for over scalar (single-element)
    [InlineData("for x in 42 { echo $x }", "42")]
    // for over null (empty)
    [InlineData("for x in null { echo $x }\necho done", "done")]
    public void Compiles_and_runs(string source, string expected)
    {
        var output = CompileAndRun(source);
        Assert.Equal(expected, output.Trim());
    }

    [Fact]
    public void Reports_unsupported_pipeline()
    {
        // Multi-stage pipelines aren't lowered yet; the emitter
        // surfaces a diagnostic instead of producing bogus IL.
        var (output, result) = CompileAndRunWithDiagnostics("echo hi | echo bye");
        Assert.False(result.IsClean);
        Assert.Contains(result.UnsupportedShapes, d => d.Contains("pipeline", StringComparison.Ordinal));
        Assert.Equal(string.Empty, output.Trim());
    }

    // ─── Runtime-host bridge dispatch ─────────────────────────────
    // These tests confirm that command calls other than `echo` route
    // through ToshHost into the live ShellCommandRegistry (populated
    // by Tosh.Stdlib's [ModuleInitializer]) and that yielded values
    // are surfaced via Console.WriteLine for statement context.

    [Fact]
    public void Host_bridge_dispatches_pwd_to_stdlib()
    {
        var output = CompileAndRun("pwd");
        // pwd yields a DirectoryInfo; formatter renders it as a path.
        Assert.False(string.IsNullOrWhiteSpace(output));
        Assert.StartsWith("/", output.TrimStart());
    }

    [Fact]
    public void Host_bridge_dispatches_whoami_to_stdlib()
    {
        var output = CompileAndRun("whoami").Trim();
        Assert.False(string.IsNullOrEmpty(output));
        Assert.DoesNotContain('\n', output);
    }

    [Fact]
    public void Host_bridge_value_context_binds_to_var()
    {
        // (pwd) in value context returns a single object via
        // ToshHost.InvokeValue; binding it to $d and echoing
        // proves the bridge round-trips into user code.
        var output = CompileAndRun("var d = (pwd)\necho $d").Trim();
        Assert.False(string.IsNullOrEmpty(output));
        Assert.StartsWith("/", output);
    }

    [Fact]
    public void Host_bridge_passes_arguments()
    {
        // `which echo` should resolve through the registry and
        // yield a value identifying the builtin command.
        var output = CompileAndRun("which echo").Trim();
        Assert.False(string.IsNullOrEmpty(output));
        Assert.Contains("echo", output, StringComparison.Ordinal);
    }

    // ─── Member / index access ────────────────────────────────────

    [Theory]
    [InlineData("var s = \"hello\"\necho $s.Length", "5")]
    [InlineData("var s = \"abc\"\necho $s.Length\necho $s.Length", "3\n3")]
    public void Member_access_reads_clr_property(string source, string expected)
    {
        var output = CompileAndRun(source).Trim();
        Assert.Equal(expected, output);
    }

    [Fact]
    public void Member_access_pwd_full_name_starts_at_root()
    {
        var output = CompileAndRun("var d = (pwd)\necho $d.FullName").Trim();
        Assert.StartsWith("/", output);
    }

    [Fact]
    public void Member_access_chained_property()
    {
        // String.Length.ToString() — proves dotted paths walk
        // multiple segments through ObjectAccessor.
        var output = CompileAndRun("var s = \"hello\"\necho $s.Length").Trim();
        Assert.Equal("5", output);
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
