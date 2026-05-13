using Tosh.Crumb.Output;

namespace Tosh.Tests;

[Collection(nameof(EnvSerialCollection))]
public class CrumbFormatterResolveTests
{
    private static IDisposable WithEnv(string key, string? value)
    {
        var prev = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
        return new Restore(() => Environment.SetEnvironmentVariable(key, prev));
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _action;
        public Restore(Action a) { _action = a; }
        public void Dispose() => _action();
    }

    [Fact]
    public void Explicit_format_is_returned_unchanged()
    {
        Assert.Equal(OutputFormat.Json, PackageFormatter.Resolve(OutputFormat.Json));
        Assert.Equal(OutputFormat.Tssp, PackageFormatter.Resolve(OutputFormat.Tssp));
        Assert.Equal(OutputFormat.Names, PackageFormatter.Resolve(OutputFormat.Names));
    }

    [Fact]
    public void Auto_picks_TSSP_when_negotiated_regardless_of_consumer()
    {
        // The Resolve() probe also checks Console.IsOutputRedirected; tests
        // run with stdout redirected, so the relevant code path is exercised.
        // Hybrid spawn mode sets consumer=terminal but still expects TSSP,
        // so any negotiated value triggers framed output.
        foreach (var consumer in new[] { "pipe", "capture", "terminal" })
        {
            using (WithEnv("TOSH_STRUCTURED_STDOUT", "1"))
            using (WithEnv("TOSH_STDOUT_CONSUMER", consumer))
            {
                Assert.Equal(OutputFormat.Tssp, PackageFormatter.Resolve(OutputFormat.Auto));
            }
        }
    }

    [Fact]
    public void Auto_falls_back_to_Ndjson_without_negotiation()
    {
        using (WithEnv("TOSH_STRUCTURED_STDOUT", null))
        using (WithEnv("TOSH_STDOUT_CONSUMER", null))
        {
            // stdout is redirected in the test host → Ndjson, never Tssp.
            Assert.Equal(OutputFormat.Ndjson, PackageFormatter.Resolve(OutputFormat.Auto));
        }
    }
}
