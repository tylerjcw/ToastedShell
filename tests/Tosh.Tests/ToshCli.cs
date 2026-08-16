using System.Reflection;

namespace Tosh.Tests;

/// <summary>
/// Locates the repository's own <c>Tosh.Cli</c> build output for tests that spawn it
/// as a child process.
///
/// `PLAN-0002`. Six sites across five files hard-coded <c>bin/Debug/net10.0</c>, so a
/// suite run under Release looked for a binary that configuration had never produced.
/// Fifteen tests failed — across `TtyCaptureTests`, `ExecCommandTests`,
/// `ExtensionCommandTests`, `ToshSdkBuildTests` and `EngineTests` — with a message
/// naming a missing file rather than a missing step.
///
/// **The symptom looked like flakiness, which is why this is worth a comment.** The
/// suite was reported as failing spuriously in 3 of 8 runs on an unchanged tree, in
/// unrelated areas. It was not spurious: those tests fail exactly when the Debug CLI is
/// absent, and something else in the suite eventually builds one, after which they pass
/// until that output is cleaned. Whether the suite passed depended on out-of-band build
/// state rather than on the commit — which is the property that makes a suite useless as
/// evidence.
///
/// The configuration is read from the test assembly's own
/// <see cref="AssemblyConfigurationAttribute"/>, which the SDK emits, so a Release run
/// looks for a Release CLI and a Debug run for a Debug one. Building the solution
/// already produces it; no separate step is needed, which is why this reports rather
/// than builds.
/// </summary>
internal static class ToshCli
{
    /// <summary>
    /// The configuration this test assembly was compiled in — the same one the CLI
    /// will have been built into by the same `dotnet build`.
    /// </summary>
    internal static string Configuration { get; } =
        typeof(ToshCli).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
        ?? "Debug";

    /// <summary>
    /// Repository root, five levels above the test output directory
    /// (<c>tests/Tosh.Tests/bin/&lt;config&gt;/net10.0</c>).
    /// </summary>
    internal static string RepositoryRoot { get; } =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static string OutputDirectory =>
        Path.Combine(RepositoryRoot, "src", "Tosh.Cli", "bin", Configuration, "net10.0");

    /// <summary>The native launcher — <c>Tosh.Cli</c>, or <c>Tosh.Cli.exe</c> on Windows.</summary>
    internal static string ExecutablePath =>
        Path.Combine(OutputDirectory, OperatingSystem.IsWindows() ? "Tosh.Cli.exe" : "Tosh.Cli");

    /// <summary>The managed assembly, for tests that load it rather than run it.</summary>
    internal static string AssemblyPath => Path.Combine(OutputDirectory, "Tosh.Cli.dll");

    /// <summary>
    /// Asserts the CLI exists, naming the command that produces it.
    /// </summary>
    /// <remarks>
    /// The message is the point. "CLI not built at &lt;path&gt;" sends a reader looking
    /// for a broken path; naming the build command and the configuration tells them
    /// what to actually do, which matters most on a clean checkout where this is the
    /// first thing to fail.
    /// </remarks>
    internal static void RequireBuilt(string path)
    {
        Assert.True(
            File.Exists(path),
            $"""
            Tosh.Cli was not found at:
                {path}

            This test spawns the CLI as a child process, and looks for it in the
            configuration these tests were built in ({Configuration}). Build the
            solution first, in the same configuration:

                dotnet build -c {Configuration}
                dotnet test --no-build -c {Configuration}
            """);
    }
}
