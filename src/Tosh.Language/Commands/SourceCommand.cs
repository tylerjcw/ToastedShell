using Tosh.Core;

namespace Tosh.Language.Commands;

public sealed class SourceCommand : ShellCommand
{
    private readonly ToshEngine _engine;

    public SourceCommand(ToshEngine engine)
        : base("source", "Executes one or more Tosh script files in the current session.", "source <path> [path...]")
    {
        _engine = engine;
    }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException("The 'source' command requires at least one path.");
        }

        foreach (var argument in context.Arguments)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var rawPath = argument?.ToString();

            if (string.IsNullOrWhiteSpace(rawPath))
            {
                throw new InvalidOperationException("The 'source' command requires non-empty paths.");
            }

            var resolvedPath = PathUtilities.ResolvePath(context.Runtime.CurrentDirectory, rawPath);

            if (!File.Exists(resolvedPath))
            {
                throw new InvalidOperationException($"Script file '{resolvedPath}' was not found.");
            }

            var source = await File.ReadAllTextAsync(resolvedPath, context.CancellationToken);

            await foreach (var value in _engine.EvaluateAsync(source, resolvedPath, context.CancellationToken)
                               .WithCancellation(context.CancellationToken))
            {
                yield return value;
            }
        }
    }
}
