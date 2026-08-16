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
        {
            BuiltInCommands.RegisterDefaults(runtime.Commands);

            // Launching a program is the shell's job, so the shell supplies the factory
            // the language calls when a name resolves to something on PATH (`TOAST-0004`).
            runtime.ExternalCommands = ExternalProcessCommandFactory.Instance;
        };

        DisplayProfileRegistry.DefaultProfileRegistrar = static (registry, preferences) =>
            BuiltInDisplayProfiles.RegisterDefaults(registry, preferences);
    }
}
