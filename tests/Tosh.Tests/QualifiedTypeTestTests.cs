using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>is</c> answers for a declared type named through its module — <c>TOAST-0105</c>.
/// </summary>
/// <remarks>
/// <para>
/// A declared type used as the right operand arrives at <c>OperatorEvaluator.IsType</c> as its
/// *definition object*, not as a name. Neither <c>ToshRecordDefinition</c> nor
/// <c>ToshClassDefinition</c> overrides <c>ToString</c>, so rendering it produced the CLR name
/// <c>"Tosh.Language.ToshRecordDefinition"</c> and every name comparison below it failed. The
/// result was a silent <c>false</c>, never a diagnostic.
/// </para>
/// <para>
/// It went unnoticed because the unqualified spelling works: inside the declaring module
/// <c>$t is Thing</c> resolves to a bare name rather than to a definition. From outside, where
/// the qualified spelling is the only one available, <c>is</c> could not be used on a declared
/// type at all — which is how <c>ToastLib.Plot.AsSeries</c> came to dispatch on
/// <c>$head is ToastLib.Plot.Series</c> and always take the wrong branch.
/// </para>
/// <para>
/// The string forms are covered too. Those reach <c>IsType</c> as text and are answered by a
/// scoped resolver threaded from the engine, for the same reason <c>CastAs</c> takes one: the
/// lookup needs module, nested and imported declarations, all of which live in engine scope,
/// and the portable operator runtime holds no engine state.
/// </para>
/// </remarks>
public sealed class QualifiedTypeTestTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private const string Declarations = """
        export partial module QtModule {
            export record QtRecord(Name: string)
            export class QtClass { prop Name = "k" }
            export func TestsUnqualified(value) { return ($value is QtRecord) }
        }
        """;

    [Fact]
    public async Task A_record_answers_is_when_named_through_its_module()
    {
        var output = await RunAsync($$"""
            {{Declarations}}
            var probe = (new QtModule.QtRecord(Name = "x"))
            echo ($probe is QtModule.QtRecord)
            """);

        Assert.Equal("True", output);
    }

    [Fact]
    public async Task A_class_answers_is_when_named_through_its_module()
    {
        var output = await RunAsync($$"""
            {{Declarations}}
            var probe = (new QtModule.QtClass {| |})
            echo ($probe is QtModule.QtClass)
            """);

        Assert.Equal("True", output);
    }

    /// <summary>The spelling that always worked, kept so a fix cannot trade one for the other.</summary>
    [Fact]
    public async Task The_unqualified_spelling_inside_the_module_still_answers()
    {
        var output = await RunAsync($$"""
            {{Declarations}}
            var probe = (new QtModule.QtRecord(Name = "x"))
            echo (QtModule.TestsUnqualified $probe)
            """);

        Assert.Equal("True", output);
    }

    [Fact]
    public async Task A_qualified_name_for_a_different_declared_type_is_false()
    {
        var output = await RunAsync($$"""
            {{Declarations}}
            var probe = (new QtModule.QtRecord(Name = "x"))
            echo ($probe is QtModule.QtClass)
            """);

        Assert.Equal("False", output);
    }

    /// <summary>
    /// A name that resolves to nothing stays <c>false</c> rather than becoming an error: the
    /// operand evaluates to null, and <c>is null</c> against a non-null value is false. Recorded
    /// so the behaviour is a decision rather than an accident.
    /// </summary>
    [Fact]
    public async Task A_qualified_name_that_names_no_type_is_false()
    {
        var output = await RunAsync($$"""
            {{Declarations}}
            var probe = (new QtModule.QtRecord(Name = "x"))
            echo ($probe is QtModule.NotDeclared)
            """);

        Assert.Equal("False", output);
    }

    [Fact]
    public async Task The_qualified_string_form_agrees_with_the_name_form()
    {
        var output = await RunAsync($$"""
            {{Declarations}}
            var probe = (new QtModule.QtRecord(Name = "x"))
            echo ($probe is "QtModule.QtRecord")
            echo ($probe is "QtRecord")
            """);

        Assert.Equal("True,True", output);
    }

    /// <summary>CLR types are unaffected, qualified or not — they never went through the broken path.</summary>
    [Theory]
    [InlineData("echo (\"x\" is string)", "True")]
    [InlineData("echo (\"x\" is System.String)", "True")]
    [InlineData("echo (42 is int)", "True")]
    [InlineData("echo (42 is string)", "False")]
    public async Task Clr_type_tests_are_unchanged(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task Is_not_negates_the_qualified_form()
    {
        var output = await RunAsync($$"""
            {{Declarations}}
            var probe = (new QtModule.QtRecord(Name = "x"))
            echo ($probe is-not QtModule.QtClass)
            echo ($probe is-not QtModule.QtRecord)
            """);

        Assert.Equal("True,False", output);
    }
}
