using Tosh.Runtime;

namespace Tosh.Stdlib.Data;

[Stdlib(StdlibCategory.Data)]
[CommandCategory("Data")]
[CommandArgument("item", "Zero or more numeric items. If omitted, consumes pipeline input.", Required = false)]
[CommandExample("vec 1 2 3", Title = "Build a vector from explicit items")]
[CommandExample("echo 1 2 3 | vec", Title = "Build a vector from pipeline items")]
[CommandExample("echo [1, 2, 3] | vec", Title = "Build a vector from a single list value")]
[CommandOutput("A Vector value.")]
[PipelineInput(AcceptsScalar = true, AcceptsList = true, Description = "Consumes numeric pipeline values or a single numeric collection and returns a Vector.")]
public sealed class VectorCommand : ShellCommand
{
    public VectorCommand()
        : base("vec", "Builds a Vector from numeric arguments or pipeline input.", "vec [item ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            yield return VectorShellType.Instance.CreateInstance(context.Arguments);
            yield break;
        }

        var values = await AsyncEnumerableExtensions.ToListAsync(
            ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken),
            context.CancellationToken);

        yield return VectorShellType.Instance.CreateInstance(values);
    }
}
