namespace Tosh.Core.Commands;

public sealed class MakeTempDirectoryCommand : ShellCommand
{
    public MakeTempDirectoryCommand()
        : base("mkdir-temp", "Creates a temporary directory and returns it as a file system entry.", "mkdir-temp [prefix]") { }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var prefix = context.Arguments.Count > 0 ? context.Arguments[0]?.ToString() ?? "tosh" : "tosh";
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return AsyncEnumerableExtensions.FromEnumerable([FileSystemEntry.From(new DirectoryInfo(path), preferLongDisplay: true)]);
    }
}
