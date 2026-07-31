using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Native bindings written where the type that wraps them lives, rather than in
/// a separate module: the library path is stated once and the result reads as
/// <c>SystemInfo.Hostname</c>, not <c>LibC.gethostname($buf)</c>.
/// </summary>
public class NativeClassMemberTests
{
    private static ToshEngine NewEngine() => new(ToshRuntime.CreateDefault());

    private static bool SkipOffLinux => !OperatingSystem.IsLinux();

    private const string UtsNameDeclaration =
        """
        raw struct UtsName {
            sysname:    cstring[65]
            nodename:   cstring[65]
            release:    cstring[65]
            version:    cstring[65]
            machine:    cstring[65]
            domainname: cstring[65]
        }
        """;

    [Fact]
    public async Task Bind_blocks_work_inside_a_class_body()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            UtsNameDeclaration +
            """

            hermit class SysFacts {
                shy bind native "libc.so.6" {
                    func uname(out buf: UtsName) -> ok
                    func gethostname(out name: buffer[256]) -> ok
                }

                shared prop Kernel:   string => SysFacts.uname().release
                shared prop Hostname: string => SysFacts.gethostname()
            }
            SysFacts.Kernel
            SysFacts.Hostname
            """);

        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(results[0])));
        Assert.Equal(File.ReadAllText("/etc/hostname").Trim(), Assert.IsType<string>(results[1]));
    }

    [Fact]
    public async Task Bind_blocks_work_inside_a_module_body()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            module Demo {
                bind native "libc.so.6" as LibC {
                    func abs(int) -> int
                }
            }
            Demo.LibC.abs(-5)
            """);

        Assert.Equal(5, Convert.ToInt32(Assert.Single(results)));
    }

    /// <summary>
    /// The standalone form, for a single binding that does not justify a block.
    /// It declares the name in the enclosing scope so it is callable directly.
    /// </summary>
    [Fact]
    public async Task Raw_func_declares_a_callable_name_at_top_level()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            raw func sysconf(name: int) -> count from "libc.so.6"
            sysconf(30)
            """);

        Assert.Equal(Environment.SystemPageSize, Convert.ToInt32(Assert.Single(results)));
    }

    [Fact]
    public async Task Raw_func_works_as_a_class_member()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            hermit class Sys {
                raw func sysconf(name: int) -> count from "libc.so.6"
                shared prop PageSize: long => Sys.sysconf(30)
            }
            Sys.PageSize
            """);

        Assert.Equal(Environment.SystemPageSize, Convert.ToInt32(Assert.Single(results)));
    }

    [Fact]
    public async Task Raw_func_without_a_library_is_rejected()
    {
        var engine = NewEngine();

        var result = ToshParser.Parse("""raw func sysconf(name: int) -> count""");

        Assert.Contains(result.Diagnostics, d => d.Code == "tosh.parser.raw_func_requires_library");
    }

    /// <summary>
    /// The whole point of the design: raw ABI surface hidden, typed surface
    /// public. `methods` must not advertise the shy bindings.
    /// </summary>
    [Fact]
    public async Task Shy_native_members_are_not_listed_as_class_methods()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            UtsNameDeclaration +
            """

            hermit class SysFacts {
                shy bind native "libc.so.6" {
                    func uname(out buf: UtsName) -> ok
                }
                shared prop Kernel: string => SysFacts.uname().release
            }
            methods SysFacts | where (_.Name == "uname") | count
            """);

        Assert.Equal(0, Convert.ToInt32(Assert.Single(results)));
    }

    /// <summary>
    /// The counterpart: `proud` opts back out of hiding, so the two spellings
    /// are genuinely different rather than decorative.
    /// </summary>
    [Fact]
    public async Task Proud_native_members_are_listed_as_class_methods()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            hermit class Hidden {
                shy bind native "libc.so.6" { func abs(int) -> int }
            }
            hermit class Shown {
                proud bind native "libc.so.6" { func labs(long) -> long }
            }
            methods Hidden | count
            methods Shown | count
            """);

        Assert.Equal(0, Convert.ToInt32(results[0]));
        Assert.Equal(1, Convert.ToInt32(results[1]));
    }

    /// <summary>
    /// The full shape `System.tosh` takes: raw structs in a nested module, a
    /// hermit class binding libc, and a typed static surface over it.
    /// </summary>
    [Fact]
    public async Task The_full_system_info_shape_works_end_to_end()
    {
        if (SkipOffLinux) return;

        var results = await NewEngine().ExecuteToListAsync(
            """
            partial module ToastLib {
                partial module System {
                    export raw struct SysInfo {
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

                    export hermit class SysFacts {
                        shy bind native "libc.so.6" {
                            func sysinfo(out info: SysInfo) -> ok
                            func gethostname(out name: buffer[256]) -> ok
                            func sysconf(name: int) -> count
                        }

                        shy shared prop info: SysInfo => SysFacts.sysinfo()

                        shared prop Uptime:   long   => SysFacts.info.uptime
                        shared prop TotalRam: long   => SysFacts.info.totalram * SysFacts.info.mem_unit
                        shared prop Load1:    double => (SysFacts.info.loads)[0] / 65536.0
                        shared prop Hostname: string => SysFacts.gethostname()
                        shared prop PageSize: long   => SysFacts.sysconf(30)
                    }
                }
            }
            var s = ToastLib.System.SysFacts
            $s.Uptime
            $s.TotalRam
            $s.Load1
            $s.Hostname
            $s.PageSize
            """);

        Assert.True(Convert.ToInt64(results[0]) > 0, "uptime should be positive");
        Assert.True(Convert.ToInt64(results[1]) > 0, "total RAM should be positive");
        Assert.True(Convert.ToDouble(results[2]) >= 0, "load average is never negative");
        Assert.Equal(File.ReadAllText("/etc/hostname").Trim(), Assert.IsType<string>(results[3]));
        Assert.Equal(Environment.SystemPageSize, Convert.ToInt32(results[4]));
    }

    /// <summary>
    /// A three-segment path through a computed static property used to fall
    /// through to the bareword fallback and evaluate to the literal string
    /// "T.p.X" — silently, with no diagnostic. The module branch had always
    /// walked arbitrary depth; declared types stopped at two segments.
    ///
    /// No native code involved: this is the general qualified-access path.
    /// </summary>
    [Fact]
    public async Task Static_property_chains_resolve_beyond_two_segments()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            class Point { prop X: int = 3 }
            hermit class Holder {
                shy shared prop p: Point => new Point()
                shared prop ViaProp: int => Holder.p.X
            }
            Holder.ViaProp
            """);

        Assert.Equal(3, Convert.ToInt32(Assert.Single(results)));
    }
}
