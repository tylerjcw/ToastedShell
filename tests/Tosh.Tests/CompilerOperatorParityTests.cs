using System.Reflection;
using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

[Collection(ConsoleSerialCollection.Name)]
public sealed class CompilerOperatorParityTests
{
    public static TheoryData<string, string, string> ValueCases =>
        new()
        {
            {
                "integer addition",
                "var left = 20\nvar right = 22",
                "$left + $right"
            },
            {
                "mixed numeric promotion",
                "var left = 2\nvar right = 0.5",
                "$left + $right"
            },
            {
                "integral overflow promotion",
                "var left = 2147483647\nvar right = 1",
                "$left + $right"
            },
            {
                "integral overflow promotion after reassignment",
                "var value = 2147483647\n$value = ($value + 1)",
                "$value"
            },
            {
                "explicit long annotation",
                "var value: long = 42",
                "$value + 1"
            },
            {
                "annotated null-coalescing conversion",
                "var value: int\n$value ??= \"5\"",
                "$value"
            },
            {
                "integer division",
                "var left = 7\nvar right = 2",
                "$left / $right"
            },
            {
                "decimal arithmetic",
                "var left = System.Decimal.Parse(\"1.25\")\nvar right = System.Decimal.Parse(\"2.50\")",
                "$left + $right"
            },
            {
                "left string concatenation",
                "var left = \"item-\"\nvar right = 12",
                "$left + $right"
            },
            {
                "right string concatenation",
                "var left = 12\nvar right = \"-items\"",
                "$left + $right"
            },
            {
                "left string repetition",
                "var text = \"ha\"\nvar count = 3",
                "$text * $count"
            },
            {
                "right string repetition",
                "var count = 3\nvar text = \"ha\"",
                "$count * $text"
            },
            {
                "collection concatenation",
                "var left = [1, 2]\nvar right = [3, 4]",
                "$left + $right"
            },
            {
                "structural collection equality",
                "var left = [1, 2]\nvar right = [1, 2]",
                "$left == $right"
            },
            {
                "mixed string numeric equality",
                "var left = 1\nvar right = \"1\"",
                "$left == $right"
            },
            {
                "string ordering",
                "var left = \"beta\"\nvar right = \"alpha\"",
                "$left > $right"
            },
            {
                "temporal arithmetic",
                "var instant = System.DateTimeOffset.Parse(\"2026-07-25T12:00:00Z\")\nvar span = System.TimeSpan.FromDays(1)",
                "$instant + $span"
            },
            {
                "storage arithmetic",
                "var left = 10kb\nvar right = 2kb",
                "$left + $right"
            },
        };

    public static TheoryData<string, string, string> FailureCases =>
        new()
        {
            {
                "division by zero",
                "var left = 10\nvar right = 0",
                "$left / $right"
            },
            {
                "incompatible subtraction",
                "var left = 1\nvar right = true",
                "$left - $right"
            },
            {
                "null addition",
                "var left = null\nvar right = 1",
                "$left + $right"
            },
            {
                "annotated overflow assignment",
                "var value: int = 0\n$value = (2147483647 + 1)",
                "$value"
            },
            {
                "compound incompatible subtraction",
                "var value = 1\n$value -= true",
                "$value"
            },
        };

    public static TheoryData<string, string> CompoundAssignmentCases =>
        new()
        {
            {
                "all compound arithmetic operators",
                """
                func probe() {
                    var add = 10
                    $add += 5
                    var subtract = 10
                    $subtract -= 3
                    var multiply = 4
                    $multiply *= 3
                    var power = 2
                    $power **= 8
                    var divide = 20
                    $divide /= 4
                    var floorDivide = 7
                    $floorDivide //= 2
                    var modulo = 17
                    $modulo %= 5
                    return $"{$add},{$subtract},{$multiply},{$power},{$divide},{$floorDivide},{$modulo}"
                }
                echo (probe)
                """
            },
            {
                "compound dynamic overflow promotion",
                """
                func probe() {
                    var value = 2147483647
                    $value += 1
                    return $value
                }
                echo (probe)
                """
            },
            {
                "compound polymorphic runtime values",
                """
                func probe() {
                    var text = "toast"
                    $text += 42
                    var storage = 10kb
                    $storage += 2kb
                    return $"{$text}:{$storage.Bytes}"
                }
                echo (probe)
                """
            },
            {
                "compound annotated conversion",
                """
                func probe() {
                    var value: int = 1
                    $value += "2"
                    return $value
                }
                echo (probe)
                """
            },
            {
                "compound captured annotated conversion",
                """
                var value: int = 1
                func probe() {
                    $value += "2"
                    return $value
                }
                echo (probe)
                """
            },
            {
                "compound member and index targets",
                """
                class Box {
                    prop Value = 2
                }
                func probe() {
                    var box = new Box()
                    $box.Value **= 3
                    var values = {% "item" => 7 %}
                    $values["item"] //= 2
                    return ($box.Value * 10 + $values["item"])
                }
                echo (probe)
                """
            },
            {
                "compound left class overload changes binding type",
                """
                class Offset(value: int) {
                    prop Value: int = value
                    func +(other) { return $this.Value - $other }
                }
                func probe() {
                    var value = new Offset(50)
                    $value += 8
                    return $value
                }
                echo (probe)
                """
            },
            {
                "compound right class overload fallback",
                """
                class Offset(value: int) {
                    prop Value: int = value
                    func +(other) { return $this.Value - $other }
                }
                func probe() {
                    var value = 3
                    var offset = new Offset(10)
                    $value += $offset
                    return $value
                }
                echo (probe)
                """
            },
        };

