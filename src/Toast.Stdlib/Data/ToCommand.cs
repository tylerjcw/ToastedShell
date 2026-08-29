using Tosh.Runtime.Formats;

using Tosh.Runtime;

namespace Tosh.Stdlib.Data;

[CommandCategory("Data")]
[CommandOutput("Serialized text in the specified format.", Mode = "text")]
[CommandExample("ls | to json")]
[CommandExample("ls | to csv")]
[CommandExample("ls | to toml")]
[CommandExample("ls | to json --compact")]
[CommandNote("The `from` and `to` commands convert between text formats (json, csv, tsv, xml, toml) and CLR objects. Parsed values stay as CLR objects until you explicitly flatten them.")]
public sealed class ToCommand : ShellCommand
{
    private readonly DataFormatRegistry _formats;

    public ToCommand(DataFormatRegistry formats, string name = "to")
        : base(name, "Serializes objects into structured text.", "to <format> [options]")
    {
        _formats = formats;
    }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException(
                $"Usage: to <format> [options]\nAvailable formats: {string.Join(", ", _formats.GetAll().Select(f => f.Name))}.");
        }

        var formatName = context.Arguments[0]?.ToString()
            ?? throw new InvalidOperationException("Format name is required.");

        var format = _formats.Resolve(formatName);
        var remainingArgs = context.Arguments.Skip(1).ToArray();

        IReadOnlyList<object?> values =
            await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (values.Count == 0)
        {
            // Nothing was piped. This used to emit the literal text `null`, which is the worst
            // possible answer: `to json {| a = 1 |}` printed `null` and read like a successful
            // serialization of a null, while the record sat in the arguments untouched
            // (TS-P1-38). An empty *collection* does not land here — `[] | to json` is one value
            // and serializes as `[]` — so reaching this point is always a mistake.
            if (TryTakeArgumentValues(remainingArgs, out var argumentValues))
            {
                values = argumentValues;
                remainingArgs = [];
            }
            else
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.to_requires_input",
                    title: $"'to {formatName}' has nothing to serialize.",
                    label: "no pipeline input reached this command",
                    help: $"pipe the value in: `$value | to {formatName}`, or pass it as the "
                        + $"only argument: `to {formatName} $value`.");
            }
        }

        await foreach (var value in format.SerializeAsync(values, remainingArgs))
        {
            yield return value;
        }
    }

    /// <summary>
    /// Treats the command's arguments as the values to serialize, when they can only be data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `to json {| a = 1 |}` is a natural spelling and used to print `null`. Serializing the
    /// arguments makes it mean what it looks like.
    /// </para>
    /// <para>
    /// Only when *no* argument looks like a flag, though. Formats read their options positionally
    /// after a switch — `to csv -d ","` — so a mixed argument list would require knowing which
    /// flags take values, and guessing wrong would silently serialize a delimiter instead of the
    /// user's data. A mixed list gets the diagnostic instead, which costs one edit and can never
    /// serialize the wrong thing.
    /// </para>
    /// </remarks>
    private static bool TryTakeArgumentValues(
        IReadOnlyList<object?> arguments,
        out IReadOnlyList<object?> values)
    {
        values = [];

        if (arguments.Count == 0)
        {
            return false;
        }

        foreach (var argument in arguments)
        {
            if (argument is string text && text.StartsWith('-'))
            {
                return false;
            }
        }

        values = arguments;
        return true;
    }
}
