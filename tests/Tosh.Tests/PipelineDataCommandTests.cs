using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class PipelineDataCommandTests
{
    [Fact]
    public async Task Flatten_expands_top_level_collections_only_when_requested()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var arrayResults = await engine.ExecuteToListAsync("echo \"[1,2,3]\" | from-json | type-of");
        var flattenedResults = await engine.ExecuteToListAsync("echo \"[1,2,3]\" | from-json | flatten");

        Assert.Collection(arrayResults, item => Assert.Equal(typeof(object[]), item));
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
            $"ls -la {Quote(temporaryDirectory.Path)} | where Type == file | sum Size");

        var total = Assert.IsType<StorageSize>(Assert.Single(sizeResults));
        Assert.Equal(StorageSize.FromBytes(8), total);
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
