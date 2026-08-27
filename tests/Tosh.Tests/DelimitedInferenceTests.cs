using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Column type inference for delimited input (<c>TS-P2-27</c>).
/// </summary>
/// <remarks>
/// <para>
/// Found by executing the specification's CSV worked example, which read
/// <c>| where _.Amount &gt; 100</c> and failed with "Values of type 'System.String'
/// and 'System.Int32' cannot be ordered". `from json` already produced typed values,
/// so the split was internal as well as against the document.
/// </para>
/// <para>
/// The decision was the narrow option: integers, decimals and booleans, no dates.
/// These cases pin the boundary in both directions, because the value of narrow
/// inference is entirely in what it declines to guess.
/// </para>
/// </remarks>
public sealed class DelimitedInferenceTests
{
    private static async Task<object?> FirstFieldAsync(string csv, string field)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            $"echo {Quote(csv)} | from csv | first 1 | get {field}");

        return Assert.Single(results);
    }

    private static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal)
                   .Replace("\"", "\\\"", StringComparison.Ordinal)
                   .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    [Theory]
    // Integers, narrowed to int where they fit.
    [InlineData("n\n150", typeof(int))]
    [InlineData("n\n-150", typeof(int))]
    [InlineData("n\n0", typeof(int))]
    [InlineData("n\n9999999999", typeof(long))]
    // Decimals.
    [InlineData("n\n2.5", typeof(double))]
    [InlineData("n\n0.5", typeof(double))]
    [InlineData("n\n-0.25", typeof(double))]
    [InlineData("n\n1e3", typeof(double))]
    // Booleans, and only the two spellings that are unambiguous.
    [InlineData("n\ntrue", typeof(bool))]
    [InlineData("n\nFALSE", typeof(bool))]
    public async Task A_column_that_agrees_with_itself_is_typed(string csv, Type expected)
    {
        var value = await FirstFieldAsync(csv, "n");

        Assert.IsType(expected, value);
    }

    [Theory]
    // A leading zero is an identifier, and converting it destroys the zero
    // irreversibly — zip codes, phone extensions, `007`.
    [InlineData("n\n007")]
    [InlineData("n\n01234")]
    [InlineData("n\n-007")]
    // The comma is the delimiter, so a thousands separator cannot be a number here.
    [InlineData("n\n\"1,234\"")]
    // Dates are deliberately excluded: 01/02/26 is three different days by locale.
    [InlineData("n\n2026-01-01")]
    [InlineData("n\n01/02/26")]
    // Boolean-ish words that are guesses rather than readings.
    [InlineData("n\nyes")]
    [InlineData("n\nY")]
    // Not a number at all.
    [InlineData("n\nalpha")]
    [InlineData("n\n12abc")]
    public async Task A_column_the_rules_decline_stays_text(string csv)
    {
        var value = await FirstFieldAsync(csv, "n");

        Assert.IsType<string>(value);
    }

    [Fact]
    public async Task Integers_and_decimals_in_one_column_reconcile_as_numbers()
    {
        // The one pair that combines rather than degrading: a column of 1, 2, 2.5
        // is numeric, and typing it int would lose the 2.5.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            "echo \"n\\n1\\n2.5\\n3\" | from csv | each { $_.n }");

        Assert.Equal(3, results.Count);
        Assert.All(results, value => Assert.IsType<double>(value));
    }

    [Fact]
    public async Task One_disagreeing_cell_makes_the_whole_column_text()
    {
        // Inference is per column, not per cell. Typing only the cells that parse
        // would put an int beside a string in one column, so values in that column
        // could not be compared with each other — a failure that shows up only on
        // the rows that differ, which is worse than leaving the column textual.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            "echo \"n\\n7\\nn/a\\n9\" | from csv | each { $_.n }");

        Assert.Equal(3, results.Count);
        Assert.All(results, value => Assert.IsType<string>(value));
    }

    [Fact]
    public async Task An_empty_cell_is_not_evidence_and_becomes_null()
    {
        // A column of numbers with a gap is still a column of numbers, and the gap
        // cannot be the empty string a textual column would keep.
        //
        // The rows are read directly rather than through `| each { $_.n }`, which
        // would report two values rather than three: a block yielding null
        // contributes nothing to the pipeline, matching PowerShell. That elision is
        // orthogonal to inference and would have made this assertion measure the
        // wrong thing.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            "echo \"n,m\\n1,a\\n,b\\n3,c\" | from csv");

        // `TOAST-0028`. The rows arrive as items now rather than as one array that a
        // downstream stage would have spread, so they are read straight off the result.
        var rows = results
            .Select(row => Assert.IsAssignableFrom<IDictionary<string, object?>>(row))
            .ToArray();

        Assert.Equal(3, rows.Length);
        Assert.Equal(1, rows[0]["n"]);
        Assert.Null(rows[1]["n"]);
        Assert.Equal(3, rows[2]["n"]);
    }

    [Fact]
    public async Task Raw_turns_inference_off()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            "echo \"n\\n150\" | from csv --raw | first 1 | get n");

        Assert.Equal("150", Assert.Single(results));
    }

    [Fact]
    public async Task The_specification_csv_example_runs_as_written()
    {
        // The example that found this. It is asserted end to end rather than by
        // column type, because "the documented pipeline runs" is the actual claim.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            """
            echo "Date,Customer,Amount\n2026-01-01,Alice,150\n2026-01-02,Bob,50\n2026-01-03,Cara,300"
                | from csv
                | where _.Amount > 100
                | sort Amount
                | reverse
                | select Date Customer Amount
                | to csv
            """);

        var text = Assert.Single(results)?.ToString();

        Assert.Equal(
            """
            Date,Customer,Amount
            2026-01-03,Cara,300
            2026-01-01,Alice,150
            """.ReplaceLineEndings("\n"),
            text?.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Tsv_infers_on_the_same_rules()
    {
        // The inference lives in the shared delimited format, so tab-separated
        // input gets it too. Asserted rather than assumed.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            "echo \"n\\tm\\n150\\talpha\" | from tsv | first 1 | get n");

        Assert.Equal(150, Assert.Single(results));
    }

    [Fact]
    public async Task A_typed_column_round_trips_back_to_csv()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            "echo \"n,b\\n150,true\" | from csv | to csv");

        Assert.Equal(
            "n,b\n150,true",
            Assert.Single(results)?.ToString()?.ReplaceLineEndings("\n"));
    }
}
