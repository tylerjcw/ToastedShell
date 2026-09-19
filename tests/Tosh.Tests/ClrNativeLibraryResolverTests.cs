using System.Runtime.InteropServices;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Finding the native libraries a loaded assembly needs, where NuGet put them.
/// </summary>
/// <remarks>
/// <para>
/// A package shipping native code lays it out as <c>runtimes/&lt;rid&gt;/native/libFoo.so</c>,
/// and an ordinary application finds it because its <c>.deps.json</c> says where to look.
/// Nothing reads such a file for an assembly loaded at runtime, so the library sat
/// unexamined two directories below a "Unable to load shared library 'foo'" — with the probe
/// list in that message naming every directory except the one it was in.
/// </para>
/// </remarks>
public sealed class ClrNativeLibraryResolverTests
{
    private static string NativeFileName(string name) =>
        OperatingSystem.IsWindows() ? $"{name}.dll"
        : OperatingSystem.IsMacOS() ? $"lib{name}.dylib"
        : $"lib{name}.so";

    private static DirectoryInfo Layout(string rid, string name)
    {
        var root = Directory.CreateTempSubdirectory("tosh-native-probe-");
        var native = Path.Combine(root.FullName, "runtimes", rid, "native");

        Directory.CreateDirectory(native);
        File.WriteAllText(Path.Combine(native, NativeFileName(name)), "not a real library");

        return root;
    }

    /// <summary>A library in the package layout is found.</summary>
    [Fact]
    public void A_library_under_the_exact_runtime_identifier_is_found()
    {
        var root = Layout(RuntimeInformation.RuntimeIdentifier, "toshprobe");

        try
        {
            var found = ClrNativeLibraryResolver.ProbePaths(root.FullName, "toshprobe").ToArray();

            Assert.NotEmpty(found);
            Assert.All(found, path => Assert.True(File.Exists(path)));
            Assert.Contains(found, path => path.Contains("native", StringComparison.Ordinal));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A portable identifier is tried too.
    /// </summary>
    /// <remarks>
    /// A package may ship <c>linux-x64</c>, or just <c>linux</c> where the binary does not
    /// depend on the architecture. Matching only the exact triple would miss the second.
    /// </remarks>
    [Fact]
    public void A_library_under_a_portable_identifier_is_found()
    {
        var portable =
            OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux";

        var root = Layout(portable, "toshprobe");

        try
        {
            Assert.NotEmpty(ClrNativeLibraryResolver.ProbePaths(root.FullName, "toshprobe"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A name the layout does not hold returns nothing.
    /// </summary>
    /// <remarks>
    /// Only existing paths are returned, so an empty result answers the question worth asking
    /// when a load has failed: whether the layout holds anything by that name at all.
    /// </remarks>
    [Fact]
    public void A_library_that_is_not_there_yields_nothing()
    {
        var root = Layout(RuntimeInformation.RuntimeIdentifier, "toshprobe");

        try
        {
            Assert.Empty(ClrNativeLibraryResolver.ProbePaths(root.FullName, "somethingelse"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>A directory with no package layout at all is not a failure.</summary>
    [Fact]
    public void A_directory_without_a_runtimes_folder_yields_nothing()
    {
        var root = Directory.CreateTempSubdirectory("tosh-native-empty-");

        try
        {
            Assert.Empty(ClrNativeLibraryResolver.ProbePaths(root.FullName, "toshprobe"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
