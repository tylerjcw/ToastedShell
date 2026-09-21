using System.Text.RegularExpressions;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The alias table and the table documenting it stay in step — <c>TOAST-0140</c>.
/// </summary>
/// <remarks>
/// <para>
/// An audit found 126 alias names in code against 85 named in `§Built-in Type Aliases`, a
/// count that said "over 40", `ptr` listed twice, `tuple` documented as `System.Tuple` where
/// the code maps `ToshTuple`, and `dir` documented and absent while `directory` resolved
/// through the platform-index fallback to the *static* `System.IO.Directory` — a type no
/// annotation can hold a value of.
/// </para>
/// <para>
/// None of that was noticed by anything; it was noticed by someone reading both. These are the
/// two checks that would have caught it, and they are deliberately repository-shape tests
/// rather than behaviour tests, because the failure is drift rather than a wrong answer.
/// </para>
/// </remarks>
public sealed class TypeAliasTableTests
{
    private static readonly string RepositoryRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static string ResolverSource() =>
        File.ReadAllText(Path.Combine(RepositoryRoot, "src/Toast.Runtime/DotNetTypeResolver.cs"));

    private static string SpecificationTable()
    {
        var tex = File.ReadAllText(Path.Combine(RepositoryRoot, "docs/spec/toastscript-spec.tex"));
        var start = tex.IndexOf(@"\section{Built-in Type Aliases}", StringComparison.Ordinal);
        var end = tex.IndexOf(@"\label{tab:types}", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "the specification's alias section moved");

        return tex[start..end];
    }

    /// <summary>
    /// No alias name is defined twice.
    /// </summary>
    /// <remarks>
    /// The table is a collection initialiser using the indexer, so a repeated key overwrites
    /// rather than throwing and two identical entries are invisible at runtime — which is how
    /// `nint` and `nuint` came to be defined twice. Nothing can catch this by reading
    /// <see cref="DotNetTypeResolver.BuiltInAliases"/>; it has to read the source.
    /// </remarks>
    [Fact]
    public void No_alias_name_is_defined_twice()
    {
        var block = AliasBlock(ResolverSource());

        var duplicates = Regex.Matches(block, """\["(?<name>[^"]+)"\]\s*=\s*typeof\(""")
            .Select(match => match.Groups["name"].Value)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ({group.Count()}x)")
            .ToList();

        Assert.Empty(duplicates);
    }

    /// <summary>
    /// Every alias the resolver defines is named in the specification's table.
    /// </summary>
    /// <remarks>
    /// A physical quantity answers to a short and a long name for the same type —
    /// `lengthquantity` beside `length` — and the section says so in prose rather than
    /// carrying twenty-six extra rows, so a long form is covered when its short form is.
    /// </remarks>
    [Fact]
    public void Every_alias_is_documented()
    {
        var documented = Regex.Matches(SpecificationTable(), @"\\code\{(?<name>[^}]+)\}")
            .Select(match => match.Groups["name"].Value.Replace("\\", "").Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var undocumented = DotNetTypeResolver.BuiltInAliases.Keys
            .Where(name => !documented.Contains(name))
            .Where(name => !(name.EndsWith("quantity", StringComparison.OrdinalIgnoreCase)
                             && documented.Contains(name[..^"quantity".Length])))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(undocumented);
    }

    /// <summary>
    /// And a name the table promises actually resolves, to the type it promises.
    /// </summary>
    /// <remarks>
    /// `dir` was documented as `DirectoryInfo` and was not an alias at all, so it did not
    /// resolve; `directory` reached the platform-index fallback and came back as the static
    /// `System.IO.Directory`. Documenting a name is a claim that it works.
    /// </remarks>
    [Theory]
    [InlineData("dir", typeof(DirectoryInfo))]
    [InlineData("directory", typeof(DirectoryInfo))]
    [InlineData("file", typeof(FileInfo))]
    [InlineData("version", typeof(Version))]
    [InlineData("datetimeoffset", typeof(DateTimeOffset))]
    [InlineData("tuple", typeof(ToshTuple))]
    public void A_documented_alias_resolves_to_the_documented_type(string alias, Type expected)
    {
        Assert.True(DotNetTypeResolver.BuiltInAliases.TryGetValue(alias, out var actual),
            $"'{alias}' is documented and is not an alias");
        Assert.Equal(expected, actual);
    }

    private static string AliasBlock(string source)
    {
        var start = source.IndexOf("Aliases = new Dictionary<string, Type>", StringComparison.Ordinal);
        var end = source.IndexOf("GenericAliasArities", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "the alias table moved");

        return source[start..end];
    }
}
