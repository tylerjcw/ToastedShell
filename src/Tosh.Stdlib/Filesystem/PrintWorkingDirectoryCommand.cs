using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[CommandCategory("Filesystem")]
[CommandExample("pwd", Title = "Print the current working directory")]
[CommandExample("pwd | get .FullName", Title = "Get the full path as a string")]
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
