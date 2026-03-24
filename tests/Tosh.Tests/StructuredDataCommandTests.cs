using System.Xml.Linq;
using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class StructuredDataCommandTests
{
    [Fact]
    public async Task From_json_parses_root_objects_into_projected_objects()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo \"{\\\"name\\\":\\\"toast\\\",\\\"size\\\":1024}\" | from-json | get { name, size }");

        var projection = Assert.IsType<ProjectedObject>(Assert.Single(results));
        Assert.True(projection.TryGetValue("name", out var name));
        Assert.True(projection.TryGetValue("size", out var size));
        Assert.Equal("toast", name);
        Assert.Equal(1024L, size);
    }

    [Fact]
    public async Task From_json_preserves_array_roots_until_explicitly_expanded()
    {
        var engine = new ToshEngine();

        var typeResults = await engine.ExecuteToListAsync("echo \"[{\\\"name\\\":\\\"alpha\\\"},{\\\"name\\\":\\\"beta\\\"}]\" | from-json | type-of");
        var expandedResults = await engine.ExecuteToListAsync("echo \"[{\\\"name\\\":\\\"alpha\\\"},{\\\"name\\\":\\\"beta\\\"}]\" | from-json | each { $it } | get name");

        Assert.Collection(typeResults, item => Assert.Equal(typeof(object[]), item));
        Assert.Equal(["alpha", "beta"], expandedResults);
    }

    [Fact]
    public async Task From_csv_parses_rows_into_projected_objects()
    {
        var engine = new ToshEngine();

        var typeResults = await engine.ExecuteToListAsync("echo \"name,size\" \"alpha,1\" \"beta,2\" | from-csv | type-of");
        var expandedResults = await engine.ExecuteToListAsync("echo \"name,size\" \"alpha,1\" \"beta,2\" | from-csv | each { $it } | get name");

        Assert.Collection(typeResults, item => Assert.Equal(typeof(ProjectedObject[]), item));
        Assert.Equal(["alpha", "beta"], expandedResults);
    }

    [Fact]
    public async Task From_xml_parses_documents_into_xdocument_values()
    {
        var engine = new ToshEngine();

        var typeResults = await engine.ExecuteToListAsync("echo \"<root><item name=\\\"alpha\\\" /></root>\" | from-xml | type-of");
        var rootNameResults = await engine.ExecuteToListAsync("echo \"<root><item name=\\\"alpha\\\" /></root>\" | from-xml | get \"Root.Name.LocalName\"");

        Assert.Collection(typeResults, item => Assert.Equal(typeof(XDocument), item));
        Assert.Equal(["root"], rootNameResults);
    }

    [Fact]
    public async Task Parse_creates_projected_objects_from_named_regex_groups()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo \"PID=42 Name=tosh\" | parse \"PID=(?<pid>[0-9]+) Name=(?<name>[A-Za-z]+)\"");

        var projection = Assert.IsType<ProjectedObject>(Assert.Single(results));
        Assert.True(projection.TryGetValue("pid", out var pid));
        Assert.True(projection.TryGetValue("name", out var name));
        Assert.Equal("42", pid);
        Assert.Equal("tosh", name);
    }
}
