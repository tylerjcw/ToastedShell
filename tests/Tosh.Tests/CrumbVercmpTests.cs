using Tosh.Crumb.Aur;

namespace Tosh.Tests;

public class CrumbVercmpTests
{
    private static readonly bool HasVercmp = ProbeVercmp();

    private static bool ProbeVercmp()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("vercmp")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("1");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(2000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    [Theory]
    [InlineData("1.0", "1.1", -1)]
    [InlineData("2.0", "1.9", 1)]
    [InlineData("1.0", "1.0", 0)]
    [InlineData("1.0-1", "1.0-2", -1)]
    [InlineData("1.0.1", "1.0", 1)]
    [InlineData("1:1.0", "0:9.9", 1)]
    public void Compare_matches_vercmp(string a, string b, int expected)
    {
        if (!HasVercmp) return;
        Assert.Equal(expected, Vercmp.Compare(a, b));
    }

    [Fact]
    public void IsOlder_true_when_installed_below_candidate()
    {
        Assert.True(Vercmp.IsOlder("1.0", "1.1"));
    }

    [Fact]
    public void IsOlder_false_when_installed_equals_candidate()
    {
        Assert.False(Vercmp.IsOlder("1.0", "1.0"));
    }

    [Fact]
    public void IsOlder_false_when_installed_above_candidate()
    {
        if (!HasVercmp) return;
        Assert.False(Vercmp.IsOlder("2.0", "1.9"));
    }
}
