using System.Reflection;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The command-table surface the language depends on.
///
/// `TOAST-0006`, stage 2a. `ShellCommandRegistry` has eleven public members and the
/// language uses six. `ICommandTable` names those six so a `ToastRuntime` can hold a
/// command table without holding the shell's registry.
///
/// The point of pinning the member list is that this interface is the boundary. Widening
/// it is how the language quietly reacquires shell capability, and a widening that
/// nothing objects to is indistinguishable from one nobody meant.
/// </summary>
public sealed class CommandTableBoundaryTests
{
    /// <summary>
    /// Exactly the six the language uses — no more.
    /// </summary>
    /// <remarks>
    /// Two of these are mutations, and that is deliberate rather than a leak. A `global`
    /// or `export` function declaration must put a name in the runtime table or
    /// `export func` would not work, and `forget` must be able to take one out. An
    /// earlier plan for a read-only view was written on the belief that the language only
    /// resolved names; it does not, and that plan would not have compiled.
    /// </remarks>
    [Fact]
    public void The_interface_names_exactly_the_operations_the_language_uses()
    {
        var members = typeof(ICommandTable)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(member => member is not MethodInfo { IsSpecialName: true })
            .Select(member => member.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "All", "AllNames", "GetAliasMap", "Register" + "OrReplace", "Remove", "TryGet" },
            members);
    }

    /// <summary>
    /// The shell's registry satisfies it, which is what makes the interface usable rather
    /// than aspirational.
    /// </summary>
    [Fact]
    public void The_shell_registry_implements_it()
    {
        Assert.True(typeof(ICommandTable).IsAssignableFrom(typeof(ShellCommandRegistry)));

        ICommandTable table = new ShellCommandRegistry();
        Assert.False(table.TryGet("nothing-registered-here", out _));
    }

    /// <summary>
    /// Members deliberately left off: declaring an alias and throw-on-missing lookup are
    /// shell concerns. Reading the alias map is not — `ScopedCommandView` needs it to
    /// report what a scope can see — which is why `GetAliasMap` is on and `RegisterAlias`
    /// is not.
    /// </summary>
    [Fact]
    public void Alias_declaration_and_throwing_lookup_stay_on_the_registry()
    {
        var names = typeof(ICommandTable).GetMembers().Select(member => member.Name).ToArray();

        Assert.DoesNotContain("RegisterAlias", names);
        Assert.DoesNotContain("GetAliases", names);
        Assert.DoesNotContain("Get", names);
        Assert.DoesNotContain("Register", names);
    }

    /// <summary>
    /// The behaviour the mutating half exists for, end to end: an exported declaration is
    /// callable afterwards. If the interface ever loses `RegisterOrReplace`, this is what
    /// says so in terms a reader recognises.
    /// </summary>
    [Fact]
    public async Task An_exported_function_becomes_a_callable_name()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("export func greet() => \"hi\"\ngreet");

        Assert.Equal("hi", results[^1]?.ToString());
        Assert.True(runtime.Commands.TryGet("greet", out _));
    }
}
