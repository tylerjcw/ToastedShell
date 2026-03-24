using Tosh.Core;
using Tosh.Language;

namespace Tosh.Cli;

public static class ToshStartupLoader
{
    public static async Task LoadAsync(ToshEngine engine, string? configDirectory = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);

        foreach (var path in EnumerateStartupFiles(configDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = await File.ReadAllTextAsync(path, cancellationToken);
            await AsyncEnumerableExtensions.ToListAsync(engine.EvaluateAsync(source, path, cancellationToken), cancellationToken);
        }
    }

    public static IReadOnlyList<string> EnumerateStartupFiles(string? configDirectory = null)
    {
        var root = configDirectory ?? GetDefaultConfigDirectory();
        var files = new List<string>();
        var profilePath = Path.Combine(root, "profile.tosh");
        var autoloadDirectory = Path.Combine(root, "autoload");

        if (File.Exists(profilePath))
        {
            files.Add(profilePath);
        }

        if (Directory.Exists(autoloadDirectory))
        {
            files.AddRange(
                Directory
                    .EnumerateFiles(autoloadDirectory, "*.tosh", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        }

        return files;
    }

    public static string GetDefaultConfigDirectory()
    {
        return Path.Combine(PathUtilities.UserHomeDirectory, ".config", "tosh");
    }
}
