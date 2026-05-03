using System.Diagnostics;
using System.Security;

namespace Tosh.Tests;

public sealed class ToshSdkBuildTests
{
    [Fact]
    public async Task Dotnet_lifecycle_direct_import_project_builds_runs_publishes_and_cleans_outputs()
    {
        using var tempDirectory = new TemporaryDirectory("tosh sdk build tests");
        var projectRoot = GetProjectRoot();
        var propsPath = Path.Combine(projectRoot, "src", "Tosh.Sdk", "Sdk", "Sdk.props");
        var targetsPath = Path.Combine(projectRoot, "src", "Tosh.Sdk", "Sdk", "Sdk.targets");
        var cliPath = GetCliPath(projectRoot);
        var sourcePath = Path.Combine(tempDirectory.Path, "hello world.tosh");
        var projectPath = Path.Combine(tempDirectory.Path, "Hello.toshproj");
        var outputPath = Path.Combine(tempDirectory.Path, "bin", "Debug", "net10.0", "Hello.dll");
        var appHostPath = Path.Combine(tempDirectory.Path, "bin", "Debug", "net10.0", AppHostFileName("Hello"));
        var stagedRuntimePath = Path.Combine(tempDirectory.Path, "bin", "Debug", "net10.0", "Tosh.Compiler.Runtime.dll");
        var publishPath = Path.Combine(tempDirectory.Path, "bin", "Debug", "net10.0", "publish", "Hello.dll");
        var publishAppHostPath = Path.Combine(tempDirectory.Path, "bin", "Debug", "net10.0", "publish", AppHostFileName("Hello"));
        var publishRuntimePath = Path.Combine(tempDirectory.Path, "bin", "Debug", "net10.0", "publish", "Tosh.Compiler.Runtime.dll");

        await File.WriteAllTextAsync(
            sourcePath,
            """
            subcommand greet {
                arg name: string
                writeline $"Hello from {$name}"
            }
            """);
        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project>
              <Import Project="{{Xml(propsPath)}}" />
              <PropertyGroup>
                <AssemblyName>Hello</AssemblyName>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <Import Project="{{Xml(targetsPath)}}" />
            </Project>
            """);

        var build = await RunAsync(
            "dotnet",
            tempDirectory.Path,
            "build",
            projectPath,
            $"-p:ToshCompilerPath={cliPath}",
            "/v:minimal");

        Assert.Equal(0, build.ExitCode);
        Assert.Contains("ToshCompile (Exec): Hello", build.Output);
        Assert.True(File.Exists(outputPath), build.Output);
        Assert.True(File.Exists(appHostPath), build.Output);
        Assert.True(File.Exists(stagedRuntimePath), build.Output);

        var runDll = await RunAsync("dotnet", tempDirectory.Path, outputPath, "greet", "dotnet build");

        Assert.Equal(0, runDll.ExitCode);
        Assert.Contains("Hello from dotnet build", runDll.StandardOutput);

        var runAppHost = await RunAsync(appHostPath, tempDirectory.Path, "greet", "apphost");

        Assert.Equal(0, runAppHost.ExitCode);
        Assert.Contains("Hello from apphost", runAppHost.StandardOutput);

        var runProject = await RunAsync(
            "dotnet",
            tempDirectory.Path,
            "run",
            "--project",
            projectPath,
            $"-p:ToshCompilerPath={cliPath}",
            "--",
            "greet",
            "dotnet run");

        Assert.Equal(0, runProject.ExitCode);
        Assert.Contains("Hello from dotnet run", runProject.StandardOutput);

        var publish = await RunAsync(
            "dotnet",
            tempDirectory.Path,
            "publish",
            projectPath,
            $"-p:ToshCompilerPath={cliPath}",
            "/v:minimal");

        Assert.Equal(0, publish.ExitCode);
        Assert.True(File.Exists(publishPath), publish.Output);
        Assert.True(File.Exists(publishAppHostPath), publish.Output);
        Assert.True(File.Exists(publishRuntimePath), publish.Output);

        var runPublished = await RunAsync("dotnet", tempDirectory.Path, publishPath, "greet", "dotnet publish");

        Assert.Equal(0, runPublished.ExitCode);
        Assert.Contains("Hello from dotnet publish", runPublished.StandardOutput);

        var clean = await RunAsync(
            "dotnet",
            tempDirectory.Path,
            "clean",
            projectPath,
            $"-p:ToshCompilerPath={cliPath}",
            "/v:minimal");

        Assert.Equal(0, clean.ExitCode);
        Assert.False(File.Exists(outputPath));
        Assert.False(File.Exists(appHostPath));
        Assert.False(File.Exists(stagedRuntimePath));
    }

    [Fact]
    public async Task Dotnet_build_packaged_sdk_project_compiles_with_in_process_task()
    {
        using var tempDirectory = new TemporaryDirectory("tosh packaged sdk tests");
        using var packageDirectory = new TemporaryDirectory("tosh packaged sdk packages");
        using var globalPackagesDirectory = new TemporaryDirectory("tosh packaged sdk global packages");
        var projectRoot = GetProjectRoot();
        var sdkProjectPath = Path.Combine(projectRoot, "src", "Tosh.Sdk", "Tosh.Sdk.csproj");
        var projectPath = Path.Combine(tempDirectory.Path, "PackHello.toshproj");
        var sourcePath = Path.Combine(tempDirectory.Path, "main.tosh");
        var nugetConfigPath = Path.Combine(tempDirectory.Path, "NuGet.config");
        var outputPath = Path.Combine(tempDirectory.Path, "bin", "Debug", "net10.0", "PackHello.dll");
        var appHostPath = Path.Combine(tempDirectory.Path, "bin", "Debug", "net10.0", AppHostFileName("PackHello"));
        var isolatedDirectory = Path.Combine(tempDirectory.Path, "isolated");

        var pack = await RunAsync(
            "dotnet",
            projectRoot,
            "pack",
            sdkProjectPath,
            "-o",
            packageDirectory.Path,
            "/p:Configuration=Debug",
            "/v:minimal");

        Assert.Equal(0, pack.ExitCode);
        var packagePath = Directory.GetFiles(packageDirectory.Path, "Tosh.Sdk.*.nupkg").Single();
        var packageVersion = Path.GetFileNameWithoutExtension(packagePath)["Tosh.Sdk.".Length..];

        await File.WriteAllTextAsync(
            nugetConfigPath,
            $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{{Xml(packageDirectory.Path)}}" />
              </packageSources>
            </configuration>
            """);
        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Tosh.Sdk/{{packageVersion}}">
              <PropertyGroup>
                <AssemblyName>PackHello</AssemblyName>
                <TargetFramework>net10.0</TargetFramework>
                <ToshPublishSingleFile>true</ToshPublishSingleFile>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(sourcePath, "echo \"Hello from packaged Tosh.Sdk\"\n");

