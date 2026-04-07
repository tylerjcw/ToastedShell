namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
public sealed class RenameCommand : ShellCommand
{
    public RenameCommand()
        : base("rename", "Renames fields on record-like objects.", "rename <old> <new> [old2 new2 ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0 || context.Arguments.Count % 2 != 0)
        {
            throw new InvalidOperationException("rename expects one or more <old> <new> pairs.");
        }

        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < context.Arguments.Count; index += 2)
        {
            var oldName = CommandArguments.RequireString(context.Arguments, index, "old field name");
            var newName = CommandArguments.RequireString(context.Arguments, index + 1, "new field name");
            renames[oldName] = newName;
        }

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            if (!ShellRecordUtilities.TryGetFields(item, out var fields))
            {
                throw new InvalidOperationException("rename expects record-like objects, for example from 'get { ... }'.");
            }

            yield return ShellRecordUtilities.CreateExpando(fields.Select(field =>
                new KeyValuePair<string, object?>(
                    renames.TryGetValue(field.Key, out var newName) ? newName : field.Key,
                    field.Value)));
        }
    }
}
