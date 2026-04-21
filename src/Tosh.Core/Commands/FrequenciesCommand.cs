namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
[CommandExample("echo red blue red green blue red | frequencies", Title = "Count distinct values")]
[CommandExample("ls | get Extension | frequencies | sort Count", Title = "Count file extensions")]
public sealed class FrequenciesCommand : ShellCommand
{
    public FrequenciesCommand()
        : base("frequencies", "Counts occurrences of each distinct value in the pipeline.", "frequencies") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var counts = new Dictionary<object, int>();
        var insertionOrder = new List<object>();

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            var key = item ?? "<null>";

            if (counts.TryGetValue(key, out var count))
            {
                counts[key] = count + 1;
            }
            else
            {
                counts[key] = 1;
                insertionOrder.Add(key);
            }
        }

        foreach (var key in insertionOrder)
        {
            IDictionary<string, object?> record = new System.Dynamic.ExpandoObject();
            record["Value"] = key is "<null>" ? null : key;
            record["Count"] = counts[key];
            yield return record;
        }
    }
}
