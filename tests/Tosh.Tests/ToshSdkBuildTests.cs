using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
        var refOutputPath = Path.Combine(tempDirectory.Path, "bin", "Debug", "net10.0", "Hello.ref.dll");
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
                <ToshEmitReferenceAssembly>true</ToshEmitReferenceAssembly>
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
        Assert.True(File.Exists(refOutputPath), build.Output);
        Assert.True(HasReferenceAssemblyAttribute(refOutputPath), build.Output);
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
        Assert.False(File.Exists(refOutputPath));
        Assert.False(File.Exists(appHostPath));
        Assert.False(File.Exists(stagedRuntimePath));
    }

    [Fact]
    public async Task Dotnet_build_packaged_sdk_project_compiles_with_packaged_sdk()
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
        Assert.True(
            build.Output.Contains("ToshCompile (in-process): PackHello", StringComparison.Ordinal) ||
            build.Output.Contains("ToshCompile (Exec): PackHello", StringComparison.Ordinal),
            build.Output);
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
    public async Task Dotnet_build_packaged_sdk_project_restores_stages_runs_and_publishes_package_references()
    {
        using var tempDirectory = new TemporaryDirectory("tosh package reference tests");
        using var packageDirectory = new TemporaryDirectory("tosh package reference packages");
        using var globalPackagesDirectory = new TemporaryDirectory("tosh package reference global packages");
        var projectRoot = GetProjectRoot();
        var sdkProjectPath = Path.Combine(projectRoot, "src", "Tosh.Sdk", "Tosh.Sdk.csproj");
        var dependencyRoot = Path.Combine(tempDirectory.Path, "Dependency");
        var greeterRoot = Path.Combine(tempDirectory.Path, "Greeter");
        var appRoot = Path.Combine(tempDirectory.Path, "App");
        Directory.CreateDirectory(dependencyRoot);
        Directory.CreateDirectory(greeterRoot);
        Directory.CreateDirectory(appRoot);

        var dependencyProjectPath = Path.Combine(dependencyRoot, "Tosh.Test.Dependency.csproj");
        var dependencySourcePath = Path.Combine(dependencyRoot, "Words.cs");
        var greeterProjectPath = Path.Combine(greeterRoot, "Tosh.Test.Greeter.csproj");
        var greeterSourcePath = Path.Combine(greeterRoot, "Greeter.cs");
        var appProjectPath = Path.Combine(appRoot, "PackageApp.toshproj");
        var appSourcePath = Path.Combine(appRoot, "main.tosh");
        var nugetConfigPath = Path.Combine(appRoot, "NuGet.config");
        var outputDirectory = Path.Combine(appRoot, "bin", "Debug", "net10.0");
        var outputPath = Path.Combine(outputDirectory, "PackageApp.dll");
        var greeterOutputPath = Path.Combine(outputDirectory, "Tosh.Test.Greeter.dll");
        var dependencyOutputPath = Path.Combine(outputDirectory, "Tosh.Test.Dependency.dll");
        var depsPath = Path.Combine(outputDirectory, "PackageApp.deps.json");
        var publishDirectory = Path.Combine(outputDirectory, "publish");
        var publishedOutputPath = Path.Combine(publishDirectory, "PackageApp.dll");
        var publishedGreeterPath = Path.Combine(publishDirectory, "Tosh.Test.Greeter.dll");
        var publishedDependencyPath = Path.Combine(publishDirectory, "Tosh.Test.Dependency.dll");

        await File.WriteAllTextAsync(
            dependencyProjectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>Tosh.Test.Dependency</PackageId>
                <Version>1.0.0</Version>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            dependencySourcePath,
            """
            namespace Tosh.Test.Dependency;

            public static class Words
            {
                public static string Value() => "package reference dependency";
            }
            """);

        var packDependency = await RunAsync(
            "dotnet",
            dependencyRoot,
            "pack",
            dependencyProjectPath,
            "-o",
            packageDirectory.Path,
            "/v:minimal");

        Assert.True(packDependency.ExitCode == 0, packDependency.Output);

        await File.WriteAllTextAsync(
            Path.Combine(greeterRoot, "NuGet.config"),
            LocalOnlyNuGetConfig(packageDirectory.Path));
        await File.WriteAllTextAsync(
            greeterProjectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>Tosh.Test.Greeter</PackageId>
                <Version>1.0.0</Version>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Tosh.Test.Dependency" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            greeterSourcePath,
            """
            using Tosh.Test.Dependency;

            namespace Tosh.Test.Greeter;

            public static class Greeter
            {
                public static string Message() => "hello from " + Words.Value();
            }
            """);

        var packGreeter = await RunWithEnvironmentAsync(
            "dotnet",
            greeterRoot,
            new Dictionary<string, string>
            {
                ["NUGET_PACKAGES"] = globalPackagesDirectory.Path,
            },
            "pack",
            greeterProjectPath,
            "-o",
            packageDirectory.Path,
            "/v:minimal");

        Assert.True(packGreeter.ExitCode == 0, packGreeter.Output);

        var packSdk = await RunAsync(
            "dotnet",
            projectRoot,
            "pack",
            sdkProjectPath,
            "-o",
            packageDirectory.Path,
            "/p:Configuration=Debug",
            "/v:minimal");

        Assert.True(packSdk.ExitCode == 0, packSdk.Output);
        var sdkPackagePath = Directory.GetFiles(packageDirectory.Path, "Tosh.Sdk.*.nupkg").Single();
        var sdkPackageVersion = Path.GetFileNameWithoutExtension(sdkPackagePath)["Tosh.Sdk.".Length..];

        // The fallback folder that lets framework ref packs resolve could also
        // satisfy `Tosh.Sdk` itself, if a build of this exact version were ever
        // left in the machine cache — and `build.tosh all` does install its own
        // output there. The test would then pass against a stale SDK while
        // appearing to prove the freshly packed one works, which is worse than
        // failing. Cheap to rule out, and loud when it happens.
        AssertVersionIsNotInMachineCache("Tosh.Sdk", sdkPackageVersion);

        await File.WriteAllTextAsync(nugetConfigPath, LocalOnlyNuGetConfig(packageDirectory.Path));
        await File.WriteAllTextAsync(
            appProjectPath,
            $$"""
            <Project Sdk="Tosh.Sdk/{{sdkPackageVersion}}">
              <PropertyGroup>
                <AssemblyName>PackageApp</AssemblyName>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Tosh.Test.Greeter" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            appSourcePath,
            """
            load-assembly (System.IO.Path.Combine((System.AppContext.BaseDirectory), "Tosh.Test.Greeter.dll"))
            echo (call Tosh.Test.Greeter.Greeter Message)
            """);

        var environment = new Dictionary<string, string>
        {
            ["NUGET_PACKAGES"] = globalPackagesDirectory.Path,
        };
        var build = await RunWithEnvironmentAsync(
            "dotnet",
            appRoot,
            environment,
            "build",
            appProjectPath,
            "/v:minimal");

        Assert.True(build.ExitCode == 0, build.Output);
        Assert.Contains("ToshStagePackageReferences: staged Tosh.Test.Greeter.dll", build.Output);
        Assert.True(File.Exists(outputPath), build.Output);
        Assert.True(File.Exists(greeterOutputPath), build.Output);
        Assert.True(File.Exists(dependencyOutputPath), build.Output);
        Assert.True(File.Exists(depsPath), build.Output);
        var depsJson = await File.ReadAllTextAsync(depsPath);
        Assert.Contains("\"Tosh.Test.Greeter\"", depsJson);
        Assert.Contains("\"Tosh.Test.Dependency\"", depsJson);

        var run = await RunWithEnvironmentAsync(
            "dotnet",
            appRoot,
            environment,
            "run",
            "--project",
            appProjectPath,
            "--no-restore");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("hello from package reference dependency", run.StandardOutput);

        var publish = await RunWithEnvironmentAsync(
            "dotnet",
            appRoot,
            environment,
            "publish",
            appProjectPath,
            "--no-restore",
            "/v:minimal");

        Assert.True(publish.ExitCode == 0, publish.Output);
        Assert.True(File.Exists(publishedOutputPath), publish.Output);
        Assert.True(File.Exists(publishedGreeterPath), publish.Output);
        Assert.True(File.Exists(publishedDependencyPath), publish.Output);

        var runPublished = await RunAsync("dotnet", publishDirectory, publishedOutputPath);

        Assert.True(runPublished.ExitCode == 0, runPublished.Output);
        Assert.Contains("hello from package reference dependency", runPublished.StandardOutput);
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

    [Fact]
    public async Task Csharp_consumer_can_compile_against_tosh_refasm_and_run_against_implementation()
    {
        using var tempDirectory = new TemporaryDirectory("tosh csharp consumer tests");
        var projectRoot = GetProjectRoot();
        var propsPath = Path.Combine(projectRoot, "src", "Tosh.Sdk", "Sdk", "Sdk.props");
        var targetsPath = Path.Combine(projectRoot, "src", "Tosh.Sdk", "Sdk", "Sdk.targets");
        var cliPath = GetCliPath(projectRoot);
        var toshRoot = Path.Combine(tempDirectory.Path, "ToshLib");
        var csharpRoot = Path.Combine(tempDirectory.Path, "Consumer");
        Directory.CreateDirectory(toshRoot);
        Directory.CreateDirectory(csharpRoot);

        var toshSourcePath = Path.Combine(toshRoot, "library.tosh");
        var toshProjectPath = Path.Combine(toshRoot, "ToshLib.toshproj");
        var toshOutputDirectory = Path.Combine(toshRoot, "bin", "Debug", "net10.0");
        var toshImplementationPath = Path.Combine(toshOutputDirectory, "ToshLib.dll");
        var toshReferencePath = Path.Combine(toshOutputDirectory, "ToshLib.ref.dll");
        var consumerProjectPath = Path.Combine(csharpRoot, "Consumer.csproj");
        var consumerSourcePath = Path.Combine(csharpRoot, "Program.cs");
        var consumerOutputDirectory = Path.Combine(csharpRoot, "bin", "Debug", "net10.0");

        await File.WriteAllTextAsync(
            toshSourcePath,
            """
            func add(a: int, b: int) -> int { return $a + $b }
            func greet(name: string) -> string { return $"Hi {$name}!" }

            module MathBox {
                var seed = 10
                func plus_seed(n) { return $seed + $n }
            }

            class Point(x, y) {
                prop X = x
                prop Y = y
                func sum() { return $this.X + $this.Y }
            }

            record Pair(x, y)
            """);
        await File.WriteAllTextAsync(
            toshProjectPath,
            $$"""
            <Project>
              <Import Project="{{Xml(propsPath)}}" />
              <PropertyGroup>
                <AssemblyName>ToshLib</AssemblyName>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Library</OutputType>
                <ToshEmitAppHost>false</ToshEmitAppHost>
                <ToshEmitReferenceAssembly>true</ToshEmitReferenceAssembly>
              </PropertyGroup>
              <Import Project="{{Xml(targetsPath)}}" />
            </Project>
            """);

        var toshBuild = await RunAsync(
            "dotnet",
            toshRoot,
            "build",
            toshProjectPath,
            $"-p:ToshCompilerPath={cliPath}",
            "/v:minimal");

        Assert.True(toshBuild.ExitCode == 0, toshBuild.Output);
        Assert.True(File.Exists(toshImplementationPath), toshBuild.Output);
        Assert.True(File.Exists(toshReferencePath), toshBuild.Output);
        Assert.True(HasReferenceAssemblyAttribute(toshReferencePath), toshBuild.Output);

        await File.WriteAllTextAsync(
            consumerProjectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="ToshLib">
                  <HintPath>{{Xml(toshReferencePath)}}</HintPath>
                  <Private>false</Private>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            consumerSourcePath,
            """
            namespace ConsumerApp;

            using System.Reflection;
            using System.Runtime.Loader;

            internal static class Entry
            {
                public static int Main()
                {
                    AssemblyLoadContext.Default.Resolving += ResolveFromBaseDirectory;
                    return Run();
                }

                private static Assembly? ResolveFromBaseDirectory(AssemblyLoadContext context, AssemblyName assemblyName)
                {
                    var path = Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll");
                    return File.Exists(path)
                        ? context.LoadFromAssemblyPath(path)
                        : null;
                }

                private static int Run()
                {
                    var add = global::ToshLib.Program.add(20, 22);
                    var greet = global::ToshLib.Program.greet("Ada");
                    var module = global::ToshLib.MathBox.plus_seed(5);
                    var point = new global::ToshLib.Point(3, 4);
                    var pair = new global::ToshLib.Pair("left", "right");

                    Console.WriteLine($"{add}|{greet}|{module}|{point.X},{point.Y},{point.sum()}|{pair.x},{pair.y}");
                    return 0;
                }
            }
            """);

        var consumerBuild = await RunAsync(
            "dotnet",
            csharpRoot,
            "build",
            consumerProjectPath,
            "/v:minimal");

        Assert.True(consumerBuild.ExitCode == 0, consumerBuild.Output);
        foreach (var dll in Directory.GetFiles(toshOutputDirectory, "*.dll"))
        {
            var fileName = Path.GetFileName(dll);
            if (fileName.EndsWith(".ref.dll", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(dll, Path.Combine(consumerOutputDirectory, fileName), overwrite: true);
        }

        var run = await RunAsync("dotnet", csharpRoot, Path.Combine(consumerOutputDirectory, "Consumer.dll"));

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("42|Hi Ada!|15|3,4,7|left,right", run.StandardOutput);
    }

    [Fact]
    public async Task Dotnet_build_direct_import_project_supports_multi_source_and_project_reference()
    {
        using var tempDirectory = new TemporaryDirectory("tosh sdk project reference tests");
        var projectRoot = GetProjectRoot();
        var propsPath = Path.Combine(projectRoot, "src", "Tosh.Sdk", "Sdk", "Sdk.props");
        var targetsPath = Path.Combine(projectRoot, "src", "Tosh.Sdk", "Sdk", "Sdk.targets");
        var cliPath = GetCliPath(projectRoot);
        var sharedRoot = Path.Combine(tempDirectory.Path, "Shared");
        var appRoot = Path.Combine(tempDirectory.Path, "App");
        Directory.CreateDirectory(sharedRoot);
        Directory.CreateDirectory(appRoot);

        var sharedSourcePath = Path.Combine(sharedRoot, "shared.tosh");
        var sharedProjectPath = Path.Combine(sharedRoot, "Shared.toshproj");
        var appLibrarySourcePath = Path.Combine(appRoot, "library.tosh");
        var appMainSourcePath = Path.Combine(appRoot, "main.tosh");
        var appProjectPath = Path.Combine(appRoot, "App.toshproj");
        var appOutputDirectory = Path.Combine(appRoot, "bin", "Debug", "net10.0");
        var appOutputPath = Path.Combine(appOutputDirectory, "App.dll");
        var copiedSharedOutputPath = Path.Combine(appOutputDirectory, "Shared.dll");
        var appDepsPath = Path.Combine(appOutputDirectory, "App.deps.json");
        var publishDirectory = Path.Combine(appOutputDirectory, "publish");
        var publishedAppOutputPath = Path.Combine(publishDirectory, "App.dll");
        var publishedSharedOutputPath = Path.Combine(publishDirectory, "Shared.dll");

        await File.WriteAllTextAsync(
            sharedSourcePath,
            """
            func meaning() -> int { return 42 }
            """);
        await File.WriteAllTextAsync(
            sharedProjectPath,
            $$"""
            <Project>
              <Import Project="{{Xml(propsPath)}}" />
              <PropertyGroup>
                <AssemblyName>Shared</AssemblyName>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Library</OutputType>
                <ToshEmitAppHost>false</ToshEmitAppHost>
              </PropertyGroup>
              <Import Project="{{Xml(targetsPath)}}" />
            </Project>
            """);
        await File.WriteAllTextAsync(
            appLibrarySourcePath,
            """
            func message() -> string { return "hello from multi-source project reference" }
            """);
        await File.WriteAllTextAsync(
            appMainSourcePath,
            """
            load-assembly (System.IO.Path.Combine((System.AppContext.BaseDirectory), "Shared.dll"))
            echo (message)
            """);
        await File.WriteAllTextAsync(
            appProjectPath,
            $$"""
            <Project>
              <Import Project="{{Xml(propsPath)}}" />
              <PropertyGroup>
                <AssemblyName>App</AssemblyName>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultToshItems>false</EnableDefaultToshItems>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{{Xml(sharedProjectPath)}}" />
                <ToshCompile Include="{{Xml(appLibrarySourcePath)}}" />
                <ToshCompile Include="{{Xml(appMainSourcePath)}}" />
              </ItemGroup>
              <Import Project="{{Xml(targetsPath)}}" />
            </Project>
            """);

        var build = await RunAsync(
            "dotnet",
            appRoot,
            "build",
            appProjectPath,
            $"-p:ToshCompilerPath={cliPath}",
            "/v:minimal");

        Assert.True(build.ExitCode == 0, build.Output);
        Assert.True(File.Exists(appOutputPath), build.Output);
        Assert.True(File.Exists(copiedSharedOutputPath), build.Output);
        Assert.True(File.Exists(appDepsPath), build.Output);
        var depsJson = await File.ReadAllTextAsync(appDepsPath);
        Assert.Contains("\"Shared\"", depsJson);

        var run = await RunAsync("dotnet", appOutputDirectory, appOutputPath);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("Shared", run.StandardOutput);
        Assert.Contains("hello from multi-source project reference", run.StandardOutput);

        var runProject = await RunAsync(
            "dotnet",
            appRoot,
            "run",
            "--project",
            appProjectPath,
            $"-p:ToshCompilerPath={cliPath}",
            "--no-restore");

        Assert.True(runProject.ExitCode == 0, runProject.Output);
        Assert.Contains("Shared", runProject.StandardOutput);
        Assert.Contains("hello from multi-source project reference", runProject.StandardOutput);

        var publish = await RunAsync(
            "dotnet",
            appRoot,
            "publish",
            appProjectPath,
            $"-p:ToshCompilerPath={cliPath}",
            "--no-restore",
            "/v:minimal");

        Assert.True(publish.ExitCode == 0, publish.Output);
        Assert.True(File.Exists(publishedAppOutputPath), publish.Output);
        Assert.True(File.Exists(publishedSharedOutputPath), publish.Output);

        var runPublished = await RunAsync("dotnet", publishDirectory, publishedAppOutputPath);

        Assert.True(runPublished.ExitCode == 0, runPublished.Output);
        Assert.Contains("Shared", runPublished.StandardOutput);
        Assert.Contains("hello from multi-source project reference", runPublished.StandardOutput);
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

    /// <summary>
    /// A NuGet configuration whose only <em>source</em> is the test's own package
    /// directory, with the machine's package cache as a read-only fallback.
    /// </summary>
    /// <remarks>
    /// The single source is the point of the test: it proves the Tosh SDK really
    /// flows package references, because nothing else could have supplied them.
    ///
    /// The fallback folder is not a weakening of that. .NET 10 resolves framework
    /// reference packs — <c>Microsoft.AspNetCore.App.Ref</c> and friends — during
    /// <em>restore</em>, for every <c>net10.0</c> project, whether or not it uses
    /// them. These tests also redirect <c>NUGET_PACKAGES</c> to an empty directory
    /// so a stale 1.0.0 from a previous run cannot be mistaken for a fresh one, and
    /// the two together left restore with nowhere to find framework data: a bare
    /// project with no package references at all fails the same way. A fallback
    /// folder is read-only and never receives writes, so the isolation that
    /// matters — the test's own packages, and their versions — is untouched.
    /// </remarks>
    private static string LocalOnlyNuGetConfig(string packageDirectory)
    {
        return $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="local" value="{{Xml(packageDirectory)}}" />
          </packageSources>
          <fallbackPackageFolders>
            <clear />
            <add key="machine" value="{{Xml(MachinePackageCache())}}" />
          </fallbackPackageFolders>
        </configuration>
        """;
    }

    /// <summary>
    /// Fails if the machine cache already holds <paramref name="packageId"/> at
    /// <paramref name="version"/>, which would let the fallback folder answer for
    /// a package this test means to supply itself.
    /// </summary>
    private static void AssertVersionIsNotInMachineCache(string packageId, string version)
    {
        var cached = Path.Combine(MachinePackageCache(), packageId.ToLowerInvariant(), version);

        Assert.False(
            Directory.Exists(cached),
            $"'{packageId}' {version} is already in the machine package cache ({cached}). " +
            "The fallback folder would satisfy the restore from there, so this test would " +
            "exercise that copy rather than the one it just packed. Remove that directory, " +
            "or bump the version under test.");
    }

    /// <summary>
    /// The machine's real NuGet package cache, which the tests read framework
    /// reference packs from without writing to it.
    /// </summary>
    private static string MachinePackageCache()
    {
        var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");

        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages")
            : configured;
    }

    private static bool HasReferenceAssemblyAttribute(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (GetAttributeTypeName(reader, attribute.Constructor) ==
                "System.Runtime.CompilerServices.ReferenceAssemblyAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetAttributeTypeName(MetadataReader reader, EntityHandle constructor)
    {
        return constructor.Kind switch
        {
            HandleKind.MemberReference => GetTypeName(
                reader,
                reader.GetMemberReference((MemberReferenceHandle)constructor).Parent),
            HandleKind.MethodDefinition => GetTypeName(
                reader,
                reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
            _ => null,
        };
    }

    private static string? GetTypeName(MetadataReader reader, EntityHandle typeHandle)
    {
        return typeHandle.Kind switch
        {
            HandleKind.TypeReference => FullName(reader, reader.GetTypeReference((TypeReferenceHandle)typeHandle)),
            HandleKind.TypeDefinition => FullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle)),
            _ => null,
        };
    }

    private static string FullName(MetadataReader reader, TypeReference type)
    {
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private static string FullName(MetadataReader reader, TypeDefinition type)
    {
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
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
