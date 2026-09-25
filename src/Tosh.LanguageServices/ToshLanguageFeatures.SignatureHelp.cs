using System.Reflection;
using Tosh.Runtime;
using Tosh.Runtime.Units;
using Tosh.Language.Parsing;

namespace Tosh.LanguageServices;

public sealed partial class ToshLanguageFeatures
{
    public LspSignatureHelp? GetSignatureHelp(string text, string sourceName, LspPosition position)
    {
        var parseResult = ToshParser.Parse(text, sourceName);
        var offset = new TextCoordinateMap(parseResult.SourceText).ToOffset(position);
        var semantics = DocumentSemanticModel.Create(sourceName, parseResult.SourceText);
        var declarations = DeclarationIndex.Create(sourceName, parseResult.SourceText);

        if (FindCommandCallSite(parseResult.Statement, parseResult.SourceText, offset) is { } commandCallSite)
        {
            var overloads = declarations.GetVisibleFunctionOverloads(offset, commandCallSite.Command.Name);
            if (overloads.Count > 0)
            {
                return CreateTopLevelFunctionSignatureHelp(overloads, commandCallSite.ActiveParameter);
            }

            if (GetMetadataLookup().TryGetValue(commandCallSite.Command.Name, out var metadataEntry) &&
                metadataEntry.Arguments.Count > 0)
            {
                return CreateBuiltInCommandSignatureHelp(metadataEntry, commandCallSite.ActiveParameter);
            }
        }

        if (FindCallSite(parseResult.Statement, parseResult.SourceText, offset) is not { } callSite)
        {
            return null;
        }

        return callSite.Argument switch
        {
            NewObjectArgumentSyntax newObject => CreateShellConstructorSignatureHelp(
                    semantics.ResolveVisibleShellClass(offset, newObject.TypeName),
                    callSite.ActiveParameter)
                ?? CreateConstructorSignatureHelp(
                    semantics.CreateTypeResolver(offset).Resolve(newObject.TypeName),
                    callSite.ActiveParameter),
            StaticMethodCallArgumentSyntax staticCall => CreateShellStaticCallSignatureHelp(
                    semantics,
                    staticCall.Path,
                    offset,
                    callSite.ActiveParameter)
                ?? CreateStaticCallSignatureHelp(
                    semantics,
                    staticCall.Path,
                    offset,
                    callSite.ActiveParameter),
            MethodCallArgumentSyntax methodCall => CreateShellInstanceMethodSignatureHelp(
                    TryGetSourceSlice(text, methodCall.Target.Span) is { } targetReference
                        ? semantics.ResolveShellTargetClass(offset, targetReference)
                        : null,
                    methodCall.MethodName,
                    callSite.ActiveParameter)
                ?? CreateInstanceMethodSignatureHelp(
                    semantics.ResolveArgumentType(methodCall.Target, offset),
                    methodCall.MethodName,
                    callSite.ActiveParameter),
            _ => null,
        };
    }

    private static LspSignatureHelp CreateBuiltInCommandSignatureHelp(CommandMetadata entry, int activeParameter)
    {
        var parameterLabels = entry.Arguments
            .Select(arg =>
            {
                var typePart = arg.TypeName is not null ? $": {arg.TypeName}" : "";
                var optPart = arg.Required ? "" : "?";
                return $"{arg.Name}{optPart}{typePart}";
            })
            .ToArray();

        var labelBuilder = new System.Text.StringBuilder(entry.Name);
        foreach (var paramLabel in parameterLabels)
        {
            labelBuilder.Append($" <{paramLabel}>");
        }

        foreach (var option in entry.Options)
        {
            var primaryFlag = option.Syntax.Split(',', StringSplitOptions.TrimEntries)[0];
            labelBuilder.Append($" [{primaryFlag}]");
        }

        var signatureLabel = labelBuilder.ToString();

        var parameters = entry.Arguments
            .Select(arg => new LspParameterInformation(
                Label: arg.TypeName is not null
                    ? $"{arg.Name}{(arg.Required ? "" : "?")}: {arg.TypeName}"
                    : $"{arg.Name}{(arg.Required ? "" : "?")}",
                Documentation: arg.Description))
            .ToArray();

        var signature = new LspSignatureInformation(
            signatureLabel,
            Documentation: entry.Description,
            Parameters: parameters);

        var boundedActive = entry.Arguments.Count == 0
            ? 0
            : Math.Min(activeParameter, entry.Arguments.Count - 1);

        return new LspSignatureHelp([signature], ActiveSignature: 0, ActiveParameter: boundedActive);
    }

