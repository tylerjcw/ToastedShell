namespace Tosh.Language.Parsing;

/// <summary>
/// Structured documentation extracted from consecutive <c>##</c> comment lines
/// preceding a declaration.
/// </summary>
public sealed record DocComment(
    string Description,
    IReadOnlyDictionary<string, string> Parameters,
    string? Returns,
    IReadOnlyList<string> Examples,
    string? Deprecated = null,
    bool IsDeprecated = false,
    IReadOnlyList<string>? SeeAlso = null,
    string? Since = null,
    IReadOnlyList<string>? Throws = null)
{
    /// <summary>
    /// Parses a sequence of doc-comment token texts into a structured <see cref="DocComment"/>.
    /// </summary>
    public static DocComment? Parse(IReadOnlyList<SyntaxToken> tokens)
    {
        if (tokens.Count == 0) return null;

        var descriptionLines = new List<string>();
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? returns = null;
        var examples = new List<string>();
        string? deprecated = null;
        var isDeprecated = false;
        List<string>? seeAlso = null;
        string? since = null;
        List<string>? throws = null;
        string? lastParamName = null;
        bool inExampleBlock = false;
        List<string>? currentExampleLines = null;

        foreach (var token in tokens)
        {
            var line = token.Text;

            // Multiline @example block handling
            if (inExampleBlock && (line.StartsWith("  ") || line == ""))
            {
                currentExampleLines!.Add(line.TrimStart());
                continue;
            }
            if (inExampleBlock)
            {
                // End of example block
                examples.Add(string.Join("\n", currentExampleLines!));
                inExampleBlock = false;
                currentExampleLines = null;
                // fall through to normal tag handling for this line
            }

            if (line.StartsWith("@param=", StringComparison.Ordinal))
            {
                var rest = line.AsSpan(7);
                var spaceIndex = rest.IndexOf(' ');
                if (spaceIndex >= 0)
                {
                    var name = rest[..spaceIndex].ToString();
                    var desc = rest[(spaceIndex + 1)..].ToString().Trim();
                    parameters[name] = desc;
                    lastParamName = name;
                }
                else
                {
                    var name = rest.ToString();
                    parameters[name] = string.Empty;
                    lastParamName = name;
                }
            }
            else if (line.StartsWith("@returns ", StringComparison.Ordinal))
            {
                returns = line[9..].Trim();
                lastParamName = null;
            }
            else if (string.Equals(line, "@returns", StringComparison.Ordinal))
            {
                returns = string.Empty;
                lastParamName = null;
            }
            else if (line.StartsWith("@example ", StringComparison.Ordinal))
            {
                // Single-line example
                examples.Add(line[9..].Trim());
                lastParamName = null;
            }
            else if (string.Equals(line, "@example", StringComparison.Ordinal))
            {
                // Start multiline example block
                inExampleBlock = true;
                currentExampleLines = new List<string>();
                lastParamName = null;
            }
            else if (line.StartsWith("@deprecated ", StringComparison.Ordinal))
            {
                deprecated = line[12..].Trim();
                isDeprecated = true;
                lastParamName = null;
            }
            else if (string.Equals(line, "@deprecated", StringComparison.Ordinal))
            {
                isDeprecated = true;
                lastParamName = null;
            }
            else if (line.StartsWith("@see ", StringComparison.Ordinal))
            {
                seeAlso ??= new List<string>();
                seeAlso.Add(line[5..].Trim());
                lastParamName = null;
            }
            else if (line.StartsWith("@since ", StringComparison.Ordinal))
            {
                since = line[7..].Trim();
                lastParamName = null;
            }
            else if (line.StartsWith("@throws ", StringComparison.Ordinal))
            {
                throws ??= new List<string>();
                throws.Add(line[8..].Trim());
                lastParamName = null;
            }
            else if (string.Equals(line, "@throws", StringComparison.Ordinal))
            {
                throws ??= new List<string>();
                throws.Add(string.Empty);
                lastParamName = null;
            }
            else if (lastParamName is not null && line.StartsWith("  ", StringComparison.Ordinal))
            {
                // Multi-line @param continuation: indented lines append to the last parameter
                parameters[lastParamName] = parameters[lastParamName] + " " + line.Trim();
            }
            else
            {
                descriptionLines.Add(line);
                lastParamName = null;
            }
            // If file ends while in an example block, flush it
            if (inExampleBlock && currentExampleLines is not null)
            {
                examples.Add(string.Join("\n", currentExampleLines));
            }
        }

        var description = string.Join(" ", descriptionLines).Trim();

        if (description.Length == 0
            && parameters.Count == 0
            && returns is null
            && examples.Count == 0
            && !isDeprecated
            && seeAlso is null
            && since is null
            && throws is null)
        {
            return null;
        }

        return new DocComment(
            description,
            parameters,
            returns,
            examples,
            deprecated,
            isDeprecated,
            (IReadOnlyList<string>?)seeAlso,
            since,
            (IReadOnlyList<string>?)throws);
    }
}
