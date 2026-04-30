using Tosh.Core;
using Tosh.Core.Commands;
using Tosh.Core.Commands.Shell;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class ConfigCommandTests
{
    [Fact]
    public async Task Config_command_can_get_and_set_runtime_configuration()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("config set display.style detail; config get display.style");

        Assert.Equal(ObjectRenderStyle.Detail, runtime.Display.Style);
        Assert.Collection(results,
            item =>
            {
                var mutation = Assert.IsType<ConfigMutationResult>(item);
                Assert.Equal("Display.Style", mutation.Path);
                Assert.Equal(ObjectRenderStyle.Detail, mutation.Value);
            },
            item => Assert.Equal(ObjectRenderStyle.Detail, Assert.IsType<ObjectRenderStyle>(item)));
    }

    [Fact]
    public async Task Config_command_normalizes_dash_cased_paths()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("config set repl.continuation-prompt \"..> \"; config get repl.continuation-prompt");

        Assert.Equal("..> ", runtime.Config.Repl.ContinuationPrompt);
        Assert.Collection(results,
            item =>
            {
                var mutation = Assert.IsType<ConfigMutationResult>(item);
                Assert.Equal("Repl.ContinuationPrompt", mutation.Path);
                Assert.Equal("..> ", mutation.Value);
            },
            item => Assert.Equal("..> ", Assert.IsType<string>(item)));
    }

    [Fact]
    public async Task Config_command_can_customize_theme_paths()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("config set theme.syntax.valid-command.foreground bright-magenta; config get theme.syntax.valid-command.foreground");

        Assert.Equal("bright-magenta", runtime.Config.Theme.Syntax.ValidCommand.Foreground);
        Assert.Collection(results,
            item =>
            {
                var mutation = Assert.IsType<ConfigMutationResult>(item);
                Assert.Equal("Theme.Syntax.ValidCommand.Foreground", mutation.Path);
                Assert.Equal("bright-magenta", mutation.Value);
            },
            item => Assert.Equal("bright-magenta", Assert.IsType<string>(item)));
    }

    [Fact]
    public async Task Config_command_can_customize_table_box_style()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("config set theme.tables.box-style double");

        Assert.Equal(ToshTableBoxStyle.Double, runtime.Config.Theme.Tables.BoxStyle);
        Assert.Equal(runtime.Config.Theme.Tables, runtime.Display.TableTheme);
    }

    [Fact]
    public async Task Config_command_can_request_interactive_browser()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("config browse box style");

        var request = Assert.IsType<ConfigBrowseRequest>(Assert.Single(results));
        Assert.Equal("box style", request.InitialQuery);
        Assert.Null(request.InitialPath);
    }

    [Fact]
    public async Task Config_variable_can_be_used_to_customize_runtime_directly()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("$tosh.Config.Display.StorageSize.Mode = \"Bytes\"");

        Assert.Equal(StorageSizeDisplayMode.Bytes, runtime.DisplayPreferences.StorageSize.Mode);
    }

    [Fact]
    public async Task Config_set_can_consume_a_pipelined_value()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("echo toast | config set prompt.name-text");

        Assert.Equal("toast", runtime.Config.Prompt.NameText);
        var mutation = Assert.IsType<ConfigMutationResult>(Assert.Single(results));
        Assert.Equal("Prompt.NameText", mutation.Path);
        Assert.Equal("toast", mutation.Value);
    }

    [Fact]
    public async Task Config_init_creates_startup_layout_without_overwriting_existing_files()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var rootDirectory = Directory.CreateTempSubdirectory().FullName;
        var existingProfile = Path.Combine(rootDirectory, "profile.tosh");
        File.WriteAllText(existingProfile, "# existing profile");

        try
        {
            var results = await engine.ExecuteToListAsync($"config init \"{rootDirectory}\"");

            var init = Assert.IsType<ConfigInitializationResult>(Assert.Single(results));
            Assert.Equal(rootDirectory, init.RootDirectory);
            Assert.True(File.Exists(init.ConfigFilePath));
            Assert.True(File.Exists(init.ProfileFilePath));
            Assert.True(Directory.Exists(init.AutoloadDirectory));
            Assert.Contains(init.ConfigFilePath, init.CreatedPaths);
            Assert.DoesNotContain(init.ProfileFilePath, init.CreatedPaths);
            Assert.Equal("# existing profile", File.ReadAllText(existingProfile));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Config_reload_reapplies_startup_files_and_resets_unspecified_values()
    {
        using var tempDirectory = new TemporaryDirectory();
        var configDirectory = tempDirectory.Path;
        var autoloadDirectory = Path.Combine(configDirectory, "autoload");
        Directory.CreateDirectory(autoloadDirectory);

        File.WriteAllText(Path.Combine(configDirectory, "config.tosh"), "$tosh.Config.Prompt.NameText = \"toast\"\n$tosh.Config.Theme.Tables.BoxStyle = \"Double\"");
        File.WriteAllText(Path.Combine(configDirectory, "profile.tosh"), "func ll => ls -la");
        File.WriteAllText(Path.Combine(autoloadDirectory, "20-theme.tosh"), "$tosh.Config.Repl.ContinuationPrompt = \"..> \"");

        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Startup.ApplyRootDirectory(configDirectory);
        runtime.Config.Prompt.NameText = "stale";
        runtime.Config.Repl.GhostTextEnabled = false;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("config reload");

        Assert.Equal("toast", runtime.Config.Prompt.NameText);
        Assert.Equal("..> ", runtime.Config.Repl.ContinuationPrompt);
        Assert.True(runtime.Config.Repl.GhostTextEnabled);
        Assert.Equal(ToshTableBoxStyle.Double, runtime.Config.Theme.Tables.BoxStyle);

        var reload = Assert.IsType<ConfigReloadResult>(Assert.Single(results));
        Assert.Equal(configDirectory, reload.RootDirectory);
        Assert.Contains(Path.Combine(configDirectory, "config.tosh"), reload.LoadedPaths);
        Assert.Contains(Path.Combine(configDirectory, "profile.tosh"), reload.LoadedPaths);
        Assert.Contains(Path.Combine(autoloadDirectory, "20-theme.tosh"), reload.LoadedPaths);

        var functionKinds = await engine.ExecuteToListAsync("which ll | get Kind");
        Assert.Contains(CommandResolutionKind.Function, functionKinds.Cast<CommandResolutionKind>());
    }

    [Fact]
    public async Task Config_reload_uses_config_file_to_redirect_profile_and_autoload_locations()
    {
        using var tempDirectory = new TemporaryDirectory();
        var configDirectory = tempDirectory.Path;
        var redirectedRoot = Path.Combine(configDirectory, "custom");
        var redirectedAutoload = Path.Combine(redirectedRoot, "modules");
        Directory.CreateDirectory(redirectedAutoload);

        File.WriteAllText(
            Path.Combine(configDirectory, "config.tosh"),
            $"$tosh.Config.Startup.ProfilePath = \"{Path.Combine("custom", "my-profile.tosh")}\"\n$tosh.Config.Startup.AutoloadDirectory = \"{Path.Combine("custom", "modules")}\"");
        File.WriteAllText(Path.Combine(redirectedRoot, "my-profile.tosh"), "func ll => ls -la");
        File.WriteAllText(Path.Combine(redirectedAutoload, "helpers.tosh"), "$tosh.Config.Prompt.NameText = \"redirected\"\nfunc helper() -> String { \"ok\" }");

        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Startup.ApplyRootDirectory(configDirectory);
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("config reload");

        Assert.Equal("redirected", runtime.Config.Prompt.NameText);

        var reload = Assert.IsType<ConfigReloadResult>(Assert.Single(results));
        Assert.Equal(Path.Combine(redirectedRoot, "my-profile.tosh"), reload.ProfileFilePath);
        Assert.Equal(redirectedAutoload, reload.AutoloadDirectory);
        Assert.Contains(Path.Combine(configDirectory, "config.tosh"), reload.LoadedPaths);
        Assert.Contains(Path.Combine(redirectedRoot, "my-profile.tosh"), reload.LoadedPaths);
        Assert.Contains(Path.Combine(redirectedAutoload, "helpers.tosh"), reload.LoadedPaths);

        var functionKinds = await engine.ExecuteToListAsync("which ll | get Kind");
        var helperResult = await engine.ExecuteToListAsync("helper");
        Assert.Contains(CommandResolutionKind.Function, functionKinds.Cast<CommandResolutionKind>());
        Assert.Collection(helperResult, item => Assert.Equal("ok", item));
    }

    [Fact]
    public void History_configuration_can_limit_recorded_entries()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.History.MaxEntries = 2;

        runtime.RecordHistory("help");
        runtime.RecordHistory("ls");
        runtime.RecordHistory("pwd");

        Assert.Collection(
            runtime.History,
            entry =>
            {
                Assert.Equal(2, entry.Id);
                Assert.Equal("ls", entry.Text);
            },
            entry =>
            {
                Assert.Equal(3, entry.Id);
                Assert.Equal("pwd", entry.Text);
            });
    }

    [Fact]
    public void History_configuration_persists_entries_to_the_configured_file()
    {
        using var tempDirectory = new TemporaryDirectory();
        var historyPath = Path.Combine(tempDirectory.Path, "history.jsonl");

        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.History.FilePath = historyPath;
        runtime.InitializeHistoryStorage(writeThrough: true);

        runtime.RecordHistory("help");
        runtime.RecordHistory("ls -la");

        Assert.True(File.Exists(historyPath));

        var reloadedRuntime = ToshRuntime.CreateDefault();
        reloadedRuntime.Config.History.FilePath = historyPath;
        reloadedRuntime.InitializeHistoryStorage(writeThrough: false);

        Assert.Collection(
            reloadedRuntime.History,
            entry => Assert.Equal("help", entry.Text),
            entry => Assert.Equal("ls -la", entry.Text));
    }

    [Fact]
    public void History_configuration_can_ignore_consecutive_duplicates()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.History.Deduplication = ToshHistoryDeduplicationMode.Consecutive;

        runtime.RecordHistory("help");
        runtime.RecordHistory("help");
        runtime.RecordHistory("ls");
        runtime.RecordHistory("help");

        Assert.Collection(
            runtime.History,
            entry => Assert.Equal("help", entry.Text),
            entry => Assert.Equal("ls", entry.Text),
            entry => Assert.Equal("help", entry.Text));
    }

    [Fact]
    public void History_configuration_can_remove_older_duplicates()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.History.Deduplication = ToshHistoryDeduplicationMode.All;

        runtime.RecordHistory("help");
        runtime.RecordHistory("ls");
        runtime.RecordHistory("help");

        Assert.Collection(
            runtime.History,
            entry =>
            {
                Assert.Equal(2, entry.Id);
                Assert.Equal("ls", entry.Text);
            },
            entry =>
            {
                Assert.Equal(3, entry.Id);
                Assert.Equal("help", entry.Text);
            });
    }

    [Fact]
    public void History_configuration_can_ignore_commands_with_leading_space()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.History.IgnoreLeadingSpace = true;

        runtime.RecordHistory(" help");
        runtime.RecordHistory("ls");

        Assert.Collection(
            runtime.History,
            entry => Assert.Equal("ls", entry.Text));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-config-tests-{Guid.NewGuid():N}");
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
