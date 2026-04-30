using Tosh.Tui;
using Tosh.Tui.Requests;
using Tosh.Tui.Widgets;

namespace Tosh.Core.Commands;

[ShellOnly]
[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Shell")]
[CommandArgument("pick [items...]", "Pick one or more values from arguments or pipeline input.", Required = false)]
[CommandArgument("confirm <message>", "Ask for a yes/no confirmation.", Required = false)]
[CommandArgument("input [prompt]", "Read text input, optionally multiline or password-style.", Required = false)]
[CommandArgument("file", "Open a file or directory picker.", Required = false)]
[CommandArgument("filter [items...]", "Open a fuzzy filter picker.", Required = false)]
[CommandArgument("screen|add-*|layout|run", "Build and run composed TUI screens from pipeline-carried screen definitions.", Required = false)]
[CommandOption("--cli", "Use inline terminal prompts instead of returning fullscreen TUI request objects where supported.")]
[CommandOption("--multi, -m", "Allow multiple selections for `pick`, `filter`, and list widgets.")]
[CommandOption("--result", "Return a structured outcome object instead of only the selected value/result.")]
[CommandOption("--prompt <text>", "Prompt text for picker, filter, input, and widget subcommands.")]
[CommandOption("--display <property>", "Property name used as the display label for object items.")]
[CommandOption("--page-size <n>", "Number of visible entries in pick/filter lists.")]
[CommandOption("--default <value|yes|no>", "Default input value or confirmation default.")]
[CommandOption("--multiline", "Allow multiline input for `input` and `add-input`.")]
[CommandOption("--password", "Mask text for `input`.")]
[CommandOption("--path <start>", "Initial path for `file`.")]
[CommandOption("--filter <glob>", "File picker filter such as `*.tosh`.")]
[CommandOption("--directory, -d", "Choose directories instead of files for `file`.")]
[CommandOption("--id <id>", "Stable widget id for screen-builder subcommands.")]
[CommandOption("--searchable, -s", "Make a list widget searchable.")]
[CommandOption("--bind <widget.property>", "Bind a text widget to another widget property.")]
[CommandOption("--no-wrap", "Disable text wrapping for `add-text`.")]
[CommandOption("--ratio <a:b>", "Layout split ratio for `layout`.")]
[CommandOption("--gap <n>", "Gap between layout regions.")]
[CommandExample("tui confirm \"Deploy now?\" --cli", Title = "Inline confirmation")]
[CommandExample("ls | tui pick --display Name --result", Title = "Pick from pipeline values")]
[CommandExample("tui input \"Project name:\" --default demo --cli", Title = "Inline text input")]
[CommandOutput("Emits nothing; drives the interactive TUI session as a side effect.")]
public sealed class TuiCommand : ShellCommand
{
    public TuiCommand()
        : base("tui",
            "Interactive TUI components for scripts. Provides list pickers, confirmations, text input, file pickers, and custom screens. Use --cli for inline (non-fullscreen) prompts.",
            "tui pick|confirm|input|file|filter|screen|add-list|add-text|add-input|add-picker|layout|run [options]")
    { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.tui.missing_subcommand",
                title: "The 'tui' command requires a subcommand.",
                help: "Available subcommands: pick, confirm, input, file, filter, screen, add-list, add-text, add-input, add-picker, layout, run");
        }

        var subcommand = CommandArguments.RequireString(context.Arguments, 0, "subcommand");
        var subArgs = CommandArguments.Slice(context.Arguments, 1);
        var subContext = context with { Arguments = subArgs };

        await foreach (var result in DispatchSubcommand(subcommand, subContext))
        {
            yield return result;
        }
    }

    private static IAsyncEnumerable<object?> DispatchSubcommand(string subcommand, CommandContext context)
    {
        return subcommand.ToLowerInvariant() switch
        {
            "pick" => ExecutePickAsync(context),
            "confirm" => ExecuteConfirmAsync(context),
            "input" => ExecuteInputAsync(context),
            "file" => ExecuteFileAsync(context),
            "filter" => ExecuteFilterAsync(context),
            "screen" => ExecuteScreenAsync(context),
            "add-list" => ExecuteAddListAsync(context),
            "add-text" => ExecuteAddTextAsync(context),
            "add-input" => ExecuteAddInputAsync(context),
            "add-picker" => ExecuteAddPickerAsync(context),
            "layout" => ExecuteLayoutAsync(context),
            "run" => ExecuteRunAsync(context),
            _ => throw context.CreateDiagnostic(
                code: "tosh.tui.unknown_subcommand",
                title: $"Unknown tui subcommand '{subcommand}'.",
                argumentIndex: 0,
                help: "Available subcommands: pick, confirm, input, file, filter, screen, add-list, add-text, add-input, add-picker, layout, run"),
        };
    }

    // ── tui pick ──────────────────────────────────────────────
    // Usage: tui pick [items...] [--multi] [--prompt "text"] [--display <property>] [--result] [--cli]
    // Or: <pipeline> | tui pick [--multi] [--prompt "text"] [--display <property>] [--result] [--cli]
    private static async IAsyncEnumerable<object?> ExecutePickAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var multi = parsed.HasFlag("multi", "m");
        var returnOutcome = parsed.HasFlag("result");
        var cli = parsed.HasFlag("cli");
        var prompt = ExtractNamedArgument(parsed.Positionals, "prompt");
        var display = ExtractNamedArgument(parsed.Positionals, "display");
        var pageSizeStr = ExtractNamedArgument(parsed.Positionals, "page-size");
        var pageSize = pageSizeStr is not null && int.TryParse(pageSizeStr, out var ps) ? ps : 10;

        var items = await CollectItemsAsync(context, parsed.Positionals, skipNamedArgs: new[] { "prompt", "display", "page-size" });

        if (items.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.tui.pick.no_items",
                title: "No items provided for 'tui pick'.",
                help: "Pipe items into 'tui pick' or provide them as arguments: tui pick item1 item2 item3");
        }

        if (cli)
        {
            var provider = RequireInlineProvider(context);
            var result = provider.Pick(items, prompt, display, multi, pageSize);

            if (result is not null)
            {
                foreach (var item in result)
                {
                    yield return item;
                }
            }
        }
        else
        {
            yield return new TuiPickRequest(items, display, prompt, multi, returnOutcome);
        }
    }

    // ── tui confirm ───────────────────────────────────────────
    // Usage: tui confirm "message" [--default yes|no] [--result] [--cli]
    private static async IAsyncEnumerable<object?> ExecuteConfirmAsync(CommandContext context)
    {
        await Task.CompletedTask;

        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var returnOutcome = parsed.HasFlag("result");
        var cli = parsed.HasFlag("cli");

        var message = parsed.Positionals.Count > 0
            ? parsed.Positionals[0]?.ToString() ?? "Confirm?"
            : "Confirm?";

        var defaultConfirm = true;

        if (parsed.HasFlag("default"))
        {
            var defaultValue = ExtractNamedArgument(parsed.Positionals, "default");

            if (string.Equals(defaultValue, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(defaultValue, "false", StringComparison.OrdinalIgnoreCase))
            {
                defaultConfirm = false;
            }
        }

        if (cli)
        {
            var provider = RequireInlineProvider(context);
            var result = provider.Confirm(message, defaultConfirm);
            yield return result ?? false;
        }
        else
        {
            yield return new TuiConfirmRequest(message, DefaultConfirm: defaultConfirm, ReturnOutcome: returnOutcome);
        }
    }

    // ── tui input ─────────────────────────────────────────────
    // Usage: tui input ["prompt"] [--default <value>] [--multiline] [--result] [--cli] [--password]
    private static async IAsyncEnumerable<object?> ExecuteInputAsync(CommandContext context)
    {
        await Task.CompletedTask;

        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var multiline = parsed.HasFlag("multiline");
        var returnOutcome = parsed.HasFlag("result");
        var cli = parsed.HasFlag("cli");
        var password = parsed.HasFlag("password");

        var prompt = parsed.Positionals.Count > 0
            ? parsed.Positionals[0]?.ToString()
            : null;

        var defaultValue = ExtractNamedArgument(parsed.Positionals, "default");

        if (cli)
        {
            var provider = RequireInlineProvider(context);
            var result = provider.Input(prompt, defaultValue, password);
            if (result is not null)
            {
                yield return result;
            }
        }
        else
        {
            yield return new TuiInputRequest(prompt, defaultValue, multiline, returnOutcome);
        }
    }

    // ── tui file ──────────────────────────────────────────────
    // Usage: tui file [--path <start>] [--filter "*.tosh"] [--directory] [--result] [--cli]
    private static async IAsyncEnumerable<object?> ExecuteFileAsync(CommandContext context)
    {
        await Task.CompletedTask;

        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var directoryOnly = parsed.HasFlag("directory", "d");
        var returnOutcome = parsed.HasFlag("result");
        var cli = parsed.HasFlag("cli");
        var initialPath = ExtractNamedArgument(parsed.Positionals, "path");
        var filter = ExtractNamedArgument(parsed.Positionals, "filter");

        if (cli)
        {
            // For --cli file picking, list files and use the inline Pick provider
            var provider = RequireInlineProvider(context);
            var basePath = initialPath ?? context.Runtime.CurrentDirectory;

            var entries = directoryOnly
                ? Directory.GetDirectories(basePath).Select(Path.GetFileName).Cast<object?>().ToArray()
                : Directory.GetFileSystemEntries(basePath)
                    .Where(e => filter is null || MatchesFilter(e, filter))
                    .Select(Path.GetFileName)
                    .Cast<object?>()
                    .ToArray();

            var result = provider.Pick(entries, directoryOnly ? "Select directory:" : "Select file:");

            if (result is { Count: > 0 } && result[0] is string selected)
            {
                yield return Path.Combine(basePath, selected);
            }
        }
        else
        {
            yield return new TuiFilePickRequest(initialPath, filter, directoryOnly, returnOutcome);
        }
    }

    // ── tui filter ────────────────────────────────────────────
    // Usage: tui filter [items...] [--multi] [--prompt "text"] [--display <property>] [--page-size <n>]
    // Or: <pipeline> | tui filter [--multi] [--prompt "text"] [--display <property>]
    // Always inline (no fullscreen equivalent).
    private static async IAsyncEnumerable<object?> ExecuteFilterAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var multi = parsed.HasFlag("multi", "m");
        var prompt = ExtractNamedArgument(parsed.Positionals, "prompt");
        var display = ExtractNamedArgument(parsed.Positionals, "display");
        var pageSizeStr = ExtractNamedArgument(parsed.Positionals, "page-size");
        var pageSize = pageSizeStr is not null && int.TryParse(pageSizeStr, out var ps) ? ps : 10;

        var items = await CollectItemsAsync(context, parsed.Positionals, skipNamedArgs: new[] { "prompt", "display", "page-size" });

        if (items.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.tui.filter.no_items",
                title: "No items provided for 'tui filter'.",
                help: "Pipe items into 'tui filter' or provide them as arguments.");
        }

        var provider = RequireInlineProvider(context);
        var result = provider.Filter(items, prompt, display, multi, pageSize);

        if (result is not null)
        {
            foreach (var item in result)
            {
                yield return item;
            }
        }
    }

    // ── tui screen ────────────────────────────────────────────
    // Usage: tui screen [--title "text"]
    // Creates and yields a new TuiScreen for pipeline building
    private static async IAsyncEnumerable<object?> ExecuteScreenAsync(CommandContext context)
    {
        await Task.CompletedTask;

        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var title = ExtractNamedArgument(parsed.Positionals, "title");

        var screen = new TuiScreen();

        if (title is not null)
        {
            screen.Title(title);
        }

        yield return screen;
    }

    // ── tui add-list ──────────────────────────────────────────
    // Usage: <screen> | tui add-list <items-var> [--id <id>] [--display <prop>] [--multi] [--searchable] [--prompt "text"]
    private static async IAsyncEnumerable<object?> ExecuteAddListAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var multi = parsed.HasFlag("multi", "m");
        var searchable = parsed.HasFlag("searchable", "s");
        var id = ExtractNamedArgument(parsed.Positionals, "id") ?? $"list-{Guid.NewGuid():N}"[..12];
        var display = ExtractNamedArgument(parsed.Positionals, "display");
        var prompt = ExtractNamedArgument(parsed.Positionals, "prompt");

        var (screen, remaining) = await ReadScreenFromPipelineAsync(context);

        // Remaining positionals after named extraction are the items (or a single collection arg)
        var items = CollectPositionalItems(remaining);

        var widget = new TuiListWidgetConfig(id, items)
        {
            DisplayProperty = display,
            MultiSelect = multi,
            Searchable = searchable,
            Prompt = prompt,
        };

        screen.AddWidget(widget);
        yield return screen;
    }

    // ── tui add-text ──────────────────────────────────────────
    // Usage: <screen> | tui add-text [content] [--id <id>] [--bind <widget.property>] [--no-wrap]
    private static async IAsyncEnumerable<object?> ExecuteAddTextAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var noWrap = parsed.HasFlag("no-wrap");
        var id = ExtractNamedArgument(parsed.Positionals, "id") ?? $"text-{Guid.NewGuid():N}"[..12];
        var bindSpec = ExtractNamedArgument(parsed.Positionals, "bind");

        var (screen, remaining) = await ReadScreenFromPipelineAsync(context);

        var widget = new TuiTextWidgetConfig(id)
        {
            WordWrap = !noWrap,
        };

        if (bindSpec is not null)
        {
            var parts = bindSpec.Split('.', 2);
            widget.Binding = parts.Length == 2
                ? new TuiWidgetBinding(parts[0], parts[1])
                : new TuiWidgetBinding(parts[0], "selected");
        }
        else if (remaining.Count > 0)
        {
            widget.Content = remaining.Count == 1 ? remaining[0] : string.Join("\n", remaining.Select(o => o?.ToString() ?? string.Empty));
        }

        screen.AddWidget(widget);
        yield return screen;
    }

    // ── tui add-input ─────────────────────────────────────────
    // Usage: <screen> | tui add-input [--id <id>] [--prompt "text"] [--default <value>] [--multiline]
    private static async IAsyncEnumerable<object?> ExecuteAddInputAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var multiline = parsed.HasFlag("multiline");
        var id = ExtractNamedArgument(parsed.Positionals, "id") ?? $"input-{Guid.NewGuid():N}"[..12];
        var prompt = ExtractNamedArgument(parsed.Positionals, "prompt");
        var defaultValue = ExtractNamedArgument(parsed.Positionals, "default");

        var (screen, _) = await ReadScreenFromPipelineAsync(context);

        var widget = new TuiTextInputConfig(id)
        {
            Prompt = prompt,
            DefaultValue = defaultValue,
            Multiline = multiline,
        };

        screen.AddWidget(widget);
        yield return screen;
    }

    // ── tui add-picker ────────────────────────────────────────
    // Usage: <screen> | tui add-picker <items-var> [--id <id>] [--display <prop>] [--prompt "text"]
    private static async IAsyncEnumerable<object?> ExecuteAddPickerAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var id = ExtractNamedArgument(parsed.Positionals, "id") ?? $"picker-{Guid.NewGuid():N}"[..12];
        var display = ExtractNamedArgument(parsed.Positionals, "display");
        var prompt = ExtractNamedArgument(parsed.Positionals, "prompt");

        var (screen, remaining) = await ReadScreenFromPipelineAsync(context);
        var options = CollectPositionalItems(remaining);

        var widget = new TuiOptionPickerConfig(id, options)
        {
            DisplayProperty = display,
            Prompt = prompt,
        };

        screen.AddWidget(widget);
        yield return screen;
    }

    // ── tui layout ────────────────────────────────────────────
    // Usage: <screen> | tui layout <orientation> [--ratio 30:70] [--gap <n>]
    private static async IAsyncEnumerable<object?> ExecuteLayoutAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var ratio = ExtractNamedArgument(parsed.Positionals, "ratio");
        var gapStr = ExtractNamedArgument(parsed.Positionals, "gap");

        var (screen, remaining) = await ReadScreenFromPipelineAsync(context);

        if (remaining.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.tui.layout.missing_orientation",
                title: "The 'tui layout' subcommand requires an orientation.",
                help: "Available orientations: single, split-horizontal, split-vertical, stacked");
        }

        var orientationStr = remaining[0]?.ToString() ?? string.Empty;
        var layout = ParseLayoutOrientation(orientationStr);
        screen.SetLayout(layout);

        if (ratio is not null)
        {
            screen.SetRatio(ratio);
        }

        if (gapStr is not null && int.TryParse(gapStr, out var gap))
        {
            screen.SetGap(gap);
        }

        yield return screen;
    }

    // ── tui run ───────────────────────────────────────────────
    // Usage: <screen> | tui run [--result]
    // Or: tui run <screen-variable> [--result]
    private static async IAsyncEnumerable<object?> ExecuteRunAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var returnOutcome = parsed.HasFlag("result");

        TuiScreen? screen = null;

        // Check positional argument first
        if (parsed.Positionals.Count > 0 && parsed.Positionals[0] is TuiScreen argScreen)
        {
            screen = argScreen;
        }

        // Fall back to pipeline input
        if (screen is null)
        {
            await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
            {
                if (item is TuiScreen pipeScreen)
                {
                    screen = pipeScreen;
                    break;
                }
            }
        }

        if (screen is null)
        {
            throw context.CreateDiagnostic(
                code: "tosh.tui.run.no_screen",
                title: "No TuiScreen provided to 'tui run'.",
                help: "Pipe a TuiScreen into 'tui run' or provide one as an argument.");
        }

        yield return new TuiRunRequest(screen, returnOutcome);
    }

    // ── Helpers ───────────────────────────────────────────────

    private static string? ExtractNamedArgument(IReadOnlyList<object?> positionals, string name)
    {
        for (var i = 0; i < positionals.Count - 1; i++)
        {
            if (positionals[i] is string key &&
                string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return positionals[i + 1]?.ToString();
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<object?>> CollectItemsAsync(
        CommandContext context,
        IReadOnlyList<object?> positionals,
        string[] skipNamedArgs)
    {
        var filtered = FilterNamedArguments(positionals, skipNamedArgs);

        if (filtered.Count > 0)
        {
            // If a single item is a collection, expand it
            if (filtered.Count == 1 && filtered[0] is System.Collections.IEnumerable enumerable and not string)
            {
                return enumerable.Cast<object?>().ToArray();
            }

            return filtered;
        }

        // Read from pipeline
        var items = new List<object?>();

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            items.Add(item);
        }

        return items;
    }

    private static IReadOnlyList<object?> FilterNamedArguments(IReadOnlyList<object?> positionals, string[] namedArgNames)
    {
        var result = new List<object?>();
        var skip = false;

        for (var i = 0; i < positionals.Count; i++)
        {
            if (skip)
            {
                skip = false;
                continue;
            }

            if (positionals[i] is string key &&
                namedArgNames.Any(name => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < positionals.Count)
            {
                skip = true;
                continue;
            }

            result.Add(positionals[i]);
        }

        return result;
    }

    private static async Task<(TuiScreen Screen, IReadOnlyList<object?> RemainingPositionals)> ReadScreenFromPipelineAsync(CommandContext context)
    {
        TuiScreen? screen = null;
        var remaining = new List<object?>();
        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        // First check if any positional is a TuiScreen
        foreach (var pos in parsed.Positionals)
        {
            if (pos is TuiScreen argScreen && screen is null)
            {
                screen = argScreen;
            }
            else
            {
                remaining.Add(pos);
            }
        }

        if (screen is not null)
        {
            return (screen, remaining);
        }

        // Read from pipeline
        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            if (item is TuiScreen pipeScreen && screen is null)
            {
                screen = pipeScreen;
            }
            else
            {
                remaining.Add(item);
            }
        }

        if (screen is null)
        {
            throw context.CreateDiagnostic(
                code: "tosh.tui.no_screen",
                title: "Expected a TuiScreen from pipeline input.",
                help: "Create a screen first: tui screen | tui add-list ...");
        }

        return (screen, remaining);
    }

    private static IReadOnlyList<object?> CollectPositionalItems(IReadOnlyList<object?> positionals)
    {
        if (positionals.Count == 1 && positionals[0] is System.Collections.IEnumerable enumerable and not string)
        {
            return enumerable.Cast<object?>().ToArray();
        }

        return positionals;
    }

    private static TuiLayout ParseLayoutOrientation(string value)
    {
        var normalized = value.Replace("-", string.Empty);

        if (Enum.TryParse<TuiLayout>(normalized, ignoreCase: true, out var layout))
        {
            return layout;
        }

        return value.ToLowerInvariant() switch
        {
            "horizontal" or "h" => TuiLayout.SplitHorizontal,
            "vertical" or "v" => TuiLayout.SplitVertical,
            "stack" => TuiLayout.Stacked,
            _ => throw new InvalidOperationException(
                $"Unknown layout orientation '{value}'. Options: single, split-horizontal, split-vertical, stacked"),
        };
    }

    private static IInlinePromptProvider RequireInlineProvider(CommandContext context)
    {
        return context.Runtime.InlinePrompts
            ?? throw context.CreateDiagnostic(
                code: "tosh.tui.no_inline_provider",
                title: "Inline prompts (--cli) are not available in this environment.",
                help: "The --cli flag requires an interactive terminal. Remove --cli to use fullscreen mode.");
    }

    private static bool MatchesFilter(string path, string filter)
    {
        var fileName = Path.GetFileName(path);

        if (filter.StartsWith('*'))
        {
            return fileName.EndsWith(filter[1..], StringComparison.OrdinalIgnoreCase);
        }

        return fileName.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}
