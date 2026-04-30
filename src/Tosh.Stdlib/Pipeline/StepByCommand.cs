using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("n", "Take every Nth item (must be >= 1).")]
[CommandExample("1.. | step-by 3 | first 5", Title = "Every 3rd integer")]
[CommandExample("echo a b c d e f g | step-by 2", Title = "Every other item")]
[CommandOutput("Every Nth item from the pipeline.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Yields every Nth item from the pipeline.")]
public sealed class StepByCommand : ShellCommand
{
    public StepByCommand()
        : base("step-by", "Takes every Nth item from the pipeline.", "... | step-by <n>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.step_by_requires_n",
                title: "'step-by' requires exactly one integer argument.",
                label: "use '... | step-by <n>'");
        }

        var n = Convert.ToInt32(context.Arguments[0]);
        if (n < 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.step_by_positive",
                title: "'step-by' requires n >= 1.",
                argumentIndex: 0,
                label: "must be at least 1");
        }

        var index = 0;
        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            if (index % n == 0)
            {
                yield return item;
            }

            index++;
        }
    }
}
