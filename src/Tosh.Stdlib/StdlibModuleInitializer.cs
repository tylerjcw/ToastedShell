using System.Runtime.CompilerServices;
using Tosh.Runtime;

namespace Tosh.Stdlib;

/// <summary>
/// Wires Tosh.Stdlib's built-in commands and display profiles into Tosh.Runtime's
/// runtime factories. Runs automatically when the Tosh.Stdlib assembly is
/// loaded; consumers that reference Tosh.Stdlib (transitively or directly) get
/// the full default command set with no extra setup.
/// </summary>
internal static class StdlibModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        ToshRuntime.DefaultCommandRegistrar = static runtime =>
            BuiltInCommands.RegisterDefaults(runtime.Commands);

        DisplayProfileRegistry.DefaultProfileRegistrar = static (registry, preferences) =>
            BuiltInDisplayProfiles.RegisterDefaults(registry, preferences);
    }
}
