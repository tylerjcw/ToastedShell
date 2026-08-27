using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class IntrospectionAndPickingTests
{
    [Fact]
    public async Task Row_picks_single_index()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("echo \"[10,20,30,40,50]\" | from json | flatten | row 2");

        Assert.Collection(results, item => Assert.Equal(30L, item));
    }

    [Fact]
    public async Task Row_variadic_picks_in_order()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("echo \"[10,20,30,40,50]\" | from json | flatten | row 4 0 2");

        Assert.Equal([50L, 10L, 30L], results.Cast<long>().ToArray());
    }

    [Fact]
    public async Task Row_list_literal_picks_in_order()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("echo \"[10,20,30,40,50]\" | from json | flatten | row [3,1,0]");

        Assert.Equal([40L, 20L, 10L], results.Cast<long>().ToArray());
    }

    [Fact]
    public async Task Row_range_picks_contiguous_slice()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("echo \"[10,20,30,40,50]\" | from json | flatten | row 1..3");

        Assert.Equal([20L, 30L, 40L], results.Cast<long>().ToArray());
    }

    [Fact]
    public async Task Row_out_of_range_throws_diagnostic()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
            await engine.ExecuteToListAsync("echo \"[1,2,3]\" | from json | flatten | row 99"));
    }

    [Fact]
    public async Task Get_variadic_projects_multiple_fields()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        const string json = "[{\"name\":\"a\",\"size\":1,\"extra\":\"x\"},{\"name\":\"b\",\"size\":2,\"extra\":\"y\"}]";
        var escapedJson = "\"" + json.Replace("\"", "\\\"") + "\"";

        var results = await engine.ExecuteToListAsync($"echo {escapedJson} | from json | flatten | get name size");

        // Each item should be a record with only Name and Size fields projected.
        Assert.Equal(2, results.Count);
        foreach (var item in results)
        {
            var dict = (IDictionary<string, object?>)item!;
            Assert.True(dict.ContainsKey("name"));
            Assert.True(dict.ContainsKey("size"));
            Assert.False(dict.ContainsKey("extra"));
        }
    }

    [Fact]
    public async Task Members_has_returns_true_for_existing_member()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("members has Length string");

        Assert.Collection(results, item => Assert.Equal(true, item));
    }

    [Fact]
    public async Task Members_has_returns_false_for_missing_member()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("members has BogusXyzNeverExists string");

        Assert.Collection(results, item => Assert.Equal(false, item));
    }

    [Fact]
    public async Task Members_get_returns_descriptor_for_named_member()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("members get Length string");

        var record = Assert.Single(results);
        var dict = (IDictionary<string, object?>)record!;
        Assert.Equal("Length", dict["Name"]);
    }

    [Fact]
    public async Task Members_props_filters_to_properties_only()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("members props string");

        Assert.NotEmpty(results);
        foreach (var record in results)
        {
            var dict = (IDictionary<string, object?>)record!;
            Assert.Equal("Property", dict["Kind"]);
        }
    }

    [Fact]
    public async Task Methods_has_returns_true_for_existing_method()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("methods has ToUpper string");

        Assert.Collection(results, item => Assert.Equal(true, item));
    }

    [Fact]
    public async Task Props_shortcut_lists_properties()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("props string");

        Assert.NotEmpty(results);
        foreach (var record in results)
        {
            var dict = (IDictionary<string, object?>)record!;
            Assert.Equal("Property", dict["Kind"]);
        }
    }

    [Fact]
    public async Task Funcs_shortcut_lists_methods()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("funcs string");

        Assert.NotEmpty(results);
        // funcs should expose methods (not necessarily with a Kind field; we just verify non-empty).
    }
}
