using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A failing CLR call reports what actually went wrong, and leaves it reachable.
///
/// `TS-P2-95`. Reflection wraps whatever a target throws in a
/// <c>TargetInvocationException</c>, whose own message is "Exception has been
/// thrown by the target of an invocation" — true, and useless. That sentence was
/// all a script ever saw, with the real cause unrecoverable: a
/// <c>DllNotFoundException</c> naming the four paths it probed for
/// <c>libSkiaSharp</c> arrived as that and nothing else, and getting at it took a
/// C# shim. Any interop deeper than one call was undebuggable from ToastScript.
///
/// Two halves, and both are needed. Unwrapping fixes the *message*; carrying the
/// original as an inner exception is what lets a caller do more than read it —
/// match on the type, or reach the fields that hold the detail.
/// </summary>
public class ClrInteropFailureTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(v => v?.ToString() ?? "null"));
    }

    /// <summary>The message is the callee's, not the reflection wrapper's.</summary>
    [Fact]
    public async Task A_failing_method_reports_its_own_message()
    {
        var message = await RunAsync("""try { Int32.Parse("not-a-number") } catch ($e) { $e.Message }""");

        Assert.Contains("not-a-number", message);
        Assert.DoesNotContain("target of an invocation", message);
    }

    /// <summary>A failing constructor takes the same path.</summary>
    [Fact]
    public async Task A_failing_constructor_reports_its_own_message()
    {
        var message = await RunAsync(
            """try { new System.IO.StreamReader("/nope/missing.txt") } catch ($e) { $e.Message }""");

        Assert.DoesNotContain("target of an invocation", message);
        Assert.Contains("/nope/missing.txt", message);
    }

    /// <summary>
    /// The type survives, so a script can tell one failure from another instead of
    /// matching on message text.
    /// </summary>
    [Fact]
    public async Task The_original_exception_type_is_reachable()
        => Assert.Equal("FormatException", await RunAsync(
            """try { Int32.Parse("x") } catch ($e) { (describe-type $e.InnerException).Name }"""));

    /// <summary>
    /// And so do its fields. This is the half that matters for the case the item
    /// was filed from: the detail lives in structured members, not in the message.
    /// </summary>
    [Fact]
    public async Task Structured_detail_on_the_original_exception_survives()
        => Assert.Equal("/tmp/tosh-definitely-missing.txt", await RunAsync(
            """
            try { System.IO.File.ReadAllText("/tmp/tosh-definitely-missing.txt") }
            catch ($e) { $e.InnerException.FileName }
            """));

    /// <summary>
    /// The control. Unwrapping must not swallow failures reflection raises on its
    /// own behalf — a call that matches no overload never reaches the target, so
    /// there is no inner exception to promote and its own message is the right one.
    /// </summary>
    [Fact]
    public async Task A_failure_before_the_call_still_reports_itself()
    {
        var message = await RunAsync("""try { Int32.Nope(1) } catch ($e) { $e.Message }""");

        Assert.Contains("No overload matched", message);
    }

    /// <summary>
    /// A successful call is untouched — the unwrapping wraps every invocation, so
    /// the ordinary path is worth asserting rather than assuming.
    /// </summary>
    [Theory]
    [InlineData("""Int32.Parse("42")""", "42")]
    [InlineData("""String.Join("-", ["a", "b"])""", "a-b")]
    [InlineData("(new System.Text.StringBuilder(\"x\")).ToString()", "x")]
    public async Task A_successful_call_is_unaffected(string expression, string expected)
        => Assert.Equal(expected, await RunAsync(expression));
}
