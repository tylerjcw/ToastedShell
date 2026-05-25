using System.Drawing;
using System.Reflection;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Covers the <c>eval</c> shell command and the
/// <see cref="DotNetTypeResolver"/> resolution of types from
/// assemblies loaded lazily after the platform-type index is built.
/// </summary>
public sealed class EvalAndTypeResolverTests
{
    [Fact]
    public async Task Eval_evaluates_a_literal_expression()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("eval \"1 + 2\"");

        Assert.Collection(results, item => Assert.Equal(3, item));
    }

    [Fact]
    public async Task Eval_streams_values_not_strings()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("eval \"System.Drawing.Color.Red\"");

        var color = Assert.IsType<Color>(Assert.Single(results));
        Assert.Equal("Red", color.Name);
    }

    [Fact]
    public async Task Eval_runs_in_caller_scope()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync("eval \"var x = 42\"\necho $x");

        // The `var x` statement yields nothing; `echo $x` yields 42.
        Assert.Equal(new object?[] { 42 }, results.ToArray());
    }

    [Fact]
    public async Task Eval_works_inside_each_with_interpolated_member_path()
    {
        var engine = new ToshEngine();

        var results = await engine.ExecuteToListAsync(
            "[\"Red\", \"Lime\", \"LightBlue\"] | each { eval $\"System.Drawing.Color.{$_}\" }");

        Assert.Collection(
            results,
            item => Assert.Equal("Red", Assert.IsType<Color>(item).Name),
            item => Assert.Equal("Lime", Assert.IsType<Color>(item).Name),
            item => Assert.Equal("LightBlue", Assert.IsType<Color>(item).Name));
    }

    [Fact]
    public async Task Eval_requires_at_least_one_argument()
    {
        var engine = new ToshEngine();

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            async () => await engine.ExecuteToListAsync("eval"));

        Assert.Contains("at least one source string", ex.Message);
    }

    [Fact]
    public void DotNetTypeResolver_finds_type_from_lazily_loaded_namespace()
    {
        // System.Drawing.Color lives in System.Drawing.Primitives which on
        // single-file publishes is not enumerable via TPA — it loads lazily
        // when a type that references it is first JIT-touched. This test
        // pins down that the resolver finds Color regardless of whether
        // it was indexed at warm-up time.
        var resolver = new DotNetTypeResolver();

        var type = resolver.Resolve("System.Drawing.Color");

        Assert.Equal(typeof(Color), type);
    }

    [Fact]
    public void DotNetTypeResolver_rescans_assemblies_loaded_after_indexing()
    {
        // Simulate the race: pretend the platform-type index was built when
        // far fewer assemblies were present, then ensure Resolve still finds
        // types in the assemblies that were "loaded after" — i.e. the
        // fallback loop in TryResolveDirect kicks in.
        var resolverType = typeof(DotNetTypeResolver);
        var countField = resolverType.GetField(
            "_platformIndexedAssemblyCount",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var negativeCache = resolverType.GetField(
            "_negativeResultCache",
            BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        var clearMethod = negativeCache.GetType().GetMethod("Clear")!;

        var original = (int)countField.GetValue(null)!;
        try
        {
            // Pretend only 1 assembly was indexed at warm-up time.
            countField.SetValue(null, 1);
            clearMethod.Invoke(negativeCache, null);

            var resolver = new DotNetTypeResolver();
            var type = resolver.Resolve("System.Drawing.Color");

            Assert.Equal(typeof(Color), type);
        }
        finally
        {
            countField.SetValue(null, original);
            clearMethod.Invoke(negativeCache, null);
        }
    }
}
