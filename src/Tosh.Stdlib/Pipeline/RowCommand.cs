using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

/// <summary>
/// Picks one or more rows (items) from the pipeline by zero-based index, range, or
/// list of indices. The row-picker counterpart to <see cref="GetCommand"/>, which
/// picks columns (member values).
/// </summary>
[CommandCategory("Pipeline")]
[CommandArgument("indices", "One or more zero-based indices, a range, or a list/array of indices.", Required = true, Kind = "expression")]
[CommandExample("ls | row 3", Title = "Pick the row at index 3")]
[CommandExample("ls | row 7 8 9", Title = "Pick multiple rows by index (variadic)")]
[CommandExample("ls | row [3, 1, 0]", Title = "Pick rows from a list literal (preserves the listed order)")]
[CommandExample("ls | row 2..5", Title = "Pick a contiguous slice")]
[CommandOutput("The pipeline rows at the requested indices, in the order they were requested.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Reads input items by their zero-based position.")]
public sealed class RowCommand : ShellCommand
{
    public RowCommand()
        : base("row", "Picks one or more rows from the pipeline by index, range, or list of indices.", "row <index ...> | row <range> | row <list>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException("Missing required argument: row index, range, or list of indices.");
        }

        var requested = CollectRequestedIndices(context);

        if (requested.Count == 0)
        {
            yield break;
        }

        // Materialise just enough of the pipeline to satisfy the highest requested index.
        var maxIndex = requested.Max();
        var collected = new List<object?>(capacity: maxIndex + 1);
        var current = 0;

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
            .WithCancellation(context.CancellationToken))
        {
            collected.Add(item);

            if (current >= maxIndex)
            {
                current++;
                break;
            }

            current++;
        }

        // Yield in the order the user requested them (preserves "row 7 8 9" vs "row [3, 1, 0]" semantics).
        foreach (var index in requested)
        {
            if (index < 0 || index >= collected.Count)
            {
                throw context.CreateDiagnostic(
                    "tosh.row.index_out_of_range",
                    $"Row index {index} is out of range (pipeline had {collected.Count} item{(collected.Count == 1 ? string.Empty : "s")}).",
                    label: "this index is past the end of the pipeline");
            }

            yield return collected[index];
        }
    }

    private static List<int> CollectRequestedIndices(CommandContext context)
    {
        // Single arg: range, list, or scalar.
        if (context.Arguments.Count == 1)
        {
            return ExpandToIndices(context.Arguments[0]);
        }

        // Variadic: every arg must coerce to an index.
        var indices = new List<int>(context.Arguments.Count);

        foreach (var arg in context.Arguments)
        {
            if (TryCoerceIndex(arg, out var index))
            {
                indices.Add(index);
                continue;
            }

            throw new InvalidOperationException(
                $"row: when called with multiple arguments, every argument must be an integer index. Got '{arg ?? "null"}' ({arg?.GetType().Name ?? "null"}).");
        }

        return indices;
    }

    private static List<int> ExpandToIndices(object? argument)
    {
        if (argument is ToshRange range)
        {
            if (range.IsInfinite)
            {
                throw new InvalidOperationException("row: cannot use an infinite range to pick rows.");
            }

            return range.Enumerate().ToList();
        }

        if (argument is System.Collections.IEnumerable enumerable and not string)
        {
            var indices = new List<int>();

            foreach (var item in enumerable)
            {
                if (!TryCoerceIndex(item, out var index))
                {
                    throw new InvalidOperationException(
                        $"row: list element must be an integer index. Got '{item ?? "null"}' ({item?.GetType().Name ?? "null"}).");
                }

                indices.Add(index);
            }

            return indices;
        }

        if (TryCoerceIndex(argument, out var single))
        {
            return [single];
        }

        throw new InvalidOperationException(
            $"row: argument must be an integer, range, or list of integers. Got '{argument ?? "null"}' ({argument?.GetType().Name ?? "null"}).");
    }

    private static bool TryCoerceIndex(object? value, out int index)
    {
        index = 0;

        switch (value)
        {
            case int i:
                index = i;
                return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                index = (int)l;
                return true;
            case short s:
                index = s;
                return true;
            case byte b:
                index = b;
                return true;
            case double d when d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue:
                index = (int)d;
                return true;
            case decimal m when m == Math.Floor(m) && m >= int.MinValue && m <= int.MaxValue:
                index = (int)m;
                return true;
            case string str when int.TryParse(str, out var parsed):
                index = parsed;
                return true;
            default:
                return false;
        }
    }
}
