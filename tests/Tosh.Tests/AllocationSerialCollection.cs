using Xunit;

namespace Tosh.Tests;

/// <summary>
/// Keeps allocation measurement from overlapping the rest of the suite.
/// </summary>
/// <remarks>
/// <c>GC.GetTotalAllocatedBytes</c> is process-wide, so a test that measures it is measuring
/// every other test allocating at the same moment too. Run inside the parallel suite, the
/// <c>TOAST-0009</c> budget read 4,863 bytes per iteration against 2,520 when run alone — noise
/// nearly twice the size of the signal.
///
/// Allocation is deterministic in a dedicated process, which is where the bench harness runs and
/// where that reputation comes from. In a shared test host it is not, and assuming otherwise is
/// how a budget becomes flaky.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationSerialCollection
{
    public const string Name = "AllocationSerial";
}
