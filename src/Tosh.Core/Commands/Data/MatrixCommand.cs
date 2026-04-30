namespace Tosh.Core.Commands.Data;

[Stdlib(StdlibCategory.Data)]
[CommandCategory("Data")]
[CommandArgument("row", "Zero or more scalar values or row sequences. If omitted, consumes pipeline input.", Required = false)]
[CommandExample("mat [1, 2, 3] [4, 5, 6]", Title = "Build a matrix from explicit rows")]
[CommandExample("echo [[1, 2], [3, 4]] | mat", Title = "Build a matrix from pipeline input")]
[CommandOutput("A Matrix value.")]
[PipelineInput(AcceptsScalar = true, AcceptsList = true, Description = "Consumes scalar values or row sequences and returns a Matrix.")]
public sealed class MatrixCommand : ShellCommand
{
    public MatrixCommand(string name = "mat")
        : base(name, "Builds a Matrix from rows, scalars, or pipeline input.", $"{name} [row ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            yield return MatrixShellType.Instance.CreateInstance(context.Arguments);
            yield break;
        }

        var values = await AsyncEnumerableExtensions.ToListAsync(
            ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken),
            context.CancellationToken);

        yield return MatrixShellType.Instance.CreateInstance(values);
    }
}
