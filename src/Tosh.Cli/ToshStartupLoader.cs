using Tosh.Core;
using Tosh.Language;

namespace Tosh.Cli;

public static class ToshStartupLoader
{
    public static async Task LoadAsync(ToshEngine engine, string? configDirectory = null, CancellationToken cancellationToken = default)
    {
        await LoadAsync(engine, configDirectory, skipProfile: false, cancellationToken: cancellationToken);
    }

    public static async Task LoadAsync(
        ToshEngine engine,
        string? configDirectory,
        bool skipProfile,
        TextWriter? errorWriter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var root = configDirectory ?? ToshConfigDefaults.GetDefaultConfigDirectory();
        engine.Runtime.Config.Startup.ApplyRootDirectory(root);

        var configPath = engine.Runtime.Config.Startup.ResolvePath(engine.Runtime.Config.Startup.ConfigFilePath);

        // Config file errors are fatal — they control everything else.
        if (File.Exists(configPath))
        {
            await ExecuteStartupFileAsync(engine, configPath, cancellationToken);
        }

        // Profile and autoload errors are non-fatal — log and continue.
        foreach (var path in EnumerateStartupFiles(engine.Runtime.Config.Startup, includeConfigFile: false, includeProfile: !skipProfile))
        {
            try
            {
                await ExecuteStartupFileAsync(engine, path, cancellationToken);
            }
            catch (Exception exception)
            {
                var writer = errorWriter ?? Console.Error;
                await writer.WriteLineAsync($"tosh: error loading '{path}': {FormatStartupError(exception)}");
            }
        }
    }

    private static string FormatStartupError(Exception exception)
    {
        return exception switch
        {
            ToshDiagnosticException diagnostic => diagnostic.Diagnostics[0].Title,
            _ => exception.Message,
        };
    }

    public static IReadOnlyList<string> EnumerateStartupFiles(string? configDirectory = null)
    {
        var startup = new ToshStartupConfig(configDirectory ?? ToshConfigDefaults.GetDefaultConfigDirectory());
        return EnumerateStartupFiles(startup);
    }

    internal static IReadOnlyList<string> EnumerateStartupFiles(ToshStartupConfig startup, bool includeConfigFile = true, bool includeProfile = true)
    {
        ArgumentNullException.ThrowIfNull(startup);

        var files = new List<string>();
        var configPath = startup.ResolvePath(startup.ConfigFilePath);
        var profilePath = startup.ResolvePath(startup.ProfilePath);
        var autoloadDirectory = startup.ResolvePath(startup.AutoloadDirectory);

        if (includeConfigFile && File.Exists(configPath))
        {
            files.Add(configPath);
        }

        if (includeProfile && File.Exists(profilePath))
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
        return ToshConfigDefaults.GetDefaultConfigDirectory();
    }

    private static async Task ExecuteStartupFileAsync(ToshEngine engine, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = await File.ReadAllTextAsync(path, cancellationToken);
        await AsyncEnumerableExtensions.ToListAsync(engine.EvaluateAsync(source, path, cancellationToken), cancellationToken);
    }
}
