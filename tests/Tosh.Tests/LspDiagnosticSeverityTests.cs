using Tosh.LanguageServices;

namespace Tosh.Tests;

/// <summary>
/// A warning reaches the editor as a warning.
///
/// `TS-P2-09`. `GetDiagnostics` discarded severity and reported every diagnostic
/// as `Severity: 1` — LSP for *Error* — so a warning underlined red and read as a
/// failure. A surface that calls everything an error teaches people to ignore all
/// of it.
///
/// The two scales agree on order and differ by one: ToastScript counts `Error`
/// from 0, LSP from 1.
/// </summary>
public class LspDiagnosticSeverityTests
{
    private static IReadOnlyList<LspDiagnostic> Diagnose(string text)
        => new ToshLanguageFeatures().GetDiagnostics(text, "<severity-test>");

    /// <summary>
    /// A type-checker warning — too many arguments to a user function — arrives as
    /// LSP severity 2, not 1.
    /// </summary>
    [Fact]
    public void A_warning_is_reported_as_a_warning()
    {
        var warnings = Diagnose(
            """
            func takesOne(a: int) -> int => $a
            takesOne 1 2 3
            """);

        var arity = Assert.Single(warnings.Where(d => d.Code == "tosh.type.arity"));
        Assert.Equal(2, arity.Severity);
    }

    /// <summary>
    /// And a parse failure is still an error. The fix must preserve severity, not
    /// simply lower everything by one.
    /// </summary>
    [Fact]
    public void A_parse_failure_is_still_reported_as_an_error()
    {
        var diagnostics = Diagnose("if (true { echo \"x\" }");

        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => Assert.Equal(1, d.Severity));
    }

    /// <summary>
    /// Clean source produces nothing, so neither assertion above can be passing on
    /// an empty set.
    /// </summary>
    [Fact]
    public void Clean_source_produces_no_diagnostics()
        => Assert.Empty(Diagnose("""
            func add(a: int, b: int) -> int => ($a + $b)
            add 1 2
            """));
}
