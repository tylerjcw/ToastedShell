using Tosh.Runtime;

namespace Tosh.Language.Bridge;

public sealed class FunctionCommand : IShellCommand, ICommandResolutionMetadata, IShellCallable, IDocumentedCommand
{
    private readonly FunctionDefinition _definition;
    private readonly ToshEngine _engine;

    public FunctionCommand(ToshEngine engine, FunctionDefinition definition)
    {
        _engine = engine;
        _definition = definition;
    }

    public string Name => _definition.Name;

    public string Description => _definition.DocComment?.Description is { Length: > 0 } desc
        ? desc
        : _definition.ReturnTypeName is null
            ? "User-defined Tosh function."
            : $"User-defined Tosh function returning {_definition.ReturnTypeName}.";

    public IReadOnlyDictionary<string, string> ParameterDescriptions =>
        _definition.DocComment?.Parameters ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();

    public string? ReturnsDescription => _definition.DocComment?.Returns;

    public IReadOnlyList<string> DocExamples =>
        _definition.DocComment?.Examples ?? Array.Empty<string>();

    public bool IsDeprecated => _definition.DocComment?.IsDeprecated ?? false;

    public string? DeprecatedMessage => _definition.DocComment?.Deprecated;

    public IReadOnlyList<string> SeeAlso =>
        _definition.DocComment?.SeeAlso ?? Array.Empty<string>();

    public string? Since => _definition.DocComment?.Since;

    public IReadOnlyList<string> Throws =>
        _definition.DocComment?.Throws ?? Array.Empty<string>();

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
        // When executing inside a fork (race/settle/parallel), redirect to the fork's
        // engine so that scope mutations and call stacks stay isolated to the fork.
        if (context.BlockExecutor is ToshEngine.EngineBlockExecutor eb &&
            !ReferenceEquals(eb.Engine, _engine))
        {
            return eb.Engine.ExecuteFunctionAsync(_definition, context);
        }

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
