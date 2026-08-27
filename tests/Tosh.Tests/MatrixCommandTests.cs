using System.Globalization;
using System.Text.RegularExpressions;
using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class MatrixCommandTests
{
    [Fact]
    public async Task Matrix_commands_build_matrices_from_rows_and_pipeline_input()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            mat [1, 2] [3, 4] | raw
            echo [[1, 2], [3, 4]] | mat | raw
            echo 1 2 3 | mat | raw
            """);

        Assert.Equal("[[1, 2], [3, 4]]", Assert.IsType<ShellTextLine>(results[0]).Text);
        Assert.Equal("[[1, 2], [3, 4]]", Assert.IsType<ShellTextLine>(results[1]).Text);
        Assert.Equal("[[1, 2, 3]]", Assert.IsType<ShellTextLine>(results[2]).Text);
    }

    [Fact]
    public async Task Matrix_values_resolve_to_shell_types_for_typeof_describe_type_and_new_command()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            mat | type-of | get Name
            new Matrix [1, 2] [3, 4] | type-of | get Name
            describe-type Matrix | get Name
            describe-type mat | get Name
            describe-type matrix | get Name
            """);

        Assert.Equal(["Matrix", "Matrix", "Matrix", "Matrix", "Matrix"], results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Matrix_type_metadata_and_operations_work()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            var left = mat [1, 2] [3, 4]
            var right = mat [5, 6] [7, 8]
            $left | type-of | get FullName
            $left | type-of | get ConstructorCount
            members Matrix | where _.Name == "RowCount" | first | get MemberType
            methods Matrix | where _.Name == "multiply" | first | get Static
            ($left * $right) | raw
            Matrix.determinant($left)
            Matrix.transpose($left) | raw
            Matrix.inverse($left) | raw
            ($left * (vec 1 1)) | raw
            """);

        Assert.Equal("ToSh.Matrix", results[0]);
        Assert.Equal(2, results[1]);
        Assert.Equal("System.Int32", results[2]);
        Assert.Equal(true, results[3]);
        Assert.Equal("[[19, 22], [43, 50]]", Assert.IsType<ShellTextLine>(results[4]).Text);
        Assert.Equal(-2d, Assert.IsType<double>(results[5]));
        Assert.Equal("[[1, 3], [2, 4]]", Assert.IsType<ShellTextLine>(results[6]).Text);
        var inverseValues = ParseMatrixText(Assert.IsType<ShellTextLine>(results[7]).Text);
        Assert.Equal(-2d, inverseValues[0], 12);
        Assert.Equal(1d, inverseValues[1], 12);
        Assert.Equal(1.5d, inverseValues[2], 12);
        Assert.Equal(-0.5d, inverseValues[3], 12);
        Assert.Equal("[3, 7]", Assert.IsType<ShellTextLine>(results[8]).Text);
    }

    private static double[] ParseMatrixText(string text)
    {
        return Regex.Matches(text, @"-?\d+(?:\.\d+)?(?:[Ee][+-]?\d+)?")
            .Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture))
            .ToArray();
    }
}
