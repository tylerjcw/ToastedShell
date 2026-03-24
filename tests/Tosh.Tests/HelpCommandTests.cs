using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class HelpCommandTests
{
    [Fact]
    public async Task Help_can_describe_commands_language_topics_types_and_externals()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var externalPath = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to resolve the current process path.");

        var aliasTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help alias")));
        var newTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help new")));
        var typeTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help System.String")));
        var externalTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync($"help \"{externalPath}\"")));

        Assert.Equal(HelpSubjectKind.Language, aliasTopic.Kind);
        Assert.Equal("Language", aliasTopic.Category);
        Assert.Equal(HelpSubjectKind.BuiltIn, newTopic.Kind);
        Assert.Contains("C#-style", newTopic.Notes, StringComparison.Ordinal);
        Assert.Equal(HelpSubjectKind.Type, typeTopic.Kind);
        Assert.Contains("System.String", typeTopic.Description, StringComparison.Ordinal);
        Assert.Equal(HelpSubjectKind.External, externalTopic.Kind);
        Assert.Equal(externalPath, externalTopic.Path);
    }

    [Fact]
    public async Task Help_search_related_and_categories_return_structured_results()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var searchResults = await engine.ExecuteToListAsync("help search json");
        var aproposResults = await engine.ExecuteToListAsync("apropos loop");
        var relatedResults = await engine.ExecuteToListAsync("help related alias");
        var categoryResults = await engine.ExecuteToListAsync("help categories");
        var manResults = await engine.ExecuteToListAsync("man ls | get Name");

        Assert.Contains(searchResults, item => Assert.IsType<HelpSearchResult>(item).Name == "from-json");
        Assert.Contains(aproposResults, item => Assert.IsType<HelpSearchResult>(item).Name == "while");
        Assert.Contains(relatedResults, item => Assert.IsType<HelpSearchResult>(item).Name == "def");
        Assert.Contains(categoryResults, item => Assert.IsType<HelpCategoryInfo>(item).Category == "Filesystem");
        Assert.Contains(categoryResults, item => Assert.IsType<HelpCategoryInfo>(item).Category == "Language");
        Assert.Collection(manResults, item => Assert.Equal("ls", item));
    }
}
