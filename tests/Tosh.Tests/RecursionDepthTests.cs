using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class RecursionDepthTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(4);

    public static TheoryData<string, string> RecursiveInterpreterCases =>
        new()
        {
            {
                "direct function",
                """
                func recurse(n) {
                    return (recurse ($n + 1))
                }
                recurse 0
                """
            },
            {
                "mutual functions",
                """
                func left(n) {
                    return (right ($n + 1))
                }
                func right(n) {
                    return (left ($n + 1))
                }
                left 0
                """
            },
            {
                "recursive default parameter",
                """
                func recurse(value = (recurse)) {
                    return $value
                }
                recurse
                """
            },
            {
                "recursive lambda",
                """
                var recurse = func(n) {
                    return $recurse($n + 1)
                }
                $recurse(0)
                """
            },
            {
                "class method",
                """
                class Recursive {
                    func run(n) {
                        return ($this.run($n + 1))
                    }
                }
                var value = new Recursive()
                $value.run(0)
                """
            },
            {
                "constructor",
                """
                class Recursive {
                    Recursive(n) {
                        var next = new Recursive($n + 1)
                    }
                }
                var value = new Recursive(0)
                """
            },
            {
                "eval",
                """
                func recurse() {
                    eval "recurse"
                }
                recurse
                """
            },
        };

    [Theory]
    [MemberData(nameof(RecursiveInterpreterCases))]
    public async Task Recursive_interpreter_paths_fail_with_a_structured_diagnostic_and_recover(
        string scenario,
        string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Shell.MaxRecursionDepth = 8;
        var engine = new ToshEngine(runtime);

        var exception = await Assert
            .ThrowsAsync<ToshDiagnosticException>(
                () => engine.ExecuteToListAsync(source).WaitAsync(SafetyTimeout));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.runtime.recursion_limit_exceeded", diagnostic.Code);
        Assert.Equal("Maximum ToastScript recursion depth was exceeded.", diagnostic.Title);
        Assert.Contains("configured limit of 8", diagnostic.Label, StringComparison.Ordinal);
        Assert.Contains("Active ToastScript frames", diagnostic.Info, StringComparison.Ordinal);
        Assert.True(
            diagnostic.Info!.Contains("script <input>", StringComparison.OrdinalIgnoreCase),
            $"{scenario} did not retain its originating script frame: {diagnostic.Info}");
        Assert.Equal(0, ToshExecutionDepthGuard.CurrentDepth);

        var recovery = await engine.ExecuteToListAsync("echo 42");

        Assert.Equal([42], recovery);
        Assert.Equal(0, ToshExecutionDepthGuard.CurrentDepth);
    }

    [Fact]
    public async Task Source_recursion_is_guarded_and_the_engine_recovers()
    {
        var directory = Directory.CreateTempSubdirectory("tosh-recursion-");
        try
        {
            var path = Path.Combine(directory.FullName, "recursive.tosh");
            var escapedPath = EscapeToshString(path);
            await File.WriteAllTextAsync(path, $"source \"{escapedPath}\"");

            var runtime = ToshRuntime.CreateDefault();
            runtime.Config.Shell.MaxRecursionDepth = 6;
            var engine = new ToshEngine(runtime);

            var exception = await Assert
                .ThrowsAsync<ToshDiagnosticException>(
                    () => engine
                        .ExecuteToListAsync($"source \"{escapedPath}\"")
                        .WaitAsync(SafetyTimeout));

            Assert.Equal(
                "tosh.runtime.recursion_limit_exceeded",
                Assert.Single(exception.Diagnostics).Code);
            Assert.Equal(0, ToshExecutionDepthGuard.CurrentDepth);
            Assert.Equal([42], await engine.ExecuteToListAsync("echo 42"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Finite_recursion_below_the_configured_limit_succeeds()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Shell.MaxRecursionDepth = 16;
        var engine = new ToshEngine(runtime);

        var values = await engine.ExecuteToListAsync(
            """
            func descend(n) {
                if ($n == 0) {
                    return 42
                }
                return (descend ($n - 1))
            }
            descend 10
            """);

        Assert.Equal([42], values);
        Assert.Equal(0, ToshExecutionDepthGuard.CurrentDepth);
    }

    [Fact]
    public async Task Recursion_limit_is_configurable_from_toastscript()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            "$tosh.Config.Shell.MaxRecursionDepth = 5");

        Assert.Equal(5, runtime.Config.Shell.MaxRecursionDepth);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                func recurse() {
                    recurse
                }
                recurse
                """));

        Assert.Contains(
            "configured limit of 5",
            Assert.Single(exception.Diagnostics).Label,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pre_cancelled_execution_reports_cancellation_before_the_depth_limit()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Shell.MaxRecursionDepth = 1;
        var engine = new ToshEngine(runtime);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.ExecuteToListAsync(
                """
                func recurse() {
                    recurse
                }
                recurse
                """,
                cancellation.Token));

        Assert.Equal(0, ToshExecutionDepthGuard.CurrentDepth);
    }

    [Fact]
    public void Recursion_limit_has_a_safe_default_and_rejects_unsafe_values()
    {
        var runtime = ToshRuntime.CreateDefault();
        var shell = runtime.Config.Shell;

        Assert.Equal(ToshExecutionDepthGuard.DefaultMaximumDepth, shell.MaxRecursionDepth);

        Assert.Throws<ArgumentOutOfRangeException>(() => shell.MaxRecursionDepth = 0);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => shell.MaxRecursionDepth = ToshExecutionDepthGuard.MaximumSafeDepth + 1);

        Assert.Equal(ToshExecutionDepthGuard.DefaultMaximumDepth, shell.MaxRecursionDepth);
    }

    private static string EscapeToshString(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
