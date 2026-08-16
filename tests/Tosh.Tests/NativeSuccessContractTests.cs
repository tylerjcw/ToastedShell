using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The success contract and the result projection are two orthogonal things:
/// `-&gt; ok` / `-&gt; count` / `where (…)` decide whether a call throws, and the
/// projection decides what a successful call yields. Conflating them is what
/// previously made the projection an unnameable side effect.
/// </summary>
public class NativeSuccessContractTests
{
    private static ToshEngine NewEngine() => new(ToshRuntime.CreateDefault());

    private static bool SkipOffLinux => !OperatingSystem.IsLinux();

    /// <summary>
    /// `-&gt; ok` consumes the return value as a pass/fail signal, so the single
    /// out parameter becomes the result — `uname()` yields a UtsName, not a
    /// record wrapping one.
    /// </summary>
    [Fact]
    public async Task Ok_convention_unwraps_the_single_out_parameter()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            raw struct UtsName {
                sysname:    cstring[65]
                nodename:   cstring[65]
                release:    cstring[65]
                version:    cstring[65]
                machine:    cstring[65]
                domainname: cstring[65]
            }
            bind native "libc.so.6" as LibC {
                func uname(out buf: UtsName) -> ok
            }
            (LibC.uname()).sysname
            """);

        Assert.Equal("Linux", Assert.IsType<string>(Assert.Single(results)));
    }

    /// <summary>
    /// A failing `-&gt; ok` call throws with errno captured. `chdir` to a path
    /// that cannot exist is a reliable ENOENT.
    /// </summary>
    [Fact]
    public async Task Ok_convention_throws_with_errno_on_failure()
    {
        if (SkipOffLinux) return;

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => NewEngine().ExecuteToListAsync(
                """
                bind native "libc.so.6" as LibC {
                    func chdir(path: string) -> ok
                }
                LibC.chdir("/nonexistent-path-for-tosh-tests")
                """));

        var diagnostic = Assert.Single(exception.Diagnostics);
        // `TS-P3-25`. The code is a stable identifier now — it used to be built from
        // the library path and symbol, which made it differ per machine for the same
        // failure. Both are still named, in the places a reader looks.
        Assert.Equal("tosh.native.call_failed", diagnostic.Code);
        Assert.Contains("chdir failed", diagnostic.Title);
        Assert.Contains("ENOENT", diagnostic.Help);
        Assert.Contains("chdir", diagnostic.Help);
        Assert.Contains("libc.so.6", diagnostic.Help);
        Assert.Contains("returned -1", diagnostic.Label);
    }

    /// <summary>
    /// `-&gt; count` treats a non-negative return as both success and the value.
    /// </summary>
    [Fact]
    public async Task Count_convention_returns_the_value()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            bind native "libc.so.6" as LibC {
                func sysconf(name: int) -> count
            }
            LibC.sysconf(30)
            """);

        // _SC_PAGESIZE
        Assert.Equal(Environment.SystemPageSize, Convert.ToInt32(Assert.Single(results)));
    }

    [Fact]
    public async Task Count_convention_throws_on_negative()
    {
        if (SkipOffLinux) return;

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => NewEngine().ExecuteToListAsync(
                """
                bind native "libc.so.6" as LibC {
                    func sysconf(name: int) -> count
                }
                LibC.sysconf(-999)
                """));

        Assert.Equal("tosh.native.call_failed", Assert.Single(exception.Diagnostics).Code);
    }

    /// <summary>
    /// The payoff of naming the conventions rather than writing
    /// `where (_ >= 0)`: readlink(2) does not NUL-terminate its output, so a
    /// pass/fail predicate could not know where the string ends. `count` does,
    /// and the projection truncates to exactly that length.
    /// </summary>
    [Fact]
    public async Task Count_supplies_the_true_length_of_a_buffer()
    {
        if (SkipOffLinux) return;

        var expected = Path.GetFullPath("/proc/self/exe");
        var actualTarget = File.ResolveLinkTarget(expected, returnFinalTarget: true)?.FullName;

        var results = await NewEngine().ExecuteToListAsync(
            """
            bind native "libc.so.6" as LibC {
                func readlink(path: string, out buf: buffer[4096]) -> count
            }
            LibC.readlink("/proc/self/exe")
            """);

        var link = Assert.IsType<string>(Assert.Single(results));

        Assert.False(string.IsNullOrWhiteSpace(link));
        Assert.DoesNotContain('\0', link);
        Assert.StartsWith("/", link);

        if (actualTarget is not null)
        {
            Assert.Equal(actualTarget, link);
        }
    }

    /// <summary>
    /// `where (…)` reuses the refinement vocabulary — same keyword, same `_`
    /// placeholder, same meaning — with `_` bound to the native return value.
    /// </summary>
    [Fact]
    public async Task Where_predicates_gate_success()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            bind native "libc.so.6" as LibC {
                func sysconf(name: int) -> long where (_ != -1)
            }
            LibC.sysconf(30)
            """);

        Assert.Equal(Environment.SystemPageSize, Convert.ToInt32(Assert.Single(results)));
    }

    [Fact]
    public async Task Where_predicates_throw_when_unsatisfied()
    {
        if (SkipOffLinux) return;

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => NewEngine().ExecuteToListAsync(
                """
                bind native "libc.so.6" as LibC {
                    func sysconf(name: int) -> long where (_ > 999999999)
                }
                LibC.sysconf(30)
                """));

        Assert.Equal("tosh.native.call_failed", Assert.Single(exception.Diagnostics).Code);
    }

    /// <summary>
    /// A typed `T[n]` out-parameter is engine-allocated and comes back as a real
    /// array. getloadavg is the canonical case, and it cross-checks against
    /// /proc/loadavg.
    /// </summary>
    [Fact]
    public async Task Typed_out_arrays_are_allocated_and_returned()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            bind native "libc.so.6" as LibC {
                func getloadavg(out avg: double[3], count: int) -> int where (_ == 3)
            }
            var l = LibC.getloadavg(3)
            $l[0]
            $l[1]
            $l[2]
            """);

        var reported = results.Select(Convert.ToDouble).ToArray();
        Assert.Equal(3, reported.Length);
        Assert.All(reported, value => Assert.True(value >= 0, "load averages are never negative"));

        var expected = File.ReadAllText("/proc/loadavg").Split(' ');

        // Load moves between the two reads, so allow generous slack — this is a
        // sanity check that the marshalling is right, not a timing assertion.
        Assert.True(
            Math.Abs(double.Parse(expected[0]) - reported[0]) < 10.0,
            $"getloadavg 1-minute {reported[0]} should be near /proc/loadavg {expected[0]}");
    }

    /// <summary>
    /// errno must be read before any other managed work, since the captured
    /// value is per-thread and the next P/Invoke overwrites it. A successful
    /// call between the failure and the read would otherwise clear it.
    /// </summary>
    [Fact]
    public async Task Errno_is_captured_per_call_not_globally()
    {
        if (SkipOffLinux) return;

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => NewEngine().ExecuteToListAsync(
                """
                bind native "libc.so.6" as LibC {
                    func sysconf(name: int) -> count
                    func chdir(path: string) -> ok
                }
                LibC.sysconf(30)
                LibC.chdir("/nonexistent-path-for-tosh-tests")
                """));

        // ENOENT from chdir, not whatever sysconf left behind.
        Assert.Contains("ENOENT", Assert.Single(exception.Diagnostics).Help);
    }

    /// <summary>
    /// A NativeError must reach `catch` as itself, not flattened into a
    /// ToshDiagnosticException. It is raised by the engine rather than a user
    /// `throw`, so it needs the same `tosh.thrown` marker that lets user-thrown
    /// values pass through the expression-level handlers untouched.
    /// </summary>
    [Fact]
    public async Task Native_errors_are_catchable_and_carry_errno()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            bind native "libc.so.6" as L { func chdir(path: string) -> ok }
            try {
                L.chdir("/nonexistent-path-for-tosh-tests")
            } catch (e) {
                type-of $e | get Name
                $e.Errno
                $e.ErrnoName
                $e.Symbol
                $e.Library
            }
            """);

        Assert.Equal("NativeError", Assert.IsType<string>(results[0]));
        Assert.Equal(2, Convert.ToInt32(results[1]));           // ENOENT
        Assert.Equal("ENOENT", Assert.IsType<string>(results[2]));
        Assert.Equal("chdir", Assert.IsType<string>(results[3]));
        Assert.Equal("libc.so.6", Assert.IsType<string>(results[4]));
    }

    /// <summary>
    /// Being catchable must not cost the uncaught rendering. The duck-typed
    /// user-error probe only reads tosh class instances, so a CLR exception
    /// subclass needs its own branch or it renders as a bare type name.
    /// </summary>
    [Fact]
    public async Task Uncaught_native_errors_still_render_the_full_contract()
    {
        if (SkipOffLinux) return;

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => NewEngine().ExecuteToListAsync(
                """
                bind native "libc.so.6" as L { func chdir(path: string) -> ok }
                echo before
                L.chdir("/nonexistent-path-for-tosh-tests")
                """));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.native.call_failed", diagnostic.Code);
        Assert.Contains("ENOENT", diagnostic.Help);
        Assert.Contains("returned -1", diagnostic.Label);

        // Underlines the failing call, not the start of the script — module and
        // class member invocations carry no CommandSpan of their own.
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span!.Value.Start > 0, "the span should point at the call site");
    }

    /// <summary>
    /// The neighbouring case, verified because an earlier diagnosis wrongly
    /// claimed it was broken: a user class that `extends Error` round-trips
    /// through `catch` with its own members intact.
    /// </summary>
    [Fact]
    public async Task User_error_classes_round_trip_through_catch()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            class MyErr extends Error {
                prop Code: string = "my.code"
                prop Detail: string = "extra"
            }
            try { throw (new MyErr()) } catch (e) {
                type-of $e | get Name
                $e.Code
                $e.Detail
            }
            """);

        Assert.Equal("MyErr", Assert.IsType<string>(results[0]));
        Assert.Equal("my.code", Assert.IsType<string>(results[1]));
        Assert.Equal("extra", Assert.IsType<string>(results[2]));
    }

    /// <summary>
    /// Unchecked calls keep the previous shape: a record with `ReturnValue`
    /// alongside the out parameters.
    /// </summary>
    [Fact]
    public async Task Unchecked_calls_still_yield_a_composite_record()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            bind native "libc.so.6" as LibC {
                func gettimeofday(out tv: Tosh.Tests.NativeTimeVal, nint) -> int
            }
            var r = LibC.gettimeofday(0)
            $r.ReturnValue
            $r.tv.tv_sec > 0
            """);

        Assert.Equal(0, Convert.ToInt32(results[0]));
        Assert.True(Assert.IsType<bool>(results[1]));
    }

    /// <summary>
    /// `TS-P2-88`. A `-&gt; ok` call with **no** out parameters must yield nothing.
    /// The return value was already spent deciding pass/fail, so emitting it too
    /// puts a status code into the pipeline: `chdir` produced a bare `0`, and a
    /// render loop calling `SDL_SetRenderDrawColor` printed a column of zeroes
    /// under the window. Every call site needed `| ignore` to stay quiet.
    /// </summary>
    [Fact]
    public async Task Ok_convention_with_no_out_parameters_yields_nothing()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            bind native "libc.so.6" as LibC {
                func chdir(path: cstring) -> ok
            }
            LibC.chdir("/tmp")
            """);

        Assert.Empty(results);
    }

    /// <summary>
    /// The rule is about the *convention*, not about having no out parameters, so
    /// the surrounding pipeline has to stay unpolluted rather than merely shorter.
    /// </summary>
    [Fact]
    public async Task A_zero_out_ok_call_does_not_interrupt_the_values_around_it()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            bind native "libc.so.6" as LibC {
                func chdir(path: cstring) -> ok
            }
            "before"
            LibC.chdir("/tmp")
            "after"
            """);

        Assert.Equal(["before", "after"], results.Select(v => v?.ToString()));
    }

    /// <summary>
    /// `count` is the deliberate exception and must keep yielding: the value is a
    /// genuine result that happens to double as the error signal. A fix that
    /// silenced every checked convention would pass the test above and break this.
    /// </summary>
    [Fact]
    public async Task Count_convention_with_no_out_parameters_still_yields_its_value()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            bind native "libc.so.6" as LibC {
                func sysconf(name: int) -> count
            }
            LibC.sysconf(30)
            """);

        Assert.True(Convert.ToInt64(Assert.Single(results)) > 0);
    }

    /// <summary>
    /// An unchecked call is untouched — it has no success contract to spend its
    /// return value on, so the value is the result.
    /// </summary>
    [Fact]
    public async Task An_unchecked_zero_out_call_still_yields_its_return_value()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            bind native "libc.so.6" as LibC {
                func strlen(s: cstring) -> int
            }
            LibC.strlen("hello")
            """);

        Assert.Equal(5, Convert.ToInt32(Assert.Single(results)));
    }
}
