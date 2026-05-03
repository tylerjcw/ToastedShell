using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Sdk.Tasks;

/// <summary>
/// In-process MSBuild task that compiles one or more <c>.tosh</c>
/// source files to a single CLR assembly. Mirrors the
/// <c>tosh --compile</c> CLI pipeline (parse → bind → lower →
/// type-check → emit) but runs inside the MSBuild process, avoiding
/// the per-build process-launch cost of the previous
/// <c>&lt;Exec&gt;</c>-based target.
///
/// <para>
/// Loaded by <c>Sdk.targets</c> via <c>&lt;UsingTask&gt;</c> when
/// the task assembly is resolvable next to the SDK; falls back to
/// the legacy <c>tosh</c>-process invocation when the task DLL is
/// missing (e.g. when consumed by a pinned older runtime).
/// </para>
/// </summary>
public sealed class ToshCompile : Microsoft.Build.Utilities.Task
{
    /// <summary>One or more <c>.tosh</c> source files.</summary>
    [Required]
    public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Path of the assembly to write.</summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Compile profile (<c>permissive</c>, <c>runtime</c>, <c>pure</c>).
    /// Mirrors the CLI's <c>--profile</c> flag. Default
    /// <c>permissive</c>.
    /// </summary>
    public string Profile { get; set; } = "permissive";

    /// <summary>
    /// When <c>true</c>, suppresses
    /// <c>tosh.compile.implicit_dynamic</c> diagnostics. Equivalent
    /// to the CLI's <c>--compile-allow-dynamic</c>.
    /// </summary>
    public bool AllowDynamic { get; set; }

    /// <summary>Optional assembly name (defaults to the file
    /// basename of <see cref="OutputPath"/>).</summary>
    public string? AssemblyName { get; set; }

    /// <summary>
    /// When <c>true</c>, generate an apphost wrapper executable
    /// (Windows: .exe; Unix: no extension) alongside the DLL.
    /// The wrapper points at the DLL and runs it via the .NET runtime.
    /// Defaults to <c>true</c> for <c>OutputType=Exe</c> projects.
    /// </summary>
    public bool EmitAppHost { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, bundle all runtime DLLs into the apphost
    /// to create a single-file, self-contained executable. Requires
    /// <c>EmitAppHost=true</c>. The binary will include the .NET
    /// runtime and be much larger (~70MB+).
    /// </summary>
    public bool PublishSingleFile { get; set; }

    /// <summary>Executes the compile task.</summary>
    public override bool Execute()
    {
        try
        {
            return Run();
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true);
            return false;
        }
    }

