namespace Tosh.Core.Commands;

public sealed class TemporaryFileCommand : ShellCommand
{
    public TemporaryFileCommand()
        : base("tempfile", "Creates a temporary file and returns it as a file system entry.", "tempfile [prefix] [extension]") { }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var prefix = context.Arguments.Count > 0 ? context.Arguments[0]?.ToString() ?? "tosh" : "tosh";
        var extension = context.Arguments.Count > 1 ? context.Arguments[1]?.ToString() ?? string.Empty : string.Empty;

        if (!string.IsNullOrWhiteSpace(extension) && !extension.StartsWith(".", StringComparison.Ordinal))
        {
            extension = "." + extension;
        }

        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}{extension}");
        using (File.Create(path))
        {
        }

        return AsyncEnumerableExtensions.FromEnumerable([FileSystemEntry.From(new FileInfo(path), preferLongDisplay: true)]);
    }
}
