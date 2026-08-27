using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Compiler.IR;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The member check only fires when it can be right — <c>TS-P2-45</c>.
/// </summary>
/// <remarks>
/// <para>
/// `var a = []` then `$a = ($a + [1])` then `$a.Length` warned "Member 'Length' was not found on
/// type 'IList'" — and then evaluated correctly and printed the length. Two mechanisms produced
/// that one symptom. The inferred type is the *interface* `IList`, while the value is really an
/// `object[]` that does have `Length`; and `Type.GetMember` on an interface does not return
/// members inherited from base interfaces, so even `Count` — which `IList` gets from `ICollection`
/// — would have been reported missing.
/// </para>
/// <para>
/// So the rule is soundness rather than lookup repair: a member missing from an interface or an
/// open class says nothing about the runtime type, because the value may be something more
/// derived. Only sealed types, value types and arrays can answer honestly, and those are where the
/// typo-catching value lives anyway — `string` is sealed.
/// </para>
/// <para>
/// Warning on valid code is the specific harm <c>TS-P2-41</c> was reverted for: a diagnostic class
/// that cries wolf gets ignored wholesale, which costs more than the check earns. Found while
/// adopting ToastScript for this programme's own scripting.
/// </para>
/// </remarks>
public sealed class MemberCheckSoundnessTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public MemberCheckSoundnessTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private IReadOnlyList<ToshDiagnostic> Check(string source)
    {
        var engine = new ToshEngine(_runtime.Language);
        var parse = engine.Parse(source, "<member-check-test>");
        var unit = Lowerer.Lower(parse, _runtime.Commands);
        return TypeChecker.Check(unit);
    }

    private IReadOnlyList<ToshDiagnostic> MemberDiagnostics(string source) =>
        Check(source)
            .Where(diagnostic => diagnostic.Code == "tosh.type.member_not_found")
            .ToArray();

    [Theory]
    // The reported case, and its neighbours. Every one of these runs correctly.
    [InlineData("var a = []\n$a = ($a + [1])\n$a.Length")]
    [InlineData("var a = [1, 2, 3]\n$a.Length")]
    // `Count` on the same inference: the runtime does reject it, but the binder cannot know that
    // from an interface, and its explanation was wrong about the type it named.
    [InlineData("var a = [1, 2, 3]\n$a.Count")]
    public void An_interface_typed_target_is_never_warned_about(string source)
    {
        Assert.Empty(MemberDiagnostics(source));
    }

    [Theory]
    // Sealed types can answer honestly, so the check keeps its value there. `string` is sealed,
    // and typos on strings are the case this diagnostic actually earns its keep on.
    [InlineData("var s = \"abc\"\n$s.Lenght")]
    [InlineData("var s = \"abc\"\n$s.Trimm()")]
    public void A_sealed_target_still_reports_a_real_typo(string source)
    {
        Assert.NotEmpty(MemberDiagnostics(source));
    }

    [Theory]
    // And correct members on that same sealed type stay silent, so the check is not simply
    // firing on everything it can reach.
    [InlineData("var s = \"abc\"\n$s.Length")]
    [InlineData("var s = \"abc\"\n$s.Trim()")]
    [InlineData("var s = \"abc\"\n$s.ToUpper()")]
    public void A_sealed_target_stays_silent_for_real_members(string source)
    {
        Assert.Empty(MemberDiagnostics(source));
    }

    [Fact]
    public void The_guard_is_about_soundness_not_about_arrays()
    {
        // Guards the *reason*, not the symptom. If the fix had special-cased arrays or the name
        // "IList", this would still fail: a value typed as an open class can also gain members at
        // runtime. `object` is the extreme case — every value is one, and it declares almost
        // nothing, so checking against it would reject nearly every member access.
        Assert.Empty(MemberDiagnostics("var o: object = \"abc\"\n$o.Length"));
    }
}