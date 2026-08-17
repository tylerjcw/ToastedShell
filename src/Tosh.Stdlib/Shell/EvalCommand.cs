using System.Text;
using Tosh.Runtime;

using Tosh.Language;

namespace Tosh.Stdlib.Shell;

[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Shell")]
[CommandArgument("source", "Tosh source text to parse and evaluate in the current session.")]
[CommandExample("eval \"1 + 2\"", Title = "Evaluate a literal expression")]
[CommandExample("eval $\"System.Drawing.Color.{$_}\"", Title = "Build and evaluate code at runtime")]
[CommandExample("read-lines colors.txt | each { eval $\"System.Drawing.Color.{$_}\" }", Title = "Resolve named members per line")]
[CommandOutput("Streams whatever values the evaluated source emits.")]
public sealed class EvalCommand : ShellCommand
{

    public EvalCommand()
        : base("eval", "Parses and evaluates a string as Tosh source in the current session.", "eval <source> [source...]")
    {
    }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException("The 'eval' command requires at least one source string.");
        }

        var sb = new StringBuilder();
        for (var i = 0; i < context.Arguments.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(context.Arguments[i]?.ToString());
        }

        var source = sb.ToString();

        await foreach (var value in RequireEvaluator(context).EvaluateAsync(source, "<eval>", context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            yield return value;
        }
    }

    /// <summary>
    /// The evaluator, reached through the runtime at execute time rather than taken at
    /// construction — which is what lets this command be registered before an engine
    /// exists (`TOAST-0006`). `eval` needs only `IShellEvaluator`; it does not run
    /// script *files*, so it does not need the script host.
    /// </summary>
    private static IShellEvaluator RequireEvaluator(CommandContext context)
        => context.Runtime.Evaluator
           ?? throw new InvalidOperationException(
               "This host has no evaluator, so there is nothing for 'eval' to evaluate.");

}
