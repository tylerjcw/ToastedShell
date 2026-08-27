using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.NET.HostModel.AppHost;
using Microsoft.NET.HostModel.Bundle;

namespace Tosh.Compiler;

public static class ToshPublisher
{
    private static readonly string[] RuntimeDependencyFileNames =
    [
        "Tosh.Compiler.IR.dll",
        "Tosh.Compiler.Runtime.dll",
        "Tosh.Language.dll",
        // `TOAST-0006`. The value model moved to its own assembly, so a compiled program
        // needs both halves: `Toast.Runtime` for the language, `Tosh.Runtime` for the shell
        // services it still reaches through the host.
        "Toast.Runtime.dll",
        "Tosh.Runtime.dll",
        "Tosh.Stdlib.dll",
        "Tosh.Tui.dll",
    ];

    public static string GetAppHostPath(string dllPath, string? outputDir = null)
    {
        var directory = outputDir ?? Path.GetDirectoryName(Path.GetFullPath(dllPath)) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(dllPath);
        var fileName = OperatingSystem.IsWindows() ? $"{baseName}.exe" : baseName;
        return Path.Combine(directory, fileName);
    }

    public static string CreateAppHost(string dllPath, string? outputDir = null)
    {
        dllPath = Path.GetFullPath(dllPath);
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException("Cannot create an apphost because the application DLL does not exist.", dllPath);
        }

        var appHostPath = GetAppHostPath(dllPath, outputDir);
        Directory.CreateDirectory(Path.GetDirectoryName(appHostPath) ?? ".");
        var appHostTemplate = ResolveAppHostTemplate()
            ?? throw new FileNotFoundException("Could not locate the .NET SDK apphost template. Set DOTNET_ROOT or install the .NET SDK.");

        HostWriter.CreateAppHost(
            appHostTemplate,
            appHostPath,
            Path.GetFileName(dllPath),
            windowsGraphicalUserInterface: false,
            assemblyToCopyResourcesFrom: dllPath);

