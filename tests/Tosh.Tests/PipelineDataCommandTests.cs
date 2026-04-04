using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class PipelineDataCommandTests
{
    [Fact]
    public async Task Flatten_expands_top_level_collections_only_when_requested()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var arrayResults = await engine.ExecuteToListAsync("echo \"[1,2,3]\" | from-json | type-of | get Name");
        var flattenedResults = await engine.ExecuteToListAsync("echo \"[1,2,3]\" | from-json | flatten");

        Assert.Collection(arrayResults, item => Assert.Equal("array", item));
        Assert.Equal([1L, 2L, 3L], flattenedResults.Cast<long>().ToArray());
    }

    [Fact]
    public async Task Distinct_and_group_by_work_with_projected_object_members()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        const string json = "[{\"kind\":\"a\",\"value\":1},{\"kind\":\"a\",\"value\":2},{\"kind\":\"b\",\"value\":3}]";
        var escapedJson = Quote(json);

        var distinctResults = await engine.ExecuteToListAsync($"echo {escapedJson} | from-json | flatten | distinct kind | get value");
        var groupedResults = await engine.ExecuteToListAsync($"echo {escapedJson} | from-json | flatten | group-by kind");

        Assert.Equal([1L, 3L], distinctResults.Cast<long>().ToArray());

        var groups = groupedResults.Cast<GroupingInfo>().OrderBy(group => group.Key?.ToString(), StringComparer.Ordinal).ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Equal("a", groups[0].Key);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal("b", groups[1].Key);
        Assert.Equal(1, groups[1].Count);
    }

    [Fact]
    public async Task Aggregates_support_numeric_and_storage_size_values()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var sumResults = await engine.ExecuteToListAsync("echo \"[1,2,3]\" | from-json | flatten | sum");
        var averageResults = await engine.ExecuteToListAsync("echo \"[1,2,3]\" | from-json | flatten | average");
        var avgResults = await engine.ExecuteToListAsync("echo \"[1,2,3]\" | from-json | flatten | avg");
        var minResults = await engine.ExecuteToListAsync("echo \"[3,1,2]\" | from-json | flatten | min");
        var maxResults = await engine.ExecuteToListAsync("echo \"[3,1,2]\" | from-json | flatten | max");

        Assert.Collection(sumResults, item => Assert.Equal(6L, item));
        Assert.Collection(averageResults, item => Assert.Equal(2L, item));
        Assert.Collection(avgResults, item => Assert.Equal(2L, item));
        Assert.Collection(minResults, item => Assert.Equal(1L, item));
        Assert.Collection(maxResults, item => Assert.Equal(3L, item));

        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "one.txt"), new string('a', 3));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "two.txt"), new string('b', 5));

        var sizeResults = await engine.ExecuteToListAsync(
            $"ls -la {Quote(temporaryDirectory.Path)} | where _.Type == file | sum Size");

        var total = Assert.IsType<StorageSize>(Assert.Single(sizeResults));
        Assert.Equal(StorageSize.FromBytes(8), total);
    }

    [Fact]
    public async Task Summarize_returns_structured_summary_for_scalar_pipeline()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo \"[1,2,3,4,5]\" | from-json | flatten | summarize --sum --avg --min --max --count");

        var summary = Assert.IsType<ColumnSummary>(Assert.Single(results));
        Assert.Equal("Value", summary.Column);
        Assert.Equal(5, summary.RowCount);
        Assert.Equal(5, summary.ValueCount);
        Assert.Equal(5, summary.Count);
        Assert.Equal(15L, summary.Sum);
        Assert.Equal(3L, summary.Average);
        Assert.Equal(1L, summary.Min);
        Assert.Equal(5L, summary.Max);
    }

    [Fact]
    public async Task Summarize_supports_projected_columns_and_alias()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        const string json = "[{\"size\":1,\"used\":1},{\"size\":3,\"used\":2},{\"size\":null,\"used\":4}]";
        var escapedJson = Quote(json);

        var results = await engine.ExecuteToListAsync(
            $"echo {escapedJson} | from-json | flatten | summary --sum size,used --avg size --count size");

        var summaries = results.Cast<ColumnSummary>().OrderBy(summary => summary.Column, StringComparer.Ordinal).ToArray();

        Assert.Equal(2, summaries.Length);

        var size = Assert.Single(summaries, summary => string.Equals(summary.Column, "size", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, size.RowCount);
        Assert.Equal(2, size.ValueCount);
        Assert.Equal(2, size.Count);
        Assert.Equal(4L, size.Sum);
        Assert.Equal(2L, size.Average);
        Assert.Null(size.Min);
        Assert.Null(size.Max);

        var used = Assert.Single(summaries, summary => string.Equals(summary.Column, "used", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, used.RowCount);
        Assert.Equal(3, used.ValueCount);
        Assert.Null(used.Count);
        Assert.Equal(7L, used.Sum);
        Assert.Null(used.Average);
        Assert.Null(used.Min);
        Assert.Null(used.Max);
    }

    [Fact]
    public async Task To_json_and_to_csv_serialize_pipeline_values()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var jsonResults = await engine.ExecuteToListAsync(
            "echo \"{\\\"name\\\":\\\"toast\\\",\\\"size\\\":2}\" | from-json | to-json -c");

        var csvResults = await engine.ExecuteToListAsync(
            "echo \"[{\\\"name\\\":\\\"alpha\\\",\\\"size\\\":1},{\\\"name\\\":\\\"beta\\\",\\\"size\\\":2}]\" | from-json | flatten | to-csv");

        Assert.Equal("{\"name\":\"toast\",\"size\":2}", Assert.IsType<ShellTextLine>(Assert.Single(jsonResults)).Text);

        var csv = Assert.IsType<ShellTextLine>(Assert.Single(csvResults)).Text;
        Assert.Equal(
            [
                "name,size",
                "alpha,1",
                "beta,2",
            ],
            csv.Split(Environment.NewLine));
    }

    [Fact]
    public async Task To_json_and_to_csv_serialize_tosh_classes_by_shell_members()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var jsonResults = await engine.ExecuteToListAsync(
            """
            class Item(name: string, quantity: int) {
                prop Name: string = name
                prop Quantity: int = quantity
                shy prop InternalName: string? = null
            }

            new Item("Bread", 2) | to-json -c
            """);

        var csvResults = await engine.ExecuteToListAsync(
            """
            class Item(name: string, quantity: int) {
                prop Name: string = name
                prop Quantity: int = quantity
                shy prop InternalName: string? = null
            }

            echo [new Item("Bread", 2), new Item("Coffee", 1)] | flatten | to-csv
            """);

        var json = Assert.IsType<ShellTextLine>(Assert.Single(jsonResults)).Text;
        Assert.Contains("\"Name\":\"Bread\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Quantity\":2", json, StringComparison.Ordinal);
        Assert.DoesNotContain("InternalName", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Definition", json, StringComparison.Ordinal);

        var csv = Assert.IsType<ShellTextLine>(Assert.Single(csvResults)).Text;
        Assert.Equal(
            [
                "Name,Quantity",
                "Bread,2",
                "Coffee,1",
            ],
            csv.Split(Environment.NewLine));
    }

    [Fact]
    public async Task Lines_splits_multiline_text_into_individual_lines()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo \"alpha\\nbeta\\ngamma\" | lines");

        Assert.Equal(["alpha", "beta", "gamma"], results.Cast<ShellTextLine>().Select(line => line.Text).ToArray());
    }

    [Fact]
    public async Task Lines_passes_single_line_text_through()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo \"hello\" | lines");

        Assert.Equal("hello", Assert.IsType<ShellTextLine>(Assert.Single(results)).Text);
    }

    [Fact]
    public async Task Lines_handles_multiple_pipeline_items()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo \"a\\nb\" \"c\\nd\" | lines");

        Assert.Equal(["a", "b", "c", "d"], results.Cast<ShellTextLine>().Select(line => line.Text).ToArray());
    }

    [Fact]
    public async Task Lines_skips_empty_text()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo \"\" | lines");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Lines_handles_windows_line_endings()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo \"one\\r\\ntwo\\r\\nthree\" | lines");

        Assert.Equal(["one", "two", "three"], results.Cast<ShellTextLine>().Select(line => line.Text).ToArray());
    }

    private static string Quote(string path)
    {
        return "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-pipeline-data-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
