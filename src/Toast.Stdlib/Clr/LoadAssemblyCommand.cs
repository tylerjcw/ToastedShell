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
                new KeyValuePair<string, object?>("Types", CountTypes(assembly)),
            ]);
        }
    }

    /// <summary>
    /// The number of types the assembly defines, without letting the count decide
    /// whether the load succeeded.
    /// </summary>
    /// <remarks>
    /// `TS-P2-96`. This was `assembly.GetTypes().Length`, called only to report a
    /// number — and <see cref="Assembly.GetTypes"/> resolves the whole type
    /// closure, so it throws when a referenced assembly is not loaded *yet*.
    /// Loading Avalonia's 25 assemblies alphabetically failed on
    /// `Avalonia.Controls` because `Avalonia.Remote.Protocol` had not been reached,
    /// though it sat in the same directory.
    ///
    /// The assembly is already in the load context when the throw happens, so the
    /// load itself had succeeded; only the reporting failed. A partial load carries
    /// the types it did resolve, so the count degrades to those rather than taking
    /// the whole command down with it.
    /// </remarks>
    private static int CountTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes().Length;
        }
        catch (ReflectionTypeLoadException partial)
        {
            return partial.Types.Count(type => type is not null);
        }
    }
}
