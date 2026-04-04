using System.Reflection;
using System.Runtime.Loader;

namespace Tosh.Core.Commands;

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
