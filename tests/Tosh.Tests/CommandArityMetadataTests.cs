using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A command's declared arity matches what it accepts.
///
/// `TS-P2-86`. `which call` documented `call &lt;method-name&gt; [args...]` while the
/// checker reported "Command 'call' expects between 0 and 2 argument(s) but
/// received 3" for `call Next 1 100` — the spelling in `examples/showcase.tosh`.
/// `race { … } { … }`, from `examples/showcase2.tosh`, reported the same for two
/// operations. Both shipped examples were flagged as written.
///
/// The runtime was never wrong: both commands ran and produced correct values.
/// Only the declared metadata was, so the symptom was a warning on correct code —
/// which is the kind that trains people to ignore warnings.
/// </summary>
public class CommandArityMetadataTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public CommandArityMetadataTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private IReadOnlyList<ToshDiagnostic> Check(string source)
    {
        var engine = new ToshEngine(_runtime.Language);
        var unit = Lowerer.Lower(engine.Parse(source, "<arity-test>"), _runtime.Commands);
        return TypeChecker.Check(unit);
    }

    private void AssertNoArityComplaint(string source)
    {
        var complaints = Check(source)
            .Where(d => d.Code == "tosh.type.command_arity")
            .Select(d => d.Title)
            .ToArray();

        Assert.True(complaints.Length == 0, string.Join("\n", complaints));
    }

    /// <summary>The two spellings the shipped examples use.</summary>
    [Theory]
    [InlineData("new System.Random | call Next 1 100")]
    [InlineData("new System.Random | call NextDouble")]
    [InlineData("race { 1 } { 2 }")]
    [InlineData("race { 1 } { 2 } { 3 }")]
    public void A_documented_argument_count_is_not_reported(string source)
        => AssertNoArityComplaint(source);

    /// <summary>
    /// The other documented form of `call`, which takes a type name before the
    /// method and so runs one argument longer.
    /// </summary>
    [Fact]
    public void The_static_form_of_call_is_not_reported()
        => AssertNoArityComplaint("call System.Math Max 1 2");

    /// <summary>
    /// Both still run, and produce the value — the runtime half was never broken,
    /// and a fix that satisfied the checker while breaking execution would pass
    /// every assertion above.
    /// </summary>
    [Fact]
    public async Task Both_commands_still_produce_their_values()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var call = Assert.Single(await engine.ExecuteToListAsync(
            "new System.Random | call Next 5 6"));
        Assert.Equal(5, Convert.ToInt32(call));

        var race = Assert.Single(await engine.ExecuteToListAsync("race { 1 } { 1 }"));
        Assert.Equal(1, Convert.ToInt32(race));
    }

    /// <summary>
    /// The control: declaring an argument variadic must not switch the arity check
    /// off altogether. A command with a fixed arity still reports too many
    /// arguments, or this fix would have removed the diagnostic rather than
    /// corrected it.
    /// </summary>
    [Fact]
    public void A_fixed_arity_command_still_reports_too_many_arguments()
    {
        var complaints = Check("hush")
            .Concat(Check("cd one two three"))
            .Where(d => d.Code == "tosh.type.command_arity")
            .ToArray();

        Assert.NotEmpty(complaints);
    }
}
