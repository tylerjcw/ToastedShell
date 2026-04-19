using Tosh.Core;

namespace Tosh.Language.Commands;

/// <summary>
/// Wraps a <see cref="RuneDefinition"/> as an <see cref="IShellCommand"/>
/// so that runes can be resolved through the normal command lookup chain.
/// Unlike FunctionCommand, RuneCommand does not evaluate its arguments —
/// the engine intercepts rune invocations and performs macro expansion.
/// </summary>
public sealed class RuneCommand : IShellCommand, ICommandResolutionMetadata, IDocumentedCommand
{
    private readonly RuneDefinition _definition;

    public RuneCommand(RuneDefinition definition)
    {
        _definition = definition;
    }

    public string Name => _definition.Name;

    public string Description => _definition.DocComment?.Description is { Length: > 0 } desc
        ? desc
        : "User-defined rune (macro).";

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

    internal RuneDefinition Definition => _definition;

    public IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        // This should never be called directly — the engine intercepts
        // RuneCommand invocations in ExecuteCommandSyntaxAsync and
        // performs macro expansion instead.
        throw new InvalidOperationException(
            $"Rune '{Name}' must be expanded by the engine, not executed as a regular command.");
    }

    private string BuildUsage()
    {
        var parameters = string.Join(
            " ",
            _definition.Parameters.Select(p => $"<{p.Name}>"));
        return string.IsNullOrEmpty(parameters)
            ? _definition.Name
            : $"{_definition.Name} {parameters}";
    }
}
