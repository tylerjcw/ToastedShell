namespace Tosh.Core.Commands;

public sealed class RenameCommand : ShellCommand
{
    public RenameCommand()
        : base("rename", "Renames fields on projected objects.", "rename <old> <new> [old2 new2 ...]") { }

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
            if (item is not ProjectedObject projected)
            {
                throw new InvalidOperationException("rename expects projected objects, for example from 'get { ... }'.");
            }

            yield return new ProjectedObject(projected.Fields
                .Select(field => renames.TryGetValue(field.Name, out var newName)
                    ? new ProjectedField(newName, field.SourcePath, field.Value)
                    : field)
                .ToArray());
        }
    }
}
