using Tosh.Tui;
using Tosh.Tui.Requests;
using Tosh.Tui.Widgets;

using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[ShellOnly]
[CommandCategory("Shell")]
[CommandArgument("pick [items...]", "Pick one or more values from arguments or pipeline input.", Required = false)]
[CommandArgument("confirm <message>", "Ask for a yes/no confirmation.", Required = false)]
[CommandArgument("input [prompt]", "Read text input, optionally multiline or password-style.", Required = false)]
[CommandArgument("file", "Open a file or directory picker.", Required = false)]
[CommandArgument("filter [items...]", "Open a fuzzy filter picker.", Required = false)]
[CommandArgument("screen|add-*|layout|run", "Build and run composed TUI screens from pipeline-carried screen definitions. Widgets: add-list, add-text, add-input, add-picker, add-confirm, add-file.", Required = false)]
[CommandOption("--cli", "Use inline terminal prompts instead of returning fullscreen TUI request objects where supported.")]
[CommandOption("--multi, -m", "Allow multiple selections for `pick`, `filter`, and list widgets.")]
[CommandOption("--result", "Return a structured outcome object instead of only the selected value/result.")]
[CommandOption("--prompt <text>", "Prompt text for picker, filter, input, and widget subcommands.")]
[CommandOption("--display <property>", "Property name used as the display label for object items.")]
[CommandOption("--page-size <n>", "Number of visible entries in pick/filter lists.", Default = "10")]
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
[CommandOption("--fullscreen", "Run `filter` as a fullscreen picker with search open, instead of its inline default.")]
[CommandOption("--ratio <a:b>", "Layout split ratio for `layout`.")]
[CommandOption("--gap <n>", "Gap between layout regions.")]
[CommandExample("tui confirm \"Deploy now?\" --cli", Title = "Inline confirmation")]
[CommandExample("ls | tui pick --display Name --result", Title = "Pick from pipeline values")]
[CommandExample("tui input \"Project name:\" --default demo --cli", Title = "Inline text input")]
[CommandExample("tui screen --title Deploy | tui add-confirm \"Deploy now?\" | tui run --result", Title = "Composed screen with a confirmation")]
[CommandOutput("The user's selection: picked item(s) for `pick`/`filter`, a bool for `confirm`, the text for `input`, the path for `file`, or a TuiScreenOutcome when `--result` is given. Nothing is emitted when the prompt is cancelled and `--result` was not asked for. The screen-builder subcommands emit the TuiScreen being composed; without `--cli`, the modal subcommands emit a request object for the shell host to run.")]
public sealed class TuiCommand : ShellCommand
{
    public TuiCommand()
        : base("tui",
            "Interactive TUI components for an interactive shell session. Provides list pickers, confirmations, text input, file pickers, and composed screens. Use --cli for inline (non-fullscreen) prompts. This command is shell-only: it needs a live terminal, so it cannot be used from a script run non-interactively.",
            "tui pick|confirm|input|file|filter|screen|add-list|add-text|add-input|add-picker|add-confirm|add-file|layout|run [options]")
    { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.tui.missing_subcommand",
                title: "The 'tui' command requires a subcommand.",
                help: "Available subcommands: pick, confirm, input, file, filter, screen, add-list, add-text, add-input, add-picker, add-confirm, add-file, layout, run");
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
            "add-confirm" => ExecuteAddConfirmAsync(context),
            "add-file" => ExecuteAddFileAsync(context),
            "layout" => ExecuteLayoutAsync(context),
            "run" => ExecuteRunAsync(context),
            _ => throw context.CreateDiagnostic(
                code: "tosh.tui.unknown_subcommand",
                title: $"Unknown tui subcommand '{subcommand}'.",
                argumentIndex: 0,
                help: "Available subcommands: pick, confirm, input, file, filter, screen, add-list, add-text, add-input, add-picker, add-confirm, add-file, layout, run"),
        };
    }

    // ── tui pick ──────────────────────────────────────────────
    // Usage: tui pick [items...] [--multi] [--prompt "text"] [--display <property>] [--result] [--cli]
    // Or: <pipeline> | tui pick [--multi] [--prompt "text"] [--display <property>] [--result] [--cli]
    private static async IAsyncEnumerable<object?> ExecutePickAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "pick", "multi", "m", "result", "cli");
        var multi = parsed.HasFlag("multi", "m");
        var returnOutcome = parsed.HasFlag("result");
        var cli = parsed.HasFlag("cli");
        var prompt = ExtractNamedArgument(parsed.Positionals, "prompt");
        var display = ExtractNamedArgument(parsed.Positionals, "display");
        var pageSize = ReadPageSize(context, parsed.Positionals);

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

            if (returnOutcome)
            {
                yield return InlineOutcome(result);
            }
            else if (result is not null)
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

        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "confirm", "result", "cli");
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

            yield return returnOutcome
                ? InlineOutcome(result is null ? null : new object?[] { result.Value }, "confirmed", result ?? false)
                : result ?? false;
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

        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "input", "multiline", "password", "result", "cli");
        var multiline = parsed.HasFlag("multiline");
        var returnOutcome = parsed.HasFlag("result");
        var cli = parsed.HasFlag("cli");
        var password = parsed.HasFlag("password");

        var prompt = parsed.Positionals.Count > 0
            ? parsed.Positionals[0]?.ToString()
            : null;

        var defaultValue = ExtractNamedArgument(parsed.Positionals, "default");

        if (cli && multiline)
        {
            throw context.CreateDiagnostic(
                code: "tosh.tui.input.multiline_requires_fullscreen",
                title: "'tui input --multiline' cannot be combined with '--cli'.",
                help: "The inline prompt is a single-row box. Drop --cli for a multiline field, "
                    + "or drop --multiline for an inline single-line prompt.");
        }

        if (cli)
        {
            var provider = RequireInlineProvider(context);
            var result = provider.Input(prompt, defaultValue, password, multiline);

            if (returnOutcome)
            {
                yield return InlineOutcome(result is null ? null : new object?[] { result }, "text", result);
            }
            else if (result is not null)
            {
                yield return result;
            }
        }
        else
        {
            yield return new TuiInputRequest(prompt, defaultValue, multiline, returnOutcome, password);
        }
    }

    // ── tui file ──────────────────────────────────────────────
    // Usage: tui file [--path <start>] [--filter "*.tosh"] [--directory] [--result] [--cli]
    private static async IAsyncEnumerable<object?> ExecuteFileAsync(CommandContext context)
    {
        await Task.CompletedTask;

        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "file", "directory", "d", "result", "cli");
        var directoryOnly = parsed.HasFlag("directory", "d");
        var returnOutcome = parsed.HasFlag("result");
        var cli = parsed.HasFlag("cli");
        var initialPath = ExtractNamedArgument(parsed.Positionals, "path");
        var filter = ExtractNamedArgument(parsed.Positionals, "filter");

        if (cli)
        {
            var provider = RequireInlineProvider(context);
            var chosen = PickPathInline(
                context,
                provider,
                initialPath ?? context.Shell().CurrentDirectory,
                filter,
                directoryOnly);

            if (returnOutcome)
            {
                yield return InlineOutcome(chosen is null ? null : new object?[] { chosen }, "path", chosen);
            }
            else if (chosen is not null)
            {
                yield return chosen;
            }
        }
        else
        {
            yield return new TuiFilePickRequest(initialPath, filter, directoryOnly, returnOutcome);
        }
    }

    /// <summary>
    /// Walks directories inline using the ordinary <see cref="IInlinePromptProvider.Pick"/>
    /// prompt, so `--cli` gets the navigation the fullscreen picker has instead of a flat
    /// listing of one directory that the caller can never leave. Composed from Pick rather
    /// than drawing its own screen, which keeps it testable without a terminal.
    /// </summary>
    private static string? PickPathInline(
        CommandContext context,
        IInlinePromptProvider provider,
        string startPath,
        string? filter,
        bool directoryOnly)
    {
        const string SelectCurrent = ".";
        const string GoUp = "..";

        var current = Path.GetFullPath(startPath);

        while (true)
        {
            var directories = ReadDirectoryEntries(context, current, directoriesOnly: true);
            var entries = new List<object?>();

            // A directory-only pick needs a way to choose where it already is; otherwise
            // descending into the target would be the only way to reach it, and the target
            // itself could never be returned.
            if (directoryOnly)
            {
                entries.Add(SelectCurrent);
            }

            if (Directory.GetParent(current) is not null)
            {
                entries.Add(GoUp);
            }

            entries.AddRange(directories.Select(d => (object?)(Path.GetFileName(d) + Path.DirectorySeparatorChar)));

            if (!directoryOnly)
            {
                entries.AddRange(ReadDirectoryEntries(context, current, directoriesOnly: false)
                    .Where(e => filter is null || MatchesFilter(e, filter))
                    .Where(e => !Directory.Exists(e))
                    .Select(e => (object?)Path.GetFileName(e)));
            }

            var prompt = directoryOnly ? $"Select directory ({current}):" : $"Select file ({current}):";
            var result = provider.Pick(entries, prompt);

            if (result is not { Count: > 0 } || result[0] is not string selected)
            {
                return null;
            }

            if (selected == SelectCurrent)
            {
                return current;
            }

            if (selected == GoUp)
            {
                current = Directory.GetParent(current)!.FullName;
                continue;
            }

            var candidate = Path.Combine(current, selected.TrimEnd(Path.DirectorySeparatorChar));

            if (Directory.Exists(candidate))
            {
                current = candidate;
                continue;
            }

            return candidate;
        }
    }

    /// <summary>
    /// Reads a directory, turning the filesystem's own exceptions into a tosh diagnostic.
    /// Unguarded, an unreadable or missing path surfaced a raw .NET exception from inside
    /// an interactive prompt.
    /// </summary>
    private static string[] ReadDirectoryEntries(CommandContext context, string path, bool directoriesOnly)
    {
        try
        {
            return directoriesOnly
                ? Directory.GetDirectories(path)
                : Directory.GetFileSystemEntries(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw context.CreateDiagnostic(
                code: "tosh.tui.file.unreadable_directory",
                title: $"Could not read directory '{path}'.",
                help: error.Message);
        }
    }

    // ── tui filter ────────────────────────────────────────────
    // Usage: tui filter [items...] [--multi] [--prompt "text"] [--display <property>] [--page-size <n>]
    // Or: <pipeline> | tui filter [--multi] [--prompt "text"] [--display <property>]
    // Always inline (no fullscreen equivalent).
    private static async IAsyncEnumerable<object?> ExecuteFilterAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "filter", "multi", "m", "result", "cli", "fullscreen");
        var multi = parsed.HasFlag("multi", "m");
        var returnOutcome = parsed.HasFlag("result");
        var prompt = ExtractNamedArgument(parsed.Positionals, "prompt");
        var display = ExtractNamedArgument(parsed.Positionals, "display");
        var pageSize = ReadPageSize(context, parsed.Positionals);

        var items = await CollectItemsAsync(context, parsed.Positionals, skipNamedArgs: new[] { "prompt", "display", "page-size" });

        if (items.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.tui.filter.no_items",
                title: "No items provided for 'tui filter'.",
                help: "Pipe items into 'tui filter' or provide them as arguments.");
        }

        // Filter had no fullscreen form at all, so a script could not choose the
        // presentation every other modal subcommand offers. It is opt-in rather than the
        // default because inline *is* filter's established contract — seven tests pin it,
        // including one asserting filter requires the inline provider — and flipping it
        // would silently change what existing scripts do. The fullscreen form is the
        // picker with its search bar already open, not a second screen.
        if (parsed.HasFlag("fullscreen"))
        {
            yield return new TuiPickRequest(items, display, prompt, multi, returnOutcome, StartInSearch: true);
            yield break;
        }

        var provider = RequireInlineProvider(context);
        var result = provider.Filter(items, prompt, display, multi, pageSize);

        if (returnOutcome)
        {
            yield return InlineOutcome(result);
        }
        else if (result is not null)
        {
            foreach (var item in result)
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// Builds the same <see cref="TuiScreenOutcome"/> the fullscreen screens return, so
    /// `--result` means one thing in both modes. A null selection is the inline providers'
    /// signal for "cancelled" — without this the two are indistinguishable inline, which
    /// is what made `tui confirm --cli` report Escape as a deliberate "no".
    /// </summary>
    private static TuiScreenOutcome InlineOutcome(
        IReadOnlyList<object?>? selected,
        string? valueKey = null,
        object? value = null)
    {
        return new TuiScreenOutcome
        {
            Selected = selected ?? Array.Empty<object?>(),
            Cancelled = selected is null,
            Values = valueKey is null || selected is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { [valueKey] = value },
        };
    }

    // ── tui screen ────────────────────────────────────────────
    // Usage: tui screen [--title "text"]
    // Creates and yields a new TuiScreen for pipeline building
    private static async IAsyncEnumerable<object?> ExecuteScreenAsync(CommandContext context)
    {
        await Task.CompletedTask;

        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "screen");
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
        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "add-list", "searchable", "s", "multi", "m");
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
        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "add-text", "no-wrap");
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
        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "add-input", "multiline");
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

    // ── tui add-confirm ───────────────────────────────────────
    // Usage: <screen> | tui add-confirm <message> [--id <id>] [--default yes|no]
    //
    // `TuiCustomScreen` has rendered a ConfirmationWidgetHost since it was written; only
    // the command to put one on a screen was missing, so a composed screen could not ask
    // a yes/no question without dropping out to the standalone `tui confirm`.
    private static async IAsyncEnumerable<object?> ExecuteAddConfirmAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "add-confirm");
        var id = ExtractNamedArgument(parsed.Positionals, "id") ?? $"confirm-{Guid.NewGuid():N}"[..14];
        var defaultValue = ExtractNamedArgument(parsed.Positionals, "default");

        var (screen, remainingPositionals) = await ReadScreenFromPipelineAsync(context);
        var remaining = FilterNamedArguments(remainingPositionals, new[] { "id", "default" });
        var message = remaining.Count > 0 ? remaining[0]?.ToString() : null;

        if (string.IsNullOrWhiteSpace(message))
        {
            throw context.CreateDiagnostic(
                code: "tosh.tui.add_confirm.missing_message",
                title: "'tui add-confirm' requires a message.",
                help: "Write the question to ask: tui add-confirm \"Deploy now?\"");
        }

        var widget = new TuiConfirmationConfig(id, message)
        {
            DefaultConfirm = !(string.Equals(defaultValue, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(defaultValue, "false", StringComparison.OrdinalIgnoreCase)),
        };

        screen.AddWidget(widget);
        yield return screen;
    }

    // ── tui add-file ──────────────────────────────────────────
    // Usage: <screen> | tui add-file [--id <id>] [--path <start>] [--filter "*.tosh"] [--directory]
    //
    // The FilePickerWidgetHost counterpart of `add-confirm` above: rendered all along,
    // unreachable from the command surface.
    private static async IAsyncEnumerable<object?> ExecuteAddFileAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "add-file", "directory", "d");
        var directoryOnly = parsed.HasFlag("directory", "d");
        var id = ExtractNamedArgument(parsed.Positionals, "id") ?? $"file-{Guid.NewGuid():N}"[..11];
        var initialPath = ExtractNamedArgument(parsed.Positionals, "path");
        var filter = ExtractNamedArgument(parsed.Positionals, "filter");

        var (screen, _) = await ReadScreenFromPipelineAsync(context);

        var widget = new TuiFilePickerConfig(id)
        {
            InitialPath = initialPath,
            Filter = filter,
            DirectoryOnly = directoryOnly,
        };

        screen.AddWidget(widget);
        yield return screen;
    }

    // ── tui add-picker ────────────────────────────────────────
    // Usage: <screen> | tui add-picker <items-var> [--id <id>] [--display <prop>] [--prompt "text"]
    private static async IAsyncEnumerable<object?> ExecuteAddPickerAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "add-picker");
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
        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "layout");
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
        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "run", "result");
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

    /// <summary>
    /// The names of `tui` options that take a value.
    /// </summary>
    private static readonly HashSet<string> ValueOptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "prompt", "display", "page-size", "default", "path", "filter",
        "id", "bind", "ratio", "gap", "title",
    };

    /// <summary>
    /// Rewrites <c>--name value</c> into <c>name value</c> before the ordinary argument
    /// parse sees it.
    /// </summary>
    /// <remarks>
    /// <see cref="ParsedCommandArguments"/> routes anything starting with <c>--</c> into
    /// its flag set, which orphaned the value in the positionals: every documented
    /// <c>--name &lt;value&gt;</c> option silently did nothing, and its value was left
    /// behind as data. <c>tui pick x y --prompt "Choose:"</c> showed no prompt and offered
    /// <c>Choose:</c> as a third item. Normalising here keeps one downstream code path and
    /// leaves the undashed spelling — which the tests and existing scripts use — working.
    /// </remarks>
    private static IReadOnlyList<object?> NormalizeOptionSyntax(IReadOnlyList<object?> arguments)
    {
        List<object?>? rewritten = null;

        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is not string text)
            {
                rewritten?.Add(arguments[i]);
                continue;
            }

            // `--` ends option parsing; everything after it is data.
            if (text == "--")
            {
                rewritten ??= new List<object?>(arguments.Take(i));

                for (var rest = i; rest < arguments.Count; rest++)
                {
                    rewritten.Add(arguments[rest]);
                }

                return rewritten;
            }

            if (text.Length > 2 &&
                text.StartsWith("--", StringComparison.Ordinal) &&
                ValueOptionNames.Contains(text[2..]) &&
                i + 1 < arguments.Count)
            {
                rewritten ??= new List<object?>(arguments.Take(i));
                rewritten.Add(text[2..]);
                continue;
            }

            rewritten?.Add(arguments[i]);
        }

        return rewritten ?? arguments;
    }

    /// <summary>
    /// Rejects a flag the subcommand does not accept, so a typo is reported rather than
    /// silently changing what the prompt does. <see cref="ParsedCommandArguments"/> keeps
    /// every flag it sees without checking any of them, so `tui pick --muti` quietly
    /// single-selected and `--cli` on a fullscreen-only subcommand quietly did nothing.
    /// Value-taking options are already normalised into positionals by
    /// <see cref="NormalizeOptionSyntax"/>, so anything still here is meant to be boolean.
    /// </summary>
    private static void RejectUnknownFlags(
        CommandContext context,
        ParsedCommandArguments parsed,
        string subcommand,
        params string[] allowed)
    {
        foreach (var flag in parsed.Flags)
        {
            if (allowed.Contains(flag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var known = allowed.Length == 0
                ? $"'tui {subcommand}' takes no flags."
                : $"'tui {subcommand}' accepts: {string.Join(", ", allowed.Where(a => a.Length > 1).Select(a => "--" + a))}.";

            throw context.CreateDiagnostic(
                code: "tosh.tui.unknown_flag",
                title: $"Unknown flag '--{flag}' for 'tui {subcommand}'.",
                help: known);
        }
    }

    /// <summary>
    /// Reads <c>--page-size</c>, refusing a value that is not a positive number rather than
    /// silently falling back to the default and showing a differently sized list.
    /// </summary>
    private static int ReadPageSize(CommandContext context, IReadOnlyList<object?> positionals)
    {
        var text = ExtractNamedArgument(positionals, "page-size");

        if (text is null)
        {
            return 10;
        }

        if (!int.TryParse(text, out var pageSize) || pageSize <= 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.tui.invalid_page_size",
                title: $"'--page-size' needs a positive whole number, not '{text}'.",
                help: "For example: tui pick $items --page-size 20");
        }

        return pageSize;
    }

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
        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));

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
        return context.Shell().InlinePrompts
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
