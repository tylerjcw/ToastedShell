using System.Globalization;
using System.Numerics;
using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class ComplexCommandTests
{
    [Fact]
    public async Task Complex_commands_build_complex_numbers_from_args_and_pipeline_input()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            complex 3 4 | raw
            echo [3, 4] | complex | raw
            echo 3 4 | complex | raw
            complex 5 | raw
            """);

        Assert.Equal("3 + 4i", Assert.IsType<ShellTextLine>(results[0]).Text);
        Assert.Equal("3 + 4i", Assert.IsType<ShellTextLine>(results[1]).Text);
        Assert.Equal("3 + 4i", Assert.IsType<ShellTextLine>(results[2]).Text);
        Assert.Equal("5 + 0i", Assert.IsType<ShellTextLine>(results[3]).Text);
    }

    [Fact]
    public async Task Complex_values_resolve_to_shell_types_for_typeof_describe_type_and_new_command()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            complex | type-of | get Name
            new Complex 3 4 | type-of | get Name
            describe-type Complex | get Name
            describe-type complex | get Name
            """);

        Assert.Equal(["Complex", "Complex", "Complex", "Complex"], results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Complex_type_metadata_and_operations_work()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var z = complex 3 4
            var w = complex 1 -2
            $z | type-of | get FullName
            $z | type-of | get ConstructorCount
            members Complex | where _.Name == "Real" | first | get MemberType
            methods Complex | where _.Name == "from-polar" | first | get Static
            ($z + $w) | raw
            ($z * $w) | raw
            Complex.conjugate($z) | raw
            Complex.magnitude($z)
            Complex.phase($z)
            $z.Magnitude
            $z.Phase
            Complex.from-polar(2, Math.PI / 2) | raw
            echo 5 | cast complex | raw
            """);

        Assert.Equal("ToSh.Complex", results[0]);
        Assert.Equal(3, results[1]);
        Assert.Equal("System.Double", results[2]);
        Assert.Equal(true, results[3]);
        Assert.Equal("4 + 2i", Assert.IsType<ShellTextLine>(results[4]).Text);
        Assert.Equal("11 - 2i", Assert.IsType<ShellTextLine>(results[5]).Text);
        Assert.Equal("3 - 4i", Assert.IsType<ShellTextLine>(results[6]).Text);
        Assert.Equal(5d, Assert.IsType<double>(results[7]), 12);
        Assert.Equal(Math.Atan2(4d, 3d), Assert.IsType<double>(results[8]), 12);
        Assert.Equal(5d, Assert.IsType<double>(results[9]), 12);
        Assert.Equal(Math.Atan2(4d, 3d), Assert.IsType<double>(results[10]), 12);

        var polar = ParseComplexText(Assert.IsType<ShellTextLine>(results[11]).Text);
        Assert.Equal(0d, polar.Real, 10);
        Assert.Equal(2d, polar.Imaginary, 10);

        Assert.Equal("5 + 0i", Assert.IsType<ShellTextLine>(results[12]).Text);
    }

    [Fact]
    public async Task Complex_cast_parses_compact_complex_strings_and_imaginary_literals()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            echo "3+4i" | cast complex | raw
            echo "3 - 4i" | cast complex | raw
            echo "4i" | cast complex | raw
            echo "-i" | cast complex | raw
            echo 4i | raw
            echo -2.5i | raw
            """);

        Assert.Equal("3 + 4i", Assert.IsType<ShellTextLine>(results[0]).Text);
        Assert.Equal("3 - 4i", Assert.IsType<ShellTextLine>(results[1]).Text);
        Assert.Equal("0 + 4i", Assert.IsType<ShellTextLine>(results[2]).Text);
        Assert.Equal("0 - 1i", Assert.IsType<ShellTextLine>(results[3]).Text);
        Assert.Equal("0 + 4i", Assert.IsType<ShellTextLine>(results[4]).Text);
        Assert.Equal("0 - 2.5i", Assert.IsType<ShellTextLine>(results[5]).Text);
    }

    private static Complex ParseComplexText(string text)
    {
        var parts = text
            .Replace("i", string.Empty, StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.True(parts.Length == 3, $"Unexpected complex text format: {text}");

        var real = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var sign = parts[1];
        var imaginary = double.Parse(parts[2], CultureInfo.InvariantCulture);

        if (sign == "-")
        {
            imaginary = -imaginary;
        }

        return new Complex(real, imaginary);
    }
}
