using System.Reflection;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The language must not depend on the shell — checked through the assembly graph, not
/// by reading project files.
///
/// `TOAST-0006`. `TOAST-0004` removed the one direct coupling (`ExternalProcessCommand`
/// at two call sites) and `ExternalCommandBoundaryTests` guards that. This guards the
/// *transitive* graph, which is where the interesting violations hide: `Tosh.Runtime`
/// referenced `Tosh.Tui`, so `Tosh.Language` depended on terminal UI code without
/// naming it anywhere, and nothing in the language project would have shown it.
///
/// Two files caused that edge — a one-line `global using` alias, and the config-browser
/// schema whose only consumer was a CLI screen. Roughly a thousand lines of shell code
/// sitting in the runtime, reached by the language for free.
/// </summary>
public sealed class AssemblyBoundaryTests
{
    /// <summary>
    /// Assemblies that belong to the shell. A language assembly reaching any of these,
    /// however indirectly, is the boundary failing.
    /// </summary>
    private static readonly string[] ShellAssemblies =
    [
        // `TOAST-0006`. `Tosh.Runtime` joined this list when the assembly was divided: it is
        // now display, help, jobs, session configuration and the composition root, while the
        // value model the language needs is `Toast.Runtime`. Until the split this file could
        // not name it — it was the assembly both halves shared — so the boundary was true by
        // inspection and enforced by nothing.
        "Tosh.Runtime",
        "Tosh.Tui",
        "Tosh.Stdlib",
        "Tosh.Cli",
        "Tosh.Tome",
        "Tosh.Crumb",
    ];

    /// <summary>
    /// Every `Tosh.*` assembly reachable from <paramref name="assembly"/>, following
    /// references transitively.
    /// </summary>
    /// <remarks>
    /// Reads the emitted metadata rather than the `.csproj`, so an unused project
    /// reference does not count and a real dependency cannot be hidden behind one. The
    /// compiler drops references nothing uses, which makes this a statement about what
    /// the code actually needs.
    /// </remarks>
    private static HashSet<string> TransitiveToshReferences(Assembly assembly)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>();
        queue.Enqueue(assembly);

        while (queue.Count > 0)
        {
            foreach (var reference in queue.Dequeue().GetReferencedAssemblies())
            {
                // `TOAST-0006`. Both prefixes, or the walk stops at the language's own main
                // dependency: `Toast.Runtime` does not begin with `Tosh.`, so filtering on that
                // alone would follow nothing past `Tosh.Language` and every assertion built on
                // this walk would pass by finding nothing.
                var name = reference.Name;
                if (name is null
                    || !(name.StartsWith("Tosh.", StringComparison.Ordinal)
                        || name.StartsWith("Toast.", StringComparison.Ordinal)))
                {
                    continue;
                }
                if (!seen.Add(name)) continue;

                try
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
                catch (Exception exception) when (exception is FileNotFoundException or BadImageFormatException)
                {
                    // Not deployed beside the tests; its own references cannot be walked,
                    // but the name is recorded above, which is what the assertion reads.
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// The language assembly, transitively, must reach no shell assembly.
    /// </summary>
    [Fact]
    public void The_language_does_not_reach_the_shell()
    {
        var reachable = TransitiveToshReferences(typeof(ToshEngine).Assembly);
        var violations = ShellAssemblies.Where(reachable.Contains).ToList();

        Assert.True(
            violations.Count == 0,
            "Tosh.Language transitively references shell assemblies: " +
            string.Join(", ", violations) +
            "\nReached through: " + string.Join(", ", reachable.OrderBy(n => n, StringComparer.Ordinal)) +
            "\n\nThis is `TOAST-0006`. The edge is often not in Tosh.Language itself — it was " +
            "Tosh.Runtime -> Tosh.Tui last time, which the language inherited without naming it.");
    }

    /// <summary>
    /// And the runtime, which is the assembly the boundary actually runs through. It
    /// holds the value model the language needs; anything shell-shaped in it leaks
    /// upward.
    /// </summary>
    [Fact]
    public void The_runtime_does_not_reach_the_shell()
    {
        var reachable = TransitiveToshReferences(typeof(ToshRuntime).Assembly);
        var violations = ShellAssemblies.Where(reachable.Contains).ToList();

        Assert.True(
            violations.Count == 0,
            "Tosh.Runtime transitively references shell assemblies: " + string.Join(", ", violations));
    }

    /// <summary>
    /// The non-vacuity check. Both assertions above are satisfied by an empty set, so a
    /// walk that silently returned nothing would report a clean boundary — and this test
    /// would be worse than absent, because it would look like evidence.
    /// </summary>
    [Fact]
    public void The_reference_walk_actually_finds_references()
    {
        var reachable = TransitiveToshReferences(typeof(ToshEngine).Assembly);

        // The language reaches the value model, and that is the whole of what it reaches on
        // this side of the division.
        Assert.Contains("Toast.Runtime", reachable);
        Assert.True(
            reachable.Count >= 2,
            "The transitive walk found almost nothing, so the boundary assertions above " +
            "prove nothing. Found: " + string.Join(", ", reachable));
    }

    /// <summary>
    /// The control: the walk does detect a shell assembly when one is genuinely present.
    /// Run against the test assembly itself, which references the shell on purpose.
    /// </summary>
    [Fact]
    public void The_walk_detects_a_shell_reference_when_there_is_one()
    {
        var reachable = TransitiveToshReferences(typeof(AssemblyBoundaryTests).Assembly);

        Assert.True(
            ShellAssemblies.Any(reachable.Contains),
            "The test assembly references the shell, so a walk that reports otherwise is " +
            "broken rather than reassuring. Found: " +
            string.Join(", ", reachable.OrderBy(n => n, StringComparer.Ordinal)));
    }
}
