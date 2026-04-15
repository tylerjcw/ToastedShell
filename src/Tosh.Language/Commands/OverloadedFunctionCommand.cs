using Tosh.Core;

namespace Tosh.Language.Commands;

internal sealed class OverloadedFunctionCommand : IShellCommand, ICommandResolutionMetadata, IShellCallable, IDocumentedCommand
{
    private readonly ToshEngine _engine;
    private readonly List<FunctionDefinition> _definitions;

    public OverloadedFunctionCommand(ToshEngine engine, IEnumerable<FunctionDefinition> definitions)
    {
        _engine = engine;
        _definitions = new List<FunctionDefinition>();

        foreach (var definition in definitions)
        {
            AddOrReplace(definition);
        }

        if (_definitions.Count == 0)
        {
            throw new ArgumentException("At least one function definition is required.", nameof(definitions));
        }
    }

    public string Name => _definitions[0].Name;

    public string Description
    {
        get
        {
            var doc = _definitions.Select(d => d.DocComment?.Description).FirstOrDefault(d => d is { Length: > 0 });
            return doc ?? $"User-defined Tosh function with {_definitions.Count} overload{(_definitions.Count == 1 ? string.Empty : "s")}.";
        }
    }

    public IReadOnlyDictionary<string, string> ParameterDescriptions
    {
        get
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in _definitions)
            {
                if (def.DocComment?.Parameters is { } paramDescs)
                {
                    foreach (var (name, desc) in paramDescs)
                    {
                        merged.TryAdd(name, desc);
                    }
                }
            }
            return merged;
        }
    }

    public string? ReturnsDescription =>
        _definitions.Select(d => d.DocComment?.Returns).FirstOrDefault(r => r is not null);

    public IReadOnlyList<string> DocExamples =>
        _definitions.SelectMany(d => d.DocComment?.Examples ?? []).ToList();

    public bool IsDeprecated =>
        _definitions.Any(d => d.DocComment?.IsDeprecated ?? false);

    public string? DeprecatedMessage =>
        _definitions.Select(d => d.DocComment?.Deprecated).FirstOrDefault(m => m is not null);

    public IReadOnlyList<string> SeeAlso =>
        _definitions.SelectMany(d => d.DocComment?.SeeAlso ?? []).Distinct(StringComparer.Ordinal).ToList();

    public string? Since =>
        _definitions.Select(d => d.DocComment?.Since).FirstOrDefault(s => s is not null);

    public IReadOnlyList<string> Throws =>
        _definitions.SelectMany(d => d.DocComment?.Throws ?? []).Distinct(StringComparer.Ordinal).ToList();

    public string Usage => string.Join(" | ", _definitions.Select(FunctionCommand.FormatUsage));

    public CommandResolutionKind ResolutionKind => CommandResolutionKind.Function;

    public string CallableName => Name;

    public int RequiredParameterCount => _definitions.Min(static definition => definition.Parameters.Count(parameter => !parameter.IsOptional && !parameter.IsRest));

    public int? MaximumParameterCount => _definitions.Any(static definition => definition.Parameters.Count > 0 && definition.Parameters[^1].IsRest)
        ? null
        : _definitions.Max(static definition => definition.Parameters.Count);

    public int OverloadCount => _definitions.Count;

    public IReadOnlyList<FunctionDefinition> Definitions => _definitions;

    public void AddOrReplace(FunctionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (_definitions.Count > 0 &&
            !string.Equals(_definitions[0].Name, definition.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot add overload '{definition.Name}' to overload set '{_definitions[0].Name}'.");
        }

        for (var index = 0; index < _definitions.Count; index++)
        {
            if (HasSameResolutionSignature(_definitions[index], definition))
            {
                _definitions[index] = definition;
                SortDefinitions();
                return;
            }
        }

        _definitions.Add(definition);
        SortDefinitions();
    }

    public IAsyncEnumerable<object?> ExecuteAsync(CommandContext context) => InvokeAsync(context);

    public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
    {
        var definition = SelectDefinition(context.Arguments, context);

        await foreach (var value in _engine.ExecuteFunctionAsync(definition, context).WithCancellation(context.CancellationToken))
        {
            yield return value;
        }
    }

    private FunctionDefinition SelectDefinition(IReadOnlyList<object?> arguments, CommandContext context)
    {
        var matches = _engine.SelectBestCallableMatches(_definitions, static definition => definition.Parameters, arguments);

        if (matches.Count == 1)
        {
            return matches[0].Candidate;
        }

        if (matches.Count > 1)
        {
            var matchingUsages = string.Join(
                Environment.NewLine,
                matches.Select(match => $"- {FunctionCommand.FormatUsage(match.Candidate)}"));

            throw context.CreateDiagnostic(
                code: "tosh::runtime::function_overload_ambiguous",
                title: $"Multiple overloads matched function '{Name}' with {arguments.Count} argument(s).",
                label: $"'{Name}' has ambiguous overloads for these arguments",
                help: $"Matching overloads:{Environment.NewLine}{matchingUsages}");
        }

        throw context.CreateDiagnostic(
            code: "tosh::runtime::function_overload_not_found",
            title: $"No overload matched function '{Name}' with {arguments.Count} argument(s).",
            label: $"'{Name}' does not have a matching overload");
    }

    private static bool HasSameResolutionSignature(FunctionDefinition left, FunctionDefinition right)
    {
        if (left.Parameters.Count != right.Parameters.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Parameters.Count; index++)
        {
            var leftParameter = left.Parameters[index];
            var rightParameter = right.Parameters[index];

            if (!string.Equals(leftParameter.TypeName, rightParameter.TypeName, StringComparison.Ordinal) ||
                leftParameter.IsOptional != rightParameter.IsOptional ||
                leftParameter.IsRest != rightParameter.IsRest)
            {
                return false;
            }
        }

        return true;
    }

    private void SortDefinitions()
    {
        _definitions.Sort(static (left, right) =>
        {
            var requiredComparison = left.Parameters.Count(parameter => !parameter.IsOptional && !parameter.IsRest)
                .CompareTo(right.Parameters.Count(parameter => !parameter.IsOptional && !parameter.IsRest));
            if (requiredComparison != 0)
            {
                return requiredComparison;
            }

            var parameterComparison = left.Parameters.Count.CompareTo(right.Parameters.Count);
            if (parameterComparison != 0)
            {
                return parameterComparison;
            }

            return StringComparer.Ordinal.Compare(FunctionCommand.FormatUsage(left), FunctionCommand.FormatUsage(right));
        });
    }
}
