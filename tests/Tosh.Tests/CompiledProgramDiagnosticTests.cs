using System.Diagnostics;
using System.Reflection;
using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// An exception escaping an emitted executable is presented as a Tōast diagnostic —
/// <c>TOAST-0042</c> — while an embedding caller still receives the exception itself.
/// </summary>
public sealed class CompiledProgramDiagnosticTests
{
    [Fact]
    public void Reflection_caller_receives_the_original_exception()
    {
        var (assembly, assemblyName) = EmitToMemory("echo (1 / 0)");
        var main = assembly.GetType($"{assemblyName}.Program")!
            .GetMethod("Main", BindingFlags.Public | BindingFlags.Static)!;

        var invocation = Assert.Throws<TargetInvocationException>(
            () => main.Invoke(null, [Array.Empty<string>()]));
        var diagnostic = Assert.IsType<ToshDiagnosticException>(invocation.InnerException);

        Assert.Equal("tosh.runtime.expression_failed", Assert.Single(diagnostic.Diagnostics).Code);
    }

    [Theory]
    [InlineData("echo (1 / 0)", new string[0], "tosh.runtime.expression_failed", "Division by zero.")]
    [InlineData("arg n: int = 1\necho $n", new[] { "1", "2" }, "tosh.runtime.error", "Unexpected argument '2'.")]
    public async Task Process_entrypoint_renders_an_uncaught_failure(
        string source,
        string[] arguments,
        string diagnosticCode,
        string title)
    {
        using var output = CompileExecutable(source);

        var process = await RunAsync(output.AssemblyPath, arguments);

        Assert.Equal(1, process.ExitCode);
        Assert.Empty(process.StandardOutput);
        Assert.Contains(diagnosticCode, process.StandardError, StringComparison.Ordinal);
        Assert.Contains(title, process.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", process.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("System.InvalidOperationException:", process.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Top_level_return_still_exits_successfully_through_the_boundary()
    {
        using var output = CompileExecutable("echo before\nreturn\necho after");

        var process = await RunAsync(output.AssemblyPath, Array.Empty<string>());

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("before", process.StandardOutput.Trim());
        Assert.Empty(process.StandardError);
    }

    private static (Assembly Assembly, string AssemblyName) EmitToMemory(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var parse = engine.Parse(source, "<compiled-boundary>");
        Assert.Empty(parse.Diagnostics);

        var unit = Lowerer.Lower(parse, runtime.Commands);
        var assemblyName = $"ToshBoundary_{Guid.NewGuid():N}";
        using var stream = new MemoryStream();
        var result = BoundUnitEmitter.Emit(unit, assemblyName, stream);
        Assert.True(result.IsClean, string.Join(Environment.NewLine, result.UnsupportedShapes));

        return (Assembly.Load(stream.ToArray()), assemblyName);
    }

    private static CompiledOutput CompileExecutable(string source)
    {
        var directory = Directory.CreateTempSubdirectory("tosh-compiled-boundary-");
        try
        {
            var runtime = ToshRuntime.CreateDefault();
            var engine = new ToshEngine(runtime);
            var parse = engine.Parse(source, "boundary.tosh");
            Assert.Empty(parse.Diagnostics);

            var unit = Lowerer.Lower(parse, runtime.Commands);
            var assemblyName = $"ToshBoundary_{Guid.NewGuid():N}";
            var assemblyPath = Path.Combine(directory.FullName, $"{assemblyName}.dll");
            using (var stream = File.Create(assemblyPath))
            {
                var result = BoundUnitEmitter.Emit(unit, assemblyName, stream);
                Assert.True(
                    result.IsClean,
                    string.Join(Environment.NewLine, result.UnsupportedShapes));
            }

            foreach (var dependency in ToshPublisher.GetRuntimeDependencyFileNames())
            {
                var sourcePath = Path.Combine(AppContext.BaseDirectory, dependency);
                Assert.True(File.Exists(sourcePath), $"Missing test runtime dependency: {sourcePath}");
                File.Copy(sourcePath, Path.Combine(directory.FullName, dependency));
            }

            var runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
            File.WriteAllText(
                runtimeConfigPath,
                $$"""
                  {
                    "runtimeOptions": {
                      "tfm": "net{{Environment.Version.Major}}.0",
                      "framework": {
                        "name": "Microsoft.NETCore.App",
                        "version": "{{Environment.Version}}"
                      }
                    }
                  }
                  """);
            ToshPublisher.WriteDepsJson(assemblyPath);

            return new CompiledOutput(directory, assemblyPath);
        }
        catch
        {
            directory.Delete(recursive: true);
            throw;
        }
    }

    private static async Task<ProcessResult> RunAsync(
        string assemblyPath,
        IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add(assemblyPath);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* best-effort test cleanup */ }
            throw new TimeoutException($"Compiled program timed out: {assemblyPath}");
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class CompiledOutput(DirectoryInfo directory, string assemblyPath) : IDisposable
    {
        public string AssemblyPath { get; } = assemblyPath;

        public void Dispose()
        {
            try { directory.Delete(recursive: true); }
            catch { /* best-effort test cleanup */ }
        }
    }
}
