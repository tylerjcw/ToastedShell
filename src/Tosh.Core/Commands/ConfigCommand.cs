namespace Tosh.Core.Commands;

[CommandCategory("Shell")]
[CommandArgument("browse [query]", "Opens the full-screen config browser, optionally filtered by an initial query, with staged editing, subtree diffs, structured section and collection editors, reusable confirmation and validation surfaces, filesystem browsing, apply/save flows, startup reload/init actions, live prompt/style/theme previews, and raw text editing for advanced cases.", Required = false)]
[CommandArgument("get <path>", "Reads one config value.", Required = false)]
[CommandArgument("set <path> [value]", "Sets one config value.", Required = false)]
[CommandArgument("reset [section]", "Resets a section or the whole config object.", Required = false)]
[CommandArgument("reload", "Replays startup config files into the current session.", Required = false)]
[CommandArgument("init [directory]", "Scaffolds a new config directory.", Required = false)]
[CommandExample("config get Shell.Prompt", Title = "Read a config value")]
[CommandExample("config set Shell.Prompt.NameText toast", Title = "Set a config value")]
[CommandExample("config browse prompt", Title = "Open the interactive config browser")]
[CommandExample("config init ~/.config/tosh", Title = "Scaffold a config directory")]
[CommandOutput("Produces the live config object, one config value, status rows, or an interactive browser request depending on the form.")]
[PipelineInput(AcceptsScalar = true, Description = "Only `config set` consumes piped scalar input, using it as the new value when no explicit value argument is present.")]
public sealed class ConfigCommand : ShellCommand
{
    public ConfigCommand()
        : base("config", "Gets or changes shell configuration.", "config [browse [query]|get <path>|set <path> [value]|reset [path]|init [path]|reload]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count == 0)
        {
            yield return context.Runtime.Config;
            yield break;
        }

        var action = CommandArguments.RequireString(parsed.Positionals, 0, "action");

        switch (action.ToLowerInvariant())
        {
            case "browse":
                {
                    var initialQuery = parsed.Positionals.Count > 1
                        ? string.Join(" ", parsed.Positionals.Skip(1).Select(value => value?.ToString() ?? string.Empty)).Trim()
                        : null;
                    yield return new ConfigBrowseRequest(string.IsNullOrWhiteSpace(initialQuery) ? null : initialQuery, null);
                    yield break;
                }

            case "get":
                {
                    if (parsed.Positionals.Count == 1)
                    {
                        yield return context.Runtime.Config;
                        yield break;
                    }

                    var path = CommandArguments.RequireString(parsed.Positionals, 1, "path");
                    yield return ResolveValue(context.Runtime, path);
                    yield break;
                }

            case "set":
                {
                    var path = CommandArguments.RequireString(parsed.Positionals, 1, "path");
                    var value = parsed.Positionals.Count > 2
                        ? parsed.Positionals[2]
                        : await ResolvePipelinedValueAsync(context, path);
                    var normalizedPath = ConfigPathUtilities.NormalizeMemberPath(context.Runtime.Config, path);
                    context.Runtime.ObjectAccessor.SetValue(context.Runtime.Config, normalizedPath, value);
                    yield return new ConfigMutationResult(normalizedPath, context.Runtime.ObjectAccessor.GetValue(context.Runtime.Config, normalizedPath));
                    yield break;
                }

            case "reset":
                {
                    if (parsed.Positionals.Count == 1)
                    {
                        context.Runtime.Config.Reset();
                        yield return context.Runtime.Config;
                        yield break;
                    }

                    var path = CommandArguments.RequireString(parsed.Positionals, 1, "path");
                    var target = ResolveValue(context.Runtime, path);

                    if (target is not IResettableShellConfig resettable)
                    {
                        throw new InvalidOperationException($"Configuration path '{path}' is not resettable.");
                    }

                    resettable.Reset();
                    yield return target;
                    yield break;
                }

            case "init":
                {
                    var rootDirectory = parsed.Positionals.Count > 1
                        ? PathUtilities.ResolvePath(context.Runtime.CurrentDirectory, CommandArguments.RequireString(parsed.Positionals, 1, "path"))
                        : context.Runtime.Config.Startup.RootDirectory;

                    yield return InitializeConfigDirectory(rootDirectory);
                    yield break;
                }

            case "reload":
                {
                    if (parsed.Positionals.Count > 1)
                    {
                        throw new InvalidOperationException("The 'config reload' action does not accept additional arguments.");
                    }

                    yield return await ReloadConfigurationAsync(context);
                    yield break;
                }

            default:
                throw new InvalidOperationException("config action must be 'get', 'set', 'reset', 'init', or 'reload'.");
        }
    }

    private static async Task<ConfigReloadResult> ReloadConfigurationAsync(CommandContext context)
    {
        try
        {
            return await ConfigStartupUtilities.ReloadConfigurationAsync(context.Runtime, context.CancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw context.CreateDiagnostic(
                code: "tosh::config::reload_unavailable",
                title: "Configuration reload is not available in this session.",
                help: "Reload requires a live evaluator. In normal ToSh sessions, `config reload` should be available.");
        }
    }

    private static async Task<object?> ResolvePipelinedValueAsync(CommandContext context, string path)
    {
        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);

        if (await enumerator.MoveNextAsync())
        {
            return enumerator.Current;
        }

        throw context.CreateDiagnostic(
            code: "tosh::config::missing_value",
            title: $"Configuration path '{path}' needs a value.",
            argumentIndex: 1,
            label: "provide a value argument or pipe one into `config set`",
            help: "Examples: `config set prompt.name-text toast` or `echo toast | config set prompt.name-text`.");
    }

    private static ConfigInitializationResult InitializeConfigDirectory(string rootDirectory)
    {
        return ConfigStartupUtilities.InitializeConfigDirectory(rootDirectory);
    }

    private static object? ResolveValue(ToshRuntime runtime, string path)
    {
        var normalizedPath = ConfigPathUtilities.NormalizeMemberPath(runtime.Config, path);
        return runtime.ObjectAccessor.GetValue(runtime.Config, normalizedPath);
    }
}

public sealed record ConfigMutationResult(string Path, object? Value);
