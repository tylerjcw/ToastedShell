using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A type annotation is enforced on every write, and enforcing it must not mean
/// re-deriving what the type is.
///
/// `TS-P2-119`. `var x: int = 0` counting to a million took 11.9s against 2.4s
/// for the same loop written `var x = 0` — annotating a variable made it nearly
/// five times *slower*, which is the opposite of what a reader expects an
/// annotation to do.
///
/// Two causes, both on the success path:
///
/// * `DotNetTypeResolver.Resolve` called `AppDomain.CurrentDomain.GetAssemblies()`
///   on entry to decide whether its cache was stale — taking a runtime lock and
///   allocating an array of every loaded assembly, purely to read `.Length`, even
///   when the very next line hit the cache. A profile of the loop was dominated
///   by `pthread_mutex_lock`.
/// * The unknown-type diagnostic was raised *before* attempting the conversion,
///   and it answers its question by resolving the type name — which the
///   conversion then did again. Every assignment resolved `int` twice.
///
/// The annotation still costs something, and always will while it is a runtime
/// contract rather than a compile-time fact: the engine must check each assigned
/// value against it. The point is that the check should be a comparison, not a
/// name lookup. After the fix the annotated loop runs at 2.6s against the
/// dynamic 2.4s.
///
/// These tests pin the *behaviour* the reordering had to preserve. The timing
/// itself is deliberately not asserted — a wall-clock threshold on a shared
/// machine is a flaky test, and the measurement lives in the item.
/// </summary>
public class AnnotationEnforcementCostTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(v => v?.ToString() ?? "null"));
    }

    private static async Task<string> CodeOfFailureAsync(string source)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(source));
        return exception is ToshDiagnosticException diagnostic && diagnostic.Diagnostics.Count > 0
            ? diagnostic.Diagnostics[0].Code
            : exception.GetType().Name;
    }

    /// <summary>
    /// An annotation that names nothing is still refused. Moving the check after
    /// the conversion could only be safe because a conversion to an unknown type
    /// always fails — so this is the case that proves the reordering.
    /// </summary>
    [Theory]
    [InlineData("var x: Nonexistent = 3")]
    [InlineData("func f(a: Nope) => 1\nf 2")]
    public async Task An_unknown_annotation_is_reported_as_unknown(string source)
        => Assert.Equal("tosh.runtime.annotation_unknown_type", await CodeOfFailureAsync(source));

    /// <summary>
    /// The one path that never touches the type name, and so the one the reordering
    /// had to check explicitly: `null` is accepted by any nullable annotation before
    /// the name is ever resolved. Without the explicit check this bound happily
    /// against a type that does not exist.
    /// </summary>
    [Fact]
    public async Task A_null_bound_to_an_unknown_nullable_annotation_is_still_refused()
        => Assert.Equal(
            "tosh.runtime.annotation_unknown_type",
            await CodeOfFailureAsync("var x: Nonexistent? = null"));

    /// <summary>
    /// A known type that the value will not convert to keeps its own diagnostic —
    /// the two failures must stay distinguishable, since "unknown type 'itn'" and
    /// "cannot convert to 'int'" send the reader to different places.
    /// </summary>
    [Fact]
    public async Task A_value_the_annotation_rejects_is_a_conversion_failure()
        => Assert.Equal(
            "tosh.runtime.annotation_conversion_failed",
            await CodeOfFailureAsync("var x: int = \"abc\""));

    /// <summary>Enforcement still converts, rather than merely checking.</summary>
    [Theory]
    [InlineData("var x: int = 3\n$x.GetType().Name", "Int32")]
    [InlineData("var x: long = 3\n$x.GetType().Name", "Int64")]
    [InlineData("var x: string = 3\n$x.GetType().Name", "String")]
    // Not `var x: int? = null` — a nullable annotation on a *builtin* alias fails to
    // parse in a `var` declaration at all (`TS-P2-120`), which is unrelated to this
    // item and pre-dates it. The parameter position is where the form works today.
    [InlineData("func f(a: int?) -> int => 1\nf null", "1")]
    public async Task An_annotation_converts_the_value_it_accepts(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// Repeated assignment goes through the enforcement path every time, which is
    /// the loop the item is about. Correctness must not depend on the caching that
    /// made it fast.
    /// </summary>
    [Fact]
    public async Task Repeated_assignment_is_enforced_every_time()
    {
        Assert.Equal("100", await RunAsync(
            """
            var x: int = 0
            until ($x == 100) { $x += 1 }
            $x
            """));

        // The 51st assignment is the one that violates it.
        Assert.Equal("tosh.runtime.annotation_conversion_failed", await CodeOfFailureAsync(
            """
            var x: int = 0
            var n = 0
            until ($n == 100) {
                $n += 1
                if ($n == 51) { $x = "not an int" } else { $x = $n }
            }
            """));
    }

    /// <summary>
    /// A refinement type still runs its predicate on every write. Its cycle guard is
    /// the reason the enforcement path allocates at all, so it is the thing most
    /// likely to break if that allocation is ever made lazy.
    /// </summary>
    [Fact]
    public async Task A_refinement_annotation_is_still_checked()
    {
        const string percent = "type Percent = int where (_ >= 0 and _ <= 100)\n";

        Assert.Equal("50", await RunAsync(percent + "var p: Percent = 50\n$p"));
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(percent + "var p: Percent = 250"));
    }
}
