namespace Tosh.Core.Commands;

public sealed class FreeCommand : ShellCommand
{
    public FreeCommand()
        : base("free", "Returns system memory and swap usage.", "free") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            throw new InvalidOperationException("free does not accept arguments yet.");
        }

        foreach (var entry in SystemInfoServices.GetMemoryUsage())
        {
            yield return entry;
        }
    }
}
