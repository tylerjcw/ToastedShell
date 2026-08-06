namespace Tosh.Language.Parsing;

/// <summary>
/// Structured documentation extracted from consecutive <c>##</c> comment lines
/// preceding a declaration.
///
/// Tag names follow the standard ECMA-334 / .NET XML doc-comment vocabulary
/// (<c>summary</c>, <c>remarks</c>, <c>param</c>, <c>typeparam</c>,
/// <c>returns</c>, <c>value</c>, <c>example</c>, <c>seealso</c>,
/// <c>exception</c>, <c>para</c>) so doc-comments can be lifted directly into
/// a CLR-visible <c>&lt;assembly&gt;.xml</c> sidecar without renaming.
///
/// Two Tosh-only tags have no XML counterpart and are preserved as-is:
/// <c>@deprecated</c> (drives <c>[Obsolete]</c>) and <c>@since</c>.
///
/// For backwards compatibility, the older tag spellings
/// <c>@see</c> and <c>@throws</c> are still accepted as silent aliases for
/// <c>@seealso</c> and <c>@exception</c> respectively.
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
    IReadOnlyList<string>? Throws = null,
    string? Remarks = null,
    IReadOnlyDictionary<string, string>? TypeParameters = null,
    string? Value = null)
{
    /// <summary>
    /// XML-aligned alias for <see cref="Description"/>: maps to
    /// <c>&lt;summary&gt;</c> on emit. Prefer this name in new code.
    /// </summary>
    public string Summary => Description;

    /// <summary>
    /// XML-aligned alias for <see cref="Throws"/>: maps to
    /// <c>&lt;exception&gt;</c> on emit. Prefer this name in new code.
    /// </summary>
    public IReadOnlyList<string>? Exceptions => Throws;

    /// <summary>
    /// Parses a sequence of doc-comment token texts into a structured <see cref="DocComment"/>.
    /// </summary>
    public static DocComment? Parse(IReadOnlyList<SyntaxToken> tokens)
    {
        if (tokens.Count == 0) return null;

        var descriptionLines = new List<string>();
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? typeParameters = null;
        string? returns = null;
        var examples = new List<string>();
        string? deprecated = null;
        var isDeprecated = false;
        List<string>? seeAlso = null;
        string? since = null;
        List<string>? throws = null;
        var remarksLines = new List<string>();
        string? value = null;
        bool hasSeenBlockTag = false;

        // Lightweight accumulator state for tags whose body can span
        // multiple physical doc-comment lines (continuation indent or
        // explicit line continuation). Mirrors the @param continuation
        // shape that already existed.
        string? lastParamName = null;
        string? lastTypeParamName = null;
        // What does an indented continuation line append to?
        //   "param" → parameters[lastParamName]
        //   "typeparam" → typeParameters[lastTypeParamName]
        //   "remarks" → remarksLines
        //   "returns" → returns
        //   "value" → value
        //   "description" / null → descriptionLines
        //   "seealso" → last entry of seeAlso
        //   "exception" → last entry of throws
        //   "deprecated" → deprecated
        //   "since" → since
        string? continuationTarget = null;

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

            // @para — explicit paragraph break inside the current
            // continuation accumulator (description / remarks). Maps
            // to a `<para/>` boundary on XML emit.
            if (string.Equals(line, "@para", StringComparison.Ordinal))
            {
                if (continuationTarget == "remarks")
                {
                    remarksLines.Add(string.Empty);
                }
                else
                {
                    descriptionLines.Add(string.Empty);
                }
                continue;
            }

            // Both spellings are accepted. The specification teaches `@param=<name>` in its
            // doc-comment section and `@param <name>` in its comments section, and only the first
            // was understood — so the second was swallowed into the description verbatim, leaving
            // "Adds two numbers. @param a first value" as the summary and no parameter
            // documentation at all. Silent, and taught by the specification itself.
            if (line.StartsWith("@param=", StringComparison.Ordinal) ||
                line.StartsWith("@param ", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
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
                lastTypeParamName = null;
                continuationTarget = "param";
            }
            else if (line.StartsWith("@typeparam=", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                typeParameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var rest = line.AsSpan(11);
                var spaceIndex = rest.IndexOf(' ');
                if (spaceIndex >= 0)
                {
                    var name = rest[..spaceIndex].ToString();
                    var desc = rest[(spaceIndex + 1)..].ToString().Trim();
                    typeParameters[name] = desc;
                    lastTypeParamName = name;
                }
                else
                {
                    var name = rest.ToString();
                    typeParameters[name] = string.Empty;
                    lastTypeParamName = name;
                }
                lastParamName = null;
                continuationTarget = "typeparam";
            }
            else if (line.StartsWith("@summary ", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                descriptionLines.Add(line[9..].Trim());
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = "description";
            }
            else if (string.Equals(line, "@summary", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = "description";
            }
            else if (line.StartsWith("@remarks ", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                remarksLines.Add(line[9..].Trim());
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = "remarks";
            }
            else if (string.Equals(line, "@remarks", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = "remarks";
            }
            else if (line.StartsWith("@returns ", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                returns = line[9..].Trim();
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = "returns";
            }
            else if (string.Equals(line, "@returns", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                returns = string.Empty;
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = "returns";
            }
            else if (line.StartsWith("@value ", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                value = line[7..].Trim();
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = "value";
            }
            else if (string.Equals(line, "@value", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                value = string.Empty;
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = "value";
            }
            else if (line.StartsWith("@example ", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                // Single-line example
                examples.Add(line[9..].Trim());
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = null;
            }
            else if (string.Equals(line, "@example", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                // Start multiline example block
                inExampleBlock = true;
                currentExampleLines = new List<string>();
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = null;
            }
            else if (line.StartsWith("@deprecated ", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                deprecated = line[12..].Trim();
                isDeprecated = true;
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = "deprecated";
            }
            else if (string.Equals(line, "@deprecated", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                isDeprecated = true;
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = "deprecated";
            }
            else if (line.StartsWith("@seealso ", StringComparison.Ordinal)
                || line.StartsWith("@see ", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                // `@see` is preserved as a back-compat alias for
                // `@seealso` so existing tosh sources keep parsing.
                var prefix = line.StartsWith("@seealso ", StringComparison.Ordinal) ? 9 : 5;
                seeAlso ??= new List<string>();
                seeAlso.Add(line[prefix..].Trim());
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = "seealso";
            }
            else if (line.StartsWith("@since ", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                since = line[7..].Trim();
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = "since";
            }
            else if (line.StartsWith("@exception ", StringComparison.Ordinal)
                || line.StartsWith("@throws ", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                // `@throws` is preserved as a back-compat alias for
                // `@exception` so existing tosh sources keep parsing.
                var prefix = line.StartsWith("@exception ", StringComparison.Ordinal) ? 11 : 8;
                throws ??= new List<string>();
                throws.Add(line[prefix..].Trim());
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = "exception";
            }
            else if (string.Equals(line, "@exception", StringComparison.Ordinal)
                || string.Equals(line, "@throws", StringComparison.Ordinal))
            {
                hasSeenBlockTag = true;
                throws ??= new List<string>();
                throws.Add(string.Empty);
                lastParamName = null;
                lastTypeParamName = null;
                continuationTarget = "exception";
            }
            else if (lastParamName is not null
                && continuationTarget == "param"
                && line.StartsWith("  ", StringComparison.Ordinal))
            {
                parameters[lastParamName] = parameters[lastParamName] + " " + line.Trim();
            }
            else if (lastTypeParamName is not null
                && continuationTarget == "typeparam"
                && line.StartsWith("  ", StringComparison.Ordinal))
            {
                typeParameters![lastTypeParamName] =
                    typeParameters[lastTypeParamName] + " " + line.Trim();
            }
            else if (continuationTarget == "remarks"
                && line.StartsWith("  ", StringComparison.Ordinal))
            {
                remarksLines.Add(line.Trim());
            }
            else if (continuationTarget == "returns"
                && line.StartsWith("  ", StringComparison.Ordinal))
            {
                returns = (returns ?? string.Empty) + " " + line.Trim();
            }
            else if (continuationTarget == "value"
                && line.StartsWith("  ", StringComparison.Ordinal))
            {
                value = (value ?? string.Empty) + " " + line.Trim();
            }
            else if (continuationTarget == "seealso"
                && seeAlso is { Count: > 0 }
                && line.StartsWith("  ", StringComparison.Ordinal))
            {
                seeAlso[^1] = seeAlso[^1] + " " + line.Trim();
            }
            else if (continuationTarget == "exception"
                && throws is { Count: > 0 }
                && line.StartsWith("  ", StringComparison.Ordinal))
            {
                throws[^1] = throws[^1].Length == 0
                    ? line.Trim()
                    : throws[^1] + " " + line.Trim();
            }
            else if (continuationTarget == "deprecated"
                && line.StartsWith("  ", StringComparison.Ordinal))
            {
                deprecated = (deprecated ?? string.Empty).Length == 0
                    ? line.Trim()
                    : deprecated + " " + line.Trim();
            }
            else if (continuationTarget == "since"
                && line.StartsWith("  ", StringComparison.Ordinal))
            {
                since = (since ?? string.Empty).Length == 0
                    ? line.Trim()
                    : since + " " + line.Trim();
            }
            else if (continuationTarget == "description"
                && line.StartsWith("  ", StringComparison.Ordinal))
            {
                descriptionLines.Add(line.Trim());
            }
            else
            {
                // Only treat untagged lines as summary if no block tag has been seen yet
                if (!hasSeenBlockTag)
                {
                    descriptionLines.Add(line);
                    lastParamName = null;
                    lastTypeParamName = null;
                    continuationTarget = "description";
                }
                // Otherwise, ignore untagged lines after a block tag
            }
        }

        // A trailing example block is flushed here, once the tokens are exhausted.
        //
        // This stood *inside* the loop, where it could never fire: the branch that accumulates
        // example lines ends in `continue`, and every branch that does reach the bottom has
        // already closed the block and cleared the flag. So `@example` as the last tag in a
        // doc-comment lost its body outright, while the same block followed by any other tag was
        // kept — saved by the code that ends a block rather than by this.
        if (inExampleBlock && currentExampleLines is not null)
        {
            examples.Add(string.Join("\n", currentExampleLines));
        }

        var description = string.Join(" ", descriptionLines).Trim();
        // Re-trim continuation accumulators that may have collected a
        // leading space from the join above.
        if (returns is not null) returns = returns.Trim();
        if (value is not null) value = value.Trim();
        var remarks = remarksLines.Count == 0 ? null : string.Join(" ", remarksLines).Trim();

        if (description.Length == 0
            && parameters.Count == 0
            && returns is null
            && examples.Count == 0
            && !isDeprecated
            && seeAlso is null
            && since is null
            && throws is null
            && remarks is null
            && typeParameters is null
            && value is null)
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
            (IReadOnlyList<string>?)throws,
            remarks,
            typeParameters,
            value);
    }
}
