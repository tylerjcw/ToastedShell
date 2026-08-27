using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What `is` tests for a CLR value — `TOAST-0029`.
/// </summary>
/// <remarks>
/// <para>
/// `is` had two halves that disagreed. A declared class instance walked its bases, its
/// interfaces and its traits; a CLR value matched an exact type name and nothing else,
/// because the only fallback was `Type.GetType`, which needs an assembly qualifier. So
/// `$e is Exception` and `[1,2] is IEnumerable` were false while
/// `$e is System.Exception` and the qualified `IEnumerable` were true — the same question,
/// answered differently depending on how the type was spelled.
/// </para>
/// <para>
/// A bare name now resolves against the same platform index an import consults. The
/// exception is deliberate and narrow: a bare name asks about the **language's** value
/// model, so a `str`, a record and a dictionary are not sequences (`§Collection Shape`),
/// while a namespace-qualified name asks about the host type graph and is answered
/// literally.
/// </para>
/// </remarks>
public sealed class TypeTestSemanticsTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>A bare CLR name resolves, and walks the type graph.</summary>
    [Theory]
    [InlineData("var e = new System.InvalidOperationException(\"x\")\n($e is Exception)", "True")]
    [InlineData("(5 is ValueType)", "True")]
    [InlineData("([1,2] is IEnumerable)", "True")]
    [InlineData("(\"abc\" is IComparable)", "True")]
    public async Task A_bare_clr_name_resolves(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// A bare sequence test respects the language's atoms; a qualified one does not.
    /// </summary>
    /// <remarks>
    /// The fork this item turned on, and it was only visible after measuring: the
    /// qualified spelling was **already** true for a string, so "exclude the atoms" had to
    /// say which spellings it applied to. A bare name is a language question and answers
    /// per `§Collection Shape`; a qualified name is an explicit CLR question and answering
    /// it falsely would mislead interop code.
    /// </remarks>
    [Theory]
    [InlineData("(\"abc\" is IEnumerable)", "False")]
    [InlineData("(\"abc\" is System.Collections.IEnumerable)", "True")]
    [InlineData("({% \"a\" => 1 %} is IEnumerable)", "False")]
    [InlineData("({| a = 1 |} is IEnumerable)", "False")]
    [InlineData("([1,2] is IEnumerable)", "True")]
    [InlineData("({: 1, 2 :} is IEnumerable)", "True")]
    public async Task A_bare_sequence_test_agrees_with_collection_shape(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// `is` and the pipeline answer the same question about what is a sequence.
    /// </summary>
    /// <remarks>
    /// Asserted as a property rather than as examples, because the two are supposed to
    /// share one predicate. A value that counts as one item must not be `is IEnumerable`,
    /// and a value that spreads must be.
    /// </remarks>
    [Theory]
    [InlineData("[1, 2, 3]", true)]
    [InlineData("{: 1, 2 :}", true)]
    [InlineData("\"abc\"", false)]
    [InlineData("{% \"a\" => 1 %}", false)]
    [InlineData("{| a = 1 |}", false)]
    public async Task Is_and_the_pipeline_agree_about_sequences(string literal, bool isSequence)
    {
        var viaIs = await RunAsync($"({literal} is IEnumerable)");
        var count = await RunAsync($"({literal} | count)");

        Assert.Equal(isSequence ? "True" : "False", viaIs);

        // A value the pipeline treats as one item is not a sequence to `is` either.
        Assert.Equal(isSequence, count != "1");
    }

    /// <summary>
    /// Only a sequence question triggers the atom rule; ordinary tests are untouched.
    /// </summary>
    [Theory]
    [InlineData("(\"abc\" is string)", "True")]
    [InlineData("(\"abc\" is str)", "True")]
    [InlineData("(5 is int)", "True")]
    [InlineData("(5 is string)", "False")]
    [InlineData("(\"abc\" is object)", "True")]
    public async Task An_ordinary_type_test_is_unchanged(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The case this was needed for: telling apart what a `catch` caught.
    /// </summary>
    /// <remarks>
    /// `TOAST-0018`'s exception-semantics box wanted three answers distinguishable — a
    /// declared error, a runtime failure, and an arbitrary thrown value. `is Error` gave
    /// the first, and the second was unreachable because a diagnostic answered only to its
    /// implementation type name. It now answers `is Exception` as well.
    /// </remarks>
    [Fact]
    public async Task A_handler_can_tell_a_diagnostic_from_an_error_from_a_value()
    {
        Assert.Equal("True", await RunAsync("try { (1 / 0) } catch (e) { ($e is Exception) }"));
        Assert.Equal("False", await RunAsync("try { (1 / 0) } catch (e) { ($e is Error) }"));

        Assert.Equal("True", await RunAsync("try { throw new Error(\"x\") } catch (e) { ($e is Exception) }"));
        Assert.Equal("True", await RunAsync("try { throw new Error(\"x\") } catch (e) { ($e is Error) }"));

        Assert.Equal("False", await RunAsync("try { throw \"plain\" } catch (e) { ($e is Exception) }"));
        Assert.Equal("False", await RunAsync("try { throw \"plain\" } catch (e) { ($e is Error) }"));
    }

    /// <summary>A declared class is unaffected, pinned as a control.</summary>
    [Theory]
    [InlineData("class TtBase { }\nclass TtLeaf extends TtBase { }\n((new TtLeaf()) is TtBase)", "True")]
    [InlineData("class TtBase { }\nclass TtLeaf extends TtBase { }\n((new TtLeaf()) is TtLeaf)", "True")]
    [InlineData("class TtErr extends Error { }\n((new TtErr()) is Error)", "True")]
    public async Task A_declared_class_is_unchanged(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));
}
