using System.Runtime.InteropServices;
using System.Text.Json;
using Tosh.Compiler;

namespace Tosh.Tests;

/// <summary>
/// The runtimeconfig a compiled program is given.
/// </summary>
/// <remarks>
/// <para>
/// It has one job: name a framework the host will actually start the program on. That is
/// easy to get subtly wrong, because <see cref="Environment.Version"/> — the obvious source
/// — drops the prerelease label, reporting <c>11.0.0</c> on a runtime calling itself
/// <c>11.0.0-rc.1.26425.128</c>. The host then finds that runtime, refuses to use it, and
/// exits 150.
/// </para>
/// <para>
/// No roll-forward setting rescues it: <c>11.0.0-rc.1</c> sorts <em>below</em> <c>11.0.0</c>,
/// so reaching it would mean rolling backwards.
/// </para>
/// <para>
/// These assertions are quiet on a released runtime, where the label is empty and both
/// sources agree. They have teeth exactly when it matters.
/// </para>
/// </remarks>
public sealed class ToshPublisherRuntimeConfigTests
{
    /// <summary>The version asked for is the version that is running, label and all.</summary>
    [Fact]
    public void The_framework_version_is_the_one_the_host_reports()
        => Assert.Equal(
            RuntimeInformation.FrameworkDescription,
            $".NET {ToshPublisher.RuntimeFrameworkVersion}");

    /// <summary>
    /// A prerelease runtime is named as a prerelease.
    /// </summary>
    /// <remarks>
    /// The regression test proper. Reverting to <see cref="Environment.Version"/> passes
    /// every other assertion here and fails this one — on the runtime where it counts.
    /// </remarks>
    [Fact]
    public void A_prerelease_label_is_not_dropped()
    {
        var description = RuntimeInformation.FrameworkDescription;

        if (!description.Contains('-', StringComparison.Ordinal))
        {
            // A released runtime has no label to drop.
            return;
        }

        Assert.Contains("-", ToshPublisher.RuntimeFrameworkVersion, StringComparison.Ordinal);
        Assert.NotEqual(Environment.Version.ToString(), ToshPublisher.RuntimeFrameworkVersion);
    }

    /// <summary>The written document says what it is meant to say, and parses.</summary>
    [Fact]
    public void The_written_config_names_the_framework_and_the_running_version()
    {
        using var document = JsonDocument.Parse(ToshPublisher.RuntimeConfigJson());

        var framework = document.RootElement
            .GetProperty("runtimeOptions")
            .GetProperty("framework");

        Assert.Equal("Microsoft.NETCore.App", framework.GetProperty("name").GetString());
        Assert.Equal(ToshPublisher.RuntimeFrameworkVersion, framework.GetProperty("version").GetString());
        Assert.Equal(
            $"net{Environment.Version.Major}.0",
            document.RootElement.GetProperty("runtimeOptions").GetProperty("tfm").GetString());
    }

    /// <summary>It is written beside the assembly, named after it.</summary>
    [Fact]
    public void The_config_is_written_beside_the_assembly()
    {
        var directory = Directory.CreateTempSubdirectory("tosh-runtimeconfig-");

        try
        {
            var assemblyPath = Path.Combine(directory.FullName, "Example.dll");
            var written = ToshPublisher.WriteRuntimeConfig(assemblyPath);

            Assert.Equal(Path.Combine(directory.FullName, "Example.runtimeconfig.json"), written);
            Assert.Equal(ToshPublisher.RuntimeConfigJson(), File.ReadAllText(written));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
