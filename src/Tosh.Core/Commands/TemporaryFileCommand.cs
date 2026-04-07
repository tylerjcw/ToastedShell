namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
[CommandArgument("prefix", "Optional prefix for the file name. Defaults to 'tosh'.", Required = false)]
[CommandArgument("extension", "Optional file extension.", Required = false)]
[CommandExample("tempfile")]
[CommandExample("tempfile data .csv", Title = "Custom prefix and extension")]
[CommandOutput("Returns a FileSystemEntry for the created temporary file.")]
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
