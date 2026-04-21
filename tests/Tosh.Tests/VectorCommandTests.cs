using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class VectorCommandTests
{
    [Fact]
    public async Task Vec_command_builds_vectors_from_arguments_pipeline_and_empty_input()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            vec 1 2 3 | raw
            echo 1 2 3 | vec | raw
            echo [1, 2, 3] | vec | raw
            vec | raw
            """);

        Assert.Equal("[1, 2, 3]", Assert.IsType<ShellTextLine>(results[0]).Text);
        Assert.Equal("[1, 2, 3]", Assert.IsType<ShellTextLine>(results[1]).Text);
        Assert.Equal("[1, 2, 3]", Assert.IsType<ShellTextLine>(results[2]).Text);
        Assert.Equal("[]", Assert.IsType<ShellTextLine>(results[3]).Text);
    }

    [Fact]
    public async Task Vector_values_resolve_to_shell_types_for_typeof_describe_type_and_new_command()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            vec | type-of | get Name
            new Vector 1 2 3 | type-of | get Name
            describe-type Vector | get Name
            describe-type vec | get Name
            """);

        Assert.Equal(["Vector", "Vector", "Vector", "Vector"], results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Vector_type_metadata_exposes_members_methods_and_aliases()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            vec 1 2 3 | type-of | get FullName
            vec 1 2 3 | type-of | get ConstructorCount
            members Vector | where _.Name == "Length" | first | get MemberType
            methods Vector | where _.Name == "dot" | first | get Static
            help Vector | get Aliases
            """);

        Assert.Equal("ToSh.Vector", results[0]);
        Assert.Equal(2, results[1]);
        Assert.Equal("System.Int32", results[2]);
        Assert.Equal(true, results[3]);

        var aliases = Assert.IsAssignableFrom<System.Collections.IEnumerable>(results[4]).Cast<object?>().OfType<string>().ToArray();
        Assert.Contains("vec", aliases, StringComparer.OrdinalIgnoreCase);
    }
}
