using Tosh.Core;

namespace Tosh.Language.Commands;

internal sealed class RenamedCommand : IShellCommand, ICommandResolutionMetadata
{
    private readonly IShellCommand _inner;
    private readonly ICommandResolutionMetadata? _metadata;

    public RenamedCommand(string name, IShellCommand inner)
    {
        Name = name;
        _inner = inner;
        _metadata = inner as ICommandResolutionMetadata;
    }

    public string Name { get; }

    public string Description => _inner.Description;

    public string Usage => _inner.Usage;

    public CommandResolutionKind ResolutionKind => _metadata?.ResolutionKind ?? CommandResolutionKind.Function;

    public IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        return _inner.ExecuteAsync(context);
    }
}
