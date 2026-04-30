using System.Reflection;
using System.Runtime.Loader;

using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[CommandCategory("CLR")]
[CommandArgument("path", "One or more file paths to .NET assemblies to load.")]
[CommandExample("load-assembly ./MyPlugin.dll", Title = "Load a plugin assembly")]
[CommandExample("load-assembly Newtonsoft.Json.dll System.Data.dll", Title = "Load multiple assemblies")]
[CommandOutput("Assembly objects for each loaded assembly.")]
public sealed class LoadAssemblyCommand : ShellCommand
{
    public LoadAssemblyCommand()
        : base("load-assembly", "Loads a .NET assembly from disk into the current process.", "load-assembly <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var paths = await ShellPathArguments.CollectAsync(context, context.Arguments, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("load-assembly requires at least one assembly path or pipeline input.");
        }

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Assembly '{path}' does not exist.");
            }

            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            yield return ShellRecordUtilities.CreateExpando(
            [
                new KeyValuePair<string, object?>("Name", assembly.GetName().Name),
                new KeyValuePair<string, object?>("FullName", assembly.FullName),
                new KeyValuePair<string, object?>("Path", path),
                new KeyValuePair<string, object?>("Types", assembly.GetTypes().Length),
            ]);
        }
    }
}
