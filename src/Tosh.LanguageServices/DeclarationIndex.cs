using System.Text.RegularExpressions;
using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.LanguageServices;

public sealed class DeclarationIndex
{
    private readonly string _sourceName;
    private readonly string _text;
    private readonly TextCoordinateMap _map;
    private readonly IReadOnlyList<IndexedDeclaration> _declarations;
    private readonly IReadOnlyList<IndexedFunctionDeclaration> _functions;

    private DeclarationIndex(
        string sourceName,
        string text,
        TextCoordinateMap map,
        IReadOnlyList<IndexedDeclaration> declarations,
        IReadOnlyList<IndexedFunctionDeclaration> functions)
    {
        _sourceName = sourceName;
        _text = text;
        _map = map;
        _declarations = declarations;
        _functions = functions;
    }

    public static DeclarationIndex Create(string sourceName, string text)
    {
        var parseResult = ToshParser.Parse(text, sourceName);
        var map = new TextCoordinateMap(parseResult.SourceText);
        var collector = new Collector(parseResult.SourceText, map);
        collector.Collect(parseResult.Statement);
        return new DeclarationIndex(
            sourceName,
            parseResult.SourceText,
            map,
            collector.BuildDeclarations(),
            collector.BuildFunctions());
    }

    public IReadOnlyList<string> GetVisibleVariables(int offset)
    {
        return GetVisibleDeclarations(offset, declaration => declaration.Kind is DeclarationKind.Variable or DeclarationKind.Parameter or DeclarationKind.LoopVariable or DeclarationKind.CatchVariable)
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetVisibleFunctions(int offset)
    {
        return GetVisibleDeclarations(offset, declaration => declaration.Kind == DeclarationKind.Function)
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<IndexedFunctionDeclaration> GetVisibleFunctionOverloads(int offset, string name)
    {
        return _functions
            .Where(function =>
                string.Equals(function.Name, name, StringComparison.Ordinal) &&
                function.ScopeStart <= offset &&
                offset <= function.ScopeEnd &&
                function.SelectionStart <= offset)
            .OrderByDescending(function => function.ScopeDepth)
            .ThenByDescending(function => function.SelectionStart)
            .ToArray();
    }

    public IReadOnlyList<string> GetVisibleClasses(int offset)
    {
        return GetVisibleDeclarations(offset, declaration => declaration.Kind == DeclarationKind.Class)
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetVisibleTypeLikeSymbols(int offset)
    {
        return GetVisibleDeclarations(
                offset,
                declaration => declaration.Kind is DeclarationKind.Class or DeclarationKind.Record or DeclarationKind.Enum)
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetVisibleModules(int offset)
    {
        return GetVisibleDeclarations(offset, declaration => declaration.Kind == DeclarationKind.Module)
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public DocComment? GetDeclarationDocComment(int offset, string name)
    {
        return GetVisibleDeclarations(
                offset,
                declaration => declaration.Kind is DeclarationKind.Class or DeclarationKind.Record or DeclarationKind.Enum or DeclarationKind.Module
                    && string.Equals(declaration.Name, name, StringComparison.Ordinal))
            .Select(declaration => declaration.DocComment)
            .FirstOrDefault(doc => doc is not null);
    }

    public string? GetDeclarationKindLabel(int offset, string name)
    {
        return GetVisibleDeclarations(
                offset,
                declaration => declaration.Kind is DeclarationKind.Class or DeclarationKind.Record or DeclarationKind.Enum or DeclarationKind.Module
                    && string.Equals(declaration.Name, name, StringComparison.Ordinal))
            .Select(declaration => declaration.Kind switch
            {
                DeclarationKind.Class => "Class",
                DeclarationKind.Record => "Record",
                DeclarationKind.Enum => "Enum",
                DeclarationKind.Module => "Module",
                _ => null,
            })
            .FirstOrDefault();
    }

    public IReadOnlyList<IndexedSymbol> GetSymbols()
    {
        var symbols = _declarations
            .Where(declaration => declaration.Kind is DeclarationKind.Variable or DeclarationKind.Class or DeclarationKind.Module or DeclarationKind.Enum or DeclarationKind.Record)
            .OrderBy(declaration => declaration.SelectionStart)
            .Select(declaration => new IndexedSymbol(
                declaration.Name,
                declaration.Kind switch
                {
                    DeclarationKind.Class => "class",
                    DeclarationKind.Module => "module",
                    DeclarationKind.Enum => "enum",
                    DeclarationKind.Record => "record",
                    _ => "var",
                },
                declaration.Kind switch
                {
                    DeclarationKind.Class => 5,
                    DeclarationKind.Module => 2,
                    DeclarationKind.Enum => 10,
                    DeclarationKind.Record => 23,
                    _ => 13,
                },
                declaration.Range,
                declaration.SelectionRange))
            .ToList();

        symbols.AddRange(
            _functions
                .GroupBy(function => new { function.Name, function.ScopeStart, function.ScopeEnd, function.ScopeDepth })
                .Select(group =>
                {
                    var ordered = group.OrderBy(function => function.SelectionStart).ToArray();
                    var first = ordered[0];
                    return new IndexedSymbol(
                        first.Name,
                        ordered.Length == 1 ? "func" : $"func ({ordered.Length} overloads)",
                        12,
                        first.Range,
                        first.SelectionRange);
                }));

        return symbols
            .OrderBy(symbol => symbol.SelectionRange.Start.Line)
            .ThenBy(symbol => symbol.SelectionRange.Start.Character)
            .ToArray();
    }

    public IReadOnlyList<LspLocation> FindDefinitions(LspPosition position)
    {
        var offset = _map.ToOffset(position);
        var token = FindWordAt(offset);

        if (string.IsNullOrWhiteSpace(token.Word))
        {
            return Array.Empty<LspLocation>();
        }

        if (TryGetVariableReferenceName(token.Word, out var variableName))
        {
            var variableDeclaration = ResolveVisibleDeclaration(offset, variableName, entry =>
                entry.Kind is DeclarationKind.Variable or DeclarationKind.Parameter or DeclarationKind.LoopVariable or DeclarationKind.CatchVariable);

            return variableDeclaration is null
                ? Array.Empty<LspLocation>()
                : [new LspLocation(_sourceName, variableDeclaration.SelectionRange)];
        }

        if (token.Word.Contains('.', StringComparison.Ordinal))
        {
            return Array.Empty<LspLocation>();
        }

        var functions = GetVisibleFunctionOverloads(offset, token.Word);
        if (functions.Count > 0)
        {
            return functions
                .Select(function => new LspLocation(_sourceName, function.SelectionRange))
                .ToArray();
        }

        var declaration = ResolveVisibleDeclaration(
            offset,
            token.Word,
            entry => entry.Kind is DeclarationKind.Function or DeclarationKind.Class or DeclarationKind.Module or DeclarationKind.Enum or DeclarationKind.Record,
            requireDeclarationBeforeUse: false);
        return declaration is null
            ? Array.Empty<LspLocation>()
            : [new LspLocation(_sourceName, declaration.SelectionRange)];
    }

    private IReadOnlyList<IndexedDeclaration> GetVisibleDeclarations(int offset, Func<IndexedDeclaration, bool> predicate)
    {
        return _declarations
            .Where(declaration =>
                predicate(declaration) &&
                declaration.ScopeStart <= offset &&
                offset <= declaration.ScopeEnd &&
                IsDeclarationVisibleAtOffset(declaration, offset))
            .OrderByDescending(declaration => declaration.ScopeDepth)
            .ThenByDescending(declaration => declaration.SelectionStart)
            .ToArray();
    }

    private IndexedDeclaration? ResolveVisibleDeclaration(
        int offset,
        string name,
        Func<IndexedDeclaration, bool> predicate,
        bool requireDeclarationBeforeUse = true)
    {
        return _declarations
            .Where(declaration =>
                string.Equals(declaration.Name, name, StringComparison.Ordinal) &&
                predicate(declaration) &&
                declaration.ScopeStart <= offset &&
                offset <= declaration.ScopeEnd &&
                (!requireDeclarationBeforeUse || IsDeclarationVisibleAtOffset(declaration, offset)))
            .OrderByDescending(declaration => declaration.ScopeDepth)
            .ThenByDescending(declaration => declaration.SelectionStart)
            .FirstOrDefault();
    }

    private static bool IsDeclarationVisibleAtOffset(IndexedDeclaration declaration, int offset)
    {
        return declaration.Kind == DeclarationKind.Function || declaration.SelectionStart <= offset;
    }

    private static bool TryGetVariableReferenceName(string word, out string name)
    {
        name = string.Empty;

        if (!word.StartsWith('$'))
        {
            return false;
        }

        var trimmed = word[1..];
        var dotIndex = trimmed.IndexOf('.');
        name = dotIndex >= 0 ? trimmed[..dotIndex] : trimmed;
        return name.Length > 0;
    }

    private (string Word, int Start, int End) FindWordAt(int offset)
    {
        if (string.IsNullOrEmpty(_text))
        {
            return (string.Empty, 0, 0);
        }

        var index = Math.Clamp(offset, 0, Math.Max(0, _text.Length - 1));

        bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch is '$' or '_' or '-' or '.';

        if (!IsWordChar(_text[index]) && index > 0 && IsWordChar(_text[index - 1]))
        {
            index--;
        }

        if (!IsWordChar(_text[index]))
        {
            return (string.Empty, index, index);
        }

        var start = index;
        while (start > 0 && IsWordChar(_text[start - 1]))
        {
            start--;
        }

        var end = index + 1;
        while (end < _text.Length && IsWordChar(_text[end]))
        {
            end++;
        }

        return (_text[start..end], start, end);
    }

    private enum DeclarationKind
    {
        Variable,
        Parameter,
        LoopVariable,
        CatchVariable,
        Function,
        Class,
        Module,
        Enum,
        Record,
    }

    public sealed record IndexedSymbol(
        string Name,
        string Detail,
        int SymbolKind,
        LspRange Range,
        LspRange SelectionRange);

    public sealed record IndexedFunctionDeclaration(
        string Name,
        IReadOnlyList<FunctionParameterSyntax> Parameters,
        string? ReturnTypeName,
        bool IsCommandWrapper,
        int ScopeStart,
        int ScopeEnd,
        int ScopeDepth,
        int SelectionStart,
        LspRange Range,
        LspRange SelectionRange,
        DocComment? DocComment = null);

    private sealed record IndexedDeclaration(
        string Name,
        DeclarationKind Kind,
        int ScopeStart,
        int ScopeEnd,
        int ScopeDepth,
        int SelectionStart,
        LspRange Range,
        LspRange SelectionRange,
        DocComment? DocComment = null);

    private sealed class Collector
    {
        private readonly string _text;
        private readonly TextCoordinateMap _map;
        private readonly List<IndexedDeclaration> _declarations = new();
        private readonly List<IndexedFunctionDeclaration> _functions = new();

        public Collector(string text, TextCoordinateMap map)
        {
            _text = text;
            _map = map;
        }

        public void Collect(StatementSyntax statement)
        {
            var rootScope = TextSpan.FromBounds(0, _text.Length);
            CollectStatement(statement, rootScope, depth: 0);
        }

        public IReadOnlyList<IndexedDeclaration> BuildDeclarations() => _declarations;

        public IReadOnlyList<IndexedFunctionDeclaration> BuildFunctions() => _functions;

        private void CollectStatement(StatementSyntax statement, TextSpan scopeSpan, int depth)
        {
            switch (statement)
            {
                case ScriptStatementSyntax script:
                    foreach (var child in script.Statements)
                    {
                        CollectStatement(child, scopeSpan, depth);
                    }
                    break;

                case PipelineStatementSyntax pipelineStatement:
                    CollectPipeline(pipelineStatement.Pipeline, scopeSpan, depth);
                    break;

                case VariableDeclarationStatementSyntax variable:
                    AddDeclaration(variable.Name, DeclarationKind.Variable, variable.Span, scopeSpan, depth);
                    if (variable.Value is not null)
                    {
                        CollectPipeline(variable.Value, scopeSpan, depth);
                    }
                    break;

                case VariableAssignmentStatementSyntax assignment:
                    CollectPipeline(assignment.Value, scopeSpan, depth);
                    break;

                case MemberAssignmentStatementSyntax assignment:
                    CollectArgument(assignment.Target, scopeSpan, depth);
                    CollectPipeline(assignment.Value, scopeSpan, depth);
                    break;

                case ReturnStatementSyntax @return when @return.Value is not null:
                    CollectPipeline(@return.Value, scopeSpan, depth);
                    break;

                case ThrowStatementSyntax @throw when @throw.Value is not null:
                    CollectPipeline(@throw.Value, scopeSpan, depth);
                    break;

                case FunctionDefinitionStatementSyntax function:
                    AddDeclaration(function.Name, DeclarationKind.Function, function.Span, scopeSpan, depth);
                    AddFunctionDeclaration(function, scopeSpan, depth);
                    var functionScope = function.Body.Span;
                    foreach (var parameter in function.Parameters)
                    {
                        AddDeclaration(parameter.Name, DeclarationKind.Parameter, parameter.Span, functionScope, depth + 1);
                    }

                    foreach (var child in function.Body.Statements)
                    {
                        CollectStatement(child, functionScope, depth + 1);
                    }
                    break;

                case ClassDefinitionStatementSyntax @class:
                    AddDeclaration(@class.Name, DeclarationKind.Class, @class.Span, scopeSpan, depth, @class.DocComment);
                    break;

                case ModuleDefinitionStatementSyntax module:
                    AddDeclaration(module.Name, DeclarationKind.Module, module.Span, scopeSpan, depth, module.DocComment);
                    CollectBlock(module.Body, depth + 1);
                    break;

                case EnumDefinitionStatementSyntax @enum:
                    AddDeclaration(@enum.Name, DeclarationKind.Enum, @enum.Span, scopeSpan, depth, @enum.DocComment);
                    foreach (var member in @enum.Members)
                    {
                        if (member.Value is not null)
                        {
                            CollectPipeline(member.Value, scopeSpan, depth);
                        }
                    }
                    break;

                case RecordDefinitionStatementSyntax record:
                    AddDeclaration(record.Name, DeclarationKind.Record, record.Span, scopeSpan, depth, record.DocComment);
                    foreach (var field in record.Fields)
                    {
                        if (field.DefaultValue is not null)
                        {
                            CollectPipeline(field.DefaultValue, scopeSpan, depth);
                        }
                    }
                    break;

                case IfStatementSyntax @if:
                    CollectArgument(@if.Condition, scopeSpan, depth);
                    CollectBlock(@if.ThenBlock, depth + 1);
                    if (@if.ElseBlock is not null)
                    {
                        CollectBlock(@if.ElseBlock, depth + 1);
                    }
                    break;

                case ForStatementSyntax @for:
                    CollectPipeline(@for.Source, scopeSpan, depth);
                    AddDeclaration(@for.VariableName, DeclarationKind.LoopVariable, @for.Span, @for.Body.Span, depth + 1);
                    CollectBlock(@for.Body, depth + 1);
                    break;

                case WhileStatementSyntax @while:
                    CollectArgument(@while.Condition, scopeSpan, depth);
                    CollectBlock(@while.Body, depth + 1);
                    break;

                case UntilStatementSyntax until:
                    CollectArgument(until.Condition, scopeSpan, depth);
                    CollectBlock(until.Body, depth + 1);
                    break;

                case TryStatementSyntax @try:
                    CollectBlock(@try.TryBlock, depth + 1);
                    if (@try.CatchClause is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(@try.CatchClause.VariableName))
                        {
                            AddDeclaration(@try.CatchClause.VariableName!, DeclarationKind.CatchVariable, @try.CatchClause.Span, @try.CatchClause.Body.Span, depth + 1);
                        }

                        CollectBlock(@try.CatchClause.Body, depth + 1);
                    }

                    if (@try.FinallyBlock is not null)
                    {
                        CollectBlock(@try.FinallyBlock, depth + 1);
                    }
                    break;

                case DeferStatementSyntax @defer:
                    CollectBlock(@defer.Body, depth + 1);
                    break;

                case SwitchStatementSyntax @switch:
                    CollectArgument(@switch.Value, scopeSpan, depth);
                    foreach (var @case in @switch.Cases)
                    {
                        CollectArgument(@case.MatchExpression, scopeSpan, depth);
                        CollectBlock(@case.Body, depth + 1);
                    }

                    if (@switch.DefaultBlock is not null)
                    {
                        CollectBlock(@switch.DefaultBlock, depth + 1);
                    }
                    break;
            }
        }

        private void CollectBlock(BlockSyntax block, int depth)
        {
            foreach (var statement in block.Statements)
            {
                CollectStatement(statement, block.Span, depth);
            }
        }

        private void CollectPipeline(PipelineSyntax pipeline, TextSpan scopeSpan, int depth)
        {
            foreach (var stage in pipeline.Stages)
            {
                switch (stage)
                {
                    case CommandSyntax command:
                        foreach (var argument in command.Arguments)
                        {
                            CollectArgument(argument, scopeSpan, depth);
                        }
                        break;

                    case ExpressionPipelineStageSyntax expression:
                        CollectArgument(expression.Expression, scopeSpan, depth);
                        break;
                }
            }

            if (pipeline.Redirections is not null)
            {
                foreach (var redirection in pipeline.Redirections)
                {
                    CollectArgument(redirection.Target, scopeSpan, depth);
                }
            }
        }

        private void CollectArgument(ArgumentSyntax argument, TextSpan scopeSpan, int depth)
        {
            switch (argument)
            {
                case SplatArgumentSyntax splat:
                    CollectArgument(splat.Value, scopeSpan, depth);
                    break;

                case NewObjectArgumentSyntax newObject:
                    foreach (var child in newObject.Arguments)
                    {
                        CollectArgument(child, scopeSpan, depth);
                    }
                    break;

                case StaticMethodCallArgumentSyntax staticCall:
                    foreach (var child in staticCall.Arguments)
                    {
                        CollectArgument(child, scopeSpan, depth);
                    }
                    break;

                case ArrayLiteralArgumentSyntax list:
                    foreach (var item in list.Items)
                    {
                        CollectArgument(item, scopeSpan, depth);
                    }
                    break;

                case TupleLiteralArgumentSyntax tuple:
                    foreach (var item in tuple.Items)
                    {
                        CollectArgument(item, scopeSpan, depth);
                    }
                    break;

                case SetLiteralArgumentSyntax set:
                    foreach (var item in set.Items)
                    {
                        CollectArgument(item, scopeSpan, depth);
                    }
                    break;

                case RecordLiteralArgumentSyntax record:
                    foreach (var entry in record.Fields)
                    {
                        if (entry is RecordFieldSyntax field)
                        {
                            CollectArgument(field.Value, scopeSpan, depth);
                        }
                        else if (entry is ComputedRecordFieldSyntax computed)
                        {
                            CollectArgument(computed.NameExpression, scopeSpan, depth);
                            CollectArgument(computed.Value, scopeSpan, depth);
                        }
                        else if (entry is SpreadRecordEntrySyntax spread)
                        {
                            CollectArgument(spread.Value, scopeSpan, depth);
                        }
                    }
                    break;

                case BlockArgumentSyntax blockArgument:
                    CollectBlock(blockArgument.Block, depth + 1);
                    break;

                case MemberAccessArgumentSyntax member:
                    CollectArgument(member.Target, scopeSpan, depth);
                    break;

                case MethodCallArgumentSyntax method:
                    CollectArgument(method.Target, scopeSpan, depth);
                    foreach (var child in method.Arguments)
                    {
                        CollectArgument(child, scopeSpan, depth);
                    }
                    break;

                case SubexpressionArgumentSyntax subexpression:
                    CollectPipeline(subexpression.Pipeline, scopeSpan, depth);
                    break;

                case OperatorArgumentSyntax operation:
                    CollectArgument(operation.Left, scopeSpan, depth);
                    CollectArgument(operation.Right, scopeSpan, depth);
                    break;

                case UnaryOperatorArgumentSyntax unary:
                    CollectArgument(unary.Operand, scopeSpan, depth);
                    break;

                case RangeArgumentSyntax range:
                    CollectArgument(range.Start, scopeSpan, depth);
                    if (range.Step is not null)
                    {
                        CollectArgument(range.Step, scopeSpan, depth);
                    }
                    CollectArgument(range.End, scopeSpan, depth);
                    break;
            }
        }

        private void AddDeclaration(string name, DeclarationKind kind, TextSpan declarationSpan, TextSpan scopeSpan, int depth, DocComment? docComment = null)
        {
            var selectionSpan = FindIdentifierSpan(name, declarationSpan);
            _declarations.Add(new IndexedDeclaration(
                name,
                kind,
                scopeSpan.Start,
                scopeSpan.End,
                depth,
                selectionSpan.Start,
                _map.ToRange(declarationSpan.Start, declarationSpan.End),
                _map.ToRange(selectionSpan.Start, selectionSpan.End),
                docComment));
        }

        private void AddFunctionDeclaration(FunctionDefinitionStatementSyntax function, TextSpan scopeSpan, int depth)
        {
            var selectionSpan = FindIdentifierSpan(function.Name, function.Span);
            _functions.Add(new IndexedFunctionDeclaration(
                function.Name,
                function.Parameters,
                function.ReturnTypeName,
                function.IsCommandWrapper,
                scopeSpan.Start,
                scopeSpan.End,
                depth,
                selectionSpan.Start,
                _map.ToRange(function.Span.Start, function.Span.End),
                _map.ToRange(selectionSpan.Start, selectionSpan.End),
                function.DocComment));
        }

        private TextSpan FindIdentifierSpan(string name, TextSpan searchSpan)
        {
            if (searchSpan.IsEmpty || searchSpan.Start >= _text.Length)
            {
                return searchSpan;
            }

            var boundedStart = Math.Clamp(searchSpan.Start, 0, _text.Length);
            var boundedEnd = Math.Clamp(searchSpan.End, boundedStart, _text.Length);
            var length = boundedEnd - boundedStart;
            var segment = _text.Substring(boundedStart, length);
            var match = Regex.Match(segment, $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])");

            return match.Success
                ? TextSpan.FromBounds(boundedStart + match.Index, boundedStart + match.Index + match.Length)
                : searchSpan;
        }
    }
}
