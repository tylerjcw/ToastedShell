using System.IO;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Declaring a type over a name that already means a type is warned — <c>TOAST-0133</c>.
/// </summary>
/// <remarks>
/// <para>
/// The resolution rule is deliberate and unchanged: a declaration wins over a built-in.
/// What was missing is the notice. <c>tosh.naming.shadowed_core_type</c> existed but guarded
/// two names and was reached from union declarations alone, so <c>union Option</c> warned
/// while <c>class Option</c> did not — and neither did <c>class Box&lt;int&gt;</c>, where a
/// property annotated <c>int</c> then holds a string with nothing said about it.
/// </para>
/// <para>
/// The widening was measured before it was made: across 121 <c>.tosh</c> files — ToastLib,
/// the repository's examples and its test corpus — no declaration and no type parameter
/// takes any of these names, so it costs nothing in noise. The <c>func double</c> case the
/// engine calls out as intended is a function rather than a type, and is untouched.
/// </para>
/// </remarks>
public sealed class ShadowedTypeNameTests
{
    private static async Task<string> WarningsAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        var errors = new StringWriter();
        runtime.Error = errors;
        await new ToshEngine(runtime.Language).ExecuteToListAsync(source);
        return errors.ToString();
    }

    /// <summary>
    /// The case the item was filed for. Inside <c>class Box&lt;int&gt;</c> the name
    /// <c>int</c> means the type parameter, so the annotation does too — and the value it
    /// admits is whatever the parameter was bound to.
    /// </summary>
    [Fact]
    public async Task A_type_parameter_that_displaces_a_built_in_is_warned()
    {
        var warnings = await WarningsAsync("class Box<int>(v: int) { prop V: int = $v }");

        Assert.Contains("shadowed_core_type", warnings, StringComparison.Ordinal);
        Assert.Contains("'int'", warnings, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same set from every declaration kind, so the answer does not depend on which
    /// keyword was written. `union Option` warned before this; the rest did not.
    /// </summary>
    [Theory]
    [InlineData("union Option { A, B }")]
    [InlineData("class Option(v) { prop V = $v }")]
    [InlineData("record Option(a)")]
    [InlineData("enum Option { A, B }")]
    [InlineData("trait Option { }")]
    [InlineData("interface Option { }")]
    public async Task Every_declaration_kind_warns_for_a_core_type_name(string source)
        => Assert.Contains("shadowed_core_type", await WarningsAsync(source), StringComparison.Ordinal);

    /// <summary>
    /// A built-in alias is the more surprising displacement of the two: `Option` is a name a
    /// user might reasonably take, `int` is one they almost certainly did not mean to redefine.
    /// </summary>
    [Theory]
    [InlineData("class int(v) { prop V = $v }", "int")]
    [InlineData("record string(a)", "string")]
    [InlineData("enum bool { A, B }", "bool")]
    [InlineData("type double = int where _ > 0", "double")]
    public async Task Every_declaration_kind_warns_for_a_built_in_alias(string source, string name)
    {
        var warnings = await WarningsAsync(source);

        Assert.Contains("shadowed_core_type", warnings, StringComparison.Ordinal);
        Assert.Contains($"'{name}'", warnings, StringComparison.Ordinal);
    }

    /// <summary>The two displacements read differently, because they are different.</summary>
    [Fact]
    public async Task A_core_type_and_a_built_in_are_named_as_what_they_are()
    {
        Assert.Contains("core type", await WarningsAsync("class Option(v) { prop V = $v }"), StringComparison.Ordinal);
        Assert.Contains("built-in type", await WarningsAsync("class int(v) { prop V = $v }"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The measurement said this costs no noise, and these are why: an ordinary name and an
    /// ordinary type parameter say nothing at all.
    /// </summary>
    [Theory]
    [InlineData("class Widget(v) { prop V = $v }")]
    [InlineData("class Fine<T>(v: T) { prop V: T = $v }")]
    [InlineData("record Point(x, y)")]
    [InlineData("enum Colour { Red, Green }")]
    public async Task An_ordinary_name_is_silent(string source)
        => Assert.DoesNotContain("shadowed_core_type", await WarningsAsync(source), StringComparison.Ordinal);
}
