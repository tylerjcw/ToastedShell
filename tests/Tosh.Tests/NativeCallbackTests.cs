using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A <c>raw callback</c> is the inbound half of native interop: ToSh code
/// called <em>by</em> C, rather than C called by ToSh.
///
/// Two rules carry most of the weight here. A callback must produce exactly one
/// value, because the alternative — silently picking one from a stream — turns
/// a mistake in a comparator into a mis-sorted array with no error anywhere.
/// And a callback arriving on a foreign thread must not touch the engine, whose
/// scope stack is a plain <c>Stack</c>; that case is recorded and rethrown on
/// managed ground rather than thrown across native frames.
/// </summary>
public class NativeCallbackTests
{
    private static ToshEngine NewEngine() => new(ToshRuntime.CreateDefault().Language);

    private static bool SkipOffLinux => !OperatingSystem.IsLinux();

    private const string QsortPreamble =
        """
        raw callback Comparator(a: ptr, b: ptr) -> int
        bind native "libc.so.6" as LibC {
            func qsort(base: ptr, count: nuint, size: nuint, compare: Comparator) -> void
        }
        func fill(buffer, items) {
            var offset = 0
            for v in ($items) {
                native-write $buffer $v --at $offset
                $offset = $offset + 4
            }
        }
        func dump(buffer, count) {
            var out = []
            for i in (0..($count - 1)) {
                $out = $out + [(native-read int32 $buffer --at ($i * 4))]
            }
            return $out
        }
        """;

