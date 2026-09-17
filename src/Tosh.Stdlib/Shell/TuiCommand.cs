using Tosh.Tui;
using Tosh.Tui.Requests;
using Tosh.Tui.Widgets;

using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[CommandCategory("Shell")]
[CommandArgument("pick [items...]", "Pick one or more values from arguments or pipeline input.", Required = false)]
[CommandArgument("confirm <message>", "Ask for a yes/no confirmation.", Required = false)]
[CommandArgument("input [prompt]", "Read text input, optionally multiline or password-style.", Required = false)]
[CommandArgument("file", "Open a file or directory picker.", Required = false)]
[CommandArgument("filter [items...]", "Open a fuzzy filter picker.", Required = false)]
[CommandArgument("run <screen>", "Run a full-screen widget tree, written as a record or built with `new`.", Required = false)]
[CommandOption("--cli", "Use inline terminal prompts instead of returning fullscreen TUI request objects where supported.")]
[CommandOption("--multi, -m", "Allow multiple selections for `pick` and `filter`.")]
[CommandOption("--result", "Return a structured outcome object instead of only the selected value/result.")]
[CommandOption("--prompt <text>", "Prompt text for picker, filter and input.")]
[CommandOption("--display <property>", "Property name used as the display label for object items.")]
[CommandOption("--page-size <n>", "Number of visible entries in pick/filter lists.", Default = "10")]
[CommandOption("--default <value|yes|no>", "Default input value or confirmation default.")]
[CommandOption("--multiline", "Allow multiline input for `input`.")]
[CommandOption("--password", "Mask text for `input`.")]
[CommandOption("--path <start>", "Initial path for `file`.")]
[CommandOption("--filter <glob>", "File picker filter such as `*.tosh`.")]
[CommandOption("--directory, -d", "Choose directories instead of files for `file`.")]
[CommandOption("--fullscreen", "Run `filter` as a fullscreen picker with search open, instead of its inline default.")]
[CommandOption("--title <text>", "Screen title shown in the header bar, for `run`. A form names its own window.")]
[CommandOption("--refresh <duration>", "Redraw this often with no input, for `run`. Accepts 1s, 500ms or 00:00:01.")]
[CommandOption("--plain", "Render `run` once as text instead of taking over the terminal, for a script whose output is piped or saved.")]
[CommandOption("--width <columns>", "Columns to render `--plain` into. Defaults to the terminal's width, or 80.")]
[CommandOption("--height <rows>", "Rows to render `--plain` into. Defaults to the terminal's height, or 24.")]
[CommandOption("--budget <duration|off>", "How long a handler may hold the render loop, for `run`. A handler that outstays it is stopped and reported on the screen.", Default = "5s")]
[CommandExample("tui confirm \"Deploy now?\" --cli", Title = "Inline confirmation")]
[CommandExample("ls | tui pick --display Name --result", Title = "Pick from pipeline values")]
[CommandExample("tui input \"Project name:\" --default demo --cli", Title = "Inline text input")]
[CommandExample("tui run {| Form = [ {| Field = \"Name\", Id = \"name\" |} ], Title = \"New\" |}", Title = "Run a widget tree")]
[CommandOutput("The user's selection: picked item(s) for `pick`/`filter`, a bool for `confirm`, the text for `input`, the path for `file`, or a TuiScreenOutcome when `--result` is given. Nothing is emitted when the prompt is cancelled and `--result` was not asked for, and a screen showing a form emits nothing either — its handlers have already said what happened.")]
public sealed class TuiCommand : ShellCommand
{
    public TuiCommand()
        : base("tui",
            "Interactive TUI components: asking a question, and running a screen. Provides list pickers, confirmations, text input and file pickers, and runs a widget tree written as a record or built with `new`. Use --cli for inline (non-fullscreen) prompts. It needs a live terminal: run from a script whose output is a terminal, not from a pipeline or a redirect.",
            "tui pick|confirm|input|file|filter|run [options]")
    { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.tui.missing_subcommand",
                title: "The 'tui' command requires a subcommand.",
                help: "Available subcommands: pick, confirm, input, file, filter, run, reset");
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
            "run" => ExecuteRunAsync(context),
            "reset" => ExecuteResetAsync(context),
            _ => throw context.CreateDiagnostic(
                code: "tosh.tui.unknown_subcommand",
                title: $"Unknown tui subcommand '{subcommand}'.",
                argumentIndex: 0,
                help: "Available subcommands: pick, confirm, input, file, filter, run, reset"),
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
            foreach (var produced in RunOrYield(context, new TuiPickRequest(items, display, prompt, multi, returnOutcome)))
            {
                yield return produced;
            }
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
            foreach (var produced in RunOrYield(context, new TuiConfirmRequest(message, DefaultConfirm: defaultConfirm, ReturnOutcome: returnOutcome)))
            {
                yield return produced;
            }
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
            foreach (var produced in RunOrYield(context, new TuiInputRequest(prompt, defaultValue, multiline, returnOutcome, password)))
            {
                yield return produced;
            }
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
            foreach (var produced in RunOrYield(context, new TuiFilePickRequest(initialPath, filter, directoryOnly, returnOutcome)))
            {
                yield return produced;
            }
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
            foreach (var produced in RunOrYield(context, new TuiPickRequest(items, display, prompt, multi, returnOutcome, StartInSearch: true)))
            {
                yield return produced;
            }
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