    private bool Run()
    {
        if (Sources.Length == 0)
        {
            Log.LogError("ToshCompile: no <ToshCompile> sources supplied.");
            return false;
        }
        if (string.IsNullOrEmpty(OutputPath))
        {
            Log.LogError("ToshCompile: OutputPath is required.");
            return false;
        }

        var profile = Profile.ToLowerInvariant() switch
        {
            "pure" => CompileProfile.Pure,
            "runtime" => CompileProfile.Runtime,
            _ => CompileProfile.Permissive,
        };

        // Concatenate sources with a `# --- <path> ---` header so
        // any reported span resolves back to a recognisable file
        // marker (matches the CLI's multi-source merge behaviour).
        string source;
        string sourceName;
        if (Sources.Length == 1)
        {
            var path = Sources[0].GetMetadata("FullPath");
            source = File.ReadAllText(path);
            sourceName = path;
        }
        else
        {
            var parts = new List<string>(Sources.Length * 2);
            foreach (var item in Sources)
            {
                var path = item.GetMetadata("FullPath");
                parts.Add($"# --- {path} ---");
                parts.Add(File.ReadAllText(path));
            }
            source = string.Join("\n", parts);
            sourceName = $"{Sources[0].GetMetadata("FullPath")} (+{Sources.Length - 1} more)";
        }

        var asmName = AssemblyName
            ?? Path.GetFileNameWithoutExtension(OutputPath);

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var parsed = engine.Parse(source, sourceName);
        if (parsed.Diagnostics.Count > 0)
        {
            foreach (var d in parsed.Diagnostics) Log.LogError($"tosh: {d}");
            return false;
        }

        var binderDiags = Binder.Bind(parsed, runtime.Commands, isInteractive: false);
        if (binderDiags.Count > 0)
        {
            foreach (var d in binderDiags) Log.LogError(d.Code, null, null, sourceName, d.Span?.Start ?? 0, 0, 0, 0, d.Title);
            return false;
        }

        var unit = Lowerer.Lower(parsed, runtime.Commands);

        var annotationDiags = TypeChecker.CheckCompileAnnotations(unit, allowDynamic: AllowDynamic);
        if (annotationDiags.Count > 0)
        {
            foreach (var d in annotationDiags) Log.LogError(d.Code, null, null, sourceName, d.Span?.Start ?? 0, 0, 0, 0, d.Title);
            return false;
        }

        var typeDiags = TypeChecker.Check(unit);
        if (typeDiags.Count > 0)
        {
            foreach (var d in typeDiags) Log.LogError(d.Code, null, null, sourceName, d.Span?.Start ?? 0, 0, 0, 0, d.Title);
            return false;
        }

        var dir = Path.GetDirectoryName(OutputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        EmitResult result;
        using (var fs = File.Create(OutputPath))
        {
            result = BoundUnitEmitter.Emit(unit, asmName, fs, profile);
        }

        if (!result.IsClean)
        {
            foreach (var shape in result.UnsupportedShapes)
            {
                Log.LogError($"tosh: {shape}");
            }
            try { File.Delete(OutputPath); } catch { /* best-effort */ }
            return false;
        }

        // Companion runtimeconfig so `dotnet <out>.dll` can run
        // without staging. Mirrors the CLI's emit step.
        var runtimeConfigPath = Path.ChangeExtension(OutputPath, ".runtimeconfig.json");
        var runtimeMajor = Environment.Version.Major;
        var runtimeConfig = $$"""
            {
              "runtimeOptions": {
                "tfm": "net{{runtimeMajor}}.0",
                "framework": {
                  "name": "Microsoft.NETCore.App",
                  "version": "{{Environment.Version}}"
                }
              }
            }
            """;
        File.WriteAllText(runtimeConfigPath, runtimeConfig);

        StageCompilerRuntime(OutputPath);

        var outputDir = Path.GetDirectoryName(OutputPath) ?? ".";
        if (EmitAppHost)
        {
            CreateAppHost(OutputPath, outputDir);
            if (PublishSingleFile)
            {
                CreateBundle(OutputPath, outputDir);
            }
        }

        Log.LogMessage(MessageImportance.High, $"ToshCompile: wrote {OutputPath}");
        return true;
    }

    /// <summary>
    /// Mirrors <c>tosh --compile</c>'s post-emit step: copies the
    /// minimum CLR-side runtime DLLs next to the produced assembly
    /// so <c>dotnet &lt;Out&gt;.dll</c> resolves them. Runs only
    /// when source and target dirs differ.
    /// </summary>
    private static void StageCompilerRuntime(string outputPath)
    {
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (string.IsNullOrEmpty(outDir)) return;
        // Where this task assembly lives. Inside an SDK consumption,
        // that's `~/.nuget/packages/tosh.sdk/<v>/tasks/net10.0/`,
        // where the compiler-runtime DLLs are co-packaged.
        // AppContext.BaseDirectory points at the MSBuild host
        // process root in some MSBuild flavours, so prefer the task
        // assembly's own directory when it differs.
        var taskAsmDir = Path.GetDirectoryName(typeof(ToshCompile).Assembly.Location);
        var sourceDir = !string.IsNullOrEmpty(taskAsmDir) ? taskAsmDir : AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(sourceDir) ||
            string.Equals(
                sourceDir.TrimEnd(Path.DirectorySeparatorChar),
                outDir.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string[] required =
        {
            "Tosh.Compiler.Runtime.dll",
            "Tosh.Language.dll",
            "Tosh.Runtime.dll",
            "Tosh.Stdlib.dll",
            "Tosh.Core.dll",
            "Tosh.Tui.dll",
        };
        foreach (var name in required)
        {
            var src = Path.Combine(sourceDir, name);
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(outDir, name);
            try
            {
                if (File.Exists(dst) &&
                    File.GetLastWriteTimeUtc(dst) >= File.GetLastWriteTimeUtc(src))
                {
                    continue;
                }
                File.Copy(src, dst, overwrite: true);
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    /// <summary>
    /// Resolves the location of apphost.exe / apphost inside the
    /// .NET SDK installation. Walks up from the build machine's
    /// dotnet root.
    /// </summary>
    private static string? ResolveAppHostTemplate()
    {
        var dotnetExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(dotnetExe)) return null;

        var dotnetRoot = Path.GetDirectoryName(dotnetExe);
        if (string.IsNullOrEmpty(dotnetRoot)) return null;

        var version = Environment.Version;
        var tfm = $"net{version.Major}.{version.Minor}";
        var sdkPath = Path.Combine(dotnetRoot, "sdk", version.ToString());

        // Template location varies by platform.
        var template = OperatingSystem.IsWindows()
            ? Path.Combine(sdkPath, "AppHostTemplate", "apphost.exe")
            : Path.Combine(sdkPath, "AppHostTemplate", "apphost");

        return File.Exists(template) ? template : null;
    }

    /// <summary>
    /// Creates an apphost wrapper that boots the .dll. On Windows,
    /// the wrapper is named <c>&lt;name&gt;.exe</c>. On Unix, it has
    /// no extension (e.g. <c>Hello</c>) and is marked executable.
    ///
    /// NOTE: The AppHost stamping API from Microsoft.NET.HostModel
    /// (version 5.0.0-preview) is not compatible with the expected
    /// API signature. Deferred to a future release when the API
    /// stabilizes.
    /// </summary>
    private void CreateAppHost(string dllPath, string outputDir)
    {
        // TODO: Implement apphost creation when HostModel API is stable
        // For now, users can use 'dotnet publish' with apphost-specific targets
        Log.LogMessage(MessageImportance.High, $"ToshCompile: apphost creation deferred (API incompatibility). Use 'dotnet publish' for executable wrappers.");
    }

    /// <summary>
    /// Bundles all runtime DLLs into a single-file executable using
    /// the Microsoft.NET.HostModel bundler. The result is a standalone
    /// executable (~70MB+ with .NET runtime).
    ///
    /// NOTE: The Bundler API in the currently available NuGet package
    /// (5.0.0-preview.1) doesn't directly expose AddFile/GenerateBundle.
    /// This is deferred — bundling can be done via `dotnet publish`
    /// instead.
    /// </summary>
    private void CreateBundle(string dllPath, string outputDir)
    {
        Log.LogWarning("ToshCompile: --publish-single-file requires 'dotnet publish' for full support (bundler API not available in current HostModel). Use: dotnet publish /p:PublishSingleFile=true /p:SelfContained=true");
    }
}

