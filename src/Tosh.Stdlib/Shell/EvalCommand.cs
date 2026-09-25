using System.Text;
using Tosh.Language;
using Tosh.Runtime;
using Tosh.Stdlib.Cas;

namespace Tosh.Stdlib.Shell;

[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Shell")]
[CommandArgument("source", "Tosh source text or CAS expression to parse and evaluate in the current session.", Required = false)]
[CommandExample("eval \"1 + 2\"", Title = "Evaluate a literal expression")]
[CommandExample("var x = sym x; solve (2 * $x + 4 == 97) | eval", Title = "Numerically evaluate symbolic solutions")]
[CommandExample("read-lines colors.txt | each { eval $\"System.Drawing.Color.{$_}\" }", Title = "Resolve named members per line")]
[CommandOutput("Streams whatever values the evaluated source or symbolic expression emits.")]
[PipelineInput(AcceptsScalar = true, Description = "Accepts strings of Tosh source, symbolic expressions, equations, or numbers.")]
public sealed class EvalCommand : ShellCommand
{
    public EvalCommand()
        : base("eval", "Parses and evaluates strings as Tosh source or evaluates CAS symbolic expressions/equations to numerical values.", "eval [source...]")
    {
    }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        bool hasPipelinedInput = false;
        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);

        if (await enumerator.MoveNextAsync())
        {
            hasPipelinedInput = true;
            do
            {
                var item = enumerator.Current;
                await foreach (var res in EvaluateItemAsync(item, context))
                {
                    yield return res;
                }
            } while (await enumerator.MoveNextAsync());
        }

        if (!hasPipelinedInput)
        {
            if (context.Arguments.Count == 0)
            {
                throw new InvalidOperationException("The 'eval' command requires at least one source string or pipeline input.");
            }

            if (context.Arguments.Count == 1 && context.Arguments[0] is not string and not null)
            {
                await foreach (var res in EvaluateItemAsync(context.Arguments[0], context))
                {
                    yield return res;
                }
            }
            else
            {
                var sb = new StringBuilder();
                for (var i = 0; i < context.Arguments.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(context.Arguments[i]?.ToString());
                }

                var source = sb.ToString();

                await foreach (var value in RequireEvaluator(context).EvaluateAsync(source, "<eval>", context.CancellationToken)
                                   .WithCancellation(context.CancellationToken))
                {
                    if (value is SymExpr or SymEquation or BigRational)
                    {
                        await foreach (var res in EvaluateItemAsync(value, context))
                        {
                            yield return res;
                        }
                    }
                    else
                    {
                        yield return value;
                    }
                }
            }
        }
    }

    private static async IAsyncEnumerable<object?> EvaluateItemAsync(object? item, CommandContext context)
    {
        switch (item)
        {
            case null:
                yield return null;
                break;

            case SymEquation eq:
                if (eq.Left is SymVariable && !eq.Right.GetVariables().Any())
                {
                    await foreach (var res in EvaluateItemAsync(eq.Right, context))
                    {
                        yield return res;
                    }
                }
                else if (eq.Right is SymVariable && !eq.Left.GetVariables().Any())
                {
                    await foreach (var res in EvaluateItemAsync(eq.Left, context))
                    {
                        yield return res;
                    }
                }
                else if (!eq.GetVariables().Any())
                {
                    yield return eq.Evaluate() != 0.0;
                }
                else
                {
                    yield return Simplifier.Simplify(eq);
                }
                break;

            case SymExpr expr:
                if (!expr.GetVariables().Any())
                {
                    if (expr is SymNumber num)
                    {
                        yield return ToNumericValue(num.Value);
                    }
                    else
                    {
                        yield return ToNumericValue(expr.Approximate);
                    }
                }
                else
                {
                    yield return Simplifier.Simplify(expr);
                }
                break;

            case BigRational r:
                yield return ToNumericValue(r);
                break;

            case int or long or short or byte or sbyte or ushort or uint or ulong or System.Numerics.BigInteger or decimal:
                yield return item;
                break;

            case double d:
                yield return ToNumericValue(d);
                break;

            case float f:
                yield return ToNumericValue((double)f);
                break;

            case string str:
                await foreach (var value in RequireEvaluator(context).EvaluateAsync(str, "<eval>", context.CancellationToken)
                                   .WithCancellation(context.CancellationToken))
                {
                    if (value is SymExpr or SymEquation or BigRational)
                    {
                        await foreach (var res in EvaluateItemAsync(value, context))
                        {
                            yield return res;
                        }
                    }
                    else
                    {
                        yield return value;
                    }
                }
                break;

            default:
                yield return item;
                break;
        }
    }

    private static object ToNumericValue(BigRational r)
    {
        if (r.IsInteger)
        {
            if (r.Numerator >= int.MinValue && r.Numerator <= int.MaxValue)
            {
                return (int)r.Numerator;
            }
            if (r.Numerator >= long.MinValue && r.Numerator <= long.MaxValue)
            {
                return (long)r.Numerator;
            }
            return r.Numerator;
        }

        return r.ToDouble();
    }

    private static object ToNumericValue(double d)
    {
        if (!double.IsInfinity(d) && !double.IsNaN(d))
        {
            var rounded = Math.Round(d);
            if (Math.Abs(d - rounded) < 1e-12)
            {
                if (rounded >= int.MinValue && rounded <= int.MaxValue)
                {
                    return (int)rounded;
                }
                if (rounded >= long.MinValue && rounded <= long.MaxValue)
                {
                    return (long)rounded;
                }
            }
        }

        return d;
    }

    /// <summary>
    /// The evaluator, reached through the runtime at execute time rather than taken at
    /// construction — which is what lets this command be registered before an engine
    /// exists (`TOAST-0006`). `eval` needs only `IShellEvaluator`; it does not run
    /// script *files*, so it does not need the script host.
    /// </summary>
    private static IShellEvaluator RequireEvaluator(CommandContext context)
        => context.Shell().Evaluator
           ?? throw new InvalidOperationException(
               "This host has no evaluator, so there is nothing for 'eval' to evaluate.");
}
