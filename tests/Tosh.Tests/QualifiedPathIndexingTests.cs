using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Subscripting a dotted path — <c>A.b.c[0]</c> — used to require parentheses.
///
/// Two independent causes, one per parse context. In argument position the
/// static-member-access node was returned without going through
/// <c>ParsePostfixChain</c>, so a trailing <c>[0]</c> was left behind. In command
/// position <c>NextTokenStartsCommandArgument</c> treated any <c>[</c> as the
/// start of an argument, so the whole path became a command name and the
/// subscript a separate list literal.
/// </summary>
public class QualifiedPathIndexingTests
{
    private static ToshEngine NewEngine() => new(ToshRuntime.CreateDefault().Language);

    private const string Fixture =
        """
        class P { prop Xs = [10, 20, 30] }
        hermit class T { shared prop p: P => new P() }
        """;

    [Fact]
    public async Task Dotted_paths_can_be_indexed_in_argument_position()
    {
        var results = await NewEngine().ExecuteToListAsync(
            Fixture +
            """

            echo T.p.Xs[0]
            echo T.p.Xs[2]
            """);

        Assert.Equal(10, Convert.ToInt32(results[0]));
        Assert.Equal(30, Convert.ToInt32(results[1]));
    }

    [Fact]
    public async Task Dotted_paths_can_be_indexed_in_command_position()
    {
        var results = await NewEngine().ExecuteToListAsync(
            Fixture +
            """

            T.p.Xs[1]
            """);

        Assert.Equal(20, Convert.ToInt32(Assert.Single(results)));
    }

    [Fact]
    public async Task Indexed_dotted_paths_compose_with_arithmetic()
    {
        var results = await NewEngine().ExecuteToListAsync(
            Fixture +
            """

            echo (T.p.Xs[1] / 2)
            """);

        Assert.Equal(10, Convert.ToInt32(Assert.Single(results)));
    }

    [Fact]
    public async Task Parenthesised_form_still_works()
    {
        var results = await NewEngine().ExecuteToListAsync(
            Fixture +
            """

            echo (T.p.Xs)[2]
            """);

        Assert.Equal(30, Convert.ToInt32(Assert.Single(results)));
    }

    /// <summary>
    /// The distinction the fix turns on: a *spaced* bracket is still a list
    /// argument to a module-qualified command. Only an adjacent bracket
    /// subscripts, which is the same adjacency rule ParsePostfixChain uses.
    /// </summary>
    [Fact]
    public async Task A_spaced_bracket_is_still_a_list_argument()
    {
        var results = await NewEngine().ExecuteToListAsync(
            """
            module M { export func F(items) => $items | count }
            M.F [1, 2, 3]
            """);

        Assert.Equal(3, Convert.ToInt32(Assert.Single(results)));
    }

    /// <summary>
    /// The motivating case: a fixed inline array inside a raw struct, reached
    /// through a computed static property.
    /// </summary>
    [Fact]
    public async Task Raw_struct_inline_arrays_index_through_a_static_property()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var results = await NewEngine().ExecuteToListAsync(
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
            hermit class SysFacts {
                shy bind native "libc.so.6" { func sysinfo(out info: SysInfo) -> ok }
                shy shared prop info: SysInfo => SysFacts.sysinfo()
                shared prop Load1: double => SysFacts.info.loads[0] / 65536.0
            }
            SysFacts.Load1
            """);

        var load = Convert.ToDouble(Assert.Single(results));
        Assert.True(load >= 0, "load average is never negative");

        var expected = double.Parse(File.ReadAllText("/proc/loadavg").Split(' ')[0]);
        Assert.True(
            Math.Abs(expected - load) < 10.0,
            $"reported {load} should be near /proc/loadavg {expected}");
    }
}
