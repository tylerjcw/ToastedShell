using System.Runtime.CompilerServices;

namespace Tosh.Tests;

/// <summary>
/// Process-wide environment the test suite needs before any test spawns a child.
/// </summary>
/// <remarks>
/// <para>
/// `PLAN-0002`. Seven test files shell out to `dotnet build`, `publish` or `pack` — 33
/// invocations in total — and MSBuild keeps its worker nodes alive afterwards so the
/// next build starts faster. Over a session that accumulates: **110 idle nodes holding
/// 19.4 GB** were found resident, none of them doing anything, and
/// `dotnet build-server shutdown` does not reclaim them.
/// </para>
/// <para>
/// Measured. A full suite run used to leave **26 nodes holding 4.21 GB** behind; with
/// this set it leaves **zero**. Peak memory during an unlimited-thread run falls from
/// ~8.9 GB to ~4.5 GB, because much of that figure was nodes piling up as the run
/// proceeded rather than the tests themselves.
/// </para>
/// <para>
/// Set here rather than in each helper because child processes inherit the parent's
/// environment: one assignment covers every spawn site, including ones added later that
/// would not think to opt in. <c>ToshSdkBuildTests</c> already set it on its own
/// `ProcessStartInfo` and was the only file that did, which is exactly the kind of
/// per-site knowledge that goes stale.
/// </para>
/// <para>
/// This is a lead on `TS-P2-38`, the unexplained memory exhaustion that the suite was
/// investigated for and cleared of. The suite genuinely is not the cause — it peaks
/// around 2.8 GB at eight threads. But repeated builds each leaving gigabytes of idle
/// nodes behind, never reclaimed, grows without bound in a way that matches the
/// symptom, and nothing was looking at it.
/// </para>
/// </remarks>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
        Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
    }
}
