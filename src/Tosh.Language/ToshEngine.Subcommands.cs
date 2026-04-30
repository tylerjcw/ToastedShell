using System.Runtime.CompilerServices;
using System.Text;
using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed partial class ToshEngine
{
    private sealed class SubcommandNode
    {
        public string? Name;
        public string? Description;
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

        node.Flags = flags;
        node.Arguments = args;
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
            statements: script.Statements);

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
            WriteAutoHelp(helpLevel, path);
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
            WriteAutoHelp(leaf, path);
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
                WriteAutoHelp(node, path);
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

    private void WriteAutoHelp(SubcommandNode target, IReadOnlyList<DispatchFrame> path)
    {
        var writer = Runtime.Output;
        writer.WriteLine(BuildSubcommandUsageLine(path, target));

        if (!string.IsNullOrEmpty(target.Description))
        {
            writer.WriteLine();
            writer.WriteLine(target.Description);
        }

        if (target.Children.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Subcommands:");
            foreach (var kv in target.Children)
            {
                var child = kv.Value;
                if ((child.Modifiers & SubcommandModifier.Hidden) != 0) continue;
                if (!string.IsNullOrEmpty(child.Description))
                    writer.WriteLine($"  {kv.Key,-18} {child.Description}");
                else
                    writer.WriteLine($"  {kv.Key}");
            }
        }

        // Local + inherited (global) flags shown together.
        var flagLines = new List<string>();
        foreach (var frame in path)
        {
            var scopeLabel = frame.Node.Name is null ? "global" : frame.Node.Name;
            foreach (var flag in frame.Node.Flags)
            {
                flagLines.Add(FormatFlagLine(flag, scopeLabel));
            }
        }
        if (target.Children.Count > 0 || target.Flags.Count == 0 || !path.Any(p => p.Node.Flags.Count > 0))
        {
            // Nothing — flagLines already captures everything including target.Flags if target is on path.
        }
        // If target is not root and not yet appended (because target != any path frame)...
        if (!path.Any(p => ReferenceEquals(p.Node, target)))
        {
            foreach (var flag in target.Flags)
            {
                flagLines.Add(FormatFlagLine(flag, target.Name ?? ""));
            }
        }

        if (flagLines.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Options:");
            foreach (var line in flagLines) writer.WriteLine(line);
        }

        if (!PathHasUserHelpFlag(path) && !target.UserDeclaredHelpFlag)
        {
            if (flagLines.Count == 0)
            {
                writer.WriteLine();
                writer.WriteLine("Options:");
            }
            writer.WriteLine("  --help             Show this help message");
        }

        writer.Flush();
    }

    private static string FormatFlagLine(FunctionParameterSyntax flag, string scope)
    {
        var primary = GetPrimaryScriptOptionName(flag.Name);
        var typeLabel = flag.TypeName ?? "any";
        var scopeTag = string.IsNullOrEmpty(scope) ? "" : $"  [{scope}]";
        var descriptionSuffix = !string.IsNullOrEmpty(flag.Description) ? $"  {flag.Description}" : "";
        return $"  --{primary,-16} {typeLabel}{scopeTag}{descriptionSuffix}";
    }
}
