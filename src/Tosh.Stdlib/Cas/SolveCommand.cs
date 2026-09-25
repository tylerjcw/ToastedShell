using Tosh.Runtime;

namespace Tosh.Stdlib.Cas;

[CommandCategory("CAS")]
[CommandArgument("equation", "Equation to solve (e.g. '2*x + 4 = 6' or 'x^2 - 4 = 0').", Required = false)]
[CommandArgument("var", "Variable to solve for (defaults to 'x').", Required = false)]
[CommandOption("--var", "Variable name to solve for.")]
[CommandExample("solve \"2*x + 4 = 6\"", Title = "Solve linear equation string")]
[CommandExample("var x = sym x; solve (2*$x + 4 == 6)", Title = "Solve live algebraic expression")]
[CommandExample("solve \"x^2 - 5*x + 6 = 0\"", Title = "Solve quadratic equation")]
[CommandOutput("The solutions for the target variable.", ClrType = typeof(IAsyncEnumerable<SymExpr>))]
[PipelineInput(AcceptsScalar = true, Description = "Accepts an equation string, SymEquation, or SymExpr from the pipeline.")]
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

        object? targetObj = inputVal;
        string variable = "x";
        bool explicitVar = false;

        if (targetObj is not null)
        {
            if (context.Arguments.Count > 0 && !(context.Arguments[0]?.ToString()?.StartsWith("--") ?? false))
            {
                variable = context.Arguments[0]?.ToString() ?? "x";
                explicitVar = true;
            }
        }
        else if (context.Arguments.Count > 0)
        {
            targetObj = context.Arguments[0];
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

        if (targetObj is null)
        {
            throw new ArgumentException("No equation provided to 'solve'.");
        }

        if (!explicitVar && targetObj is SymExpr targetExpr)
        {
            var detected = targetExpr.GetVariables().FirstOrDefault();
            if (!string.IsNullOrEmpty(detected))
            {
                variable = detected;
            }
        }

        IReadOnlyList<SymExpr> solutions;
        if (targetObj is SymEquation eq)
        {
            solutions = Solver.Solve(eq.ToStandardForm(), variable);
        }
        else if (targetObj is SymExpr expr)
        {
            solutions = Solver.Solve(expr, variable);
        }
        else
        {
            solutions = Solver.Solve(targetObj.ToString()!, variable);
        }

        foreach (var sol in solutions)
        {
            yield return sol;
        }
    }
}
