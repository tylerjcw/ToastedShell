using System.Reflection;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A capitalised name reaches the CLR type in member-access position, not the shell alias it
/// collides with — <c>TS-P2-37</c>.
/// </summary>
/// <remarks>
/// <para>
/// The alias table is matched case-insensitively, so <c>File</c> found <c>file</c> and every
/// static on <c>System.IO.File</c> failed with a message naming <c>System.IO.FileInfo</c> — a type
/// the reader never wrote. Filed against <c>File</c>; measuring it found the same collision on
/// <c>Array</c> and <c>Tuple</c>, reached through a *different* lookup (the shell's own static
/// types rather than the alias table), which is why the check runs at the one point both share.
/// </para>
/// <para>
/// Scoped to the head of a dotted path, which is where a type is used as a type. Annotations
/// resolve elsewhere and are deliberately untouched, so <c>var f: file</c> still binds
/// <c>FileInfo</c> and <c>var q: queue</c> does not silently become
/// <c>System.Collections.Queue</c>. The board's intent asked for exactly that split.
/// </para>
/// </remarks>
public sealed class AliasCaseVariantTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public AliasCaseVariantTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    /// <summary>
    /// Runs against the *default* runtime. A bare <c>new ToshRuntime()</c> has no stdlib, so
    /// `count` and `get` in these scripts would silently yield nothing and the assertions would
    /// be measuring the harness rather than the fix.
    /// </summary>
    private async Task<object?> EvalAsync(string script)
    {
        var engine = new ToshEngine(_runtime);
        return (await engine.ExecuteToListAsync(script)).LastOrDefault();
    }

    // ── the collision ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_static_on_File_reaches_System_IO_File()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "p2-37");

            Assert.Equal("p2-37", (await EvalAsync($"File.ReadAllText(\"{path}\")"))?.ToString());
            Assert.Equal(true, await EvalAsync($"File.Exists(\"{path}\")"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_static_on_Array_reaches_System_Array()
    {
        // `Array` collided through the shell's own static types rather than the alias table, so
        // this covers the second mechanism. It reported "Static method 'IndexOf' was not found on
        // type 'array'".
        Assert.Equal(1, Convert.ToInt32(await EvalAsync("Array.IndexOf([1,2,3], 2)")));
    }

    [Fact]
    public async Task The_unqualified_and_qualified_spellings_now_agree()
    {
        // The point of the fix is that the head resolves to the same type either way. `Tuple.Create`
        // still fails — generic static inference, `TS-P2-36` — but it must now fail *identically*
        // to the fully-qualified form rather than against `ToshTuple`, which is what shows the
        // head was routed correctly.
        var unqualified = await Assert.ThrowsAsync<ToshDiagnosticException>(
            async () => await EvalAsync("Tuple.Create(1, 2)"));
        var qualified = await Assert.ThrowsAsync<ToshDiagnosticException>(
            async () => await EvalAsync("System.Tuple.Create(1, 2)"));

        Assert.Contains("System.Tuple", unqualified.Diagnostics[0].Title, StringComparison.Ordinal);
        Assert.Equal(qualified.Diagnostics[0].Title, unqualified.Diagnostics[0].Title);
    }

    // ── what must not move ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("var f: file = (new FileInfo(\"/etc/hostname\"))\n$f.Name", "hostname")]
    [InlineData("var a: array = [1,2]\ntype-of $a | get Name", "array")]
    [InlineData("cast file (new FileInfo(\"/etc/hostname\")) | get Name", "hostname")]
    public async Task An_annotation_still_binds_the_shell_alias(string script, string expected)
    {
        Assert.Equal(expected, (await EvalAsync(script))?.ToString());
    }

    [Theory]
    [InlineData("new array(1,2,3) | count", 3)]
    [InlineData("new dict(\"a\", 1) | count", 1)]
    [InlineData("new list(1,2) | count", 2)]
    public async Task The_shell_static_types_are_reached_by_their_own_names(string script, int expected)
    {
        Assert.Equal(expected, Convert.ToInt32(await EvalAsync(script)));
    }

    [Theory]
    // An alias whose CLR namesake is the *same* type has nothing to switch to, and a capitalised
    // name with no alias at all never entered this path. Both must be unaffected.
    [InlineData("String.Join(\",\", [\"a\",\"b\"])", "a,b")]
    [InlineData("Regex.IsMatch(\"abc\", \"b\")", "True")]
    [InlineData("Path.GetFileName(\"/etc/hostname\")", "hostname")]
    public async Task Names_that_never_collided_are_unaffected(string script, string expected)
    {
        Assert.Equal(expected, (await EvalAsync(script))?.ToString());
    }

    [Fact]
    public async Task Directory_which_has_no_alias_still_resolves()
    {
        Assert.True(Convert.ToInt32(await EvalAsync("Directory.GetFiles(\"/etc\") | count")) > 0);
    }

    // ── the check runs on every dotted call, so it has to be cached ────────────

    [Fact]
    public void The_case_variant_answer_is_cached()
    {
        // Uncached, this cost exactly what `TS-P1-42` measured and removed: 3,000
        // `File.Exists` calls took 18.1s against 0.41s for an unaffected name. The mechanism is
        // asserted rather than the wall clock, because `TypeResolutionCacheTests` already
        // recorded that a timing comparison measures the machine as much as the code and flaked
        // under full-suite load.
        var resolver = new DotNetTypeResolver();
        var field = typeof(DotNetTypeResolver).GetField(
            "_aliasCaseVariantCache",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var cache = (System.Collections.IDictionary)field.GetValue(resolver)!;
        Assert.Equal(0, cache.Count);

        Assert.NotNull(resolver.ResolveAliasCaseVariant("File"));
        Assert.True(cache.Count > 0, "resolving a case variant did not populate the cache");

        var afterFirst = cache.Count;
        for (var i = 0; i < 50; i++)
        {
            Assert.NotNull(resolver.ResolveAliasCaseVariant("File"));
        }

        Assert.Equal(afterFirst, cache.Count);
    }

    [Fact]
    public void The_cache_is_keyed_case_sensitively()
    {
        // The question this cache answers *is* the casing, so `File` and `file` must not share
        // an entry — a case-insensitive key would hand `file` the answer computed for `File`.
        var resolver = new DotNetTypeResolver();

        Assert.NotNull(resolver.ResolveAliasCaseVariant("File"));
        Assert.Null(resolver.ResolveAliasCaseVariant("file"));
    }
}
