using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// An operator method can learn which side of the expression it was on — <c>TOAST-0106</c>.
/// </summary>
/// <remarks>
/// <para>
/// The dispatch rule is: the left operand's <c>OP</c> method is tried first with the right
/// operand as its argument; failing that, the right operand's <c>OP</c> method is invoked with
/// the *left* operand as its argument, and <c>$this</c> is still the right operand.
/// </para>
/// <para>
/// For <c>+</c> and <c>*</c> that is correct. For <c>-</c>, <c>/</c> and <c>%</c> it was not, and
/// no library could work around it: <c>10 - $p</c> and <c>$p - 10</c> arrive as the same call and
/// are indistinguishable from inside the method. <c>ToastLib.Math.Point2D</c> answered
/// <c>(-9, -8)</c> to both, when the first should be <c>(9, 8)</c>.
/// </para>
/// <para>
/// An operator method may now declare a second parameter, true when the instance was the right
/// operand. The two-argument form is offered first and the one-argument form is the fallback, so
/// every operator written before this keeps its behaviour exactly — which is what the
/// <c>Legacy</c> cases below pin.
/// </para>
/// </remarks>
public sealed class ReversedOperatorTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private const string Declarations = """
        export partial module RevOps {
            export class Oriented(amount: double) {
                prop Amount: double = $amount
                func -(other, reversed) {
                    var scalar = ($other * 1.0)
                    return (new RevOps.Oriented(($reversed) ? ($scalar - $this.Amount) : ($this.Amount - $scalar)))
                }
                func /(other, reversed) {
                    var scalar = ($other * 1.0)
                    return (new RevOps.Oriented(($reversed) ? ($scalar / $this.Amount) : ($this.Amount / $scalar)))
                }
                func +(other, reversed) { return (new RevOps.Oriented(($this.Amount + ($other * 1.0)))) }
                func ToString() => $"{$this.Amount}"
            }

            export class Legacy(amount: double) {
                prop Amount: double = $amount
                func -(other) { return (new RevOps.Legacy(($this.Amount - ($other * 1.0)))) }
                func ToString() => $"{$this.Amount}"
            }
        }
        """;

    [Theory]
    [InlineData("$v - 10", "-7")]
    [InlineData("10 - $v", "7")]
    [InlineData("$v / 6", "0.5")]
    [InlineData("6 / $v", "2")]
    public async Task An_operator_taking_the_flag_orders_its_operands(string expression, string expected)
    {
        var output = await RunAsync($$"""
            {{Declarations}}
            var v = (new RevOps.Oriented(3.0))
            echo ({{expression}})
            """);

        Assert.Equal(expected, output);
    }

    /// <summary>Commutative operators are unaffected either way round.</summary>
    [Theory]
    [InlineData("$v + 10", "13")]
    [InlineData("10 + $v", "13")]
    public async Task Commutative_operators_are_unchanged(string expression, string expected)
    {
        var output = await RunAsync($$"""
            {{Declarations}}
            var v = (new RevOps.Oriented(3.0))
            echo ({{expression}})
            """);

        Assert.Equal(expected, output);
    }

    /// <summary>
    /// The whole point of offering the flag rather than requiring it: an operator written before
    /// this existed behaves exactly as it did, including in the reversed position where its answer
    /// is wrong. Changing that silently would be a worse bug than the one being fixed.
    /// </summary>
    [Theory]
    [InlineData("$v - 10", "-7")]
    [InlineData("10 - $v", "-7")]
    public async Task A_single_argument_operator_keeps_its_old_behaviour(string expression, string expected)
    {
        var output = await RunAsync($$"""
            {{Declarations}}
            var v = (new RevOps.Legacy(3.0))
            echo ({{expression}})
            """);

        Assert.Equal(expected, output);
    }

    /// <summary>The flag is false when the instance is on the left, not merely absent.</summary>
    [Fact]
    public async Task The_flag_is_false_for_the_left_operand()
    {
        var output = await RunAsync("""
            export partial module RevProbe {
                export class Probe {
                    func -(other, reversed) { return $reversed }
                }
            }
            var p = (new RevProbe.Probe {| |})
            echo ($p - 1)
            echo (1 - $p)
            """);

        Assert.Equal("False,True", output);
    }
}