        var build = await RunWithEnvironmentAsync(
            "dotnet",
            tempDirectory.Path,
            new Dictionary<string, string>
            {
                ["NUGET_PACKAGES"] = globalPackagesDirectory.Path,
            },
            "build",
            projectPath,
            "/v:minimal");

        Assert.Equal(0, build.ExitCode);
        Assert.Contains("ToshCompile (in-process): PackHello", build.Output);
        Assert.True(File.Exists(outputPath), build.Output);
        Assert.True(File.Exists(appHostPath), build.Output);

        var run = await RunAsync("dotnet", tempDirectory.Path, outputPath);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Hello from packaged Tosh.Sdk", run.StandardOutput);

        Directory.CreateDirectory(isolatedDirectory);
        var isolatedAppHostPath = Path.Combine(isolatedDirectory, AppHostFileName("PackHello"));
        File.Copy(appHostPath, isolatedAppHostPath);

        var runSingleFile = await RunAsync(isolatedAppHostPath, isolatedDirectory);

        Assert.Equal(0, runSingleFile.ExitCode);
        Assert.Contains("Hello from packaged Tosh.Sdk", runSingleFile.StandardOutput);
    }

    [Fact]
    public async Task Dotnet_publish_single_file_project_outputs_isolated_executable()
    {
        using var tempDirectory = new TemporaryDirectory("tosh sdk single file tests");
        var projectRoot = GetProjectRoot();
        var propsPath = Path.Combine(projectRoot, "src", "Tosh.Sdk", "Sdk", "Sdk.props");
        var targetsPath = Path.Combine(projectRoot, "src", "Tosh.Sdk", "Sdk", "Sdk.targets");
        var cliPath = GetCliPath(projectRoot);
        var sourcePath = Path.Combine(tempDirectory.Path, "single.tosh");
        var projectPath = Path.Combine(tempDirectory.Path, "Single.toshproj");
        var publishPath = Path.Combine(tempDirectory.Path, "bin", "Debug", "net10.0", "publish", AppHostFileName("Single"));
        var isolatedDirectory = Path.Combine(tempDirectory.Path, "isolated");

        await File.WriteAllTextAsync(sourcePath, "echo \"Hello from single file publish\"\n");
        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project>
              <Import Project="{{Xml(propsPath)}}" />
              <PropertyGroup>
                <AssemblyName>Single</AssemblyName>
                <TargetFramework>net10.0</TargetFramework>
                <ToshPublishSingleFile>true</ToshPublishSingleFile>
              </PropertyGroup>
              <Import Project="{{Xml(targetsPath)}}" />
            </Project>
            """);

        var publish = await RunAsync(
            "dotnet",
            tempDirectory.Path,
            "publish",
            projectPath,
            $"-p:ToshCompilerPath={cliPath}",
            "/v:minimal");

        Assert.Equal(0, publish.ExitCode);
        Assert.True(File.Exists(publishPath), publish.Output);
        Assert.False(File.Exists(Path.ChangeExtension(publishPath, ".dll")), publish.Output);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(publishPath)!, "Tosh.Compiler.Runtime.dll")), publish.Output);

        Directory.CreateDirectory(isolatedDirectory);
        var isolatedAppHostPath = Path.Combine(isolatedDirectory, AppHostFileName("Single"));
        File.Copy(publishPath, isolatedAppHostPath);

        var run = await RunAsync(isolatedAppHostPath, isolatedDirectory);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Hello from single file publish", run.StandardOutput);
    }

    private static async Task<ProcessResult> RunAsync(string fileName, string workingDirectory, params string[] arguments)
    {
        return await RunWithEnvironmentAsync(
            fileName,
            workingDirectory,
            null,
            arguments);
    }

    private static async Task<ProcessResult> RunWithEnvironmentAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        process.StartInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup before surfacing the timeout.
            }

            throw new TimeoutException($"Command timed out: {fileName} {string.Join(" ", arguments)}");
        }

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string GetProjectRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }

    private static string GetCliPath(string projectRoot)
    {
        var cliName = OperatingSystem.IsWindows() ? "Tosh.Cli.exe" : "Tosh.Cli";
        return Path.Combine(projectRoot, "src", "Tosh.Cli", "bin", "Debug", "net10.0", cliName);
    }

    private static string AppHostFileName(string assemblyName)
    {
        return OperatingSystem.IsWindows() ? $"{assemblyName}.exe" : assemblyName;
    }

    private static string Xml(string value)
    {
        return SecurityElement.Escape(value) ?? value;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string Output => StandardOutput + StandardError;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix} {Guid.NewGuid():N}");
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
}
