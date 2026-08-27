using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// `raw struct` is the keystone of fluent native interop: without it, every
/// libc entry point that takes a struct pointer requires hand-computed byte
/// offsets, and inline char buffers (`struct utsname`) are not expressible at
/// all.
///
/// The layout numbers live here rather than in the declarations, which is the
/// whole point — a declaration transcribes its man page and nothing else, and
/// these tests carry the arithmetic.
/// </summary>
public class RawStructTests
{
    /// <summary>Linux `struct sysinfo` on x86-64. See sysinfo(2).</summary>
    private const string SysInfoDeclaration =
        """
        raw struct SysInfo {
            uptime:    long
            loads:     ulong[3]
            totalram:  ulong
            freeram:   ulong
            sharedram: ulong
            bufferram: ulong
            totalswap: ulong
            freeswap:  ulong
            procs:     ushort
            totalhigh: ulong
            freehigh:  ulong
            mem_unit:  uint
        }
        """;

    private static ToshEngine NewEngine() => new(ToshRuntime.CreateDefault().Language);

    /// <summary>
    /// The load-bearing assertion of the whole design. No padding is declared —
    /// the C header's `unsigned short pad` is deliberately absent — so
    /// `totalhigh` landing at 88 rather than 82 proves sequential layout applied
    /// natural alignment exactly as a C compiler does. If that were wrong,
    /// every field after `procs` would silently read the wrong bytes.
    /// </summary>
    [Fact]
    public async Task Sequential_layout_aligns_fields_without_declared_padding()
    {
        var results = await NewEngine().ExecuteToListAsync(
            SysInfoDeclaration +
            """

            size-of SysInfo
            offset-of SysInfo uptime
            offset-of SysInfo loads
            offset-of SysInfo totalram
            offset-of SysInfo freeram
            offset-of SysInfo procs
            offset-of SysInfo totalhigh
            offset-of SysInfo mem_unit
            """);

        Assert.Equal(112, Convert.ToInt32(results[0]));   // incl. 4 bytes tail padding
        Assert.Equal(0L, Convert.ToInt64(results[1]));
        Assert.Equal(8L, Convert.ToInt64(results[2]));    // ulong[3] sits inline: 24 bytes
        Assert.Equal(32L, Convert.ToInt64(results[3]));
        Assert.Equal(40L, Convert.ToInt64(results[4]));
        Assert.Equal(80L, Convert.ToInt64(results[5]));   // ushort, ends at 82
        Assert.Equal(88L, Convert.ToInt64(results[6]));   // realigned to 8 — the 6-byte gap
        Assert.Equal(104L, Convert.ToInt64(results[7]));
    }

