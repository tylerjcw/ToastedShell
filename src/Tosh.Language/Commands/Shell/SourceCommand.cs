using Tosh.Core;

namespace Tosh.Language.Commands.Shell;

[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Shell")]
[CommandArgument("path", "Path to a .tosh script file to execute in the current session.", TypeName = "path-like", Kind = "path")]
[CommandArgument("args", "Arguments forwarded to the script.", Required = false)]
[CommandExample("source ~/.config/tosh/profile.tosh", Title = "Re-run your profile in the current session")]
[CommandExample("source ./setup.tosh prod 8080", Title = "Run a script with arguments")]
[CommandOutput("Streams whatever values the sourced script emits.")]
public sealed class SourceCommand : ShellCommand
{
    private readonly ToshEngine _engine;

    public SourceCommand(ToshEngine engine)
        : base("source", "Executes a Tosh script file in the current session and lets it affect the caller scope.", "source <path> [arg...]")
    {
        _engine = engine;
    }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException("The 'source' command requires at least one path.");
        }

        var rawPath = context.Arguments[0]?.ToString();

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new InvalidOperationException("The 'source' command requires a non-empty path.");
        }

        await foreach (var value in _engine.ExecuteScriptFileAsync(rawPath, context.Arguments.Skip(1).ToArray(), isolateScope: false, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            yield return value;
        }
    }
}
