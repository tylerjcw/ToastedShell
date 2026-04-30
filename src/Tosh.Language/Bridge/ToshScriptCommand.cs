using Tosh.Runtime;

namespace Tosh.Language.Bridge;

internal sealed class ToshScriptCommand : IShellCommand, ICommandResolutionMetadata
{
    private readonly string _scriptPath;
    private readonly ToshEngine _engine;

    public ToshScriptCommand(string name, string scriptPath, ToshEngine engine)
    {
        Name = name;
        _scriptPath = scriptPath;
        _engine = engine;
    }

    public string Name { get; }

    public string Description => $"Runs the Tosh script at '{_scriptPath}' in an isolated script scope.";

    public string Usage => $"{Name} [arg ...]";

    public CommandResolutionKind ResolutionKind => CommandResolutionKind.External;

    public IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        return _engine.ExecuteScriptFileAsync(_scriptPath, context.Arguments, context.CancellationToken);
    }
}
