namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
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
                code: $"tosh::runtime::{Name}_requires_callable_or_block",
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
}
