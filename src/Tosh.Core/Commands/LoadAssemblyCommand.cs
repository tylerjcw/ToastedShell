using System.Reflection;
using System.Runtime.Loader;

namespace Tosh.Core.Commands;

public sealed class LoadAssemblyCommand : ShellCommand
{
    public LoadAssemblyCommand()
        : base("load-assembly", "Loads a .NET assembly from disk into the current process.", "load-assembly <path> [path...]") { }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var results = new List<object?>();

        foreach (var rawPath in context.Arguments.Select(argument => argument?.ToString()).Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>())
        {
            var path = PathUtilities.ResolvePath(context.Runtime.CurrentDirectory, rawPath);

            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Assembly '{path}' does not exist.");
            }

            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            results.Add(new ProjectedObject(
            [
                new ProjectedField("Name", "Name", assembly.GetName().Name),
                new ProjectedField("FullName", "FullName", assembly.FullName),
                new ProjectedField("Path", "Path", assembly.Location),
                new ProjectedField("Types", "Types", assembly.GetTypes().Length),
            ]));
        }

        return AsyncEnumerableExtensions.FromEnumerable(results);
    }
}
