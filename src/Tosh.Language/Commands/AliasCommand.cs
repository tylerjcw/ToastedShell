using Tosh.Core;

namespace Tosh.Language.Commands;

public sealed class AliasCommand : IShellCommand, ICommandResolutionMetadata
{
    private readonly AliasDefinition _definition;
    private readonly ToshEngine _engine;

    public AliasCommand(ToshEngine engine, AliasDefinition definition)
    {
        _engine = engine;
        _definition = definition;
    }

    public string Name => _definition.Name;

    public string Description => $"User-defined alias for '{_definition.ExpansionText}'.";

    public string Usage => $"{Name} [args...]";

    public CommandResolutionKind ResolutionKind => CommandResolutionKind.Alias;

    public IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        return _engine.ExecuteAliasAsync(_definition, context);
    }
}
