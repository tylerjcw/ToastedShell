using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Member and operator diagnostics name the shell type and the true cause — <c>TS-P2-18</c>.
/// </summary>
/// <remarks>
/// <para>
/// A refused <c>shy</c> member reported "Member 'Secret' was not found on type
/// 'Tosh.Language.ToshClassInstance'", which is wrong twice. The type is the shell's internal
/// carrier — every ToastScript class shares it, the reader never wrote it, and <c>type-of</c> had
/// been answering <c>S</c> for the same value all along — and "was not found" describes an
/// absence, sending the reader after a typo instead of at the modifier.
/// </para>
/// <para>
/// The naming rule lives in one <c>ShellTypeNaming</c> because the leak was spread across the
/// member accessor, the operator evaluator and the conversion paths, each with its own spelling
/// of "name this type". The cause is answered by the value itself through
/// <c>IShellMemberDiagnostics</c>: only the class knows that a name it declined is one it
/// declares, since visibility and absence arrive at the accessor identically.
/// </para>
/// </remarks>
public sealed class MemberDiagnosticNamingTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public MemberDiagnosticNamingTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private async Task<string> FailureAsync(string script)
    {
        var engine = new ToshEngine(_runtime);
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            async () => await engine.ExecuteToListAsync(script));

        return exception is ToshDiagnosticException diagnostic
            ? string.Join(" ", diagnostic.Diagnostics.Select(d => d.Title))
            : exception.Message;
    }

    // ── no implementation type reaches the reader ──────────────────────────────

    [Theory]
    [InlineData("class S { prop A: int = 1 }\nvar s = new S()\n$s.Nope")]
    [InlineData("class S { prop A: int = 1 }\nvar s = new S()\n$s.Nope = 5")]
    [InlineData("struct S { prop A: int = 1 }\nvar s = new S()\n$s.Nope")]
    [InlineData("record R(A: int)\nvar r = new R(1)\n$r.Nope")]
    [InlineData("enum E { A, B }\n(E.A + 1)")]
    [InlineData("enum E { A, B }\n(E.A > \"x\")")]
    [InlineData("class S { }\nvar s = new S()\n($s + 1)")]
    [InlineData("class S { }\nvar s = new S()\ncast int $s")]
    public async Task No_internal_type_name_appears(string script)
    {
        Assert.DoesNotContain("Tosh.Language.", await FailureAsync(script), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("class S { prop A: int = 1 }\nvar s = new S()\n$s.Nope", "'S'")]
    [InlineData("struct S { prop A: int = 1 }\nvar s = new S()\n$s.Nope", "'S'")]
    [InlineData("record R(A: int)\nvar r = new R(1)\n$r.Nope", "'R'")]
    [InlineData("enum E { A, B }\n(E.A + 1)", "'E'")]
    public async Task The_shell_type_is_named_instead(string script, string expected)
    {
        Assert.Contains(expected, await FailureAsync(script), StringComparison.Ordinal);
    }

    // ── private is described as private, not as absent ─────────────────────────

    [Fact]
    public async Task A_refused_private_property_says_so()
    {
        var message = await FailureAsync("class S { shy prop Secret: int = 1 }\nvar s = new S()\n$s.Secret");

        Assert.Contains("is private to 'S'", message, StringComparison.Ordinal);
        Assert.DoesNotContain("was not found", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_private_method_says_so()
    {
        var message = await FailureAsync("class S { shy func hidden() { return 1 } }\nvar s = new S()\n$s.hidden");

        Assert.Contains("is private to 'S'", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_static_reached_through_an_instance_is_told_where_it_lives()
    {
        var message = await FailureAsync("class S { static prop T: int = 1 }\nvar s = new S()\n$s.T");

        Assert.Contains("static property", message, StringComparison.Ordinal);
        Assert.Contains("S.T", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_genuinely_absent_member_still_reads_as_absent()
    {
        // The distinction is the point: a name the class does not declare must keep the message
        // that sends the reader looking for a typo.
        var message = await FailureAsync("class S { prop A: int = 1 }\nvar s = new S()\n$s.Nope");

        Assert.Contains("was not found", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_private_member_declared_on_a_base_class_is_still_recognised()
    {
        // The member the reader wrote may be declared further up, so the explanation walks the
        // chain rather than looking only at the instantiated class.
        var message = await FailureAsync(
            "class B { shy prop Secret: int = 1 }\nclass D extends B { }\nvar d = new D()\n$d.Secret");

        Assert.Contains("is private to 'B'", message, StringComparison.Ordinal);
    }

    // ── what must not change ───────────────────────────────────────────────────

    [Fact]
    public async Task A_visible_member_is_unaffected()
    {
        var engine = new ToshEngine(_runtime);
        var results = await engine.ExecuteToListAsync("class S { prop A: int = 7 }\nvar s = new S()\n$s.A");

        Assert.Equal(7, Convert.ToInt32(results.LastOrDefault()));
    }

    [Fact]
    public async Task A_private_member_is_still_reachable_from_inside_the_class()
    {
        var engine = new ToshEngine(_runtime);
        var results = await engine.ExecuteToListAsync(
            "class S { shy prop Secret: int = 7\nfunc reveal() { return $this.Secret } }\nvar s = new S()\n$s.reveal()");

        Assert.Equal(7, Convert.ToInt32(results.LastOrDefault()));
    }
}
