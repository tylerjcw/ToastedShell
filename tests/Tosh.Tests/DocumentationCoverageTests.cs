using System.Reflection;
using Tosh.Core;
using Tosh.Core.Commands;
using Tosh.Language;

namespace Tosh.Tests;

/// <summary>
/// Documentation completeness checks. Every concrete <see cref="ShellCommand"/> registered
/// in the default engine must publish enough metadata for help, the LSP, the spec, and the
/// MCP server. We test the *resolved* <see cref="ShellCommand.GetMetadata"/> output rather
/// than raw attributes so factory-style commands (e.g. <c>PathPredicateCommand</c>) that
/// fill metadata at runtime still count.
///
/// Tiered enforcement:
/// 1. **Hard fail** — every command must have a non-default Category and at least one Example.
/// 2. **Allowlist with shrinking** — Output description has a frozen baseline of currently
///    missing commands; net-new gaps fail. The allowlist is asserted not to drift below the
///    baseline (i.e. when you fix a command, you must remove it from the allowlist).
/// 3. **Opt-out** — annotate with <see cref="UndocumentedForAttribute"/> for legitimate cases.
/// </summary>
public sealed class DocumentationCoverageTests
{
    [Fact]
    public void Every_registered_command_has_a_non_default_Category()
    {
        var (registry, _) = GetRegistryAndEntries();

        var missing = new List<string>();
        foreach (var command in registry.All.OfType<ShellCommand>())
        {
            if (HasOptOut(command, "category")) continue;
            var metadata = command.GetMetadata();
            // CommandMetadata defaults Category to "Shell" when no [CommandCategory] attribute
            // is present. We allow "Shell" only when the [CommandCategory("Shell")] attribute
            // is explicitly applied — otherwise the command is silently uncategorized.
            var explicitCategory = command.GetType().GetCustomAttribute<CommandCategoryAttribute>();
            if (explicitCategory is null && string.Equals(metadata.Category, "Shell", StringComparison.OrdinalIgnoreCase))
            {
                missing.Add(command.Name);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"The following registered commands are missing an explicit [CommandCategory] attribute:\n  - " +
            string.Join("\n  - ", missing) +
            "\nAdd `[CommandCategory(\"<group>\")]` to the class, or annotate with " +
            "`[UndocumentedFor(\"category\", \"reason\")]` if there is a real reason to omit it.");
    }

    [Fact]
    public void Every_registered_command_has_at_least_one_Example()
    {
        var (registry, _) = GetRegistryAndEntries();

        var missing = new List<string>();
        foreach (var command in registry.All.OfType<ShellCommand>())
        {
            if (HasOptOut(command, "example")) continue;
            var metadata = command.GetMetadata();
            if (metadata.Examples.Count == 0)
            {
                missing.Add(command.Name);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"The following registered commands have no [CommandExample] entries:\n  - " +
            string.Join("\n  - ", missing) +
            "\nAdd at least one `[CommandExample(\"...\", Title = \"...\")]` to the class, or annotate with " +
            "`[UndocumentedFor(\"example\", \"reason\")]` if there is a real reason to omit it.");
    }

    [Fact]
    public void Output_description_baseline_does_not_grow()
    {
        var (registry, _) = GetRegistryAndEntries();

        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in registry.All.OfType<ShellCommand>())
        {
            if (HasOptOut(command, "output")) continue;
            var metadata = command.GetMetadata();
            if (string.IsNullOrWhiteSpace(metadata.Output))
            {
                missing.Add(command.Name);
            }
        }

        var newGaps = missing.Except(OutputBaseline, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(
            newGaps.Count == 0,
            $"The following NEW commands are missing a [CommandOutput] description:\n  - " +
            string.Join("\n  - ", newGaps) +
            "\nAdd `[CommandOutput(\"...\")]` describing what the command emits, or annotate with " +
            "`[UndocumentedFor(\"output\", \"reason\")]` if there is a real reason to omit it.");

        // Equally important: don't let the baseline drift out of sync with reality. If a
        // command in the baseline now has Output, the developer must remove it from the list
        // so coverage shrinks over time rather than ratcheting up.
        var fixedSinceBaseline = OutputBaseline.Except(missing, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(
            fixedSinceBaseline.Count == 0,
            $"The following commands now have [CommandOutput] but are still listed in OutputBaseline:\n  - " +
            string.Join("\n  - ", fixedSinceBaseline) +
            $"\nRemove them from {nameof(DocumentationCoverageTests)}.{nameof(OutputBaseline)} in " +
            "tests/Tosh.Tests/DocumentationCoverageTests.cs.");
    }

    [Fact]
    public void Every_registered_command_has_a_Stdlib_or_ShellOnly_classification()
    {
        var (registry, _) = GetRegistryAndEntries();

        var missing = new List<string>();
        foreach (var command in registry.All.OfType<ShellCommand>())
        {
            if (HasOptOut(command, "stdlib")) continue;
            var type = command.GetType();
            var hasStdlib = type.GetCustomAttribute<StdlibAttribute>() is not null;
            var hasShellOnly = type.GetCustomAttribute<ShellOnlyAttribute>() is not null;
            if (!hasStdlib && !hasShellOnly)
            {
                missing.Add(command.Name);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"The following registered commands have neither [Stdlib(StdlibCategory.X)] nor [ShellOnly]:\n  - " +
            string.Join("\n  - ", missing) +
            "\nAdd `[Stdlib(StdlibCategory.<bucket>)]` to mark the future Tosh.Stdlib.* assembly bucket, " +
            "or `[ShellOnly]` for REPL-only commands. See docs/COMPILED_TOSH.md for the bucket layout. " +
            "Use `[UndocumentedFor(\"stdlib\", \"reason\")]` only for legitimate cases.");
    }

    [Fact]
    public void Description_and_Usage_are_non_empty_and_not_placeholder()
    {
        var (registry, _) = GetRegistryAndEntries();

        var problems = new List<string>();
        foreach (var command in registry.All.OfType<ShellCommand>())
        {
            var metadata = command.GetMetadata();
            if (string.IsNullOrWhiteSpace(metadata.Description) || metadata.Description.Length < 8)
            {
                problems.Add($"{command.Name}: Description is empty or too short ('{metadata.Description}')");
            }
            if (string.IsNullOrWhiteSpace(metadata.Usage))
            {
                problems.Add($"{command.Name}: Usage is empty");
            }
            // Catch obvious placeholder text.
            foreach (var placeholder in new[] { "TODO", "FIXME", "XXX", "todo!", "..." })
            {
                if (metadata.Description.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{command.Name}: Description contains placeholder '{placeholder}'");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            $"Description/Usage problems:\n  - " + string.Join("\n  - ", problems));
    }

    private static (ShellCommandRegistry registry, IReadOnlyList<CommandMetadata> entries) GetRegistryAndEntries()
    {
        var engine = new ToshEngine();
        var entries = CommandMetadataExporter.BuildMetadata(engine.Runtime.Commands);
        return (engine.Runtime.Commands, entries);
    }

    private static bool HasOptOut(IShellCommand command, string field)
    {
        var optOuts = command.GetType().GetCustomAttributes<UndocumentedForAttribute>(inherit: false);
        return optOuts.Any(a => string.Equals(a.Field, field, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Frozen baseline of commands currently missing a <c>[CommandOutput]</c> description.
    /// Net-new misses outside this list fail the build; commands listed here that have since
    /// been documented also fail (so the list shrinks with every fix). The eventual goal is
    /// an empty baseline.
    /// </summary>
    private static readonly HashSet<string> OutputBaseline = new(StringComparer.Ordinal)
    {
    };
}