    /// <summary>
    /// The acceptance case: C's qsort calls back into ToSh for every
    /// comparison, and the array comes back ordered.
    /// </summary>
    [Fact]
    public async Task A_tosh_comparator_drives_qsort()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            QsortPreamble +
            """

            func ascending(a, b) {
                return (native-read int32 $a) - (native-read int32 $b)
            }
            alloc buf = 16
            fill($buf, [4, 2, 3, 1])
            LibC.qsort($buf, 4, 4, &ascending)
            var sorted = dump($buf, 4)
            native-free $buf
            $sorted | join ","
            """);

        Assert.Equal("1,2,3,4", Assert.IsType<string>(Assert.Single(results)));
    }

    /// <summary>
    /// The comparator is ordinary ToSh, so reversing the sort is a change of
    /// function rather than a change of binding.
    /// </summary>
    [Fact]
    public async Task The_comparator_decides_the_order()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            QsortPreamble +
            """

            func descending(a, b) {
                return (native-read int32 $b) - (native-read int32 $a)
            }
            alloc buf = 16
            fill($buf, [4, 2, 3, 1])
            LibC.qsort($buf, 4, 4, &descending)
            var sorted = dump($buf, 4)
            native-free $buf
            $sorted | join ","
            """);

        Assert.Equal("4,3,2,1", Assert.IsType<string>(Assert.Single(results)));
    }

    /// <summary>
    /// Several yielded values is an error, not a silent pick. `writeline` would
    /// not trigger this — it prints rather than yielding — so the body here
    /// uses bare expressions, which do.
    /// </summary>
    [Fact]
    public async Task A_callback_yielding_several_values_is_an_error()
    {
        if (SkipOffLinux) return;

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => NewEngine().ExecuteToListAsync(
                QsortPreamble +
                """

                func chatty(a, b) {
                    -1
                    1
                }
                alloc buf = 16
                fill($buf, [4, 2, 3, 1])
                LibC.qsort($buf, 4, 4, &chatty)
                """));

        Assert.Contains("exactly one value", exception.Message);
        Assert.Contains("produced 2", exception.Message);
    }

    /// <summary>
    /// Yielding nothing is the same error from the other side: a non-void
    /// callback that produces no value has not answered the question C asked.
    /// </summary>
    [Fact]
    public async Task A_callback_yielding_nothing_is_an_error()
    {
        if (SkipOffLinux) return;

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => NewEngine().ExecuteToListAsync(
                QsortPreamble +
                """

                func silent(a, b) { }
                alloc buf = 16
                fill($buf, [4, 2, 3, 1])
                LibC.qsort($buf, 4, 4, &silent)
                """));

        Assert.Contains("exactly one value", exception.Message);
        Assert.Contains("produced 0", exception.Message);
    }

    /// <summary>
    /// The diagnostic names the callback and points at the argument, rather
    /// than failing later inside the marshaller.
    /// </summary>
    [Fact]
    public async Task A_non_function_argument_is_rejected_at_the_call_site()
    {
        if (SkipOffLinux) return;

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => NewEngine().ExecuteToListAsync(
                QsortPreamble +
                """

                alloc buf = 16
                LibC.qsort($buf, 4, 4, 12345)
                """));

        Assert.Contains("Comparator", exception.Message);
    }

    /// <summary>
    /// A callback stored by the callee outlives the call that registered it.
    /// The forced collection is the point of the test: an unrooted thunk would
    /// be freed here and GLFW would call into freed memory.
    /// </summary>
    [Fact]
    public async Task A_stored_callback_survives_a_collection()
    {
        if (SkipOffLinux) return;
        if (!File.Exists("/usr/lib/libglfw.so.3")) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            raw callback ErrorHandler(code: int, description: cstring) -> void
            bind native "libglfw.so.3" as GLFW {
                func glfwSetErrorCallback(handler: ErrorHandler) -> ptr
                func glfwGetPrimaryMonitor() -> ptr
            }
            var seen = 0
            func on_error(code, description) { $seen = $seen + 1 }
            GLFW.glfwSetErrorCallback(&on_error)
            System.GC.Collect()
            System.GC.WaitForPendingFinalizers()
            System.GC.Collect()
            GLFW.glfwGetPrimaryMonitor()
            $seen
            """);

        Assert.Equal(1, Assert.IsType<int>(results[^1]));
    }

    /// <summary>
    /// A failing <c>void</c> callback must surface as an exception, not kill the
    /// process.
    ///
    /// The value returned to native code when a callback cannot run came from
    /// <c>NativeInteropUtilities.CreateDefaultValue</c>, which for <c>void</c>
    /// reaches <c>Activator.CreateInstance(typeof(void))</c> and throws
    /// <c>NotSupportedException</c> — from inside the catch block whose whole
    /// purpose is to stop an exception escaping into native frames. The process
    /// died with an unhandled exception on a native stack rather than reporting
    /// anything.
    /// </summary>
    [Fact]
    public async Task A_failing_void_callback_reports_instead_of_crashing()
    {
        if (SkipOffLinux) return;
        if (!File.Exists("/usr/lib/libglfw.so.3")) return;

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => NewEngine().ExecuteToListAsync(
                """
                raw callback ErrorHandler(code: int, description: cstring) -> void
                bind native "libglfw.so.3" as GLFW {
                    func glfwSetErrorCallback(handler: ErrorHandler) -> ptr
                    func glfwGetPrimaryMonitor() -> ptr
                }
                func on_error(code, description) { throw new Error("handler exploded") }
                GLFW.glfwSetErrorCallback(&on_error)
                GLFW.glfwGetPrimaryMonitor()
                """));

        Assert.Contains("handler exploded", exception.Message);
    }

    /// <summary>
    /// A <c>cstring</c> parameter reaches the body as a string, not a pointer
    /// the callback would have to decode itself.
    /// </summary>
    [Fact]
    public async Task A_cstring_parameter_arrives_decoded()
    {
        if (SkipOffLinux) return;
        if (!File.Exists("/usr/lib/libglfw.so.3")) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            raw callback ErrorHandler(code: int, description: cstring) -> void
            bind native "libglfw.so.3" as GLFW {
                func glfwSetErrorCallback(handler: ErrorHandler) -> ptr
                func glfwGetPrimaryMonitor() -> ptr
            }
            var message = ""
            func on_error(code, description) { $message = $description }
            GLFW.glfwSetErrorCallback(&on_error)
            GLFW.glfwGetPrimaryMonitor()
            $message
            """);

        Assert.Contains("not initialized", Assert.IsType<string>(results[^1]));
    }

    /// <summary>
    /// `ok` and `count` decide whether a native call <em>failed</em>. A
    /// callback's return is a value it produces, so the convention would
    /// silently do nothing — it is rejected at the declaration instead.
    /// </summary>
    [Fact]
    public async Task A_callback_cannot_declare_a_success_convention()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => NewEngine().ExecuteToListAsync(
                """
                raw callback Handler(code: int) -> ok
                """));

        Assert.Contains("success convention", exception.Message);
    }

    /// <summary>
    /// A by-reference callback parameter would need writing back into the
    /// caller's memory after the body ran, which has no design yet. Rejected
    /// rather than silently ignored.
    /// </summary>
    [Fact]
    public async Task A_callback_cannot_take_a_by_reference_parameter()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => NewEngine().ExecuteToListAsync(
                """
                raw callback Handler(out value: int) -> void
                """));

        Assert.Contains("by-reference parameter", exception.Message);
    }

    /// <summary>
    /// The declaration is introspectable like any other named type, so a
    /// callback's shape can be inspected without reading the source.
    /// </summary>
    [Fact]
    public async Task A_callback_type_reports_its_signature()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            raw callback Comparator(a: ptr, b: ptr) -> int
            (describe-type Comparator).Name
            """);

        Assert.Equal("Comparator", Assert.IsType<string>(results[^1]));
    }
}
