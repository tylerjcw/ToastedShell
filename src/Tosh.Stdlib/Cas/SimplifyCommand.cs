using Tosh.Runtime;

namespace Tosh.Stdlib.Cas;

[CommandCategory("CAS")]
[CommandArgument("expr", "Expression to simplify.", Required = false)]
[CommandExample("simplify \"2*x + 3*x\"", Title = "Combine like terms")]
[CommandExample("sym \"sin(0) + x\" | simplify", Title = "Simplify piped expression")]
[CommandOutput("The simplified symbolic expression.", ClrType = typeof(IAsyncEnumerable<SymExpr>))]
[PipelineInput(AcceptsScalar = true, Description = "Accepts a symbolic expression or string to simplify.")]
public sealed class SimplifyCommand : ShellCommand
{
    public SimplifyCommand()
        : base("simplify", "Simplifies a symbolic expression algebraically.", "simplify [expr]") { }

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
            expr = SymParser.Parse(context.Arguments[0]?.ToString()!);
        }

        if (expr is null)
        {
            throw new ArgumentException("No expression provided to 'simplify'.");
        }

        yield return Simplifier.Simplify(expr);
    }
}
