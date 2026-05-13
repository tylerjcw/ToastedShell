using Tosh.Crumb.Pacman;

namespace Tosh.Tests;

[Collection(nameof(EnvSerialCollection))]
public class CrumbPrivilegeTests
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
    public void CRUMB_SUDO_wins_over_path_lookup()
    {
        using var _ = WithEnv("CRUMB_SUDO", "doas-stub -E");
        var tokens = Privilege.ResolveEscalator();
        if (Privilege.IsRoot)
        {
            Assert.Empty(tokens);
            return;
        }
        Assert.Equal(new[] { "doas-stub", "-E" }, tokens);
    }

    [Fact]
    public void CRUMB_SUDO_blank_falls_back_to_path()
    {
        using var _ = WithEnv("CRUMB_SUDO", "   ");
        var tokens = Privilege.ResolveEscalator();
        if (Privilege.IsRoot) { Assert.Empty(tokens); return; }
        // Whatever the host has — must be a single existing path.
        Assert.Single(tokens);
        Assert.True(File.Exists(tokens[0]), $"resolved escalator does not exist: {tokens[0]}");
    }

    [Fact]
    public void Wrap_prepends_escalator()
    {
        using var _ = WithEnv("CRUMB_SUDO", "fake-sudo");
        var wrapped = Privilege.Wrap(new[] { "pacman", "-Sy" });
        if (Privilege.IsRoot)
        {
            Assert.Equal(new[] { "pacman", "-Sy" }, wrapped);
        }
        else
        {
            Assert.Equal(new[] { "fake-sudo", "pacman", "-Sy" }, wrapped);
        }
    }
}

[CollectionDefinition(nameof(EnvSerialCollection))]
public sealed class EnvSerialCollection { /* serializes env-mutating tests */ }
