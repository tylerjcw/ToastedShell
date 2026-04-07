namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
[CommandExample("pwd")]
[CommandOutput("Returns the current working directory as a DirectoryInfo object.")]
public sealed class PrintWorkingDirectoryCommand : ShellCommand
{
    public PrintWorkingDirectoryCommand()
        : base("pwd", "Returns the current directory as a DirectoryInfo object.", "pwd") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        yield return new DirectoryInfo(context.Runtime.CurrentDirectory);
    }
}
