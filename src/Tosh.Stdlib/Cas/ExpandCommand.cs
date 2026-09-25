using Tosh.Runtime;

namespace Tosh.Stdlib.Cas;

[CommandCategory("CAS")]
[CommandArgument("expr", "Expression to expand.", Required = false)]
[CommandExample("expand \"(x + 1)^2\"", Title = "Expand square of binomial")]
[CommandExample("expand \"(x + 1) * (x + 2)\"", Title = "Distribute polynomial multiplication")]
[CommandOutput("The expanded symbolic expression.", ClrType = typeof(IAsyncEnumerable<SymExpr>))]
[PipelineInput(AcceptsScalar = true, Description = "Accepts a symbolic expression or string to expand.")]
public sealed class ExpandCommand : ShellCommand
{
    public ExpandCommand()
        : base("expand", "Expands polynomial products and powers into a sum of terms.", "expand [expr]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        object? inputVal = null;
        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            inputVal = item;
            break;
        }

        SymExpr? expr = null;
        if (inputVal is SymExpr sym)
        {
            expr = sym;
        }
        else if (inputVal is not null)
        {
            expr = SymParser.Parse(inputVal.ToString()!);
        }
        else if (context.Arguments.Count > 0)
        {
            if (context.Arguments[0] is SymExpr se)
            {
                expr = se;
            }
            else
            {
                expr = SymParser.Parse(context.Arguments[0]?.ToString()!);
            }
        }

        if (expr is null)
        {
            throw new ArgumentException("No expression provided to 'expand'.");
        }

        yield return Expander.Expand(expr);
    }
}
