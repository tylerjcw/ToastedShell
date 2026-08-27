using System.Reflection;
using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

[Collection(ConsoleSerialCollection.Name)]
public sealed class CompiledDeferSemanticsTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public CompiledDeferSemanticsTests(ToshRuntimeFixture fixture)
    {
        _runtime = fixture.Runtime;
    }

    [Fact]
    public void Top_level_defer_runs_when_main_scope_exits()
    {
        var execution = CompileAndRun(
            """
            defer { System.Console.WriteLine("top-cleanup") }
            System.Console.WriteLine("top-body")
            """);

        Assert.Null(execution.Failure);
        Assert.Equal("top-body\ntop-cleanup", execution.Output);
    }

    [Fact]
    public void Function_root_defers_run_once_in_lifo_order()
    {
        var execution = CompileAndRun(
            """
            func run-cleanup() {
                defer { System.Console.WriteLine("cleanup-A") }
                defer { System.Console.WriteLine("cleanup-B") }
                System.Console.WriteLine("body")
            }
            run-cleanup
            """);

        Assert.Null(execution.Failure);
        Assert.Equal("body\ncleanup-B\ncleanup-A", execution.Output);
    }

    [Fact]
    public void Body_and_cleanup_failures_preserve_body_first_lifo_order()
    {
        var execution = CompileAndRun(
            """
            func explode() {
                defer { System.Console.WriteLine("cleanup-A"); throw "cleanup-A" }
                defer { System.Console.WriteLine("cleanup-B"); throw "cleanup-B" }
                throw "body"
            }
            explode
            """);

        Assert.Equal("cleanup-B\ncleanup-A", execution.Output);
        var aggregate = Assert.IsType<ToshDeferAggregateException>(execution.Failure);
        Assert.Equal("body", PayloadOf(aggregate.BodyFailure));
        Assert.Equal(
            ["cleanup-B", "cleanup-A"],
            aggregate.CleanupFailures.Select(PayloadOf).ToArray());
        Assert.Equal(
            ["body", "cleanup-B", "cleanup-A"],
            aggregate.Failures.Select(PayloadOf).ToArray());
    }

    [Fact]
    public void Cleanup_failure_overrides_return_but_all_cleanups_still_run()
    {
        var execution = CompileAndRun(
            """
            func fail-on-return() {
                defer { System.Console.WriteLine("cleanup-A"); throw "cleanup-A" }
                defer { System.Console.WriteLine("cleanup-B"); throw "cleanup-B" }
                return 42
            }
            echo (fail-on-return)
            """);

        Assert.Equal("cleanup-B\ncleanup-A", execution.Output);
        var aggregate = Assert.IsType<ToshDeferAggregateException>(execution.Failure);
        Assert.Null(aggregate.BodyFailure);
        Assert.Equal(
            ["cleanup-B", "cleanup-A"],
            aggregate.CleanupFailures.Select(PayloadOf).ToArray());
    }

    [Fact]
    public void Cleanup_failures_supersede_compiled_return_break_and_continue()
    {
        var returnExecution = CompileAndRun(
            """
            func fail-return() {
                defer { throw "cleanup-return" }
                return 1
            }
            fail-return
            """);
        var breakExecution = CompileAndRun(
            """
            func fail-break() {
                for i in (1..3) {
                    defer { throw "cleanup-break" }
                    break
                }
                System.Console.WriteLine("unreachable")
            }
            fail-break
            """);
        var continueExecution = CompileAndRun(
            """
            func fail-continue() {
                for i in (1..3) {
                    defer { throw "cleanup-continue" }
                    continue
                }
                System.Console.WriteLine("unreachable")
            }
            fail-continue
            """);

        Assert.Equal(
            "cleanup-return",
            PayloadOf(Assert.IsType<ThrowSignalException>(returnExecution.Failure)));
        Assert.Equal(
            "cleanup-break",
            PayloadOf(Assert.IsType<ThrowSignalException>(breakExecution.Failure)));
        Assert.Equal(
            "cleanup-continue",
            PayloadOf(Assert.IsType<ThrowSignalException>(continueExecution.Failure)));
        Assert.Equal(string.Empty, breakExecution.Output);
        Assert.Equal(string.Empty, continueExecution.Output);
    }

    [Fact]
    public void Sole_cleanup_failure_crosses_compiled_boundary_unchanged()
    {
        var execution = CompileAndRun(
            """
            func sole-cleanup-failure() {
                defer { throw "cleanup" }
            }
            sole-cleanup-failure
            """);

        var failure = Assert.IsType<ThrowSignalException>(execution.Failure);
        Assert.Equal("cleanup", failure.Value);
        Assert.Same(
            failure,
            Assert.Single(ToshDeferFailures.GetCleanupFailures(failure)));
    }

    [Fact]
    public void Compiled_defer_is_registered_only_when_execution_reaches_it()
    {
        var execution = CompileAndRun(
            """
            func reached-only() {
                defer { System.Console.WriteLine("reached") }
                throw "body"
                defer { System.Console.WriteLine("unreached") }
            }
            reached-only
            """);

        Assert.Equal("reached", execution.Output);
        var failure = Assert.IsType<ThrowSignalException>(execution.Failure);
        Assert.Equal("body", failure.Value);
        Assert.False(ToshDeferFailures.IsDeferFailure(failure));
    }

    [Fact]
    public void Return_crossing_root_and_nested_defer_regions_is_valid()
    {
        var root = CompileAndRun(
            """
            func root-return() {
                defer { System.Console.WriteLine("root-cleanup") }
                return 42
            }
            echo (root-return)
            """);
        var nested = CompileAndRun(
            """
            func nested-return() {
                if (true) {
                    defer { System.Console.WriteLine("nested-cleanup") }
                    return 84
                }
                return 0
            }
            echo (nested-return)
            """);

        Assert.Null(root.Failure);
        Assert.Equal("root-cleanup\n42", root.Output);
        Assert.Null(nested.Failure);
        Assert.Equal("nested-cleanup\n84", nested.Output);
    }

    [Fact]
    public void Break_and_continue_leaving_defer_scope_run_cleanup()
    {
        var execution = CompileAndRun(
            """
            for i in (1..3) {
                defer { System.Console.WriteLine($"cleanup-{$i}") }
                if ($i == 1) { continue }
                break
            }
            System.Console.WriteLine("after")
            """);

        Assert.Null(execution.Failure);
        Assert.Equal("cleanup-1\ncleanup-2\nafter", execution.Output);
    }

    [Fact]
    public void Cleanup_control_flow_is_suppressed_but_inner_loops_remain_local()
    {
        var execution = CompileAndRun(
            """
            func cleanup-control() {
                defer {
                    for j in (1..3) {
                        if ($j == 2) { break }
                        System.Console.WriteLine($"inner-{$j}")
                    }
                    System.Console.WriteLine("cleanup-return")
                    return 99
                    System.Console.WriteLine("unreachable")
                }
                return 7
            }
            echo (cleanup-control)
            """);

        Assert.Null(execution.Failure);
        Assert.Equal("inner-1\ncleanup-return\n7", execution.Output);
    }

    [Fact]
    public void Cleanup_local_break_and_continue_are_suppressed_in_compiled_code()
    {
        var execution = CompileAndRun(
            """
            func cleanup-jumps() {
                defer {
                    System.Console.WriteLine("cleanup-break")
                    break
                    System.Console.WriteLine("unreachable-break")
                }
                defer {
                    System.Console.WriteLine("cleanup-continue")
                    continue
                    System.Console.WriteLine("unreachable-continue")
                }
                return 7
            }
            echo (cleanup-jumps)
            """);

        Assert.Null(execution.Failure);
        Assert.Equal("cleanup-continue\ncleanup-break\n7", execution.Output);
    }

    [Fact]
    public void Nested_compiled_defer_failures_flatten_in_execution_order()
    {
        var execution = CompileAndRun(
            """
            func nested-failures() {
                defer { throw "oldest-cleanup" }
                defer {
                    defer { throw "nested-cleanup" }
                    throw "nested-body"
                }
                throw "outer-body"
            }
            nested-failures
            """);

        var aggregate = Assert.IsType<ToshDeferAggregateException>(execution.Failure);
        Assert.Equal("outer-body", PayloadOf(aggregate.BodyFailure));
        Assert.Equal(
            ["nested-body", "nested-cleanup", "oldest-cleanup"],
            aggregate.CleanupFailures.Select(PayloadOf).ToArray());
    }

    [Fact]
    public void Compiled_cancellation_retains_cleanup_failures_without_losing_identity()
    {
        var execution = CompileAndRun(
            """
            func cancel-with-cleanup() {
                defer { throw "cleanup" }
                throw (new System.OperationCanceledException("cancelled"))
            }
            cancel-with-cleanup
            """);

        var cancellation = Assert.IsType<OperationCanceledException>(execution.Failure);
        Assert.Contains("cancelled", cancellation.Message, StringComparison.Ordinal);
        var cleanup = Assert.Single(ToshDeferFailures.GetCleanupFailures(cancellation));
        Assert.Equal("cleanup", PayloadOf(cleanup));
    }

    [Fact]
    public void Module_and_class_method_roots_honor_direct_defer()
    {
        var module = CompileAndRun(
            """
            module CleanupModule {
                func run() {
                    defer { System.Console.WriteLine("module-cleanup") }
                    return 11
                }
            }
            echo (CleanupModule.run())
            """);
        var classMethod = CompileAndRun(
            """
            class CleanupClass {
                func run() {
                    defer { System.Console.WriteLine("method-cleanup") }
                    return 12
                }
            }
            var instance = new CleanupClass()
            echo ($instance.run())
            """);

        Assert.Null(module.Failure);
        Assert.Equal("module-cleanup\n11", module.Output);
        Assert.Null(classMethod.Failure);
        Assert.Equal("method-cleanup\n12", classMethod.Output);
    }

    [Fact]
    public void Constructor_and_lambda_roots_honor_direct_defer()
    {
        var constructor = CompileAndRun(
            """
            class CleanupCtor {
                CleanupCtor() {
                    defer { System.Console.WriteLine("ctor-cleanup") }
                    System.Console.WriteLine("ctor-body")
                }
            }
            var instance = new CleanupCtor()
            """);
        var lambda = CompileAndRun(
            """
            var cleanupLambda = func() {
                defer { System.Console.WriteLine("lambda-cleanup") }
                return 13
            }
            echo ($cleanupLambda())
            """);

        Assert.Null(constructor.Failure);
        Assert.Equal("ctor-body\nctor-cleanup", constructor.Output);
        Assert.Null(lambda.Failure);
        Assert.Equal("lambda-cleanup\n13", lambda.Output);
    }

    [Fact]
    public void Subcommand_body_root_honors_direct_defer()
    {
        var execution = CompileAndRun(
            """
            subcommand run {
                defer { System.Console.WriteLine("subcommand-cleanup") }
                System.Console.WriteLine("subcommand-body")
            }
            """,
            "run");

        Assert.Null(execution.Failure);
        Assert.Equal("subcommand-body\nsubcommand-cleanup", execution.Output);
    }

    private CompiledExecution CompileAndRun(string source, params string[] arguments)
    {
        var engine = new ToshEngine(_runtime.Language);
        var parse = engine.Parse(source, "<compiled-defer-test>");
        Assert.True(
            parse.Diagnostics.Count == 0,
            $"parse errors: {string.Join(", ", parse.Diagnostics)}");

        var unit = Lowerer.Lower(parse, _runtime.Commands);
        var assemblyName = $"ToshCompiledDefer_{Guid.NewGuid():N}";
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
            main.Invoke(null, new object?[] { arguments });
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

    private static string PayloadOf(Exception? exception)
        => exception switch
        {
            ThrowSignalException signal => signal.Value?.ToString() ?? string.Empty,
            null => string.Empty,
            _ => exception.Message,
        };

    private sealed record CompiledExecution(string Output, Exception? Failure);
}
