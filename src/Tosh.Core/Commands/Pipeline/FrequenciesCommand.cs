namespace Tosh.Core.Commands.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("member-path", "Optional member path to count frequencies of, instead of the whole item.", Required = false)]
[CommandExample("echo red blue red green blue red | frequencies", Title = "Count distinct values")]
[CommandExample("ls | frequencies Extension", Title = "Count file extensions")]
[CommandOutput("Records of the form { Value, Count } describing how often each distinct input value occurred.")]
public sealed class FrequenciesCommand : ShellCommand, ICurrentItemMemberPathCommand
{
    public FrequenciesCommand()
        : base("frequencies", "Counts occurrences of each distinct value in the pipeline.", "frequencies [member-path]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.frequencies_too_many_args",
                title: "'frequencies' accepts at most one member path argument.",
                label: "use 'frequencies [member-path]'");
        }

        string? memberPath = context.Arguments.Count == 1 ? context.Arguments[0]?.ToString() : null;

        var counts = new Dictionary<object, int>();
        var insertionOrder = new List<object>();

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            var value = memberPath is null ? item : context.Runtime.ObjectAccessor.GetValue(item, memberPath);
            var key = value ?? "<null>";

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
