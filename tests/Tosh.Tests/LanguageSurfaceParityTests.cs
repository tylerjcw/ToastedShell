using System.Text.RegularExpressions;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>TS-P2-10</c>. Two directions: every word <see cref="LanguageSurface"/> claims
/// is real, and no consumer knows a word the registry does not.
/// </summary>
/// <remarks>
/// <para>
/// The first direction is the one that earns the registry its authority, and it has
/// to be established by *running* each word rather than by finding its spelling in
/// the parser. A source scan said all 115 candidate words were genuine, because
/// <c>let</c>, <c>quote</c>, and <c>once</c> all appear in parser source for
/// unrelated reasons. Executing them shows <c>let x = 5</c> fails with
/// <c>annotation_unknown_type</c>, <c>quote</c> is an unknown command, and
/// <c>once</c> is not a member modifier — and all three were documented in the LSP
/// feature table as though they existed. <c>let</c> is in fact still a *proposal*,
/// <c>TS-P3-02</c>.
/// </para>
/// <para>
/// So each word carries a probe: the smallest program in which it does its job. A
/// word joins the registry by parsing, not by being asserted.
/// </para>
/// </remarks>
public sealed class LanguageSurfaceParityTests
{
    /// <summary>
    /// The smallest program in which each registry word does its job. Absent here,
    /// a word cannot be in the registry — that is the point.
    /// </summary>
    /// <remarks>
    /// Nine probes were wrong on the first attempt, all in the same way: ToastScript
    /// requires a *parenthesized* condition — <c>if (true) { }</c>, not
    /// <c>if true { }</c>. Each wrong probe produced a diagnostic naming the fix
    /// (<c>expected_if_condition</c>, <c>expected_switch_value</c>), which is what
    /// made them separable from words that are not real.
    /// </remarks>
    private static readonly Dictionary<string, string> Probes = new(StringComparer.Ordinal)
    {
        // Declarations.
        ["var"] = "var x = 1",
        ["alloc"] = "alloc x = 1",
        ["const"] = "const x = 1",
        ["func"] = "func f() { }",
        ["prop"] = "class C { prop X = 1 }",
        ["bind"] = "bind Crypto {\n    func sha256(data: byte-ptr) -> byte-ptr\n}",
        ["native"] = "native func puts(s: string) -> int from \"libc.so.6\"",
        ["event"] = "class C { event Changed }",

        // Type declarations.
        ["class"] = "class C { }",
        ["struct"] = "struct S(x: int)",
        ["record"] = "record R(x: int)",
        ["enum"] = "enum E { A, B }",
        ["trait"] = "trait T { func f() }",
        ["interface"] = "interface I { func f() }",
        ["union"] = "union U { A, B }",
        ["rune"] = "rune X { }",
        ["module"] = "module M { }",

        // Control flow.
        ["if"] = "if (true) { }",
        ["else"] = "if (true) { } else { }",
        ["for"] = "for x in [1] { }",
        ["in"] = "for x in [1] { }",
        ["while"] = "while (false) { }",
        ["until"] = "until (true) { }",
        ["break"] = "for x in [1] { break }",
        ["continue"] = "for x in [1] { continue }",
        ["return"] = "func f() { return 1 }",
        ["yield"] = "func f() { yield 1 }",
        ["throw"] = "func f() { throw \"x\" }",
        ["try"] = "try { } catch (e) { }",
        ["catch"] = "try { } catch (e) { }",
        ["finally"] = "try { } finally { }",
        ["defer"] = "func f() { defer { } }",
        ["switch"] = "switch (1) { case 1 { } }",
        ["case"] = "switch (1) { case 1 { } }",
        ["default"] = "switch (1) { case 1 { } default { } }",
        ["match"] = "var x = match (1) {\n    1 => 2\n    default => 3\n}",

        // Visibility.
        ["shy"] = "shy func f() { }",
        ["global"] = "global func f() { }",
        ["export"] = "module M { export func f() { } }",

        // Type modifiers.
        ["sealed"] = "sealed class C { }",
        ["hollow"] = "hollow class C { }",
        ["hermit"] = "hermit class C { }",
        ["strict"] = "strict class C { prop X = 1 }",
        ["partial"] = "partial class C { }",
        ["fluid"] = "fluid struct S(x: int)",

        // Member modifiers.
        ["shared"] = "class C { shared prop X = 1 }",
        ["static"] = "class C { static prop X = 1 }",
        ["fixed"] = "class C { fixed prop X = 1 }",
        ["vital"] = "class C(x: int) { vital prop X: int = 0 }",
        ["guarded"] = "class C { guarded prop X = 1 }",
        ["overrule"] = "class C { overrule func f() { } }",
        ["fading"] = "class C { fading prop X = 1 }",
        ["lazy"] = "class C { lazy prop X = 1 }",
        ["raw"] = "class C { raw prop X = 1 }",
        ["proud"] = "class C { proud prop X = 1 }",
        ["public"] = "class C { public prop X = 1 }",
        ["local"] = "class C { local prop X = 1 }",
        ["required"] = "class C(x: int) { required prop Y: int = 0 }",

        // The C#-familiar aliases. Probed in member position, which is the whole
        // point: `abstract class C { }` fails because `abstract` is a member
        // modifier and `hollow` is the type-level spelling.
        ["private"] = "class C { private prop X = 1 }",
        ["abstract"] = "hollow class C { abstract func f() { } }",
        ["readonly"] = "class C { readonly prop X = 1 }",
        ["override"] = "class C { override func f() { } }",
        ["protected"] = "class C { protected prop X = 1 }",
        ["obsolete"] = "class C { obsolete prop X = 1 }",

        // Imports.
        ["using"] = "using System.Text",
        ["require"] = "require \"./nothing.tosh\"",
        ["import"] = "import System.Text",
        ["from"] = "require M from \"./nothing.tosh\"",
        ["as"] = "require M from \"./nothing.tosh\" as N",

        // Interop.
        ["out"] = "native func f(out x: int) -> int from \"libc.so.6\"",
        ["ref"] = "native func f(ref x: int) -> int from \"libc.so.6\"",
        ["callconv"] = "native func f() -> int from \"libc.so.6\" callconv cdecl",

        // Composition.
        ["uses"] = "trait T { func f() { } }\nclass C uses T { }",
        ["fulfills"] = "interface I { func f() }\nclass C fulfills I { func f() { } }",
        ["extends"] = "class B { }\nclass C extends B { }",

        // Operator words.
        ["and"] = "true and false",
        ["or"] = "true or false",
        ["not"] = "not true",
        ["is"] = "1 is Numeric",
        ["is-not"] = "1 is-not Numeric",
        ["not-in"] = "1 not-in [2]",

        // Constants.
        ["true"] = "true",
        ["false"] = "false",
        ["null"] = "null",

        // Word-shaped expression forms.
        ["new"] = "class C { }\nnew C()",
        ["nameof"] = "var x = 1\nnameof($x)",
        ["name-of"] = "var x = 1\nname-of($x)",
    };

