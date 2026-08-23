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
/// *calls* one is not compiled at all — the emitted assembly embeds the whole source and
/// hands it to the interpreter, which is the most severe fallback the emitter has. That is
/// `TOAST-0069`'s to fix, by expanding rune calls at lowering.
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
    /// A real call site still forces replay.
    /// </summary>
    /// <remarks>
    /// The control, and a tripwire. A scan that answered "no call" for everything would pass
    /// the theory above and be useless; this is what keeps it honest. When `TOAST-0069`
    /// expands rune calls at lowering, this flips and should be moved rather than deleted —
    /// the program will compile because the rune is gone, not because the scan missed it.
    /// </remarks>
    [Fact]
    public void A_rune_call_still_forces_replay()
        => Assert.False(EmitsWithoutReplay(Definition + "retry 3 { writeline \"hi\" }"));

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
