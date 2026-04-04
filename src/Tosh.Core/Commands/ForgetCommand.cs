namespace Tosh.Core.Commands;

public sealed class ForgetCommand : ShellCommand
{
    public ForgetCommand(string name = "forget")
        : base(name, "Removes Tosh variables, functions, and exported environment names.", $"{name} <name> [name...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var evaluator = context.Runtime.Evaluator
            ?? throw new InvalidOperationException($"{Name} requires an active Tosh evaluator.");

        var results = new List<object?>();
        var processedAny = false;

        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);

        while (await enumerator.MoveNextAsync())
        {
            processedAny = true;

            foreach (var removal in evaluator.ForgetValue(enumerator.Current))
            {
                results.Add(ToResultRecord(removal));
            }
        }

        foreach (var argument in context.Arguments.Select((value, index) => (Value: value, Index: index)))
        {
            processedAny = true;

            var treatAsValue = ShouldTreatArgumentAsValue(context, argument.Index);

            if (!treatAsValue && argument.Value is string name)
            {
                results.Add(ToResultRecord(evaluator.Forget(name)));
                continue;
            }

            foreach (var removal in evaluator.ForgetValue(argument.Value))
            {
                results.Add(ToResultRecord(removal));
            }
        }

        if (!processedAny)
        {
            throw new InvalidOperationException($"{Name} expects at least one name or value.");
        }

        foreach (var result in results)
        {
            yield return result;
        }
    }

    private static object ToResultRecord(ShellNameRemovalResult removal)
    {
        return ShellRecordUtilities.CreateExpando(
        [
            new KeyValuePair<string, object?>("Name", removal.Name),
            new KeyValuePair<string, object?>("RemovedVariable", removal.RemovedVariable),
            new KeyValuePair<string, object?>("VariableScope", removal.VariableScope),
            new KeyValuePair<string, object?>("RemovedCommand", removal.RemovedCommand),
            new KeyValuePair<string, object?>("CommandKind", removal.CommandKind),
            new KeyValuePair<string, object?>("CommandScope", removal.CommandScope),
            new KeyValuePair<string, object?>("RemovedEnvironment", removal.RemovedEnvironment),
            new KeyValuePair<string, object?>("FreedValue", removal.FreedValue),
            new KeyValuePair<string, object?>("FreedValueKind", removal.FreedValueKind),
        ]);
    }

    private static bool ShouldTreatArgumentAsValue(CommandContext context, int argumentIndex)
    {
        var span = context.GetArgumentSpan(argumentIndex);

        if (span is null ||
            context.Invocation is null ||
            span.Value.Start < 0 ||
            span.Value.End > context.Invocation.SourceText.Length ||
            span.Value.End <= span.Value.Start)
        {
            return true;
        }

        var text = context.Invocation.SourceText[span.Value.Start..span.Value.End].TrimStart();
        return text.StartsWith('$') || text.StartsWith('(');
    }
}
