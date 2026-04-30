using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[CommandCategory("Shell")]
[CommandArgument("code ...", "Diagnostic codes (e.g. tosh.runtime.fading_member) to suppress.", TypeName = "string")]
[CommandExample("hush tosh.runtime.fading_member", Title = "Suppress a deprecation warning in the current scope")]
[CommandExample(
    "$tosh.Config.Diagnostics.Hushed.Add(\"tosh.naming.shadowed_builtin\")",
    Title = "Suppress globally from a profile.tosh entry")]
[CommandOutput("Emits nothing; mutates the current scope's hush set as a side effect.")]
public sealed class HushCommand : ShellCommand
{
    public HushCommand(string name = "hush")
        : base(
            name,
            "Suppresses one or more diagnostic codes within the current scope.",
            $"{name} <code> [code...]")
    {
    }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var evaluator = context.Runtime.Evaluator
            ?? throw new InvalidOperationException($"{Name} requires an active Tosh evaluator.");

        var codes = new List<string>();

        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);
        while (await enumerator.MoveNextAsync())
        {
            if (enumerator.Current is string piped && !string.IsNullOrWhiteSpace(piped))
            {
                codes.Add(piped.Trim());
            }
        }

        foreach (var argument in context.Arguments)
        {
            if (argument is string code && !string.IsNullOrWhiteSpace(code))
            {
                codes.Add(code.Trim());
            }
        }

        if (codes.Count == 0)
        {
            throw new InvalidOperationException(
                $"{Name} expects at least one diagnostic code (e.g. 'hush tosh.runtime.fading_member').");
        }

        foreach (var code in codes)
        {
            evaluator.HushDiagnosticCode(code);
        }

        yield break;
    }
}