    // ── tui run ───────────────────────────────────────────────
    // Usage: tui run <tree> [--title <text>] [--refresh <duration>] [--budget <duration|off>] [--result]
    // Or:    <tree> | tui run
    private static async IAsyncEnumerable<object?> ExecuteRunAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(NormalizeOptionSyntax(context.Arguments));
        RejectUnknownFlags(context, parsed, "run", "result", "plain");

        // One frame as text, for a destination that is not a terminal. Asked for rather
        // than inferred: a script whose output happens to be redirected today should not
        // silently change what it does (`TUI-0009`).
        var plain = parsed.HasFlag("plain");

        var tree = FindTree(parsed.Positionals);

        if (tree is null)
        {
            await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
            {
                if (IsTree(item))
                {
                    tree = item;
                    break;
                }
            }
        }

        if (tree is null)
        {
            throw context.CreateDiagnostic(
                code: "tosh.tui.run.no_screen",
                title: "No screen was given to 'tui run'.",
                help: "Pass a widget tree as an argument or pipe one in: "
                      + "tui run {| Column = [ \"hello\" ] |}");
        }

        foreach (var produced in RunOrYield(context, new TuiTreeRunRequest(
            tree,
            parsed.HasFlag("result"),
            BuildArgumentInvoker(context, ParseBudget(ExtractNamedArgument(parsed.Positionals, "budget"))),
            ParseInterval(ExtractNamedArgument(parsed.Positionals, "refresh")),
            ExtractNamedArgument(parsed.Positionals, "title"),
            plain,
            ParseSize(ExtractNamedArgument(parsed.Positionals, "width")),
            ParseSize(ExtractNamedArgument(parsed.Positionals, "height"))),
            needsTerminal: !plain))
        {
            yield return produced;
        }
    }

    /// <summary>
    /// Puts the terminal back after something left it in full-screen mode.
    /// </summary>
    /// <remarks>
    /// A session restores on every path it can reach, including the signals a process can
    /// be asked to die from — but not <c>SIGKILL</c>, and not a machine that lost power.
    /// TōSh repairs that by itself the next time it starts on the same terminal; this is
    /// for the reader who can already see that theirs is wrong and should not have to
    /// convince anything of it (<c>TUI-0010</c>).
    /// </remarks>
    private static async IAsyncEnumerable<object?> ExecuteResetAsync(CommandContext context)
    {
        RejectUnknownFlags(context, ParsedCommandArguments.Parse(context.Arguments), "reset");

        // Not through `RunOrYield`: that refuses when there is no terminal to draw a screen
        // on, and this is the one subcommand you would reach for *because* the terminal is
        // in a state nothing can draw on. It needs a file descriptor, not a screen.
        var runner = context.Shell().TuiScreens;

        foreach (var produced in runner is not null && runner.TryRun(new TuiResetRequest(), out var results)
            ? results ?? []
            : [new TuiResetRequest()])
        {
            yield return produced;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Whether a value is something the widget builder can make a screen out of.
    /// </summary>
    /// <remarks>
    /// A widget built with <c>new</c>, a record tree written as a literal, and a tree
    /// loaded from a file of its own are the same tree by the time anything draws them.
    /// The option values a caller wrote — <c>--title "x"</c> reaches here as a positional
    /// pair — are not, which is why the search is for a shape rather than for the first
    /// argument.
    /// </remarks>
    private static bool IsTree(object? value)
        => value is TuiWidget ||
           value is IDictionary<string, object?> ||
           (value is not null && ShellRecordUtilities.IsRecordLike(value));

    private static object? FindTree(IReadOnlyList<object?> positionals)
        => positionals.FirstOrDefault(IsTree);

    /// <summary>
    /// Reads a refresh interval, in the spellings a script would write.
    /// </summary>
    /// <remarks>
    /// A duration literal like <c>1s</c> reaches a command as its text rather than as a
    /// <see cref="TimeSpan"/>, and <c>TimeSpan.TryParse("1s")</c> fails — it wants
    /// <c>"00:00:01"</c>. Accepting both, plus <c>ms</c>, <c>m</c> and <c>h</c>, costs a
    /// dozen lines and saves every caller from discovering that.
    /// </remarks>
    private static TimeSpan? ParseInterval(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();

        if (TimeSpan.TryParse(trimmed, out var parsed))
        {
            return parsed;
        }

        foreach (var (suffix, scale) in ((string Suffix, double Scale)[])
                 [("ms", 1), ("s", 1000), ("m", 60_000), ("h", 3_600_000)])
        {
            if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var number = trimmed[..^suffix.Length].Trim();

            if (double.TryParse(number, out var value))
            {
                return TimeSpan.FromMilliseconds(value * scale);
            }
        }

        return double.TryParse(trimmed, out var seconds) ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>
    /// Wraps a script function so the TUI can call it with one argument and read what it
    /// produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pull binding is a function of the screen's state: it is handed the form's current
    /// values and returns what to show.
    /// </para>
    /// <para>
    /// Everything it produces is the answer, not the last of it. A function returning a
    /// collection yields its elements one at a time, so keeping only the last was keeping
    /// only the last <em>row</em> — which silently broke every binding that carries more
    /// than one thing: <c>Lines</c>, <c>List</c>, <c>Table</c>, <c>Spark</c> and
    /// <c>Bars</c> all showed exactly their final element. One value is still that value,
    /// so a caption or a title reads exactly as it did.
    /// </para>
    /// <para>
    /// Every one of these runs on the render loop, which is the thread that answers keys —
    /// so a handler that never returns is a terminal that never comes back
    /// (<c>TUI-0008</c>). Each call carries a deadline, and a handler that outstays it is
    /// cancelled and reported on the screen's banner like any other failure.
    /// </para>
    /// <para>
    /// What that catches is a script that loops, waits or polls forever, because the
    /// interpreter checks the token as it goes. What it does not catch is a handler blocked
    /// inside a call that cannot be cancelled — a read on a pipe nobody writes to. The
    /// alternative is to run handlers on a thread and abandon them, which trades a stuck
    /// screen for two threads writing the same widget, and a torn screen is worse than a
    /// stopped one.
    /// </para>
    /// </remarks>
    private static Func<IShellCallable, object?, object?> BuildArgumentInvoker(
        CommandContext context,
        TimeSpan? budget)
        => (callable, argument) =>
        {
            // A binding that does not need the form's values should not have to declare a
            // parameter it ignores, so the argument is offered only to something that can
            // take one.
            var wantsArgument = argument is not null &&
                (callable.MaximumParameterCount is null || callable.MaximumParameterCount > 0);

            // Linked rather than standalone, so Ctrl+C still reaches a handler that is
            // halfway through something slow.
            var deadline = budget is { } limit
                ? CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken)
                : null;

            deadline?.CancelAfter(budget!.Value);

            try
            {
                var inner = context with
                {
                    Arguments = wantsArgument ? [argument] : [],
                    Input = AsyncEnumerableExtensions.Empty<object?>(),
                    IsPipelined = false,
                    CancellationToken = deadline?.Token ?? context.CancellationToken,
                };

                return Collect(callable, inner);
            }
            catch (OperationCanceledException) when (
                deadline is { IsCancellationRequested: true } &&
                !context.CancellationToken.IsCancellationRequested)
            {
                // Translated rather than rethrown: a screen reports what it catches by
                // type and message, and "TaskCanceledException: A task was canceled"
                // names neither the handler nor the reason it stopped.
                throw new TimeoutException(
                    $"a screen handler ran for longer than {Describe(budget!.Value)} and was "
                    + "stopped. Do the slow part in a job and let the screen read the result, "
                    + "or pass --budget off.");
            }
            finally
            {
                deadline?.Dispose();
            }
        };

    /// <summary>Reads everything a call produced, in order.</summary>
    private static object? Collect(IShellCallable callable, CommandContext inner)
    {
        object? single = null;
        List<object?>? produced = null;
        var count = 0;

        var enumerator = callable.InvokeAsync(inner).GetAsyncEnumerator(inner.CancellationToken);

        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                if (count == 0)
                {
                    single = enumerator.Current;
                }
                else
                {
                    // Only allocated once a second value shows up, because almost every
                    // binding on almost every screen produces exactly one.
                    produced ??= [single];
                    produced.Add(enumerator.Current);
                }

                count += 1;
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return produced is null ? single : produced.ToArray();
    }

    /// <summary>How long a budget is, in the spelling a script would have written.</summary>
    private static string Describe(TimeSpan budget)
        => budget.TotalMilliseconds < 1000
            ? $"{budget.TotalMilliseconds:0.##}ms"
            : $"{budget.TotalSeconds:0.##}s";

    /// <summary>The longest a handler may hold the render loop, unless a script says otherwise.</summary>
    /// <remarks>
    /// This is a hang breaker rather than a latency budget. A binding that takes five
    /// seconds has already made the screen useless; what this is for is the one that never
    /// comes back at all, which without it takes the terminal with it.
    /// </remarks>
    private static readonly TimeSpan DefaultHandlerBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reads <c>--budget</c>: a duration, or <c>off</c> for a screen that means to be slow.
    /// </summary>
    /// <remarks>
    /// An unreadable value falls back to the default rather than to <c>off</c>. The two
    /// mistakes are not equal — a typo that silently removes the guard is found much later
    /// than one that silently keeps it.
    /// </remarks>
    private static TimeSpan? ParseBudget(string? text)
    {
        if (text is null)
        {
            return DefaultHandlerBudget;
        }

        var trimmed = text.Trim();

        if (trimmed.Length == 0 ||
            string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return ParseInterval(trimmed) switch
        {
            { } span when span > TimeSpan.Zero => span,
            { } => null,
            null => DefaultHandlerBudget,
        };
    }

    /// <summary>
    /// Runs a screen and yields its result, or yields the request for a display sink to
    /// pick up when no terminal is available.
    /// </summary>
    /// <remarks>
    /// Running here is what lets a result be assigned, passed to a function, or used in
    /// a condition. Yielding a request only works when the value happens to flow to the
    /// display, which is why `var answer = tui confirm "..."` used to store a
    /// `TuiConfirmRequest` and show no dialog (`TUI-0013`).
    ///
    /// The fallback is kept rather than removed: with no runner — a headless process, a
    /// test host — behaviour is exactly what it was.
    /// </remarks>
    /// <param name="needsTerminal">
    /// Whether this request has to be drawn on something. False for a render that answers
    /// with text, which is the whole point of having one.
    /// </param>
    private static IEnumerable<object?> RunOrYield(
        CommandContext context,
        object request,
        bool needsTerminal = true)
    {
        if (context.Shell().TuiScreens is not { } runner)
        {
            // No host that can run screens — a headless or test process. Leave the
            // request for a display sink, which is what happened before there was a
            // runner at all.
            return [request];
        }

        if (needsTerminal && !runner.CanRun)
        {
            // Yielding the request here is worse than failing: it lands in whatever the
            // caller assigned it to, and the next thing they touch reports that
            // `Cancelled` is not a member of `TuiRunRequest`, which says nothing about
            // the actual problem.
            throw context.CreateDiagnostic(
                code: "tosh.tui.no_terminal",
                title: "A full-screen TUI needs a terminal.",
                help: "Output is redirected, so there is nothing to draw on. Run this "
                    + "from a terminal, or use --cli for an inline prompt where the "
                    + "subcommand supports one.");
        }

        return runner.TryRun(request, out var results) ? results ?? [] : [request];
    }

    // ── Helpers ───────────────────────────────────────────────

    /// <summary>
    /// The names of `tui` options that take a value.
    /// </summary>
    private static readonly HashSet<string> ValueOptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "prompt", "display", "page-size", "default", "path", "filter",
        "id", "bind", "ratio", "gap", "title", "refresh", "budget", "width", "height",
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

    /// <summary>Reads a column or row count, ignoring anything that is not one.</summary>
    private static int? ParseSize(string? text)
        => int.TryParse(text, out var value) && value > 0 ? value : null;

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

        // A pipeline carrying one collection is expanded, exactly as a single collection
        // argument is. Without this, `["a", "b"] | tui pick` offers one choice reading
        // "System.String[]": a list is a single pipeline value, so it arrives whole, and
        // only the argument path had the rule.
        if (items.Count == 1 && items[0] is System.Collections.IEnumerable pipedCollection and not string)
        {
            return pipedCollection.Cast<object?>().ToArray();
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
