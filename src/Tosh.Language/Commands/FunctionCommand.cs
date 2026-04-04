using Tosh.Core;

namespace Tosh.Language.Commands;

public sealed class FunctionCommand : IShellCommand, ICommandResolutionMetadata, IShellCallable
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

    public string CallableName => Name;

    internal FunctionDefinition Definition => _definition;

    public int RequiredParameterCount => _definition.Parameters.Count(parameter => !parameter.IsOptional && !parameter.IsRest);

    public int? MaximumParameterCount => _definition.Parameters.Count > 0 && _definition.Parameters[^1].IsRest
        ? null
        : _definition.Parameters.Count;

    public IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        return _engine.ExecuteFunctionAsync(_definition, context);
    }

    public IAsyncEnumerable<object?> InvokeAsync(CommandContext context) => ExecuteAsync(context);

    internal static string FormatUsage(FunctionDefinition definition)
    {
        var parameters = string.Join(
            " ",
            definition.Parameters.Select(parameter =>
            {
                var optional = parameter.IsOptional ? "?" : "";
                var rest = parameter.IsRest ? "..." : "";
                return parameter.TypeName is null
                    ? $"<{parameter.Name}{optional}{rest}>"
                    : $"<{parameter.Name}{optional}{rest}: {parameter.TypeName}>";
            }));
        var returnType = definition.ReturnTypeName is null ? string.Empty : $" -> {definition.ReturnTypeName}";
        return string.IsNullOrEmpty(parameters)
            ? $"{definition.Name}{returnType}"
            : $"{definition.Name} {parameters}{returnType}";
    }

    private string BuildUsage()
    {
        return FormatUsage(_definition);
    }
}
