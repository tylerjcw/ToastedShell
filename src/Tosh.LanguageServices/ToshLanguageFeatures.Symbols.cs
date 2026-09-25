using System.Reflection;
using Tosh.Runtime;
using Tosh.Runtime.Units;
using Tosh.Language.Parsing;

namespace Tosh.LanguageServices;

public sealed partial class ToshLanguageFeatures
{
    public IReadOnlyList<LspLocation> GetDefinitions(string text, string sourceName, LspPosition position)
    {
        return DeclarationIndex.Create(sourceName, text).FindDefinitions(position);
    }

    public IReadOnlyList<LspDocumentSymbol> GetDocumentSymbols(string text, string sourceName)
    {
        return DeclarationIndex.Create(sourceName, text)
            .GetSymbols()
            .Select(ConvertDocumentSymbol)
            .ToArray();
    }

    private static LspDocumentSymbol ConvertDocumentSymbol(DeclarationIndex.IndexedSymbol symbol)
    {
        var children = symbol.Children != null && symbol.Children.Count > 0
            ? symbol.Children.Select(ConvertDocumentSymbol).ToArray()
            : Array.Empty<LspDocumentSymbol>();

        var docText = symbol.DocComment?.Summary;

        return new LspDocumentSymbol(
            symbol.Name,
            symbol.Detail,
            symbol.SymbolKind,
            symbol.Range,
            symbol.SelectionRange,
            children,
            DocComment: docText);
    }

    public IReadOnlyList<LspSymbolInformation> GetSymbolInformations(string text, string sourceName)
    {
        return DeclarationIndex.Create(sourceName, text)
            .GetSymbols()
            .Select(symbol => new LspSymbolInformation(
                symbol.Name,
                symbol.SymbolKind,
                new LspLocation(sourceName, symbol.Range),
                symbol.Detail))
            .ToArray();
    }

    public static readonly IReadOnlyList<string> SemanticTokenTypes =
    [
        "comment",    // 0
        "keyword",    // 1
        "string",     // 2
        "number",     // 3
        "variable",   // 4
        "function",   // 5
        "type",       // 6
        "operator",   // 7
    ];

    public static readonly IReadOnlyList<string> SemanticTokenModifiers =
    [
        "declaration",     // bit 0
        "defaultLibrary",  // bit 1
        "documentation",   // bit 2
    ];


}
