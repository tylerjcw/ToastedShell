using Tosh.Core;

namespace Tosh.Language.Commands;

public sealed class FunctionCommand : IShellCommand, ICommandResolutionMetadata
{
    private readonly FunctionDefinition _definition;
    private readonly ToshEngine _engine;

    public FunctionCommand(ToshEngine engine, FunctionDefinition definition)
    {
        _engine = engine;
        _definition = definition;
    }

    public string Name => _definition.Name;

    public string Description => _definition.ReturnTypeName is null
        ? "User-defined Tosh function."
        : $"User-defined Tosh function returning {_definition.ReturnTypeName}.";

    public string Usage => BuildUsage();

    public CommandResolutionKind ResolutionKind => CommandResolutionKind.Function;

    public IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        return _engine.ExecuteFunctionAsync(_definition, context);
    }

    private string BuildUsage()
    {
        var parameters = string.Join(
            " ",
            _definition.Parameters.Select(parameter => parameter.TypeName is null
                ? $"<{parameter.Name}>"
                : $"<{parameter.Name}: {parameter.TypeName}>"));
        var returnType = _definition.ReturnTypeName is null ? string.Empty : $" -> {_definition.ReturnTypeName}";
        return string.IsNullOrEmpty(parameters)
            ? $"{Name}{returnType}"
            : $"{Name} {parameters}{returnType}";
    }
}
