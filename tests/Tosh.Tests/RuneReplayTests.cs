using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What makes a rune force whole-script replay — `TOAST-0070`.
/// </summary>
/// <remarks>
/// <para>
/// A rune is ToastScript's macro: arguments captured lazily as syntax thunks. A program that
/// *called* one used not to be compiled at all — the emitted assembly embedded the whole
/// source and handed it to the interpreter, the most severe fallback the emitter has.
/// `TOAST-0069` expands sealed calls at lowering, so replay is now the exception rather than
/// the rule, and the emitter is told which calls were left behind rather than guessing.
/// </para>
/// <para>
/// What is asserted here is narrower: which programs are judged to *have* a call site. That
/// was decided by scanning the source text for the rune's name as a whole word, so a rune
/// named `retry` mentioned inside a string literal sent the entire program to replay, with
/// nothing in the message to say why. It is a token scan now — a bareword is what a call is
/// written with, a string literal is one token whose text is its content, and a comment is
/// not a token at all.
/// </para>
/// </remarks>
[Collection(ConsoleSerialCollection.Name)]
public sealed class RuneReplayTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public RuneReplayTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private bool EmitsWithoutReplay(string source)
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse(source, "<rune-replay>");
        Assert.True(parse.Diagnostics.Count == 0, $"parse: {string.Join(", ", parse.Diagnostics)}");

        var unit = Lowerer.Lower(parse, _runtime.Commands);
        using var stream = new MemoryStream();
        return BoundUnitEmitter.Emit(unit, $"ToshTest_{Guid.NewGuid():N}", stream, CompileProfile.Runtime)
            .IsClean;
    }

    private const string Definition = "rune retry(count, body) {\n    $body\n}\n";

    /// <summary>A rune's name outside a call site does not force replay.</summary>
    /// <remarks>
    /// The first row is the defect this item was filed for. The others are the neighbours it
    /// would also have caught: a comment, a longer identifier that merely starts with the
    /// name, and a path.
    /// </remarks>
    [Theory]
    [InlineData("in a string", "writeline \"the word retry appears only here\"")]
    [InlineData("in a comment", "# retry is mentioned in this comment\nwriteline \"done\"")]
    [InlineData("as a longer identifier", "func retry-all() -> int => 1\nwriteline \"done\"")]
    [InlineData("not mentioned at all", "writeline \"done\"")]
    public void A_rune_name_that_is_not_a_call_does_not_force_replay(string what, string body)
        => Assert.True(EmitsWithoutReplay(Definition + body), what);

    /// <summary>
    /// A real call site now compiles, because the rune is expanded away — `TOAST-0069`.
    /// </summary>
    /// <remarks>
    /// This is the flip the item above predicted. It asserts the opposite of what it used to,
    /// and the distinction matters: the program compiles because expansion removed the call,
    /// not because the scan failed to see it. The rows below are what keeps that honest — a
    /// call the expander *declines* must still reach replay, or "compiles" would only mean
    /// "the emitter stopped looking".
    /// </remarks>
    [Fact]
    public void An_expanded_rune_call_no_longer_forces_replay()
        => Assert.True(EmitsWithoutReplay(Definition + "retry 3 { writeline \"hi\" }"));

    /// <summary>Both modifiers expand, so neither reaches the interpreter.</summary>
    /// <remarks>
    /// Asserted per modifier rather than for one example, because the differential corpus
    /// compares *outputs*: a case that quietly replayed would agree with the interpreter for
    /// the least interesting reason available — it would be the interpreter. This is what says
    /// the compiled path is the one being compared.
    /// </remarks>
    [Theory]
    [InlineData("sealed", "rune keep-it(x) {\n    var kept = $x\n}\nkeep-it 7")]
    [InlineData("leaky", "leaky rune bind-it(x) {\n    var bound = $x\n}\nbind-it 7")]
    public void Both_rune_modifiers_expand_without_replay(string modifier, string source)
        => Assert.True(EmitsWithoutReplay(source), modifier);

    /// <summary>A call the expander declines still forces replay.</summary>
    /// <remarks>
    /// <para>
    /// The tripwire for the test above. A mismatched arity has no substitution to build, so
    /// it must fall back rather than compile to something that merely looks right. (`leaky`
    /// used to be listed here; it expands now — the modifier is one pushed scope, and not
    /// pushing it is exactly what "writes into the caller's scope" means.)
    /// </para>
    /// <para>
    /// The last four rows are shapes that *did* compile, and crashed. Declining was decided at
    /// the expansion pass, which only ever sees a single-stage pipeline statement — so a rune
    /// piped, redirected, or written inside an expression was never recorded as surviving, and
    /// the emitted program dispatched a rune as an ordinary command: "must be expanded by the
    /// engine, not executed as a regular command", at run time, from a program the compiler
    /// had called clean. Recording moved to `LowerCommand`, which every unexpanded call
    /// reaches whatever shape it was written in.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("too few arguments", "retry 3")]
    [InlineData("too many arguments", "retry 3 { writeline \"hi\" } extra")]
    [InlineData("piped into a command", "retry 3 { writeline \"hi\" } | writeline")]
    [InlineData("with a redirection", "retry 3 { writeline \"hi\" } > /dev/null")]
    [InlineData("in an expression", "writeline (retry 3 { writeline \"hi\" })")]
    [InlineData("in a command substitution", "var v = $(retry 3 { writeline \"hi\" })")]
    public void A_declined_rune_call_still_forces_replay(string what, string body)
        => Assert.False(EmitsWithoutReplay(Definition + body), what);

    /// <summary>A rune with no call site compiles, and one with a call still runs.</summary>
    /// <remarks>
    /// Behaviour rather than emission: replay is a fallback, not a failure, and a program
    /// that uses a rune has to keep working while it is still replayed.
    /// </remarks>
    [Fact]
    public async Task A_called_rune_still_produces_its_effect()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(
            "rune do-twice(body) {\n    $body\n    $body\n}\nvar n = 0\ndo-twice { $n = $n + 1 }\necho $n");

        Assert.Equal("2", results[^1]?.ToString());
    }
}
