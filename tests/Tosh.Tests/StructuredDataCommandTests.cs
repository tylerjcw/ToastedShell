using System.Xml.Linq;
using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class StructuredDataCommandTests
{
    [Fact]
    public async Task From_json_parses_root_objects_into_expando_records()
    {
        var engine = new ToshEngine();

        var typeResults = await engine.ExecuteToListAsync("echo \"{\\\"name\\\":\\\"toast\\\",\\\"size\\\":1024}\" | from json | type-of | get Name");
        var results = await engine.ExecuteToListAsync("echo \"{\\\"name\\\":\\\"toast\\\",\\\"size\\\":1024}\" | from json | get { name, size }");

        // `record` since TS-P3-11; `table` remains an alias but is not what type-of answers.
        Assert.Collection(typeResults, item => Assert.Equal("record", item));
        var projection = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(results));
        Assert.True(projection.TryGetValue("name", out var name));
        Assert.True(projection.TryGetValue("size", out var size));
        Assert.Equal("toast", name);
        Assert.Equal(1024L, size);
    }

    [Fact]
    public async Task From_json_preserves_array_roots_until_explicitly_expanded()
    {
        var engine = new ToshEngine();

        var typeResults = await engine.ExecuteToListAsync("echo \"[{\\\"name\\\":\\\"alpha\\\"},{\\\"name\\\":\\\"beta\\\"}]\" | from json | type-of | get Name");
        var expandedResults = await engine.ExecuteToListAsync("echo \"[{\\\"name\\\":\\\"alpha\\\"},{\\\"name\\\":\\\"beta\\\"}]\" | from json | each { _ } | get name");

        Assert.Collection(typeResults, item => Assert.Equal("array", item));
        Assert.Equal(["alpha", "beta"], expandedResults);
    }

    [Fact]
    public async Task From_csv_parses_rows_into_expando_records()
    {
        var engine = new ToshEngine();

        var typeResults = await engine.ExecuteToListAsync("echo \"name,size\" \"alpha,1\" \"beta,2\" | from csv | type-of | get Name");
        var expandedResults = await engine.ExecuteToListAsync("echo \"name,size\" \"alpha,1\" \"beta,2\" | from csv | each { _ } | get name");

        // `TOAST-0028`. One row per item, so one `record` per row. This read a single
        // `array<record>` until 2026-08-21, because `from csv` collected the whole table
        // into one value and relied on a downstream stage spreading it — which meant
        // `from csv | select name` was reaching into an `ExpandoObject[]` and being rescued.
        // Yielding rows is the honest shape and restores streaming for every consumer that
        // does not need the column-type inference's whole-column view.
        Assert.Equal(["record", "record"], typeResults);
        Assert.Equal(["alpha", "beta"], expandedResults);
    }

    /// <summary>
    /// `TS-P1-44` moved the XDocument behind `--raw`; it was not removed. Handing over
    /// the document is how a caller reaches the XML API for namespaces or node-level
    /// navigation, and this test pinned it — so what changed is which of the two is the
    /// default, and this now pins the escape hatch.
    /// </summary>
    [Fact]
    public async Task From_xml_raw_still_yields_an_xdocument()
    {
        var engine = new ToshEngine();

        var typeResults = await engine.ExecuteToListAsync("echo \"<root><item name=\\\"alpha\\\" /></root>\" | from xml --raw | type-of");
        var rootNameResults = await engine.ExecuteToListAsync("echo \"<root><item name=\\\"alpha\\\" /></root>\" | from xml --raw | get \"Root.Name.LocalName\"");

        Assert.Collection(typeResults, item => Assert.Equal(typeof(XDocument), item));
        Assert.Equal(["root"], rootNameResults);
    }

    [Fact]
    public async Task Parse_creates_expando_records_from_named_regex_groups()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo \"PID=42 Name=tosh\" | parse \"PID=(?<pid>[0-9]+) Name=(?<name>[A-Za-z]+)\"");

        var projection = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(results));
        Assert.True(projection.TryGetValue("pid", out var pid));
        Assert.True(projection.TryGetValue("name", out var name));
        Assert.Equal("42", pid);
        Assert.Equal("tosh", name);
    }

    [Fact]
    public async Task Parse_supports_shared_regex_flags_and_regex_objects()
    {
        var engine = new ToshEngine();

        var ignoreCaseResults = await engine.ExecuteToListAsync("echo \"pid=42\" | parse -i \"PID=(?<Pid>[0-9]+)\" | get Pid");
        var regexObjectResults = await engine.ExecuteToListAsync("echo \"PID=42\" | parse (new regex(\"PID=(?<Pid>[0-9]+)\")) | get Pid");

        Assert.Equal(["42"], ignoreCaseResults.Cast<string>().ToArray());
        Assert.Equal(["42"], regexObjectResults.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Summarize_auto_mode_applies_all_ops_to_scalar_numerics()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        // Inject integer scalars directly
        runtime.Variables["nums"] = new object?[] { 1, 2, 3, 4, 5 };
        var results = await engine.ExecuteToListAsync("$nums | summarize");

        var summary = Assert.IsType<ColumnSummary>(Assert.Single(results));
        Assert.Equal("Value", summary.Column);
        Assert.Equal(5L, summary.Count);
        Assert.NotNull(summary.Sum);
        Assert.NotNull(summary.Average);
        Assert.Equal(1L, summary.Min);
        Assert.Equal(5L, summary.Max);
    }

    [Fact]
    public async Task Summarize_auto_mode_string_columns_get_count_min_max_only()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        // Inject string-valued records directly
        var rows = new object?[]
        {
            ShellRecordUtilities.CreateExpando([new KeyValuePair<string, object?>("Name", "alpha"), new KeyValuePair<string, object?>("Score", "10")]),
            ShellRecordUtilities.CreateExpando([new KeyValuePair<string, object?>("Name", "beta"),  new KeyValuePair<string, object?>("Score", "20")]),
            ShellRecordUtilities.CreateExpando([new KeyValuePair<string, object?>("Name", "gamma"), new KeyValuePair<string, object?>("Score", "30")]),
        };
        runtime.Variables["rows"] = rows;

        var results = await engine.ExecuteToListAsync("$rows | summarize");

        var summaries = results.Cast<ColumnSummary>().ToDictionary(s => s.Column, StringComparer.OrdinalIgnoreCase);

        // Name column: string → count, min, max; no sum/avg
        Assert.True(summaries.ContainsKey("Name"));
        var nameSummary = summaries["Name"];
        Assert.Equal(3L, nameSummary.Count);
        Assert.Equal("alpha", nameSummary.Min?.ToString());
        Assert.Equal("gamma", nameSummary.Max?.ToString());
        Assert.Null(nameSummary.Sum);
        Assert.Null(nameSummary.Average);
    }

    [Fact]
    public async Task Summarize_auto_mode_numeric_records_get_all_ops()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        // Inject records with integer Size field directly
        var rows = new object?[]
        {
            ShellRecordUtilities.CreateExpando([new KeyValuePair<string, object?>("Size", 10)]),
            ShellRecordUtilities.CreateExpando([new KeyValuePair<string, object?>("Size", 20)]),
            ShellRecordUtilities.CreateExpando([new KeyValuePair<string, object?>("Size", 30)]),
        };
        runtime.Variables["rows"] = rows;

        var results = await engine.ExecuteToListAsync("$rows | summarize");

        var summaries = results.Cast<ColumnSummary>().ToDictionary(s => s.Column, StringComparer.OrdinalIgnoreCase);
        Assert.True(summaries.ContainsKey("Size"));
        var sizeSummary = summaries["Size"];
        Assert.Equal(3L, sizeSummary.Count);
        Assert.NotNull(sizeSummary.Sum);
        Assert.NotNull(sizeSummary.Average);
    }

    [Fact]
    public async Task Summarize_single_column_name_applies_auto_ops_to_that_column_only()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var rows = new object?[]
        {
            ShellRecordUtilities.CreateExpando([new KeyValuePair<string, object?>("Name", "alpha"), new KeyValuePair<string, object?>("Size", 10)]),
            ShellRecordUtilities.CreateExpando([new KeyValuePair<string, object?>("Name", "beta"),  new KeyValuePair<string, object?>("Size", 20)]),
            ShellRecordUtilities.CreateExpando([new KeyValuePair<string, object?>("Name", "gamma"), new KeyValuePair<string, object?>("Size", 30)]),
        };
        runtime.Variables["rows"] = rows;

        var results = await engine.ExecuteToListAsync("$rows | summarize Size");

        var summary = Assert.IsType<ColumnSummary>(Assert.Single(results));
        Assert.Equal("Size", summary.Column);
        Assert.Equal(3L, summary.Count);
        Assert.NotNull(summary.Sum);
        Assert.NotNull(summary.Average);
    }

    [Fact]
    public async Task Summarize_auto_mode_discovers_columns_on_typed_rows()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        runtime.Variables["rows"] = new object?[]
        {
            new FileSystemUsageInfo("/dev/sda1", "/", "ext4", StorageSize.FromBytes(1_000), StorageSize.FromBytes(400), StorageSize.FromBytes(600), 40, null, true),
            new FileSystemUsageInfo("/dev/sdb1", "/data", "ntfs", StorageSize.FromBytes(2_000), StorageSize.FromBytes(800), StorageSize.FromBytes(1_200), 40, null, true),
        };

        var results = await engine.ExecuteToListAsync("$rows | summarize");

        var summaries = results.Cast<ColumnSummary>().ToDictionary(summary => summary.Column, StringComparer.OrdinalIgnoreCase);

        Assert.Contains("FileSystem", summaries.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Used", summaries.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Available", summaries.Keys, StringComparer.OrdinalIgnoreCase);

        var used = summaries["Used"];
        Assert.Equal(2L, used.Count);
        Assert.Equal(StorageSize.FromBytes(1_200), used.Sum);
        Assert.Equal(StorageSize.FromBytes(600), used.Average);
        Assert.Equal(StorageSize.FromBytes(400), used.Min);
        Assert.Equal(StorageSize.FromBytes(800), used.Max);
    }

    [Fact]
    public async Task Summarize_single_member_path_shorthand_normalizes_underscore_prefix()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        runtime.Variables["rows"] = new object?[]
        {
            new FileSystemUsageInfo("/dev/sda1", "/", "ext4", StorageSize.FromBytes(1_000), StorageSize.FromBytes(400), StorageSize.FromBytes(600), 40, null, true),
            new FileSystemUsageInfo("/dev/sdb1", "/data", "ntfs", StorageSize.FromBytes(2_000), StorageSize.FromBytes(800), StorageSize.FromBytes(1_200), 40, null, true),
        };

        var summaryResults = await engine.ExecuteToListAsync("$rows | summarize _.Used");
        var sumResults = await engine.ExecuteToListAsync("$rows | sum _.Used");

        var summary = Assert.IsType<ColumnSummary>(Assert.Single(summaryResults));
        Assert.Equal("Used", summary.Column);
        Assert.Equal(2L, summary.Count);
        Assert.Equal(StorageSize.FromBytes(1_200), summary.Sum);
        Assert.Equal(StorageSize.FromBytes(600), summary.Average);
        Assert.Equal(StorageSize.FromBytes(400), summary.Min);
        Assert.Equal(StorageSize.FromBytes(800), summary.Max);

        Assert.Equal(StorageSize.FromBytes(1_200), Assert.IsType<StorageSize>(Assert.Single(sumResults)));
    }

    [Fact]
    public async Task Summarize_empty_input_returns_no_results()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("echo \"{\\\"x\\\":1}\" | from json | where { false } | summarize");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Collect_drains_pipeline_into_a_single_array()
    {
        var engine = new ToshEngine();

        // echo emits individual scalars within one pipeline; collect buffers them into one array
        var results = await engine.ExecuteToListAsync("echo 1 2 3 | collect");

        var arr = Assert.IsType<object?[]>(Assert.Single(results));
        Assert.Equal(["1", "2", "3"], arr.Select(x => x?.ToString() ?? string.Empty).ToArray());
    }
}
