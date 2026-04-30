using System.Reflection;
using Tosh.Runtime;
using Tosh.Stdlib;
using Tosh.Language;
using Tosh.Language.Bridge;

namespace Tosh.Tests;

/// <summary>
/// Parity tests that compare every concrete <see cref="ShellCommand"/> subclass found via
/// reflection against the actual command registry built by a fresh <see cref="ToshEngine"/>.
///
/// Catches the "I added a command class but forgot to register it in BuiltInCommands" bug
/// class. A new <see cref="ShellCommand"/> subclass either has to be registered by the
/// default engine setup or be opted out via <see cref="IntentionallyUnregisteredAttribute"/>.
/// </summary>
public sealed class RegistryParityTests
{
    [Fact]
    public void Every_concrete_ShellCommand_subclass_is_registered_or_explicitly_opted_out()
    {
        var engine = new ToshEngine();
        var registeredTypes = engine.Runtime.Commands.All
            .Select(c => c.GetType())
            .ToHashSet();

        var assemblies = new[] { typeof(ShellCommand).Assembly, typeof(ToshEngine).Assembly };

        var missing = new List<string>();

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition) continue;
                if (!typeof(ShellCommand).IsAssignableFrom(type)) continue;
                if (type.GetCustomAttribute<IntentionallyUnregisteredAttribute>() is not null) continue;

                if (!registeredTypes.Contains(type))
                {
                    missing.Add(type.FullName ?? type.Name);
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "The following concrete ShellCommand subclasses are not registered in the default " +
            "engine and have no [IntentionallyUnregistered] opt-out:\n  - " +
            string.Join("\n  - ", missing) +
            "\nFix by adding `commands.Register(new YourCommand());` to BuiltInCommands.RegisterDefaults " +
            "(or to ToshEngine if it depends on the engine), or annotate the class with " +
            "[IntentionallyUnregistered(\"reason\")].");
    }

    [Fact]
    public void Default_engine_exposes_at_least_one_command_per_known_category()
    {
        // Smoke check: the registry isn't unexpectedly empty and a few well-known
        // commands resolve. Cheap sanity guard against accidental wholesale wipes.
        var engine = new ToshEngine();
        var registry = engine.Runtime.Commands;

        foreach (var name in new[] { "echo", "cd", "ls", "spawn", "scope", "help", "source" })
        {
            Assert.True(
                registry.TryGet(name, out _),
                $"Expected '{name}' to be present in the default command registry.");
        }
    }

    [Fact]
    public void Registered_aliases_resolve_to_their_canonical_command()
    {
        // Every alias declared via RegisterAlias must resolve through TryGet to the
        // exact same instance as its canonical command, and the canonical name must be
        // present in registry.All. Catches typo'd canonical names in BuiltInCommands.
        var engine = new ToshEngine();
        var registry = engine.Runtime.Commands;
        var canonicalsByName = registry.All.ToDictionary(c => c.Name, StringComparer.Ordinal);

        foreach (var (canonical, aliases) in registry.GetAliasMap())
        {
            Assert.True(
                canonicalsByName.TryGetValue(canonical, out var canonicalCmd),
                $"Alias canonical '{canonical}' is not in the registry.");

            foreach (var alias in aliases)
            {
                Assert.True(
                    registry.TryGet(alias, out var resolved),
                    $"Alias '{alias}' for '{canonical}' does not resolve via TryGet.");
                Assert.Same(canonicalCmd, resolved);
            }
        }
    }

    [Fact]
    public void Registered_aliases_appear_in_canonical_command_help_metadata()
    {
        // Aliases registered with RegisterAlias must surface in the canonical command's
        // help topic Aliases list — that's the user-visible signal that they're real
        // alternative names rather than separate commands.
        var engine = new ToshEngine();
        var aliasMap = engine.Runtime.Commands.GetAliasMap();
        if (aliasMap.Count == 0) return; // nothing registered, nothing to verify

        foreach (var (canonical, aliases) in aliasMap)
        {
            var topic = HelpCatalog.ResolveTopic(engine.Runtime, canonical);
            Assert.NotNull(topic);

            foreach (var alias in aliases)
            {
                Assert.Contains(
                    alias,
                    topic!.Aliases,
                    StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