    private static (string Word, int Start, int End) FindWordAt(string text, int offset)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (string.Empty, 0, 0);
        }

        var index = Math.Clamp(offset, 0, Math.Max(0, text.Length - 1));

        bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch is '$' or '_' or '-' or '.';

        if (!IsWordChar(text[index]) && index > 0 && IsWordChar(text[index - 1]))
        {
            index--;
        }

        if (!IsWordChar(text[index]))
        {
            return (string.Empty, index, index);
        }

        var start = index;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }

        var end = index + 1;
        while (end < text.Length && IsWordChar(text[end]))
        {
            end++;
        }

        return (text[start..end], start, end);
    }

    private static LspSignatureHelp? CreateShellConstructorSignatureHelp(DocumentSemanticModel.ShellClassSymbol? shellClass, int activeParameter)
    {
        if (shellClass is null)
        {
            return null;
        }

        var constructors = shellClass.Constructors
            .OrderBy(constructor => constructor.Parameters.Count)
            .ToArray();

        if (constructors.Length == 0)
        {
            return null;
        }

        var signatures = constructors
            .Select(constructor => new LspSignatureInformation(
                FormatShellConstructorSignature(shellClass.Name, constructor),
                Documentation: null,
                Parameters: constructor.Parameters
                    .Select(parameter => new LspParameterInformation(FormatShellParameter(parameter)))
                    .ToArray()))
            .ToArray();

        return CreateShellSignatureHelp(constructors.Select(constructor => constructor.Parameters.Count).ToArray(), signatures, activeParameter);
    }

    private static LspSignatureHelp? CreateConstructorSignatureHelp(Type? type, int activeParameter)
    {
        if (type is null)
        {
            return null;
        }

        return CreateSignatureHelp(
            type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Cast<MethodBase>()
                .ToArray(),
            activeParameter,
            constructor => ClrMetadataFormatting.FormatConstructorSignature((ConstructorInfo)constructor));
    }

    private static LspSignatureHelp? CreateStaticCallSignatureHelp(
        DocumentSemanticModel semantics,
        string path,
        int offset,
        int activeParameter)
    {
        var resolver = semantics.CreateTypeResolver(offset);
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var type = resolver.Resolve(string.Join('.', segments.Take(prefixLength)));

            if (type is null)
            {
                continue;
            }

            return CreateMethodSignatureHelp(type, segments[^1], activeParameter, staticOnly: true);
        }

        return null;
    }

    private static LspSignatureHelp? CreateShellStaticCallSignatureHelp(
        DocumentSemanticModel semantics,
        string path,
        int offset,
        int activeParameter)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 2 &&
            semantics.ResolveVisibleShellClass(offset, segments[0]) is { } shellClass)
        {
            var methods = shellClass.Methods
                .Where(method => method.IsStatic && string.Equals(method.Name, segments[1], StringComparison.OrdinalIgnoreCase))
                .OrderBy(method => method.Parameters.Count)
                .ToArray();

            return CreateShellMethodSignatureHelp(methods, activeParameter);
        }

        return null;
    }

    private static LspSignatureHelp? CreateShellInstanceMethodSignatureHelp(
        DocumentSemanticModel.ShellClassSymbol? shellClass,
        string methodName,
        int activeParameter)
    {
        if (shellClass is null)
        {
            return null;
        }

        var methods = shellClass.Methods
            .Where(method => !method.IsStatic && string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(method => method.Parameters.Count)
            .ToArray();

        return CreateShellMethodSignatureHelp(methods, activeParameter);
    }

    private static LspSignatureHelp? CreateInstanceMethodSignatureHelp(Type? targetType, string methodName, int activeParameter)
    {
        return targetType is null
            ? null
            : CreateMethodSignatureHelp(targetType, methodName, activeParameter, staticOnly: false);
    }

    private static LspSignatureHelp? CreateMethodSignatureHelp(Type type, string methodName, int activeParameter, bool staticOnly)
    {
        var bindingFlags = BindingFlags.Public | (staticOnly ? BindingFlags.Static : BindingFlags.Instance);
        var methods = type.GetMethods(bindingFlags)
            .Where(method => !method.IsSpecialName && string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .OrderBy(method => method.GetParameters().Length)
            .Cast<MethodBase>()
            .ToArray();

        return CreateSignatureHelp(methods, activeParameter, method => ClrMetadataFormatting.FormatMethodSignature((MethodInfo)method));
    }

    private static LspSignatureHelp? CreateShellMethodSignatureHelp(
        IReadOnlyList<DocumentSemanticModel.ShellClassMethodSymbol> methods,
        int activeParameter)
    {
        if (methods.Count == 0)
        {
            return null;
        }

        var signatures = methods
            .Select(method => new LspSignatureInformation(
                FormatShellMethodSignature(method),
                Documentation: null,
                Parameters: method.Parameters
                    .Select(parameter => new LspParameterInformation(FormatShellParameter(parameter)))
                    .ToArray()))
            .ToArray();

        return CreateShellSignatureHelp(methods.Select(method => method.Parameters.Count).ToArray(), signatures, activeParameter);
    }

    private static LspSignatureHelp CreateTopLevelFunctionSignatureHelp(
        IReadOnlyList<DeclarationIndex.IndexedFunctionDeclaration> functions,
        int activeParameter)
    {
        var ordered = functions
            .OrderBy(function => function.Parameters.Count)
            .ThenBy(function => FormatTopLevelFunctionSignature(function), StringComparer.Ordinal)
            .ToArray();

        var signatures = ordered
            .Select(function =>
            {
                var doc = function.DocComment;
                return new LspSignatureInformation(
                    FormatTopLevelFunctionSignature(function),
                    Documentation: doc?.Description is { Length: > 0 } desc
                        ? desc
                        : function.IsCommandWrapper ? "Command-wrapper function" : null,
                    Parameters: function.Parameters
                        .Select(parameter => new LspParameterInformation(
                            FormatShellParameter(parameter),
                            Documentation: doc?.Parameters.TryGetValue(parameter.Name, out var paramDesc) == true && paramDesc.Length > 0
                                ? paramDesc
                                : null))
                        .ToArray());
            })
            .ToArray();

        return CreateShellSignatureHelp(ordered.Select(function => function.Parameters.Count).ToArray(), signatures, activeParameter);
    }

    private static LspSignatureHelp? CreateSignatureHelp(
        IReadOnlyList<MethodBase> methods,
        int activeParameter,
        Func<MethodBase, string> formatLabel)
    {
        if (methods.Count == 0)
        {
            return null;
        }

        var signatures = methods
            .Select(method => new LspSignatureInformation(
                formatLabel(method),
                Documentation: null,
                Parameters: method.GetParameters()
                    .Select(parameter => new LspParameterInformation(
                        ClrMetadataFormatting.FormatParameter(parameter)))
                    .ToArray()))
            .ToArray();

        var activeSignature = methods
            .Select((method, index) => new
            {
                index,
                parameterCount = method.GetParameters().Length,
                preferred = method.GetParameters().Length > activeParameter ? 0 : 1,
                delta = Math.Abs(method.GetParameters().Length - Math.Max(1, activeParameter + 1)),
            })
            .OrderBy(candidate => candidate.preferred)
            .ThenBy(candidate => candidate.delta)
            .ThenBy(candidate => candidate.parameterCount)
            .Select(candidate => candidate.index)
            .First();

        var activeMethodParameterCount = methods[activeSignature].GetParameters().Length;
        var boundedActiveParameter = activeMethodParameterCount == 0
            ? 0
            : Math.Min(activeParameter, activeMethodParameterCount - 1);

        return new LspSignatureHelp(signatures, activeSignature, boundedActiveParameter);
    }

    private static LspSignatureHelp CreateShellSignatureHelp(
        IReadOnlyList<int> parameterCounts,
        IReadOnlyList<LspSignatureInformation> signatures,
        int activeParameter)
    {
        var activeSignature = parameterCounts
            .Select((parameterCount, index) => new
            {
                index,
                parameterCount,
                preferred = parameterCount > activeParameter ? 0 : 1,
                delta = Math.Abs(parameterCount - Math.Max(1, activeParameter + 1)),
            })
            .OrderBy(candidate => candidate.preferred)
            .ThenBy(candidate => candidate.delta)
            .ThenBy(candidate => candidate.parameterCount)
            .Select(candidate => candidate.index)
            .First();

        var activeMethodParameterCount = parameterCounts[activeSignature];
        var boundedActiveParameter = activeMethodParameterCount == 0
            ? 0
            : Math.Min(activeParameter, activeMethodParameterCount - 1);

        return new LspSignatureHelp(signatures, activeSignature, boundedActiveParameter);
    }

    private static string FormatShellParameter(FunctionParameterSyntax parameter)
    {
        var suffix = parameter.IsOptional ? "?" : string.Empty;
        var rest = parameter.IsRest ? "..." : string.Empty;
        return string.IsNullOrWhiteSpace(parameter.TypeName)
            ? $"{parameter.Name}{suffix}{rest}"
            : $"{parameter.Name}{suffix}{rest}: {parameter.TypeName}";
    }

    private static CallSite? FindCallSite(StatementSyntax statement, string text, int offset)
    {
        var candidates = new List<CallSite>();
        CollectCallSites(statement, text, offset, candidates);
        return candidates
            .OrderBy(candidate => candidate.Argument.Span.Length)
            .FirstOrDefault();
    }

    private static CommandCallSite? FindCommandCallSite(StatementSyntax statement, string text, int offset)
    {
        var candidates = new List<CommandCallSite>();
        CollectCommandCallSites(statement, text, offset, candidates);
        return candidates
            .OrderBy(candidate => candidate.Command.Span.Length)
            .FirstOrDefault();
    }

    private static void CollectCallSites(StatementSyntax statement, string text, int offset, ICollection<CallSite> matches)
    {
        switch (statement)
        {
            case ScriptStatementSyntax script:
                foreach (var child in script.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case PipelineStatementSyntax pipelineStatement:
                CollectCallSites(pipelineStatement.Pipeline, text, offset, matches);
                break;

            case VariableDeclarationStatementSyntax variable when variable.Value is not null:
                CollectCallSites(variable.Value, text, offset, matches);
                break;

            case VariableAssignmentStatementSyntax assignment:
                CollectCallSites(assignment.Value, text, offset, matches);
                break;

            case MemberAssignmentStatementSyntax assignment:
                CollectCallSites(assignment.Target, text, offset, matches);
                CollectCallSites(assignment.Value, text, offset, matches);
                break;

            case ReturnStatementSyntax @return when @return.Value is not null:
                CollectCallSites(@return.Value, text, offset, matches);
                break;

            case ThrowStatementSyntax @throw when @throw.Value is not null:
                CollectCallSites(@throw.Value, text, offset, matches);
                break;

            case FunctionDefinitionStatementSyntax function:
                foreach (var child in function.Body.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case IfStatementSyntax @if:
                CollectCallSites(@if.Condition, text, offset, matches);
                foreach (var child in @if.ThenBlock.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }

                if (@if.ElseBlock is not null)
                {
                    foreach (var child in @if.ElseBlock.Statements)
                    {
                        CollectCallSites(child, text, offset, matches);
                    }
                }
                break;

            case ForStatementSyntax @for:
                CollectCallSites(@for.Source, text, offset, matches);
                foreach (var child in @for.Body.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case WhileStatementSyntax @while:
                CollectCallSites(@while.Condition, text, offset, matches);
                foreach (var child in @while.Body.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case UntilStatementSyntax until:
                CollectCallSites(until.Condition, text, offset, matches);
                foreach (var child in until.Body.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case TryStatementSyntax @try:
                foreach (var child in @try.TryBlock.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }

                if (@try.CatchClause is not null)
                {
                    foreach (var child in @try.CatchClause.Body.Statements)
                    {
                        CollectCallSites(child, text, offset, matches);
                    }
                }

                if (@try.FinallyBlock is not null)
                {
                    foreach (var child in @try.FinallyBlock.Statements)
                    {
                        CollectCallSites(child, text, offset, matches);
                    }
                }
                break;

            case DeferStatementSyntax @defer:
                foreach (var child in @defer.Body.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case SwitchStatementSyntax @switch:
                CollectCallSites(@switch.Value, text, offset, matches);
                foreach (var @case in @switch.Cases)
                {
                    CollectCallSites(@case.MatchExpression, text, offset, matches);
                    foreach (var child in @case.Body.Statements)
                    {
                        CollectCallSites(child, text, offset, matches);
                    }
                }

                if (@switch.DefaultBlock is not null)
                {
                    foreach (var child in @switch.DefaultBlock.Statements)
                    {
                        CollectCallSites(child, text, offset, matches);
                    }
                }
                break;
        }
    }

    private static void CollectCommandCallSites(StatementSyntax statement, string text, int offset, ICollection<CommandCallSite> matches)
    {
        switch (statement)
        {
            case ScriptStatementSyntax script:
                foreach (var child in script.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case PipelineStatementSyntax pipelineStatement:
                CollectCommandCallSites(pipelineStatement.Pipeline, text, offset, matches);
                break;
            case VariableDeclarationStatementSyntax variable when variable.Value is not null:
                CollectCommandCallSites(variable.Value, text, offset, matches);
                break;
            case VariableAssignmentStatementSyntax assignment:
                CollectCommandCallSites(assignment.Value, text, offset, matches);
                break;
            case MemberAssignmentStatementSyntax assignment:
                CollectCommandCallSites(assignment.Target, text, offset, matches);
                CollectCommandCallSites(assignment.Value, text, offset, matches);
                break;
            case ReturnStatementSyntax @return when @return.Value is not null:
                CollectCommandCallSites(@return.Value, text, offset, matches);
                break;
            case ThrowStatementSyntax @throw when @throw.Value is not null:
                CollectCommandCallSites(@throw.Value, text, offset, matches);
                break;
            case FunctionDefinitionStatementSyntax function:
                foreach (var child in function.Body.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case IfStatementSyntax @if:
                CollectCommandCallSites(@if.Condition, text, offset, matches);
                foreach (var child in @if.ThenBlock.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                if (@if.ElseBlock is not null)
                {
                    foreach (var child in @if.ElseBlock.Statements)
                    {
                        CollectCommandCallSites(child, text, offset, matches);
                    }
                }
                break;
            case ForStatementSyntax @for:
                CollectCommandCallSites(@for.Source, text, offset, matches);
                foreach (var child in @for.Body.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case WhileStatementSyntax @while:
                CollectCommandCallSites(@while.Condition, text, offset, matches);
                foreach (var child in @while.Body.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case UntilStatementSyntax until:
                CollectCommandCallSites(until.Condition, text, offset, matches);
                foreach (var child in until.Body.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case TryStatementSyntax @try:
                foreach (var child in @try.TryBlock.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                if (@try.CatchClause is not null)
                {
                    foreach (var child in @try.CatchClause.Body.Statements)
                    {
                        CollectCommandCallSites(child, text, offset, matches);
                    }
                }
                if (@try.FinallyBlock is not null)
                {
                    foreach (var child in @try.FinallyBlock.Statements)
                    {
                        CollectCommandCallSites(child, text, offset, matches);
                    }
                }
                break;

            case DeferStatementSyntax @defer:
                foreach (var child in @defer.Body.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case SwitchStatementSyntax @switch:
                CollectCommandCallSites(@switch.Value, text, offset, matches);
                foreach (var @case in @switch.Cases)
                {
                    CollectCommandCallSites(@case.MatchExpression, text, offset, matches);
                    foreach (var child in @case.Body.Statements)
                    {
                        CollectCommandCallSites(child, text, offset, matches);
                    }
                }
                if (@switch.DefaultBlock is not null)
                {
                    foreach (var child in @switch.DefaultBlock.Statements)
                    {
                        CollectCommandCallSites(child, text, offset, matches);
                    }
                }
                break;
        }
    }

    private static void CollectCommandCallSites(PipelineSyntax pipeline, string text, int offset, ICollection<CommandCallSite> matches)
    {
        foreach (var stage in pipeline.Stages)
        {
            switch (stage)
            {
                case CommandSyntax command:
                    if (command.NameSpan.Start <= offset && offset <= command.Span.End)
                    {
                        matches.Add(new CommandCallSite(command, GetActiveCommandParameter(command, offset)));
                    }

                    foreach (var argument in command.Arguments)
                    {
                        CollectCommandCallSites(argument, text, offset, matches);
                    }
                    break;
                case ExpressionPipelineStageSyntax expression:
                    CollectCommandCallSites(expression.Expression, text, offset, matches);
                    break;
            }
        }

        if (pipeline.Redirections is null)
        {
            return;
        }

        foreach (var redirection in pipeline.Redirections)
        {
            CollectCommandCallSites(redirection.Target, text, offset, matches);
        }
    }

    private static void CollectCommandCallSites(ArgumentSyntax argument, string text, int offset, ICollection<CommandCallSite> matches)
    {
        if (argument.Span.Start > offset || offset > argument.Span.End)
        {
            return;
        }

        switch (argument)
        {
            case SplatArgumentSyntax splat:
                CollectCommandCallSites(splat.Value, text, offset, matches);
                break;
            case NewObjectArgumentSyntax newObject:
                foreach (var child in newObject.Arguments)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case StaticMethodCallArgumentSyntax staticMethodCall:
                foreach (var child in staticMethodCall.Arguments)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case MethodCallArgumentSyntax methodCall:
                CollectCommandCallSites(methodCall.Target, text, offset, matches);
                foreach (var child in methodCall.Arguments)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case MemberAccessArgumentSyntax member:
                CollectCommandCallSites(member.Target, text, offset, matches);
                break;
            case ArrayLiteralArgumentSyntax list:
                foreach (var item in list.Items)
                {
                    CollectCommandCallSites(item, text, offset, matches);
                }
                break;
            case TupleLiteralArgumentSyntax tuple:
                foreach (var item in tuple.Items)
                {
                    CollectCommandCallSites(item, text, offset, matches);
                }
                break;
            case SetLiteralArgumentSyntax set:
                foreach (var item in set.Items)
                {
                    CollectCommandCallSites(item, text, offset, matches);
                }
                break;
            case ComparisonPatternSyntax comparisonPattern:
                CollectCommandCallSites(comparisonPattern.Operand, text, offset, matches);
                break;
            case RecordLiteralArgumentSyntax record:
                foreach (var entry in record.Fields)
                {
                    switch (entry)
                    {
                        case RecordFieldSyntax field:
                            CollectCommandCallSites(field.Value, text, offset, matches);
                            break;
                        case ComputedRecordFieldSyntax computed:
                            CollectCommandCallSites(computed.NameExpression, text, offset, matches);
                            CollectCommandCallSites(computed.Value, text, offset, matches);
                            break;
                        case SpreadRecordEntrySyntax spread:
                            CollectCommandCallSites(spread.Value, text, offset, matches);
                            break;
                    }
                }
                break;
            case BlockArgumentSyntax blockArgument:
                foreach (var child in blockArgument.Block.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case SubexpressionArgumentSyntax subexpression:
                CollectCommandCallSites(subexpression.Pipeline, text, offset, matches);
                break;
            case OperatorArgumentSyntax operation:
                CollectCommandCallSites(operation.Left, text, offset, matches);
                CollectCommandCallSites(operation.Right, text, offset, matches);
                break;
            case MatchArgumentSyntax match:
                CollectCommandCallSites(match.Value, text, offset, matches);
                foreach (var arm in match.Arms)
                {
                    if (arm.Pattern is not null)
                    {
                        CollectCommandCallSites(arm.Pattern, text, offset, matches);
                    }
                    if (arm.Guard is not null)
                    {
                        CollectCommandCallSites(arm.Guard, text, offset, matches);
                    }
                    switch (arm.Body)
                    {
                        case MatchArmPipelineBodySyntax pipelineBody:
                            CollectCommandCallSites(pipelineBody.Pipeline, text, offset, matches);
                            break;
                        case MatchArmBlockBodySyntax blockBody:
                            foreach (var child in blockBody.Block.Statements)
                            {
                                CollectCommandCallSites(child, text, offset, matches);
                            }
                            break;
                    }
                }
                break;
            case UnaryOperatorArgumentSyntax unary:
                CollectCommandCallSites(unary.Operand, text, offset, matches);
                break;
            case RangeArgumentSyntax range:
                CollectCommandCallSites(range.Start, text, offset, matches);
                if (range.Step is not null)
                {
                    CollectCommandCallSites(range.Step, text, offset, matches);
                }
                if (range.End is not null)
                {
                    CollectCommandCallSites(range.End, text, offset, matches);
                }
                break;
        }
    }

    private static int GetActiveCommandParameter(CommandSyntax command, int offset)
    {
        if (command.Arguments.Count == 0)
        {
            return 0;
        }

        for (var index = 0; index < command.Arguments.Count; index++)
        {
            if (offset <= command.Arguments[index].Span.End)
            {
                return index;
            }
        }

        return Math.Max(0, command.Arguments.Count - 1);
    }

    private static void CollectCallSites(PipelineSyntax pipeline, string text, int offset, ICollection<CallSite> matches)
    {
        foreach (var stage in pipeline.Stages)
        {
            switch (stage)
            {
                case CommandSyntax command:
                    foreach (var argument in command.Arguments)
                    {
                        CollectCallSites(argument, text, offset, matches);
                    }
                    break;

                case ExpressionPipelineStageSyntax expression:
                    CollectCallSites(expression.Expression, text, offset, matches);
                    break;
            }
        }

        if (pipeline.Redirections is null)
        {
            return;
        }

        foreach (var redirection in pipeline.Redirections)
        {
            CollectCallSites(redirection.Target, text, offset, matches);
        }
    }

    private static void CollectCallSites(ArgumentSyntax argument, string text, int offset, ICollection<CallSite> matches)
    {
        if (argument.Span.Start > offset || offset > argument.Span.End)
        {
            return;
        }

        switch (argument)
        {
            case SplatArgumentSyntax splat:
                CollectCallSites(splat.Value, text, offset, matches);
                break;

            case NewObjectArgumentSyntax newObject:
                TryAddCallSite(newObject, FindOpenParenIndex(text, newObject.Span), text, offset, matches);
                foreach (var child in newObject.Arguments)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case StaticMethodCallArgumentSyntax staticMethodCall:
                TryAddCallSite(staticMethodCall, FindOpenParenIndex(text, staticMethodCall.Span), text, offset, matches);
                foreach (var child in staticMethodCall.Arguments)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case MethodCallArgumentSyntax methodCall:
                TryAddCallSite(methodCall, FindMethodCallOpenParenIndex(text, methodCall), text, offset, matches);
                CollectCallSites(methodCall.Target, text, offset, matches);
                foreach (var child in methodCall.Arguments)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case MemberAccessArgumentSyntax member:
                CollectCallSites(member.Target, text, offset, matches);
                break;

            case ArrayLiteralArgumentSyntax list:
                foreach (var item in list.Items)
                {
                    CollectCallSites(item, text, offset, matches);
                }
                break;

            case TupleLiteralArgumentSyntax tuple:
                foreach (var item in tuple.Items)
                {
                    CollectCallSites(item, text, offset, matches);
                }
                break;

            case SetLiteralArgumentSyntax set:
                foreach (var item in set.Items)
                {
                    CollectCallSites(item, text, offset, matches);
                }
                break;

            case ComparisonPatternSyntax comparisonPattern:
                CollectCallSites(comparisonPattern.Operand, text, offset, matches);
                break;

            case RecordLiteralArgumentSyntax record:
                foreach (var entry in record.Fields)
                {
                    if (entry is RecordFieldSyntax field)
                    {
                        CollectCallSites(field.Value, text, offset, matches);
                    }
                    else if (entry is ComputedRecordFieldSyntax computed)
                    {
                        CollectCallSites(computed.NameExpression, text, offset, matches);
                        CollectCallSites(computed.Value, text, offset, matches);
                    }
                    else if (entry is SpreadRecordEntrySyntax spread)
                    {
                        CollectCallSites(spread.Value, text, offset, matches);
                    }
                }
                break;

            case BlockArgumentSyntax blockArgument:
                foreach (var child in blockArgument.Block.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case SubexpressionArgumentSyntax subexpression:
                CollectCallSites(subexpression.Pipeline, text, offset, matches);
                break;

            case OperatorArgumentSyntax operation:
                CollectCallSites(operation.Left, text, offset, matches);
                CollectCallSites(operation.Right, text, offset, matches);
                break;

            case MatchArgumentSyntax match:
                CollectCallSites(match.Value, text, offset, matches);
                foreach (var arm in match.Arms)
                {
                    if (arm.Pattern is not null)
                    {
                        CollectCallSites(arm.Pattern, text, offset, matches);
                    }

                    if (arm.Guard is not null)
                    {
                        CollectCallSites(arm.Guard, text, offset, matches);
                    }

                    switch (arm.Body)
                    {
                        case MatchArmPipelineBodySyntax pipelineBody:
                            CollectCallSites(pipelineBody.Pipeline, text, offset, matches);
                            break;
                        case MatchArmBlockBodySyntax blockBody:
                            foreach (var child in blockBody.Block.Statements)
                            {
                                CollectCallSites(child, text, offset, matches);
                            }
                            break;
                    }
                }
                break;

            case UnaryOperatorArgumentSyntax unary:
                CollectCallSites(unary.Operand, text, offset, matches);
                break;

            case RangeArgumentSyntax range:
                CollectCallSites(range.Start, text, offset, matches);
                if (range.Step is not null)
                {
                    CollectCallSites(range.Step, text, offset, matches);
                }
                if (range.End is not null)
                {
                    CollectCallSites(range.End, text, offset, matches);
                }
                break;
        }
    }

    private static void TryAddCallSite(ArgumentSyntax argument, int openParenIndex, string text, int offset, ICollection<CallSite> matches)
    {
        if (openParenIndex < 0 || offset < openParenIndex || offset > argument.Span.End)
        {
            return;
        }

        matches.Add(new CallSite(argument, openParenIndex, GetActiveParameter(text, openParenIndex, offset)));
    }

    private static int FindOpenParenIndex(string text, TextSpan span)
    {
        var start = Math.Clamp(span.Start, 0, text.Length);
        var length = Math.Clamp(span.End - start, 0, text.Length - start);
        return text.IndexOf('(', start, length);
    }

    private static int FindMethodCallOpenParenIndex(string text, MethodCallArgumentSyntax methodCall)
    {
        var searchStart = Math.Clamp(methodCall.Target.Span.End, 0, text.Length);
        var searchEnd = Math.Clamp(methodCall.Span.End, searchStart, text.Length);
        var segment = text[searchStart..searchEnd];
        var methodIndex = segment.LastIndexOf(methodCall.MethodName, StringComparison.Ordinal);

        while (methodIndex >= 0)
        {
            var openParenIndex = searchStart + methodIndex + methodCall.MethodName.Length;
            var probe = openParenIndex;

            while (probe < text.Length && char.IsWhiteSpace(text[probe]))
            {
                probe++;
            }

            if (probe < searchEnd && text[probe] == '(')
            {
                return probe;
            }

            methodIndex = segment.LastIndexOf(methodCall.MethodName, methodIndex - 1, StringComparison.Ordinal);
        }

        return -1;
    }

    private static int GetActiveParameter(string text, int openParenIndex, int offset)
    {
        var activeParameter = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inString = false;
        var escapeNext = false;

        for (var index = openParenIndex + 1; index < Math.Min(offset, text.Length); index++)
        {
            var ch = text[index];

            if (inString)
            {
                if (escapeNext)
                {
                    escapeNext = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escapeNext = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                    {
                        parenDepth--;
                    }
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                    {
                        bracketDepth--;
                    }
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                    {
                        braceDepth--;
                    }
                    break;
                case ',' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    activeParameter++;
                    break;
            }
        }

        return activeParameter;
    }

    private sealed record CallSite(ArgumentSyntax Argument, int OpenParenIndex, int ActiveParameter);

    private static (string PathText, int Start, int End) ExtractStringLiteralAt(string text, int offset)
    {
        if (string.IsNullOrEmpty(text) || offset < 0 || offset >= text.Length)
            return (string.Empty, offset, offset);

        int start = offset;
        while (start > 0 && text[start - 1] != '"' && text[start - 1] != '\'' && text[start - 1] != '\n')
            start--;

        if (start > 0 && (text[start - 1] == '"' || text[start - 1] == '\''))
        {
            char quote = text[start - 1];
            int openQuote = start - 1;
            int closeQuote = text.IndexOf(quote, start);
            if (closeQuote > openQuote)
            {
                var val = text.Substring(start, closeQuote - start);
                if (val.Contains('/') || val.Contains('.'))
                {
                    return (val, openQuote, closeQuote + 1);
                }
            }
        }
        return (string.Empty, offset, offset);
    }

    private static int FindPipelineOrRedirectOffset(string text, int offset)
    {
        if (string.IsNullOrEmpty(text)) return -1;

        for (int delta = 0; delta <= 3; delta++)
        {
            int p1 = offset + delta;
            if (p1 >= 0 && p1 < text.Length && IsPipelineOrRedirectAt(text, p1))
                return p1;

            int p2 = offset - delta;
            if (p2 >= 0 && p2 < text.Length && IsPipelineOrRedirectAt(text, p2))
                return p2;
        }
        return -1;
    }

    /// <summary>
    /// Whether the character at <paramref name="index"/> really is a pipeline or redirect
    /// operator — <c>TOAST-0109</c>.
    /// </summary>
    /// <remarks>
    /// The search above accepts any <c>|</c> or <c>&gt;</c> within three characters of the
    /// cursor, which caught a great deal that is neither: the <c>|</c> of a record literal's
    /// <c>{|</c> and <c>|}</c>, of a dict's <c>{%</c> pair's siblings, of an or-pattern's
    /// alternatives, of <c>||</c>; and the <c>&gt;</c> of <c>=&gt;</c>, <c>&gt;=</c> and
    /// <c>-&gt;</c>. Hovering a field name in a short typed literal produced a "Pipeline Data
    /// Stream" card about a pipeline that is not there.
    /// </remarks>
    private static bool IsPipelineOrRedirectAt(string text, int index)
    {
        var ch = text[index];

        if (ch == '|')
        {
            // A collection or record delimiter, not a pipe.
            if (index > 0 && text[index - 1] is '{' or '[') { return false; }
            if (index + 1 < text.Length && text[index + 1] is '}' or ']') { return false; }

            // `||` is logical or; `<|` is the comprehension separator.
            if (index > 0 && text[index - 1] is '|' or '<') { return false; }
            if (index + 1 < text.Length && text[index + 1] == '|') { return false; }

            return true;
        }

        if (ch == '>')
        {
            // `=>`, `->` and `>=` are not redirects.
            if (index > 0 && text[index - 1] is '=' or '-') { return false; }
            if (index + 1 < text.Length && text[index + 1] == '=') { return false; }

            return true;
        }

        return false;
    }

    private sealed record CommandCallSite(CommandSyntax Command, int ActiveParameter);

}
