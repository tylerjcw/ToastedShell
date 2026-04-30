using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandExample("build-project | ignore", Title = "Run for side effects and discard output")]
[CommandExample("http post https://example.com/hooks --json $payload | ignore", Title = "Suppress response output")]
[CommandOutput("Emits nothing; drains the input pipeline silently.")]
public sealed class IgnoreCommand : ShellCommand
{
    public IgnoreCommand()
        : base("ignore", "Consumes and discards all pipeline input.", "... | ignore") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await foreach (var _ in context.Input.WithCancellation(context.CancellationToken))
        {
            // Consume and discard
        }

        yield break;
    }
}
