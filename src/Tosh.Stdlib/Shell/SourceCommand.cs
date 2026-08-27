using Tosh.Runtime;

using Tosh.Language;

namespace Tosh.Stdlib.Shell;

[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Shell")]
[CommandArgument("path", "Path to a .tosh script file to execute in the current session.", TypeName = "path-like", Kind = "path")]
[CommandArgument("args", "Arguments forwarded to the script.", Required = false)]
[CommandExample("source ~/.config/tosh/profile.tosh", Title = "Re-run your profile in the current session")]
[CommandExample("source ./setup.tosh prod 8080", Title = "Run a script with arguments")]
[CommandOutput("Streams whatever values the sourced script emits.")]
public sealed class SourceCommand : ShellCommand
{

    public SourceCommand()
        : base("source", "Executes a Tosh script file in the current session and lets it affect the caller scope.", "source <path> [arg...]")
    {
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

        // `TS-P2-29`. A relative path means "beside the script doing the sourcing", as it already
        // does for `require`; the working directory stays as the fallback.
        var path = RequireHost(context).ResolveSourcePath(rawPath);

        await foreach (var value in RequireHost(context).ExecuteScriptFileAsync(path, context.Arguments.Skip(1).ToArray(), isolateScope: false, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            yield return value;
        }
    }

    /// <summary>
    /// The engine, reached through the runtime at execute time rather than taken at
    /// construction — which is what lets this command be registered before an engine
    /// exists (`TOAST-0006`).
    /// </summary>
    private static IToshScriptHost RequireHost(CommandContext context)
        => context.Shell().Evaluator as IToshScriptHost
           ?? throw new InvalidOperationException(
               "This host cannot run scripts. Register a ToastScript engine on the runtime " +
               "before using script-running commands.");

}
