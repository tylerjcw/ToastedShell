using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Property accessor blocks — <c>prop X { get … set … }</c> (<c>TS-P2-31</c>).
/// </summary>
/// <remarks>
/// <para>
/// Found while checking whether the specification documented <c>get</c> and
/// <c>set</c> as accessors. It does not, and the reason nobody had noticed is that
/// the brace-bodied form silently did the wrong thing:
/// <c>prop X { get { return $this.b } }</c> returned a <c>ShellBlock</c> rather than
/// running the getter, with no diagnostic. The arrow form
/// <c>prop X { get =&gt; ($this.b) }</c> worked throughout.
/// </para>
/// <para>
/// The cause was a lenient helper meeting a stricter grammar. Accessor bodies went
/// through <c>ParseArrowStatementBlock</c>, whose <c>ConsumeFatArrow</c> consumes an
/// arrow if present and shrugs otherwise, so the brace form fell into
/// <c>ParseStatement</c> — where <c>TS-P2-25</c>'s block-only <c>{</c> turned it into
/// a first-class block value. Neither change was wrong alone.
/// </para>
/// </remarks>
public sealed class PropertyAccessorTests
{
    private static async Task<object?> EvaluateAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? null : results[^1];
    }

    [Fact]
    public async Task An_arrow_bodied_getter_runs()
    {
        // The form that always worked, pinned so supporting the other one cannot
        // break it.
        var value = await EvaluateAsync(
            """
            class C {
                shy prop backing: int = 5
                prop X { get => ($this.backing * 2) }
            }
            (new C()).X
            """);

        Assert.Equal(10, value);
    }

    [Fact]
    public async Task A_brace_bodied_getter_runs_rather_than_returning_a_block()
    {
        // The defect. Before the fix this answered with a ShellBlock.
        var value = await EvaluateAsync(
            """
            class C {
                shy prop backing: int = 5
                prop X {
                    get { return $this.backing * 2 }
                }
            }
            (new C()).X
            """);

        Assert.Equal(10, value);
    }

    [Fact]
    public async Task A_brace_bodied_getter_holds_more_than_one_statement()
    {
        // The reason the brace form was supported rather than diagnosed: a getter
        // restricted to a single expression pushes anything conditional into a
        // helper method.
        var value = await EvaluateAsync(
            """
            class C {
                shy prop backing: int = 5
                prop X {
                    get {
                        var doubled = $this.backing * 2
                        return $doubled + 1
                    }
                }
            }
            (new C()).X
            """);

        Assert.Equal(11, value);
    }

    [Fact]
    public async Task A_brace_bodied_setter_receives_the_incoming_value()
    {
        // Round trip through both accessors: the setter converts on the way in and
        // the getter converts back on the way out. `$value` is the incoming value.
        var value = await EvaluateAsync(
            """
            class Temp {
                shy prop celsius: double = 0.0
                prop Fahrenheit {
                    get { return ($this.celsius * 9.0 / 5.0) + 32.0 }
                    set {
                        var c = ($value - 32.0) * 5.0 / 9.0
                        $this.celsius = $c
                    }
                }
            }
            var t = new Temp()
            $t.Fahrenheit = 212.0
            $t.Fahrenheit
            """);

        Assert.Equal(212.0, Assert.IsType<double>(value), precision: 6);
    }

    [Fact]
    public async Task The_two_body_forms_agree()
    {
        // Stated as a property rather than two separate expectations, because the
        // whole point is that the choice of body syntax is not supposed to be
        // observable.
        var arrow = await EvaluateAsync(
            """
            class C {
                shy prop b: int = 7
                prop X { get => ($this.b + 1) }
            }
            (new C()).X
            """);

        var braced = await EvaluateAsync(
            """
            class C {
                shy prop b: int = 7
                prop X { get { return $this.b + 1 } }
            }
            (new C()).X
            """);

        Assert.Equal(arrow, braced);
    }

    [Fact]
    public async Task An_unknown_accessor_name_is_still_refused()
    {
        // Accepting brace bodies must not have widened the accessor names accepted.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var parse = Tosh.Language.Parsing.ToshParser.Parse(
            """
            class C {
                prop X {
                    fetch { return 1 }
                }
            }
            """,
            "<t>");

        Assert.Contains(
            "tosh.parser.unknown_property_accessor",
            parse.Diagnostics.Select(diagnostic => diagnostic.Code));

        await Task.CompletedTask;
    }
}
