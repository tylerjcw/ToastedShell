using Tosh.Runtime;

namespace Tosh.Stdlib;

/// <summary>
/// Supplies <see cref="ExternalProcessCommand"/> to the language layer, which resolves
/// the name but must not know the type that runs it.
/// </summary>
/// <remarks>
/// Stateless, so one instance serves every runtime. Registered by
/// <c>StdlibModuleInitializer</c> beside the built-in command set — a host that loads
/// Tōast without Tosh.Stdlib gets neither, which is the intended pairing: the commands
/// and the ability to launch programs are the same layer's responsibility.
/// </remarks>
internal sealed class ExternalProcessCommandFactory : IExternalCommandFactory
{
    internal static readonly ExternalProcessCommandFactory Instance = new();

    private ExternalProcessCommandFactory()
    {
    }

    public IExternalProcessCommand CreateExternalProcess(string name, string resolvedPath)
        => new ExternalProcessCommand(name, resolvedPath);
}
