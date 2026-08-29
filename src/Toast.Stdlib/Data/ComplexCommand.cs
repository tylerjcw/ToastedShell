using Tosh.Runtime;

namespace Tosh.Stdlib.Data;

[CommandCategory("Data")]
[CommandArgument("component", "Zero, one, or two numeric components. If omitted, consumes pipeline input.", Required = false)]
[CommandExample("complex 3 4", Title = "Build a complex number from real and imaginary parts")]
[CommandExample("echo [3, 4] | complex", Title = "Build a complex number from a single pair value")]
[CommandExample("echo 3 4 | complex", Title = "Build a complex number from pipeline values")]
[CommandOutput("A Complex value.")]
[PipelineInput(AcceptsScalar = true, AcceptsList = true, Description = "Consumes scalar values or a single two-item collection and returns a Complex value.")]
public sealed class ComplexCommand : ShellCommand
{
    public ComplexCommand()
        : base("complex", "Builds a Complex value from numeric arguments or pipeline input.", "complex [real [imaginary]]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            yield return ComplexShellType.Instance.CreateInstance(context.Arguments);
            yield break;
        }

        var values = await AsyncEnumerableExtensions.ToListAsync(
            ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken),
            context.CancellationToken);

        yield return ComplexShellType.Instance.CreateInstance(values);
    }
}
