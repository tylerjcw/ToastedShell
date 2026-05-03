using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("callable|block", "A predicate evaluated for each input item.")]
[CommandExample("echo 1 2 3 | any { _ > 2 }", Title = "Check if any value exceeds 2")]
[CommandExample("echo 2 4 6 | all { _ % 2 == 0 }", Title = "Check if all values are even")]
[CommandExample("echo 1 3 5 | none { _ % 2 == 0 }", Title = "Check that no values are even")]
[CommandOutput("A boolean: true or false.", ClrType = typeof(bool))]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Tests pipeline items against the predicate.")]
public sealed class QuantifierCommand : ShellCommand
{
    private readonly QuantifierKind _kind;

    public QuantifierCommand(string name, string description, QuantifierKind kind)
        : base(name, description, $"{name} <callable|block>")
    {
        _kind = kind;
    }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: $"tosh.runtime.{Name}_requires_callable_or_block",
                title: $"'{Name}' requires exactly one callable value or block.",
                label: "pass a lambda like 'func(x) => ...' or a block like '{ ... }'");
        }

        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 0);

        switch (_kind)
        {
            case QuantifierKind.Any:
                {
                    await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                                       .WithCancellation(context.CancellationToken))
                    {
                        if (await EvaluatePredicateAsync(context, operation, item))
                        {
                            yield return true;
                            yield break;
                        }
                    }

                    yield return false;
                    yield break;
                }

            case QuantifierKind.All:
                {
                    await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                                       .WithCancellation(context.CancellationToken))
                    {
                        if (!await EvaluatePredicateAsync(context, operation, item))
                        {
                            yield return false;
                            yield break;
                        }
                    }

                    yield return true;
                    yield break;
                }

            case QuantifierKind.None:
                {
                    await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                                       .WithCancellation(context.CancellationToken))
                    {
                        if (await EvaluatePredicateAsync(context, operation, item))
                        {
                            yield return false;
                            yield break;
                        }
                    }

                    yield return true;
                    yield break;
                }

            default:
                throw new InvalidOperationException($"Unsupported quantifier kind '{_kind}'.");
        }
    }

    private static async Task<bool> EvaluatePredicateAsync(CommandContext context, object operation, object? item)
    {
        return await FunctionalCommandUtilities.EvaluatePredicateAsync(
            context,
            operation,
            [item],
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["_"] = item,
            });
    }

    public enum QuantifierKind
    {
        Any,
        All,
        None,
    }

    public override CommandMetadata GetMetadata(IReadOnlyList<string>? aliases = null)
    {
        return new CommandMetadata(
            Name: Name,
            Description: Description,
            LongDescription: null,
            Usage: Usage,
            Category: "Pipeline",
            Aliases: aliases ?? [],
            Arguments: [new("callable|block", "A lambda or block predicate that returns boolean values.", Required: true, TypeName: null, Kind: "block")],
            Options: [],
            Examples: _kind switch
            {
                QuantifierKind.Any =>
                [
                    new("ps | any func(p) => ($p.Name == \"sshd\")", null),
                    new("echo 1 2 3 | any func(x) => ($x == 2)", "Check whether any item matches"),
                ],
                QuantifierKind.All =>
                [
                    new("echo 2 4 6 | all func(x) => ((($x % 2) == 0))", "Check whether all items match"),
                    new("ls | all { _.Exists }", null),
                ],
                QuantifierKind.None =>
                [
                    new("echo 1 2 3 | none func(x) => ($x > 10)", "Check whether no items match"),
                    new("ls | none { _.Type == link }", null),
                ],
                _ => [],
            },
            Notes: [],
            Output: _kind switch
            {
                QuantifierKind.Any => "Returns `true` if any item matches the predicate; otherwise `false`.",
                QuantifierKind.All => "Returns `true` if every item matches the predicate; otherwise `false`.",
                QuantifierKind.None => "Returns `true` if no items match the predicate; otherwise `false`.",
                _ => null,
            },
            PipelineInput: new(
                AcceptsScalar: true,
                AcceptsRecord: true,
                AcceptsList: false,
                AcceptsTable: false,
                Description: _kind switch
                {
                    QuantifierKind.Any => "Consumes the current pipeline and stops at the first matching item.",
                    QuantifierKind.All => "Consumes the current pipeline and stops at the first non-matching item.",
                    QuantifierKind.None => "Consumes the current pipeline and stops at the first matching item.",
                    _ => null,
                }),
            OutputType: "Boolean",
            OutputMembers: null,
            OutputMode: "structured",
            SideEffects: null,
            SinceVersion: null,
            DeprecatedVersion: null,
            RemovedVersion: null,
            Tags: [],
            SeeAlso: [],
            Permissions: [],
            IsExperimental: false,
            ErrorConditions: [],
            CanonicalExamples: []);
    }
}
