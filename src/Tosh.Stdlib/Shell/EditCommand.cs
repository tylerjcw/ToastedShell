using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[CommandCategory("Shell")]
[CommandArgument("file", "Path of the file to edit.", Required = false)]
[CommandExample("edit ~/.config/tosh/profile.tosh", Title = "Open a file in the configured editor")]
[CommandExample("edit notes.md", Title = "Open a local file")]
[CommandNote("Resolves the editor from $EDITOR, $VISUAL, then falls back to `tome`, then `vi`, then `nano`. The child process inherits the terminal, so it can run a full-screen UI.")]
[CommandOutput("No output — the editor takes over the terminal until it exits.")]
public sealed class EditCommand : ShellCommand
{
    private static readonly string[] FallbackEditors = ["tome", "vi", "nano"];

    public EditCommand()
        : base("edit", "Open a file in the configured terminal editor.", "edit [file]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.IsPipelined)
        {
            throw context.CreateDiagnostic(
                code: "tosh.edit.pipeline_unsupported",
                title: "`edit` cannot run inside a pipeline.",
                label: "this `edit` is part of a pipeline",
                help: "Run `edit <file>` on its own — the editor needs full terminal control.");
        }

        var (editorName, editorPath) = ResolveEditor(context);

        var external = new ExternalProcessCommand(editorName, editorPath);
        await foreach (var item in external.ExecuteAsync(context))
        {
            yield return item;
        }
    }

    private static (string Name, string Path) ResolveEditor(CommandContext context)
    {
        foreach (var envVar in new[] { "EDITOR", "VISUAL" })
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrWhiteSpace(value)) continue;

            var lookup = ExternalCommandResolver.Resolve(context.Shell().CurrentDirectory, value.Trim());
            if (lookup.Status == ExternalCommandLookupStatus.Found && lookup.ResolvedPath is not null)
                return (lookup.Name, lookup.ResolvedPath);
        }

        foreach (var fallback in FallbackEditors)
        {
            var lookup = ExternalCommandResolver.Resolve(context.Shell().CurrentDirectory, fallback);
            if (lookup.Status == ExternalCommandLookupStatus.Found && lookup.ResolvedPath is not null)
                return (lookup.Name, lookup.ResolvedPath);
        }

        throw context.CreateDiagnostic(
            code: "tosh.edit.no_editor",
            title: "No terminal editor is available.",
            label: "could not locate an editor on PATH",
            help: "Set $EDITOR to your preferred editor, install `tome`, or install `vi`/`nano`.");
    }
}
