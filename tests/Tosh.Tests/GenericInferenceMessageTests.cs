using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What a call is told when a generic method's type arguments cannot be worked out.
/// </summary>
/// <remarks>
/// <para>
/// A script lambda carries no signature, so a type parameter appearing only in a delegate's
/// return position has nothing to be inferred from: C# reads <c>TOutput</c> off the lambda
/// body, and a dynamically typed shell has no body to read. That call cannot be closed, and
/// saying so is the right outcome.
/// </para>
/// <para>
/// Saying it accurately is the part that was missing. The message named every type parameter
/// of every generic overload, including the ones the arguments had already determined, and
/// said nothing about the spelling that does work.
/// </para>
/// </remarks>
public sealed class GenericInferenceMessageTests
{
    private static async Task<string> FailureOf(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteToListAsync(source));

        return error.Message;
    }

    /// <summary>
    /// Only the parameters the arguments left open are named.
    /// </summary>
    /// <remarks>
    /// <c>Array.ConvertAll&lt;TInput, TOutput&gt;(TInput[], Converter&lt;TInput, TOutput&gt;)</c>
    /// called with an <c>int[]</c> has <c>TInput</c> settled before the lambda is even
    /// considered. Being told to supply something you already supplied is worse than being
    /// told nothing.
    /// </remarks>
    [Fact]
    public async Task Only_the_type_parameters_that_are_open_are_named()
    {
        var message = await FailureOf(
            """
            var f = func(n) => ($n * 2)
            var r = System.Array::ConvertAll([3, 1, 2], $f)
            """);

        Assert.Contains("Cannot infer type argument 'TOutput'", message, StringComparison.Ordinal);
        Assert.DoesNotContain("'TInput', 'TOutput'", message, StringComparison.Ordinal);
    }

    /// <summary>The message gives the spelling that works.</summary>
    /// <remarks>
    /// Explicit type arguments close the call, so a failure that does not mention them leaves
    /// the author at a dead end with a correct diagnosis.
    /// </remarks>
    [Fact]
    public async Task The_message_says_how_to_supply_the_arguments()
    {
        var message = await FailureOf(
            """
            var f = func(n) => ($n * 2)
            var r = System.Array::ConvertAll([3, 1, 2], $f)
            """);

        Assert.Contains("ConvertAll<TInput, TOutput>(...)", message, StringComparison.Ordinal);
    }

    /// <summary>And that spelling does work.</summary>
    [Fact]
    public async Task Explicit_type_arguments_close_the_call()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            var f = func(n) => ($n * 2)
            var r = System.Array::ConvertAll<int, int>([3, 1, 2], $f)
            echo $"{$r[0]},{$r[1]},{$r[2]}"
            """);

        Assert.Equal(["6,2,4"], results.OfType<string>());
    }

    /// <summary>Inference from ordinary values is untouched.</summary>
    /// <remarks>
    /// The failure path is the only thing this changes. A generic static whose arguments do
    /// determine its type parameters still resolves without them being written.
    /// </remarks>
    [Fact]
    public async Task Inference_from_ordinary_values_still_works()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            echo (System.Linq.Enumerable::Count<int>(new System.Collections.Generic.List<int>([1, 2, 3])))
            echo (System.Enum::Parse<System.DayOfWeek>("Monday"))
            """);

        Assert.Equal(["3", "Monday"], results.Select(r => r?.ToString()));
    }
}
