using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

/// <summary>
/// Shared subcommand-dispatch helpers for the structured-introspection cluster:
/// <c>members</c> and <c>methods</c> accept first-arg keywords like <c>has</c>, <c>get</c>,
/// <c>props</c>, <c>fields</c>, <c>methods</c>, <c>events</c>.
/// </summary>
internal static class IntrospectionSubcommands
{
    /// <summary>Recognised first-arg keywords.</summary>
    public static readonly HashSet<string> Recognised = new(StringComparer.OrdinalIgnoreCase)
    {
        "has",
        "get",
        "props",
        "fields",
        "methods",
        "events",
    };

    public static bool TryDispatch(
        CommandContext context,
        out string? subcommand,
        out string? operand)
    {
        subcommand = null;
        operand = null;

        if (context.Arguments.Count == 0)
        {
            return false;
        }

        if (context.Arguments[0] is not string firstArg)
        {
            return false;
        }

        if (!Recognised.Contains(firstArg))
        {
            return false;
        }

        subcommand = firstArg.ToLowerInvariant();

        // Only `has` and `get` consume the next arg as a member-name operand.
        // For `props`/`fields`/`methods`/`events`, remaining args are type targets.
        var consumesOperand = subcommand is "has" or "get";

        if (consumesOperand && context.Arguments.Count >= 2 && context.Arguments[1] is string secondArg)
        {
            operand = secondArg;
        }

        return true;
    }
}
