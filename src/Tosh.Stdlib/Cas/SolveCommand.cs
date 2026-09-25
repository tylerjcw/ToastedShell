using Tosh.Runtime;

namespace Tosh.Stdlib.Cas;

[CommandCategory("CAS")]
[CommandArgument("equation", "Equation to solve (e.g. '2*x + 4 = 6' or 'x^2 - 4 = 0').", Required = false)]
[CommandArgument("var", "Variable to solve for (defaults to 'x').", Required = false)]
[CommandOption("--var", "Variable name to solve for.")]
[CommandExample("solve \"2*x + 4 = 6\"", Title = "Solve linear equation")]
[CommandExample("solve \"x^2 - 5*x + 6 = 0\"", Title = "Solve quadratic equation")]
[CommandExample("solve \"x^2 = 9\"", Title = "Solve square equation")]
[CommandOutput("The solutions for the target variable.", ClrType = typeof(IAsyncEnumerable<SymExpr>))]
[PipelineInput(AcceptsScalar = true, Description = "Accepts an equation string from the pipeline.")]
public sealed class SolveCommand : ShellCommand
{
    public SolveCommand()
        : base("solve", "Solves a linear or quadratic algebraic equation for a variable.", "solve [equation] [var]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        object? inputVal = null;
        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            inputVal = item;
            break;
        }

        string? eqStr = null;
        string variable = "x";

        if (inputVal is not null)
        {
            eqStr = inputVal.ToString();
            if (context.Arguments.Count > 0)
            {
                variable = context.Arguments[0]?.ToString() ?? "x";
            }
        }
        else if (context.Arguments.Count > 0)
        {
            eqStr = context.Arguments[0]?.ToString();
            if (context.Arguments.Count > 1)
            {
                variable = context.Arguments[1]?.ToString() ?? "x";
            }
        }

        for (int i = 0; i < context.Arguments.Count; i++)
        {
            if (context.Arguments[i]?.ToString() == "--var" && i + 1 < context.Arguments.Count)
            {
                variable = context.Arguments[++i]?.ToString() ?? "x";
            }
        }

        if (string.IsNullOrWhiteSpace(eqStr))
        {
            throw new ArgumentException("No equation provided to 'solve'.");
        }

        var solutions = Solver.Solve(eqStr, variable);
        foreach (var sol in solutions)
        {
            yield return sol;
        }
    }
}
