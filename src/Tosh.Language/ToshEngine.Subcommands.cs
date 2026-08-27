using System.Runtime.CompilerServices;
using System.Text;
using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed partial class ToshEngine
{
    private sealed class SubcommandNode
    {
        public string? Name;
        public string? Description;
        public IReadOnlyList<string>? Examples;
        public SubcommandModifier Modifiers;
        public TextSpan Span;
        public IReadOnlyList<FunctionParameterSyntax> Flags = Array.Empty<FunctionParameterSyntax>();
        public IReadOnlyList<FunctionParameterSyntax> Arguments = Array.Empty<FunctionParameterSyntax>();
        public IReadOnlyList<StatementSyntax> BodyStatements = Array.Empty<StatementSyntax>();
        public Dictionary<string, SubcommandNode> Children = new(StringComparer.Ordinal);
        public bool UserDeclaredHelpFlag;
    }

    private sealed class DispatchFrame
    {
        public required SubcommandNode Node;
        public readonly Dictionary<string, ScriptArgumentValue> FlagValues = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<ScriptArgumentValue> PositionalValues = new();
    }

    private SubcommandNode BuildSubcommandNode(
        string sourceName,
        string sourceText,
        string? name,
        SubcommandModifier modifiers,
        TextSpan span,
        IReadOnlyList<StatementSyntax> statements,
        DocComment? docComment = null)
    {
        var node = new SubcommandNode
        {
            Name = name,
            Description = docComment?.Description?.Trim() is { Length: > 0 } desc ? desc : null,
            Examples = docComment?.Examples is { Count: > 0 } ex ? ex : null,
            Modifiers = modifiers,
            Span = span,
        };

        var flags = new List<FunctionParameterSyntax>();
        var args = new List<FunctionParameterSyntax>();
        var body = new List<StatementSyntax>();

        foreach (var statement in statements)
        {
            if (statement is ScriptInputStatementSyntax input)
            {
                foreach (var parameter in input.Parameters)
                {
                    if (string.IsNullOrWhiteSpace(parameter.Name)) continue;
                    if (input.Kind == ScriptInputDeclarationKind.Flag)
                    {
                        flags.Add(parameter);
                        if (string.Equals(parameter.Name, "help", StringComparison.OrdinalIgnoreCase))
                            node.UserDeclaredHelpFlag = true;
                    }
                    else
                    {
                        args.Add(parameter);
                    }
                }
                continue;
            }

            if (statement is SubcommandStatementSyntax child)
            {
                var childNode = BuildSubcommandNode(
                    sourceName, sourceText,
                    child.Name, child.Modifiers, child.Span, child.Body.Statements,
                    child.DocComment);

                if (node.Children.ContainsKey(childNode.Name!))
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.duplicate_subcommand",
                        Title: $"Subcommand '{childNode.Name}' is declared more than once at this level.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: child.Span,
                        Label: "rename or remove this duplicate subcommand"));
                }
                node.Children[childNode.Name!] = childNode;
                continue;
            }

            body.Add(statement);
        }

        ValidateScriptInputs(sourceName, sourceText, flags, args);

        var documented = CollectDocumentedNames(statements.OfType<ScriptInputStatementSyntax>(), docComment);
        node.Flags = ApplyDocumentedDescriptions(flags, documented);
        node.Arguments = ApplyDocumentedDescriptions(args, documented);
        node.BodyStatements = body;
        return node;
    }

    private async IAsyncEnumerable<object?> EvaluateScriptWithSubcommandsAsync(
        string sourceName,
        string sourceText,
        ScriptStatementSyntax script,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var root = BuildSubcommandNode(
            sourceName, sourceText,
            name: null,
            modifiers: SubcommandModifier.None,
            span: script.Span,
            statements: script.Statements,
            docComment: script.DocComment);

        var argv = GetCurrentScriptArguments();
        var (path, helpLevel) = ResolveDispatch(sourceName, sourceText, root, argv);

        // Bind root's globals before anything else runs (so top-level setup can see them).
        await BindLevelInputsAsync(sourceName, sourceText, path[0], cancellationToken);

        // Top-level body always runs as setup.
        PreRegisterTypeDefinitions(sourceName, sourceText, root.BodyStatements);
        await foreach (var value in YieldBodyStatementsAsync(sourceName, sourceText, root.BodyStatements, cancellationToken))
            yield return value;

        // If --help was triggered, emit help for the requested level and stop.
        if (helpLevel is not null)
        {
            yield return BuildAutoHelpTopic(helpLevel, path);
            yield break;
        }

        // Walk deeper levels in order, honoring eager/leaf rules.
        await foreach (var value in ExecuteDispatchPathAsync(sourceName, sourceText, path, cancellationToken))
            yield return value;

        // If we ended on a node whose children exist but none was picked, honor vital/auto-help.
        var leaf = path[^1].Node;
        var isLeafChosen = path.Count > 1;
        var leafPickedNoChild = leaf.Children.Count > 0 && !path.Skip(1).Any();
        if (!isLeafChosen && leaf.Children.Count > 0 && !HasEffectiveBody(leaf))
        {
            // root with children, no child picked, no body at root → auto-help (unless vital).
            if ((leaf.Modifiers & SubcommandModifier.Vital) != 0)
            {
                throw CreateRequiredChildException(sourceName, sourceText, leaf);
            }
            yield return BuildAutoHelpTopic(leaf, path);
        }
    }

    private static bool HasEffectiveBody(SubcommandNode node)
    {
        // A node has an effective body if it has any non-definition statements that produce behavior.
        return node.BodyStatements.Count > 0;
    }

    private async IAsyncEnumerable<object?> ExecuteDispatchPathAsync(
        string sourceName,
        string sourceText,
        IReadOnlyList<DispatchFrame> path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // path[0] is root (already handled for global binding + setup body by caller).
        // For each subsequent level, push a scope, bind locals, and either run body (eager or leaf) or skip.
        await foreach (var value in ExecuteLevelRecursiveAsync(sourceName, sourceText, path, 1, cancellationToken))
            yield return value;
    }

    private async IAsyncEnumerable<object?> ExecuteLevelRecursiveAsync(
        string sourceName,
        string sourceText,
        IReadOnlyList<DispatchFrame> path,
        int index,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (index >= path.Count) yield break;

        var frame = path[index];
        var node = frame.Node;
        var isLeaf = index == path.Count - 1;

        using var _ = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal));
        await BindLevelInputsAsync(sourceName, sourceText, frame, cancellationToken);

        var shouldRunBody = isLeaf || (node.Modifiers & SubcommandModifier.Eager) != 0;

        if (shouldRunBody && node.BodyStatements.Count > 0)
        {
            PreRegisterTypeDefinitions(sourceName, sourceText, node.BodyStatements);
            await foreach (var value in YieldBodyStatementsAsync(sourceName, sourceText, node.BodyStatements, cancellationToken))
                yield return value;
        }

        if (isLeaf)
        {
            // Leaf reached. If this leaf has children but no body, emit auto-help / vital.
            if (node.Children.Count > 0 && node.BodyStatements.Count == 0)
            {
                if ((node.Modifiers & SubcommandModifier.Vital) != 0)
                {
                    throw CreateRequiredChildException(sourceName, sourceText, node);
                }
                yield return BuildAutoHelpTopic(node, path);
            }
            yield break;
        }

        await foreach (var value in ExecuteLevelRecursiveAsync(sourceName, sourceText, path, index + 1, cancellationToken))
            yield return value;
    }

    private async IAsyncEnumerable<object?> YieldBodyStatementsAsync(
        string sourceName,
        string sourceText,
        IReadOnlyList<StatementSyntax> statements,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var statement in statements)
        {
            if (statement is ScriptInputStatementSyntax or SubcommandStatementSyntax) continue;

            IReadOnlyList<object?> values = await AsyncEnumerableExtensions.ToListAsync(
                EvaluateStatementAsync(sourceName, sourceText, statement, cancellationToken),
                cancellationToken);

            if (ShouldSuppressStatementResults(statement, values))
                values = Array.Empty<object?>();

            UpdateLastResultIfAny(values);

            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
            }
        }
    }

    private async Task BindLevelInputsAsync(
        string sourceName,
        string sourceText,
        DispatchFrame frame,
        CancellationToken cancellationToken)
    {
        var node = frame.Node;

        // Flags: either the parsed value or the default or null (if optional).
        foreach (var parameter in node.Flags)
        {
            object? value;

            if (frame.FlagValues.TryGetValue(parameter.Name, out var argumentValue))
            {
                value = ConvertScriptInputValue(sourceName, sourceText, parameter, argumentValue.Value, "flag");
            }
            else if (parameter.DefaultValue is not null)
            {
                var defaultValue = await EvaluatePipelineAsync(
                    sourceName, sourceText, parameter.DefaultValue, cancellationToken)
                    .FirstOrDefaultAsync(cancellationToken);
                value = ConvertScriptInputValue(sourceName, sourceText, parameter, defaultValue, "flag");
            }
            else if (parameter.IsOptional)
            {
                value = null;
            }
            else
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.missing_script_flag",
                    Title: $"Missing required script flag '{parameter.Name}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: null,
                    Label: $"provide --{GetPrimaryScriptOptionName(parameter.Name)}",
                    Help: BuildSubcommandUsageHelp([frame], node)));
            }

            DeclareVariable(parameter.Name, ToVariableBinding(value), DeclarationModifier.Default);
        }

        // Positional arguments.
        var positionals = frame.PositionalValues;
        var positionalIndex = 0;
        var restParameter = node.Arguments.LastOrDefault(static p => p.IsRest);

        foreach (var parameter in node.Arguments)
        {
            if (parameter.IsRest)
            {
                var restValues = positionals
                    .Skip(positionalIndex)
                    .Select(static a => a.Value)
                    .ToList();
                DeclareVariable(parameter.Name, ToVariableBinding(restValues), DeclarationModifier.Default);
                continue;
            }

            object? value;

            if (positionalIndex < positionals.Count)
            {
                value = ConvertScriptInputValue(sourceName, sourceText, parameter, positionals[positionalIndex++].Value, "argument");
            }
            else if (parameter.DefaultValue is not null)
            {
                var defaultValue = await EvaluatePipelineAsync(
                    sourceName, sourceText, parameter.DefaultValue, cancellationToken)
                    .FirstOrDefaultAsync(cancellationToken);
                value = ConvertScriptInputValue(sourceName, sourceText, parameter, defaultValue, "argument");
            }
            else if (parameter.IsOptional)
            {
                value = null;
            }
            else
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.missing_script_argument",
                    Title: $"Missing required script argument '{parameter.Name}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: null,
                    Label: node.Name is null
                        ? "provide a positional argument"
                        : $"provide a positional argument to '{node.Name}'",
                    Help: BuildSubcommandUsageHelp([frame], node)));
            }

            DeclareVariable(parameter.Name, ToVariableBinding(value), DeclarationModifier.Default);
        }

        if (restParameter is null && positionalIndex < positionals.Count)
        {
            var unexpected = positionals[positionalIndex];
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.unexpected_script_argument",
                Title: $"Unexpected {(node.Name is null ? "script" : node.Name + " subcommand")} argument '{FormatScriptArgumentForDiagnostic(unexpected.Value)}'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: null,
                Label: $"argument #{unexpected.Index + 1} does not match any declared argument",
                Help: BuildSubcommandUsageHelp([frame], node)));
        }
    }

    private (List<DispatchFrame> Path, SubcommandNode? HelpLevel) ResolveDispatch(
        string sourceName,
        string sourceText,
        SubcommandNode root,
        IReadOnlyList<object?> argv)
    {
        var path = new List<DispatchFrame> { new() { Node = root } };
        SubcommandNode? helpLevel = null;
        var parseOptions = true;

        for (var i = 0; i < argv.Count; i++)
        {
            var raw = argv[i];

            if (parseOptions && raw is string text && text.Length > 0)
            {
                if (text == "--")
                {
                    parseOptions = false;
                    continue;
                }

                if (text.StartsWith("--", StringComparison.Ordinal) && text.Length > 2)
                {
                    var optionText = text[2..];
                    string optionName;
                    string? inlineValue = null;
                    var eq = optionText.IndexOf('=');
                    if (eq >= 0)
                    {
                        optionName = optionText[..eq];
                        inlineValue = optionText[(eq + 1)..];
                    }
                    else
                    {
                        optionName = optionText;
                    }

                    // Auto --help unless the current-level stack has a user-declared help flag.
                    if (string.Equals(optionName, "help", StringComparison.OrdinalIgnoreCase) &&
                        !PathHasUserHelpFlag(path))
                    {
                        helpLevel = path[^1].Node;
                        continue;
                    }

                    // Search path leaf-to-root for a declared flag with this option name.
                    FunctionParameterSyntax? matched = null;
                    DispatchFrame? owningFrame = null;
                    for (var j = path.Count - 1; j >= 0; j--)
                    {
                        foreach (var flag in path[j].Node.Flags)
                        {
                            if (ScriptOptionNameMatches(flag, optionName))
                            {
                                matched = flag;
                                owningFrame = path[j];
                                break;
                            }
                        }
                        if (matched is not null) break;
                    }

                    if (matched is null)
                    {
                        var leafNode = path[^1].Node;
                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh.runtime.unknown_script_flag",
                            Title: $"Unknown script flag '--{optionName}'.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: null,
                            Label: "this script does not declare a matching flag at the current level",
                            Help: BuildUnknownSubcommandFlagHelp(leafNode)));
                    }

                    object? value;
                    if (IsBooleanScriptInput(matched))
                    {
                        value = inlineValue is null ? (object)true : inlineValue;
                    }
                    else if (inlineValue is not null)
                    {
                        value = inlineValue;
                    }
                    else
                    {
                        if (i + 1 >= argv.Count)
                        {
                            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                                Code: "tosh.runtime.script_option_requires_value",
                                Title: $"Option '--{optionName}' requires a value.",
                                SourceName: sourceName,
                                SourceText: sourceText,
                                Span: null,
                                Label: $"'{matched.Name}' expects a value",
                                Help: BuildSubcommandUsageHelp(path, owningFrame!.Node)));
                        }
                        value = argv[++i];
                    }

                    owningFrame!.FlagValues[matched.Name] = new ScriptArgumentValue(value, i);
                    continue;
                }
            }

            // Positional token. If it matches a child subcommand at the current leaf AND no
            // positionals have been taken by that leaf yet, dispatch into the child.
            var leaf = path[^1];
            if (parseOptions &&
                leaf.PositionalValues.Count == 0 &&
                raw is string tokenText &&
                leaf.Node.Children.TryGetValue(tokenText, out var child))
            {
                path.Add(new DispatchFrame { Node = child });
                continue;
            }

            // No explicit child matched. If the current leaf declares a child with the
            // `default` modifier and no positionals have been taken yet, route into that
            // child and reinterpret the current token as its first positional.
            if (parseOptions &&
                leaf.PositionalValues.Count == 0)
            {
                SubcommandNode? defaultChild = null;
                foreach (var kv in leaf.Node.Children)
                {
                    if ((kv.Value.Modifiers & SubcommandModifier.Default) != 0)
                    {
                        defaultChild = kv.Value;
                        break;
                    }
                }
                if (defaultChild is not null)
                {
                    var defaultFrame = new DispatchFrame { Node = defaultChild };
                    defaultFrame.PositionalValues.Add(new ScriptArgumentValue(raw, i));
                    path.Add(defaultFrame);
                    continue;
                }
            }

            leaf.PositionalValues.Add(new ScriptArgumentValue(raw, i));
        }

        return (path, helpLevel);
    }

    private static bool PathHasUserHelpFlag(IReadOnlyList<DispatchFrame> path)
    {
        for (var i = path.Count - 1; i >= 0; i--)
        {
            if (path[i].Node.UserDeclaredHelpFlag) return true;
        }
        return false;
    }

    private static string? BuildUnknownSubcommandFlagHelp(SubcommandNode node)
    {
        var flags = node.Flags
            .Where(static flag => !flag.IsRest && !string.IsNullOrWhiteSpace(flag.Name))
            .Select(static flag => $"--{GetPrimaryScriptOptionName(flag.Name)}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static option => option, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var children = node.Children
            .Where(static kv => (kv.Value.Modifiers & SubcommandModifier.Hidden) == 0)
            .Select(static kv => kv.Key)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (flags.Length > 0 && children.Length > 0)
        {
            return $"available flags here: {string.Join(", ", flags)}; available subcommands: {string.Join(", ", children)}";
        }

        if (flags.Length > 0)
        {
            return $"available flags here: {string.Join(", ", flags)}";
        }

        if (children.Length > 0)
        {
            return $"choose a subcommand first: {string.Join(", ", children)}";
        }

        return null;
    }

    private string BuildSubcommandUsageHelp(IReadOnlyList<DispatchFrame> path, SubcommandNode target)
        => BuildSubcommandUsageLine(path, target).Replace("Usage:", "usage:", StringComparison.Ordinal);

    private string BuildSubcommandUsageLine(IReadOnlyList<DispatchFrame> path, SubcommandNode target)
    {
        var scriptPath = GetCurrentScriptPath();
        var scriptDisplayName = string.IsNullOrWhiteSpace(scriptPath)
            ? "<script>"
            : (Path.GetFileName(scriptPath) ?? scriptPath);
        var breadcrumb = new List<string> { scriptDisplayName };

        foreach (var frame in path)
        {
            if (frame.Node.Name is not null)
            {
                breadcrumb.Add(frame.Node.Name);
            }

            if (ReferenceEquals(frame.Node, target))
            {
                break;
            }
        }

        if (!path.Any(p => ReferenceEquals(p.Node, target)) && target.Name is not null)
        {
            breadcrumb.Add(target.Name);
        }

        var usage = new StringBuilder();
        usage.Append("Usage: ").Append(string.Join(' ', breadcrumb));

        if (target.Flags.Count > 0 || path.Any(p => p.Node.Flags.Count > 0) || !target.UserDeclaredHelpFlag)
        {
            usage.Append(" [options]");
        }

        if (target.Children.Count > 0)
        {
            usage.Append(" <subcommand>");
        }

        foreach (var arg in target.Arguments)
        {
            usage.Append(' ');
            usage.Append(arg.IsOptional || arg.DefaultValue is not null ? '[' : '<');
            usage.Append(arg.Name);
            if (arg.IsRest)
            {
                usage.Append("...");
            }

            usage.Append(arg.IsOptional || arg.DefaultValue is not null ? ']' : '>');
        }

        return usage.ToString();
    }

    private static bool ScriptOptionNameMatches(FunctionParameterSyntax flag, string optionName)
    {
        if (string.Equals(flag.Name, optionName, StringComparison.OrdinalIgnoreCase)) return true;
        var primary = GetPrimaryScriptOptionName(flag.Name);
        return string.Equals(primary, optionName, StringComparison.OrdinalIgnoreCase);
    }

    private ToshDiagnosticException CreateRequiredChildException(
        string sourceName,
        string sourceText,
        SubcommandNode node)
    {
        var visible = node.Children
            .Where(kv => (kv.Value.Modifiers & SubcommandModifier.Hidden) == 0)
            .Select(kv => kv.Key);
        var children = string.Join(" | ", visible);
        var label = node.Name is null
            ? $"pick one of: {children}"
            : $"subcommand '{node.Name}' requires a child: {children}";
        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.subcommand_required",
            Title: node.Name is null
                ? "A subcommand is required."
                : $"Subcommand '{node.Name}' requires a child subcommand.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: null,
            Label: label,
            Help: BuildSubcommandUsageHelp([], node)));
    }

    /// <summary>
    /// Describes a subcommand level as a <see cref="ToastScriptHelp"/>, which the host turns
    /// into whatever it renders, so that `script.tosh sub --help` is drawn by the same panel
    /// renderer as `help &lt;name&gt;` — colour, boxes and column layout included.
    /// </summary>
    /// <remarks>
    /// This replaces a hand-written plain-text writer. Two renderers for the same kind of content
    /// is how they drift: the text one had gained an Arguments section only just now, and had
    /// never had the styling the panel renderer applies to names, types and usage lines.
    ///
    /// It described the level as a `HelpTopic` until `TOAST-0006`, which is the shell's help
    /// metadata — the language now says what the interface *is* and asks the host to present
    /// it, keeping the same rendering while naming no shell type.
    /// </remarks>
    private object BuildAutoHelpTopic(SubcommandNode target, IReadOnlyList<DispatchFrame> path)
    {
        var usageLine = BuildSubcommandUsageLine(path, target);

        // The root node carries no name — the script file is its name — so the title falls back
        // to the same display name the usage line uses rather than to a placeholder.
        var scriptPath = GetCurrentScriptPath();
        var scriptDisplayName = string.IsNullOrWhiteSpace(scriptPath)
            ? "<script>"
            : Path.GetFileName(scriptPath) ?? scriptPath;

        var displayName = target.Name is not null
            ? string.Join(' ', path.Where(p => p.Node.Name is not null).Select(p => p.Node.Name).Append(target.Name).Distinct())
            : scriptDisplayName;

        var subcommands = target.Children
            .Where(child => (child.Value.Modifiers & SubcommandModifier.Hidden) == 0)
            .Select(child => new ToastScriptHelpArgument(child.Key, child.Value.Description ?? string.Empty))
            .ToList();

        var arguments = target.Arguments
            .Select(argument => new ToastScriptHelpArgument(
                Name: argument.IsRest ? argument.Name + "..." : argument.Name,
                Description: argument.Description ?? string.Empty,
                Required: !argument.IsOptional && argument.DefaultValue is null,
                TypeName: argument.TypeName))
            .ToList();

        // Every flag reachable at this level: those declared here, and those declared by the
        // levels dispatched through to reach it.
        var options = new List<ToastScriptHelpOption>();
        foreach (var frame in path)
        {
            foreach (var flag in frame.Node.Flags)
            {
                options.Add(ToHelpOption(flag));
            }
        }

        if (!path.Any(frame => ReferenceEquals(frame.Node, target)))
        {
            foreach (var flag in target.Flags)
            {
                options.Add(ToHelpOption(flag));
            }
        }

        if (!PathHasUserHelpFlag(path) && !target.UserDeclaredHelpFlag)
        {
            options.Add(new ToastScriptHelpOption("--help", "Show this help message"));
        }

        var help = new ToastScriptHelp(
            Name: string.IsNullOrWhiteSpace(displayName) ? scriptDisplayName : displayName,
            Description: target.Description ?? string.Empty,
            Usage: usageLine.StartsWith("Usage: ", StringComparison.Ordinal)
                ? usageLine["Usage: ".Length..]
                : usageLine,
            Examples: target.Examples ?? Array.Empty<string>(),
            Arguments: arguments.Count > 0 ? arguments : null,
            Options: options.Count > 0 ? options : null,
            Subcommands: subcommands.Count > 0 ? subcommands : null);

        // No factory means no host opinion about help, so the description is the value. It
        // still renders — as a record rather than a panel.
        return LanguageRuntime.ScriptHelpFactory?.CreateScriptHelpTopic(help) ?? help;
    }

    private static ToastScriptHelpOption ToHelpOption(FunctionParameterSyntax flag)
    {
        var syntax = "--" + GetPrimaryScriptOptionName(flag.Name);
        var badge = RenderTypeBadge(flag.TypeName);
        return new ToastScriptHelpOption(
            badge is null ? syntax : $"{syntax} {badge}",
            flag.Description ?? string.Empty);
    }

    private void WriteAutoHelp(SubcommandNode target, IReadOnlyList<DispatchFrame> path)
    {
        using var writer = new StringWriter();
        var usageLine = BuildSubcommandUsageLine(path, target);

        // Header: "name — description" (or just usage if no description).
        var titlePieces = new List<string>();
        var rootName = path.Count > 0 ? path[0].Node.Name : null;
        var displayName = target.Name is not null
            ? string.Join(' ', path.Where(p => p.Node.Name is not null).Select(p => p.Node.Name).Append(target.Name).Distinct())
            : rootName;

        if (!string.IsNullOrEmpty(displayName))
        {
            titlePieces.Add(displayName!);
            if (!string.IsNullOrEmpty(target.Description))
            {
                titlePieces.Add("— " + target.Description);
            }
        }
        else if (!string.IsNullOrEmpty(target.Description))
        {
            // Root invocation without a name still benefits from a
            // file-level description (sourced from a top-of-file
            // `## @summary ...` doc-comment block).
            titlePieces.Add(target.Description);
        }
        if (titlePieces.Count > 0)
        {
            writer.WriteLine(string.Join(' ', titlePieces));
            writer.WriteLine();
        }

        writer.WriteLine("  Usage  " + usageLine[("Usage: ".Length)..]);

        // ── Subcommands ────────────────────────────────────────────────
        var visibleChildren = target.Children
            .Where(kv => (kv.Value.Modifiers & SubcommandModifier.Hidden) == 0)
            .ToList();

        if (visibleChildren.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("  Subcommands");
            var nameWidth = visibleChildren.Max(kv => kv.Key.Length);
            foreach (var kv in visibleChildren)
            {
                var child = kv.Value;
                var name = kv.Key.PadRight(nameWidth);
                if (!string.IsNullOrEmpty(child.Description))
                {
                    writer.WriteLine($"    {name}  {child.Description}");
                }
                else
                {
                    writer.WriteLine($"    {kv.Key}");
                }
            }
        }

        // ── Arguments ─────────────────────────────────────────────────
        // The usage line above names the positional arguments but says nothing about them. Their
        // descriptions — the doc-comment written above each `arg` — were parsed, carried on the
        // parameter, and never rendered, so a subcommand that documented its arguments showed
        // only `--help` under Options and nothing else.
        if (target.Arguments.Count > 0)
        {
            var argumentNameWidth = target.Arguments.Max(argument => argument.Name.Length);
            var argumentTypeTokens = target.Arguments
                .Select(argument => RenderTypeBadge(argument.TypeName))
                .Where(static badge => !string.IsNullOrEmpty(badge))
                .ToList();
            var argumentTypeWidth = argumentTypeTokens.Count == 0
                ? 0
                : argumentTypeTokens.Max(badge => badge!.Length);

            writer.WriteLine();
            writer.WriteLine("  Arguments");

            foreach (var argument in target.Arguments)
            {
                writer.WriteLine(FormatArgumentLine(argument, argumentNameWidth, argumentTypeWidth));
            }
        }

        // ── Options ───────────────────────────────────────────────────
        // Collect every flag from every frame on the dispatch path AND the
        // target itself (when target isn't already on the path).
        var flagEntries = new List<(string Scope, FunctionParameterSyntax Flag)>();
        foreach (var frame in path)
        {
            var scopeLabel = frame.Node.Name is null ? "global" : frame.Node.Name!;
            foreach (var flag in frame.Node.Flags)
            {
                flagEntries.Add((scopeLabel, flag));
            }
        }
        if (!path.Any(p => ReferenceEquals(p.Node, target)))
        {
            foreach (var flag in target.Flags)
            {
                flagEntries.Add((target.Name ?? string.Empty, flag));
            }
        }

        var includeHelpFlag = !PathHasUserHelpFlag(path) && !target.UserDeclaredHelpFlag;

        if (flagEntries.Count > 0 || includeHelpFlag)
        {
            // Compute the rendered "--name" widths so we can right-align the
            // (optional) type column and align descriptions.
            var nameTokens = flagEntries
                .Select(e => "--" + GetPrimaryScriptOptionName(e.Flag.Name))
                .Append(includeHelpFlag ? "--help" : null)
                .Where(n => n is not null)
                .Cast<string>()
                .ToList();
            var nameWidth = nameTokens.Count == 0 ? 6 : nameTokens.Max(n => n.Length);

            // Type column width: only non-bool flags carry an explicit type.
            var typeTokens = flagEntries
                .Select(e => RenderTypeBadge(e.Flag.TypeName))
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();
            var typeWidth = typeTokens.Count == 0 ? 0 : typeTokens.Max(t => t!.Length);

            writer.WriteLine();
            writer.WriteLine("  Options");

            // Group flags by scope so global options stay together and
            // subcommand-local options follow.
            foreach (var group in flagEntries
                .GroupBy(e => e.Scope, StringComparer.Ordinal)
                .OrderBy(g => g.Key == "global" ? 0 : 1))
            {
                foreach (var (_, flag) in group)
                {
                    writer.WriteLine(FormatFlagLine(flag, nameWidth, typeWidth));
                }
            }

            if (includeHelpFlag)
            {
                var name = "--help".PadRight(nameWidth);
                var typePad = typeWidth == 0 ? string.Empty : new string(' ', typeWidth + 2);
                writer.WriteLine($"    {name}  {typePad}Show this help message");
            }
        }

        if (target.Examples is { Count: > 0 } examples)
        {
            writer.WriteLine();
            writer.WriteLine("  Examples");
            foreach (var example in examples)
            {
                foreach (var line in example.Split('\n'))
                {
                    writer.WriteLine("    " + line.TrimEnd('\r'));
                }
            }
        }

        LanguageRuntime.Output.WriteText(writer.ToString());
        LanguageRuntime.Output.Flush();
    }

    private static string FormatFlagLine(FunctionParameterSyntax flag, int nameWidth, int typeWidth)
    {
        var primary = "--" + GetPrimaryScriptOptionName(flag.Name);
        var name = primary.PadRight(nameWidth);

        var typeBadge = RenderTypeBadge(flag.TypeName);
        var typeColumn = typeWidth == 0
            ? string.Empty
            : (typeBadge ?? string.Empty).PadRight(typeWidth) + "  ";

        var description = string.IsNullOrEmpty(flag.Description) ? string.Empty : flag.Description;
        return $"    {name}  {typeColumn}{description}".TrimEnd();
    }

    /// <summary>
    /// Applies the descriptions from a subcommand's own doc-comment — its <c>@arg</c>,
    /// <c>@flag</c> and <c>@param</c> tags — to the inputs it declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both places may document an input: a tag in the subcommand's block, and a doc-comment
    /// written directly above the <c>arg</c> or <c>flag</c>. The subcommand's block wins, so one
    /// header can describe every input of a subcommand and override anything the declarations say
    /// individually. A declaration keeps its own description when the block does not mention it,
    /// which is what lets the two be mixed.
    /// </para>
    /// <para>
    /// Previously neither reached this far for a subcommand: the tags were parsed into the
    /// doc-comment and discarded, and the rendered help listed argument names with no descriptions
    /// beside them.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<FunctionParameterSyntax> ApplyDocumentedDescriptions(
        IReadOnlyList<FunctionParameterSyntax> parameters,
        DocComment? docComment)
        => ApplyDocumentedDescriptions(parameters, CollectDocumentedNames(Array.Empty<ScriptInputStatementSyntax>(), docComment));

    private static IReadOnlyList<FunctionParameterSyntax> ApplyDocumentedDescriptions(
        IReadOnlyList<FunctionParameterSyntax> parameters,
        IReadOnlyDictionary<string, string> documented)
    {
        if (parameters.Count == 0 || documented.Count == 0)
        {
            return parameters;
        }

        var described = new List<FunctionParameterSyntax>(parameters.Count);

        foreach (var parameter in parameters)
        {
            described.Add(
                documented.TryGetValue(parameter.Name, out var description)
                && !string.IsNullOrWhiteSpace(description)
                    ? parameter with { Description = description.Trim() }
                    : parameter);
        }

        return described;
    }

    /// <summary>
    /// Gathers every <c>@arg</c> / <c>@flag</c> / <c>@param</c> tag that describes this level's
    /// inputs, from the declarations themselves and from the block's own comment.
    /// </summary>
    /// <remarks>
    /// `TS-P2-67`. A comment is attached to the declaration that follows it, so a single block
    /// documenting several inputs — the way anyone actually writes one — reached only the first.
    /// `## @flag clean - remove artefacts` written above `arg target` described nothing, because
    /// the tag lived on the argument's comment while the flag was a separate statement.
    /// Declarations are read in order and the block's own comment overlays them, which keeps the
    /// precedence already decided: a subcommand-level tag wins over a per-declaration one.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> CollectDocumentedNames(
        IEnumerable<ScriptInputStatementSyntax> declarations,
        DocComment? blockDoc)
    {
        var documented = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var declaration in declarations)
        {
            if (declaration.DocComment is not { Parameters.Count: > 0 } doc)
            {
                continue;
            }

            foreach (var (name, description) in doc.Parameters)
            {
                documented[name] = description;
            }
        }

        if (blockDoc is { Parameters.Count: > 0 })
        {
            foreach (var (name, description) in blockDoc.Parameters)
            {
                documented[name] = description;
            }
        }

        return documented;
    }

    /// <summary>
    /// One row of the Arguments section: the argument's name, its type when it declares one, and
    /// the description written above it. A rest argument keeps its ellipsis so the row matches the
    /// usage line, and an optional one is marked by its default rather than by brackets, which the
    /// usage line already carries.
    /// </summary>
    private static string FormatArgumentLine(
        FunctionParameterSyntax argument,
        int nameWidth,
        int typeWidth)
    {
        var name = (argument.IsRest ? argument.Name + "..." : argument.Name).PadRight(nameWidth);

        var typeBadge = RenderTypeBadge(argument.TypeName);
        var typeColumn = typeWidth == 0
            ? string.Empty
            : (typeBadge ?? string.Empty).PadRight(typeWidth) + "  ";

        var description = string.IsNullOrEmpty(argument.Description) ? string.Empty : argument.Description;
        return $"    {name}  {typeColumn}{description}".TrimEnd();
    }

    private static string? RenderTypeBadge(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;
        // Boolean flags are switches; rendering "<bool>" is noise.
        if (string.Equals(typeName, "bool", StringComparison.OrdinalIgnoreCase)) return null;
        return $"<{typeName}>";
    }
}
