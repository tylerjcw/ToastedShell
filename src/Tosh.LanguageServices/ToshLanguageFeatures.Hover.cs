using System.Reflection;
using Tosh.Runtime;
using Tosh.Runtime.Units;
using Tosh.Language.Parsing;

namespace Tosh.LanguageServices;

public sealed partial class ToshLanguageFeatures
{
    public LspHover? GetHover(string text, string sourceName, LspPosition position)
    {
        var map = new TextCoordinateMap(text);
        var offset = map.ToOffset(position);

        // 1. Check for Pipeline Operator '|' or Redirect Operator (o>, out>, err>, >>)
        var pipeOffset = FindPipelineOrRedirectOffset(text, offset);
        if (pipeOffset >= 0)
        {
            var upstream = text.Substring(0, pipeOffset).TrimEnd();
            var lineStart = upstream.LastIndexOf('\n');
            var lineText = lineStart >= 0 ? upstream.Substring(lineStart + 1) : upstream;

            string schemaMarkdown;
            if (lineText.Contains("ls"))
            {
                schemaMarkdown = """
                    ### 🔀 Pipeline Data Stream: `FileInfo`
                    
                    | Column Name | Type | Description |
                    |:---|:---|:---|
                    | `Name` | `string` | File or directory name |
                    | `Type` | `FileType` | `file` \| `dir` \| `symlink` |
                    | `Size` | `long` | File size in bytes |
                    | `Modified` | `DateTime` | Last write timestamp |
                    | `Mode` | `string` | File permission attributes |
                    
                    *Pipeline operations: `get Name Size`, `where _.Type == file`, `sort-by Size`, `row 0`.*
                    """;
            }
            else if (lineText.Contains("ps"))
            {
                schemaMarkdown = """
                    ### 🔀 Pipeline Data Stream: `ProcessInfo`
                    
                    | Column Name | Type | Description |
                    |:---|:---|:---|
                    | `PID` | `int` | Process identifier |
                    | `Name` | `string` | Process executable name |
                    | `CPU` | `double` | CPU usage percentage |
                    | `Memory` | `long` | Working set memory (bytes) |
                    
                    *Pipeline operations: `where CPU > 50`, `sort-by Memory`, `get PID Name`.*
                    """;
            }
            else if (lineText.Contains("split") || lineText.Contains("PATH") || lineText.Contains("cat") || lineText.Contains("read"))
            {
                schemaMarkdown = """
                    ### 🔀 Pipeline Data Stream: `string` (Lines / Path Segments)
                    
                    *Elements*: Text string sequence (`string`)
                    
                    *Pipeline operations: `where _ is not in $to_add`, `chain $other`, `join ':'`, `grep "pattern"`.*
                    """;
            }
            else
            {
                schemaMarkdown = """
                    ### 🔀 Pipeline Data Stream
                    
                    *Piped Stream*: Dynamic sequence of objects or values.
                    
                    *Operations: `get <columns>`, `where <cond>`, `sort-by <key>`, `row <index>`.*
                    """;
            }

            return new LspHover(
                new LspMarkupContent("markdown", schemaMarkdown),
                map.ToRange(pipeOffset, pipeOffset + 1));
        }

        // 2. Check for Path Validation on String Literals
        if (offset >= 0 && offset < text.Length && (text[offset] == '"' || text[offset] == '\'' || (offset > 0 && (text[offset - 1] == '"' || text[offset - 1] == '\''))))
        {
            var stringLit = ExtractStringLiteralAt(text, offset);
            if (!string.IsNullOrEmpty(stringLit.PathText))
            {
                var fullPath = Path.IsPathRooted(stringLit.PathText)
                    ? stringLit.PathText
                    : Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), stringLit.PathText.TrimStart('~', '/')));

                string validationMarkdown;
                if (Directory.Exists(fullPath))
                {
                    validationMarkdown = $"### 📁 Path Validation\n`{stringLit.PathText}`\n\n✅ **Status**: Valid Directory (`Directory exists on disk`)\n\n*Absolute*: `{fullPath}`";
                }
                else if (File.Exists(fullPath))
                {
                    var fileInfo = new FileInfo(fullPath);
                    validationMarkdown = $"### 📄 Path Validation\n`{stringLit.PathText}`\n\n✅ **Status**: Valid File (`{fileInfo.Length:N0} bytes`)\n\n*Absolute*: `{fullPath}`";
                }
                else
                {
                    validationMarkdown = $"### ⚠️ Path Validation\n`{stringLit.PathText}`\n\n⚠️ **Status**: Target path does not exist on disk.\n\n*Target*: `{fullPath}`";
                }

