using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandExample("echo @{a=1; b=2} @{a=3; b=4} | transpose", Title = "Pivot rows into columns")]
[CommandOutput("Pivoted records where original keys become headers and values become rows.")]
[PipelineInput(AcceptsRecord = true, AcceptsTable = true, Description = "Reads records from the pipeline and transposes them.")]
public sealed class TransposeCommand : ShellCommand
{
    public TransposeCommand()
        : base("transpose", "Pivots rows into columns. Each input record's keys become column headers and values become rows.", "transpose") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var records = new List<IReadOnlyList<KeyValuePair<string, object?>>>();

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            if (item is IShellRecordObject recordObject)
            {
                records.Add(recordObject.GetMembers());
            }
            else if (item is IDictionary<string, object?> dict)
            {
                records.Add(dict.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)).ToList());
            }
            else
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.transpose_requires_records",
                    title: "'transpose' requires record/object pipeline items.",
                    label: "this value is not a record or object");
            }
        }

        if (records.Count == 0)
        {
            yield break;
        }

        // Collect all keys across all records
        var allKeys = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var record in records)
        {
            foreach (var (key, _) in record)
            {
                if (seenKeys.Add(key))
                {
                    allKeys.Add(key);
                }
            }
        }

        // For each key, produce a record: { Column = key, Row0 = val0, Row1 = val1, ... }
        foreach (var key in allKeys)
        {
            IDictionary<string, object?> row = new System.Dynamic.ExpandoObject();
            row["Column"] = key;

            for (var i = 0; i < records.Count; i++)
            {
                var value = records[i].FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.Ordinal)).Value;
                row[$"Row{i}"] = value;
            }

            yield return row;
        }
    }
}
