namespace Tosh.Core.Commands;

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
            yield return new ProjectedObject(
            [
                new ProjectedField("Path", "Path", path),
                new ProjectedField(_fieldName, _fieldName, _predicate(path)),
            ]);
        }
    }
}