        EnsureExecutable(appHostPath);
        return appHostPath;
    }

    public static string WriteDepsJson(
        string dllPath,
        IReadOnlyCollection<string>? runtimeDependencyFileNames = null)
    {
        dllPath = Path.GetFullPath(dllPath);
        var directory = Path.GetDirectoryName(dllPath) ?? ".";
        var appAssembly = AssemblyName.GetAssemblyName(dllPath);
        var appName = appAssembly.Name ?? Path.GetFileNameWithoutExtension(dllPath);
        var appVersion = "1.0.0";
        var targetName = $".NETCoreApp,Version=v{Environment.Version.Major}.0";
        var appKey = $"{appName}/{appVersion}";
        var dependencies = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var targetLibraries = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            [appKey] = new Dictionary<string, object>
            {
                ["runtime"] = new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    [Path.GetFileName(dllPath)] = new Dictionary<string, object>(),
                },
            },
        };
        var libraries = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            [appKey] = CreateLibraryEntry(),
        };

        var allowedDependencies = runtimeDependencyFileNames is null
            ? null
            : new HashSet<string>(runtimeDependencyFileNames, StringComparer.OrdinalIgnoreCase);
        var knownRuntimeDependencies = new HashSet<string>(
            RuntimeDependencyFileNames,
            StringComparer.OrdinalIgnoreCase);
        foreach (var dependencyPath in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                     .Where(p => !string.Equals(Path.GetFullPath(p), dllPath, StringComparison.OrdinalIgnoreCase))
                     .Where(p => !Path.GetFileName(p).EndsWith(".ref.dll", StringComparison.OrdinalIgnoreCase))
                     .Where(p => allowedDependencies is null
                         || !knownRuntimeDependencies.Contains(Path.GetFileName(p))
                         || allowedDependencies.Contains(Path.GetFileName(p)))
                     .OrderBy(static p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            // `TOSH-0008`. Not every `.dll` beside the application is a managed assembly. A
            // self-contained Windows publish ships native ones — `coreclr.dll`, `clrjit.dll`,
            // `Microsoft.DiaSymReader.Native.amd64.dll` — and reading an assembly name out of
            // one throws. That is not an error to report: a native library is not a managed
            // dependency to record in `.deps.json`, so it is skipped.
            //
            // Invisible on Linux, where the equivalents are `.so` and never matched the
            // `*.dll` enumeration above.
            AssemblyName dependencyAssembly;
            try
            {
                dependencyAssembly = AssemblyName.GetAssemblyName(dependencyPath);
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            var dependencyName = dependencyAssembly.Name ?? Path.GetFileNameWithoutExtension(dependencyPath);
            var dependencyVersion = ToDependencyVersion(dependencyAssembly.Version);
            var dependencyKey = $"{dependencyName}/{dependencyVersion}";
            dependencies[dependencyName] = dependencyVersion;
            targetLibraries[dependencyKey] = new Dictionary<string, object>
            {
                ["runtime"] = new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    [Path.GetFileName(dependencyPath)] = CreateRuntimeAssetEntry(dependencyPath, dependencyAssembly),
                },
            };
            libraries[dependencyKey] = CreateLibraryEntry();
        }

        if (dependencies.Count > 0)
        {
            ((Dictionary<string, object>)targetLibraries[appKey])["dependencies"] = dependencies;
        }

        var deps = new Dictionary<string, object>
        {
            ["runtimeTarget"] = new Dictionary<string, object>
            {
                ["name"] = targetName,
                ["signature"] = string.Empty,
            },
            ["compilationOptions"] = new Dictionary<string, object>(),
            ["targets"] = new Dictionary<string, object>
            {
                [targetName] = targetLibraries,
            },
            ["libraries"] = libraries,
        };

        var depsPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(dllPath)}.deps.json");
        File.WriteAllText(
            depsPath,
            JsonSerializer.Serialize(deps, new JsonSerializerOptions { WriteIndented = true }));
        return depsPath;
    }

    public static string CreateSingleFileBundle(
        string dllPath,
        string? outputDir = null,
        IReadOnlyCollection<string>? runtimeDependencyFileNames = null)
    {
        dllPath = Path.GetFullPath(dllPath);
        var appHostPath = File.Exists(GetAppHostPath(dllPath, outputDir))
            ? GetAppHostPath(dllPath, outputDir)
            : CreateAppHost(dllPath, outputDir);
        var appHostDirectory = Path.GetDirectoryName(Path.GetFullPath(appHostPath)) ?? ".";
        var hostName = Path.GetFileName(appHostPath);
        var dependenciesToBundle = runtimeDependencyFileNames ?? RuntimeDependencyFileNames;
        var stagingDir = Directory.CreateTempSubdirectory("tosh-bundle-stage-");
        var outputTempDir = Directory.CreateTempSubdirectory("tosh-bundle-out-");

        try
        {
            // Stage only the known output files (apphost + dll + deps + runtimeconfig)
            // into a clean temp directory so the bundler never sees unrelated files
            // in the output folder (sockets, pipes, etc. in /tmp cause ENXIO).
            foreach (var srcPath in Directory.EnumerateFiles(appHostDirectory, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(static p => Path.GetFileName(p), StringComparer.Ordinal))
            {
                var name = Path.GetFileName(srcPath);
                if (name.EndsWith(".ref.dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Only include files that belong to this output (same stem or hostName).
                var stem = Path.GetFileNameWithoutExtension(dllPath);
                if (!name.Equals(hostName, StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)
                    && !dependenciesToBundle.Contains(name))
                    continue;
                File.Copy(srcPath, Path.Combine(stagingDir.FullName, name), overwrite: true);
            }

            // Copy runtime dependencies that may live in the apphost directory.
            foreach (var name in dependenciesToBundle)
            {
                var src = Path.Combine(appHostDirectory, name);
                var dst = Path.Combine(stagingDir.FullName, name);
                if (File.Exists(src) && !File.Exists(dst))
                    File.Copy(src, dst);
            }

            var fileSpecs = EnumerateBundleFileSpecs(stagingDir.FullName, hostName).ToArray();
            var bundler = new Bundler(
                hostName,
                outputTempDir.FullName,
                BundleOptions.BundleAllContent,
                targetFrameworkVersion: Environment.Version,
                appAssemblyName: Path.GetFileNameWithoutExtension(dllPath));
            var bundlePath = bundler.GenerateBundle(fileSpecs);
            File.Copy(bundlePath, appHostPath, overwrite: true);
            EnsureExecutable(appHostPath);
            return appHostPath;
        }
        finally
        {
            TryDeleteDirectory(stagingDir.FullName);
            TryDeleteDirectory(outputTempDir.FullName);
        }
    }

    public static IReadOnlyList<string> GetRuntimeDependencyFileNames()
    {
        return RuntimeDependencyFileNames;
    }

    public static IReadOnlyList<string> GetRuntimeDependencyFileNames(CompileProfile profile)
    {
        return profile == CompileProfile.Pure
            ? ["Tosh.Runtime.dll"]
            : RuntimeDependencyFileNames;
    }

    private static IEnumerable<FileSpec> EnumerateBundleFileSpecs(string directory, string hostName)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(static p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.EndsWith(".ref.dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new FileSpec(path, fileName);
        }

        var hostPath = Path.Combine(directory, hostName);
        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException("Cannot create a single-file bundle because the apphost does not exist.", hostPath);
        }
    }

    private static string? ResolveAppHostTemplate()
    {
        var fileName = OperatingSystem.IsWindows() ? "apphost.exe" : "apphost";
        var explicitTemplate = Environment.GetEnvironmentVariable("TOSH_APPHOST_TEMPLATE");
        if (!string.IsNullOrWhiteSpace(explicitTemplate) && File.Exists(explicitTemplate))
        {
            return explicitTemplate;
        }

        foreach (var root in EnumerateDotNetRoots())
        {
            foreach (var candidate in EnumerateSdkAppHostTemplates(root, fileName))
            {
                return candidate;
            }

            foreach (var candidate in EnumeratePackAppHostTemplates(root, fileName))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSdkAppHostTemplates(string dotnetRoot, string fileName)
    {
        var sdkDirectory = Path.Combine(dotnetRoot, "sdk");
        if (!Directory.Exists(sdkDirectory)) yield break;

        foreach (var sdk in Directory.EnumerateDirectories(sdkDirectory)
                     .OrderByDescending(static p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(sdk, "AppHostTemplate", fileName);
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumeratePackAppHostTemplates(string dotnetRoot, string fileName)
    {
        var packsDirectory = Path.Combine(dotnetRoot, "packs");
        if (!Directory.Exists(packsDirectory)) yield break;

        foreach (var hostPack in Directory.EnumerateDirectories(packsDirectory, "Microsoft.NETCore.App.Host.*")
                     .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var versionDirectory in Directory.EnumerateDirectories(hostPack)
                         .OrderByDescending(static p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            {
                foreach (var candidate in Directory.EnumerateFiles(versionDirectory, fileName, SearchOption.AllDirectories)
                             .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateDotNetRoots()
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        foreach (var value in new[]
                 {
                     Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                     Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
                     Environment.GetEnvironmentVariable("DOTNET_ROOT_X86"),
                     GetDotNetRootFromCurrentProcess(),
                     GetDotNetRootFromPath(),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"),
                     "/usr/share/dotnet",
                     "/usr/local/share/dotnet",
                 })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var fullPath = Path.GetFullPath(value);
            if (Directory.Exists(fullPath) && seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }
    }

    private static string? GetDotNetRootFromCurrentProcess()
    {
        var processPath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(processPath)) return null;
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(processPath)
            : null;
    }

    private static string? GetDotNetRootFromPath()
    {
        var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(Path.GetFullPath(candidate));
            }
        }

        return null;
    }

    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        var mode = File.GetUnixFileMode(path);
        mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(path, mode);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of temporary bundle output.
        }
    }

    private static Dictionary<string, object> CreateLibraryEntry()
    {
        return new Dictionary<string, object>
        {
            ["type"] = "project",
            ["serviceable"] = false,
            ["sha512"] = string.Empty,
        };
    }

    private static Dictionary<string, object> CreateRuntimeAssetEntry(string path, AssemblyName assemblyName)
    {
        var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
        return new Dictionary<string, object>
        {
            ["assemblyVersion"] = assemblyName.Version?.ToString() ?? "0.0.0.0",
            ["fileVersion"] = string.IsNullOrWhiteSpace(fileVersion)
                ? assemblyName.Version?.ToString() ?? "0.0.0.0"
                : fileVersion,
        };
    }

    private static string ToDependencyVersion(Version? version)
    {
        if (version is null || version.Major == 0)
        {
            return "1.0.0";
        }

        return version.Revision >= 0
            ? version.ToString()
            : version.ToString(3);
    }
}
