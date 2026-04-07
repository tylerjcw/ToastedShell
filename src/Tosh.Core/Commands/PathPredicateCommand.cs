namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
public sealed class PathPredicateCommand : ShellCommand
{
    private readonly Func<string, bool> _predicate;
    private readonly string _fieldName;

    public PathPredicateCommand(string name, string description, string fieldName, Func<string, bool> predicate)
        : base(name, description, $"{name} <path> [path...]")
    {
        _fieldName = fieldName;
        _predicate = predicate;
    }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var paths = await ShellPathArguments.CollectAsync(context, context.Arguments, context.CancellationToken);

        foreach (var path in paths)
        {
            yield return ShellRecordUtilities.CreateExpando(
            [
                new KeyValuePair<string, object?>("Path", path),
                new KeyValuePair<string, object?>(_fieldName, _predicate(path)),
            ]);
        }
    }
}
