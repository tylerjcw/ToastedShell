using Tosh.Runtime;

namespace Tosh.Stdlib.Cas;

[CommandCategory("CAS")]
[CommandArgument("expr", "Expression to differentiate.", Required = false)]
[CommandArgument("var", "Variable to differentiate with respect to (defaults to 'x').", Required = false)]
[CommandOption("--var", "Variable name to differentiate with respect to.")]
[CommandExample("diff \"x^3 + 2*x\"", Title = "Differentiate polynomial")]
[CommandExample("sym \"sin(x)\" | diff", Title = "Differentiate piped symbolic expression")]
[CommandExample("diff \"x^2 * y\" y", Title = "Partial derivative with respect to y")]
[CommandOutput("The derivative as a simplified symbolic expression.", ClrType = typeof(IAsyncEnumerable<SymExpr>))]
[PipelineInput(AcceptsScalar = true, Description = "Accepts a symbolic expression or string to differentiate.")]
public sealed class DiffCommand : ShellCommand
{
    public DiffCommand()
        : base("diff", "Computes the exact symbolic derivative of an expression.", "diff [expr] [var]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        object? inputVal = null;
        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            inputVal = item;
            break;
        }

        SymExpr? expr = null;
        string variable = "x";
        bool explicitVar = false;

        if (inputVal is SymExpr sym)
        {
            expr = sym;
            if (context.Arguments.Count > 0 && !(context.Arguments[0]?.ToString()?.StartsWith("--") ?? false))
            {
                variable = context.Arguments[0]?.ToString() ?? "x";
                explicitVar = true;
            }
        }
        else if (inputVal is not null)
        {
            expr = SymParser.Parse(inputVal.ToString()!);
            if (context.Arguments.Count > 0 && !(context.Arguments[0]?.ToString()?.StartsWith("--") ?? false))
            {
                variable = context.Arguments[0]?.ToString() ?? "x";
                explicitVar = true;
            }
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

            if (context.Arguments.Count > 1 && !(context.Arguments[1]?.ToString()?.StartsWith("--") ?? false))
            {
                variable = context.Arguments[1]?.ToString() ?? "x";
                explicitVar = true;
            }
        }

        for (int i = 0; i < context.Arguments.Count; i++)
        {
            if (context.Arguments[i]?.ToString() == "--var" && i + 1 < context.Arguments.Count)
            {
                variable = context.Arguments[++i]?.ToString() ?? "x";
                explicitVar = true;
            }
        }

        if (expr is null)
        {
            throw new ArgumentException("No expression provided to 'diff'.");
        }

        if (!explicitVar)
        {
            var detected = expr.GetVariables().FirstOrDefault();
            if (!string.IsNullOrEmpty(detected))
            {
                variable = detected;
            }
        }

        var derivative = expr.Differentiate(variable);
        yield return Simplifier.Simplify(derivative);
    }
}
