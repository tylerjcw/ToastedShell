using System.Collections.Concurrent;

using Tosh.Tui.Widgets;

namespace Tosh.Tui.Declarative;

/// <summary>
/// Widgets contributed from outside the framework, so markup can name them.
/// </summary>
/// <remarks>
/// <para>
/// A script can write a widget — <c>class Banner extends TuiWidget</c> — and hand it to a
/// container, because a declared class that extends a CLR type is one. Markup was the half
/// that could not: a node's name is looked up in the registry, the registry holds only what
/// the framework put there, and there was no way to add to it from outside. So the same
/// widget was usable with <c>new</c> and unnameable in a markup tree, which is a strange
/// thing for a framework to say about the widget you just wrote.
/// </para>
/// <para>
/// Contributions are held here rather than on the registry because a registry is built
/// fresh for each screen: a contribution has to outlive the screen that did not yet exist
/// when the script made it.
/// </para>
/// <para>
/// A contribution wins over a built-in of the same name. Shadowing <c>text</c> is almost
/// certainly a mistake, and refusing it is a rule that would also stop someone replacing a
/// widget deliberately — which is the more useful of the two things to allow.
/// </para>
/// </remarks>
public static class TuiWidgetContributions
{
    private static readonly ConcurrentDictionary<string, Func<TuiWidgetSpec, TuiBuildContext, TuiWidget>> Factories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The contributed names, for diagnostics.</summary>
    public static IReadOnlyList<string> Names =>
        [.. Factories.Keys.OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>Makes a widget nameable in markup, replacing any previous contribution.</summary>
    public static void Contribute(string name, Func<TuiWidgetSpec, TuiBuildContext, TuiWidget> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);

        Factories[name] = factory;
    }

    /// <summary>Removes a contribution. Returns whether there was one.</summary>
    public static bool Withdraw(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Factories.TryRemove(name, out _);
    }

    /// <summary>Forgets every contribution.</summary>
    /// <remarks>
    /// For a test that must not leak a name into the next one, and for a script that wants
    /// its screen built from the framework's own set.
    /// </remarks>
    public static void Clear() => Factories.Clear();

    /// <summary>Folds the contributions into a registry about to build a tree.</summary>
    internal static void ApplyTo(TuiWidgetRegistry registry)
    {
        foreach (var entry in Factories)
        {
            registry.Register(entry.Key, entry.Value);
        }
    }
}
