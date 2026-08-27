using System.Collections.Generic;
using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

/// <summary>
/// Verifies that pipeline commands tagged with <see cref="StreamingBehavior.ShortCircuit"/>
/// actually cancel upstream production, and that the metadata is surfaced on
/// <see cref="CommandMetadata.Streaming"/>.
/// </summary>
public sealed class StreamingContractTests(ToshRuntimeFixture fixture) : IClassFixture<ToshRuntimeFixture>
{
    [Fact]
    public async Task First_short_circuits_a_large_upstream_range()
    {
        var engine = new ToshEngine(fixture.Runtime.Language);

        // A 10-million-element range; if `first 3` did not short-circuit, this
        // would visibly hang. The xUnit default timeout will catch a regression.
        var results = await engine.ExecuteToListAsync("1..10000000 | first 3 | collect");

        Assert.Single(results);
        var collected = Assert.IsAssignableFrom<IEnumerable<object?>>(results[0]);
        Assert.Equal(new object[] { 1, 2, 3 }, collected);
    }

    [Fact]
    public async Task TakeWhile_short_circuits_at_first_false()
    {
        var engine = new ToshEngine(fixture.Runtime.Language);

        var results = await engine.ExecuteToListAsync("1..10000000 | take-while { _ < 5 } | collect");

        Assert.Single(results);
        var collected = Assert.IsAssignableFrom<IEnumerable<object?>>(results[0]);
        Assert.Equal(new object[] { 1, 2, 3, 4 }, collected);
    }

    [Fact]
    public async Task Any_short_circuits_at_first_match()
    {
        var engine = new ToshEngine(fixture.Runtime.Language);

        var results = await engine.ExecuteToListAsync("1..10000000 | any { _ == 3 }");

        Assert.Single(results);
        Assert.Equal(true, results[0]);
    }

    [Fact]
    public void First_advertises_short_circuit_streaming_in_metadata()
    {
        var metadata = new Tosh.Stdlib.Pipeline.FirstCommand().GetMetadata();
        Assert.Equal("short-circuit", metadata.Streaming);
    }

    [Fact]
    public void Where_advertises_lazy_streaming_in_metadata()
    {
        var metadata = new Tosh.Stdlib.Pipeline.WhereCommand().GetMetadata();
        Assert.Equal("lazy", metadata.Streaming);
    }

    [Fact]
    public void Sort_advertises_eager_streaming_in_metadata()
    {
        var metadata = new Tosh.Stdlib.Pipeline.SortCommand().GetMetadata();
        Assert.Equal("eager", metadata.Streaming);
    }
}
