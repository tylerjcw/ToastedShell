using System.Diagnostics;
using Tosh.Runtime;
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
        bool profileStartup = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var runtime = engine.LanguageRuntime.CommandHost as ToshRuntime
            ?? throw new InvalidOperationException("The TōSh startup loader requires a TōSh command host.");

        var profile = profileStartup ? new StartupProfileData() : null;
        var fileProfiles = profileStartup ? new List<StartupFileProfile>() : null;
        var totalStopwatch = profileStartup ? Stopwatch.StartNew() : null;

        var root = configDirectory ?? ToshConfigDefaults.GetDefaultConfigDirectory();
        runtime.Config.Startup.ApplyRootDirectory(root);

        var configPath = runtime.Config.Startup.ResolvePath(runtime.Config.Startup.ConfigFilePath);

        // Config errors are non-fatal — log and continue so the shell remains usable.
        if (File.Exists(configPath))
        {
            var phaseStopwatch = profileStartup ? Stopwatch.StartNew() : null;
            try
            {
                await ExecuteStartupFileAsync(engine, configPath, cancellationToken);
            }
            catch (Exception exception)
            {
                var writer = errorWriter ?? Console.Error;
                await writer.WriteLineAsync($"tosh: error loading config '{configPath}': {FormatStartupError(exception)}");
                await writer.WriteLineAsync("tosh: running with default configuration. Fix the error above or run with --safe to skip startup.");
            }
            finally
            {
                if (phaseStopwatch is not null)
                {
                    phaseStopwatch.Stop();
                    profile!.Config = phaseStopwatch.Elapsed;
                    fileProfiles!.Add(new StartupFileProfile { Path = configPath, Duration = phaseStopwatch.Elapsed });
                }
            }
        }

        // Profile and autoload errors are non-fatal — log and continue.
        foreach (var path in EnumerateStartupFiles(runtime.Config.Startup, includeConfigFile: false, includeProfile: !skipProfile))
        {
            var isProfile = path.EndsWith("profile.tosh", StringComparison.OrdinalIgnoreCase);
            var fileStopwatch = profileStartup ? Stopwatch.StartNew() : null;

            try
            {
                await ExecuteStartupFileAsync(engine, path, cancellationToken);
            }
            catch (Exception exception)
            {
                var writer = errorWriter ?? Console.Error;
                await writer.WriteLineAsync($"tosh: error loading '{path}': {FormatStartupError(exception)}");
            }
            finally
            {
                if (fileStopwatch is not null)
                {
                    fileStopwatch.Stop();
                    fileProfiles!.Add(new StartupFileProfile { Path = path, Duration = fileStopwatch.Elapsed });

                    if (isProfile)
                    {
                        profile!.Profile = fileStopwatch.Elapsed;
                    }
                    else
                    {
                        profile!.Autoload += fileStopwatch.Elapsed;
                    }
                }
            }
        }

        if (totalStopwatch is not null)
        {
            totalStopwatch.Stop();
            profile!.Total = totalStopwatch.Elapsed;
            profile.Files = fileProfiles!;
            runtime.StartupProfile = profile;
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
