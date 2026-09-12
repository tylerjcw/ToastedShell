using Tosh.Language.Parsing;
using Tosh.Runtime;
using Tosh.Stdlib;

namespace Tosh.Tests;

/// <summary>
/// Every example in every built-in command's metadata is valid ToastScript.
/// </summary>
/// <remarks>
/// <para>
/// The 609 examples are the most-read code in the project: they are what
/// <c>help &lt;command&gt;</c> prints, what the command reference in the
/// specification is generated from, what the VS Code extension shows on hover, and
/// what the MCP metadata serves. Nothing checked them, and 18 did not parse.
/// </para>
/// <para>
/// Fifteen wrote a list literal with spaces — <c>[1 2 3] | permutations</c> — which
/// the language has never accepted; the separator is a comma, as the specification
/// says throughout. One used <c>{ |v| … }</c> block parameters, which is not
/// ToastScript syntax. One called a composed function as <c>$f $input</c> rather
/// than <c>$f($input)</c>.
/// </para>
/// <para>
/// The eighteenth was not the example's fault. <c>unfold</c>'s own argument
/// documentation says the callable returns <c>[value, next-state]</c> "or null to
/// stop", and the documented spelling could not stop it: an arrow body or block
/// whose value *is* <c>null</c> produces no pipeline value, so only an explicit
/// <c>return null</c> reached the command and every other form raised
/// <c>tosh.runtime.unfold_requires_single_result</c>. It now applies the canonical
/// value-context collapse for that one case.
/// </para>
/// </remarks>
public sealed class CommandExampleParseTests
{
    private static IReadOnlyList<CommandMetadata> Metadata()
    {
        var commands = new ShellCommandRegistry();
        BuiltInCommands.RegisterDefaults(commands);
        return CommandMetadataExporter.BuildMetadata(commands);
    }

    [Fact]
    public void Every_command_example_parses()
    {
        var broken = Metadata()
            .SelectMany(entry => (entry.Examples ?? []).Select(example => (entry.Name, example.Code)))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Code))
            .Select(pair => (pair.Name, pair.Code, errors: ToshParser.Parse(pair.Code, "<example>").Diagnostics))
            .Where(pair => pair.errors.Count > 0)
            .Select(pair => $"{pair.Name}: {pair.errors[0].Code}\n      {pair.Code}")
            .ToArray();

        Assert.True(
            broken.Length == 0,
            $"{broken.Length} command examples do not parse. They are what `help` prints, "
            + "what the command reference is generated from, and what the editor shows on "
            + "hover:\n  " + string.Join("\n  ", broken));
    }

    /// <summary>
    /// The negative control: a check that found no examples would pass the assertion
    /// above without asserting anything.
    /// </summary>
    [Fact]
    public void The_check_sees_the_whole_example_corpus()
    {
        var count = Metadata().Sum(entry => (entry.Examples ?? []).Count);

        Assert.True(count > 500, $"Only {count} examples were found; the extraction is broken.");
    }
}
