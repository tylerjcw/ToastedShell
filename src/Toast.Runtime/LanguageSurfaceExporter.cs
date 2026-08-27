using System.Text.Encodings.Web;
using System.Text.Json;

namespace Tosh.Runtime;

/// <summary>
/// <see cref="LanguageSurface"/> as JSON, for consumers that cannot read C#
/// (<c>TS-P2-10</c>).
/// </summary>
/// <remarks>
/// <para>
/// The VS Code grammar generator is the reason this exists. It is a separate program
/// in a separate language, so the registry could not simply hand it a set the way the
/// CLI highlighter and the Tome colorizer take one — it kept its own copy of 101
/// language words, and that copy drifted.
/// </para>
/// <para>
/// Word categories, not prose. Descriptions are editorial and belong with whichever
/// consumer renders them; what must not differ between consumers is *which words
/// exist*, which is exactly what this emits.
/// </para>
/// </remarks>
public static class LanguageSurfaceExporter
{
    public static string ExportJson()
    {
        // Ordinal-sorted so the output is stable: a consumer that checks its
        // generated artefact into git should see a diff only when the language
        // changes, never because a dictionary enumerated differently.
        var words = LanguageSurface.Words
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                pair => pair.Key,
                pair => KindNames(pair.Value),
                StringComparer.Ordinal);

        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["words"] = words,
            ["keywords"] = Sorted(LanguageSurface.Keywords),
            ["controlFlow"] = Sorted(LanguageSurface.ControlFlow),
            ["typeDeclarations"] = Sorted(LanguageSurface.TypeDeclarations),
            ["operatorWords"] = Sorted(LanguageSurface.OperatorWords),
            ["constants"] = Sorted(LanguageSurface.Constants),
            ["languageForms"] = Sorted(LanguageSurface.LanguageForms),
            ["modifiers"] = Sorted(LanguageSurface.Modifiers),
            ["subcommandModifiers"] = Sorted(LanguageSurface.SubcommandModifiers),
            ["memberModifierAliases"] = LanguageSurface.MemberModifierAliases
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),

            // `TS-P2-78`. Operators travel with the word surface so a consumer outside
            // .NET gets one answer about the language rather than two — the VS Code
            // grammar generator already reads this endpoint for its keyword alternations
            // and can take operator scopes from the same place.
            ["operators"] = OperatorSurface.Operators
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal),
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static string[] Sorted(IReadOnlySet<string> words) =>
        words.OrderBy(word => word, StringComparer.Ordinal).ToArray();

    private static string[] KindNames(LanguageWordKind kind) =>
        Enum.GetValues<LanguageWordKind>()
            .Where(value => value != LanguageWordKind.None && kind.HasFlag(value))
            .Select(value => value.ToString())
            .ToArray();
}