    [Fact]
    public void Every_registry_word_has_a_probe()
    {
        // Without this, a word could be added to the registry and quietly skip the
        // demonstration that it is real — which is the failure mode the registry
        // exists to prevent.
        var missing = LanguageSurface.Words.Keys
            .Where(word => !Probes.ContainsKey(word))
            .OrderBy(word => word, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Registry words with no probe. Add the smallest program in which each "
            + "does its job, or remove it from the registry:\n  "
            + string.Join("\n  ", missing));
    }

    [Fact]
    public void Every_registry_word_parses_in_its_probe()
    {
        var failures = new List<string>();

        foreach (var (word, probe) in Probes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var result = Tosh.Language.Parsing.ToshParser.Parse(probe, $"<probe:{word}>");

            if (result.Diagnostics.Count > 0)
            {
                failures.Add($"{word}: {probe}\n      {result.Diagnostics[0].Code} — {result.Diagnostics[0].Title}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Registry words whose probe does not parse. Either the probe is wrong or "
            + "the word is not real — `let`, `quote` and `once` were all documented "
            + "as keywords while being neither:\n  "
            + string.Join("\n  ", failures));
    }

    [Fact]
    public void The_probe_set_would_notice_a_word_that_is_not_real()
    {
        // Negative control for the mechanism itself. If a nonexistent word's probe
        // parsed clean, every assertion above would be vacuous. `let` is the real
        // example: documented in the LSP feature table, proposed as TS-P3-02, and
        // not a binding keyword today.
        var result = Tosh.Language.Parsing.ToshParser.Parse("let x = 1", "<probe:let>");
        var executed = new ToshEngine(ToshRuntime.CreateDefault());

        // It may parse — `let x = 1` is a plausible command line — so the check is
        // that it does not *behave* as a binding.
        Assert.True(
            result.Diagnostics.Count > 0 ||
            !HasVariable(executed, "x"),
            "`let x = 1` bound a variable named x, so `let` now works and belongs in "
            + "the registry with a probe (TS-P3-02).");
    }

    private static bool HasVariable(ToshEngine engine, string name)
    {
        try
        {
            engine.ExecuteToListAsync("let x = 1").GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }

        return engine.TryGetVariableValue(name, out _);
    }

    [Fact]
    public void The_visibility_family_is_exactly_what_the_parser_accepts()
    {
        // ParseDeclarationModifier's switch is the authority for this one family,
        // and it is short enough to compare exactly. The parser's separate
        // IsDeclarationModifierWord skip-list is deliberately *not* the authority:
        // it names `abstract` and `private`, which are not modifiers in this
        // language at all — `abstract class C { }` reports unknown_command.
        var parserSource = File.ReadAllText(LocateParserSource());
        var match = Regex.Match(
            parserSource,
            @"private DeclarationModifier ParseDeclarationModifier\(\).*?return Current\.Text switch\s*\{(.*?)\};",
            RegexOptions.Singleline);

        Assert.True(match.Success, "could not locate ParseDeclarationModifier's switch");

        var accepted = Regex.Matches(match.Groups[1].Value, @"""([a-z][a-z0-9\-]*)""\s+when")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var declared = LanguageSurface.Words
            .Where(pair => pair.Value.HasFlag(LanguageWordKind.VisibilityModifier))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(accepted.OrderBy(x => x), declared.OrderBy(x => x));
    }

    [Theory]
    // The consumers that held their own lists. Each must now be a subset of the
    // registry — a consumer knowing a word the registry does not means one of them
    // is wrong, and the registry is the one under test.
    [InlineData("src/Tosh.Cli/SyntaxHighlighter.cs")]
    [InlineData("src/Tosh.Tome/ToshSyntaxColorizer.cs")]
    [InlineData("src/Tosh.Cli/ReplInputClassifier.cs")]
    [InlineData("src/Tosh.Cli/ReplCompletionEngine.cs")]
    [InlineData("src/Tosh.Language/Binding/Binder.cs")]
    public void A_consumer_knows_no_word_the_registry_does_not(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath);
        var source = File.ReadAllText(path);

        // Only the keyword-shaped sets, by name; a consumer holds plenty of other
        // string literals that are not claims about the language.
        //
        // `Modifiers` and `Constants` were absent from this list on the first pass,
        // and the Tome colorizer keeps its modifiers in a set by that name — so the
        // check passed while missing nine words. A guard is only as wide as its
        // pattern, which is the least visible way for one to be useless.
        var words = Regex.Matches(
                source,
                @"(?:Keywords?|ControlFlowKeywords|TypeDeclarationKeywords|OperatorWords|LanguageForms|BuiltInConstants|Modifiers|Constants|KeywordSuggestionPool)\w*\s*=\s*(?:new\([^)]*\)\s*)?(?:new\[\]\s*)?[\{\[](.*?)[\}\]]\s*;",
                RegexOptions.Singleline)
            .SelectMany(m => Regex.Matches(m.Groups[1].Value, @"""([a-z][a-z0-9\-]*)""").Select(w => w.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        var unknown = words
            .Where(word => !LanguageSurface.Words.ContainsKey(word))
            .OrderBy(word => word, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unknown.Length == 0,
            $"{relativePath} names words the registry does not:\n  " + string.Join("\n  ", unknown));
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static string LocateParserSource() =>
        Path.Combine(RepositoryRoot(), "src/Tosh.Language/Parsing/ToshParser.cs");
}
