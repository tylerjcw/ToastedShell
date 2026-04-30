using Tosh.Runtime;

namespace Tosh.Stdlib.Sys;

[Stdlib(StdlibCategory.Sys)]
[CommandCategory("System")]
[CommandExample("free", Title = "Show memory and swap usage")]
[CommandExample("free | get .UsedMemory", Title = "Get the used memory value")]
[CommandOutput("A record with TotalMemory, UsedMemory, FreeMemory, TotalSwap, UsedSwap, and FreeSwap as StorageSize values.")]
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
