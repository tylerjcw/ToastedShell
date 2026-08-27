using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The language does not depend on the shell's command library.
///
/// `TOAST-0004`, the first phase of the Tōast/TōSh separation. The whole coupling was
/// one type at two call sites: the background-job path tested for
/// `Tosh.Stdlib.ExternalProcessCommand` to get a resolved path, and command resolution
/// constructed one. Measured by deleting `using Tosh.Stdlib;` from `ToshEngine.cs` and
/// compiling — two errors, both that type.
///
/// Both are now abstractions the runtime owns: `IExternalProcessCommand` for "is this a
/// program, and where does it live", and `IExternalCommandFactory` for "make me one".
/// The shell registers the factory alongside its command set.
///
/// The load-bearing test here is the reference tripwire. The rest could pass with the
/// dependency quietly restored.
///
/// **Which of these are controls.** Reverting the change — restoring the project
/// reference and both call sites — fails exactly two:
/// `The_language_assembly_does_not_reference_the_shell_command_library` and
/// `A_host_with_no_launcher_reports_that_it_cannot_run_programs`. The other three pass
/// against both versions and are regression guards rather than controls, which is
/// correct: they assert that behaviour did *not* change. Recorded so nobody later reads
/// five passing tests as five pieces of evidence for the inversion.
/// </summary>
public class ExternalCommandBoundaryTests
{
    /// <summary>
    /// The invariant, stated where a build can enforce it: the language assembly must
    /// not reference the shell's command library.
    ///
    /// This is the check the item is verified by, and it belongs in the suite rather
    /// than in a reviewer's memory — a single `using` restores the dependency, and
    /// nothing else here would notice.
    /// </summary>
    [Fact]
    public void The_language_assembly_does_not_reference_the_shell_command_library()
    {
        var referenced = typeof(ToshEngine).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.DoesNotContain("Tosh.Stdlib", referenced);
    }

    /// <summary>
    /// A host that does not launch processes says so, rather than failing with a null
    /// reference. Embedding Tōast to evaluate expressions is a legitimate
    /// configuration, and this is what makes the factory a registered capability
    /// instead of an assumed one.
    /// </summary>
    [Fact]
    public async Task A_host_with_no_launcher_reports_that_it_cannot_run_programs()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.ExternalCommands = null;

        var engine = new ToshEngine(runtime.Language);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("git --version"));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.runtime.external_commands_unavailable", diagnostic.Code);

        // The message has to name the way out, because the reader is an embedder who
        // has not registered something they did not know existed.
        Assert.Contains("Tosh.Stdlib", diagnostic.Help, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control for the test above: with the shell present — which is how
    /// <see cref="ToshRuntime.CreateDefault"/> leaves it — a program on `PATH` still
    /// resolves and runs.
    /// </summary>
    /// <remarks>
    /// Read through command substitution rather than a captured writer: an external
    /// process inherits stdio and writes to the real console, so a `StringWriter` on the
    /// runtime stays empty even when the command ran perfectly.
    /// </remarks>
    [Fact]
    public async Task An_external_command_still_resolves_and_runs()
    {
        if (!OperatingSystem.IsLinux()) return;

        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("$(/usr/bin/echo boundary-intact)");

        Assert.Equal("boundary-intact", results[^1]?.ToString());
    }

    /// <summary>
    /// `CreateDefault` registers a launcher, and it produces something the language
    /// recognises through the runtime's interface rather than through the shell's type.
    /// Both halves matter: registration happening, and the result satisfying the
    /// contract the background-job path tests for.
    /// </summary>
    [Fact]
    public void The_default_runtime_registers_a_launcher_producing_the_runtime_contract()
    {
        var runtime = ToshRuntime.CreateDefault();

        Assert.NotNull(runtime.ExternalCommands);

        var command = runtime.ExternalCommands!.CreateExternalProcess("echo", "/usr/bin/echo");

        Assert.Equal("echo", command.Name);
        Assert.Equal("/usr/bin/echo", command.ResolvedPath);
    }

    /// <summary>
    /// The background-job path — site one of the two, and the reason
    /// `IExternalProcessCommand` exposes a resolved path at all. A job specification is
    /// built from that path, so if the type test stopped matching, backgrounding an
    /// external program would report that it is not external.
    /// </summary>
    /// <remarks>
    /// Backgrounding yields no value, so the assertion is on the diagnostic *not*
    /// raised. If the type test stopped matching, this would throw
    /// `tosh.runtime.background_command_must_be_external` — asserting on a returned
    /// value would have tested nothing, since there is none either way.
    /// </remarks>
    [Fact]
    public async Task An_external_program_can_still_be_backgrounded()
    {
        if (!OperatingSystem.IsLinux()) return;

        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync("/usr/bin/sleep 0.2 &");

        var jobs = await engine.ExecuteToListAsync("jobs");
        Assert.NotEmpty(jobs);
    }
}