                return new LspHover(
                    new LspMarkupContent("markdown", validationMarkdown),
                    map.ToRange(stringLit.Start, stringLit.End));
            }
        }

        var index = DeclarationIndex.Create(sourceName, text);
        var token = FindWordAt(text, offset);

        if (string.IsNullOrWhiteSpace(token.Word))
        {
            return null;
        }

        string? description = null;
        var normalizedWord = token.Word;
        var semantics = DocumentSemanticModel.Create(sourceName, text);

        if (SpecialVariables.TryGetValue(normalizedWord, out var special))
        {
            description = special;
        }
        else if (Keywords.TryGetValue(normalizedWord, out var keyword))
        {
            description = keyword;
        }
        else if (GetDeclaredTypeMemberHoverDescription(index, text, token.Start, normalizedWord) is { } memberDescription)
        {
            description = memberDescription;
        }
        else if (GetTypedLiteralMemberHoverDescription(index, text, token.Start, normalizedWord) is { } literalDescription)
        {
            description = literalDescription;
        }
        else if (GetTypeLikeDeclarationHoverDescription(index, offset, normalizedWord) is { } typeDescription)
        {
            description = typeDescription;
        }
        else if (GetShellHoverDescription(semantics, offset, normalizedWord) is { } shellDescription)
        {
            description = shellDescription;
        }
        else if (GetTopLevelFunctionHoverDescription(index, offset, normalizedWord) is { } functionDescription)
        {
            description = functionDescription;
        }
        else if (HelpCatalog.ResolveTopic(_runtime, normalizedWord) is { } topic)
        {
            if (topic.Kind == HelpSubjectKind.BuiltIn && GetMetadataLookup().TryGetValue(normalizedWord, out var metadataEntry))
            {
                description = FormatCommandHoverMarkdown(metadataEntry);
            }
            else
            {
                description = topic.Description;
            }
        }

        if (description is null)
        {
            description = GetClrHoverDescription(semantics, offset, normalizedWord);
        }

        if (description is null)
        {
            return null;
        }

        return new LspHover(
            new LspMarkupContent("markdown", $"**{normalizedWord}**\n\n{description}"),
            map.ToRange(token.Start, token.End));
    }

    private static string FormatCommandHoverMarkdown(CommandMetadata entry)
    {
        var sb = new System.Text.StringBuilder();

        // Badges: experimental, deprecated, version
        var badges = new List<string>();
        if (entry.IsExperimental) badges.Add("⚗️ Experimental");
        if (entry.DeprecatedVersion is not null) badges.Add($"⚠️ Deprecated since {entry.DeprecatedVersion}");
        if (entry.RemovedVersion is not null) badges.Add($"❌ Removed in {entry.RemovedVersion}");
        if (badges.Count > 0)
        {
            sb.AppendLine(string.Join(" · ", badges));
            sb.AppendLine();
        }

        sb.AppendLine(entry.Description);
        sb.AppendLine();

        if (entry.LongDescription is not null)
        {
            sb.AppendLine(entry.LongDescription);
            sb.AppendLine();
        }

        if (entry.Aliases.Count > 0)
        {
            sb.AppendLine($"*Aliases:* {string.Join(", ", entry.Aliases.Select(a => $"`{a}`"))}");
            sb.AppendLine();
        }

        // Category and version info
        var infoLine = new List<string>();
        infoLine.Add($"Category: {entry.Category}");
        if (entry.SinceVersion is not null) infoLine.Add($"Since: {entry.SinceVersion}");
        if (entry.Streaming is not null) infoLine.Add($"Streaming: {entry.Streaming}");
        sb.AppendLine($"*{string.Join(" · ", infoLine)}*");
        sb.AppendLine();

        sb.AppendLine("```tosh");
        sb.AppendLine(entry.Usage);
        sb.AppendLine("```");

        if (entry.Arguments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Arguments**");
            foreach (var arg in entry.Arguments)
            {
                var req = arg.Required ? "" : " *(optional)*";
                var typePart = arg.TypeName is not null ? $" `{arg.TypeName}`" : "";
                sb.AppendLine($"- `{arg.Name}`{typePart} — {arg.Description}{req}");
            }
        }

        if (entry.Options.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Options**");
            foreach (var opt in entry.Options)
            {
                sb.AppendLine($"- `{opt.Syntax}` — {opt.Description}");
            }
        }

        if (entry.PipelineInput is { } pi)
        {
            var accepts = new List<string>();
            if (pi.AcceptsScalar) accepts.Add("scalar");
            if (pi.AcceptsRecord) accepts.Add("record");
            if (pi.AcceptsList) accepts.Add("list");
            if (pi.AcceptsTable) accepts.Add("table");
            if (accepts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"**Pipeline input:** {string.Join(", ", accepts)}");
                if (pi.Description is not null)
                    sb.AppendLine($"  {pi.Description}");
            }
        }

        if (entry.Output is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"**Output:** {entry.Output}");
        }

        if (entry.Examples.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Examples**");
            sb.AppendLine("```tosh");
            foreach (var ex in entry.Examples)
            {
                var comment = ex.Title is not null ? $"  # {ex.Title}" : "";
                sb.AppendLine($"{ex.Code}{comment}");
            }
            sb.AppendLine("```");
        }

        if (entry.CanonicalExamples.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Canonical Examples**");
            foreach (var ce in entry.CanonicalExamples)
            {
                if (ce.Description is not null)
                    sb.AppendLine($"*{ce.Description}*");
                sb.AppendLine("```tosh");
                sb.AppendLine($"> {ce.Input}");
                sb.AppendLine(ce.Output);
                sb.AppendLine("```");
            }
        }

        if (entry.Notes.Count > 0)
        {
            sb.AppendLine();
            foreach (var note in entry.Notes)
            {
                sb.AppendLine($"> {note}");
            }
        }

        if (entry.ErrorConditions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Error Conditions**");
            foreach (var err in entry.ErrorConditions)
            {
                sb.AppendLine($"- {err}");
            }
        }

        if (entry.Permissions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Permissions:** {string.Join(", ", entry.Permissions)}");
        }

        if (entry.Tags.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"*Tags:* {string.Join(", ", entry.Tags.Select(t => $"`{t}`"))}");
        }

        if (entry.SeeAlso.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"*See also:* {string.Join(", ", entry.SeeAlso.Select(s => $"`{s}`"))}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string? GetShellHoverDescription(DocumentSemanticModel semantics, int offset, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (token.StartsWith("$", StringComparison.Ordinal))
        {
            var trimmed = token[1..];

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            if (!trimmed.Contains('.', StringComparison.Ordinal))
            {
                return semantics.ResolveVisibleVariableShellClass(offset, trimmed) is { } shellClass
                    ? $"Variable\n\n```tosh\n{shellClass.Name} ${trimmed}\n```"
                    : null;
            }

            return DescribeShellReference(semantics.ResolveShellReference(offset, token));
        }

        return DescribeShellReference(semantics.ResolveShellReference(offset, token));
    }

    private static string? GetTopLevelFunctionHoverDescription(DeclarationIndex index, int offset, string token)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            token.StartsWith("$", StringComparison.Ordinal) ||
            token.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        var overloads = index.GetVisibleFunctionOverloads(offset, token);
        if (overloads.Count == 0)
        {
            return null;
        }

        var label = overloads.Count == 1 ? "Function" : "Functions";
        var signatures = string.Join(
            "\n",
            overloads.Take(6).Select(FormatTopLevelFunctionSignature));
        var overflow = overloads.Count > 6 ? $"\n... {overloads.Count - 6} more overload(s)" : string.Empty;

        var doc = overloads.Select(o => o.DocComment).FirstOrDefault(d => d is not null);
        var parts = new List<string> { label };

        AppendDeprecatedBanner(parts, doc);
        AppendSummary(parts, doc);

        parts.Add($"```tosh\n{signatures}{overflow}\n```");

        if (doc is not null)
        {
            // Use the first overload as the canonical source of parameter
            // names and types when rendering @param/@returns docs.
            var canonical = overloads[0];
            AppendDocCommentSections(parts, doc, canonical.Parameters, canonical.ReturnTypeName);
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// The right-hand side of a path — <c>Level::Novice</c>, <c>Option::None</c>; <c>TOAST-0090</c>.
    /// </summary>
    /// <remarks>
    /// Hover answered for the type and returned nothing for the member, because a member's scope
    /// is its declaring type's span and the cursor is somewhere else entirely. The qualifier
    /// standing to the left of the cursor is what says which type to ask, so it is read from the
    /// source rather than looked up by the member's bare name.
    /// </remarks>
    private static string? GetDeclaredTypeMemberHoverDescription(
        DeclarationIndex index,
        string text,
        int wordStart,
        string word)
    {
        if (string.IsNullOrWhiteSpace(word) || word.StartsWith("$", StringComparison.Ordinal))
        {
            return null;
        }

        var cursor = wordStart;
        int qualifierEnd;

        if (cursor >= 2 && text[cursor - 1] == ':' && text[cursor - 2] == ':')
        {
            qualifierEnd = cursor - 2;
        }
        else if (cursor >= 1 && text[cursor - 1] == '.')
        {
            qualifierEnd = cursor - 1;
        }
        else
        {
            return null;
        }

        var qualifierStart = qualifierEnd;

        while (qualifierStart > 0 && (char.IsLetterOrDigit(text[qualifierStart - 1]) || text[qualifierStart - 1] is '_'))
        {
            qualifierStart--;
        }

        if (qualifierStart == qualifierEnd)
        {
            return null;
        }

        var qualifier = text[qualifierStart..qualifierEnd];
        var member = index.GetTypeMembers(qualifierStart, qualifier)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, word, StringComparison.Ordinal));

        if (member is null)
        {
            return null;
        }

        var parts = new List<string> { $"{member.KindLabel} of `{member.DeclaringType}`" };
        AppendDeprecatedBanner(parts, member.DocComment);
        AppendSummary(parts, member.DocComment);

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// A field name inside <c>new T {| … |}</c> — <c>TOAST-0091</c>.
    /// </summary>
    /// <remarks>
    /// The same shape as the path-member case: a member's scope is its declaring type's span, so
    /// looking the bare name up from the cursor finds nothing. The enclosing literal is what says
    /// which type to ask.
    /// </remarks>
    private static string? GetTypedLiteralMemberHoverDescription(
        DeclarationIndex index,
        string text,
        int wordStart,
        string word)
    {
        if (string.IsNullOrWhiteSpace(word) || word.StartsWith("$", StringComparison.Ordinal))
        {
            return null;
        }

        var open = FindEnclosingRecordLiteral(text, wordStart);
        if (open < 0 || !TryReadTypedLiteralHead(text, open, out var typeName))
        {
            return null;
        }

        var member = index.GetInitializableMembers(wordStart, typeName)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, word, StringComparison.Ordinal));

        if (member is null)
        {
            return null;
        }

        var parts = new List<string> { $"{member.KindLabel} of `{member.DeclaringType}`" };
        AppendDeprecatedBanner(parts, member.DocComment);
        AppendSummary(parts, member.DocComment);

        return string.Join("\n\n", parts);
    }

    private static string? GetTypeLikeDeclarationHoverDescription(DeclarationIndex index, int offset, string token)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            token.StartsWith("$", StringComparison.Ordinal) ||
            token.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        var kindLabel = index.GetDeclarationKindLabel(offset, token);
        if (kindLabel is null)
        {
            return null;
        }

        var parts = new List<string> { kindLabel };

        var doc = index.GetDeclarationDocComment(offset, token);
        AppendDeprecatedBanner(parts, doc);
        AppendSummary(parts, doc);

        if (doc is not null)
        {
            AppendDocCommentSections(parts, doc, parameters: null, returnTypeName: null);
        }

        return string.Join("\n\n", parts);
    }

    private static void AppendDeprecatedBanner(List<string> parts, DocComment? doc)
    {
        if (doc?.IsDeprecated != true)
        {
            return;
        }

        parts.Add(doc.Deprecated is { Length: > 0 } depMsg
            ? $"⚠️ **Deprecated** — {depMsg}"
            : "⚠️ **Deprecated**");
    }

    private static void AppendSummary(List<string> parts, DocComment? doc)
    {
        if (doc?.Description is { Length: > 0 } desc)
        {
            parts.Add(desc);
        }
    }

    /// <summary>
    /// Appends the non-summary doc-comment sections (remarks, type
    /// parameters, parameters, returns, value, exceptions, examples,
    /// see-also, since) to <paramref name="parts"/> in a consistent
    /// Markdown layout. Each entry in <paramref name="parts"/> ends up
    /// being joined with a blank line, so every section is its own
    /// paragraph in the rendered hover.
    /// </summary>
    private static void AppendDocCommentSections(
        List<string> parts,
        DocComment doc,
        IReadOnlyList<FunctionParameterSyntax>? parameters,
        string? returnTypeName)
    {
        if (doc.Remarks is { Length: > 0 } remarks)
        {
            parts.Add($"**Remarks**\n\n{remarks}");
        }

        if (doc.TypeParameters is { Count: > 0 } typeParams)
        {
            var lines = new List<string> { "**Type parameters**" };
            foreach (var (name, description) in typeParams)
            {
                lines.Add(description.Length > 0
                    ? $"- `{name}` — {description}"
                    : $"- `{name}`");
            }
            parts.Add(string.Join("\n", lines));
        }

        if (doc.Parameters is { Count: > 0 } && parameters is not null)
        {
            var paramLines = new List<string>();
            foreach (var param in parameters)
            {
                if (!doc.Parameters.TryGetValue(param.Name, out var paramDesc) || paramDesc.Length == 0)
                {
                    continue;
                }

                var typeAnnotation = param.TypeName is not null ? $" `{param.TypeName}`" : string.Empty;
                paramLines.Add($"- `{param.Name}`{typeAnnotation} — {paramDesc}");
            }

            if (paramLines.Count > 0)
            {
                parts.Add("**Parameters**\n" + string.Join("\n", paramLines));
            }
        }
        else if (doc.Parameters is { Count: > 0 } looseParams && parameters is null)
        {
            // No syntactic parameter list available (e.g. record/class
            // hover); fall back to the names captured in the doc-comment.
            var paramLines = new List<string> { "**Parameters**" };
            foreach (var (name, description) in looseParams)
            {
                paramLines.Add(description.Length > 0
                    ? $"- `{name}` — {description}"
                    : $"- `{name}`");
            }
            parts.Add(string.Join("\n", paramLines));
        }

        if (doc.Returns is { Length: > 0 } ret)
        {
            var returnType = returnTypeName is { Length: > 0 } rt ? $" `{rt}`" : string.Empty;
            parts.Add($"**Returns**{returnType} — {ret}");
        }

        if (doc.Value is { Length: > 0 } val)
        {
            parts.Add($"**Value** — {val}");
        }

        if (doc.Throws is { Count: > 0 } throws)
        {
            var throwLines = new List<string> { "**Throws**" };
            foreach (var t in throws)
            {
                throwLines.Add(t.Length > 0 ? $"- {t}" : "- _(unspecified)_");
            }
            parts.Add(string.Join("\n", throwLines));
        }

        if (doc.Examples is { Count: > 0 } examples)
        {
            var heading = examples.Count == 1 ? "**Example**" : "**Examples**";
            var blocks = new List<string> { heading };
            foreach (var example in examples)
            {
                blocks.Add($"```tosh\n{example}\n```");
            }
            parts.Add(string.Join("\n\n", blocks));
        }

        if (doc.SeeAlso is { Count: > 0 } seeAlso)
        {
            parts.Add($"**See also:** {string.Join(", ", seeAlso.Select(s => $"`{s}`"))}");
        }

        if (doc.Since is { Length: > 0 } since)
        {
            parts.Add($"_Since {since}_");
        }
    }

    private static string? GetClrHoverDescription(DocumentSemanticModel semantics, int offset, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (token.StartsWith("$", StringComparison.Ordinal))
        {
            return DescribeVariableOrInstancePath(semantics, offset, token);
        }

        if (string.Equals(token, "_", StringComparison.Ordinal) ||
            token.StartsWith("_.", StringComparison.Ordinal))
        {
            return null;
        }

        return DescribeTypeOrStaticPath(semantics, offset, token);
    }

    private static string? DescribeVariableOrInstancePath(DocumentSemanticModel semantics, int offset, string token)
    {
        var trimmed = token[1..];

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return null;
        }

        var rootReference = "$" + segments[0];
        var currentType = semantics.ResolveReferenceType(offset, rootReference);

        if (currentType is null)
        {
            return null;
        }

        if (segments.Length == 1)
        {
            return $"Variable\n\n```tosh\n{ClrMetadataFormatting.FormatTypeDisplayName(currentType)} ${segments[0]}\n```";
        }

        for (var index = 1; index < segments.Length; index++)
        {
            var isLast = index == segments.Length - 1;
            var description = DescribeMember(currentType, segments[index], staticOnly: false, out var nextType);

            if (description is null)
            {
                return null;
            }

            if (isLast)
            {
                return description;
            }

            if (nextType is null)
            {
                return null;
            }

            currentType = nextType;
        }

        return null;
    }

    private static string? DescribeTypeOrStaticPath(DocumentSemanticModel semantics, int offset, string token)
    {
        var resolver = semantics.CreateTypeResolver(offset);
        var directType = resolver.Resolve(token);

        if (directType is not null)
        {
            return FormatTypeDescription(directType);
        }

        var segments = token.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var type = resolver.Resolve(string.Join('.', segments.Take(prefixLength)));

            if (type is null)
            {
                continue;
            }

            var currentType = type;

            for (var index = prefixLength; index < segments.Length; index++)
            {
                var isLast = index == segments.Length - 1;
                var description = DescribeMember(currentType, segments[index], staticOnly: index == prefixLength, out var nextType);

                if (description is null)
                {
                    return null;
                }

                if (isLast)
                {
                    return description;
                }

                if (nextType is null)
                {
                    return null;
                }

                currentType = nextType;
            }
        }

        return null;
    }

    private static string? DescribeMember(Type declaringType, string memberName, bool staticOnly, out Type? nextType)
    {
        nextType = null;

        if (staticOnly)
        {
            var nestedType = declaringType.GetNestedType(memberName, BindingFlags.Public);

            if (nestedType is not null)
            {
                nextType = nestedType;
                return FormatTypeDescription(nestedType);
            }
        }

        var bindingFlags = BindingFlags.Public | (staticOnly ? BindingFlags.Static : BindingFlags.Instance);
        var property = declaringType.GetProperty(memberName, bindingFlags);

        if (property is not null && property.GetIndexParameters().Length == 0)
        {
            nextType = property.PropertyType;
            return $"Property\n\n```tosh\n{ClrMetadataFormatting.FormatTypeDisplayName(property.PropertyType)} {property.Name}\n```";
        }

        var field = declaringType.GetField(memberName, bindingFlags);

        if (field is not null && !field.IsSpecialName)
        {
            nextType = field.FieldType;
            return $"Field\n\n```tosh\n{ClrMetadataFormatting.FormatTypeDisplayName(field.FieldType)} {field.Name}\n```";
        }

        var methods = declaringType.GetMethods(bindingFlags)
            .Where(method => !method.IsSpecialName && string.Equals(method.Name, memberName, StringComparison.Ordinal))
            .OrderBy(method => method.GetParameters().Length)
            .ToArray();

        if (methods.Length == 0)
        {
            return null;
        }

        nextType = methods[0].ReturnType == typeof(void) ? declaringType : methods[0].ReturnType;
        var visibleMethods = methods.Take(6).Select(ClrMetadataFormatting.FormatMethodSignature).ToArray();
        var overflowSuffix = methods.Length > visibleMethods.Length
            ? $"\n... {methods.Length - visibleMethods.Length} more overload(s)"
            : string.Empty;

        return $"Method{(methods.Length > 1 ? "s" : string.Empty)}\n\n```tosh\n{string.Join("\n", visibleMethods)}{overflowSuffix}\n```";
    }

    private static string FormatTypeDescription(Type type)
    {
        var kind = type.IsEnum
            ? "Enum"
            : type.IsInterface
                ? "Interface"
                : type.IsValueType
                    ? "Struct"
                    : type.IsClass
                        ? "Class"
                        : "Type";

        return $"{kind}\n\n```tosh\n{ClrMetadataFormatting.FormatTypeDisplayName(type)}\n```";
    }

    private static string? DescribeShellReference(DocumentSemanticModel.ShellReferenceSymbol? reference)
    {
        return reference switch
        {
            DocumentSemanticModel.ShellReferenceSymbol.Class shellClass => $"Class\n\n```tosh\n{shellClass.Symbol.Name}\n```",
            DocumentSemanticModel.ShellReferenceSymbol.Property property =>
                FormatShellPropertyReferenceHover(property),
            DocumentSemanticModel.ShellReferenceSymbol.Method method =>
                FormatShellMethodReferenceHover(method),
            _ => null,
        };
    }

    private static string FormatShellPropertyReferenceHover(DocumentSemanticModel.ShellReferenceSymbol.Property property)
    {
        var parts = new List<string> { "Property" };
        var doc = property.Symbol.Doc;

        AppendDeprecatedBanner(parts, doc);
        AppendSummary(parts, doc);

        parts.Add($"```tosh\n{FormatShellPropertySignature(property.Symbol)}\n```");

        if (doc is not null)
        {
            AppendDocCommentSections(parts, doc, parameters: null, returnTypeName: property.Symbol.TypeName);
        }

        return string.Join("\n\n", parts);
    }

    private static string FormatShellMethodReferenceHover(DocumentSemanticModel.ShellReferenceSymbol.Method method)
    {
        var label = method.Overloads.Count > 1 ? "Methods" : "Method";
        var signatures = string.Join("\n", method.Overloads.Take(6).Select(FormatShellMethodSignature));
        var overflow = method.Overloads.Count > 6
            ? $"\n... {method.Overloads.Count - 6} more overload(s)"
            : string.Empty;
        var doc = method.Overloads.Select(o => o.Doc).FirstOrDefault(d => d is not null);

        var parts = new List<string> { label };

        AppendDeprecatedBanner(parts, doc);
        AppendSummary(parts, doc);

        parts.Add($"```tosh\n{signatures}{overflow}\n```");

        if (doc is not null)
        {
            var canonical = method.Overloads[0];
            AppendDocCommentSections(parts, doc, canonical.Parameters, canonical.ReturnTypeName);
        }

        return string.Join("\n\n", parts);
    }

    private static string FormatShellPropertySignature(DocumentSemanticModel.ShellClassPropertySymbol property)
    {
        var typeName = NormalizeShellTypeName(property.TypeName);
        return $"{typeName} {property.Name}";
    }

    private static string FormatShellMethodSignature(DocumentSemanticModel.ShellClassMethodSymbol method)
    {
        var modifier = method.IsStatic ? "static " : string.Empty;
        return $"{modifier}{NormalizeShellTypeName(method.ReturnTypeName)} {method.Name}({FormatShellParameters(method.Parameters)})";
    }

    private static string FormatTopLevelFunctionSignature(DeclarationIndex.IndexedFunctionDeclaration function)
    {
        var returnType = string.IsNullOrWhiteSpace(function.ReturnTypeName)
            ? string.Empty
            : $" -> {function.ReturnTypeName}";
        return $"func {function.Name}({FormatShellParameters(function.Parameters)}){returnType}";
    }

    private static string FormatShellConstructorSignature(string className, DocumentSemanticModel.ShellClassConstructorSymbol constructor)
    {
        return $"{className}({FormatShellParameters(constructor.Parameters)})";
    }

    private static string FormatShellParameters(IReadOnlyList<FunctionParameterSyntax> parameters)
    {
        return string.Join(
            ", ",
            parameters.Select(parameter =>
            {
                var suffix = parameter.IsOptional ? "?" : string.Empty;
                var rest = parameter.IsRest ? "..." : string.Empty;
                return string.IsNullOrWhiteSpace(parameter.TypeName)
                    ? $"{parameter.Name}{suffix}{rest}"
                    : $"{parameter.Name}{suffix}{rest}: {parameter.TypeName}";
            }));
    }

    private static string NormalizeShellTypeName(string? typeName)
    {
        return string.IsNullOrWhiteSpace(typeName) ? "object" : typeName;
    }


}
