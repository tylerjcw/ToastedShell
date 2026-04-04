namespace Tosh.Core;

public static class ConfigStartupUtilities
{
    public static async Task<ConfigReloadResult> ReloadConfigurationAsync(
        ToshRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var evaluator = runtime.Evaluator
            ?? throw new InvalidOperationException("Configuration reload requires a live evaluator.");

        var preservedRootDirectory = runtime.Config.Startup.RootDirectory;

        runtime.Config.Reset();
        runtime.Config.Startup.ApplyRootDirectory(preservedRootDirectory);

        var loadedPaths = new List<string>();
        var configPath = runtime.Config.Startup.ResolvePath(runtime.Config.Startup.ConfigFilePath);

        if (File.Exists(configPath))
        {
            await ExecuteStartupFileAsync(evaluator, configPath, cancellationToken);
            loadedPaths.Add(configPath);
        }

        foreach (var path in EnumerateStartupFiles(runtime.Config.Startup, includeConfigFile: false))
        {
            await ExecuteStartupFileAsync(evaluator, path, cancellationToken);
            loadedPaths.Add(path);
        }

        if (runtime.HistoryStorageInitialized)
        {
            runtime.ReloadHistoryFromFile();
        }

        return new ConfigReloadResult(
            RootDirectory: runtime.Config.Startup.RootDirectory,
            ConfigFilePath: runtime.Config.Startup.ResolvePath(runtime.Config.Startup.ConfigFilePath),
            ProfileFilePath: runtime.Config.Startup.ResolvePath(runtime.Config.Startup.ProfilePath),
            AutoloadDirectory: runtime.Config.Startup.ResolvePath(runtime.Config.Startup.AutoloadDirectory),
            LoadedPaths: loadedPaths);
    }

    public static ConfigInitializationResult InitializeConfigDirectory(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        Directory.CreateDirectory(rootDirectory);

        var configPath = Path.Combine(rootDirectory, "config.tosh");
        var profilePath = Path.Combine(rootDirectory, "profile.tosh");
        var autoloadDirectory = Path.Combine(rootDirectory, "autoload");

        var createdPaths = new List<string>();

        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, GetDefaultConfigFileContents());
            createdPaths.Add(configPath);
        }

        if (!File.Exists(profilePath))
        {
            File.WriteAllText(profilePath, GetDefaultProfileFileContents());
            createdPaths.Add(profilePath);
        }

        if (!Directory.Exists(autoloadDirectory))
        {
            Directory.CreateDirectory(autoloadDirectory);
            createdPaths.Add(autoloadDirectory);
        }

        return new ConfigInitializationResult(
            RootDirectory: rootDirectory,
            ConfigFilePath: configPath,
            ProfileFilePath: profilePath,
            AutoloadDirectory: autoloadDirectory,
            CreatedPaths: createdPaths);
    }

    public static IReadOnlyList<string> EnumerateStartupFiles(ToshStartupConfig startup, bool includeConfigFile = true)
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

    public static async Task ExecuteStartupFileAsync(IShellEvaluator evaluator, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        cancellationToken.ThrowIfCancellationRequested();
        var source = await File.ReadAllTextAsync(path, cancellationToken);
        await AsyncEnumerableExtensions.ToListAsync(evaluator.EvaluateAsync(source, path, cancellationToken), cancellationToken);
    }

    public static string GetDefaultConfigFileContents()
    {
        return """
            # ToSh startup configuration
            # This file runs before profile.tosh and autoload modules.
            #
            # Uncomment and adjust the settings you want to keep for every session.
            #
            # $tosh.Config.Prompt.HeaderLeftLayout = "Time, Directory, Git"
            # $tosh.Config.Prompt.HeaderRightLayout = "UserHost, Jobs, Duration"
            # $tosh.Config.Prompt.PromptLeftLayout = "HistoryId, ExitCode, Name, Indicator"
            # $tosh.Config.Prompt.NameText = "toast"
            # $tosh.Config.Prompt.TimeEnabled = true
            # $tosh.Config.Prompt.TimeFormat = "HH:mm"
            # $tosh.Config.Prompt.IndicatorText = " >> "
            # $tosh.Config.Repl.ContinuationPrompt = "..> "
            # $tosh.Config.Repl.CompletionMaxVisible = 10
            # $tosh.Config.Display.Style = "Compact"
            # $tosh.Config.Display.DateTime.TableMode = "Relative"
            # $tosh.Config.History.MaxEntries = 5000
            # $tosh.Config.History.Deduplication = "Consecutive"
            """;
    }

    public static string GetDefaultProfileFileContents()
    {
        return """
            # ToSh profile
            # This file runs after config.tosh and before autoload modules.
            #
            # Example:
            # func ll => ls -la
            """;
    }
}
