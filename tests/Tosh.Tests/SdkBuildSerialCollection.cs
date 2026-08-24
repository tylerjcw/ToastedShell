using Xunit;

namespace Tosh.Tests;

/// <summary>
/// Keeps SDK build, pack, publish, and launch subprocesses from overlapping the
/// rest of the test suite.
/// </summary>
/// <remarks>
/// The packaged-SDK single-file launch has twice failed only under full-suite
/// load: once with SIGABRT and once with Linux <c>ETXTBSY</c>. The latter proves
/// that another process retained a write-open executable inode. Isolating this
/// collection removes that cross-test process contention while retaining the
/// end-to-end coverage.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SdkBuildSerialCollection
{
    public const string Name = "SdkBuildSerial";
}
