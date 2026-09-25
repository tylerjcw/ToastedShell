using System.Reflection;

using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The editor's language data keeps up with the language.
///
/// `language-data.json` is generated from <see cref="VsCodeMetadataEmitter"/>, whose command
/// list comes from the runtime and whose special variables are written by hand. So the
/// commands never drift and the variables silently did: the table described 25 members while
/// `$tosh` answered to 63, and every one of the other 38 was a name the editor could not
/// complete, describe or colour.
/// </summary>
public class VsCodeMetadataCurrencyTests
{
    /// <summary>Statics on the profile type, reached through the namespace but not state of it.</summary>
    private static readonly string[] NotVariables = ["Detected", "Unknown", "ShellTypeName"];

    private static object Runtime()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new Tosh.Language.ToshEngine(runtime.Language);
        engine.ExecuteToListAsync("$tosh").GetAwaiter().GetResult();
        Assert.NotNull(runtime.RuntimeNamespace);
        return runtime.RuntimeNamespace!;
    }

    /// <summary>Every `$tosh.*` member the shell answers to, as the editor would spell it.</summary>
    private static IEnumerable<string> LiveNames()
    {
        var root = (IShellRecordObject)Runtime();

        foreach (var (name, value) in root.GetMembers())
        {
            if (NotVariables.Contains(name)) { continue; }
            yield return $"$tosh.{name}";

            if (value is null) { continue; }

            // One level down is where the useful names are: `$tosh.Host.UserName` and friends.
            foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (NotVariables.Contains(property.Name)) { continue; }
                yield return $"$tosh.{name}.{property.Name}";
            }
        }
    }

    [Fact]
    public void Every_member_of_the_tosh_namespace_is_described_for_the_editor()
    {
        var described = VsCodeMetadataEmitter.SpecialVariableNames;

        var missing = LiveNames()
            .Where(name => !described.Contains(name))
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"the editor cannot describe {string.Join(", ", missing)} — add them to VsCodeMetadataEmitter.");
    }

    /// <summary>And nothing described has since been removed from the shell.</summary>
    [Fact]
    public void Nothing_described_for_the_editor_has_been_removed_from_the_shell()
    {
        var live = LiveNames().ToHashSet(StringComparer.Ordinal);

        // Only the `$tosh.` tree is checked here: `_`, `$env`, `$this` and `$value` are
        // language constructs rather than members of a namespace that can be walked.
        var gone = VsCodeMetadataEmitter.SpecialVariableNames
            .Where(name => name.StartsWith("$tosh.", StringComparison.Ordinal))
            .Where(name => name.Count(c => c == '.') <= 2)
            .Where(name => !live.Contains(name) && name != "$tosh.Config.Terminal")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            gone.Length == 0,
            $"the editor still describes {string.Join(", ", gone)}, which the shell no longer has.");
    }
}