    [Theory]
    [MemberData(nameof(ValueCases))]
    public async Task Compiled_operators_match_interpreter_value_type_value_and_stdout(
        string label,
        string setup,
        string expression)
    {
        var source = BuildProbe(setup, expression, echoResult: true);
        await AssertValueParityAsync(label, source);
    }

    [Theory]
    [InlineData(
        "left class overload",
        """
        class Box(value: int) {
            prop Value: int = value
            func +(other) { return $this.Value - $other.Value }
        }
        func probe() {
            var left = new Box(50)
            var right = new Box(8)
            return ($left + $right)
        }
        echo (probe)
        """)]
    [InlineData(
        "right class overload",
        """
        class Offset(value: int) {
            prop Value: int = value
            func +(other) { return $this.Value - $other }
        }
        func probe() {
            var left = 3
            var right = new Offset(10)
            return ($left + $right)
        }
        echo (probe)
        """)]
    [InlineData(
        "source-replayed class overload",
        """
        class DeferredBox(value: int) {
            prop Value: int = value
            lazy prop Deferred = 1
            func +(other) { return $this.Value - $other.Value }
        }
        func probe() {
            var left = new DeferredBox(50)
            var right = new DeferredBox(8)
            return ($left + $right)
        }
        echo (probe)
        """)]
    [InlineData(
        "inherited class overload",
        """
        class BaseOffset(value: int) {
            prop Value: int = value
            func +(other) { return $this.Value - $other }
        }
        class ChildOffset(value: int) extends BaseOffset($value) {
        }
        func probe() {
            var left = new ChildOffset(50)
            return ($left + 8)
        }
        echo (probe)
        """)]
    [InlineData(
        "operator throw preserves catch payload",
        """
        class Exploder {
            func +(other) { throw "boom" }
        }
        func probe() {
            try {
                var value = new Exploder()
                return ($value + 1)
            } catch (error) {
                return $error
            }
        }
        echo (probe)
        """)]
    [InlineData(
        "class ToString concatenation",
        """
        class Label(value: string) {
            prop Value: string = value
            shy func ToString() -> string { return $this.Value }
        }
        func probe() {
            var value = new Label("toast")
            return ("pre-" + $value)
        }
        echo (probe)
        """)]
    [InlineData(
        "class Equals protocol",
        """
        class Token(value: int) {
            prop Value: int = value
            func Equals(other) { return ($this.Value == $other.Value) }
        }
        func probe() {
            var left = new Token(5)
            var right = new Token(5)
            return ($left == $right)
        }
        echo (probe)
        """)]
    [InlineData(
        "record structural equality",
        """
        record Pair(left, right)
        func probe() {
            var left = new Pair(1, 2)
            var right = new Pair(1, 2)
            return ($left == $right)
        }
        echo (probe)
        """)]
    [InlineData(
        "base constructor argument arithmetic",
        """
        class Root(value: int) {
            prop Value: int = value
        }
        class Child(value: int) extends Root($value + 1) {
        }
        func probe() {
            var child = new Child(41)
            return $child.Value
        }
        echo (probe)
        """)]
    public async Task Compiled_class_operator_dispatch_matches_interpreter(
        string label,
        string source)
    {
        await AssertValueParityAsync(label, source);
    }

    [Theory]
    [MemberData(nameof(CompoundAssignmentCases))]
    public async Task Compiled_compound_assignments_match_interpreter_value_type_value_and_stdout(
        string label,
        string source)
    {
        await AssertValueParityAsync(label, source);
    }

    [Fact]
    public async Task Compiled_captured_annotation_conversion_matches_interpreter()
    {
        const string source =
            """
            var captured: long = 42
            func probe() {
                return ($captured + 1)
            }
            echo (probe)
            """;

        await AssertValueParityAsync("captured long annotation", source);
    }

