using Tosh.Cli;
using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class StartupLoaderTests
{
    [Fact]
    public void Startup_loader_enumerates_config_profile_before_sorted_autoload_files()
    {
        using var tempDirectory = new TemporaryDirectory();
        var configDirectory = tempDirectory.Path;
        var autoloadDirectory = System.IO.Path.Combine(configDirectory, "autoload");
        Directory.CreateDirectory(autoloadDirectory);

        var configPath = System.IO.Path.Combine(configDirectory, "config.tosh");
        var profilePath = System.IO.Path.Combine(configDirectory, "profile.tosh");
        var betaPath = System.IO.Path.Combine(autoloadDirectory, "20-beta.tosh");
        var alphaPath = System.IO.Path.Combine(autoloadDirectory, "10-alpha.tosh");

        File.WriteAllText(configPath, string.Empty);
        File.WriteAllText(profilePath, string.Empty);
        File.WriteAllText(betaPath, string.Empty);
        File.WriteAllText(alphaPath, string.Empty);

        var paths = ToshStartupLoader.EnumerateStartupFiles(configDirectory);

        Assert.Equal(new[] { configPath, profilePath, alphaPath, betaPath }, paths);
    }

    [Fact]
    public void Startup_loader_enumerates_without_profile_when_skip_profile_is_true()
    {
        using var tempDirectory = new TemporaryDirectory();
        var configDirectory = tempDirectory.Path;
        var autoloadDirectory = System.IO.Path.Combine(configDirectory, "autoload");
        Directory.CreateDirectory(autoloadDirectory);

        var configPath = System.IO.Path.Combine(configDirectory, "config.tosh");
        var profilePath = System.IO.Path.Combine(configDirectory, "profile.tosh");
        var alphaPath = System.IO.Path.Combine(autoloadDirectory, "10-alpha.tosh");

        File.WriteAllText(configPath, string.Empty);
        File.WriteAllText(profilePath, string.Empty);
        File.WriteAllText(alphaPath, string.Empty);

        var startup = new ToshStartupConfig(configDirectory);
        var paths = ToshStartupLoader.EnumerateStartupFiles(startup, includeConfigFile: true, includeProfile: false);

        Assert.Equal(new[] { configPath, alphaPath }, paths);
        Assert.DoesNotContain(profilePath, paths);
    }

    [Fact]
    public async Task Startup_loader_executes_profile_and_autoload_scripts()
    {
        using var tempDirectory = new TemporaryDirectory();
        var configDirectory = tempDirectory.Path;
        var autoloadDirectory = System.IO.Path.Combine(configDirectory, "autoload");
        Directory.CreateDirectory(autoloadDirectory);

        File.WriteAllText(System.IO.Path.Combine(configDirectory, "profile.tosh"), "func ll => ls -la");
        File.WriteAllText(System.IO.Path.Combine(autoloadDirectory, "functions.tosh"), "func stringifyCount() -> String { count }");

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        await ToshStartupLoader.LoadAsync(engine, configDirectory);

        var functionKinds = await engine.ExecuteToListAsync("which ll | get Kind");
        var functionResults = await engine.ExecuteToListAsync("echo 1 2 3 | stringifyCount");

        Assert.Contains(CommandResolutionKind.Function, functionKinds.Cast<CommandResolutionKind>());
        Assert.Collection(functionResults, item => Assert.Equal("3", item));
    }

    [Fact]
    public async Task Startup_loader_continues_after_broken_profile()
    {
        using var tempDirectory = new TemporaryDirectory();
        var configDirectory = tempDirectory.Path;
        var autoloadDirectory = System.IO.Path.Combine(configDirectory, "autoload");
        Directory.CreateDirectory(autoloadDirectory);

        File.WriteAllText(System.IO.Path.Combine(configDirectory, "profile.tosh"), "this is intentionally broken syntax !!!");
        File.WriteAllText(System.IO.Path.Combine(autoloadDirectory, "functions.tosh"), "func autoloaded() -> String { \"ok\" }");

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        var errorOutput = new StringWriter();

        await ToshStartupLoader.LoadAsync(engine, configDirectory, skipProfile: false, errorWriter: errorOutput);

        var results = await engine.ExecuteToListAsync("autoloaded");

        Assert.Collection(results, item => Assert.Equal("ok", item));
        Assert.Contains("error loading", errorOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_loader_continues_after_broken_autoload_script()
    {
        using var tempDirectory = new TemporaryDirectory();
        var configDirectory = tempDirectory.Path;
        var autoloadDirectory = System.IO.Path.Combine(configDirectory, "autoload");
        Directory.CreateDirectory(autoloadDirectory);

        File.WriteAllText(System.IO.Path.Combine(autoloadDirectory, "10-broken.tosh"), "this is broken !!!");
        File.WriteAllText(System.IO.Path.Combine(autoloadDirectory, "20-good.tosh"), "func goodFunc() -> String { \"loaded\" }");

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        var errorOutput = new StringWriter();

        await ToshStartupLoader.LoadAsync(engine, configDirectory, skipProfile: false, errorWriter: errorOutput);

        var results = await engine.ExecuteToListAsync("goodFunc");

        Assert.Collection(results, item => Assert.Equal("loaded", item));
        Assert.Contains("error loading", errorOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_loader_skips_profile_when_skip_profile_is_true()
    {
        using var tempDirectory = new TemporaryDirectory();
        var configDirectory = tempDirectory.Path;
        var autoloadDirectory = System.IO.Path.Combine(configDirectory, "autoload");
        Directory.CreateDirectory(autoloadDirectory);

        File.WriteAllText(System.IO.Path.Combine(configDirectory, "profile.tosh"), "func profileFunc => echo profile");
        File.WriteAllText(System.IO.Path.Combine(autoloadDirectory, "helpers.tosh"), "func autoloadFunc() -> String { \"autoloaded\" }");

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        await ToshStartupLoader.LoadAsync(engine, configDirectory, skipProfile: true);

        var autoloadResults = await engine.ExecuteToListAsync("autoloadFunc");
        Assert.Collection(autoloadResults, item => Assert.Equal("autoloaded", item));

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync("profileFunc"));
        Assert.Contains("profileFunc", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_loader_uses_config_file_to_redirect_profile_and_autoload_locations()
    {
        using var tempDirectory = new TemporaryDirectory();
        var configDirectory = tempDirectory.Path;
        var redirectedRoot = System.IO.Path.Combine(configDirectory, "custom");
        var redirectedAutoload = System.IO.Path.Combine(redirectedRoot, "modules");
        Directory.CreateDirectory(redirectedAutoload);

        File.WriteAllText(
            System.IO.Path.Combine(configDirectory, "config.tosh"),
            $"$tosh.Config.Startup.ProfilePath = \"{System.IO.Path.Combine("custom", "my-profile.tosh")}\"\n$tosh.Config.Startup.AutoloadDirectory = \"{System.IO.Path.Combine("custom", "modules")}\"");
        File.WriteAllText(System.IO.Path.Combine(redirectedRoot, "my-profile.tosh"), "func ll => ls -la");
        File.WriteAllText(System.IO.Path.Combine(redirectedAutoload, "helpers.tosh"), "func helper() -> String { \"ok\" }");

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        await ToshStartupLoader.LoadAsync(engine, configDirectory);

        var functionKinds = await engine.ExecuteToListAsync("which ll | get Kind");
        var helperResult = await engine.ExecuteToListAsync("helper");

        Assert.Contains(CommandResolutionKind.Function, functionKinds.Cast<CommandResolutionKind>());
        Assert.Collection(helperResult, item => Assert.Equal("ok", item));
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
