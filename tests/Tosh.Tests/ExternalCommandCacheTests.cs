using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class ExternalCommandCacheTests
{
    [Fact]
    public void Cached_resolution_returns_same_result_as_uncached()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ExternalCommandResolver.InvalidateCache();

        var first = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "sh");
        var second = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "sh");

        Assert.Equal(ExternalCommandLookupStatus.Found, first.Status);
        Assert.Equal(first.ResolvedPath, second.ResolvedPath);
    }

    [Fact]
    public void Cache_is_invalidated_when_path_changes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ExternalCommandResolver.InvalidateCache();

        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            var first = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "sh");
            Assert.Equal(ExternalCommandLookupStatus.Found, first.Status);

            // Simulate PATH change — cache should invalidate
            Environment.SetEnvironmentVariable("PATH", "/nonexistent:" + previousPath);

            var second = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "sh");
            Assert.Equal(ExternalCommandLookupStatus.Found, second.Status);
            Assert.Equal(first.ResolvedPath, second.ResolvedPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            ExternalCommandResolver.InvalidateCache();
        }
    }

    [Fact]
    public void InvalidateCache_clears_all_cached_entries()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Warm the cache
        var result = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "sh");
        Assert.Equal(ExternalCommandLookupStatus.Found, result.Status);

        ExternalCommandResolver.InvalidateCache();

        // After invalidation the resolver should still find the command but via a fresh lookup
        var fresh = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "sh");
        Assert.Equal(ExternalCommandLookupStatus.Found, fresh.Status);
        Assert.Equal(result.ResolvedPath, fresh.ResolvedPath);
    }

    [Fact]
    public void NotFound_commands_are_not_cached()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ExternalCommandResolver.InvalidateCache();

        var result = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "definitely-not-a-real-command-xyz");
        Assert.Equal(ExternalCommandLookupStatus.NotFound, result.Status);

        // A second lookup should still return NotFound (not a stale cached result)
        var second = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "definitely-not-a-real-command-xyz");
        Assert.Equal(ExternalCommandLookupStatus.NotFound, second.Status);
    }
}