    private static async Task AssertValueParityAsync(string label, string source)
    {
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);
        var engine = new ToshEngine(runtime);
        var interpretedValues = await engine.ExecuteToListAsync(source, label);
        var interpreted = Assert.Single(interpretedValues);

        // Use a separately emitted assembly for the typed return probe. Its
        // Main initializes declarations but does not call probe, so stateful
        // cases still execute the operation exactly once.
        var valueProgram = Compile(
            RemoveTerminalProbeEcho(source),
            label + " (value)");
        valueProgram.Main.Invoke(null, [Array.Empty<string>()]);
        var emitted = valueProgram.Probe.Invoke(null, Array.Empty<object?>());

        var outputProgram = Compile(source, label + " (stdout)");
        var originalOut = Console.Out;
        var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            outputProgram.Main.Invoke(null, [Array.Empty<string>()]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(interpreted?.GetType(), emitted?.GetType());
        Assert.True(
            OperatorEvaluator.AreEqual(interpreted, emitted),
            $"{label}: interpreted '{FormatValue(interpreted)}' but emitted '{FormatValue(emitted)}'.");
        // Against the *renderer*, not the shell's formatter. `TOAST-0014` separated the
        // two: `runtime.Formatter` applies display profiles, so it renders a StorageSize
        // as `12 kB` and a DateTimeOffset in ISO — presentation a compiled program with no
        // shell has no access to and should not be held to. What a compiled program prints
        // must equal what the *language* renders, which is what this now asserts.
        Assert.Equal(
            ToastRenderer.Render(interpreted) + Environment.NewLine,
            output.ToString());
    }

    private static string RemoveTerminalProbeEcho(string source)
    {
        const string terminalCall = "echo (probe)";
        var trimmed = source.TrimEnd();
        Assert.EndsWith(terminalCall, trimmed, StringComparison.Ordinal);
        return trimmed[..^terminalCall.Length].TrimEnd() + Environment.NewLine;
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task Compiled_operator_failures_match_interpreter_exception_type_and_message(
        string label,
        string setup,
        string expression)
    {
        var source = BuildProbe(setup, expression, echoResult: false) + "probe";
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);
        var engine = new ToshEngine(runtime);
        var interpreted = await Record.ExceptionAsync(
            () => engine.ExecuteToListAsync(source, label));

        var compiled = Compile(source, label);
        var emitted = Record.Exception(
            () => compiled.Probe.Invoke(null, Array.Empty<object?>()));

        var interpretedRoot = Unwrap(interpreted);
        var emittedRoot = Unwrap(emitted);
        Assert.Equal(interpretedRoot.GetType(), emittedRoot.GetType());
        Assert.Equal(interpretedRoot.Message, emittedRoot.Message);
        Assert.Equal(
            Assert.IsType<ToshDiagnosticException>(interpretedRoot).Diagnostics,
            Assert.IsType<ToshDiagnosticException>(emittedRoot).Diagnostics);
    }

    private static string BuildProbe(string setup, string expression, bool echoResult)
    {
        var indentedSetup = setup.Replace(
            Environment.NewLine,
            Environment.NewLine + "    ",
            StringComparison.Ordinal);
        var source =
            "func probe() {\n"
            + $"    {indentedSetup}\n"
            + $"    return ({expression})\n"
            + "}\n";
        return echoResult ? source + "echo (probe)" : source;
    }

    private static (MethodInfo Main, MethodInfo Probe) Compile(string source, string label)
    {
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);
        var engine = new ToshEngine(runtime);
        var parse = engine.Parse(source, label);
        Assert.Empty(parse.Diagnostics);

        var unit = Lowerer.Lower(parse, runtime.Commands);
        var assemblyName = $"OperatorParity_{Guid.NewGuid():N}";
        using var stream = new MemoryStream();
        var result = BoundUnitEmitter.Emit(unit, assemblyName, stream);
        Assert.True(
            result.IsClean,
            $"{label}: unexpected emit diagnostics: {string.Join(", ", result.UnsupportedShapes)}");

        var assembly = Assembly.Load(stream.ToArray());
        var program = Assert.Single(assembly.GetTypes(), type => type.Name == "Program");
        var main = Assert.IsAssignableFrom<MethodInfo>(
            program.GetMethod("Main", BindingFlags.Public | BindingFlags.Static));
        var probe = Assert.IsAssignableFrom<MethodInfo>(
            program.GetMethod("Func_probe", BindingFlags.Public | BindingFlags.Static));
        return (main, probe);
    }

    private static Exception Unwrap(Exception? exception)
    {
        Assert.NotNull(exception);
        while (exception is TargetInvocationException { InnerException: not null } invocation)
        {
            exception = invocation.InnerException;
        }

        return exception!;
    }

    private static string FormatValue(object? value) =>
        value is null
            ? "<null>"
            : $"{value} ({value.GetType().FullName})";
}
