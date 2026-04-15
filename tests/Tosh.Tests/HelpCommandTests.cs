using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class HelpCommandTests(ToshRuntimeFixture fixture) : IClassFixture<ToshRuntimeFixture>
{
    [Fact]
    public async Task Help_can_describe_commands_language_topics_types_and_externals()
    {
        var engine = fixture.Engine;
        var externalPath = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to resolve the current process path.");

        var functionTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help func")));
        var allocTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help alloc")));
        var ipTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help ip")));
        var newTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help new")));
        var ternaryTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help ternary")));
        var matchExprTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help match-expr")));
        var typeTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help System.String")));
        var regexTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help regex")));
        var listTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help list")));
        var mapTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help map")));
        var dictTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help dict")));
        var genericListTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help list<int>")));
        var externalTopic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync($"help \"{externalPath}\"")));

        Assert.Equal(HelpSubjectKind.Language, functionTopic.Kind);
        Assert.Equal("Language", functionTopic.Category);
        Assert.Equal(HelpSubjectKind.BuiltIn, allocTopic.Kind);
        Assert.Equal(HelpSubjectKind.BuiltIn, ipTopic.Kind);
        Assert.Equal("Network", ipTopic.Category);
        Assert.Equal(HelpSubjectKind.Language, newTopic.Kind);
        Assert.Contains("requires `new`", newTopic.Notes, StringComparison.Ordinal);
        Assert.Equal(HelpSubjectKind.Language, ternaryTopic.Kind);
        Assert.Contains("?", ternaryTopic.Usage, StringComparison.Ordinal);
        Assert.Equal(HelpSubjectKind.Language, matchExprTopic.Kind);
        Assert.Contains("default", matchExprTopic.Usage, StringComparison.Ordinal);
        Assert.Equal(HelpSubjectKind.Type, typeTopic.Kind);
        Assert.Contains("System.String", typeTopic.Description, StringComparison.Ordinal);
        Assert.Equal(HelpSubjectKind.Type, regexTopic.Kind);
        Assert.Contains("System.Text.RegularExpressions.Regex", regexTopic.Description, StringComparison.Ordinal);
        Assert.Equal(HelpSubjectKind.Type, listTopic.Kind);
        Assert.Equal("Shell Types", listTopic.Category);
        Assert.Equal(HelpSubjectKind.BuiltIn, mapTopic.Kind);
        Assert.Equal("map", mapTopic.Name);
        Assert.Equal("Pipeline", mapTopic.Category);
        Assert.Equal(HelpSubjectKind.Type, dictTopic.Kind);
        Assert.Contains("map", dictTopic.Aliases, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(HelpSubjectKind.Type, genericListTopic.Kind);
        Assert.Equal("list<int>", genericListTopic.Name);
        Assert.Equal(HelpSubjectKind.External, externalTopic.Kind);
        Assert.Equal(externalPath, externalTopic.Path);
    }

    [Fact]
    public async Task Help_search_related_and_categories_return_structured_results()
    {
        var engine = fixture.Engine;

        var searchResults = await engine.ExecuteToListAsync("help search json");
        var aproposResults = await engine.ExecuteToListAsync("apropos loop");
        var relatedResults = await engine.ExecuteToListAsync("help related func");
        var categoryResults = await engine.ExecuteToListAsync("help categories");
        var helpResults = await engine.ExecuteToListAsync("help ls | get Name");

        Assert.Contains(searchResults, item => Assert.IsType<HelpSearchResult>(item).Name == "from");
        Assert.Contains(aproposResults, item => Assert.IsType<HelpSearchResult>(item).Name == "while");
        Assert.Contains(relatedResults, item => Assert.IsType<HelpSearchResult>(item).Name == "return");
        Assert.Contains(categoryResults, item => Assert.IsType<HelpCategoryInfo>(item).Category == "Filesystem");
        Assert.Contains(categoryResults, item => Assert.IsType<HelpCategoryInfo>(item).Category == "Language");
        Assert.Collection(helpResults, item => Assert.Equal("ls", item));
    }

    [Fact]
    public async Task Help_and_apropos_accept_pipeline_input()
    {
        var engine = fixture.Engine;

        var helpResults = await engine.ExecuteToListAsync("echo list | help | get Name");
        var searchResults = await engine.ExecuteToListAsync("echo json | help search | get Name");
        var relatedResults = await engine.ExecuteToListAsync("echo func | help related | get Name");
        var aproposResults = await engine.ExecuteToListAsync("echo loop | apropos | get Name");

        Assert.Collection(helpResults, item => Assert.Equal("list", item));
        Assert.Contains("from", searchResults.Cast<string>());
        Assert.Contains("return", relatedResults.Cast<string>());
        Assert.Contains("while", aproposResults.Cast<string>());
    }

    [Fact]
    public async Task Help_can_request_the_interactive_browser()
    {
        var engine = fixture.Engine;

        var results = await engine.ExecuteToListAsync("help browse regex");

        var request = Assert.IsType<HelpBrowseRequest>(Assert.Single(results));
        Assert.Equal("regex", request.InitialQuery);
    }
}
