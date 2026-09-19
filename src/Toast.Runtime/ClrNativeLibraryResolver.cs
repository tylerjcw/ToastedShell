using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Tosh.Runtime;

/// <summary>
/// Finds the native libraries a loaded assembly needs, where NuGet put them.
/// </summary>
/// <remarks>
/// <para>
/// A package that ships native code lays it out as
/// <c>runtimes/&lt;rid&gt;/native/libFoo.so</c>, and an ordinary .NET application finds it
/// because its <c>.deps.json</c> says where to look. An assembly loaded at runtime has no
/// such file read for it, so the runtime probes only beside the assembly and in its own
/// directory — and a package like SkiaSharp fails with "Unable to load shared library", the
/// library sitting unexamined two directories down.
/// </para>
/// <para>
/// <c>LD_LIBRARY_PATH</c> does not help: the probe paths the runtime reports are the ones it
/// will use, and the package layout is not among them. Copying the <c>.so</c> up beside the
/// managed assembly does work, which is the workaround this removes the need for.
/// </para>
/// <para>
/// The requesting assembly decides where to look, so this needs no registry of directories:
/// a library is searched for under the assembly that asked for it and nowhere else, and an
/// assembly with no location on disk is left alone.
/// </para>
/// </remarks>
public static class ClrNativeLibraryResolver
{
    private static int _registered;

    /// <summary>
    /// Starts resolving native libraries out of package layouts, once per process.
    /// </summary>
    /// <remarks>
    /// Called when an assembly is loaded from disk rather than at startup: until something
    /// has been loaded there is nothing this could resolve for, and a handler that never runs
    /// is better not installed.
    /// </remarks>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += Resolve;
        }
    }

    private static IntPtr Resolve(Assembly assembly, string name)
    {
        // An assembly loaded from bytes has no location, and nothing to probe relative to.
        if (string.IsNullOrEmpty(assembly.Location))
        {
            return IntPtr.Zero;
        }

        var directory = Path.GetDirectoryName(assembly.Location);

        if (string.IsNullOrEmpty(directory))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in ProbePaths(directory, name))
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// The package-layout paths this library would be loaded from, in the order tried.
    /// </summary>
    /// <remarks>
    /// The exact runtime identifier first, then the portable one — a package may ship
    /// <c>linux-x64</c>, or just <c>linux</c> for something architecture-independent. Both
    /// spellings of the file name are tried because a <c>DllImport</c> names the library
    /// without its prefix or extension and the file on disk has both.
    ///
    /// Only paths that exist are returned, so an empty result says the layout holds nothing
    /// by that name — which is the question worth asking when a load has failed.
    /// </remarks>
    public static IEnumerable<string> ProbePaths(string directory, string name)
    {
        foreach (var rid in RuntimeIdentifiers())
        {
            var native = Path.Combine(directory, "runtimes", rid, "native");

            if (!Directory.Exists(native))
            {
                continue;
            }

            foreach (var fileName in FileNames(name))
            {
                var path = Path.Combine(native, fileName);

                if (File.Exists(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> RuntimeIdentifiers()
    {
        yield return RuntimeInformation.RuntimeIdentifier;

        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        if (OperatingSystem.IsLinux())
        {
            yield return $"linux-{architecture}";
            yield return "linux";
            yield return "unix";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return $"osx-{architecture}";
            yield return "osx";
            yield return "unix";
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return $"win-{architecture}";
            yield return "win";
        }
    }

    private static IEnumerable<string> FileNames(string name)
    {
        yield return name;

        if (OperatingSystem.IsWindows())
        {
            yield return $"{name}.dll";
            yield break;
        }

        var extension = OperatingSystem.IsMacOS() ? ".dylib" : ".so";

        yield return $"{name}{extension}";
        yield return $"lib{name}{extension}";
        yield return $"lib{name}";
    }
}
