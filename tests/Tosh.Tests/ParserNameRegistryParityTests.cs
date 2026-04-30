using System.Text.RegularExpressions;
using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

/// <summary>
/// The parser hardcodes a number of command names (e.g. in <c>IsPredicateExpressionCommand</c>,
/// <c>TryGetCurrentItemExpressionArgumentIndex</c>) so it can apply special parsing rules to them.
/// If somebody renames or removes a command in the registry without updating the parser, those
/// branches go stale and silently stop firing. This test scans the parser source for hardcoded
/// command-name comparisons and verifies each one resolves to a registered command (canonical
/// or alias).
/// </summary>
public sealed class ParserNameRegistryParityTests
{
    [Fact]
    public void Every_hardcoded_command_name_in_ToshParser_resolves_via_the_registry()
    {
        var parserPath = LocateParserSource();
        var source = File.ReadAllText(parserPath);

        // Capture string literals on the right-hand side of `string.Equals(commandName, "X", …)`
        // and `string.Equals(nameToken.Text, "X", …)`. These are the parser's command-name
        // comparison sites; literals on `Current.Text` are typically operator tokens and are
        // intentionally NOT scanned here (they have their own parity check).
        var pattern = new Regex(
            @"string\.Equals\(\s*(?:commandName|nameToken\.Text)\s*,\s*""([a-z][a-z0-9\-]*)""",
            RegexOptions.IgnoreCase);

        var hardcodedNames = pattern.Matches(source)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(hardcodedNames); // sanity: regex actually matched

        var engine = new ToshEngine();
        var registry = engine.Runtime.Commands;

        var unresolved = hardcodedNames
            .Where(name => !registry.TryGet(name, out _))
            .ToList();

        Assert.True(
            unresolved.Count == 0,
            $"The following names are hardcoded in {Path.GetFileName(parserPath)} but do not resolve via the command registry:\n  - " +
            string.Join("\n  - ", unresolved) +
            "\nEither register the missing command/alias, or remove the stale parser branch.");
    }

    private static string LocateParserSource()
    {
        // Walk upwards from the test assembly to the repo root, then locate the parser file.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Tosh.Language", "Parsing", "ToshParser.cs");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate src/Tosh.Language/Parsing/ToshParser.cs from the test runtime working directory.");
    }
}
