using Tosh.Runtime;

namespace Tosh.Stdlib.Cas;

[CommandCategory("CAS")]
[CommandArgument("expr", "Mathematical expression string to parse into a symbolic expression.", Required = false)]
[CommandExample("sym \"x^2 + 2*x + 1\"", Title = "Parse a polynomial")]
[CommandExample("sym \"sin(x)^2 + cos(x)^2\"", Title = "Parse a trigonometric expression")]
[CommandOutput("The parsed symbolic expression.", ClrType = typeof(IAsyncEnumerable<SymExpr>))]
[PipelineInput(AcceptsScalar = true, Description = "Accepts an expression string or symbolic expression from the pipeline.")]
public sealed class SymCommand : ShellCommand
{
    public SymCommand()
        : base("sym", "Creates or parses a symbolic expression.", "sym [expr]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        object? inputVal = null;
        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            inputVal = item;
            break;
        }

        string? exprStr = null;
        if (inputVal != null)
        {
            if (inputVal is SymExpr sym)
            {
                yield return sym;
                yield break;
            }
            exprStr = inputVal.ToString();
        }
        else if (context.Arguments.Count > 0)
        {
            exprStr = context.Arguments[0]?.ToString();
        }

        if (string.IsNullOrWhiteSpace(exprStr))
        {
            throw new ArgumentException("No expression provided to 'sym'.");
        }

        yield return SymParser.Parse(exprStr);
    }
}