    /// <summary>
    /// `struct utsname` is six inline `char[65]` buffers — 390 bytes, no padding,
    /// and completely inexpressible before `raw struct`.
    /// </summary>
    [Fact]
    public async Task Inline_char_buffers_size_and_decode()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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
                func uname(out buf: UtsName) -> int
            }
            size-of UtsName
            var r = LibC.uname()
            $r.ReturnValue
            $r.buf.sysname
            $r.buf.machine
            """);

        Assert.Equal(390, Convert.ToInt32(results[0]));
        Assert.Equal(0, Convert.ToInt32(results[1]));
        Assert.Equal("Linux", Assert.IsType<string>(results[2]));
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(results[3])));
    }

    /// <summary>
    /// End to end against real libc, cross-checked against an independent source
    /// of the same fact.
    /// </summary>
    [Fact]
    public async Task Sysinfo_round_trips_through_a_raw_struct()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var results = await NewEngine().ExecuteToListAsync(
            SysInfoDeclaration +
            """

            bind native "libc.so.6" as LibC {
                func sysinfo(out info: SysInfo) -> int
            }
            var r = LibC.sysinfo()
            $r.ReturnValue
            $r.info.uptime
            $r.info.totalram
            $r.info.loads[0]
            """);

        Assert.Equal(0, Convert.ToInt32(results[0]));
        Assert.True(Convert.ToInt64(results[1]) > 0, "uptime should be positive");

        // Cross-check total RAM against /proc/meminfo rather than trusting the
        // binding to agree with itself.
        var totalRam = Convert.ToUInt64(results[2]);
        var memTotalKb = ReadMemTotalKilobytes();
        Assert.True(
            Math.Abs((long)(totalRam / 1024) - (long)memTotalKb) < 64 * 1024,
            $"sysinfo totalram {totalRam / 1024} kB should be within 64 MB of /proc/meminfo {memTotalKb} kB");

        // loads is a fixed inline array; indexing it proves ByValArray marshalling.
        Assert.True(Convert.ToUInt64(results[3]) >= 0);
    }

    /// <summary>
    /// `out` parameters are engine-allocated, so they leave the call-site arity
    /// entirely. This is what turns `LibC.sysinfo(null)` into `LibC.sysinfo()`.
    /// </summary>
    [Fact]
    public async Task Out_parameters_leave_the_call_site_arity()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => NewEngine().ExecuteToListAsync(
                SysInfoDeclaration +
                """

                bind native "libc.so.6" as LibC {
                    func sysinfo(out info: SysInfo) -> int
                }
                LibC.sysinfo(null)
                """));

        Assert.Contains("expects 0 argument(s) but received 1", exception.Message);
        Assert.Contains("supplied by the engine", Assert.Single(exception.Diagnostics).Label);
    }

    /// <summary>
    /// `size n` is an optional assertion. It exists to convert a mis-transcribed
    /// struct from silent memory corruption into a declaration-time error —
    /// here, by catching a `pad` field wrongly carried over from the C header.
    /// </summary>
    [Fact]
    public async Task Declared_size_mismatch_is_reported()
    {
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => NewEngine().ExecuteToListAsync(
                """
                raw struct Wrong size 99 {
                    a: long
                    b: long
                }
                """));

        Assert.Equal("tosh.runtime.raw_struct_size_mismatch", Assert.Single(exception.Diagnostics).Code);
        Assert.Contains("16 bytes", exception.Message);
    }

    [Fact]
    public async Task Declared_size_matching_is_accepted()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            raw struct Right size 16 {
                a: long
                b: long
            }
            size-of Right
            """);

        Assert.Equal(16, Convert.ToInt32(Assert.Single(results)));
    }

    /// <summary>
    /// Default CLR marshalling makes `bool` a 4-byte Win32 BOOL, not a C
    /// `_Bool`. Silently accepting it would shift every subsequent field.
    /// </summary>
    [Fact]
    public async Task Bool_fields_are_rejected_with_a_pointer_to_byte()
    {
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => NewEngine().ExecuteToListAsync(
                """
                raw struct Flags {
                    enabled: bool
                }
                """));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.runtime.raw_struct_bool_field", diagnostic.Code);
        Assert.Contains("byte", diagnostic.Label);
    }

    /// <summary>
    /// A bare `cstring` would be a pointer, not an inline buffer, and the count
    /// cannot be inferred — `char[65]` and `char[256]` are different layouts.
    /// </summary>
    [Fact]
    public async Task Inline_char_buffers_require_an_explicit_length()
    {
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => NewEngine().ExecuteToListAsync(
                """
                raw struct Bad {
                    name: cstring
                }
                """));

        Assert.Equal("tosh.runtime.raw_struct_cstring_requires_length", Assert.Single(exception.Diagnostics).Code);
    }

    [Fact]
    public async Task Pack_produces_a_packed_layout()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            raw struct Loose {
                a: byte
                b: long
            }
            raw struct Tight pack 1 {
                a: byte
                b: long
            }
            size-of Loose
            size-of Tight
            """);

        Assert.Equal(16, Convert.ToInt32(results[0]));  // b realigned to 8
        Assert.Equal(9, Convert.ToInt32(results[1]));   // packed: no gap
    }

    [Fact]
    public async Task Unions_place_every_field_at_offset_zero()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            raw union Word {
                whole: uint
                low:   ushort
            }
            size-of Word
            offset-of Word whole
            offset-of Word low
            """);

        Assert.Equal(4, Convert.ToInt32(results[0]));
        Assert.Equal(0L, Convert.ToInt64(results[1]));
        Assert.Equal(0L, Convert.ToInt64(results[2]));
    }

    /// <summary>
    /// Defaults apply when TōSh constructs a value. They deliberately do not
    /// apply to `out` parameters — see
    /// <see cref="Out_parameters_are_zeroed_not_defaulted"/>.
    /// </summary>
    [Fact]
    public async Task Field_defaults_apply_when_constructing_a_value()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            raw struct PollFd {
                fd:      int = -1
                events:  short = 1
                revents: short
            }
            var p = new PollFd()
            $p.fd
            $p.events
            $p.revents
            """);

        Assert.Equal(-1, Convert.ToInt32(results[0]));
        Assert.Equal(1, Convert.ToInt32(results[1]));
        Assert.Equal(0, Convert.ToInt32(results[2]));
    }

    /// <summary>
    /// A CLR struct cannot carry an initializer the marshaller would honour, so
    /// an `out` parameter arrives zero-filled. That is correct rather than a
    /// limitation: `out` means the callee writes everything, and seeding
    /// defaults would mask a callee that failed to write.
    /// </summary>
    [Fact]
    public async Task Out_parameters_are_zeroed_not_defaulted()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var results = await NewEngine().ExecuteToListAsync(
            """
            raw struct Timeval {
                tv_sec:  long = 12345
                tv_usec: long = 999
            }
            bind native "libc.so.6" as LibC {
                func gettimeofday(out tv: Timeval, nint) -> int
            }
            var built = new Timeval()
            $built.tv_sec
            var r = LibC.gettimeofday(0)
            $r.tv.tv_sec != 12345
            """);

        Assert.Equal(12345L, Convert.ToInt64(results[0]));
        Assert.True(Assert.IsType<bool>(results[1]), "the callee's value must not be the declared default");
    }

    /// <summary>
    /// Caching emitted types by structural key, not by name: re-declaring the
    /// same layout must not mint a second incompatible CLR type.
    /// </summary>
    [Fact]
    public async Task Redeclaring_an_identical_layout_reuses_the_emitted_type()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            raw struct Point { x: int; y: int }
            var first = (size-of Point)
            raw struct Point { x: int; y: int }
            var second = (size-of Point)
            $first == $second
            $second
            """);

        Assert.True(Assert.IsType<bool>(results[0]));
        Assert.Equal(8, Convert.ToInt32(results[1]));
    }

    [Fact]
    public async Task Raw_structs_are_usable_as_buffer_types()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            raw struct Pair { a: int; b: int }
            alloc buf = Pair
            write-buffer $buf (new Pair(7, 11))
            var round = (read-buffer Pair $buf)
            $round.a
            $round.b
            forget $buf | ignore
            """);

        Assert.Equal(7, Convert.ToInt32(results[0]));
        Assert.Equal(11, Convert.ToInt32(results[1]));
    }

    /// <summary>
    /// C's output-string idiom is always an adjacent (pointer, length) pair, so
    /// declaring it literally means writing the capacity twice. `buffer[n]`
    /// expands to both ABI arguments and yields one decoded string.
    /// </summary>
    [Fact]
    public async Task Buffer_parameters_expand_to_a_pointer_and_length_pair()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var results = await NewEngine().ExecuteToListAsync(
            """
            bind native "libc.so.6" as LibC {
                func gethostname(out name: buffer[256]) -> int
            }
            var r = LibC.gethostname()
            $r.ReturnValue
            $r.name
            """);

        Assert.Equal(0, Convert.ToInt32(results[0]));

        var hostname = Assert.IsType<string>(results[1]);
        Assert.False(string.IsNullOrWhiteSpace(hostname));
        Assert.Equal(File.ReadAllText("/etc/hostname").Trim(), hostname);

        // Decoding must stop at the NUL, not return the whole 256-byte buffer.
        Assert.DoesNotContain('\0', hostname);
    }

    /// <summary>
    /// A buffer is memory the callee writes into, so it carries nothing in.
    /// Declaring it any other way is a mistake worth naming.
    /// </summary>
    [Fact]
    public async Task Buffer_parameters_must_be_declared_out()
    {
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => NewEngine().ExecuteToListAsync(
                """
                bind native "libc.so.6" as LibC {
                    func gethostname(name: buffer[256]) -> int
                }
                """));

        Assert.Equal("tosh.runtime.native_buffer_requires_out", Assert.Single(exception.Diagnostics).Code);
    }

    /// <summary>
    /// `export` must be recognised as a declaration modifier here, not as the
    /// environment-variable `export` command — `raw` had to join the parser's
    /// declaration-keyword list for that.
    /// </summary>
    [Fact]
    public async Task Raw_structs_accept_declaration_modifiers()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            export raw struct Exported { a: int }
            size-of Exported
            """);

        Assert.Equal(4, Convert.ToInt32(Assert.Single(results)));
    }

    /// <summary>
    /// A qualified reference evaluates to the type object before the command
    /// runs, so there is no name left to resolve — the interop commands
    /// recognise <c>INativeLayoutType</c> directly instead.
    /// </summary>
    [Fact]
    public async Task Module_qualified_raw_structs_are_usable()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            partial module Outer {
                partial module Inner {
                    export raw struct Pair { a: int; b: long }
                }
            }
            size-of Outer.Inner.Pair
            offset-of Outer.Inner.Pair b
            """);

        Assert.Equal(16, Convert.ToInt32(results[0]));  // long realigned to 8
        Assert.Equal(8L, Convert.ToInt64(results[1]));
    }

    /// <summary>
    /// A raw struct declared in a module is nameable from a native signature
    /// outside it — the shape `System.tosh` actually takes.
    /// </summary>
    [Fact]
    public async Task Module_qualified_raw_structs_work_in_native_signatures()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var results = await NewEngine().ExecuteToListAsync(
            """
            partial module ToastLib {
                partial module System {
                    export raw struct UtsName {
                        sysname:    cstring[65]
                        nodename:   cstring[65]
                        release:    cstring[65]
                        version:    cstring[65]
                        machine:    cstring[65]
                        domainname: cstring[65]
                    }
                }
            }
            bind native "libc.so.6" as LibC {
                func uname(out buf: ToastLib.System.UtsName) -> int
            }
            var r = LibC.uname()
            $r.ReturnValue
            $r.buf.sysname
            """);

        Assert.Equal(0, Convert.ToInt32(results[0]));
        Assert.Equal("Linux", Assert.IsType<string>(results[1]));
    }

    private static ulong ReadMemTotalKilobytes()
    {
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (!line.StartsWith("MemTotal:", StringComparison.Ordinal)) continue;

            var digits = new string(line.Where(char.IsDigit).ToArray());
            return ulong.Parse(digits);
        }

        throw new InvalidOperationException("MemTotal not found in /proc/meminfo");
    }
}
