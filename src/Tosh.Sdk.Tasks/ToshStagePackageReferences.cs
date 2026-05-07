using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Tosh.Sdk.Tasks;

/// <summary>
/// Stages NuGet package runtime assemblies from a restored
/// <c>project.assets.json</c> next to a compiled Tosh output.
/// </summary>
public sealed class ToshStagePackageReferences : Microsoft.Build.Utilities.Task
{
    [Required]
    public string AssetsFile { get; set; } = string.Empty;

    [Required]
    public string OutputDirectory { get; set; } = string.Empty;

    public string TargetFramework { get; set; } = string.Empty;

    public string RuntimeIdentifier { get; set; } = string.Empty;

    [Output]
    public ITaskItem[] CopiedFiles { get; private set; } = Array.Empty<ITaskItem>();

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
        if (string.IsNullOrWhiteSpace(AssetsFile))
        {
            Log.LogError("ToshStagePackageReferences: AssetsFile is required.");
            return false;
        }

        if (!File.Exists(AssetsFile))
        {
            Log.LogError($"ToshStagePackageReferences: assets file '{AssetsFile}' does not exist.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            Log.LogError("ToshStagePackageReferences: OutputDirectory is required.");
            return false;
        }

        Directory.CreateDirectory(OutputDirectory);

        using var document = JsonDocument.Parse(File.ReadAllText(AssetsFile));
        var root = document.RootElement;

        if (!root.TryGetProperty("targets", out var targets) ||
            !root.TryGetProperty("libraries", out var libraries) ||
            !root.TryGetProperty("packageFolders", out var packageFolders))
        {
            Log.LogError($"ToshStagePackageReferences: '{AssetsFile}' is not a NuGet project.assets.json file.");
            return false;
        }

        if (!TrySelectTarget(targets, TargetFramework, RuntimeIdentifier, out var targetName, out var target))
        {
            Log.LogError($"ToshStagePackageReferences: no target matching '{TargetFramework}' was found in '{AssetsFile}'.");
            return false;
        }

        var packageFolderPaths = packageFolders.EnumerateObject()
            .Select(static folder => folder.Name)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        var copied = new List<ITaskItem>();
        var seenDestinations = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var package in target.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
        {
            if (!IsPackageLibrary(package.Value))
            {
                continue;
            }

            if (!libraries.TryGetProperty(package.Name, out var library))
            {
                continue;
            }

            var packagePath = GetPackagePath(package.Name, library);
            foreach (var asset in EnumerateRuntimeAssets(package.Value, RuntimeIdentifier))
            {
                if (!asset.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    asset.EndsWith("/_._", StringComparison.Ordinal) ||
                    asset.EndsWith("\\_._", StringComparison.Ordinal))
                {
                    continue;
                }

                var sourcePath = ResolvePackageAsset(packageFolderPaths, packagePath, asset);
                if (sourcePath is null)
                {
                    Log.LogWarning($"ToshStagePackageReferences: package asset '{asset}' from '{package.Name}' was not found.");
                    continue;
                }

                var destinationPath = Path.Combine(OutputDirectory, Path.GetFileName(asset));
                if (!seenDestinations.Add(Path.GetFullPath(destinationPath)))
                {
                    continue;
                }

                CopyIfNeeded(sourcePath, destinationPath);
                copied.Add(new TaskItem(destinationPath));
                Log.LogMessage(MessageImportance.High, $"ToshStagePackageReferences: staged {Path.GetFileName(destinationPath)} from {package.Name}");
            }
        }

        CopiedFiles = copied.ToArray();
        Log.LogMessage(
            copied.Count == 0 ? MessageImportance.Low : MessageImportance.High,
            $"ToshStagePackageReferences: staged {copied.Count} package runtime assembly file(s) for {targetName}");
        return !Log.HasLoggedErrors;
    }

    private static bool TrySelectTarget(
        JsonElement targets,
        string targetFramework,
        string runtimeIdentifier,
        out string targetName,
        out JsonElement target)
    {
        targetName = string.Empty;
        target = default;

        var framework = targetFramework.Trim();
        var rid = runtimeIdentifier.Trim();
        if (!string.IsNullOrEmpty(framework) &&
            !string.IsNullOrEmpty(rid) &&
            targets.TryGetProperty($"{framework}/{rid}", out target))
        {
            targetName = $"{framework}/{rid}";
            return true;
        }

        if (!string.IsNullOrEmpty(framework) && targets.TryGetProperty(framework, out target))
        {
            targetName = framework;
            return true;
        }

        if (!string.IsNullOrEmpty(framework))
        {
            foreach (var candidate in targets.EnumerateObject())
            {
                if (candidate.Name.StartsWith(framework + "/", StringComparison.Ordinal))
                {
                    targetName = candidate.Name;
                    target = candidate.Value;
                    return true;
                }
            }
        }

        using var enumerator = targets.EnumerateObject();
        if (enumerator.MoveNext())
        {
            targetName = enumerator.Current.Name;
            target = enumerator.Current.Value;
            return true;
        }

        return false;
    }

    private static bool IsPackageLibrary(JsonElement library)
    {
        return library.TryGetProperty("type", out var type) &&
               string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPackagePath(string packageKey, JsonElement library)
    {
        if (library.TryGetProperty("path", out var path) &&
            !string.IsNullOrWhiteSpace(path.GetString()))
        {
            return path.GetString()!;
        }

        var slash = packageKey.IndexOf('/');
        return slash >= 0
            ? packageKey.ToLowerInvariant()
            : packageKey;
    }

    private static IEnumerable<string> EnumerateRuntimeAssets(JsonElement package, string runtimeIdentifier)
    {
        if (package.TryGetProperty("runtime", out var runtime))
        {
            foreach (var asset in runtime.EnumerateObject())
            {
                if (asset.Name.EndsWith("/_._", StringComparison.Ordinal) ||
                    asset.Name.EndsWith("\\_._", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return asset.Name;
            }
        }

        if (package.TryGetProperty("runtimeTargets", out var runtimeTargets))
        {
            foreach (var asset in runtimeTargets.EnumerateObject())
            {
                if (asset.Name.EndsWith("/_._", StringComparison.Ordinal) ||
                    asset.Name.EndsWith("\\_._", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(runtimeIdentifier) ||
                    MatchesRuntimeIdentifier(asset.Value, runtimeIdentifier))
                {
                    yield return asset.Name;
                }
            }
        }
    }

    private static bool MatchesRuntimeIdentifier(JsonElement asset, string runtimeIdentifier)
    {
        return asset.TryGetProperty("rid", out var rid) &&
               string.Equals(rid.GetString(), runtimeIdentifier, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolvePackageAsset(
        IReadOnlyList<string> packageFolders,
        string packagePath,
        string assetPath)
    {
        foreach (var packageFolder in packageFolders)
        {
            var candidate = Path.Combine(
                packageFolder,
                packagePath.Replace('/', Path.DirectorySeparatorChar),
                assetPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void CopyIfNeeded(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath) &&
            File.GetLastWriteTimeUtc(destinationPath) >= File.GetLastWriteTimeUtc(sourcePath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        File.Copy(sourcePath, destinationPath, overwrite: true);
    }
}
