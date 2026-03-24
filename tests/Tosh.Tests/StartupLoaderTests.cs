using Tosh.Cli;
using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class StartupLoaderTests
{
    [Fact]
    public void Startup_loader_enumerates_profile_before_sorted_autoload_files()
    {
        using var tempDirectory = new TemporaryDirectory();
        var configDirectory = tempDirectory.Path;
        var autoloadDirectory = System.IO.Path.Combine(configDirectory, "autoload");
        Directory.CreateDirectory(autoloadDirectory);

        var profilePath = System.IO.Path.Combine(configDirectory, "profile.tosh");
        var betaPath = System.IO.Path.Combine(autoloadDirectory, "20-beta.tosh");
        var alphaPath = System.IO.Path.Combine(autoloadDirectory, "10-alpha.tosh");

        File.WriteAllText(profilePath, string.Empty);
        File.WriteAllText(betaPath, string.Empty);
        File.WriteAllText(alphaPath, string.Empty);

        var paths = ToshStartupLoader.EnumerateStartupFiles(configDirectory);

        Assert.Equal(new[] { profilePath, alphaPath, betaPath }, paths);
    }

    [Fact]
    public async Task Startup_loader_executes_profile_and_autoload_scripts()
    {
        using var tempDirectory = new TemporaryDirectory();
        var configDirectory = tempDirectory.Path;
        var autoloadDirectory = System.IO.Path.Combine(configDirectory, "autoload");
        Directory.CreateDirectory(autoloadDirectory);

        File.WriteAllText(System.IO.Path.Combine(configDirectory, "profile.tosh"), "alias ll = ls -la");
        File.WriteAllText(System.IO.Path.Combine(autoloadDirectory, "functions.tosh"), "def stringifyCount() -> String { count }");

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await ToshStartupLoader.LoadAsync(engine, configDirectory);

        var aliasKinds = await engine.ExecuteToListAsync("which ll | get Kind");
        var functionResults = await engine.ExecuteToListAsync("echo 1 2 3 | stringifyCount");

        Assert.Contains(CommandResolutionKind.Alias, aliasKinds.Cast<CommandResolutionKind>());
        Assert.Collection(functionResults, item => Assert.Equal("3", item));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-startup-tests-{Guid.NewGuid():N}");
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
